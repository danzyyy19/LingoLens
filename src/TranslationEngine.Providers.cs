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
    public partial class TranslationEngine
    {
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
