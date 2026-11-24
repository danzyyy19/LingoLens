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
    public class DialogLogEntry
    {
        public DateTime Time;
        public string Speaker;
        public string Source;
        public string Translated;
    }

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
        private volatile int _fastPollMs;

        private readonly Dictionary<int, byte[]> _prevSigs = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _prevFrameBufByArea = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, int> _prevFrameWByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _prevFrameHByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, byte[]> _prevTextMaskByArea = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, int> _prevTextPtsByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, byte[]> _lockedTextMaskByArea = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, int> _lockedTextPtsByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _emptyStreakByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _candOcrByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _candScoreByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _candHitsByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _fadeHoldTicksByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _lastOcrByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _lastOcrScoreByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _lastTransByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _lastActiveUtc = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, bool> _idleHidden = new Dictionary<int, bool>();
        private readonly Dictionary<int, string> _pendingOcrByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _pendingCountByArea = new Dictionary<int, int>();
        private readonly Dictionary<int, DateTime> _pendingStartUtc = new Dictionary<int, DateTime>();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private readonly List<DialogLogEntry> _dialogHistory = new List<DialogLogEntry>();
        private readonly object _cacheLock = new object();

        private string _cachedOcrLangTag = "";
        private OcrEngine _ocrEngine;
        private List<string> _installedOcrLangs = new List<string>();

        public event EventHandler<ScanUpdateEventArgs> OnAreaTranslated;
        public event Action<bool> OnTargetWindowActiveChanged;
        private bool _lastTargetActive = true;
        private static int _bestGtxHostIdx = 0;

        public bool IsRunning { get { return _running; } }
        public List<string> InstalledOcrLanguages { get { return new List<string>(_installedOcrLangs); } }

        public List<DialogLogEntry> GetDialogHistory()
        {
            lock (_cacheLock)
            {
                return new List<DialogLogEntry>(_dialogHistory);
            }
        }

        public void ClearDialogHistory()
        {
            lock (_cacheLock)
            {
                _dialogHistory.Clear();
            }
        }

        private void RecordDialogHistory(string speaker, string src, string trans)
        {
            if (string.IsNullOrWhiteSpace(trans)) return;
            lock (_cacheLock)
            {
                if (_dialogHistory.Count > 0)
                {
                    DialogLogEntry last = _dialogHistory[_dialogHistory.Count - 1];
                    if (string.Equals(NormalizeCoreText(last.Source), NormalizeCoreText(src), StringComparison.OrdinalIgnoreCase))
                        return;
                }
                _dialogHistory.Add(new DialogLogEntry
                {
                    Time = DateTime.Now,
                    Speaker = speaker ?? "",
                    Source = src ?? "",
                    Translated = trans ?? ""
                });
                if (_dialogHistory.Count > 120) _dialogHistory.RemoveAt(0);
            }
        }

        private string BuildRecentStoryContext()
        {
            lock (_cacheLock)
            {
                if (_dialogHistory.Count == 0) return "";
                int start = Math.Max(0, _dialogHistory.Count - 5);
                StringBuilder sb = new StringBuilder();
                for (int i = start; i < _dialogHistory.Count; i++)
                {
                    DialogLogEntry d = _dialogHistory[i];
                    if (!string.IsNullOrEmpty(d.Speaker)) sb.Append(d.Speaker).Append(": ");
                    sb.Append(d.Source).Append(" => ").AppendLine(d.Translated);
                }
                return sb.ToString().Trim();
            }
        }

        public TranslationEngine(AppConfig cfg)
        {
            _cfg = cfg;
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.DefaultConnectionLimit = 32;
            }
            catch { }

            // Pre-warm HTTPS TLS Keep-Alive connection to primary Google Translate CDN in background
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HttpRequestFast("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=id&dt=t&q=ok", null, "application/x-www-form-urlencoded", 2200);
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
                _prevFrameBufByArea.Remove(areaId);
                _prevFrameWByArea.Remove(areaId);
                _prevFrameHByArea.Remove(areaId);
                _prevTextMaskByArea.Remove(areaId);
                _prevTextPtsByArea.Remove(areaId);
                _lockedTextMaskByArea.Remove(areaId);
                _lockedTextPtsByArea.Remove(areaId);
                _emptyStreakByArea.Remove(areaId);
                _candOcrByArea.Remove(areaId);
                _candScoreByArea.Remove(areaId);
                _candHitsByArea.Remove(areaId);
                _fadeHoldTicksByArea.Remove(areaId);
                _lastOcrByArea.Remove(areaId);
                _lastOcrScoreByArea.Remove(areaId);
                _lastTransByArea.Remove(areaId);
                _lastActiveUtc.Remove(areaId);
                _idleHidden.Remove(areaId);
                _pendingOcrByArea.Remove(areaId);
                _pendingCountByArea.Remove(areaId);
                _pendingStartUtc.Remove(areaId);
            }
        }

        public void ClearState()
        {
            lock (_cacheLock)
            {
                _prevSigs.Clear();
                _prevFrameBufByArea.Clear();
                _prevFrameWByArea.Clear();
                _prevFrameHByArea.Clear();
                _prevTextMaskByArea.Clear();
                _prevTextPtsByArea.Clear();
                _lockedTextMaskByArea.Clear();
                _lockedTextPtsByArea.Clear();
                _emptyStreakByArea.Clear();
                _candOcrByArea.Clear();
                _candScoreByArea.Clear();
                _candHitsByArea.Clear();
                _fadeHoldTicksByArea.Clear();
                _lastOcrByArea.Clear();
                _lastOcrScoreByArea.Clear();
                _lastTransByArea.Clear();
                _lastActiveUtc.Clear();
                _idleHidden.Clear();
                _pendingOcrByArea.Clear();
                _pendingCountByArea.Clear();
                _pendingStartUtc.Clear();
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
            string lastGame = "";
            bool lastMerge = true;
            int lastTone = 0;
            bool lastSpeaker = true;
            bool lastInstant = true;

            while (!_stop)
            {
                if (!_running || _pausedForEdit) { Thread.Sleep(50); continue; }

                List<Area> snapshot = new List<Area>();
                string srcLang, tgtLang, engineName, geminiKey, glossary, gameTitle, gameLore, targetProc, targetTitle;
                int intervalMs, autoHideSec, toneMode;
                bool waitStable, mergeLines, detectSpeaker, enhanceOcr, instantSub;

                lock (_cfg.Sync)
                {
                    srcLang = _cfg.Src;
                    tgtLang = _cfg.Tgt;
                    engineName = _cfg.Engine;
                    geminiKey = _cfg.GeminiApiKey;
                    glossary = _cfg.Glossary ?? "";
                    gameTitle = _cfg.ActiveGameTitle ?? "";
                    gameLore = _cfg.GameLoreSummary ?? "";
                    targetProc = _cfg.TargetProcessName ?? "";
                    targetTitle = _cfg.TargetWindowTitle ?? "";
                    toneMode = _cfg.ToneMode;
                    detectSpeaker = _cfg.DetectSpeaker;
                    enhanceOcr = _cfg.EnhanceGameOcr;
                    instantSub = _cfg.InstantSubtitleMode;
                    intervalMs = Math.Max(55, Math.Min(3000, _cfg.Interval));
                    autoHideSec = Math.Max(0, _cfg.AutoHideSeconds);
                    waitStable = _cfg.WaitStable;
                    mergeLines = _cfg.MergeLines;
                    foreach (Area a in _cfg.Areas)
                    {
                        snapshot.Add(new Area { Id = a.Id, Enabled = a.Enabled, Mode = a.Mode, Ocr = a.Ocr, Result = a.Result });
                    }
                }

                // TARGET GAME WINDOW LOCK: if locked to a specific game/window and user switches to another app,
                // pause OCR completely (0% CPU, never reads chat/IDE/MyASUS!) and auto-hide overlay!
                bool targetActive = Native.IsTargetWindowForeground(targetProc, targetTitle);
                if (targetActive != _lastTargetActive)
                {
                    _lastTargetActive = targetActive;
                    Action<bool> cb = OnTargetWindowActiveChanged;
                    if (cb != null) cb(targetActive);
                }
                if (!targetActive)
                {
                    Thread.Sleep(90);
                    continue;
                }

                if (snapshot.Count == 0) { Thread.Sleep(120); continue; }

                bool configChanged = (srcLang != lastSrc) || (tgtLang != lastTgt) ||
                                     (engineName != lastEngine) || (mergeLines != lastMerge) ||
                                     (toneMode != lastTone) || (glossary != lastGlossary) ||
                                     (detectSpeaker != lastSpeaker) || (instantSub != lastInstant) ||
                                     (gameTitle != lastGame);
                if (configChanged)
                {
                    ClearState();
                    lastSrc = srcLang; lastTgt = tgtLang; lastEngine = engineName;
                    lastMerge = mergeLines; lastTone = toneMode; lastGlossary = glossary;
                    lastSpeaker = detectSpeaker; lastInstant = instantSub; lastGame = gameTitle;
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

                    ProcessArea(area, srcLang, tgtLang, engineName, geminiKey, glossary, gameTitle, gameLore, toneMode, detectSpeaker, instantSub, waitStable, mergeLines, enhanceOcr, autoHideSec, forced);
                }

                int waitTarget = _fastPollMs > 0 ? _fastPollMs : intervalMs;
                _fastPollMs = 0;
                int slept = 0;
                while (slept < waitTarget && !_stop && _running && !_forceScan && !_pausedForEdit)
                {
                    Thread.Sleep(15);
                    slept += 15;
                }
            }
        }

        private void ProcessArea(Area area, string srcLang, string tgtLang, string engineName, string geminiKey, string glossary,
            string gameTitle, string gameLore, int toneMode, bool detectSpeaker, bool instantSub, bool waitStable, bool mergeLines, bool enhanceOcr, int autoHideSec, bool forced)
        {
            if (area.Ocr.Width < 12 || area.Ocr.Height < 10) return;

            Bitmap frame = CaptureRect(area.Ocr);
            if (frame == null) return;

            try
            {
                ProcessCapturedFrame(area.Id, frame, area.Mode, srcLang, tgtLang, engineName, geminiKey, glossary,
                    gameTitle, gameLore, toneMode, detectSpeaker, instantSub, waitStable, mergeLines, enhanceOcr, autoHideSec, forced);
            }
            finally
            {
                if (frame != null) frame.Dispose();
            }
        }

        public void ProcessCapturedFrame(int areaId, Bitmap frame, AreaTextMode areaMode, string srcLang, string tgtLang, string engineName, string geminiKey, string glossary,
            string gameTitle, string gameLore, int toneMode, bool detectSpeaker, bool instantSub, bool waitStable, bool mergeLines, bool enhanceOcr, int autoHideSec, bool forced)
        {
            if (frame == null || frame.Width < 12 || frame.Height < 10) return;

            Stopwatch sw = Stopwatch.StartNew();
            byte[] sig = ComputeSignature(frame);
            byte[] prevSig;
            bool hasPrev = _prevSigs.TryGetValue(areaId, out prevSig);
            int pendingCount = 0;
            _pendingCountByArea.TryGetValue(areaId, out pendingCount);
            string pendingText = "";
            _pendingOcrByArea.TryGetValue(areaId, out pendingText);

            bool screenStatic = false;
            if (!forced && hasPrev)
            {
                float diff = ComputeDiffPercent(sig, prevSig);
                if (diff < 0.45f)
                {
                    if (pendingCount > 0 && !string.IsNullOrEmpty(pendingText))
                    {
                        screenStatic = true;
                    }
                    else
                    {
                        CheckIdleTimeout(areaId, autoHideSec);
                        return;
                    }
                }
            }

            _prevSigs[areaId] = sig;

            bool treatAsChoice = (areaMode == AreaTextMode.Choice);
            string cleanText;
            int newOcrScore;
            byte[] curTextMask = null;
            int curTextPts = 0;
            byte[] staticSubMask = null;
            int staticSubPts = 0;

            string prevOcr, prevTrans;
            int prevOcrScore = 0;
            _lastOcrByArea.TryGetValue(areaId, out prevOcr);
            _lastOcrScoreByArea.TryGetValue(areaId, out prevOcrScore);
            _lastTransByArea.TryGetValue(areaId, out prevTrans);

            if (screenStatic)
            {
                cleanText = pendingText;
                newOcrScore = ScoreOcrQuality(cleanText, srcLang);
                _pendingCountByArea[areaId] = 0;
            }
            else
            {
                // Extract current frame raw BGRA + fast 320x80 bright-stroke text mask (< 0.5ms)
                int fw = frame.Width, fh = frame.Height;
                byte[] curBgra = CopyBitmapToTightBgra(frame);
                curTextMask = ComputeBrightTextMask(curBgra, fw, fh, out curTextPts);

                byte[] prevBgra = null;
                int prevW = 0, prevH = 0;
                _prevFrameBufByArea.TryGetValue(areaId, out prevBgra);
                _prevFrameWByArea.TryGetValue(areaId, out prevW);
                _prevFrameHByArea.TryGetValue(areaId, out prevH);

                byte[] prevMask = null;
                int prevPts = 0;
                _prevTextMaskByArea.TryGetValue(areaId, out prevMask);
                _prevTextPtsByArea.TryGetValue(areaId, out prevPts);

                bool hasPairedPrev = (prevBgra != null && prevW == fw && prevH == fh);
                int prevInter = 0;
                float prevOverlap = 0f;
                if (hasPairedPrev && prevMask != null && prevPts > 0 && curTextPts > 0)
                {
                    prevInter = ComputeMaskIntersection(prevMask, curTextMask);
                    prevOverlap = (float)prevInter / Math.Min(prevPts, curTextPts);
                }

                // Update stored frame & mask for next tick
                _prevFrameBufByArea[areaId] = curBgra;
                _prevFrameWByArea[areaId] = fw;
                _prevFrameHByArea[areaId] = fh;
                _prevTextMaskByArea[areaId] = curTextMask;
                _prevTextPtsByArea[areaId] = curTextPts;

                // PIXEL-SPACE STATIC SUBTITLE FAST-LOCK (0 ms OCR, 0 API calls, ZERO flip-flopping!):
                // Compare pure static-subtitle mask (prev & cur intersection, free of moving 3D sparks!) against curTextMask.
                // As long as >= 78% of the locked subtitle's exact stroke pixels remain on screen, skip OCR & translation completely!
                byte[] lockedMask = null;
                int lockedPts = 0;
                bool lockedSubJustChanged = false;
                if (!forced && !treatAsChoice && enhanceOcr && !string.IsNullOrEmpty(prevTrans) && prevOcrScore >= 82 &&
                    _lockedTextMaskByArea.TryGetValue(areaId, out lockedMask) && lockedMask != null &&
                    _lockedTextPtsByArea.TryGetValue(areaId, out lockedPts) && lockedPts >= 16)
                {
                    int lockInter = (curTextPts > 0) ? ComputeMaskIntersection(lockedMask, curTextMask) : 0;
                    float lockRecall = (float)lockInter / lockedPts;
                    if (curTextPts >= 16 && lockRecall >= 0.78f)
                    {
                        _emptyStreakByArea[areaId] = 0;
                        _candHitsByArea[areaId] = 0;
                        _fadeHoldTicksByArea[areaId] = 0;
                        _lastActiveUtc[areaId] = DateTime.UtcNow;
                        CheckIdleTimeout(areaId, autoHideSec);
                        return;
                    }
                    // The previously locked subtitle strokes just vanished / were replaced by a brand-new subtitle!
                    if (curTextPts >= 16 && lockRecall < 0.55f)
                        lockedSubJustChanged = true;
                }

                // FADE-IN / BRAND-NEW SUBTITLE SETTLE GUARD (1-2 fast 55ms ticks):
                // If bright text strokes just surged (mid-fade-in) OR a brand-new subtitle just replaced the old one,
                // hold ONE 55ms tick so fade-in settles to 100% opacity AND pairs 2 clean frames of the NEW subtitle for Temporal Eraser!
                int fadeTicks = 0;
                _fadeHoldTicksByArea.TryGetValue(areaId, out fadeTicks);
                bool maskJustSurged = (curTextPts >= 14 && (prevPts < 8 || (curTextPts > prevPts && (curTextPts - prevPts) * 100 / curTextPts >= 32)));
                bool brandNewSubSwitched = (curTextPts >= 14 && prevInter < 24);
                if (!forced && enhanceOcr && hasPairedPrev && fadeTicks < 2 &&
                    (lockedSubJustChanged || maskJustSurged || brandNewSubSwitched || IsMidFadeInTransition(prevBgra, curBgra, fw, fh)))
                {
                    if (lockedSubJustChanged)
                    {
                        // Unlock pixel mask so the very next paired frame (55ms later) runs Temporal Eraser on the new subtitle!
                        _lockedTextMaskByArea.Remove(areaId);
                        _lockedTextPtsByArea.Remove(areaId);
                    }
                    _fadeHoldTicksByArea[areaId] = fadeTicks + 1;
                    _fastPollMs = 55;
                    return;
                }
                _fadeHoldTicksByArea[areaId] = 0;

                // SAFE TEMPORAL ERASER GUARD (Fixes BUG 1 + 100% immune to chaotic moving bright 3D sparks!):
                // Checks static pixel-aligned stroke intersection (prevInter >= 14) without denominator pollution from moving 3D glare.
                // Even if a static 3D pillar yields prevInter >= 14 during a subtitle switch, Pass 1/2/3 on curFrame automatically beat empty Pass 0!
                bool allowTemporal = hasPairedPrev && prevInter >= 14;
                if (allowTemporal && prevMask != null)
                {
                    staticSubMask = IntersectMasks(prevMask, curTextMask, out staticSubPts);
                }

                bool usedOnline, detectedChoiceLayout;
                string ocrError;
                string rawText = RecognizeBitmapWithTemporal(frame, curBgra, hasPairedPrev ? prevBgra : null, allowTemporal, srcLang, enhanceOcr, out usedOnline, out detectedChoiceLayout, out ocrError);

                if (!string.IsNullOrEmpty(ocrError))
                {
                    RaiseResult(areaId, "", "", (int)sw.ElapsedMilliseconds, ocrError, true);
                    return;
                }

                treatAsChoice = (areaMode == AreaTextMode.Choice) || detectedChoiceLayout;
                cleanText = treatAsChoice
                    ? NormalizeChoices(rawText, srcLang)
                    : NormalizeText(rawText, srcLang, mergeLines);

                // Fuzzy Glossary Auto-Repair before anti-flicker lock & translation (fixes OCR misreads of character/place names!)
                if (!string.IsNullOrEmpty(cleanText) && !string.IsNullOrEmpty(glossary))
                    cleanText = RepairOcrWithGlossary(cleanText, glossary);

                if (string.IsNullOrEmpty(cleanText) || (!treatAsChoice && !IsMeaningfulSubtitle(cleanText, srcLang)))
                {
                    _pendingOcrByArea[areaId] = "";
                    _pendingCountByArea[areaId] = 0;
                    _pendingStartUtc.Remove(areaId);
                    _candHitsByArea[areaId] = 0;

                    int emptyStreak = 0;
                    _emptyStreakByArea.TryGetValue(areaId, out emptyStreak);
                    emptyStreak++;
                    _emptyStreakByArea[areaId] = emptyStreak;

                    // EMPTY-FRAME DEBOUNCE (Fixes BUG 2!):
                    // Require 3 consecutive empty frames before wiping an active locked subtitle!
                    // Prevents a single bright camera flash / motion blur frame from breaking Anti-Flip-Flop Lock!
                    if (!string.IsNullOrEmpty(prevTrans))
                    {
                        if (!forced && emptyStreak < 3)
                        {
                            _fastPollMs = 65;
                            return;
                        }
                        _lastOcrByArea[areaId] = "";
                        _lastOcrScoreByArea[areaId] = 0;
                        _lastTransByArea[areaId] = "";
                        _lockedTextMaskByArea.Remove(areaId);
                        _lockedTextPtsByArea.Remove(areaId);
                        _idleHidden[areaId] = true;
                        RaiseResult(areaId, "", "", (int)sw.ElapsedMilliseconds, "Kosong", false);
                    }
                    return;
                }

                _emptyStreakByArea[areaId] = 0;
                newOcrScore = ScoreOcrQuality(cleanText, srcLang);

                // STRICT GARBAGE GATE: never waste 500ms+ translating severely corrupted 3D-noise OCR (< 68)!
                if (!treatAsChoice && !forced && newOcrScore < 68)
                {
                    _fastPollMs = 55;
                    CheckIdleTimeout(areaId, autoHideSec);
                    return;
                }

                // Instant Subtitle Mode (default): ZERO typewriter delay!
                if (!forced && !treatAsChoice && (!instantSub || waitStable))
                {
                    bool isTerminal = EndsWithTerminalPunctuation(cleanText);
                    string coreClean = NormalizeCoreText(cleanText);
                    string corePend = NormalizeCoreText(pendingText ?? "");
                    string corePrevConfirmed = NormalizeCoreText(prevOcr ?? "");

                    bool isNewUnconfirmed = (coreClean != corePrevConfirmed);
                    bool isExpanding = !string.IsNullOrEmpty(corePend) && coreClean.Length > corePend.Length &&
                        (coreClean.StartsWith(corePend, StringComparison.OrdinalIgnoreCase) ||
                         (corePend.Length >= 4 && ComputeSimilarity(coreClean.Substring(0, corePend.Length), corePend) >= 0.72f));

                    if (isNewUnconfirmed)
                    {
                        DateTime pendStart;
                        if (!_pendingStartUtc.TryGetValue(areaId, out pendStart) || pendingCount == 0)
                        {
                            pendStart = DateTime.UtcNow;
                            _pendingStartUtc[areaId] = pendStart;
                        }
                        double waitedMs = (DateTime.UtcNow - pendStart).TotalMilliseconds;

                        double maxHoldMs = waitStable ? 800.0 : 480.0;
                        if (waitedMs < maxHoldMs)
                        {
                            if (waitStable && (pendingCount == 0 || coreClean != corePend) && pendingCount < 14)
                            {
                                _pendingOcrByArea[areaId] = cleanText;
                                _pendingCountByArea[areaId] = pendingCount + 1;
                                _fastPollMs = 45;
                                return;
                            }

                            if (pendingCount == 0 && (!isTerminal || coreClean.Length < 20))
                            {
                                _pendingOcrByArea[areaId] = cleanText;
                                _pendingCountByArea[areaId] = 1;
                                _fastPollMs = 30;
                                return;
                            }

                            if (isExpanding && pendingCount < 12)
                            {
                                _pendingOcrByArea[areaId] = cleanText;
                                _pendingCountByArea[areaId] = pendingCount + 1;
                                _fastPollMs = (isTerminal || cleanText.EndsWith(",")) ? 60 : 30;
                                return;
                            }
                        }
                    }

                    _pendingOcrByArea[areaId] = cleanText;
                    _pendingCountByArea[areaId] = 0;
                    _pendingStartUtc[areaId] = DateTime.UtcNow;
                }
            }

            // ROCK-SOLID ANTI-FLIP-FLOP LOCK (Token Jaccard + Space-Insensitive Trigram Dice + One-Way Quality Ratchet):
            // Eliminates meaning flip-flopping on instant borderless subtitles when 3D scene moves behind static text!
            if (!forced && !string.IsNullOrEmpty(prevOcr) && !string.IsNullOrEmpty(prevTrans))
            {
                string coreNew = NormalizeCoreText(cleanText);
                string corePrev = NormalizeCoreText(prevOcr);

                if (coreNew == corePrev)
                {
                    if (staticSubMask != null && staticSubPts >= 16)
                    {
                        _lockedTextMaskByArea[areaId] = staticSubMask;
                        _lockedTextPtsByArea[areaId] = staticSubPts;
                    }
                    else if (curTextMask != null && curTextPts >= 16)
                    {
                        _lockedTextMaskByArea[areaId] = curTextMask;
                        _lockedTextPtsByArea[areaId] = curTextPts;
                    }
                    _candHitsByArea[areaId] = 0;
                    _lastActiveUtc[areaId] = DateTime.UtcNow;
                    CheckIdleTimeout(areaId, autoHideSec);
                    return;
                }

                bool isSameSub;
                bool isGenuineExpansion;
                AnalyzeSubtitleRelation(corePrev, coreNew, instantSub, out isSameSub, out isGenuineExpansion);

                if (isSameSub && !isGenuineExpansion)
                {
                    // One-Way Quality Ratchet: NEVER downgrade a clean locked subtitle to a noisier background-corrupted read!
                    // Only upgrade if the previous read was genuinely poor (< 78) and new read is dramatically cleaner (+12).
                    bool prevIncomplete = !instantSub && !EndsWithTerminalPunctuation(prevOcr) && EndsWithTerminalPunctuation(cleanText);
                    bool majorRepair = (prevOcrScore < 78 && newOcrScore >= prevOcrScore + 12 && coreNew.Length >= corePrev.Length - 2);
                    if (!prevIncomplete && !majorRepair)
                    {
                        if (staticSubMask != null && staticSubPts >= 16 && prevOcrScore >= 82)
                        {
                            _lockedTextMaskByArea[areaId] = staticSubMask;
                            _lockedTextPtsByArea[areaId] = staticSubPts;
                        }
                        _candHitsByArea[areaId] = 0;
                        _lastActiveUtc[areaId] = DateTime.UtcNow;
                        CheckIdleTimeout(areaId, autoHideSec);
                        return;
                    }
                }
            }

            // 2-FRAME NEW-SUBTITLE CONSENSUS GATE (Fixes BUG 3!):
            // Pristine reads (score >= 94, len >= 8) translate with ZERO extra delay.
            // Borderline/noisy reads (68..93) must agree across 2 consecutive frames (~55ms apart),
            // picking whichever frame scored higher — eliminating single-frame 3D noise flashes & flip-flops!
            if (!forced && !treatAsChoice && newOcrScore < 94)
            {
                string candText = "";
                int candScore = 0, candHits = 0;
                _candOcrByArea.TryGetValue(areaId, out candText);
                _candScoreByArea.TryGetValue(areaId, out candScore);
                _candHitsByArea.TryGetValue(areaId, out candHits);

                bool agrees = false;
                if (candHits > 0 && !string.IsNullOrEmpty(candText))
                {
                    string cCoreA = NormalizeCoreText(candText);
                    string cCoreB = NormalizeCoreText(cleanText);
                    if (cCoreA == cCoreB) agrees = true;
                    else
                    {
                        bool sameC, expC;
                        AnalyzeSubtitleRelation(cCoreA, cCoreB, true, out sameC, out expC);
                        agrees = sameC || ComputeTrigramDice(cCoreA, cCoreB) >= 0.56f;
                    }
                }

                if (!agrees)
                {
                    _candOcrByArea[areaId] = cleanText;
                    _candScoreByArea[areaId] = newOcrScore;
                    _candHitsByArea[areaId] = 1;
                    _fastPollMs = 55;
                    return;
                }

                // Consensus reached across 2 frames! Pick the cleaner of the two readings.
                if (candScore > newOcrScore + 1 || (Math.Abs(candScore - newOcrScore) <= 1 && candText.Length > cleanText.Length + 2))
                {
                    cleanText = candText;
                    newOcrScore = candScore;
                }
            }
            _candHitsByArea[areaId] = 0;
            _candOcrByArea[areaId] = "";

            string speakerName = "";
            string textToTranslate = cleanText;
            if (detectSpeaker && !treatAsChoice)
                textToTranslate = ExtractSpeakerTag(cleanText, out speakerName);

            string storyCtx = BuildRecentStoryContext();
            string transErr, usedProvider, translated;
            if (treatAsChoice)
            {
                translated = TranslateChoicesLineByLine(textToTranslate, srcLang, tgtLang, engineName, geminiKey, toneMode, out usedProvider, out transErr, glossary, gameTitle, gameLore, storyCtx);
            }
            else
            {
                translated = TranslateText(textToTranslate, srcLang, tgtLang, engineName, geminiKey, toneMode, out usedProvider, out transErr, glossary, gameTitle, gameLore, storyCtx);
            }
            sw.Stop();

            if (!string.IsNullOrEmpty(transErr))
            {
                RaiseResult(areaId, cleanText, "", (int)sw.ElapsedMilliseconds, transErr, true);
                return;
            }

            _lastOcrByArea[areaId] = cleanText;
            _lastOcrScoreByArea[areaId] = newOcrScore;
            _lastTransByArea[areaId] = translated;
            if (staticSubMask != null && staticSubPts >= 16)
            {
                _lockedTextMaskByArea[areaId] = staticSubMask;
                _lockedTextPtsByArea[areaId] = staticSubPts;
            }
            else if (curTextMask != null && curTextPts >= 16)
            {
                _lockedTextMaskByArea[areaId] = curTextMask;
                _lockedTextPtsByArea[areaId] = curTextPts;
            }
            else
            {
                _lockedTextMaskByArea.Remove(areaId);
                _lockedTextPtsByArea.Remove(areaId);
            }
            _lastActiveUtc[areaId] = DateTime.UtcNow;
            _idleHidden[areaId] = false;
            RecordDialogHistory(speakerName, cleanText, translated);

            string tag = (treatAsChoice ? "Pilihan · " : (!string.IsNullOrEmpty(speakerName) ? (speakerName + " · ") : "")) + usedProvider + " · " + sw.ElapsedMilliseconds + " ms";
            RaiseResult(areaId, cleanText, translated, (int)sw.ElapsedMilliseconds, tag, false, false, speakerName);
        }

        public static float ComputeTrigramDice(string coreA, string coreB)
        {
            if (string.IsNullOrEmpty(coreA) || string.IsNullOrEmpty(coreB)) return 0f;
            string a = Regex.Replace(coreA, @"\s+", "");
            string b = Regex.Replace(coreB, @"\s+", "");
            if (a == b) return 1f;
            if (a.Length < 3 || b.Length < 3) return ComputeSimilarity(a, b);

            Dictionary<string, int> countsA = new Dictionary<string, int>(a.Length);
            int totalA = a.Length - 2;
            for (int i = 0; i < totalA; i++)
            {
                string tri = a.Substring(i, 3);
                int c;
                countsA.TryGetValue(tri, out c);
                countsA[tri] = c + 1;
            }

            int totalB = b.Length - 2;
            int shared = 0;
            for (int i = 0; i < totalB; i++)
            {
                string tri = b.Substring(i, 3);
                int c;
                if (countsA.TryGetValue(tri, out c) && c > 0)
                {
                    shared++;
                    countsA[tri] = c - 1;
                }
            }
            return (2f * shared) / (totalA + totalB);
        }

        public static void AnalyzeSubtitleRelation(string corePrev, string coreNew, bool instantSub, out bool isSameSub, out bool isGenuineExpansion)
        {
            isSameSub = false;
            isGenuineExpansion = false;
            if (string.IsNullOrEmpty(corePrev) || string.IsNullOrEmpty(coreNew)) return;
            if (corePrev == coreNew) { isSameSub = true; return; }

            // Check genuine typewriter expansion (only when NOT in instant mode, or when adding >= 2 full new words)
            string[] prevToks = corePrev.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] newToks = coreNew.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (!instantSub && coreNew.Length >= corePrev.Length + 4 &&
                (coreNew.StartsWith(corePrev, StringComparison.OrdinalIgnoreCase) ||
                 (corePrev.Length >= 5 && ComputeSimilarity(coreNew.Substring(0, corePrev.Length), corePrev) >= 0.72f)))
            {
                isGenuineExpansion = true;
                return;
            }

            // If instant mode and new reading adds >= 2 brand new words at the end (multi-part subtitle append), allow update
            if (instantSub && newToks.Length >= prevToks.Length + 2 &&
                coreNew.Length >= corePrev.Length + 8 &&
                coreNew.StartsWith(corePrev, StringComparison.OrdinalIgnoreCase))
            {
                isGenuineExpansion = true;
                return;
            }

            // Token-level fuzzy overlap (tolerates 1-char OCR typo inside words caused by moving 3D video background!)
            int matchedPrev = 0;
            bool[] usedNew = new bool[newToks.Length];
            for (int i = 0; i < prevToks.Length; i++)
            {
                string pt = prevToks[i];
                for (int j = 0; j < newToks.Length; j++)
                {
                    if (usedNew[j]) continue;
                    string nt = newToks[j];
                    if (pt == nt || (pt.Length >= 4 && nt.Length >= 4 && LevenshteinDistance(pt, nt) <= 1))
                    {
                        matchedPrev++;
                        usedNew[j] = true;
                        break;
                    }
                }
            }

            int maxToks = Math.Max(1, Math.Max(prevToks.Length, newToks.Length));
            int minToks = Math.Max(1, Math.Min(prevToks.Length, newToks.Length));
            float jaccard = (float)matchedPrev / maxToks;
            float containment = (float)matchedPrev / minToks;
            float charSim = ComputeSimilarity(coreNew, corePrev);
            float triDice = ComputeTrigramDice(coreNew, corePrev);

            // In Instant Subtitle Mode, be decisively firm against background-noise flip-flopping:
            // Space-insensitive Trigram Dice catches glued/split 3D-noise variations (e.g. "mustaprotect" vs "must protect")!
            if (instantSub)
            {
                if (jaccard >= 0.55f || charSim >= 0.70f || triDice >= 0.56f ||
                    (minToks >= 2 && containment >= 0.72f && Math.Abs(prevToks.Length - newToks.Length) <= 2))
                {
                    isSameSub = true;
                    return;
                }
            }
            else
            {
                if (jaccard >= 0.70f || charSim >= 0.80f || triDice >= 0.74f ||
                    (corePrev.Length >= 12 && corePrev.Contains(coreNew) && coreNew.Length <= corePrev.Length))
                {
                    isSameSub = true;
                    return;
                }
            }
        }

        public static string RepairOcrWithGlossary(string text, string glossary)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(glossary)) return text;
            List<GlossaryEntry> entries = ParseGlossary(glossary);
            if (entries.Count == 0) return text;

            string result = text;
            foreach (GlossaryEntry ge in entries)
            {
                string target = ge.Source;
                if (string.IsNullOrEmpty(target) || target.Length < 4 || target.Contains(" ")) continue;
                int maxDist = target.Length >= 7 ? 2 : 1;

                result = Regex.Replace(result, @"\b[A-Za-z][A-Za-z0-9'\-]{2,}\b", delegate(Match m)
                {
                    string w = m.Value;
                    if (string.Equals(w, target, StringComparison.OrdinalIgnoreCase))
                        return target;
                    if (Math.Abs(w.Length - target.Length) > maxDist) return w;
                    // Guard against accidentally replacing very common English function words
                    string wl = w.ToLowerInvariant();
                    if (wl == "that" || wl == "this" || wl == "with" || wl == "from" || wl == "have" ||
                        wl == "will" || wl == "your" || wl == "what" || wl == "when" || wl == "they" ||
                        wl == "them" || wl == "then" || wl == "than" || wl == "line" || wl == "like" ||
                        wl == "live" || wl == "look" || wl == "long" || wl == "last" || wl == "left" ||
                        wl == "time" || wl == "here" || wl == "there" || wl == "where" || wl == "were" ||
                        wl == "been" || wl == "some" || wl == "come" || wl == "make" || wl == "take")
                        return w;
                    // First char should match (case-insensitive) for short 4-5 char names unless exact capital match
                    if (target.Length <= 5 && char.ToLowerInvariant(w[0]) != char.ToLowerInvariant(target[0]))
                        return w;
                    // Normalize thin-font OCR split-letter illusions BEFORE distance check (e.g. Delcu -> Deku, rn -> m, vv -> w)
                    string wNorm = wl.Replace("lc", "k").Replace("rn", "m").Replace("vv", "w").Replace("cl", "d");
                    string tLow = target.ToLowerInvariant();
                    int d = Math.Min(LevenshteinDistance(wl, tLow), LevenshteinDistance(wNorm, tLow));
                    if (d <= maxDist) return target;
                    return w;
                });
            }
            return result;
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

            // Saria: Dialog... (Exclude common non-speaker English prefixes like Note:, Warning:, Tip:, Objective:, Quest:, Chapter:, Location:, Hint:, Error:, Mission:, System:)
            Match mColon = Regex.Match(s, @"^([A-Z\p{Lu}][A-Za-z0-9 '\-]{1,20}):\s+(.+)$", RegexOptions.Singleline);
            if (mColon.Success && mColon.Groups[2].Value.Trim().Length > 0)
            {
                string cand = mColon.Groups[1].Value.Trim();
                if (!Regex.IsMatch(cand, @"^(?:http|https|note|notes|warning|caution|tip|tips|hint|hints|quest|objective|mission|chapter|act|part|stage|level|floor|room|area|location|place|time|date|day|year|status|effect|skill|item|weapon|armor|reward|bonus|tutorial|system|error|info|notice|rule|rules|goal|target|task|step|option|result|summary)$", RegexOptions.IgnoreCase))
                {
                    speaker = cand;
                    return mColon.Groups[2].Value.Trim();
                }
            }

            // Two-line: "Saria\nYou should hurry..." (Only extract if first line is not a common conversational opener/greeting)
            int nl = s.IndexOf('\n');
            if (nl > 1 && nl < 22)
            {
                string first = s.Substring(0, nl).Trim();
                string rest = s.Substring(nl + 1).Trim();
                if (first.Length >= 2 && first.Length <= 20 && char.IsUpper(first[0]) &&
                    !first.Contains(".") && !first.Contains("!") && !first.Contains("?") && !first.Contains(",") &&
                    first.Split(' ').Length <= 2 && rest.Length > first.Length &&
                    !Regex.IsMatch(first, @"^(?:Hello|Hi|Hey|Well|Now|So|Wait|Listen|Look|Stop|Go|Come|Run|Help|Please|Thanks|Thank\s+you|Sorry|Excuse\s+me|Yes|No|Okay|Alright|Right|Sure|Fine|Good|Great|What|Why|Where|When|Who|How|Here|There|This|That|These|Those|Watch\s+out|Hold\s+on|Hang\s+on|Be\s+careful|Come\s+on|Let's\s+go|Hurry|Hurry\s+up|By\s+the\s+way|Of\s+course|No\s+way|Not\s+yet|Once\s+upon|Long\s+ago|One\s+day|At\s+last|Finally|Suddenly|Meanwhile|However|Therefore|Besides|Anyway|Actually|Really|Seriously|Indeed|Perhaps|Maybe)$", RegexOptions.IgnoreCase))
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
            s = Regex.Replace(s, @"\b[Aa]nd\s+here\s+it\s+goes\b", "And it is like this");
            s = Regex.Replace(s, @"\b[Hh]ere\s+it\s+goes\b", "Here we go");
            s = Regex.Replace(s, @"\b[Hh]ere\s+we\s+go\b", "let us begin");

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

        public static string NaturalizeIndonesian(string translated, string tgtLang, int toneMode)
        {
            // toneMode: 0 = Gaul (Gue/Lo), 1 = Santai (Aku/Kamu), 2 = Baku/Formal
            if (toneMode == 2 || tgtLang != "id" || string.IsNullOrEmpty(translated)) return translated;
            string s = NaturalizeIndonesian(translated, tgtLang, true);
            if (toneMode == 0) s = ApplyIndonesianGaul(s);
            return s;
        }

        public static string ApplyIndonesianGaul(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // 1. Possessive suffixes & prepositional pronouns -> natural Gaul ("lagu gue", "nama lo", "bareng gue", "buat lo")
            s = Regex.Replace(s, @"\b(lagu|nama|teman|temen|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)ku\b", "$1 gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(lagu|nama|teman|temen|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)mu\b", "$1 lo", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\bmilikku\b|\bpunyaku\b", "punya gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilikmu\b|\bpunyamu\b", "punya lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiriku\b", "diri gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdirimu\b", "diri lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdenganku\b", "bareng gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdenganmu\b", "bareng lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpadaku\b|\bkepadaku\b", "ke gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpadamu\b|\bkepadamu\b", "ke lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntukku\b", "buat gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntukmu\b", "buat lo", RegexOptions.IgnoreCase);

            // 2. Core pronouns -> Gue / Lo
            s = Regex.Replace(s, @"\bAndalah\b|\bKamulah\b", "Lo yang");
            s = Regex.Replace(s, @"\bandalah\b|\bkamulah\b", "lo yang");
            s = Regex.Replace(s, @"\b(?:Kamu|Kau|Engkau|Anda)\b", "Lo");
            s = Regex.Replace(s, @"\b(?:kamu|kau|engkau|anda)\b", "lo");
            s = Regex.Replace(s, @"\b(?:Aku|Saya)\b", "Gue");
            s = Regex.Replace(s, @"\b(?:aku|saya)\b", "gue");
            s = Regex.Replace(s, @"\b[Kk]ami\b", "kita");

            // 3. Gaul expressions & natural everyday gamer slang
            s = Regex.Replace(s, @"\bmenanyakan\s+(?:lo|kamu|anda)\b|\bmemanggil(?:mu|\s+(?:lo|kamu))\b", "nyariin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmemintaku\s+buat\s+menjemput(?:mu|\s+lo)\b", "nyuruh gue jemput lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:gue|aku)\s+udah\s+kembali\b", "gue udah balik", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbukankah\s+gitu\b", "iya kan", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembuat(?:mu|\s+lo)\b", "bikin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengelabuhi(?:ku|\s+gue)\b", "nipu gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmelindungi(?:mu|\s+lo)\b", "ngelindungin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembantunya\b", "bantu dia", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:begitu|sangat|amat)\s+(keras|berani|bodoh|berharga|keterlaluan|cepet|cepat|lambat|mudah|sulit|susah|gampang|bagus|indah|kuat|lemah|banyak|sedikit|capek|lelah|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|gede|kecil|panas|dingin|penting|aneh|parah|seru|gila|takut|sedih|senang|marah|lucu|serius)\b", "$1 banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgak\s+apa-apa\b", "gak papa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgak\s+tahu\b", "gak tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbaik-baik\s+aja\b", "aman-aman aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btentu\s+aja\b", "pastilah", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btenang\s+aja\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bada\s+apa\b", "kenapa nih", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\s+ya\b", "oh gitu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkerja\s+bagus\b", "mantap banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkalau\b", "kalo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\b", "buat", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbelum\b", "belom", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbener-bener\b|\bsungguh\b", "beneran", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti\b", "kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmelihat\b", "liat", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btahu\b", "tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmencari\b", "nyari", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmemakai\b|\bmenggunakan\b", "pake", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembuat\b", "bikin", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengambil\b", "ngambil", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmendengar\b", "denger", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberpikir\b", "mikir", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberbicara\b", "ngomong", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberkata\b|\bkatakan\b", "bilang", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberi\s+tahu\b|\bberitahu\b", "kasih tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengerti\b", "ngerti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenunggu\b", "nunggu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bteman\b", "temen", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buang\b", "duit", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\blelah\b", "capek", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcepatlah\b", "cepetan deh", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcepat\b", "cepet", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bharus\b", "mesti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsekali\b", "banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat\b", "banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\b", "gitu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegini\b", "gini", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkarena\b", "soalnya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bingin\b|\bhendak\b", "pengen", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsedang\b", "lagi", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bakhirnya\b", "ujung-ujungnya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbanget\s+(keras|berani|bodoh|berharga|keterlaluan|cepet|cepat|lambat|mudah|sulit|susah|gampang|bagus|indah|kuat|lemah|banyak|sedikit|capek|lelah|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|gede|kecil|panas|dingin|penting|aneh|parah|seru|gila|takut|sedih|senang|marah|lucu|serius)\b", "$1 banget", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"([\.\!\?]\s+)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            if (s.Length > 0 && char.IsLower(s[0]))
                s = char.ToUpperInvariant(s[0]) + s.Substring(1);
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

            // 3. Pronouns, Possessive Suffixes (-ku / -mu) & Conversational Polish (eliminating stiff "lagu saya" / "lagu aku" / "dengan aku")
            s = Regex.Replace(s, @"\b(lagu|nama|teman|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|masa\s+lalu|masa\s+depan|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)\s+(?:saya|aku)\b", "$1ku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(lagu|nama|teman|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)\s+(?:Anda|kamu)\b", "$1mu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilik\s+(?:saya|aku)\b", "milikku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilik\s+(?:Anda|kamu)\b", "milikmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiri\s+(?:saya|aku)\b", "diriku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiri\s+(?:Anda|kamu)\b", "dirimu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdengan\s+(?:saya|aku)\b", "denganku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkepada\s+(?:saya|aku)\b|\bpada\s+(?:saya|aku)\b", "padaku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\s+(?:saya|aku)\b", "untukku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdengan\s+(?:Anda|kamu)\b", "denganmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkepada\s+(?:Anda|kamu)\b|\bpada\s+(?:Anda|kamu)\b", "padamu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\s+(?:Anda|kamu)\b", "untukmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Dd]an\s+itu\s+berjalan\s+(?:seperti|kayak)\s+(?:ini|gini)", "Dan seperti ini");
            s = Regex.Replace(s, @"\b[Dd]an\s+begini\.\.\.", "Dan seperti ini...");
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
            const int sw = 48, sh = 24;
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
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 8) changed++;
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
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            using (ManualResetEventSlim ev = new ManualResetEventSlim(false))
            {
                try
                {
                    op.Completed = delegate { try { ev.Set(); } catch { } };
                    if (op.Status == AsyncStatus.Started) ev.Wait(2200);
                }
                catch
                {
                    int tries = 0;
                    while (op.Status == AsyncStatus.Started && tries < 2200) { Thread.Sleep(1); tries++; }
                }
            }
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

        public static byte[] CopyBitmapToTightBgra(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                int rowBytes = w * 4;
                byte[] dst = new byte[rowBytes * h];
                if (stride == rowBytes)
                {
                    Marshal.Copy(bd.Scan0, dst, 0, dst.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(bd.Scan0, y * stride), dst, y * rowBytes, rowBytes);
                }
                return dst;
            }
            finally { bmp.UnlockBits(bd); }
        }

        /// <summary>
        /// Detects when a subtitle is actively fading in/out across two consecutive frames:
        /// counts bright low-saturation text-candidate pixels; if they changed by > 22% across 60ms, hold 1 tick!
        /// </summary>
        public static bool IsMidFadeInTransition(byte[] prevBgra, byte[] curBgra, int w, int h)
        {
            if (prevBgra == null || curBgra == null || prevBgra.Length != curBgra.Length) return false;
            int step = Math.Max(1, (w * h) / 14000); // ultra-fast < 0.3ms sampling
            int prevBright = 0, curBright = 0, changedBright = 0;
            for (int i = 0; i < w * h; i += step)
            {
                int off = i << 2;
                byte pb = prevBgra[off], pg = prevBgra[off + 1], pr = prevBgra[off + 2];
                byte cb = curBgra[off], cg = curBgra[off + 1], cr = curBgra[off + 2];

                int pMin = pr < pg ? (pr < pb ? pr : pb) : (pg < pb ? pg : pb);
                int cMin = cr < cg ? (cr < cb ? cr : cb) : (cg < cb ? cg : cb);
                bool pIsTxt = (pMin >= 155);
                bool cIsTxt = (cMin >= 155);
                if (pIsTxt) prevBright++;
                if (cIsTxt) curBright++;
                if (pIsTxt != cIsTxt)
                {
                    int delta = Math.Abs(cr - pr) + Math.Abs(cg - pg) + Math.Abs(cb - pb);
                    if (delta >= 45 && delta <= 220) changedBright++;
                }
            }
            int maxB = Math.Max(prevBright, curBright);
            if (maxB < 12) return false;
            // Subtitle just appeared or is mid-fade-in (bright text pixel count surged > 24%)
            if (curBright > prevBright && (curBright - prevBright) * 100 / maxB >= 24) return true;
            return false;
        }

        /// <summary>
        /// 2-FRAME TEMPORAL BACKGROUND ERASER (~3ms O(W*H)):
        /// Static subtitle glyphs stay bright & identical across consecutive frames, while moving 3D video backgrounds
        /// (fire, sparks, camera pan, nebula, foliage) change color/position!
        /// Keeps 100% of static anti-aliased glyph strokes intact while pushing moving 3D background pixels down to dark matte!
        /// </summary>
        public static byte[] ApplyTemporalBackgroundEraser(byte[] prevBgra, byte[] curBgra, int w, int h, out bool hadMotion)
        {
            int total = w * h;
            byte[] dst = new byte[total * 4];
            int movingPixels = 0;

            for (int i = 0; i < total; i++)
            {
                int off = i << 2;
                byte pb = prevBgra[off], pg = prevBgra[off + 1], pr = prevBgra[off + 2];
                byte cb = curBgra[off], cg = curBgra[off + 1], cr = curBgra[off + 2];

                byte mb = pb < cb ? pb : cb;
                byte mg = pg < cg ? pg : cg;
                byte mr = pr < cr ? pr : cr;
                int diff = Math.Abs(cr - pr) + Math.Abs(cg - pg) + Math.Abs(cb - pb);
                if (diff > 54)
                {
                    movingPixels++;
                    // Pure per-channel temporal minimum naturally erases moving bright 3D sparks (230 vs 22 -> 22)
                    // while preserving 100% of anti-aliased thin white letter edges (242 vs 138 -> 138, NEVER crushed to black)!
                    // Only dampen dark/colored moving 3D background pixels where achromatic minC < 95.
                    byte minC = mr < mg ? (mr < mb ? mr : mb) : (mg < mb ? mg : mb);
                    if (minC < 95)
                    {
                        dst[off] = (byte)(mb >> 1);
                        dst[off + 1] = (byte)(mg >> 1);
                        dst[off + 2] = (byte)(mr >> 1);
                    }
                    else
                    {
                        dst[off] = mb;
                        dst[off + 1] = mg;
                        dst[off + 2] = mr;
                    }
                    dst[off + 3] = 255;
                }
                else
                {
                    dst[off] = mb;
                    dst[off + 1] = mg;
                    dst[off + 2] = mr;
                    dst[off + 3] = 255;
                }
            }
            hadMotion = (movingPixels > Math.Max(36, total / 400)); // >= 0.25% of ROI (even a single spark/wing overlapping 1 word!)
            return dst;
        }

        /// <summary>
        /// Ultra-fast (&lt; 0.5ms) 320x80 bright-stroke text mask for Pixel-Space Static Subtitle Lock &amp; Safe Temporal Guard.
        /// Marks ONLY crisp bright low-saturation glyph strokes that have local dark contrast (ignoring flat sky/glow/3D scenery).
        /// </summary>
        public static byte[] ComputeBrightTextMask(byte[] bgra, int w, int h, out int activePts)
        {
            const int MW = 320, MH = 80;
            byte[] mask = new byte[MW * MH];
            activePts = 0;
            if (bgra == null || w < 8 || h < 8 || bgra.Length < w * h * 4) return mask;

            int dx = Math.Max(2, Math.Min(5, w / 220));
            int dy = Math.Max(2, Math.Min(5, h / 55));

            for (int gy = 0; gy < MH; gy++)
            {
                int py = (gy * h + (MH >> 1)) / MH;
                if (py >= h) py = h - 1;
                int pyUp = py - dy; if (pyUp < 0) pyUp = 0;
                int pyDn = py + dy; if (pyDn >= h) pyDn = h - 1;

                int rowC = py * w;
                int rowU = pyUp * w;
                int rowD = pyDn * w;
                int mRow = gy * MW;

                for (int gx = 0; gx < MW; gx++)
                {
                    int px = (gx * w + (MW >> 1)) / MW;
                    if (px >= w) px = w - 1;

                    // Sample 2x2 micro-block (px, px+2) x (py, py+1) with ZERO heap allocations so delicate 1.5px thin fonts on wide 1758x319 ROIs NEVER fall between grid samples!
                    int bestLum = 0;
                    int px2 = (px + 2 < w) ? (px + 2) : px;
                    int py2 = (py + 1 < h) ? (py + 1) : py;
                    int rowC2 = py2 * w;
                    for (int si = 0; si < 4; si++)
                    {
                        int off = (si == 0) ? ((rowC + px) << 2)
                                : (si == 1) ? ((rowC + px2) << 2)
                                : (si == 2) ? ((rowC2 + px) << 2)
                                : ((rowC2 + px2) << 2);
                        byte b = bgra[off], g = bgra[off + 1], r = bgra[off + 2];
                        byte mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                        byte mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                        if (mn >= 142 && (mx - mn) <= 86)
                        {
                            int l = (77 * r + 150 * g + 29 * b) >> 8;
                            if (l > bestLum) bestLum = l;
                        }
                    }
                    if (bestLum == 0) continue;

                    int pxL = px - dx; if (pxL < 0) pxL = 0;
                    int pxR = px + dx; if (pxR >= w) pxR = w - 1;

                    int oL = (rowC + pxL) << 2;
                    int oR = (rowC + pxR) << 2;
                    int oU = (rowU + px) << 2;
                    int oD = (rowD + px) << 2;

                    int lumL = (77 * bgra[oL + 2] + 150 * bgra[oL + 1] + 29 * bgra[oL]) >> 8;
                    int lumR = (77 * bgra[oR + 2] + 150 * bgra[oR + 1] + 29 * bgra[oR]) >> 8;
                    int lumU = (77 * bgra[oU + 2] + 150 * bgra[oU + 1] + 29 * bgra[oU]) >> 8;
                    int lumD = (77 * bgra[oD + 2] + 150 * bgra[oD + 1] + 29 * bgra[oD]) >> 8;

                    int minNb = lumL < lumR ? lumL : lumR;
                    if (lumU < minNb) minNb = lumU;
                    if (lumD < minNb) minNb = lumD;

                    if (bestLum - minNb >= 36)
                    {
                        mask[mRow + gx] = 1;
                        activePts++;
                    }
                }
            }
            return mask;
        }

        public static int ComputeMaskIntersection(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;
            int inter = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != 0 && b[i] != 0) inter++;
            return inter;
        }

        public static byte[] IntersectMasks(byte[] a, byte[] b, out int pts)
        {
            pts = 0;
            if (a == null || b == null || a.Length != b.Length) return null;
            byte[] dst = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != 0 && b[i] != 0)
                {
                    dst[i] = 1;
                    pts++;
                }
            }
            return dst;
        }

        public static float ComputeMaskIoU(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0f;
            int inter = 0, uni = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool ba = a[i] != 0;
                bool bb = b[i] != 0;
                if (ba && bb) { inter++; uni++; }
                else if (ba || bb) uni++;
            }
            return uni == 0 ? 0f : (float)inter / uni;
        }

        /// <summary>
        /// Auto-crops huge 1440p/4K subtitle ROIs (e.g. 1758x319) down to the tight active subtitle band (+padding)
        /// in &lt; 1ms! Strips ~70% of 3D background clutter AND lets thin fonts get crisp 1.65x-2.0x super-resolution!
        /// </summary>
        public static bool DetectActiveTextBand(byte[] bgra, int w, int h, out Rectangle cropRect)
        {
            cropRect = new Rectangle(0, 0, w, h);
            if (bgra == null || w < 200 || h < 70) return false;

            int[] rowHits = new int[h];
            int[] colHits = new int[w];
            int totalHits = 0;
            int stepX = w > 1200 ? 2 : 1;
            int stepY = h > 220 ? 2 : 1;

            for (int y = 3; y < h - 3; y += stepY)
            {
                int row = y * w;
                int rowU = (y - 3) * w;
                int rowD = (y + 3) * w;
                for (int x = 3; x < w - 3; x += stepX)
                {
                    int off = (row + x) << 2;
                    byte b = bgra[off], g = bgra[off + 1], r = bgra[off + 2];
                    byte mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                    byte mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                    if (mn < 146 || (mx - mn) > 84) continue;

                    int lumC = (77 * r + 150 * g + 29 * b) >> 8;
                    int oL = (row + x - 3) << 2;
                    int oR = (row + x + 3) << 2;
                    int oU = (rowU + x) << 2;
                    int oD = (rowD + x) << 2;
                    int lumL = (77 * bgra[oL + 2] + 150 * bgra[oL + 1] + 29 * bgra[oL]) >> 8;
                    int lumR = (77 * bgra[oR + 2] + 150 * bgra[oR + 1] + 29 * bgra[oR]) >> 8;
                    int lumU = (77 * bgra[oU + 2] + 150 * bgra[oU + 1] + 29 * bgra[oU]) >> 8;
                    int lumD = (77 * bgra[oD + 2] + 150 * bgra[oD + 1] + 29 * bgra[oD]) >> 8;
                    int minNb = Math.Min(Math.Min(lumL, lumR), Math.Min(lumU, lumD));

                    if (lumC - minNb >= 40)
                    {
                        rowHits[y]++;
                        colHits[x]++;
                        totalHits++;
                    }
                }
            }

            if (totalHits < 28) return false;

            int minRowThresh = Math.Max(3, (w / stepX) / 160);
            int top = -1, bot = -1;
            for (int y = 0; y < h; y++)
            {
                if (rowHits[y] >= minRowThresh) { top = y; break; }
            }
            for (int y = h - 1; y >= 0; y--)
            {
                if (rowHits[y] >= minRowThresh) { bot = y; break; }
            }
            if (top < 0 || bot <= top + 8) return false;

            // Fixed low column threshold (never scale with full box height so single-line subtitles NEVER get clipped at ends!)
            const int minColThresh = 2;
            int left = -1, right = -1;
            for (int x = 0; x < w; x++)
            {
                if (colHits[x] >= minColThresh) { left = x; break; }
            }
            for (int x = w - 1; x >= 0; x--)
            {
                if (colHits[x] >= minColThresh) { right = x; break; }
            }
            if (left < 0 || right <= left + 40) return false;

            int padX = Math.Max(52, w / 14);
            int padY = Math.Max(32, h / 8);
            int rx = Math.Max(0, left - padX);
            int ry = Math.Max(0, top - padY);
            int rx2 = Math.Min(w, right + padX);
            int ry2 = Math.Min(h, bot + padY);
            int rw = rx2 - rx;
            int rh = ry2 - ry;

            // Only apply auto-crop if it meaningfully trims away 3D background clutter (< 84% of original area)
            if (rw >= 90 && rh >= 36 && ((long)rw * rh) < ((long)w * h * 84 / 100))
            {
                cropRect = new Rectangle(rx, ry, rw, rh);
                return true;
            }
            return false;
        }

        private static byte[] SliceTightBgra(byte[] src, int sw, Rectangle rect)
        {
            byte[] dst = new byte[rect.Width * rect.Height * 4];
            int rowBytes = rect.Width * 4;
            for (int y = 0; y < rect.Height; y++)
            {
                Buffer.BlockCopy(src, ((rect.Y + y) * sw + rect.X) * 4, dst, y * rowBytes, rowBytes);
            }
            return dst;
        }

        public string RecognizeBitmapWithTemporal(Bitmap sourceBmp, byte[] curTightBgra, byte[] prevTightBgra, string srcLang, bool enhanceOcr, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            return RecognizeBitmapWithTemporal(sourceBmp, curTightBgra, prevTightBgra, true, srcLang, enhanceOcr, out usedOnline, out detectedChoiceLayout, out error);
        }

        public string RecognizeBitmapWithTemporal(Bitmap sourceBmp, byte[] curTightBgra, byte[] prevTightBgra, bool allowTemporal, string srcLang, bool enhanceOcr, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            usedOnline = false;
            detectedChoiceLayout = false;
            error = "";
            if (sourceBmp == null) return "";

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

            try
            {
                int origW = sourceBmp.Width, origH = sourceBmp.Height;
                byte[] fullBgra = curTightBgra ?? CopyBitmapToTightBgra(sourceBmp);

                // AUTO-CROP ACTIVE SUBTITLE BAND for wide/tall ROIs (e.g. user's 1758x319 box!):
                // When 2 paired frames of the same subtitle exist, detect the active text band on the TEMPORAL-CLEANED frame
                // so moving 3D bright sparks/glare are already erased before computing the tight subtitle crop!
                int w = origW, h = origH;
                byte[] baseBgra = fullBgra;
                byte[] prevBgra = (allowTemporal && prevTightBgra != null && prevTightBgra.Length == fullBgra.Length) ? prevTightBgra : null;
                bool usedAutoCrop = false;

                if (enhanceOcr && (origW > 950 || origH > 180))
                {
                    Rectangle band = Rectangle.Empty;
                    bool gotBand = false;
                    if (prevBgra != null)
                    {
                        bool hadMotPre;
                        byte[] preTemp = ApplyTemporalBackgroundEraser(prevBgra, fullBgra, origW, origH, out hadMotPre);
                        if (hadMotPre) gotBand = DetectActiveTextBand(preTemp, origW, origH, out band);
                    }
                    if (!gotBand) gotBand = DetectActiveTextBand(fullBgra, origW, origH, out band);
                    if (gotBand)
                    {
                        baseBgra = SliceTightBgra(fullBgra, origW, band);
                        if (prevBgra != null) prevBgra = SliceTightBgra(prevBgra, origW, band);
                        w = band.Width;
                        h = band.Height;
                        usedAutoCrop = true;
                    }
                }

                string bestRaw;
                int bestScore;
                bool bestChoice;
                RunMultiPassWinRtOcr(eng, baseBgra, prevBgra, w, h, srcLang, enhanceOcr, out bestRaw, out bestScore, out bestChoice);

                // Safety fallback: if auto-crop missed a dark-on-light box or scored poorly (< 72), re-run once on full ROI!
                if (usedAutoCrop && (bestScore < 72 || string.IsNullOrWhiteSpace(bestRaw)))
                {
                    string fullRaw;
                    int fullScore;
                    bool fullChoice;
                    byte[] fullPrev = (allowTemporal && prevTightBgra != null && prevTightBgra.Length == fullBgra.Length) ? prevTightBgra : null;
                    RunMultiPassWinRtOcr(eng, fullBgra, fullPrev, origW, origH, srcLang, enhanceOcr, out fullRaw, out fullScore, out fullChoice);
                    if (fullScore > bestScore + 2 || (string.IsNullOrWhiteSpace(bestRaw) && !string.IsNullOrWhiteSpace(fullRaw)))
                    {
                        bestRaw = fullRaw;
                        bestChoice = fullChoice;
                    }
                }

                detectedChoiceLayout = bestChoice;
                return bestRaw;
            }
            catch (Exception ex)
            {
                error = "OCR error: " + ex.Message;
                return "";
            }
        }

        private static void RunMultiPassWinRtOcr(OcrEngine eng, byte[] baseBgra, byte[] prevBgra, int w, int h, string srcLang, bool enhanceOcr,
            out string bestRaw, out int bestScore, out bool bestChoice)
        {
            bestRaw = "";
            string bestClean = "";
            bestScore = 0;
            bestChoice = false;
            int srcStride = w * 4;

            // Optimal Super-Resolution scale factor:
            // Ensures delicate thin game fonts are crisp ~28-40px tall for WinRT OCR while keeping runtime under ~25-45ms!
            float scale = 1.0f;
            if (h < 150 && w * 2.0f <= 2200) scale = 2.0f;
            else if (h < 220 && w * 1.65f <= 2200) scale = 1.65f;
            else if (h < 300 && w * 1.35f <= 2200) scale = 1.35f;

            int nw = (scale > 1.01f) ? (int)(w * scale) : w;
            int nh = (scale > 1.01f) ? (int)(h * scale) : h;

            // PASS 0 (SAFE 2-FRAME TEMPORAL BACKGROUND ERASER — ONLY WHEN SAME SUBTITLE EXISTS IN BOTH FRAMES!):
            if (enhanceOcr && prevBgra != null && prevBgra.Length == baseBgra.Length)
            {
                bool hadMotion;
                byte[] tempClean = ApplyTemporalBackgroundEraser(prevBgra, baseBgra, w, h, out hadMotion);
                if (hadMotion)
                {
                    byte[] tempUp = (scale > 1.01f) ? ScaleAndProcessRawBgra(tempClean, w, h, srcStride, nw, nh, false) : tempClean;
                    int tpW, tpH;
                    byte[] tempPad = PadBgraBorder(tempUp, nw, nh, 16, out tpW, out tpH);
                    bool choiceT = false;
                    string rawT = RunWinRtOcrFromBuffer(eng, tempPad, tpW, tpH, srcLang, out choiceT);
                    string cleanT = choiceT ? NormalizeChoices(rawT, srcLang) : NormalizeText(rawT, srcLang, true);
                    int scoreT = ScoreOcrQuality(cleanT, srcLang);
                    if (!string.IsNullOrWhiteSpace(cleanT))
                    {
                        bestRaw = rawT; bestClean = cleanT; bestScore = scoreT; bestChoice = choiceT;
                    }
                    if (bestScore >= 96 && bestClean.Length >= 8) return;
                }
            }

            // Upscale current frame once for fallback passes
            byte[] upscaledRaw = (scale > 1.01f) ? ScaleAndProcessRawBgra(baseBgra, w, h, srcStride, nw, nh, false) : baseBgra;
            if (!enhanceOcr)
            {
                bool choiceRaw = false;
                bestRaw = RunWinRtOcrFromBuffer(eng, upscaledRaw, nw, nh, srcLang, out choiceRaw);
                bestChoice = choiceRaw;
                bestScore = ScoreOcrQuality(choiceRaw ? NormalizeChoices(bestRaw, srcLang) : NormalizeText(bestRaw, srcLang, true), srcLang);
                return;
            }

            // PASS 1 (RAW UPSCALED COLOR RGB + 16px PAD)
            int rawPadW, rawPadH;
            byte[] rawPadded = PadBgraBorder(upscaledRaw, nw, nh, 16, out rawPadW, out rawPadH);
            bool choice1 = false;
            string raw1 = RunWinRtOcrFromBuffer(eng, rawPadded, rawPadW, rawPadH, srcLang, out choice1);
            string clean1 = choice1 ? NormalizeChoices(raw1, srcLang) : NormalizeText(raw1, srcLang, true);
            int score1 = ScoreOcrQuality(clean1, srcLang);
            if (!string.IsNullOrWhiteSpace(clean1) &&
                (score1 > bestScore + 1 || (Math.Abs(score1 - bestScore) <= 2 && clean1.Length > bestClean.Length + 3)))
            {
                bestRaw = raw1; bestClean = clean1; bestScore = score1; bestChoice = choice1;
            }

            // PASS 2 (LOCAL HIGH-PASS BACKGROUND REMOVAL + FONT-PRESERVING EDGE SHARPEN — strips BOTH bright white/gray glare AND colored nebula!)
            // Always compare Pass 1 vs Pass 2 (~14ms) so glowing nebula/Navi glare NEVER silently drops leading words like "Farore, the "!
            {
                int fpW, fpH;
                byte[] fpBuf = ApplyFontPreservingEnhancer(upscaledRaw, nw, nh, 16, out fpW, out fpH);
                bool choice2 = false;
                string raw2 = RunWinRtOcrFromBuffer(eng, fpBuf, fpW, fpH, srcLang, out choice2);
                string clean2 = choice2 ? NormalizeChoices(raw2, srcLang) : NormalizeText(raw2, srcLang, true);
                int score2 = ScoreOcrQuality(clean2, srcLang);
                if (!string.IsNullOrWhiteSpace(clean2) &&
                    (score2 > bestScore + 1 || (Math.Abs(score2 - bestScore) <= 4 && clean2.Length > bestClean.Length + 3)))
                {
                    bestRaw = raw2; bestClean = clean2; bestScore = score2; bestChoice = choice2;
                }
            }

            // Detect bright/glowing/saturated 3D background (~0.15ms sampled): when present, Pass 1/2 can still drop words over intense glow
            // while scoring 100 on the surviving fragment — so we MUST also run Pass 3 (Dark-Outline Isolator) to compare completeness!
            bool hasBrightBg = false;
            {
                int totalPx = nw * nh;
                int stepBg = Math.Max(1, totalPx / 4000);
                int samples = 0, brightBgCnt = 0;
                for (int i = 0; i < totalPx; i += stepBg)
                {
                    int off = i << 2;
                    byte b = upscaledRaw[off], g = upscaledRaw[off + 1], r = upscaledRaw[off + 2];
                    int lum = (77 * r + 150 * g + 29 * b) >> 8;
                    byte mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                    byte mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                    if (lum >= 135 || (mx >= 150 && (mx - mn) >= 70)) brightBgCnt++;
                    samples++;
                }
                if (samples > 0 && brightBgCnt * 100 / samples >= 12)
                    hasBrightBg = true;
            }

            // PASS 3 (SOFT ANTI-ALIASED DARK-OUTLINE SUBTITLE ISOLATOR):
            // Runs when Pass 1 & 2 struggled (< 86), returned empty, OR ROI has bright 3D background glare that could hide words!
            if (bestScore < 86 || hasBrightBg || string.IsNullOrWhiteSpace(bestClean))
            {
                int padW, padH;
                byte[] isoBuf = ApplyDarkOutlineSubtitleIsolator(upscaledRaw, nw, nh, out padW, out padH);
                bool choiceIso = false;
                string rawIso = RunWinRtOcrFromBuffer(eng, isoBuf, padW, padH, srcLang, out choiceIso);
                string cleanIso = choiceIso ? NormalizeChoices(rawIso, srcLang) : NormalizeText(rawIso, srcLang, true);
                int scoreIso = ScoreOcrQuality(cleanIso, srcLang);
                if (!string.IsNullOrWhiteSpace(cleanIso) &&
                    (scoreIso > bestScore + 2 || (Math.Abs(scoreIso - bestScore) <= 4 && cleanIso.Length > bestClean.Length + 4)))
                {
                    bestRaw = rawIso; bestClean = cleanIso; bestScore = scoreIso; bestChoice = choiceIso;
                }
            }
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, bool enhanceOcr, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            return RecognizeBitmapWithTemporal(sourceBmp, null, null, false, srcLang, enhanceOcr, out usedOnline, out detectedChoiceLayout, out error);
        }

        private static byte[] ScaleAndProcessRawBgra(byte[] src, int sw, int sh, int srcStride, int dw, int dh, bool achromatic)
        {
            byte[] dst = new byte[dw * dh * 4];
            int xRatio = ((sw - 1) << 16) / Math.Max(1, dw);
            int yRatio = ((sh - 1) << 16) / Math.Max(1, dh);

            for (int y = 0; y < dh; y++)
            {
                int syFixed = y * yRatio;
                int sy = syFixed >> 16;
                int fy = (syFixed >> 8) & 0xFF;
                int ify = 256 - fy;
                int sy2 = (sy + 1 < sh) ? (sy + 1) : sy;
                int row0 = sy * srcStride;
                int row1 = sy2 * srcStride;
                int dstRow = y * dw * 4;

                for (int x = 0; x < dw; x++)
                {
                    int sxFixed = x * xRatio;
                    int sx = sxFixed >> 16;
                    int fx = (sxFixed >> 8) & 0xFF;
                    int ifx = 256 - fx;
                    int sx2 = (sx + 1 < sw) ? (sx + 1) : sx;

                    int off00 = row0 + (sx << 2);
                    int off01 = row0 + (sx2 << 2);
                    int off10 = row1 + (sx << 2);
                    int off11 = row1 + (sx2 << 2);

                    int w00 = ifx * ify;
                    int w01 = fx * ify;
                    int w10 = ifx * fy;
                    int w11 = fx * fy;

                    byte b = (byte)((src[off00] * w00 + src[off01] * w01 + src[off10] * w10 + src[off11] * w11) >> 16);
                    byte g = (byte)((src[off00 + 1] * w00 + src[off01 + 1] * w01 + src[off10 + 1] * w10 + src[off11 + 1] * w11) >> 16);
                    byte r = (byte)((src[off00 + 2] * w00 + src[off01 + 2] * w01 + src[off10 + 2] * w10 + src[off11 + 2] * w11) >> 16);

                    int dOff = dstRow + (x << 2);
                    if (achromatic)
                    {
                        byte minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
                        byte maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
                        int sat = maxC - minC;
                        int val = minC - ((sat * 3) >> 2);
                        byte v = (byte)(val > 0 ? val : 0);
                        dst[dOff] = v;
                        dst[dOff + 1] = v;
                        dst[dOff + 2] = v;
                        dst[dOff + 3] = 255;
                    }
                    else
                    {
                        dst[dOff] = b;
                        dst[dOff + 1] = g;
                        dst[dOff + 2] = r;
                        dst[dOff + 3] = 255;
                    }
                }
            }
            return dst;
        }

        /// <summary>
        /// Font-Preserving Local High-Pass Enhancer (~3ms O(W*H)):
        /// Keeps 100% of delicate anti-aliased font strokes completely connected (never splits 'k' into 'lc'),
        /// suppresses BOTH saturated 3D nebula (green/gold/blue) AND bright white/gray 3D background glare (Navi glow, sky, stone)
        /// via O(W*H) separable local illumination subtraction, sharpens thin strokes, and pads 16px for optimal WinRT OCR.
        /// </summary>
        public static byte[] ApplyFontPreservingEnhancer(byte[] srcBgra, int w, int h, int pad, out int outW, out int outH)
        {
            outW = w + pad * 2;
            outH = h + pad * 2;
            int total = w * h;
            byte[] gray = new byte[total];

            for (int i = 0; i < total; i++)
            {
                int off = i << 2;
                byte b = srcBgra[off];
                byte g = srcBgra[off + 1];
                byte r = srcBgra[off + 2];
                int y = (77 * r + 150 * g + 29 * b) >> 8;
                byte mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                byte mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                int sat = mx - mn;

                // Softly suppress saturated 3D nebula/effects (sat > 52) WITHOUT touching neutral white/gray font edges!
                int suppressed = y;
                if (sat > 52)
                {
                    suppressed = y - ((sat - 52) * 68 / 100);
                    if (suppressed < 0) suppressed = 0;
                }
                gray[i] = (byte)suppressed;
            }

            // Fast O(W*H) separable sliding-window local illumination estimate (radius r = 12..18px)
            // Cancels bright white/gray 3D backgrounds (Navi fairy glow, clouds, bright floors) while leaving thin letters razor sharp!
            int rBg = Math.Max(10, Math.Min(18, h / 10));
            int win = rBg * 2 + 1;
            byte[] blurH = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int sum = 0;
                for (int k = -rBg; k <= rBg; k++)
                {
                    int cx = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    sum += gray[row + cx];
                }
                for (int x = 0; x < w; x++)
                {
                    blurH[row + x] = (byte)(sum / win);
                    int xOut = x - rBg; if (xOut < 0) xOut = 0;
                    int xIn = x + rBg + 1; if (xIn >= w) xIn = w - 1;
                    sum += gray[row + xIn] - gray[row + xOut];
                }
            }

            byte[] hp = new byte[total];
            long sumHp = 0;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int k = -rBg; k <= rBg; k++)
                {
                    int cy = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    sum += blurH[cy * w + x];
                }
                for (int y = 0; y < h; y++)
                {
                    int idx = y * w + x;
                    int localBg = sum / win;
                    int gVal = gray[idx];
                    // Blend natural luminance with local high-pass boost: kills flat bright 3D patches while keeping full 0..255 AA!
                    int val = gVal + ((gVal - localBg) * 55 / 100) - (localBg > 135 ? ((localBg - 135) * 45 / 100) : 0);
                    byte bv = (byte)(val < 0 ? 0 : (val > 255 ? 255 : val));
                    hp[idx] = bv;
                    sumHp += bv;

                    int yOut = y - rBg; if (yOut < 0) yOut = 0;
                    int yIn = y + rBg + 1; if (yIn >= h) yIn = h - 1;
                    sum += blurH[yIn * w + x] - blurH[yOut * w + x];
                }
            }

            byte bgFill = (byte)(sumHp / Math.Max(1, total));
            byte[] dst = new byte[outW * outH * 4];
            for (int off = 0; off < dst.Length; off += 4)
            {
                dst[off] = bgFill; dst[off + 1] = bgFill; dst[off + 2] = bgFill; dst[off + 3] = 255;
            }

            // Fast 3x3 cross unsharp mask: sharpens thin strokes cleanly while preserving full 0..255 anti-aliasing!
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int rowUp = (y > 0 ? y - 1 : 0) * w;
                int rowDn = (y + 1 < h ? y + 1 : h - 1) * w;
                int dstRow = (y + pad) * outW;

                for (int x = 0; x < w; x++)
                {
                    int xm = x > 0 ? x - 1 : 0;
                    int xp = x + 1 < w ? x + 1 : w - 1;
                    int c = hp[row + x];
                    int avg = (hp[rowUp + x] + hp[rowDn + x] + hp[row + xm] + hp[row + xp]) >> 2;
                    int sharp = c + ((c - avg) >> 1);
                    byte v = (byte)(sharp < 0 ? 0 : (sharp > 255 ? 255 : sharp));

                    int dOff = (dstRow + x + pad) << 2;
                    dst[dOff] = v;
                    dst[dOff + 1] = v;
                    dst[dOff + 2] = v;
                }
            }
            return dst;
        }

        /// <summary>
        /// Zero-Delay Borderless Subtitle Enhancer:
        /// Fast O(W*H) separable 1D dark-halo dilation isolates light AND colored game subtitle glyphs
        /// that carry a dark outline/shadow (or sit on a dark box), while stripping bright 3D background glare.
        /// Also auto-detects light/parchment boxes with dark ink, and adds a clean 16px white border for WinRT OCR.
        /// </summary>
        public static byte[] ApplyDarkOutlineSubtitleIsolator(byte[] srcBgra, int w, int h, out int outW, out int outH)
        {
            const int pad = 16;
            outW = w + pad * 2;
            outH = h + pad * 2;
            int total = w * h;
            byte[] lum = new byte[total];
            byte[] sat = new byte[total];
            byte[] maxCh = new byte[total];
            byte[] minCh = new byte[total];
            byte[] isDark = new byte[total];

            long sumLum = 0;
            long sumSat = 0;
            long sumMinC = 0;
            int darkCount = 0;

            for (int i = 0; i < total; i++)
            {
                int off = i << 2;
                byte b = srcBgra[off];
                byte g = srcBgra[off + 1];
                byte r = srcBgra[off + 2];
                int y = (77 * r + 150 * g + 29 * b) >> 8;
                byte mx = r > g ? (r > b ? r : b) : (g > b ? g : b);
                byte mn = r < g ? (r < b ? r : b) : (g < b ? g : b);
                byte st = (byte)(mx - mn);
                lum[i] = (byte)y;
                maxCh[i] = mx;
                minCh[i] = mn;
                sat[i] = st;
                sumLum += y;
                sumSat += st;
                sumMinC += mn;
                if (y < 98)
                {
                    isDark[i] = 1;
                    darkCount++;
                }
            }

            // Detect genuine light/parchment dialog box with dark text (low saturation + high minC, NEVER bright colored 3D scenes!)
            int safeTotal = Math.Max(1, total);
            int avgLum = (int)(sumLum / safeTotal);
            int avgSat = (int)(sumSat / safeTotal);
            int avgMinC = (int)(sumMinC / safeTotal);
            bool lightBoxDarkText = (avgLum >= 182 && avgSat <= 48 && avgMinC >= 165 && darkCount > total / 250 && darkCount < total * 28 / 100);

            byte[] outBuf = new byte[outW * outH * 4];
            // Fill entire padded canvas with pure white (255,255,255,255) -> black ink on white paper is optimal for WinRT OCR!
            for (int i = 0; i < outBuf.Length; i++) outBuf[i] = 255;

            if (lightBoxDarkText)
            {
                for (int y = 0; y < h; y++)
                {
                    int rowIn = y * w;
                    int rowOut = (y + pad) * outW;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = lum[rowIn + x];
                        // Stretch dark ink sharply toward 0, push parchment background toward 255
                        int ink = (v - 75) * 255 / 85;
                        byte pv = (byte)(ink < 0 ? 0 : (ink > 255 ? 255 : ink));
                        int dOff = (rowOut + x + pad) << 2;
                        outBuf[dOff] = pv;
                        outBuf[dOff + 1] = pv;
                        outBuf[dOff + 2] = pv;
                    }
                }
                return outBuf;
            }

            // Separable 1D box-dilation of dark outline/shadow pixels (radius r = 4px) in O(W*H)!
            const int rHalo = 4;
            byte[] haloH = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int run = 0;
                for (int x = 0; x < Math.Min(w, rHalo + 1); x++) run += isDark[row + x];
                for (int x = 0; x < w; x++)
                {
                    if (run > 0) haloH[row + x] = 1;
                    int left = x - rHalo;
                    int right = x + rHalo + 1;
                    if (left >= 0) run -= isDark[row + left];
                    if (right < w) run += isDark[row + right];
                }
            }

            byte[] hasDarkHalo = new byte[total];
            for (int x = 0; x < w; x++)
            {
                int run = 0;
                for (int y = 0; y < Math.Min(h, rHalo + 1); y++) run += haloH[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    int idx = y * w + x;
                    if (run > 0) hasDarkHalo[idx] = 1;
                    int top = y - rHalo;
                    int bot = y + rHalo + 1;
                    if (top >= 0) run -= haloH[top * w + x];
                    if (bot < h) run += haloH[bot * w + x];
                }
            }

            bool darkDominantBg = (darkCount > total * 45 / 100);

            // Extract crisp glyph ink -> black on pure white!
            // 1) White/light borderless glyphs use minC (achromatic core), completely rejecting golden/blue/red 3D backgrounds!
            // 2) Colored RPG keywords are extracted when background is predominantly dark (prevents gluing kerning gaps on bright colored 3D scenes).
            for (int y = 0; y < h; y++)
            {
                int rowIn = y * w;
                int rowOut = (y + pad) * outW;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowIn + x;
                    byte l = lum[idx];
                    byte s = sat[idx];
                    byte mx = maxCh[idx];
                    byte mn = minCh[idx];
                    bool halo = hasDarkHalo[idx] != 0;

                    int inkStrength = 0;
                    if (halo)
                    {
                        // White / off-white borderless subtitle glyph hugged by dark outline/drop-shadow (soft ramp preserves thin strokes!)
                        if (mn >= 102 && s <= 78)
                        {
                            inkStrength = (mn - 96) * 255 / 115;
                        }
                        // Colored RPG keyword glyph on dark/shadowed background (never glues bright outdoor/golden kerning gaps!)
                        else if (darkDominantBg && mx >= 150 && s > 75 && l >= 85)
                        {
                            inkStrength = (mx - 135) * 255 / 95;
                        }
                    }
                    else if (mn >= 220 && s <= 35 && darkCount > total / 8)
                    {
                        // Thick pure white bold glyph interior on dark background
                        inkStrength = (mn - 205) * 255 / 50;
                    }

                    if (inkStrength > 0)
                    {
                        if (inkStrength > 255) inkStrength = 255;
                        byte pv = (byte)(255 - inkStrength);
                        int dOff = (rowOut + x + pad) << 2;
                        outBuf[dOff] = pv;
                        outBuf[dOff + 1] = pv;
                        outBuf[dOff + 2] = pv;
                    }
                }
            }

            return outBuf;
        }

        private static byte[] PadBgraBorder(byte[] srcBgra, int w, int h, int pad, out int outW, out int outH)
        {
            outW = w + pad * 2;
            outH = h + pad * 2;
            byte[] dst = new byte[outW * outH * 4];
            // Sample top-left pixel as natural border fill so we never introduce false high-contrast frame edges
            byte fb = srcBgra.Length >= 4 ? srcBgra[0] : (byte)0;
            byte fg = srcBgra.Length >= 4 ? srcBgra[1] : (byte)0;
            byte fr = srcBgra.Length >= 4 ? srcBgra[2] : (byte)0;
            for (int off = 0; off < dst.Length; off += 4)
            {
                dst[off] = fb; dst[off + 1] = fg; dst[off + 2] = fr; dst[off + 3] = 255;
            }
            int rowBytes = w * 4;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(srcBgra, y * rowBytes, dst, ((y + pad) * outW + pad) * 4, rowBytes);
            }
            return dst;
        }

        private static byte[] ApplySmoothAchromaticInPlaceOrCopy(byte[] srcBgra, int w, int h)
        {
            byte[] dst = new byte[srcBgra.Length];
            int len = w * h * 4;
            for (int off = 0; off < len; off += 4)
            {
                byte b = srcBgra[off], g = srcBgra[off + 1], r = srcBgra[off + 2];
                byte minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
                byte maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int sat = maxC - minC;
                int val = minC - ((sat * 3) >> 2);
                byte v = (byte)(val > 0 ? val : 0);
                dst[off] = v;
                dst[off + 1] = v;
                dst[off + 2] = v;
                dst[off + 3] = 255;
            }
            return dst;
        }

        private static byte[] ApplyHighContrastSubtitleCore(byte[] srcBgra, int w, int h)
        {
            byte[] dst = new byte[srcBgra.Length];
            int len = w * h * 4;
            for (int off = 0; off < len; off += 4)
            {
                byte b = srcBgra[off], g = srcBgra[off + 1], r = srcBgra[off + 2];
                byte minC = r < g ? (r < b ? r : b) : (g < b ? g : b);
                byte maxC = r > g ? (r > b ? r : b) : (g > b ? g : b);
                int sat = maxC - minC;
                // Heavily suppress colored 3D background pixels (sat * 1.5) and stretch luminance contrast around subtitle white/light-gray range (135..235)
                int achrom = minC - ((sat * 3) >> 1);
                int stretched = (achrom - 115) * 255 / 115;
                byte v = (byte)(stretched < 0 ? 0 : (stretched > 255 ? 255 : stretched));
                dst[off] = v;
                dst[off + 1] = v;
                dst[off + 2] = v;
                dst[off + 3] = 255;
            }
            return dst;
        }

        private static string RunWinRtOcrFromBuffer(OcrEngine eng, byte[] bgraBuf, int width, int height, string srcLang, out bool detectedChoiceLayout)
        {
            detectedChoiceLayout = false;
            if (bgraBuf == null || width <= 0 || height <= 0) return "";

            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            var ibuf = CryptographicBuffer.CreateFromByteArray(bgraBuf);
            using (SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(
                ibuf, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied))
            {
                OcrResult res = AwaitWinRt(eng.RecognizeAsync(sb));
                if (res == null || res.Lines == null || res.Lines.Count == 0) return "";

                List<string> validLines = new List<string>();
                int choiceMarkerLines = 0;

                foreach (OcrLine ln in res.Lines)
                {
                    if (ln.Words == null || ln.Words.Count == 0) continue;

                    int validWords = 0;
                    List<string> keptWords = new List<string>();

                    for (int wi = 0; wi < ln.Words.Count; wi++)
                    {
                        OcrWord ow = ln.Words[wi];
                        string wt = (ow.Text ?? "").Trim();
                        if (wt.Length == 0) continue;

                        bool wordValid = IsPlausibleOcrWord(wt, isCjk);
                        // Strip 1-2 char 3D-background margin ghosts at start/end of line
                        if (!wordValid && (wi == 0 || wi == ln.Words.Count - 1) && ln.Words.Count > 1 && wt.Length <= 2 &&
                            !Regex.IsMatch(wt, @"^[>▸►▶•\d\[\]\.\)\!\?""'“”]+$"))
                        {
                            continue;
                        }

                        keptWords.Add(wt);
                        if (wordValid) validWords++;
                    }

                    if (keptWords.Count == 0) continue;
                    string lineTxt = isCjk ? string.Join("", keptWords.ToArray()) : string.Join(" ", keptWords.ToArray());
                    bool isMarker = Regex.IsMatch(lineTxt, @"^(?:[>▸►▶•]|\[?\d+[\]\.\)])");

                    // Reject background 3D ghost lines that contain zero valid words
                    if (validWords == 0 && !isMarker) continue;

                    validLines.Add(lineTxt);
                    if (isMarker) choiceMarkerLines++;
                }

                if (validLines.Count == 0) return "";
                if (validLines.Count >= 2 && validLines.Count <= 5 && choiceMarkerLines >= 1)
                    detectedChoiceLayout = true;

                return string.Join("\n", validLines.ToArray());
            }
        }

        private static bool IsPlausibleOcrWord(string wt, bool isCjk)
        {
            if (string.IsNullOrEmpty(wt)) return false;
            if (isCjk) return true;
            string letters = Regex.Replace(wt, @"[^A-Za-z]", "");
            if (letters.Length >= 3 && Regex.IsMatch(letters, @"[aeiouyAEIOUY]")) return true;
            if (letters.Length == 2 && Regex.IsMatch(letters, @"^(?:no|go|ok|oh|ah|hi|up|on|in|it|he|we|so|do|be|me|my|by|or|if|at|as|us|to|an|am|is|of|mr|ms|dr|st)$", RegexOptions.IgnoreCase))
                return true;
            if (letters == "I" || letters == "a" || letters == "A") return true;
            if (Regex.IsMatch(wt, @"^(?:11|1001|100k|l00k|1ook|t0|g0|d0|n0|s0|y0u|f0r|\d+(?:st|nd|rd|th))$", RegexOptions.IgnoreCase))
                return true;
            return false;
        }

        public static int ScoreOcrQuality(string raw, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            if (s.Length < 2) return 0;

            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            int score = 100;

            int quoteCount = 0;
            foreach (char c in s)
            {
                if (c == '"') quoteCount++;
                if (c == '<' || c == '>' || c == '*' || c == '&' || c == '~' || c == '^' || c == '|' || c == '_' || c == '#' || c == '@' ||
                    c == '%' || c == '$' || c == '+' || c == '=' || c == '\\' || c == '/' || c == '{' || c == '}' || c == '•' || c == '„' || c == 'ß')
                    score -= 20;
                else if (!isCjk && c > 127 && c != '’' && c != '“' && c != '”' && c != '…' && c != '—' && c != '•')
                    score -= 16;
            }
            if ((quoteCount & 1) == 1) score -= 16; // Penalize unmatched stray double-quote artifacts (e.g. Whenever" wapt%talk)

            if (!isCjk)
            {
                // Penalize quote/bracket/symbol noise clusters & glued-dot 3D artifacts (e.g. "and•the", "prineess.tgc", "promisetONabooru.u", "effi'hTIet's")
                score -= Regex.Matches(s, @"[A-Za-z][""\(\)\[\]\{\}%$+=\\\/•„]+[A-Za-z\(\)\[\]]").Count * 24;
                score -= Regex.Matches(s, @"[a-z]\.[a-z]{1,}").Count * 22;
                score -= Regex.Matches(s, @"[a-z]'[A-Z]|[a-z]{2,}'[a-z]{3,}").Count * 24;
                score -= Regex.Matches(s, @"['""]{2,}|\(\)|\[\]|[:;]{2,}").Count * 18;
                // Penalize weird mid-word uppercase glitches from 3D noise (e.g. "mUst", "trickS", "sneakY", "promisetONabooru", "hTIet", "noOup")
                score -= Regex.Matches(s, @"[a-z][A-Z]{2,}").Count * 26;
                score -= Regex.Matches(s, @"\b[a-z]{2,}[A-Z][a-zA-Z]*\b").Count * 18;
                score -= Regex.Matches(s, @"\b[A-Z][a-z]+[A-Z][a-z]+\b").Count * 14;
                // Penalize broken impossible English word endings caused by 3D noise (e.g. "tricksw", "foolr", "tohancl")
                score -= Regex.Matches(s, @"\b[a-z]{3,}(?:sw|lr|cl|zr|qn|vx|xz|jw|kw|bf|df|gf|pf|tf|vt|zp)\b", RegexOptions.IgnoreCase).Count * 18;
                // Penalize glued mega-words & impossible 3D-noise consonant/bigram glitches (e.g. "kryown", "Icevto", "alononat", "kidzup", "haueltohelp")
                score -= Regex.Matches(s, @"\b[a-zA-Z]{16,}\b").Count * 22;
                score -= Regex.Matches(s, @"(?:kry|vto|zup|dzup|nonat|lwee|coul\b|gu\s+es|wontt)", RegexOptions.IgnoreCase).Count * 20;
                score -= Regex.Matches(s, @"[bcdfghjklmnpqrstvwxz]{5,}", RegexOptions.IgnoreCase).Count * 16;

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

                    // Penalize hyphenated single-letter OCR glitches (e.g. "Tree-i") and non-English 2-letter splits (e.g. "De cu")
                    if (Regex.IsMatch(trimmedW, @"^[A-Za-z]+-[A-Za-z]$"))
                        score -= 16;

                    string cleanW = Regex.Replace(trimmedW, @"[^A-Za-z]", "");
                    if (cleanW.Length == 0) continue;
                    if (cleanW.Length == 1 && cleanW != "I" && cleanW != "a" && cleanW != "A")
                        score -= 12;
                    else if (cleanW.Length == 2 && !Regex.IsMatch(cleanW, @"^(?:no|go|ok|oh|ah|hi|up|on|in|it|he|we|so|do|be|me|my|by|or|if|at|as|us|to|an|am|is|of|mr|ms|dr|st|tv|hp|mp|xp)$", RegexOptions.IgnoreCase))
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

            // Reject random mixed-case nebula/effect hallucinations (e.g. "uOHMA", "cneFG")
            if (Regex.IsMatch(s, @"^[a-z][A-Z]{3,}$")) return false;

            // Reject single-word random gibberish with no punctuation & suspicious consonant clusters (e.g. "Nisciorn")
            if (!s.Contains(" ") && s.Length >= 5 && !EndsWithTerminalPunctuation(s))
            {
                if (Regex.IsMatch(lettersOnly, @"(?:sci|xqz|qxz|zqj|rn$|iorm|iorn)$", RegexOptions.IgnoreCase) &&
                    !Regex.IsMatch(lettersOnly, @"^(?:return|modern|pattern|eastern|western|northern|southern|cavern|lantern|tavern|concern|discern|unicorn|acorn|scorn|thorn|sworn|worn|born|horn|corn|torn|morn|forlorn)$", RegexOptions.IgnoreCase))
                    return false;
            }

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

            // 0. Strip leading/trailing 3D-background noise ghosts (e.g. "fib Din,", ") Play this song...", "Hi,} Nabooru!", "saveßyrule")
            s = Regex.Replace(s, @"^(?:fib|flb|fih|tih|ltb|lfb|lhb|iib)\s+(?=[A-Z][a-z]{2,})", "");
            s = Regex.Replace(s, @"^[\)\}\]\•\„\—\s]+(?=[A-Za-z""'“])", "");
            s = Regex.Replace(s, @"(?<=[a-z])ß(?=[a-z])", " H");
            s = s.Replace("ß", "H").Replace("„", " ");
            s = Regex.Replace(s, @"(?<=[A-Za-z])•(?=[A-Za-z])", " ");
            s = Regex.Replace(s, @"[\{\}]", "");

            // 0b. Universal thin-font split-'k' & serif OCR repair (Delcu->Deku, talce->take, malce->make, Guardiar->Guardian, forese->forest)
            s = Regex.Replace(s, @"\bDelcu\b", "Deku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btalce\b", "take", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmalce\b", "make", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwalce\b", "wake", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbaclc\b", "back", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdarlc\b", "dark", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bspealc\b", "speak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbrealc\b", "break", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwealc\b", "weak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bloolc\b|\blooIc\b", "look", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcloclc\b", "clock", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbloclc\b", "block", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bGuardiar\b", "Guardian", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bforese\b", "forest", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\baland\b", "a land", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btyne\b", "the", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bfighling\b", "fighting", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhoyal\b", "Royal", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdidnlt\b", "didn't", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwontt\b", "won't", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\blhere\b", "there", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bprineess\b", "princess", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgoddes\b", "goddess", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btohancl\b", "to hand", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhaueltohelp\b", "have to help", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\boppesing\b", "opposing", RegexOptions.IgnoreCase);

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
                // Also fix curly/acute/backtick apostrophes (’, `, ´) so all contraction rules match reliably
                s = Regex.Replace(s, @"(?<=[A-Za-z0-9\|!])[’`´](?=[A-Za-z])", "'");
                // Fix OCR spaces around apostrophe in common contractions (e.g. "don 't" -> "don't", "I 'm" -> "I'm", "Saria 's" -> "Saria's")
                s = Regex.Replace(s, @"\b([A-Za-z]+)\s+'\s*(s|t|m|re|ve|ll|d)\b", "$1'$2", RegexOptions.IgnoreCase);

                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'m\b", "I'm");
                s = Regex.Replace(s, @"\b(?:1m|I'rn|Irn|11m|11'm)\b", "I'm");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ll\b", "I'll");
                s = Regex.Replace(s, @"\b(?:1ll|11ll|I1l|Il1)\b", "I'll");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ve\b", "I've");
                s = Regex.Replace(s, @"\b1ve\b", "I've");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'d\b", "I'd");
                s = Regex.Replace(s, @"\b([Yy]ou|[Ww]e|[Tt]hey)'11\b", "$1'll");

                // Fix common missing-apostrophe contractions from low-contrast fonts (avoiding real words like "ill", "well", "hell", "shell", "cant", "wont" handled safely)
                s = Regex.Replace(s, @"\bdont\b", "don't");
                s = Regex.Replace(s, @"\bDont\b", "Don't");
                s = Regex.Replace(s, @"\bdidnt\b", "didn't");
                s = Regex.Replace(s, @"\bDidnt\b", "Didn't");
                s = Regex.Replace(s, @"\bdoesnt\b", "doesn't");
                s = Regex.Replace(s, @"\bDoesnt\b", "Doesn't");
                s = Regex.Replace(s, @"\bisnt\b", "isn't");
                s = Regex.Replace(s, @"\bIsnt\b", "Isn't");
                s = Regex.Replace(s, @"\barent\b", "aren't");
                s = Regex.Replace(s, @"\bArent\b", "Aren't");
                s = Regex.Replace(s, @"\bwasnt\b", "wasn't");
                s = Regex.Replace(s, @"\bWasnt\b", "Wasn't");
                s = Regex.Replace(s, @"\bwerent\b", "weren't");
                s = Regex.Replace(s, @"\bWerent\b", "Weren't");
                s = Regex.Replace(s, @"\bwouldnt\b", "wouldn't");
                s = Regex.Replace(s, @"\bWouldnt\b", "Wouldn't");
                s = Regex.Replace(s, @"\bcouldnt\b", "couldn't");
                s = Regex.Replace(s, @"\bCouldnt\b", "Couldn't");
                s = Regex.Replace(s, @"\bshouldnt\b", "shouldn't");
                s = Regex.Replace(s, @"\bShouldnt\b", "Shouldn't");
                s = Regex.Replace(s, @"\bhavent\b", "haven't");
                s = Regex.Replace(s, @"\bHavent\b", "Haven't");
                s = Regex.Replace(s, @"\bhasnt\b", "hasn't");
                s = Regex.Replace(s, @"\bHasnt\b", "Hasn't");
                s = Regex.Replace(s, @"\bhadnt\b", "hadn't");
                s = Regex.Replace(s, @"\bHadnt\b", "Hadn't");
                s = Regex.Replace(s, @"\bwont\b", "won't");
                s = Regex.Replace(s, @"\bWont\b", "Won't");
                s = Regex.Replace(s, @"\bcant\b", "can't");
                s = Regex.Replace(s, @"\bCant\b", "Can't");
                s = Regex.Replace(s, @"\bIm\b", "I'm");
                s = Regex.Replace(s, @"\bIve\b", "I've");
                s = Regex.Replace(s, @"\byoure\b", "you're");
                s = Regex.Replace(s, @"\bYoure\b", "You're");
                s = Regex.Replace(s, @"\btheyre\b", "they're");
                s = Regex.Replace(s, @"\bTheyre\b", "They're");
                s = Regex.Replace(s, @"\bthats\b", "that's");
                s = Regex.Replace(s, @"\bThats\b", "That's");
                s = Regex.Replace(s, @"\bwhats\b", "what's");
                s = Regex.Replace(s, @"\bWhats\b", "What's");
                s = Regex.Replace(s, @"\bheres\b", "here's");
                s = Regex.Replace(s, @"\bHeres\b", "Here's");
                s = Regex.Replace(s, @"\btheres\b", "there's");
                s = Regex.Replace(s, @"\bTheres\b", "There's");

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

                // 4g. Common Font Ligatures & Small-Font Misreads: vv -> w, rn -> m, % -> to, " -> you
                s = Regex.Replace(s, @"\b([Ww]henever|[Ii]f|[Ww]hen|[Ww]herever|[Ww]hatever)\s*[""“”']+\s*(?=(?:wapt|wanr|vvant|waht|want|need|like|can|could|are|were|have|had|see|go|come|get|find|hear|feel|think|know|play|talk|call|speak|ask|tell|try|use|make|take|give|wish|choose|decide)\b)", "$1 you ");
                s = Regex.Replace(s, @"\b(?:wapt|wanr|vvant|waht)\b", "want");
                s = Regex.Replace(s, @"\b(?:Wapt|Wanr|VVant|Waht)\b", "Want");
                // In small fonts, "to" (t + o) is frequently misread as "%" (o/o) between words (e.g. "wapt%talk" -> "want to talk")
                s = Regex.Replace(s, @"(?<=\b(?:want|wants|wanted|going|try|trying|tried|need|needs|needed|have|has|had|used|ready|able|unable|time|way|how|what|where|who|nothing|something|anything|someone|anyone|place|like|likes|liked|love|loves|loved|start|started|begin|began|continue|continued|decide|decided|promise|promised|refuse|refused|forget|forgot|remember|remembered|learn|learned|teach|taught|help|helped|come|came|go|went|listen|talk|speak|say|tell|ask|call|play|move|return|get|got|make|made))\s*%\s*(?=[A-Za-z])", " to ", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"(?<=[A-Za-z])\s*%\s*(?=(?:talk|speak|say|tell|ask|see|look|hear|listen|go|come|get|make|take|give|find|know|think|feel|play|call|use|try|help|save|protect|fight|defeat|open|close|enter|leave|return|stay|live|die|be|do|have|meet|catch|follow|run|walk|stop|start|begin|finish|end|win|lose|buy|sell|pay|eat|drink|sleep|wake|read|write|learn|understand|believe|remember|forget|me|you|him|her|us|them|it|this|that|here|there|the|a|an|my|your|his|our|their)\b)", " to ", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"(?<=[A-Za-z])%(?=[A-Za-z])", " to ");

                // Strip unmatched stray single double-quote '"' attached to a word in the middle of a sentence
                int qCnt = 0;
                foreach (char qc in s) if (qc == '"') qCnt++;
                if ((qCnt & 1) == 1)
                {
                    s = Regex.Replace(s, @"(?<=[A-Za-z])""(?=\s+[a-z])", "");
                }

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

        private static string HttpRequestFast(string url, string postBody = null, string contentType = "application/x-www-form-urlencoded", int timeoutMs = 2200)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            // Respect Windows system proxy/VPN (critical so Google Translate answers in ~40-90ms instead of timing out!)
            req.KeepAlive = true;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            try
            {
                req.ServicePoint.UseNagleAlgorithm = false;
                req.ServicePoint.Expect100Continue = false;
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
            return NormalizeChoices(raw, "auto");
        }

        public static string NormalizeChoices(string raw, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string[] lines = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            List<string> cleaned = new List<string>();
            foreach (string ln in lines)
            {
                string s = CleanGameOcrText(ln, string.IsNullOrEmpty(srcLang) ? "auto" : srcLang);
                s = Regex.Replace(s, @"^(?:[>▸►▶•·\-\*]|\[?\d+[\]\.\)])\s*", "");
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
            return TranslateChoicesLineByLine(multilineChoices, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone ? 1 : 2, out usedProvider, out error, glossary, "", "", "");
        }

        public string TranslateChoicesLineByLine(string multilineChoices, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, int toneMode, out string usedProvider, out string error,
            string glossary = "", string gameTitle = "", string gameLore = "", string storyContext = "")
        {
            usedProvider = preferredEngine == "mymemory" ? "MyMemory" : (preferredEngine == "gemini" ? "Gemini AI" : "Google");
            error = "";
            if (string.IsNullOrWhiteSpace(multilineChoices)) return "";

            string[] lines = multilineChoices.Replace("\r\n", "\n").Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 1)
            {
                string trSingle = TranslateText(lines[0].Trim(), srcLang, tgtLang, preferredEngine, geminiKey, toneMode, out usedProvider, out error, glossary, gameTitle, gameLore, storyContext);
                return string.IsNullOrEmpty(trSingle) ? "" : ("• " + trSingle.Trim());
            }

            string batchResult = TranslateText(multilineChoices, srcLang, tgtLang, preferredEngine, geminiKey, toneMode, out usedProvider, out error, glossary, gameTitle, gameLore, storyContext);
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
                string tr = TranslateText(trimmed, srcLang, tgtLang, preferredEngine, geminiKey, toneMode, out usedProvider, out error, glossary, gameTitle, gameLore, storyContext);
                if (!string.IsNullOrEmpty(tr)) results.Add("• " + tr.Trim());
            }
            return string.Join("\n", results.ToArray());
        }

        public string TranslateText(string text, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, bool naturalTone, out string usedProvider, out string error, string glossary = "")
        {
            return TranslateText(text, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone ? 1 : 2, out usedProvider, out error, glossary, "", "", "");
        }

        public string TranslateText(string text, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, int toneMode, out string usedProvider, out string error,
            string glossary = "", string gameTitle = "", string gameLore = "", string storyContext = "")
        {
            usedProvider = preferredEngine == "mymemory" ? "MyMemory" : (preferredEngine == "gemini" ? "Gemini AI" : "Google");
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return "";

            bool naturalFlag = (toneMode != 2);
            string coreKey = NormalizeCoreText(text);
            string cacheKey = preferredEngine + "|T" + toneMode + "|" + srcLang + "|" + tgtLang + "|" + (gameTitle ?? "") + "|" + (glossary ?? "") + "|" + (string.IsNullOrEmpty(coreKey) ? text.Trim() : coreKey);
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
            string prepped = PreProcessGameIdioms(protectedText, srcLang, tgtLang, naturalFlag);
            string result = "";

            if (preferredEngine == "gemini" && !string.IsNullOrWhiteSpace(geminiKey))
            {
                result = CallGemini(prepped, tgtLang, geminiKey, toneMode, glossary, gameTitle, gameLore, storyContext, out error);
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
                result = NaturalizeIndonesian(result, tgtLang, toneMode);
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

        private static string CallGemini(string text, string tgtLang, string apiKey, int toneMode, string glossary, string gameTitle, string gameLore, string storyContext, out string error)
        {
            error = "";
            try
            {
                string langName = tgtLang == "id" ? "Indonesian" : (tgtLang == "ms" ? "Malay" : tgtLang);
                string style;
                if (toneMode == 0 && tgtLang == "id")
                    style = "bahasa gaul Indonesia sehari-hari yang natural dan ekspresif untuk gamer (pakai gue/lo, gak, udah, banget, kayak, beneran; JANGAN pakai bahasa baku seperti Anda/saya/mengapa/apakah/tidak)";
                else if (toneMode == 1 && tgtLang == "id")
                    style = "polite everyday conversational RPG dialogue (natural, friendly, use aku/kamu, avoid stiff formal words like Anda/saya/mengapa/apakah)";
                else
                    style = "accurate natural translation";

                StringBuilder p = new StringBuilder();
                p.Append("Translate this game subtitle into ").Append(langName).Append(" using ").Append(style).Append(".");
                if (!string.IsNullOrWhiteSpace(gameTitle))
                    p.Append(" Game: ").Append(gameTitle).Append(".");
                if (!string.IsNullOrWhiteSpace(gameLore))
                {
                    string shortLore = gameLore.Length > 360 ? gameLore.Substring(0, 360) : gameLore;
                    p.Append(" Game lore context: ").Append(shortLore.Replace("\r", " ").Replace("\n", " ")).Append(".");
                }
                if (!string.IsNullOrWhiteSpace(glossary))
                    p.Append(" Keep these exact game names/terms: ").Append(glossary.Replace("\r", "").Replace("\n", "; ")).Append(".");
                if (!string.IsNullOrWhiteSpace(storyContext))
                    p.Append("\nRecent dialogue for continuity:\n").Append(storyContext);
                p.Append("\nReturn ONLY the translated text, preserving line breaks:\n").Append(text);

                string escaped = p.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
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
            "https://clients5.google.com/translate_a/t",
            "https://translate.google.com/translate_a/single"
        };
        private static readonly DateTime[] _hostCooldownUntil = new DateTime[3];

        private static string CallGoogleGtx(string text, string srcLang, string tgtLang, out string error)
        {
            error = "";
            string sl = string.IsNullOrEmpty(srcLang) ? "auto" : srcLang;
            string tl = string.IsNullOrEmpty(tgtLang) ? "id" : tgtLang;

            Exception lastEx = null;
            int start = _bestGtxHostIdx;
            DateTime now = DateTime.UtcNow;

            for (int k = 0; k < GoogleGtxHosts.Length; k++)
            {
                int idx = (start + k) % GoogleGtxHosts.Length;
                // Circuit breaker: skip rate-limited / recently-failed host immediately (0ms wasted!)
                if (_hostCooldownUntil[idx] > now) continue;

                try
                {
                    string url;
                    if (idx == 1)
                    {
                        // Fast Chrome Dictionary endpoint (separate quota pool, ultra-fast JSON response!)
                        url = GoogleGtxHosts[idx] + "?client=dict-chrome-ex&sl=" + Uri.EscapeDataString(sl) +
                              "&tl=" + Uri.EscapeDataString(tl) + "&q=" + Uri.EscapeDataString(text);
                    }
                    else
                    {
                        url = GoogleGtxHosts[idx] + "?client=gtx&sl=" + Uri.EscapeDataString(sl) +
                              "&tl=" + Uri.EscapeDataString(tl) + "&dt=t&q=" + Uri.EscapeDataString(text);
                    }

                    string json = HttpRequestFast(url, null, "application/x-www-form-urlencoded", 1200);
                    string parsed = (idx == 1) ? ParseClients5Json(json) : ParseGoogleGtxJson(json);
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        _bestGtxHostIdx = idx;
                        error = "";
                        return parsed;
                    }
                    _hostCooldownUntil[idx] = DateTime.UtcNow.AddSeconds(20);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    bool isRateLimit = (ex.Message != null && (ex.Message.Contains("429") || ex.Message.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (isRateLimit)
                    {
                        // Google 429 is IP-wide across all 3 hosts -> cooldown ALL hosts for 120s immediately!
                        // Saves ~1.8 seconds of futile sequential 429 retries and falls back instantly in 0ms!
                        DateTime coolAll = DateTime.UtcNow.AddSeconds(120);
                        for (int hIdx = 0; hIdx < _hostCooldownUntil.Length; hIdx++)
                            _hostCooldownUntil[hIdx] = coolAll;
                        break;
                    }
                    _hostCooldownUntil[idx] = DateTime.UtcNow.AddSeconds(25);
                }
            }
            if (lastEx != null) error = "Google error: " + lastEx.Message;
            return "";
        }

        private static string ParseClients5Json(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string t = json.Trim();
            // Format A: ["translated text"] or [["translated text","en"]]
            if (t.StartsWith("[["))
            {
                int q1 = t.IndexOf('"');
                if (q1 >= 0) return ReadJsonString(t, q1);
            }
            else if (t.StartsWith("["))
            {
                int q1 = t.IndexOf('"');
                if (q1 >= 0) return ReadJsonString(t, q1);
            }
            return ParseGoogleGtxJson(json);
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
