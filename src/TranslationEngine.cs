using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace TranslatorLive
{
    public class ScanUpdateEventArgs : EventArgs
    {
        public int AreaId;
        public string SpeakerName;
        public string SourceText;
        public string TranslatedText;
        public int LatencyMs;
        public string StatusMessage;
        public bool IsError;
        public bool ClearedByIdle;
    }

    public class TranslationEngine : IDisposable
    {
        private readonly AppConfig _cfg;
        private Thread _worker;
        private volatile bool _stop;
        private volatile bool _running;
        private volatile bool _pausedForEdit;
        private volatile bool _forceScan;

        private readonly Dictionary<int, byte[]> _prevSigs = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, string> _lastOcrByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _lastOcrScoreByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _lastTransByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _lastActiveUtc = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, bool> _idleHidden = new Dictionary<int, bool>();
        private readonly Dictionary<int, string> _pendingOcrByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _pendingCountByArea = new Dictionary<int, int>();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private readonly object _cacheLock = new object();

        private string _cachedOcrLangTag = "";
        private OcrEngine _ocrEngine;
        private List<string> _installedOcrLangs = new List<string>();

        public event EventHandler<ScanUpdateEventArgs> OnAreaTranslated;

        public bool IsRunning { get { return _running; } }
        public List<string> InstalledOcrLanguages { get { return new List<string>(_installedOcrLangs); } }

        public TranslationEngine(AppConfig cfg)
        {
            _cfg = cfg;
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.DefaultConnectionLimit = 24;
                WebRequest.DefaultWebProxy = null;
            }
            catch { }

            // Pre-warm IPv4 HTTP connections to Google Translate endpoints in background (eliminates cold-start TLS lag)
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HttpRequestFast("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=id&dt=t&q=1", null, "application/x-www-form-urlencoded", 1200);
                    HttpRequestFast("https://translate.google.com/translate_a/single?client=gtx&sl=en&tl=id&dt=t&q=1", null, "application/x-www-form-urlencoded", 1200);
                }
                catch { }
            });

            LoadInstalledOcrLanguages();
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "TranslatorLiveWorker";
            _worker.Start();
        }

        private void LoadInstalledOcrLanguages()
        {
            _installedOcrLangs.Clear();
            try
            {
                foreach (Language lang in OcrEngine.AvailableRecognizerLanguages)
                    _installedOcrLangs.Add(lang.LanguageTag);
            }
            catch { }
        }

        public void SetRunning(bool running)
        {
            _running = running;
            if (running) _forceScan = true;
        }

        public void SetPausedForEdit(bool paused) { _pausedForEdit = paused; }
        public void ForceScanNow() { _forceScan = true; }

        public void ClearAreaState(int areaId)
        {
            lock (_cacheLock)
            {
                _prevSigs.Remove(areaId);
                _lastOcrByArea.Remove(areaId);
                _lastOcrScoreByArea.Remove(areaId);
                _lastTransByArea.Remove(areaId);
                _lastActiveUtc.Remove(areaId);
                _idleHidden.Remove(areaId);
                _pendingOcrByArea.Remove(areaId);
                _pendingCountByArea.Remove(areaId);
            }
        }

        public void ClearState()
        {
            lock (_cacheLock)
            {
                _prevSigs.Clear();
                _lastOcrByArea.Clear();
                _lastOcrScoreByArea.Clear();
                _lastTransByArea.Clear();
                _lastActiveUtc.Clear();
                _idleHidden.Clear();
                _pendingOcrByArea.Clear();
                _pendingCountByArea.Clear();
            }
        }

        public void Dispose()
        {
            _stop = true;
            _running = false;
            try { if (_worker != null && _worker.IsAlive) _worker.Join(800); } catch { }
        }

        private void RaiseResult(int areaId, string src, string trans, int ms, string status, bool err, bool idleCleared = false, string speaker = "")
        {
            EventHandler<ScanUpdateEventArgs> h = OnAreaTranslated;
            if (h != null)
            {
                h(this, new ScanUpdateEventArgs
                {
                    AreaId = areaId,
                    SpeakerName = speaker ?? "",
                    SourceText = src ?? "",
                    TranslatedText = trans ?? "",
                    LatencyMs = ms,
                    StatusMessage = status ?? "",
                    IsError = err,
                    ClearedByIdle = idleCleared
                });
            }
        }

        private void WorkerLoop()
        {
            string lastSrc = "";
            string lastTgt = "";
            string lastEngine = "";
            string lastGlossary = "";
            bool lastMerge = true;
            bool lastNat = true;
            bool lastSpeaker = true;

            while (!_stop)
            {
                if (!_running || _pausedForEdit) { Thread.Sleep(50); continue; }

                List<Area> snapshot = new List<Area>();
                string srcLang, tgtLang, engineName, geminiKey, glossary;
                int intervalMs, autoHideSec;
                bool waitStable, mergeLines, naturalTone, detectSpeaker, enhanceOcr;

                lock (_cfg.Sync)
                {
                    srcLang = _cfg.Src;
                    tgtLang = _cfg.Tgt;
                    engineName = _cfg.Engine;
                    geminiKey = _cfg.GeminiApiKey;
                    glossary = _cfg.Glossary ?? "";
                    naturalTone = _cfg.NaturalTone;
                    detectSpeaker = _cfg.DetectSpeaker;
                    enhanceOcr = _cfg.EnhanceGameOcr;
                    intervalMs = Math.Max(100, Math.Min(3000, _cfg.Interval));
                    autoHideSec = Math.Max(0, _cfg.AutoHideSeconds);
                    waitStable = _cfg.WaitStable;
                    mergeLines = _cfg.MergeLines;
                    foreach (Area a in _cfg.Areas)
                    {
                        snapshot.Add(new Area { Id = a.Id, Enabled = a.Enabled, Mode = a.Mode, Ocr = a.Ocr, Result = a.Result });
                    }
                }

                if (snapshot.Count == 0) { Thread.Sleep(120); continue; }

                bool configChanged = (srcLang != lastSrc) || (tgtLang != lastTgt) ||
                                     (engineName != lastEngine) || (mergeLines != lastMerge) ||
                                     (naturalTone != lastNat) || (glossary != lastGlossary) || (detectSpeaker != lastSpeaker);
                if (configChanged)
                {
                    ClearState();
                    lastSrc = srcLang; lastTgt = tgtLang; lastEngine = engineName;
                    lastMerge = mergeLines; lastNat = naturalTone; lastGlossary = glossary; lastSpeaker = detectSpeaker;
                }

                bool forced = _forceScan || configChanged;
                _forceScan = false;

                foreach (Area area in snapshot)
                {
                    if (_stop || !_running || _pausedForEdit) break;

                    if (!area.Enabled)
                    {
                        string prevT;
                        if (_lastTransByArea.TryGetValue(area.Id, out prevT) && !string.IsNullOrEmpty(prevT))
                        {
                            _lastOcrByArea[area.Id] = "";
                            _lastTransByArea[area.Id] = "";
                            RaiseResult(area.Id, "", "", 0, "Nonaktif", false);
                        }
                        continue;
                    }

                    ProcessArea(area, srcLang, tgtLang, engineName, geminiKey, glossary, naturalTone, detectSpeaker, waitStable, mergeLines, enhanceOcr, autoHideSec, forced);
                }

                int slept = 0;
                while (slept < intervalMs && !_stop && _running && !_forceScan && !_pausedForEdit)
                {
                    Thread.Sleep(35);
                    slept += 35;
                }
            }
        }

        private void ProcessArea(Area area, string srcLang, string tgtLang, string engineName, string geminiKey, string glossary,
            bool naturalTone, bool detectSpeaker, bool waitStable, bool mergeLines, bool enhanceOcr, int autoHideSec, bool forced)
        {
            if (area.Ocr.Width < 12 || area.Ocr.Height < 10) return;

            Stopwatch sw = Stopwatch.StartNew();
            Bitmap frame = CaptureRect(area.Ocr);
            if (frame == null) return;

            try
            {
                byte[] sig = ComputeSignature(frame);
                byte[] prevSig;
                bool hasPrev = _prevSigs.TryGetValue(area.Id, out prevSig);

                if (!forced && hasPrev)
                {
                    float diff = ComputeDiffPercent(sig, prevSig);
                    if (diff < 1.8f)
                    {
                        CheckIdleTimeout(area.Id, autoHideSec);
                        return;
                    }
                }

                _prevSigs[area.Id] = sig;

                bool usedOnline, detectedChoiceLayout;
                string ocrError;
                string rawText = RecognizeBitmap(frame, srcLang, enhanceOcr, out usedOnline, out detectedChoiceLayout, out ocrError);

                if (!string.IsNullOrEmpty(ocrError))
                {
                    RaiseResult(area.Id, "", "", (int)sw.ElapsedMilliseconds, ocrError, true);
                    return;
                }

                bool treatAsChoice = (area.Mode == AreaTextMode.Choice) || detectedChoiceLayout;
                string cleanText = treatAsChoice
                    ? NormalizeChoices(rawText)
                    : NormalizeText(rawText, srcLang, mergeLines);

                if (string.IsNullOrEmpty(cleanText) || (!treatAsChoice && !IsMeaningfulSubtitle(cleanText, srcLang)))
                {
                    string oldT;
                    if (_lastTransByArea.TryGetValue(area.Id, out oldT) && !string.IsNullOrEmpty(oldT))
                    {
                        _lastOcrByArea[area.Id] = "";
                        _lastOcrScoreByArea[area.Id] = 0;
                        _lastTransByArea[area.Id] = "";
                        _pendingOcrByArea[area.Id] = "";
                        _pendingCountByArea[area.Id] = 0;
                        _idleHidden[area.Id] = true;
                        RaiseResult(area.Id, "", "", (int)sw.ElapsedMilliseconds, "Kosong", false);
                    }
                    return;
                }

                int newOcrScore = ScoreOcrQuality(rawText, srcLang);
                string prevOcr, prevTrans;
                int prevOcrScore = 0;
                _lastOcrByArea.TryGetValue(area.Id, out prevOcr);
                _lastOcrScoreByArea.TryGetValue(area.Id, out prevOcrScore);
                _lastTransByArea.TryGetValue(area.Id, out prevTrans);

                // Anti-Flicker & Monotonic Quality Ratchet:
                // Locks steady/fading-out subtitles against flicker, but immediately upgrades when a fading-in video subtitle becomes cleaner/longer!
                if (!forced && !string.IsNullOrEmpty(prevOcr) && !string.IsNullOrEmpty(prevTrans))
                {
                    string coreNew = NormalizeCoreText(cleanText);
                    string corePrev = NormalizeCoreText(prevOcr);

                    // 1. Exact core semantic match: words are identical despite punctuation/quote jitter!
                    if (coreNew == corePrev)
                    {
                        _lastActiveUtc[area.Id] = DateTime.UtcNow;
                        CheckIdleTimeout(area.Id, autoHideSec);
                        return; // Identical core words: KEEP DISPLAYED RESULT, DO NOT RE-TRANSLATE
                    }

                    // 2. High semantic similarity check with Quality Ratchet
                    int dist = LevenshteinDistance(coreNew, corePrev);
                    int maxLen = Math.Max(coreNew.Length, corePrev.Length);
                    bool isSameSentence = false;

                    if (maxLen <= 25 && dist <= 1) isSameSentence = true;
                    else if (maxLen <= 60 && dist <= 2) isSameSentence = true;
                    else if (ComputeSimilarity(coreNew, corePrev) >= 0.84f) isSameSentence = true;

                    if (isSameSentence)
                    {
                        // Allow upgrade if the new scan is significantly cleaner (e.g. finished fade-in or glare cleared)
                        bool qualityUpgraded = (newOcrScore >= prevOcrScore + 6) ||
                                               (coreNew.Length >= corePrev.Length + 6 && newOcrScore >= prevOcrScore);
                        if (!qualityUpgraded)
                        {
                            _lastActiveUtc[area.Id] = DateTime.UtcNow;
                            CheckIdleTimeout(area.Id, autoHideSec);
                            return; // Same sentence & not cleaner: lock existing clean translation!
                        }
                    }
                }

                // Typewriter / Sequential Subtitle Typing Debouncing (Video-safe: max 1 frame wait only when actively typing):
                if (!forced && !treatAsChoice)
                {
                    bool isTerminal = EndsWithTerminalPunctuation(cleanText);
                    string pendingText;
                    _pendingOcrByArea.TryGetValue(area.Id, out pendingText);
                    int pendingCount;
                    _pendingCountByArea.TryGetValue(area.Id, out pendingCount);

                    int maxWaitTicks = waitStable ? 2 : 1;
                    if (!isTerminal && !string.IsNullOrEmpty(pendingText) && cleanText.Length > pendingText.Length && cleanText.StartsWith(pendingText, StringComparison.OrdinalIgnoreCase))
                    {
                        if (pendingCount < maxWaitTicks)
                        {
                            _pendingOcrByArea[area.Id] = cleanText;
                            _pendingCountByArea[area.Id] = pendingCount + 1;
                            return;
                        }
                    }
                    _pendingOcrByArea[area.Id] = cleanText;
                    _pendingCountByArea[area.Id] = 0;
                }

                string speakerName = "";
                string textToTranslate = cleanText;
                if (detectSpeaker && !treatAsChoice)
                    textToTranslate = ExtractSpeakerTag(cleanText, out speakerName);

                string transErr, usedProvider, translated;
                if (treatAsChoice)
                {
                    translated = TranslateChoicesLineByLine(textToTranslate, srcLang, tgtLang, engineName, geminiKey, naturalTone, out usedProvider, out transErr, glossary);
                }
                else
                {
                    translated = TranslateText(textToTranslate, srcLang, tgtLang, engineName, geminiKey, naturalTone, out usedProvider, out transErr, glossary);
                }
                sw.Stop();

                if (!string.IsNullOrEmpty(transErr))
                {
                    RaiseResult(area.Id, cleanText, "", (int)sw.ElapsedMilliseconds, transErr, true);
                    return;
                }

                _lastOcrByArea[area.Id] = cleanText;
                _lastOcrScoreByArea[area.Id] = newOcrScore;
                _lastTransByArea[area.Id] = translated;
                _lastActiveUtc[area.Id] = DateTime.UtcNow;
                _idleHidden[area.Id] = false;

                string tag = (treatAsChoice ? "Pilihan · " : (!string.IsNullOrEmpty(speakerName) ? (speakerName + " · ") : "")) + usedProvider + " · " + sw.ElapsedMilliseconds + " ms";
                RaiseResult(area.Id, cleanText, translated, (int)sw.ElapsedMilliseconds, tag, false, false, speakerName);
            }
            finally
            {
                if (frame != null) frame.Dispose();
            }
        }

        private void CheckIdleTimeout(int areaId, int autoHideSec)
        {
            if (autoHideSec <= 0) return;
            bool alreadyHidden = false;
            _idleHidden.TryGetValue(areaId, out alreadyHidden);
            if (alreadyHidden) return;

            DateTime lastAct;
            if (_lastActiveUtc.TryGetValue(areaId, out lastAct) && (DateTime.UtcNow - lastAct).TotalSeconds >= autoHideSec)
            {
                _idleHidden[areaId] = true;
                RaiseResult(areaId, "", "", 0, "Idle (disembunyikan)", false, true);
            }
        }

        // =====================================================================
        // SPEAKER TAG EXTRACTION + GLOSSARY PROTECTION + NATURAL INDONESIAN
        // =====================================================================
        public static string ExtractSpeakerTag(string text, out string speaker)
        {
            speaker = "";
            if (string.IsNullOrWhiteSpace(text)) return text;
            string s = text.Trim();

            // [Saria] Dialog...
            Match mBracket = Regex.Match(s, @"^\[([A-Za-z0-9 '\-]{2,24})\]\s*(.+)$", RegexOptions.Singleline);
            if (mBracket.Success && mBracket.Groups[2].Value.Trim().Length > 0)
            {
                speaker = mBracket.Groups[1].Value.Trim();
                return mBracket.Groups[2].Value.Trim();
            }

            // Saria: Dialog...
            Match mColon = Regex.Match(s, @"^([A-Z\p{Lu}][A-Za-z0-9 '\-]{1,20}):\s+(.+)$", RegexOptions.Singleline);
            if (mColon.Success && mColon.Groups[2].Value.Trim().Length > 0)
            {
                string cand = mColon.Groups[1].Value.Trim();
                if (!cand.Equals("http", StringComparison.OrdinalIgnoreCase) && !cand.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    speaker = cand;
                    return mColon.Groups[2].Value.Trim();
                }
            }

            // Two-line: "Saria\nYou should hurry..."
            int nl = s.IndexOf('\n');
            if (nl > 1 && nl < 22)
            {
                string first = s.Substring(0, nl).Trim();
                string rest = s.Substring(nl + 1).Trim();
                if (first.Length >= 2 && first.Length <= 20 && char.IsUpper(first[0]) &&
                    !first.Contains(".") && !first.Contains("!") && !first.Contains("?") && !first.Contains(",") &&
                    first.Split(' ').Length <= 2 && rest.Length > first.Length)
                {
                    speaker = first;
                    return rest;
                }
            }

            return s;
        }

        private class GlossaryEntry
        {
            public string Source;
            public string Target;
            public string Token;
        }

        private static List<GlossaryEntry> ParseGlossary(string glossary)
        {
            List<GlossaryEntry> list = new List<GlossaryEntry>();
            if (string.IsNullOrWhiteSpace(glossary)) return list;
            string[] parts = glossary.Replace("\r\n", "\n").Split(new char[] { ';', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length < 2) continue;
                int eq = p.IndexOf('=');
                string src = eq > 0 ? p.Substring(0, eq).Trim() : p;
                string tgt = eq > 0 ? p.Substring(eq + 1).Trim() : p;
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) continue;
                list.Add(new GlossaryEntry { Source = src, Target = tgt, Token = "ZXLLG" + (idx++) + "XZ" });
            }
            list.Sort(delegate (GlossaryEntry a, GlossaryEntry b) { return b.Source.Length.CompareTo(a.Source.Length); });
            return list;
        }

        private static string ProtectGlossary(string text, List<GlossaryEntry> entries)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(text)) return text;
            string s = text;
            foreach (GlossaryEntry e in entries)
                s = Regex.Replace(s, @"\b" + Regex.Escape(e.Source) + @"\b", e.Token, RegexOptions.IgnoreCase);
            return s;
        }

        private static string RestoreGlossary(string text, List<GlossaryEntry> entries)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(text)) return text;
            string s = text;
            foreach (GlossaryEntry e in entries)
                s = Regex.Replace(s, Regex.Escape(e.Token), e.Target, RegexOptions.IgnoreCase);
            return s;
        }

        private static string PreProcessGameIdioms(string text, string srcLang, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id") return text;
            if (srcLang != "en" && srcLang != "auto") return text;

            string s = text;
            // Catch idioms & follow up
            s = Regex.Replace(s, @"\b[Cc]atch\s+(?:me\s+)?up\s+later\b", "meet me later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+later\b", "meet up later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+me\b", "follow and meet me");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+us\b", "follow and meet us");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+you\b", "meet you later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+(?:you|ya)\s+later\b", "see you later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+later\b", "meet me later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+if\s+you\s+can\b", "chase me if you can");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+up\b", "tell me what happened");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\b", "meet up");
            s = Regex.Replace(s, @"\b(?:I'll|I\s+will)\s+catch\s+up\b", "I will follow you");
            s = Regex.Replace(s, @"\b[Dd]id\s+you\s+catch\s+that\b", "did you understand that");

            // Game & combat dialogue expressions
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+this\b", "leave this to me");
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+your\s+back\b", "I will protect you");
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+it\b", "I understand");
            s = Regex.Replace(s, @"\b[Hh]ang\s+in\s+there\b", "stay strong");
            s = Regex.Replace(s, @"\b[Hh]old\s+on\s+a\s+(?:sec|second|minute|moment)\b", "wait a moment");
            s = Regex.Replace(s, @"\b[Hh]old\s+on\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Hh]ang\s+on\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Ww]atch\s+out\b", "be careful");
            s = Regex.Replace(s, @"\b[Ll]ook\s+out\b", "be careful");
            s = Regex.Replace(s, @"\b[Hh]eads\s+up\b", "be careful");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+up\b", "what is happening");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+wrong\b", "what is the problem");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+the\s+matter\b", "what is the problem");
            s = Regex.Replace(s, @"\b[Ww]hat\s+are\s+you\s+up\s+to\b", "what are you doing");
            s = Regex.Replace(s, @"\b[Ww]hat('s|\s+is)\s+going\s+on\b", "what is happening");
            s = Regex.Replace(s, @"\b[Nn]o\s+way\b", "impossible");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+kidding(?:\s+me)?\b", "are you joking");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+for\s+real\b", "are you serious");
            s = Regex.Replace(s, @"\b[Pp]iece\s+of\s+cake\b", "very easy");
            s = Regex.Replace(s, @"\b[Gg]ive\s+me\s+a\s+break\b", "spare me");
            s = Regex.Replace(s, @"\b[Dd]on't\s+sweat\s+it\b", "do not worry");
            s = Regex.Replace(s, @"\b[Nn]o\s+worries\b", "do not worry");
            s = Regex.Replace(s, @"\b[Tt]ake\s+care\b", "be safe");
            s = Regex.Replace(s, @"\b[Nn]ever\s+mind\b", "forget it");
            s = Regex.Replace(s, @"\b[Yy]ou\s+bet\b", "of course");
            s = Regex.Replace(s, @"\b[Mm]ake\s+up\s+your\s+mind\b", "decide quickly");
            s = Regex.Replace(s, @"\b[Cc]ut\s+it\s+out\b", "stop doing that");
            s = Regex.Replace(s, @"\b[Gg]ive\s+it\s+a\s+shot\b", "try it");
            s = Regex.Replace(s, @"\b[Gg]ive\s+it\s+a\s+try\b", "try it");
            s = Regex.Replace(s, @"\b[Ss]hut\s+up\b", "be quiet");
            s = Regex.Replace(s, @"\b[Gg]et\s+lost\b", "go away");
            s = Regex.Replace(s, @"\b[Bb]eat\s+it\b", "go away");
            s = Regex.Replace(s, @"\b[Bb]ack\s+off\b", "step back");
            s = Regex.Replace(s, @"\b[Tt]ake\s+it\s+easy\b", "relax");
            s = Regex.Replace(s, @"\b[Ff]air\s+enough\b", "sounds reasonable");
            s = Regex.Replace(s, @"\b[Ss]ounds\s+good\b", "sounds good");
            s = Regex.Replace(s, @"\b[Bb]y\s+the\s+way\b", "incidentally");
            s = Regex.Replace(s, @"\b[Ii]t's\s+up\s+to\s+you\b", "you decide");
            s = Regex.Replace(s, @"\b[Aa]s\s+soon\s+as\s+possible\b", "immediately");
            s = Regex.Replace(s, @"\b[Cc]heck\s+this\s+out\b", "look at this");
            s = Regex.Replace(s, @"\b[Cc]heck\s+it\s+out\b", "look at this");
            s = Regex.Replace(s, @"\b[Ff]or\s+real\b", "seriously");
            s = Regex.Replace(s, @"\b[Cc]alm\s+down\b", "stay calm");
            s = Regex.Replace(s, @"\b[Cc]ome\s+on\b", "hurry up");
            s = Regex.Replace(s, @"\bI\s+have\s+no\s+idea\b", "I do not know at all");
            s = Regex.Replace(s, @"\bI\s+don't\s+have\s+a\s+clue\b", "I do not know at all");
            s = Regex.Replace(s, @"\b[Ll]ong\s+time\s+no\s+see\b", "long time no see");
            s = Regex.Replace(s, @"\b[Ll]et\s+me\s+see\b", "let me check");
            s = Regex.Replace(s, @"\b[Oo]ut\s+of\s+the\s+way\b", "move aside");
            s = Regex.Replace(s, @"\b[Ss]tep\s+aside\b", "move aside");
            s = Regex.Replace(s, @"\b[Dd]on't\s+give\s+up\b", "do not give up");

            // Additional gaming dialogue expressions
            s = Regex.Replace(s, @"\b[Ll]ook\s+who('s|\s+is)\s+here\b", "look who is arriving");
            s = Regex.Replace(s, @"\b[Mm]ake\s+yourself\s+at\s+home\b", "feel comfortable");
            s = Regex.Replace(s, @"\b[Ss]tep\s+on\s+it\b", "hurry up");
            s = Regex.Replace(s, @"\b[Kk]eep\s+your\s+chin\s+up\b", "stay strong");
            s = Regex.Replace(s, @"\b[Cc]ount\s+me\s+in\b", "I will join");
            s = Regex.Replace(s, @"\b[Cc]ount\s+me\s+out\b", "I will not join");
            s = Regex.Replace(s, @"\b(?:I'm\s+)?on\s+my\s+way\b", "I am coming soon");
            s = Regex.Replace(s, @"\b[Tt]ake\s+a\s+look\b", "look at this");
            s = Regex.Replace(s, @"\b[Tt]ake\s+your\s+time\b", "do not rush");
            s = Regex.Replace(s, @"\b[Cc]all\s+it\s+a\s+day\b", "finish for today");
            s = Regex.Replace(s, @"\b[Gg]et\s+a\s+grip\b", "calm yourself down");
            s = Regex.Replace(s, @"\b[Oo]ut\s+of\s+the\s+blue\b", "suddenly");
            s = Regex.Replace(s, @"\b[Hh]ands\s+off\b", "do not touch");
            s = Regex.Replace(s, @"\b[Dd]on't\s+you\s+dare\b", "do not dare");
            s = Regex.Replace(s, @"\b[Hh]ang\s+tight\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Ss]uit\s+yourself\b", "it is your choice");
            s = Regex.Replace(s, @"\b[Mm]y\s+bad\b", "my mistake");
            s = Regex.Replace(s, @"\b[Nn]o\s+big\s+deal\b", "not a big problem");
            s = Regex.Replace(s, @"\b[Nn]o\s+biggie\b", "not a big problem");
            s = Regex.Replace(s, @"\b[Ss]o\s+far\s+so\s+good\b", "everything is fine so far");
            s = Regex.Replace(s, @"\b[Yy]ou\s+nailed\s+it\b", "you did great");
            s = Regex.Replace(s, @"\b[Ww]ay\s+to\s+go\b", "great job");
            s = Regex.Replace(s, @"\b[Ll]et\s+me\s+know\b", "tell me later");
            s = Regex.Replace(s, @"\b[Kk]eep\s+in\s+touch\b", "stay connected");
            s = Regex.Replace(s, @"\bI'm\s+exhausted\b", "I am very tired");
            s = Regex.Replace(s, @"\bI'm\s+starving\b", "I am very hungry");
            s = Regex.Replace(s, @"\b[Nn]ot\s+at\s+all\b", "not at all");
            s = Regex.Replace(s, @"\b[Bb]y\s+all\s+means\b", "please proceed");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+(?:crazy|insane|out\s+of\s+your\s+mind)\b", "are you crazy");
            s = Regex.Replace(s, @"\b[Ww]hat\s+on\s+earth\b|\b[Ww]hat\s+the\s+heck\b", "what is happening");
            s = Regex.Replace(s, @"\b[Gg]et\s+out\s+of\s+my\s+way\b", "move away");
            s = Regex.Replace(s, @"\b[Ll]eave\s+me\s+alone\b", "do not disturb me");
            s = Regex.Replace(s, @"\b[Ww]atch\s+your\s+back\b", "be careful");
            s = Regex.Replace(s, @"\b[Dd]on't\s+let\s+your\s+guard\s+down\b", "stay alert");
            s = Regex.Replace(s, @"\b[Ii]t's\s+about\s+time\b", "finally the time has come");
            s = Regex.Replace(s, @"\b[Bb]etter\s+late\s+than\s+never\b", "better late than never");
            s = Regex.Replace(s, @"\b[Ii]n\s+the\s+nick\s+of\s+time\b", "just in time");
            s = Regex.Replace(s, @"\b[Pp]ull\s+yourself\s+together\b", "calm down");
            s = Regex.Replace(s, @"\bI\s+owe\s+you\s+one\b", "I am grateful to you");
            s = Regex.Replace(s, @"\b[Yy]ou\s+can\s+say\s+that\s+again\b", "I completely agree");

            // Gaming entities & sacred titles (Zelda, Elden Ring, RPGs)
            s = Regex.Replace(s, @"\b[Tt]he\s+Great\s+Deku\s+Tree\b", "The Great Deku Tree");
            s = Regex.Replace(s, @"\bGreat\s+Deku\s+Tree\b", "Great Deku Tree");
            s = Regex.Replace(s, @"\bDeku\s+Nuts?\b", "Deku Nut");
            s = Regex.Replace(s, @"\bDeku\s+Sticks?\b", "Deku Stick");
            s = Regex.Replace(s, @"\bDeku\s+Scrubs?\b", "Deku Scrub");
            s = Regex.Replace(s, @"\bDeku\s+Shield\b", "Deku Shield");
            s = Regex.Replace(s, @"\bDeku\s+Baba\b", "Deku Baba");
            s = Regex.Replace(s, @"\bDeku\s+Sprout\b", "Deku Sprout");
            s = Regex.Replace(s, @"\bDeku\s+Mask\b", "Deku Mask");
            s = Regex.Replace(s, @"\bMaster\s+Sword\b", "Master Sword");
            s = Regex.Replace(s, @"\bKokiri\s+Sword\b", "Kokiri Sword");
            s = Regex.Replace(s, @"\bHylian\s+Shield\b", "Hylian Shield");
            s = Regex.Replace(s, @"\bMirror\s+Shield\b", "Mirror Shield");
            s = Regex.Replace(s, @"\bGreat\s+Fairy\b", "Great Fairy");
            s = Regex.Replace(s, @"\bGreat\s+Plateau\b", "Great Plateau");
            s = Regex.Replace(s, @"\bOcarina\s+of\s+Time\b", "Ocarina of Time");
            s = Regex.Replace(s, @"\bKokiri(?:'s)?\s+Emerald\b", "Kokiri's Emerald");
            s = Regex.Replace(s, @"\bGoron(?:'s)?\s+Ruby\b", "Goron's Ruby");
            s = Regex.Replace(s, @"\bZora(?:'s)?\s+Sapphire\b", "Zora's Sapphire");
            s = Regex.Replace(s, @"\bHyrule\s+Castle\b", "Hyrule Castle");
            s = Regex.Replace(s, @"\bTemple\s+of\s+Time\b", "Temple of Time");
            s = Regex.Replace(s, @"\bDeath\s+Mountain\b", "Death Mountain");
            s = Regex.Replace(s, @"\bKakariko\s+Village\b", "Kakariko Village");
            s = Regex.Replace(s, @"\bLost\s+Woods\b", "Lost Woods");
            s = Regex.Replace(s, @"\bLon\s+Lon\s+Ranch\b", "Lon Lon Ranch");
            s = Regex.Replace(s, @"\bTriforce\b", "Triforce");
            s = Regex.Replace(s, @"\bPrincess\s+Zelda\b", "Putri Zelda");

            return s;
        }

        public static string NaturalizeIndonesian(string translated, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id" || string.IsNullOrEmpty(translated)) return translated;

            string s = translated;

            // 1. Fix literal idiom leftovers (Game Idioms & robotic artifacts)
            s = Regex.Replace(s, @"\b[Pp]ohon\s+[Dd]eku\s+(?:yang\s+)?baik\b", "Pohon Deku Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]ohon\s+[Dd]eku\s+hebat\b", "Pohon Deku Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Kk]acang-[Kk]acang|[Bb]iji-[Bb]iji)\s+[Dd]eku\b", "Deku Nuts", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Kk]acang|[Bb]iji)\s+[Dd]eku\b", "Deku Nut", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]ongkat\s+[Dd]eku\b", "Deku Stick", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]erisa?i\s+[Dd]eku\b", "Deku Shield", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]edang\s+[Kk]okiri\b", "Pedang Kokiri", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Hh]utan\s+[Kk]okiri\b", "Hutan Kokiri", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]edang\s+(?:tuan|guru|ahli)\b", "Master Sword", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]eri\s+(?:yang\s+)?baik\b", "Peri Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]eri\s+hebat\b", "Peri Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Zz]amrud|[Ee]merald)\s+[Kk]okiri\b", "Kokiri's Emerald", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Rr]ubi|[Rr]uby)\s+[Gg]oron\b", "Goron's Ruby", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Ss]afir|[Ss]apphire)\s+[Zz]ora\b", "Zora's Sapphire", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Kk]astil\s+[Ff]rule\b|\b[Ff]rule\s+Castle\b", "Kastil Hyrule", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Kk]astil\s+[Hh]yrule\b", "Kastil Hyrule", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bKuil\s+Waktu\b", "Temple of Time", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bGunung\s+Kematian\b", "Death Mountain", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bDesa\s+Kakariko\b", "Kakariko Village", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bHutan\s+Hilang\b", "Lost Woods", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bPeternakan\s+Lon\s+Lon\b", "Lon Lon Ranch", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTriforce\b", "Triforce", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:aku|saya|kami)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:kamu|anda|kalian)\s+nanti\b", "sampai ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+nanti\b", "ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enangkap\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enangkap\s+(?:kamu|anda)\s+nanti\b", "sampai ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejar\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejarku\s+nanti\b", "menyusulku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejar\s+nanti\b", "susul nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enyusul\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:aku|saya)\s+jika\s+(?:kamu|anda)\s+bisa\b", "kejar aku kalau bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bikuti\s+dan\s+temui\s+(?:aku|saya)\b", "susul aku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btemui\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenemui\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenemui\s+(?:kamu|anda)\s+nanti\b", "ketemu nanti", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+punya\s+ini\b", "biar aku yang urus");
            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+punya\s+punggungmu\b", "aku jagain kamu");
            s = Regex.Replace(s, @"\b[Ss]aya\s+mengerti\b", "aku paham");
            s = Regex.Replace(s, @"\bsaya\s+mengerti\b", "aku paham");
            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+melihat(?=[\.\,\!\?]|\s*$)", "begitu ya");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\bapa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\b[Aa]da\s+apa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+sedang\s+(?:kamu|anda)\s+lakukan\b", "kamu lagi ngapain");
            s = Regex.Replace(s, @"\bapa\s+yang\s+sedang\s+(?:kamu|anda)\s+lakukan\b", "kamu lagi ngapain");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+sedang\s+terjadi\b", "lagi ada apa ini");
            s = Regex.Replace(s, @"\bapa\s+yang\s+sedang\s+terjadi\b", "lagi ada apa ini");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+(?:kamu|anda)\s+bicarakan\b", "kamu ngomong apa sih");
            s = Regex.Replace(s, @"\b[Aa]pa\s+arti\s+yang\s+(?:kamu|anda)\s+maksud\b", "maksud kamu apa");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+(?:kamu|anda)\s+maksud\b", "maksud kamu apa");
            s = Regex.Replace(s, @"\b[Ss]epotong\s+kue\b", "gampang banget");
            s = Regex.Replace(s, @"\b[Bb]eri\s+aku\s+istirahat\b", "yang bener aja");
            s = Regex.Replace(s, @"\b[Bb]ertahanlah\s+di\s+sana\b", "bertahanlah");
            s = Regex.Replace(s, @"\b[Bb]erpegang\s+teguh\b", "bertahanlah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+ada\s+cara\b", "gak mungkin");
            s = Regex.Replace(s, @"\b[Tt]inggalkan\s+ini\s+padaku\b", "biar aku yang urus");
            s = Regex.Replace(s, @"\b[Kk]epala\s+ke\s+atas\b", "awas");
            s = Regex.Replace(s, @"\b[Jj]angan\s+khawatir\s+tentang\s+itu\b", "tenang aja");
            s = Regex.Replace(s, @"\b[Jj]angan\s+khawatir\b", "tenang aja");
            s = Regex.Replace(s, @"\b[Jj]angan\s+takut\b", "gak usah takut");
            s = Regex.Replace(s, @"\b[Bb]aik-baik\s+saja\b", "baik-baik aja");

            // 2. Conversational Negations ("tidak" -> "gak" for natural gaming Indonesian)
            s = Regex.Replace(s, @"\b[Tt]idak\s+apa-apa\b|\b[Tt]ak\s+apa-apa\b", "gak apa-apa");
            s = Regex.Replace(s, @"\b[Tt]idak\s+masalah\b", "gak masalah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+bisa\b|\b[Tt]ak\s+bisa\b|\b[Tt]idak\s+dapat\b|\b[Tt]ak\s+dapat\b", "gak bisa");
            s = Regex.Replace(s, @"\b[Tt]idak\s+ada\b|\b[Tt]ak\s+ada\b", "gak ada");
            s = Regex.Replace(s, @"\b[Tt]idak\s+tahu\b|\b[Tt]ak\s+tahu\b", "gak tahu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+mau\b|\b[Tt]ak\s+mau\b", "gak mau");
            s = Regex.Replace(s, @"\b[Tt]idak\s+akan\b|\b[Tt]ak\s+akan\b|\b[Tt]akkan\b", "gak bakal");
            s = Regex.Replace(s, @"\b[Tt]idak\s+boleh\b|\b[Tt]ak\s+boleh\b", "gak boleh");
            s = Regex.Replace(s, @"\b[Tt]idak\s+mungkin\b|\b[Tt]ak\s+mungkin\b", "gak mungkin");
            s = Regex.Replace(s, @"\b[Tt]idak\s+perlu\b|\b[Tt]ak\s+perlu\b|\b[Tt]idak\s+usah\b", "gak usah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+suka\b", "gak suka");
            s = Regex.Replace(s, @"\b[Tt]idak\s+peduli\b", "gak peduli");
            s = Regex.Replace(s, @"\b[Tt]idak\s+pernah\b", "gak pernah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+benar\b", "gak bener");
            s = Regex.Replace(s, @"\b[Tt]idak\s+adil\b", "gak adil");
            s = Regex.Replace(s, @"\b[Tt]idak\s+jelas\b", "gak jelas");
            s = Regex.Replace(s, @"\b[Tt]idak\s+percaya\b", "gak percaya");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sempat\b", "gak keburu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+kenal\b", "gak kenal");
            s = Regex.Replace(s, @"\b[Tt]idak\s+enak\b", "gak enak");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sanggup\b", "gak sanggup");
            s = Regex.Replace(s, @"\b[Tt]idak\s+kelihatan\b", "gak kelihatan");
            s = Regex.Replace(s, @"\b[Tt]idak\s+terlalu\b", "gak terlalu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sama\b", "gak sama");
            s = Regex.Replace(s, @"\b[Tt]idak\s+begitu\b", "gak begitu");
            s = Regex.Replace(s, @"\b[Tt]idak!\b", "Gak!");
            s = Regex.Replace(s, @"\b[Tt]idak,\b", "Gak,");
            s = Regex.Replace(s, @"\btidak\b", "gak");

            // 3. Pronouns & Conversational Polish (eliminating robotic formal speech)
            s = Regex.Replace(s, @"\bAnda\b", "kamu");
            s = Regex.Replace(s, @"\banda\b", "kamu");
            s = Regex.Replace(s, @"\bSaya\b", "aku");
            s = Regex.Replace(s, @"\bsaya\b", "aku");
            s = Regex.Replace(s, @"\bApakah\b", "apa");
            s = Regex.Replace(s, @"\bapakah\b", "apa");
            s = Regex.Replace(s, @"\bMengapa\b", "kenapa");
            s = Regex.Replace(s, @"\bmengapa\b", "kenapa");
            s = Regex.Replace(s, @"\bBagaimana\b", "gimana");
            s = Regex.Replace(s, @"\bbagaimana\b", "gimana");
            s = Regex.Replace(s, @"\bSiapakah\b", "siapa");
            s = Regex.Replace(s, @"\bsiapakah\b", "siapa");
            s = Regex.Replace(s, @"\bDimanakah\b", "di mana");
            s = Regex.Replace(s, @"\bdimanakah\b", "di mana");
            s = Regex.Replace(s, @"\bKapankah\b", "kapan");
            s = Regex.Replace(s, @"\bkapankah\b", "kapan");
            s = Regex.Replace(s, @"\bBisakah\s+Anda\b", "bisa tolong", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bBisakah\b", "bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bDapatkah\b", "bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bjika\b", "kalau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bapabila\b", "kalau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bagar\b", "biar", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsupaya\b", "biar", RegexOptions.IgnoreCase);

            // 4. Everyday Game Dialogue Vocabulary & Particles
            s = Regex.Replace(s, @"\b[Ss]aja\b", "aja");
            s = Regex.Replace(s, @"\b[Hh]anya\b", "cuma");
            s = Regex.Replace(s, @"\b[Ss]udah\b", "udah");
            s = Regex.Replace(s, @"\b[Tt]elah\b", "udah");
            s = Regex.Replace(s, @"\b[Tt]erlambat\b", "telat");
            s = Regex.Replace(s, @"\b[Bb]enar-benar\b", "bener-bener");
            s = Regex.Replace(s, @"\byang\s+benar\s+saja\b", "yang bener aja");
            s = Regex.Replace(s, @"\b[Ss]aat\s+ini\b", "sekarang");
            s = Regex.Replace(s, @"\b[Ss]ekarang\s+juga\b", "sekarang juga");
            s = Regex.Replace(s, @"\b[Bb]ergegaslah\b", "cepatlah");
            s = Regex.Replace(s, @"\b[Bb]ergegas\b", "cepat");
            s = Regex.Replace(s, @"\b[Mm]ari\b", "ayo");
            s = Regex.Replace(s, @"\bAkan\s+tetapi\b|\bakan\s+tetapi\b|\bTetapi\b|\btetapi\b|\bNamun\b|\bnamun\b", "tapi");
            s = Regex.Replace(s, @"\bOleh\s+karena\s+itu\b|\boleh\s+karena\s+itu\b", "jadi");
            s = Regex.Replace(s, @"\b[Tt]erima\s+kasih\s+banyak\b", "makasih banyak");
            s = Regex.Replace(s, @"\b[Tt]erima\s+kasih\b", "makasih");
            s = Regex.Replace(s, @"\bseperti\s+ini\b", "kayak gini");
            s = Regex.Replace(s, @"\bseperti\s+itu\b", "kayak gitu");
            s = Regex.Replace(s, @"\bseperti\s+apa\b", "kayak apa");
            s = Regex.Replace(s, @"\bbagaimana\s+jika\b|\bbagaimana\s+kalau\b", "gimana kalau");

            // Naturalize ongoing actions (sedang [verb] -> lagi [verb])
            s = Regex.Replace(s, @"\b[Ss]edang\s+(makan|tidur|bicara|ngomong|nunggu|menunggu|mencari|nyari|pergi|jalan|berjalan|berlari|lari|bertarung|bertempur|istirahat|belajar|bekerja|berlatih|latihan|sembunyi|bersembunyi|bersiap|menyiapkan|menatap|melihat|memikirkan|berpikir)\b", "lagi $1");

            // Naturalize intense adjectives (sangat [adj] -> [adj] banget)
            s = Regex.Replace(s, @"\b(?:[Ss]angat|[Aa]mat)\s+(cepat|lambat|mudah|sulit|gampang|bagus|indah|kuat|lemah|banyak|sedikit|lelah|capek|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|kecil|panas|dingin)\b", "$1 banget");

            // Additional conversational idioms & expressions
            s = Regex.Replace(s, @"\blihat siapa yang (?:ada di sini|sedang tiba)\b", "lihat siapa yang datang", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\banggap saja rumah sendiri\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbukan masalah besar\b", "bukan masalah", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btiba-tiba saja\b", "tiba-tiba", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btinggalkan aku sendiri\b", "jangan ganggu aku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkamu sudah gila\b", "kamu gila ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bapakah kamu gila\b", "kamu gila ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkabari aku\b", "kabari aku ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkerja yang bagus\b", "kerja bagus", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkamu hebat sekali\b", "kamu hebat banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkeren sekali\b", "keren banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat lapar\b", "lapar banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat lelah\b", "capek banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsantai saja\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bjangan terburu-buru\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpelan-pelan saja\b", "pelan-pelan aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti biasa\b", "kayak biasa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti semula\b", "kayak semula", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btepat pada waktunya\b", "pas banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\baku berutang budi padamu\b", "aku berutang padamu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsaya berutang budi padamu\b", "aku berutang padamu", RegexOptions.IgnoreCase);

            // Universal informal transformations (applies to ALL dialogue & sentences)
            s = Regex.Replace(s, @"\b(?:terlihat|tampak)\s+seperti\b", "kelihatan kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\brasanya\s+seperti\b", "rasanya kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhendak\b", "mau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsebenarnya\b", "sebenernya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\s+(cepat|lambat|mudah|sulit|kuat|lemah|banyak|besar|kecil|indah|hebat|keren)\b", "$1 banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bamat\s+sangat\b", "bener-bener", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpada\s+akhirnya\b", "akhirnya", RegexOptions.IgnoreCase);

            // Fix capitalization after punctuation or start of string
            s = Regex.Replace(s, @"([\.\!\?]\s+)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            if (s.Length > 0 && char.IsLower(s[0]))
                s = char.ToUpperInvariant(s[0]) + s.Substring(1);

            return s;
        }

        public static Bitmap CaptureRect(Rectangle rc)
        {
            try
            {
                if (rc.Width <= 0 || rc.Height <= 0) return null;
                Bitmap bmp = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rc.X, rc.Y, 0, 0, new Size(rc.Width, rc.Height), CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch { return null; }
        }

        private static byte[] ComputeSignature(Bitmap bmp)
        {
            const int sw = 32, sh = 16;
            byte[] sig = new byte[sw * sh];
            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, sw, sh);
                }
                BitmapData bd = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[bd.Stride * sh];
                    Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
                    for (int y = 0; y < sh; y++)
                    {
                        int rowOff = y * bd.Stride;
                        for (int x = 0; x < sw; x++)
                        {
                            int off = rowOff + x * 4;
                            sig[y * sw + x] = (byte)((raw[off] * 29 + raw[off + 1] * 150 + raw[off + 2] * 77) >> 8);
                        }
                    }
                }
                finally { small.UnlockBits(bd); }
            }
            return sig;
        }

        private static float ComputeDiffPercent(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 100f;
            int changed = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 12) changed++;
            return (changed * 100f) / a.Length;
        }

        private static string MapWinRtLangTag(string appCode)
        {
            switch (appCode)
            {
                case "en": return "en-US";
                case "ja": return "ja-JP";
                case "ko": return "ko-KR";
                case "zh-CN": return "zh-Hans-CN";
                case "zh-TW": return "zh-Hant-TW";
                case "ru": return "ru-RU";
                case "es": return "es-ES";
                case "fr": return "fr-FR";
                case "de": return "de-DE";
                default: return "auto";
            }
        }

        private static string MapOcrSpaceLang(string appCode)
        {
            switch (appCode)
            {
                case "ja": return "jpn";
                case "ko": return "kor";
                case "zh-CN": return "chs";
                case "zh-TW": return "cht";
                case "ru": return "rus";
                case "es": return "spa";
                case "fr": return "fre";
                case "de": return "ger";
                default: return "eng";
            }
        }

        private bool HasLocalPackFor(string appCode)
        {
            if (appCode == "auto" || string.IsNullOrEmpty(appCode)) return _installedOcrLangs.Count > 0;
            string prefix = appCode.Split('-')[0].ToLowerInvariant();
            foreach (string tag in _installedOcrLangs)
                if (tag.ToLowerInvariant().StartsWith(prefix)) return true;
            return false;
        }

        private OcrEngine GetWinRtEngine(string appCode)
        {
            string tag = MapWinRtLangTag(appCode);
            if (_ocrEngine != null && _cachedOcrLangTag == tag) return _ocrEngine;
            OcrEngine eng = null;
            try
            {
                if (tag != "auto")
                {
                    Language lang = new Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang)) eng = OcrEngine.TryCreateFromLanguage(lang);
                }
                if (eng == null) eng = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { }
            _ocrEngine = eng;
            _cachedOcrLangTag = tag;
            return _ocrEngine;
        }

        private static TR AwaitWinRt<TR>(IAsyncOperation<TR> op)
        {
            int tries = 0;
            while (op.Status == AsyncStatus.Started && tries < 2500) { Thread.Sleep(1); tries++; }
            if (op.Status != AsyncStatus.Completed) throw new Exception("WinRT OCR status: " + op.Status);
            return op.GetResults();
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, out bool usedOnline, out string error)
        {
            bool dummy;
            return RecognizeBitmap(sourceBmp, srcLang, _cfg.EnhanceGameOcr, out usedOnline, out dummy, out error);
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            return RecognizeBitmap(sourceBmp, srcLang, _cfg.EnhanceGameOcr, out usedOnline, out detectedChoiceLayout, out error);
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, bool enhanceOcr, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            usedOnline = false;
            detectedChoiceLayout = false;
            error = "";

            if (srcLang != "auto" && !HasLocalPackFor(srcLang))
            {
                usedOnline = true;
                return RecognizeOnlineOcrSpace(sourceBmp, srcLang, out error);
            }

            OcrEngine eng = GetWinRtEngine(srcLang);
            if (eng == null)
            {
                usedOnline = true;
                return RecognizeOnlineOcrSpace(sourceBmp, srcLang, out error);
            }

            // Pass 1: Natural High-Resolution Bicubic Upscaling (preserves full RGB channels, anti-aliasing, and white floating subtitles over bright cutscenes)
            Bitmap scaledBmp = UpscaleNaturalForOcr(sourceBmp);
            try
            {
                bool choice1 = false;
                string text1 = RunWinRtOcrSinglePass(eng, scaledBmp, out choice1);
                if (!enhanceOcr)
                {
                    detectedChoiceLayout = choice1;
                    return text1;
                }

                int score1 = ScoreOcrQuality(text1, srcLang);
                // If Natural Super-Resolution already produced clean, high-confidence text (>= 86), return immediately (~12ms total!)
                if (!string.IsNullOrWhiteSpace(text1) && score1 >= 86)
                {
                    detectedChoiceLayout = choice1;
                    return text1;
                }

                // Pass 2: Achromatic Shadow-Contrast Top-Hat Isolation (eliminates blinding cutscene glare, Triforce rays, fire, or 3D background bark)
                using (Bitmap topHatBmp = ExtractFloatingSubtitleTopHat(scaledBmp))
                {
                    bool choice2 = false;
                    string text2 = RunWinRtOcrSinglePass(eng, topHatBmp, out choice2);
                    int score2 = ScoreOcrQuality(text2, srcLang);

                    if (score2 > score1 && !string.IsNullOrWhiteSpace(text2))
                    {
                        detectedChoiceLayout = choice2;
                        return text2;
                    }
                    detectedChoiceLayout = choice1;
                    return text1;
                }
            }
            catch (Exception ex)
            {
                error = "OCR error: " + ex.Message;
                return "";
            }
            finally
            {
                if (scaledBmp != null && scaledBmp != sourceBmp)
                    scaledBmp.Dispose();
            }
        }

        private static string RunWinRtOcrSinglePass(OcrEngine eng, Bitmap bmp, out bool detectedChoiceLayout)
        {
            detectedChoiceLayout = false;
            if (bmp == null) return "";

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] buf = new byte[bd.Stride * bmp.Height];
            try { Marshal.Copy(bd.Scan0, buf, 0, buf.Length); }
            finally { bmp.UnlockBits(bd); }

            var ibuf = CryptographicBuffer.CreateFromByteArray(buf);
            using (SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(
                ibuf, BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied))
            {
                OcrResult res = AwaitWinRt(eng.RecognizeAsync(sb));
                if (res == null || res.Lines == null || res.Lines.Count == 0) return "";

                if (res.Lines.Count >= 2 && res.Lines.Count <= 5)
                {
                    int choiceMarkerLines = 0;
                    foreach (OcrLine ln in res.Lines)
                    {
                        string t = ln.Text.Trim();
                        if (Regex.IsMatch(t, @"^(?:[>▸►▶•]|\[?\d+[\]\.\)])")) choiceMarkerLines++;
                    }
                    if (choiceMarkerLines >= 1)
                        detectedChoiceLayout = true;
                }

                StringBuilder sbTxt = new StringBuilder();
                for (int i = 0; i < res.Lines.Count; i++)
                {
                    if (i > 0) sbTxt.Append('\n');
                    sbTxt.Append(res.Lines[i].Text);
                }
                return sbTxt.ToString();
            }
        }

        private static Bitmap UpscaleNaturalForOcr(Bitmap bmp)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return bmp;

            // Target optimal character height for WinRT OCR (approx 38-64px)
            float scale = 1.0f;
            if (bmp.Height < 180) scale = 2.5f;
            else if (bmp.Height < 400 && bmp.Width * 2 <= 3400) scale = 2.0f;
            else if (bmp.Height < 600 && bmp.Width * 1.5f <= 3400) scale = 1.5f;

            if (scale <= 1.01f) return bmp;

            int nw = (int)(bmp.Width * scale);
            int nh = (int)(bmp.Height * scale);
            Bitmap up = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(up))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(bmp, 0, 0, nw, nh);
            }
            return up;
        }

        private static Bitmap ExtractFloatingSubtitleTopHat(Bitmap scaledBmp)
        {
            int w = scaledBmp.Width, h = scaledBmp.Height;
            Bitmap target = new Bitmap(scaledBmp);
            BitmapData bd = target.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                int totalBytes = Math.Abs(stride) * h;
                byte[] raw = new byte[totalBytes];
                Marshal.Copy(bd.Scan0, raw, 0, totalBytes);

                int darkCount = 0;
                int totalPix = w * h;
                for (int y = 0; y < h; y += 2)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += 2)
                    {
                        int off = row + x * 4;
                        int lum = (raw[off + 2] * 77 + raw[off + 1] * 150 + raw[off] * 29) >> 8;
                        if (lum < 100) darkCount += 4;
                    }
                }
                bool hasDarkBox = (darkCount > totalPix * 0.65);

                byte[] intensity = new byte[totalPix];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    int yw = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int off = row + x * 4;
                        int b = raw[off], g = raw[off + 1], r = raw[off + 2];
                        int lum = (r * 77 + g * 150 + b * 29) >> 8;
                        int maxC = Math.Max(r, Math.Max(g, b));
                        int minC = Math.Min(r, Math.Min(g, b));
                        int sat = maxC - minC;

                        // Dark dialog box: preserve colored text (Link green, Navi cyan, gold items)
                        // Floating subtitle without background box: penalize saturated background glare (yellow Triforce, fire, sky)
                        int val = hasDarkBox ? ((lum + maxC) >> 1) : Math.Max(0, minC - sat);
                        intensity[yw + x] = (byte)Math.Min(255, val);
                    }
                }

                // Separable 2D sliding minimum filter for Morphological White Top-Hat local contrast
                int rad = 12;
                byte[] hMin = new byte[totalPix];
                for (int y = 0; y < h; y++)
                {
                    int yw = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int x0 = Math.Max(0, x - rad), x1 = Math.Min(w - 1, x + rad);
                        byte mn = 255;
                        for (int xi = x0; xi <= x1; xi += 2)
                        {
                            byte v = intensity[yw + xi];
                            if (v < mn) mn = v;
                        }
                        hMin[yw + x] = mn;
                    }
                }

                byte[] localMin = new byte[totalPix];
                for (int y = 0; y < h; y++)
                {
                    int y0 = Math.Max(0, y - rad), y1 = Math.Min(h - 1, y + rad);
                    int yw = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte mn = 255;
                        for (int yi = y0; yi <= y1; yi += 2)
                        {
                            byte v = hMin[yi * w + x];
                            if (v < mn) mn = v;
                        }
                        localMin[yw + x] = mn;
                    }
                }

                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    int yw = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int off = row + x * 4;
                        int val = intensity[yw + x];
                        int lmin = localMin[yw + x];
                        int tophat = val - lmin;

                        byte ink = 0;
                        int minVal = hasDarkBox ? 80 : 95;
                        int minTopHat = hasDarkBox ? 20 : 24;
                        if (val > minVal && tophat > minTopHat)
                        {
                            int boosted = (tophat - minTopHat) * 4;
                            if (val > 170 && tophat > 42) boosted = Math.Max(boosted, val);
                            ink = (byte)Math.Min(255, Math.Max(0, boosted));
                        }

                        // Render as crisp Black ink on pure White paper
                        byte paper = (byte)(255 - ink);
                        raw[off] = paper;
                        raw[off + 1] = paper;
                        raw[off + 2] = paper;
                    }
                }

                Marshal.Copy(raw, 0, bd.Scan0, totalBytes);
            }
            finally
            {
                target.UnlockBits(bd);
            }
            return target;
        }

        public static int ScoreOcrQuality(string raw, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            if (s.Length < 2) return 0;

            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            int score = 100;

            foreach (char c in s)
            {
                if (c == '<' || c == '>' || c == '*' || c == '&' || c == '~' || c == '^' || c == '|' || c == '_' || c == '#' || c == '@')
                    score -= 16;
                else if (!isCjk && c > 127 && c != '’' && c != '“' && c != '”' && c != '…' && c != '—' && c != '•')
                    score -= 14;
            }

            if (!isCjk)
            {
                // Penalize quote/bracket noise clusters inside words like 'l'herefore or t'(')
                score -= Regex.Matches(s, @"[A-Za-z]['""\(\)\[\]]+[A-Za-z\(\)\[\]]").Count * 20;
                score -= Regex.Matches(s, @"['""]{2,}|\(\)|\[\]|[:;]{2,}").Count * 18;

                string[] words = Regex.Split(s, @"\s+");
                int validWords = 0;
                foreach (string w in words)
                {
                    string trimmedW = w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '“', '”', '’', '(', ')', '[', ']');
                    if (trimmedW.Length == 0) continue;

                    // Penalize suspicious digit hallucinations (e.g. "11" for "I'm", "1001"/"100k" for "look", "t0" for "to")
                    if (Regex.IsMatch(trimmedW, @"^(?:11|1001|100k|l00k|1ook|0|1)$", RegexOptions.IgnoreCase))
                    {
                        score -= 18;
                        continue;
                    }
                    if (Regex.IsMatch(trimmedW, @"[0-9]") && Regex.IsMatch(trimmedW, @"[A-Za-z]") &&
                        !Regex.IsMatch(trimmedW, @"^(?:\d+(?:st|nd|rd|th|s|g|k|m|p|x|d|hp|mp|xp|lv)|lv\d+)$", RegexOptions.IgnoreCase))
                    {
                        score -= 18;
                    }

                    string cleanW = Regex.Replace(trimmedW, @"[^A-Za-z]", "");
                    if (cleanW.Length == 0) continue;
                    if (cleanW.Length == 1 && cleanW != "I" && cleanW != "a" && cleanW != "A")
                        score -= 12;
                    else if (cleanW.Length >= 3 && !Regex.IsMatch(cleanW, @"[aeiouyAEIOUY]"))
                        score -= 18;
                    else
                        validWords++;
                }
                if (validWords == 0) return 0;
            }

            return Math.Max(0, score);
        }

        public static bool IsMeaningfulSubtitle(string cleanText, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(cleanText)) return false;
            string s = cleanText.Trim();
            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            if (isCjk) return s.Length >= 1;

            // Reject pure punctuation/numbers or 1-character noise on moving video frames
            string lettersOnly = Regex.Replace(s, @"[^A-Za-z]", "");
            if (lettersOnly.Length < 2) return false;

            // Require at least one vowel in English/Latin subtitles
            if (!Regex.IsMatch(lettersOnly, @"[aeiouyAEIOUY]")) return false;

            // If very short (2-3 letters total), ensure it's not random symbol noise
            if (lettersOnly.Length <= 2 && s.Length <= 3)
            {
                return Regex.IsMatch(s, @"^(?:no|go|ok|oh|ah|hi|up|on|in|it|he|we|so|do|be|me|my|by|or|if|at|as|us|to)[\.\!\?]?$", RegexOptions.IgnoreCase);
            }
            return true;
        }

        public static string CleanGameOcrText(string text, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string s = text;

            // 1. Clustered punctuation & noise symbols
            s = Regex.Replace(s, @"[:;]{2,}", ":");
            s = Regex.Replace(s, @"(?:\:|\;)?\.{2,}'?", "...");
            s = Regex.Replace(s, @"\b(tal|wal|fol)<(?!\w)", "$1k", RegexOptions.IgnoreCase); // tal< -> talk, wal< -> walk
            s = Regex.Replace(s, @"l<", "k");                             // mal<e -> make, lil<e -> like
            s = Regex.Replace(s, @"(?<=[A-Za-z])<(?=[A-Za-z])", "k");      // < between letters -> k
            s = Regex.Replace(s, @"(?<=[\s\(\[\{]|^)[<>\*~^_#]+", "");     // leading noise symbols (keep | for I-restoration first)
            s = Regex.Replace(s, @"[<>\*~^|_#]+(?=[\s\)\.\,\!\?\]\}]|$)", ""); // trailing noise symbols
            s = Regex.Replace(s, @"[&*]+([A-Za-z\u00C0-\u024F])", "$1");   // &word -> word
            s = Regex.Replace(s, @"([A-Za-z\u00C0-\u024F])[&*]+", "$1");   // word* -> word
            s = Regex.Replace(s, @"[<>]", " ");                            // strip remaining stray brackets

            // 2. Glued noise digits/letters between words (e.g. power4eGo -> power. Go)
            s = Regex.Replace(s, @"(?<=[a-z]{2,})\d+[a-z]{1,2}(?=[A-Z])", ". ");

            // 3. Glued lowercase-to-uppercase words (e.g. DearNavi -> Dear Navi)
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z][a-z])", " ");

            // 4. Universal English / Latin Digit-to-Letter & Ligature OCR Restoration (Applies to ALL games)
            if (srcLang == "en" || srcLang == "auto")
            {
                // 4a. Contractions & Pronoun "I" misread as "11", "1'm", "l'm", "|'m", "I'rn", "l", "1", "|"
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'m\b", "I'm");
                s = Regex.Replace(s, @"\b(?:1m|I'rn|Irn|11m|11'm)\b", "I'm");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ll\b", "I'll");
                s = Regex.Replace(s, @"\b(?:1ll|11ll|I1l|Il1)\b", "I'll");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ve\b", "I've");
                s = Regex.Replace(s, @"\b1ve\b", "I've");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'d\b", "I'd");

                // "11 <base verb>" -> "I'll <verb>"
                s = Regex.Replace(s, @"\b11\s+(?=(?:go|do|be|take|make|see|tell|give|get|find|help|wait|show|protect|handle|leave|never|have|try|come|bring|keep|let|stop|start|need|check|ask|meet|catch|kill|destroy|save|fix|open|close|use|buy|pay|stay|return|follow)\b)", "I'll ");

                // "11 <non-unit word>" at start of sentence/clause or after conjunction -> "I'm "
                s = Regex.Replace(s, @"(?:(?<=^|[\.\!\?\,\;\:""'“”\(]\s*)|(?<=\b(?:and|but|so|because|since|that|if|when|while|though|although)\s+))11\s+(?!(?:years|days|hours|minutes|seconds|months|weeks|times|people|men|women|children|items|coins|gold|bucks|dollars|yen|gil|rupees|souls|points|percent|o'clock|am|pm|levels|floors|steps|miles|meters|feet|inches|pounds|kg|cm|mm|of\b)\b)(?=[A-Za-z])", "I'm ");

                // "11 <present participle / state adjective>" anywhere -> "I'm "
                s = Regex.Replace(s, @"\b11\s+(?=(?:[a-z]+ing|sorry|sure|ready|glad|afraid|happy|tired|exhausted|fine|okay|alright|here|back|not|just|still|only|already|so|too|very|quite|really|about|on|in|at|from|with|a|an|the|your|her|his|my|our|their|done|finished|dead|alive|hurt|stuck|lost|late|certain|positive|worried|scared|proud|honored|grateful|thankful)\b)", "I'm ");

                // Standalone l / 1 / | before common verbs -> "I "
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|1l])\s+(?=(?:am|was|were|have|had|will|would|can|could|should|shall|must|may|might|need|want|think|thought|know|knew|see|saw|feel|felt|hope|wish|guess|suppose|believe|understand|remember|forgot|mean|meant|said|told|asked|heard|found|got|took|made|came|went|left|gave|tried|used|loved|hated|feared|doubted|wonder|bet|swear|promise|refuse|agree|disagree|don't|didn't|won't|can't|couldn't|shouldn't|wouldn't|haven't|hadn't|wasn't|cannot)\b)", "I ");

                // 4b. "1001" / "100k" / "l00k" / "1ook" -> "Look" (at sentence start) or "look"
                s = Regex.Replace(s, @"(?<=^|[\.\!\?""'“”]\s*)(?:1001|100k|l00k|L00k|L001|100K)(s|ed|ing)?\b(?!(?:\s+(?:nights|arabian|dalmatians|years|coins|points|gold|rupees|gil|exp|hp|mp))\b)", "Look$1");
                s = Regex.Replace(s, @"(?<!\b(?:year|room|page|level|lvl|id|code|error|port)\s+)\b(?:1001|100k|l00k|1ook|l001|10ok|lo0k)(s|ed|ing)?\b(?!(?:\s+(?:nights|arabian|dalmatians|years|coins|points|gold|rupees|gil|exp|hp|mp))\b)", "look$1");
                s = Regex.Replace(s, @"\b(?:L00k|L001|100K)(s|ed|ing)?\b", "Look$1");

                // 4c. "10-" words -> "lo-" (10st -> lost, 10ng -> long, 10ve -> love, 10rd -> lord, 10ck -> lock, 10w -> low, 10ud -> loud, 100se -> loose)
                s = Regex.Replace(s, @"\b10(st|ng|ve[dsr]?|rd[s]?|ck(?:s|ed|ing)?|w(?:er|est|ly)?|ud(?:er|est|ly)?|0se|cal|nely|cat(?:e|ed|ing|ion))\b", "lo$1");

                // 4d. Leading '1' before letters -> 'I' or 'l' (excluding 1st)
                s = Regex.Replace(s, @"\b1(t's|t|n|s|f|nto|nside|ndeed|nstead|mpossible|mportant|mmortal|sland|tem|dea|diot|gnore|llusion|magine|nform|nvite|nclude|ncrease|nsist)\b", "I$1");
                s = Regex.Replace(s, @"\b1(?!st\b)([a-z]{2,})\b", "l$1");

                // 4e. Internal/trailing '1' adjacent to letters -> 'l' (wi11 -> will, te11 -> tell, care1ess1y -> carelessly)
                s = Regex.Replace(s, @"(?<=[a-zA-Z])1+", delegate(Match m) { return new string('l', m.Length); });

                // 4f. Digit '0' adjacent to letters -> 'o' / 'O' (t0 -> to, g0 -> go, y0u -> you, w0rld -> world, 0ne -> one)
                s = Regex.Replace(s, @"\b0([a-z]+)\b", "o$1");
                s = Regex.Replace(s, @"\b0([A-Z][a-z]+)\b", "O$1");
                s = Regex.Replace(s, @"(?<=[a-zA-Z])0+", delegate(Match m) { return new string('o', m.Length); });

                // 4g. Common Font Ligatures: vv -> w, rn -> m
                s = Regex.Replace(s, @"\bvv([a-zA-Z]+)\b", "w$1");
                s = Regex.Replace(s, @"\bVV([a-zA-Z]+)\b", "W$1");
                s = Regex.Replace(s, @"\brn(ust|ake|ade|any|ore|ost|uch|y|e|ean|eant|ight|ind|agic|onster|aster|other|ove|oment|ountain|ission|ay|an|en)\b", "m$1");
                s = Regex.Replace(s, @"\b(ti|so|co|ho|na|ga|sa|fla|fra|sha|bla)rne(s|d|one|thing|where|how|times)?\b", "$1me$2");
                s = Regex.Replace(s, @"\bfrorn\b", "from");
                s = Regex.Replace(s, @"\btherne\b", "theme");
                s = Regex.Replace(s, @"\benern(y|ies)\b", "enem$1");
                s = Regex.Replace(s, @"\b(?:rernember|remernber)\b", "remember");
            }

            // 5. Classic Retro / Adventure Font Typo Corrections
            s = Regex.Replace(s, @"\bLinl[ec]\b|\bLineha\b", "Link");
            s = Regex.Replace(s, @"\bfrule\s+Castle\b", "Hyrule Castle");
            s = Regex.Replace(s, @"\bfrule\b", "Hyrule");
            s = Regex.Replace(s, @"\bEar\s+Navi\b", "Dear Navi");
            s = Regex.Replace(s, @"\bOwevfr\b", "However");
            s = Regex.Replace(s, @"\bMusteprotect\b", "Must protect");
            s = Regex.Replace(s, @"\bIfiite:e\b|\bIfiite\b", "if it");
            s = Regex.Replace(s, @"\bChe;\b|\bChe\b(?=\s+[a-z]{3,})", "the");
            s = Regex.Replace(s, @"\berad\b(?=\s+of)", "end");
            s = Regex.Replace(s, @"\bT0\*\b|\bT0\b", "To");
            s = Regex.Replace(s, @"\bIbhppe\s+o\b|\bIbhppe\b", "beyond help");
            s = Regex.Replace(s, @"\binteétioh\b|\binteetioh\b", "intention");

            // 6. Filter genuine non-ASCII / mixed-case 3D texture garbage tokens (e.g. 9äUxyfios) without deleting valid words or numbers
            if (srcLang == "en" || srcLang == "auto")
            {
                string[] tokens = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> cleanTokens = new List<string>();
                foreach (string tok in tokens)
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;

                    // Only drop token if it starts with a digit AND has non-ASCII diacritics or random mid-word uppercase (like 9äUxyfios)
                    if (Regex.IsMatch(t, @"^\d+[a-zA-Z\u00C0-\u024F]*[\u00C0-\u024F][a-zA-Z\u00C0-\u024F]*$") ||
                        Regex.IsMatch(t, @"^\d+[a-z]+[A-Z][a-zA-Z0-9]{2,}$"))
                        continue;

                    // Isolated noise punctuation
                    if (Regex.IsMatch(t, @"^[•*#~^\|\\/<>_]{1,3}$"))
                        continue;

                    cleanTokens.Add(t);
                }
                s = string.Join(" ", cleanTokens.ToArray());

                // 7. Universal Diacritic Normalization for English/Latin text
                s = RemoveDiacritics(s);
                // Strip single stray consonants at sentence boundaries (e.g. "protect t.")
                s = Regex.Replace(s, @"\b([a-zA-Z]{3,})\s+[b-df-hj-np-tv-zB-DF-HJ-NP-TV-Z](?=[\.\,\!\?]|\s*$)", "$1");
            }

            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string normalized = text.Normalize(NormalizationForm.FormD);
                StringBuilder sb = new StringBuilder();
                foreach (char c in normalized)
                {
                    var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                    if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                        sb.Append(c);
                }
                return sb.ToString().Normalize(NormalizationForm.FormC);
            }
            catch { return text; }
        }

        private static IPEndPoint BindIpv4Only(ServicePoint servicePoint, IPEndPoint remoteEndPoint, int retryCount)
        {
            if (remoteEndPoint.AddressFamily == AddressFamily.InterNetwork || retryCount > 2)
                return new IPEndPoint(IPAddress.Any, 0);
            throw new IOException("Skip IPv6 to prevent 2.5s timeout on IPv4-only ISP routes");
        }

        private static string HttpRequestFast(string url, string postBody = null, string contentType = "application/x-www-form-urlencoded", int timeoutMs = 1800)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.KeepAlive = true;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                req.ServicePoint.UseNagleAlgorithm = false;
                req.ServicePoint.Expect100Continue = false;
                req.ServicePoint.BindIPEndPointDelegate = BindIpv4Only;
            }
            catch { }

            if (postBody != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(postBody);
                req.Method = "POST";
                req.ContentType = contentType;
                req.ContentLength = data.Length;
                using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            }
            else req.Method = "GET";

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static string RecognizeOnlineOcrSpace(Bitmap bmp, string srcLang, out string error)
        {
            error = "";
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    string b64 = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    string postData = "apikey=helloworld&OCREngine=2&scale=true&language=" +
                                      Uri.EscapeDataString(MapOcrSpaceLang(srcLang)) +
                                      "&base64Image=" + Uri.EscapeDataString(b64);
                    string json = HttpRequestFast("https://api.ocr.space/parse/image", postData);
                    int idx = json.IndexOf("\"ParsedText\":\"", StringComparison.Ordinal);
                    if (idx < 0) return "";
                    return ReadJsonString(json, idx + 13);
                }
            }
            catch (Exception ex) { error = "OCR Online gagal: " + ex.Message; return ""; }
        }

        public static string NormalizeChoices(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            List<string> cleaned = new List<string>();
            foreach (string ln in lines)
            {
                string s = CleanGameOcrText(ln, "");
                s = Regex.Replace(s, @"[|_~=<>▸►▶•·]+", " ");
                s = Regex.Replace(s, @"\s+[xX×○△□]\s*$", "");
                s = Regex.Replace(s, @"\s+", " ").Trim();
                if (s.Length >= 2) cleaned.Add(s);
            }
            return string.Join("\n", cleaned.ToArray());
        }

        public static string NormalizeText(string raw, string srcLang, bool mergeLines)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            List<string> cleaned = new List<string>();

            foreach (string ln in lines)
            {
                string s = CleanGameOcrText(ln, srcLang);
                s = Regex.Replace(s, @"[|_~=<>]{2,}", " ");
                s = s.Trim(' ', '\t', '|', '_', '~', '=');
                s = Regex.Replace(s, @"\s+", " ");
                if (s.Length >= 2 || (s.Length == 1 && char.IsLetterOrDigit(s[0]))) cleaned.Add(s);
            }
            if (cleaned.Count == 0) return "";

            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW");
            string joined;
            if (!mergeLines) joined = string.Join("\n", cleaned.ToArray());
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < cleaned.Count; i++)
                {
                    string part = cleaned[i];
                    if (i > 0)
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] == '-' && part.Length > 0 && char.IsLower(part[0]))
                            sb.Length--;
                        else if (!isCjk)
                            sb.Append(' ');
                    }
                    sb.Append(part);
                }
                joined = sb.ToString();
            }

            joined = Regex.Replace(joined, @"\s+([,\.\!\?\;\:])", "$1");
            joined = Regex.Replace(joined, @"([,\!\?\;])([^\s\d""'\)\]\}])", "$1 $2");
            return joined.Trim();
        }

        public static string NormalizeCoreText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string low = s.Trim().ToLowerInvariant();
            // Replace all punctuation & bracket symbols with space so core words are compared
            low = Regex.Replace(low, @"[.,!?;:""'’“”\-—_\[\]\(\)\{\}\<\>\\\/|*~`#@^=+]+", " ");
            low = Regex.Replace(low, @"\s+", " ").Trim();
            return low;
        }

        public static bool EndsWithTerminalPunctuation(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.TrimEnd();
            if (t.Length == 0) return false;
            char last = t[t.Length - 1];
            return last == '.' || last == '!' || last == '?' || last == '"' || last == '”' ||
                   last == '…' || last == ')' || last == ']' || last == '}' || last == ':' ||
                   t.EndsWith("...") || t.EndsWith("..") || last == '。' || last == '！' || last == '？';
        }

        public static int LevenshteinDistance(string a, string b)
        {
            if (a == b) return 0;
            if (string.IsNullOrEmpty(a)) return b == null ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;

            int[] prev = new int[b.Length + 1];
            int[] curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                }
                int[] tmp = prev; prev = curr; curr = tmp;
            }
            return prev[b.Length];
        }

        public static float ComputeSimilarity(string a, string b)
        {
            if (a == b) return 1f;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            string la = a.ToLowerInvariant(), lb = b.ToLowerInvariant();
            if (la == lb) return 1f;
            int maxLen = Math.Max(la.Length, lb.Length);
            if (maxLen > 240)
            {
                int same = 0, minL = Math.Min(la.Length, lb.Length);
                for (int i = 0; i < minL; i++) if (la[i] == lb[i]) same++;
                return (float)same / maxLen;
            }
            int dist = LevenshteinDistance(la, lb);
            return 1f - ((float)dist / maxLen);
        }

        public string TranslateChoicesLineByLine(string multilineChoices, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, bool naturalTone, out string usedProvider, out string error, string glossary = "")
        {
            usedProvider = preferredEngine;
            error = "";
            string[] lines = multilineChoices.Split('\n');
            if (lines.Length == 1)
            {
                string single = TranslateText(lines[0], srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error, glossary);
                return string.IsNullOrEmpty(single) ? "" : ("• " + single);
            }

            string batchResult = TranslateText(multilineChoices, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error, glossary);
            if (!string.IsNullOrEmpty(batchResult))
            {
                string[] outLines = batchResult.Replace("\r\n", "\n").Split('\n');
                if (outLines.Length == lines.Length)
                {
                    List<string> formatted = new List<string>();
                    foreach (string ol in outLines)
                    {
                        string t = ol.Trim();
                        if (!string.IsNullOrEmpty(t)) formatted.Add("• " + t);
                    }
                    return string.Join("\n", formatted.ToArray());
                }
            }

            List<string> results = new List<string>();
            foreach (string ln in lines)
            {
                string trimmed = ln.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                string tr = TranslateText(trimmed, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error, glossary);
                if (!string.IsNullOrEmpty(tr)) results.Add("• " + tr.Trim());
            }
            return string.Join("\n", results.ToArray());
        }

        public string TranslateText(string text, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, bool naturalTone, out string usedProvider, out string error, string glossary = "")
        {
            usedProvider = preferredEngine == "mymemory" ? "MyMemory" : (preferredEngine == "gemini" ? "Gemini AI" : "Google");
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return "";

            string coreKey = NormalizeCoreText(text);
            string cacheKey = preferredEngine + "|" + (naturalTone ? "N" : "L") + "|" + srcLang + "|" + tgtLang + "|" + (glossary ?? "") + "|" + (string.IsNullOrEmpty(coreKey) ? text.Trim() : coreKey);
            lock (_cacheLock)
            {
                string hit;
                if (_cache.TryGetValue(cacheKey, out hit))
                {
                    usedProvider += " (Instan)";
                    return hit;
                }
            }

            List<GlossaryEntry> glossEntries = ParseGlossary(glossary);
            string protectedText = ProtectGlossary(text, glossEntries);
            string prepped = PreProcessGameIdioms(protectedText, srcLang, tgtLang, naturalTone);
            string result = "";

            if (preferredEngine == "gemini" && !string.IsNullOrWhiteSpace(geminiKey))
            {
                result = CallGemini(prepped, tgtLang, geminiKey, naturalTone, glossary, out error);
                if (!string.IsNullOrEmpty(result)) usedProvider = "Gemini AI";
            }

            if (string.IsNullOrEmpty(result))
            {
                if (preferredEngine == "mymemory")
                {
                    result = CallMyMemory(prepped, srcLang, tgtLang, out error);
                    if (!string.IsNullOrEmpty(result)) usedProvider = "MyMemory";
                    else
                    {
                        result = CallGoogleGtx(prepped, srcLang, tgtLang, out error);
                        if (!string.IsNullOrEmpty(result)) { usedProvider = "Google (Cadangan)"; error = ""; }
                    }
                }
                else
                {
                    result = CallGoogleGtx(prepped, srcLang, tgtLang, out error);
                    if (!string.IsNullOrEmpty(result)) usedProvider = "Google";
                    else
                    {
                        result = CallMyMemory(prepped, srcLang, tgtLang, out error);
                        if (!string.IsNullOrEmpty(result)) { usedProvider = "MyMemory (Cadangan)"; error = ""; }
                    }
                }
            }

            if (!string.IsNullOrEmpty(result))
            {
                result = NaturalizeIndonesian(result, tgtLang, naturalTone);
                result = RestoreGlossary(result, glossEntries);
            }

            if (!string.IsNullOrEmpty(result))
            {
                lock (_cacheLock)
                {
                    if (_cache.Count > 500) _cache.Clear();
                    _cache[cacheKey] = result;
                }
            }
            return result;
        }

        private static string CallGemini(string text, string tgtLang, string apiKey, bool naturalTone, string glossary, out string error)
        {
            error = "";
            try
            {
                string langName = tgtLang == "id" ? "Indonesian" : (tgtLang == "ms" ? "Malay" : tgtLang);
                string style = naturalTone ? "polite everyday conversational RPG dialogue (natural, friendly, use aku/kamu, avoid stiff formal words like Anda/saya/mengapa/apakah)" : "accurate natural translation";
                string glossNote = string.IsNullOrWhiteSpace(glossary) ? "" : (" Keep/apply these game glossary terms: " + glossary.Replace("\r", "").Replace("\n", "; ") + ".");
                string prompt = "Translate this game text into " + langName + " using " + style + "." + glossNote + " Return ONLY the translated text, preserving line breaks:\n" + text;
                string escaped = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                string body = "{\"contents\":[{\"parts\":[{\"text\":\"" + escaped + "\"}]}]}";
                string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=" + Uri.EscapeDataString(apiKey.Trim());
                string json = HttpRequestFast(url, body, "application/json", 3800);
                int idx = json.IndexOf("\"text\": \"", StringComparison.Ordinal);
                if (idx < 0) idx = json.IndexOf("\"text\":\"", StringComparison.Ordinal);
                if (idx < 0) return "";
                int q = json.IndexOf('"', idx + 6);
                return ReadJsonString(json, q).Trim();
            }
            catch (Exception ex) { error = "Gemini error: " + ex.Message; return ""; }
        }

        private static readonly string[] GoogleGtxHosts = new string[]
        {
            "https://translate.googleapis.com/translate_a/single",
            "https://translate.google.com/translate_a/single",
            "https://translate.google.co.id/translate_a/single"
        };

        private static string CallGoogleGtx(string text, string srcLang, string tgtLang, out string error)
        {
            error = "";
            string sl = string.IsNullOrEmpty(srcLang) ? "auto" : srcLang;
            string tl = string.IsNullOrEmpty(tgtLang) ? "id" : tgtLang;
            string query = "?client=gtx&sl=" + Uri.EscapeDataString(sl) +
                           "&tl=" + Uri.EscapeDataString(tl) +
                           "&dt=t&q=" + Uri.EscapeDataString(text);

            Exception lastEx = null;
            for (int i = 0; i < GoogleGtxHosts.Length; i++)
            {
                try
                {
                    string json = HttpRequestFast(GoogleGtxHosts[i] + query, null, "application/x-www-form-urlencoded", 1300);
                    string parsed = ParseGoogleGtxJson(json);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        error = "";
                        return parsed;
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }
            if (lastEx != null) error = "Google error: " + lastEx.Message;
            return "";
        }

        private static string CallMyMemory(string text, string srcLang, string tgtLang, out string error)
        {
            error = "";
            try
            {
                string sl = (string.IsNullOrEmpty(srcLang) || srcLang == "auto") ? "en" : srcLang;
                string tl = string.IsNullOrEmpty(tgtLang) ? "id" : tgtLang;
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                             "&langpair=" + Uri.EscapeDataString(sl + "|" + tl);
                string json = HttpRequestFast(url, null, "application/x-www-form-urlencoded", 1800);
                if (string.IsNullOrEmpty(json)) return "";
                if (json.Contains("\"quotaFinished\":true") || json.Contains("\"responseStatus\":429"))
                {
                    error = "MyMemory daily limit exceeded";
                    return "";
                }
                int idx = json.IndexOf("\"translatedText\":\"", StringComparison.Ordinal);
                if (idx < 0) return "";
                string parsed = ReadJsonString(json, idx + 17);
                if (parsed.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    error = "MyMemory quota exhausted";
                    return "";
                }
                return parsed;
            }
            catch (Exception ex) { error = "MyMemory error: " + ex.Message; return ""; }
        }

        public static string ParseGoogleGtxJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length < 4 || json[0] != '[') return "";
            int pos = 1;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length || json[pos] != '[') return "";
            pos++;

            StringBuilder sb = new StringBuilder();
            while (pos < json.Length)
            {
                while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == ',')) pos++;
                if (pos >= json.Length || json[pos] == ']') break;
                if (json[pos] == '[')
                {
                    pos++;
                    while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                    if (pos < json.Length && json[pos] == '"')
                    {
                        int endPos;
                        sb.Append(ReadJsonString(json, pos, out endPos));
                        pos = endPos;
                    }
                    int depth = 1;
                    bool inStr = false;
                    while (pos < json.Length && depth > 0)
                    {
                        char c = json[pos++];
                        if (inStr) { if (c == '\\' && pos < json.Length) pos++; else if (c == '"') inStr = false; }
                        else { if (c == '"') inStr = true; else if (c == '[') depth++; else if (c == ']') depth--; }
                    }
                }
                else break;
            }
            return sb.ToString();
        }

        private static string ReadJsonString(string json, int quoteIndex)
        {
            int end; return ReadJsonString(json, quoteIndex, out end);
        }

        private static string ReadJsonString(string json, int quoteIndex, out int endPos)
        {
            StringBuilder sb = new StringBuilder();
            int i = quoteIndex + 1;
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') { endPos = i; return sb.ToString(); }
                if (c == '\\' && i < json.Length)
                {
                    char n = json[i++];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                { sb.Append((char)code); i += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            endPos = json.Length;
            return sb.ToString();
        }
    }
}
