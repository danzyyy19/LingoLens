using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
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
        private readonly Dictionary<int, string> _lastTransByArea = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _lastActiveUtc = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, bool> _idleHidden = new Dictionary<int, bool>();
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
                ServicePointManager.DefaultConnectionLimit = 12;
                WebRequest.DefaultWebProxy = null;
            }
            catch { }

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
                _lastTransByArea.Remove(areaId);
                _lastActiveUtc.Remove(areaId);
                _idleHidden.Remove(areaId);
            }
        }

        public void ClearState()
        {
            lock (_cacheLock)
            {
                _prevSigs.Clear();
                _lastOcrByArea.Clear();
                _lastTransByArea.Clear();
                _lastActiveUtc.Clear();
                _idleHidden.Clear();
            }
        }

        public void Dispose()
        {
            _stop = true;
            _running = false;
            try { if (_worker != null && _worker.IsAlive) _worker.Join(800); } catch { }
        }

        private void RaiseResult(int areaId, string src, string trans, int ms, string status, bool err, bool idleCleared = false)
        {
            EventHandler<ScanUpdateEventArgs> h = OnAreaTranslated;
            if (h != null)
            {
                h(this, new ScanUpdateEventArgs
                {
                    AreaId = areaId,
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
            bool lastMerge = true;
            bool lastNat = true;

            while (!_stop)
            {
                if (!_running || _pausedForEdit) { Thread.Sleep(50); continue; }

                List<Area> snapshot = new List<Area>();
                string srcLang, tgtLang, engineName, geminiKey;
                int intervalMs, autoHideSec;
                bool waitStable, mergeLines, naturalTone;

                lock (_cfg.Sync)
                {
                    srcLang = _cfg.Src;
                    tgtLang = _cfg.Tgt;
                    engineName = _cfg.Engine;
                    geminiKey = _cfg.GeminiApiKey;
                    naturalTone = _cfg.NaturalTone;
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
                                     (engineName != lastEngine) || (mergeLines != lastMerge) || (naturalTone != lastNat);
                if (configChanged)
                {
                    ClearState();
                    lastSrc = srcLang; lastTgt = tgtLang; lastEngine = engineName; lastMerge = mergeLines; lastNat = naturalTone;
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

                    ProcessArea(area, srcLang, tgtLang, engineName, geminiKey, naturalTone, waitStable, mergeLines, autoHideSec, forced);
                }

                int slept = 0;
                while (slept < intervalMs && !_stop && _running && !_forceScan && !_pausedForEdit)
                {
                    Thread.Sleep(35);
                    slept += 35;
                }
            }
        }

        private void ProcessArea(Area area, string srcLang, string tgtLang, string engineName, string geminiKey,
            bool naturalTone, bool waitStable, bool mergeLines, int autoHideSec, bool forced)
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
                    if (diff < 1.2f)
                    {
                        CheckIdleTimeout(area.Id, autoHideSec);
                        return;
                    }
                }

                if (waitStable && !forced)
                {
                    Thread.Sleep(90);
                    if (_stop || !_running || _pausedForEdit) return;
                    Bitmap second = CaptureRect(area.Ocr);
                    if (second != null)
                    {
                        byte[] sig2 = ComputeSignature(second);
                        if (ComputeDiffPercent(sig, sig2) > 4.5f)
                        {
                            _prevSigs[area.Id] = sig2;
                            second.Dispose();
                            return;
                        }
                        frame.Dispose();
                        frame = second;
                        sig = sig2;
                    }
                }

                _prevSigs[area.Id] = sig;

                bool usedOnline, detectedChoiceLayout;
                string ocrError;
                string rawText = RecognizeBitmap(frame, srcLang, out usedOnline, out detectedChoiceLayout, out ocrError);

                if (!string.IsNullOrEmpty(ocrError))
                {
                    RaiseResult(area.Id, "", "", (int)sw.ElapsedMilliseconds, ocrError, true);
                    return;
                }

                bool treatAsChoice = (area.Mode == AreaTextMode.Choice) || detectedChoiceLayout;
                string cleanText = treatAsChoice
                    ? NormalizeChoices(rawText)
                    : NormalizeText(rawText, srcLang, mergeLines);

                if (string.IsNullOrEmpty(cleanText))
                {
                    string oldT;
                    if (_lastTransByArea.TryGetValue(area.Id, out oldT) && !string.IsNullOrEmpty(oldT))
                    {
                        _lastOcrByArea[area.Id] = "";
                        _lastTransByArea[area.Id] = "";
                        _idleHidden[area.Id] = true;
                        RaiseResult(area.Id, "", "", (int)sw.ElapsedMilliseconds, "Kosong", false);
                    }
                    return;
                }

                string prevOcr, prevTrans;
                _lastOcrByArea.TryGetValue(area.Id, out prevOcr);
                _lastTransByArea.TryGetValue(area.Id, out prevTrans);

                if (!forced && !string.IsNullOrEmpty(prevOcr) && !string.IsNullOrEmpty(prevTrans))
                {
                    if (ComputeSimilarity(cleanText, prevOcr) >= 0.94f)
                    {
                        CheckIdleTimeout(area.Id, autoHideSec);
                        return;
                    }
                }

                string transErr, usedProvider, translated;
                if (treatAsChoice)
                {
                    translated = TranslateChoicesLineByLine(cleanText, srcLang, tgtLang, engineName, geminiKey, naturalTone, out usedProvider, out transErr);
                }
                else
                {
                    translated = TranslateText(cleanText, srcLang, tgtLang, engineName, geminiKey, naturalTone, out usedProvider, out transErr);
                }
                sw.Stop();

                if (!string.IsNullOrEmpty(transErr))
                {
                    RaiseResult(area.Id, cleanText, "", (int)sw.ElapsedMilliseconds, transErr, true);
                    return;
                }

                _lastOcrByArea[area.Id] = cleanText;
                _lastTransByArea[area.Id] = translated;
                _lastActiveUtc[area.Id] = DateTime.UtcNow;
                _idleHidden[area.Id] = false;

                string tag = (treatAsChoice ? "Pilihan · " : "") + usedProvider + " · " + sw.ElapsedMilliseconds + " ms";
                RaiseResult(area.Id, cleanText, translated, (int)sw.ElapsedMilliseconds, tag, false);
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
        // NATURAL GAME DIALOGUE & IDIOM ENGINE (Fixes "Catch me up" -> "Susul aku", "Anda" -> "kamu")
        // =====================================================================
        private static string PreProcessGameIdioms(string text, string srcLang, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id") return text;
            if (srcLang != "en" && srcLang != "auto") return text;

            string s = text;
            // Fix common English game dialogue idioms that machine translators butcher literally:
            s = Regex.Replace(s, @"\b[Cc]atch me up\b", "follow and meet me");
            s = Regex.Replace(s, @"\b[Cc]atch up with me\b", "follow and meet me");
            s = Regex.Replace(s, @"\b[Cc]atch up later\b", "meet up later");
            s = Regex.Replace(s, @"\b[Hh]ang in there\b", "stay strong");
            s = Regex.Replace(s, @"\b[Nn]ever mind\b", "forget it");
            s = Regex.Replace(s, @"\b[Yy]ou bet\b", "of course");
            s = Regex.Replace(s, @"\b[Mm]ake up your mind\b", "decide quickly");
            s = Regex.Replace(s, @"\b[Gg]ive me a break\b", "spare me");
            return s;
        }

        public static string NaturalizeIndonesian(string translated, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id" || string.IsNullOrEmpty(translated)) return translated;

            string s = translated;

            // Fix literal idiom leftovers if any slipped through
            s = Regex.Replace(s, @"\b[Tt]angkap aku nanti\b", "Susul aku nanti");
            s = Regex.Replace(s, @"\b[Tt]angkap saya nanti\b", "Susul aku nanti");
            s = Regex.Replace(s, @"\b[Mm]engejarku nanti\b", "menyusulku nanti");
            s = Regex.Replace(s, @"\bikuti dan temui aku\b", "susul aku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bikuti dan temui saya\b", "susul aku", RegexOptions.IgnoreCase);

            // Natural conversational pronouns for RPG / Adventure / VN games (instead of stiff "Anda / Saya")
            s = Regex.Replace(s, @"\bAnda\b", "Kamu");
            s = Regex.Replace(s, @"\banda\b", "kamu");
            s = Regex.Replace(s, @"\bSaya\b", "Aku");
            s = Regex.Replace(s, @"\bsaya\b", "aku");

            // Fix awkward lowercase after period
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
            return RecognizeBitmap(sourceBmp, srcLang, out usedOnline, out dummy, out error);
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            usedOnline = false;
            detectedChoiceLayout = false;
            error = "";

            if (srcLang != "auto" && !HasLocalPackFor(srcLang))
            {
                usedOnline = true;
                return RecognizeOnlineOcrSpace(sourceBmp, srcLang, out error);
            }

            Bitmap prepared = PrepareForOcr(sourceBmp);
            try
            {
                OcrEngine eng = GetWinRtEngine(srcLang);
                if (eng == null) { usedOnline = true; return RecognizeOnlineOcrSpace(sourceBmp, srcLang, out error); }

                BitmapData bd = prepared.LockBits(new Rectangle(0, 0, prepared.Width, prepared.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] buf = new byte[bd.Stride * prepared.Height];
                try { Marshal.Copy(bd.Scan0, buf, 0, buf.Length); }
                finally { prepared.UnlockBits(bd); }

                var ibuf = CryptographicBuffer.CreateFromByteArray(buf);
                using (SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(
                    ibuf, BitmapPixelFormat.Bgra8, prepared.Width, prepared.Height, BitmapAlphaMode.Premultiplied))
                {
                    OcrResult res = AwaitWinRt(eng.RecognizeAsync(sb));
                    if (res == null || res.Lines == null || res.Lines.Count == 0) return "";

                    if (res.Lines.Count >= 2 && res.Lines.Count <= 5)
                    {
                        int shortLines = 0, terminalLines = 0, choiceMarkerLines = 0;
                        foreach (OcrLine ln in res.Lines)
                        {
                            string t = ln.Text.Trim();
                            if (t.Length <= 42) shortLines++;
                            if (t.EndsWith(".") || t.EndsWith("!") || t.EndsWith("?")) terminalLines++;
                            if (Regex.IsMatch(t, @"^(?:[>▸►▶•]|\[?\d+[\]\.\)])")) choiceMarkerLines++;
                        }
                        if (choiceMarkerLines >= 1 || (shortLines == res.Lines.Count && terminalLines >= 2))
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
            catch (Exception ex) { error = "OCR error: " + ex.Message; return ""; }
            finally { if (prepared != sourceBmp) prepared.Dispose(); }
        }

        private static Bitmap PrepareForOcr(Bitmap bmp)
        {
            if (bmp.Height < 150 && bmp.Width < 1400)
            {
                int nw = bmp.Width * 2, nh = bmp.Height * 2;
                Bitmap up = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(up))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, 0, 0, nw, nh);
                }
                return up;
            }
            return bmp;
        }

        private static string HttpRequestFast(string url, string postBody = null, string contentType = "application/x-www-form-urlencoded")
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.KeepAlive = true;
            req.Timeout = 4500;
            req.ReadWriteTimeout = 4500;
            req.UserAgent = "Mozilla/5.0";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

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
                string s = Regex.Replace(ln, @"[|_~=<>▸►▶•·]+", " ");
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
                string s = Regex.Replace(ln, @"[|_~=<>]{2,}", " ");
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
            int[] prev = new int[lb.Length + 1];
            int[] curr = new int[lb.Length + 1];
            for (int j = 0; j <= lb.Length; j++) prev[j] = j;
            for (int i = 1; i <= la.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= lb.Length; j++)
                {
                    int cost = (la[i - 1] == lb[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                }
                int[] tmp = prev; prev = curr; curr = tmp;
            }
            return 1f - ((float)prev[lb.Length] / maxLen);
        }

        public string TranslateChoicesLineByLine(string multilineChoices, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, bool naturalTone, out string usedProvider, out string error)
        {
            usedProvider = preferredEngine;
            error = "";
            string[] lines = multilineChoices.Split('\n');
            if (lines.Length == 1)
            {
                string single = TranslateText(lines[0], srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error);
                return string.IsNullOrEmpty(single) ? "" : ("• " + single);
            }

            string batchResult = TranslateText(multilineChoices, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error);
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
                string tr = TranslateText(trimmed, srcLang, tgtLang, preferredEngine, geminiKey, naturalTone, out usedProvider, out error);
                if (!string.IsNullOrEmpty(tr)) results.Add("• " + tr.Trim());
            }
            return string.Join("\n", results.ToArray());
        }

        public string TranslateText(string text, string srcLang, string tgtLang,
            string preferredEngine, string geminiKey, bool naturalTone, out string usedProvider, out string error)
        {
            usedProvider = preferredEngine == "mymemory" ? "MyMemory" : (preferredEngine == "gemini" ? "Gemini AI" : "Google");
            error = "";
            if (string.IsNullOrWhiteSpace(text)) return "";

            string cacheKey = preferredEngine + "|" + (naturalTone ? "N" : "L") + "|" + srcLang + "|" + tgtLang + "|" + text;
            lock (_cacheLock)
            {
                string hit;
                if (_cache.TryGetValue(cacheKey, out hit))
                {
                    usedProvider += " (Instan)";
                    return hit;
                }
            }

            string prepped = PreProcessGameIdioms(text, srcLang, tgtLang, naturalTone);
            string result = "";

            if (preferredEngine == "gemini" && !string.IsNullOrWhiteSpace(geminiKey))
            {
                result = CallGemini(text, tgtLang, geminiKey, naturalTone, out error);
                if (!string.IsNullOrEmpty(result)) usedProvider = "Gemini AI";
            }

            if (string.IsNullOrEmpty(result))
            {
                if (preferredEngine == "mymemory")
                {
                    result = CallMyMemory(prepped, srcLang, tgtLang, out error);
                    if (!string.IsNullOrEmpty(result)) usedProvider = "MyMemory";
                    else { result = CallGoogleGtx(prepped, srcLang, tgtLang, out error); if (!string.IsNullOrEmpty(result)) { usedProvider = "Google"; error = ""; } }
                }
                else
                {
                    result = CallGoogleGtx(prepped, srcLang, tgtLang, out error);
                    if (!string.IsNullOrEmpty(result)) usedProvider = "Google";
                    else { result = CallMyMemory(prepped, srcLang, tgtLang, out error); if (!string.IsNullOrEmpty(result)) { usedProvider = "MyMemory"; error = ""; } }
                }
                result = NaturalizeIndonesian(result, tgtLang, naturalTone);
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

        private static string CallGemini(string text, string tgtLang, string apiKey, bool naturalTone, out string error)
        {
            error = "";
            try
            {
                string langName = tgtLang == "id" ? "Indonesian" : (tgtLang == "ms" ? "Malay" : tgtLang);
                string style = naturalTone ? "natural conversational casual RPG game dialogue (use kamu/aku, avoid stiff literal translations)" : "accurate natural translation";
                string prompt = "Translate this game text into " + langName + " using " + style + ". Return ONLY the translated text, preserving line breaks:\n" + text;
                string escaped = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                string body = "{\"contents\":[{\"parts\":[{\"text\":\"" + escaped + "\"}]}]}";
                string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=" + Uri.EscapeDataString(apiKey.Trim());
                string json = HttpRequestFast(url, body, "application/json");
                int idx = json.IndexOf("\"text\": \"", StringComparison.Ordinal);
                if (idx < 0) idx = json.IndexOf("\"text\":\"", StringComparison.Ordinal);
                if (idx < 0) return "";
                int q = json.IndexOf('"', idx + 6);
                return ReadJsonString(json, q).Trim();
            }
            catch (Exception ex) { error = "Gemini error: " + ex.Message; return ""; }
        }

        private static string CallGoogleGtx(string text, string srcLang, string tgtLang, out string error)
        {
            error = "";
            try
            {
                string sl = string.IsNullOrEmpty(srcLang) ? "auto" : srcLang;
                string tl = string.IsNullOrEmpty(tgtLang) ? "id" : tgtLang;
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=" +
                             Uri.EscapeDataString(sl) + "&tl=" + Uri.EscapeDataString(tl) +
                             "&dt=t&q=" + Uri.EscapeDataString(text);
                return ParseGoogleGtxJson(HttpRequestFast(url));
            }
            catch (Exception ex) { error = "Google error: " + ex.Message; return ""; }
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
                string json = HttpRequestFast(url);
                int idx = json.IndexOf("\"translatedText\":\"", StringComparison.Ordinal);
                if (idx < 0) return "";
                return ReadJsonString(json, idx + 17);
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
