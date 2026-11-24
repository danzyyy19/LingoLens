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

    public partial class TranslationEngine : IDisposable
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

                // FADE-IN / BRAND-NEW SUBTITLE SETTLE GUARD (1 fast 40ms tick):
                // If bright text strokes just surged (mid-fade-in) OR a brand-new subtitle just replaced the old one,
                // hold ONE 40ms tick so fade-in settles to 100% opacity AND pairs 2 clean frames of the NEW subtitle for Temporal Eraser!
                int fadeTicks = 0;
                _fadeHoldTicksByArea.TryGetValue(areaId, out fadeTicks);
                bool maskJustSurged = (curTextPts >= 14 && (prevPts < 8 || (curTextPts > prevPts && (curTextPts - prevPts) * 100 / curTextPts >= 32)));
                bool brandNewSubSwitched = (curTextPts >= 14 && prevInter < 24);
                if (!forced && enhanceOcr && hasPairedPrev && fadeTicks < 1 &&
                    (lockedSubJustChanged || maskJustSurged || brandNewSubSwitched || IsMidFadeInTransition(prevBgra, curBgra, fw, fh)))
                {
                    if (lockedSubJustChanged)
                    {
                        // Unlock pixel mask so the very next paired frame (40ms later) runs Temporal Eraser on the new subtitle!
                        _lockedTextMaskByArea.Remove(areaId);
                        _lockedTextPtsByArea.Remove(areaId);
                    }
                    _fadeHoldTicksByArea[areaId] = fadeTicks + 1;
                    _fastPollMs = 40;
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
                    // Require 4 consecutive empty frames before wiping an active locked subtitle!
                    // Prevents a bright camera flash / white background transition from breaking Anti-Flip-Flop Lock!
                    if (!string.IsNullOrEmpty(prevTrans))
                    {
                        if (!forced && emptyStreak < 4)
                        {
                            _fastPollMs = 45;
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
                    _fastPollMs = 40;
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
                    // One-Way Quality Ratchet: NEVER downgrade or flip-flop an already-displayed locked subtitle!
                    // Once a clean translation is shown (prevOcrScore >= 78), keep it 100% FIXED even if background changes!
                    bool prevIncomplete = !instantSub && !EndsWithTerminalPunctuation(prevOcr) && EndsWithTerminalPunctuation(cleanText);
                    bool majorRepair = (prevOcrScore < 76 && newOcrScore >= prevOcrScore + 14 && coreNew.Length >= corePrev.Length);
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

            // 2-FRAME NEW-SUBTITLE CONSENSUS GATE (Fixes BUG 3 + Anti-Flip-Flop on Background Change!):
            // Pristine brand-new reads (score >= 92, len >= 6) when NO subtitle is currently locked translate with ZERO extra delay.
            // When REPLACING an existing locked subtitle OR when score < 92, require 2-frame consensus (~40ms apart)
            // so a transient background change / partial occlusion NEVER causes the displayed translation to flip-flop!
            bool replacingLockedSub = !string.IsNullOrEmpty(prevTrans) && !string.IsNullOrEmpty(prevOcr);
            if (!forced && !treatAsChoice && (newOcrScore < 92 || replacingLockedSub))
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
                    _fastPollMs = 40;
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

            // If instant mode and new reading strictly extends the previous prefix by >= 1 full word (+5 chars),
            // auto-detect typewriter / multi-part subtitle append so Visual Novels never get stuck on half a sentence!
            if (instantSub && newToks.Length >= prevToks.Length + 1 &&
                coreNew.Length >= corePrev.Length + 5 &&
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
            // Also lock partial/occluded subsets of the same subtitle when background changes obscure several words!
            if (instantSub)
            {
                bool isPartialSubset = (newToks.Length <= prevToks.Length && minToks >= 2 && containment >= 0.62f);
                bool isSubstringOverlap = (corePrev.Length >= 12 && coreNew.Length >= 6 &&
                    (corePrev.Contains(coreNew) || (coreNew.Length < corePrev.Length && triDice >= 0.45f && containment >= 0.55f)));
                if (jaccard >= 0.48f || charSim >= 0.66f || triDice >= 0.50f || isPartialSubset || isSubstringOverlap ||
                    (minToks >= 2 && containment >= 0.68f && Math.Abs(prevToks.Length - newToks.Length) <= 3))
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
    }
}
