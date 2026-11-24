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

            int dx = Math.Max(1, Math.Min(3, w / 300));
            int dy = Math.Max(1, Math.Min(3, h / 80));

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
                        int l = (77 * r + 150 * g + 29 * b) >> 8;
                        // Match BOTH neutral white/off-white strokes AND bright yellow/cyan/gold RPG/cyberpunk subtitles!
                        if ((mn >= 142 && (mx - mn) <= 86) || (mx >= 195 && l >= 140))
                        {
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

            // Detect bright/glowing/saturated 3D background (~0.15ms sampled) early so we can smart-exit on clean dark boxes!
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

            // LATENCY FAST-PATH 1: Clean dark-box subtitle with pristine Pass 1 read -> skip Pass 2/3/4 (~50ms saved!)
            if (!hasBrightBg && bestScore >= 96 && bestClean.Length >= 10) return;

            // PASS 2 (LOCAL HIGH-PASS BACKGROUND REMOVAL + FONT-PRESERVING EDGE SHARPEN — strips BOTH bright white/gray glare AND colored nebula!)
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
                // LATENCY FAST-PATH 2: If Pass 1 & Pass 2 agree on a pristine reading, skip Pass 3/4 (~35ms saved!)
                if (bestScore >= 94 && !string.IsNullOrWhiteSpace(clean1) && !string.IsNullOrWhiteSpace(clean2) &&
                    NormalizeCoreText(clean1) == NormalizeCoreText(clean2))
                {
                    return;
                }
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

            // PASS 4 (HOLLOW THIN-BORDER WHITE-ON-WHITE ENHANCER):
            // Fixes white font + thin black outline over a pure white / very bright game background!
            // Runs on bright backgrounds whenever Pass 1-3 struggled (< 90) or returned empty (0ms overhead when Pass 1-3 already nailed it!).
            if (hasBrightBg && (bestScore < 90 || string.IsNullOrWhiteSpace(bestClean)))
            {
                int padW4, padH4;
                byte[] fillBuf = ApplyThinDarkBorderFillEnhancer(upscaledRaw, nw, nh, out padW4, out padH4);
                bool choice4 = false;
                string raw4 = RunWinRtOcrFromBuffer(eng, fillBuf, padW4, padH4, srcLang, out choice4);
                string clean4 = choice4 ? NormalizeChoices(raw4, srcLang) : NormalizeText(raw4, srcLang, true);
                int score4 = ScoreOcrQuality(clean4, srcLang);
                if (!string.IsNullOrWhiteSpace(clean4) &&
                    (score4 > bestScore + 2 || (Math.Abs(score4 - bestScore) <= 4 && clean4.Length > bestClean.Length + 3)))
                {
                    bestRaw = raw4; bestClean = clean4; bestScore = score4; bestChoice = choice4;
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

                // Softly suppress saturated 3D nebula/effects (sat > 52) WITHOUT crushing bright yellow/cyan neon subtitles (mx >= 215 && y >= 155)!
                int suppressed = y;
                if (sat > 52 && !(mx >= 215 && y >= 155))
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

            // Detect genuine light/parchment dialog box with dark text (supports both white paper AND warm yellow/tan parchment like Skyrim/BG3/Pentiment!)
            int safeTotal = Math.Max(1, total);
            int avgLum = (int)(sumLum / safeTotal);
            int avgSat = (int)(sumSat / safeTotal);
            int avgMinC = (int)(sumMinC / safeTotal);
            bool lightBoxDarkText = (avgLum >= 155 && avgSat <= 95 && avgMinC >= 115 && darkCount > total / 250 && darkCount < total * 30 / 100);

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

        /// <summary>
        /// Hollow Thin-Border White-on-White Subtitle Enhancer (~3ms O(W*H)):
        /// When white text with a thin dark outline sits on a white or very bright game background,
        /// standard OCR sees hollow wireframe outlines and fails to recognize letters.
        /// Extracts thin dark outline pixels relative to local bright background, applies separable 1D
        /// morphological closing (Dilate -> Erode) to fill the hollow white stroke interior without
        /// closing letter counters (O, D, e, a), and outputs smooth solid black ink on pure white!
        /// </summary>
        public static byte[] ApplyThinDarkBorderFillEnhancer(byte[] srcBgra, int w, int h, out int outW, out int outH)
        {
            const int pad = 16;
            outW = w + pad * 2;
            outH = h + pad * 2;
            int total = w * h;
            byte[] lum = new byte[total];

            for (int i = 0; i < total; i++)
            {
                int off = i << 2;
                lum[i] = (byte)((77 * srcBgra[off + 2] + 150 * srcBgra[off + 1] + 29 * srcBgra[off]) >> 8);
            }

            // Fast O(W*H) separable local background luminance estimate (radius rBg = 12..16px)
            int rBg = Math.Max(10, Math.Min(16, h / 10));
            int win = rBg * 2 + 1;
            byte[] blurH = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int sum = 0;
                for (int k = -rBg; k <= rBg; k++)
                {
                    int cx = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    sum += lum[row + cx];
                }
                for (int x = 0; x < w; x++)
                {
                    blurH[row + x] = (byte)(sum / win);
                    int xOut = x - rBg; if (xOut < 0) xOut = 0;
                    int xIn = x + rBg + 1; if (xIn >= w) xIn = w - 1;
                    sum += lum[row + xIn] - lum[row + xOut];
                }
            }

            // Extract thin dark border pixels (significantly darker than local bright background)
            byte[] darkMask = new byte[total];
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
                    int l = lum[idx];
                    // Catch both crisp black borders (l < 115) and anti-aliased thin dark outlines on white/bright bg
                    if ((localBg >= 165 && (localBg - l) >= 28) || (localBg >= 135 && l < 115 && (localBg - l) >= 38))
                    {
                        darkMask[idx] = 1;
                    }
                    int yOut = y - rBg; if (yOut < 0) yOut = 0;
                    int yIn = y + rBg + 1; if (yIn >= h) yIn = h - 1;
                    sum += blurH[yIn * w + x] - blurH[yOut * w + x];
                }
            }

            // Morphological Closing: Dilate(rDil) -> Erode(rEro) fills hollow white interior between thin black borders!
            // Using rEro == rDil (exact closing) preserves original outer letter thickness & punctuation ('.', 'i') cleanly.
            int rDil = (h >= 180) ? 3 : 2;
            int rEro = rDil;

            // 1. Separable 1D Dilation
            byte[] dilH = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int run = 0;
                for (int x = 0; x < Math.Min(w, rDil + 1); x++) run += darkMask[row + x];
                for (int x = 0; x < w; x++)
                {
                    if (run > 0) dilH[row + x] = 1;
                    int left = x - rDil;
                    int right = x + rDil + 1;
                    if (left >= 0) run -= darkMask[row + left];
                    if (right < w) run += darkMask[row + right];
                }
            }
            byte[] dil = new byte[total];
            for (int x = 0; x < w; x++)
            {
                int run = 0;
                for (int y = 0; y < Math.Min(h, rDil + 1); y++) run += dilH[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    if (run > 0) dil[y * w + x] = 1;
                    int top = y - rDil;
                    int bot = y + rDil + 1;
                    if (top >= 0) run -= dilH[top * w + x];
                    if (bot < h) run += dilH[bot * w + x];
                }
            }

            // 2. Separable 1D Erosion (restores outer silhouette while keeping filled stroke interiors solid!)
            byte[] eroH = new byte[total];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int zeros = 0;
                for (int k = -rEro; k <= rEro; k++)
                {
                    int cx = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    if (dil[row + cx] == 0) zeros++;
                }
                for (int x = 0; x < w; x++)
                {
                    if (zeros == 0) eroH[row + x] = 1;
                    int xOut = x - rEro; if (xOut < 0) xOut = 0;
                    int xIn = x + rEro + 1; if (xIn >= w) xIn = w - 1;
                    if (dil[row + xOut] == 0) zeros--;
                    if (dil[row + xIn] == 0) zeros++;
                }
            }
            byte[] closed = new byte[total];
            for (int x = 0; x < w; x++)
            {
                int zeros = 0;
                for (int k = -rEro; k <= rEro; k++)
                {
                    int cy = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    if (eroH[cy * w + x] == 0) zeros++;
                }
                for (int y = 0; y < h; y++)
                {
                    if (zeros == 0) closed[y * w + x] = 1;
                    int yOut = y - rEro; if (yOut < 0) yOut = 0;
                    int yIn = y + rEro + 1; if (yIn >= h) yIn = h - 1;
                    if (eroH[yOut * w + x] == 0) zeros--;
                    if (eroH[yIn * w + x] == 0) zeros++;
                }
            }

            // 3. Render with gentle 3x3 anti-aliasing onto pure white 16px-padded canvas
            byte[] outBuf = new byte[outW * outH * 4];
            for (int i = 0; i < outBuf.Length; i++) outBuf[i] = 255;

            for (int y = 0; y < h; y++)
            {
                int rU = (y > 0 ? y - 1 : 0) * w;
                int rC = y * w;
                int rD = (y + 1 < h ? y + 1 : h - 1) * w;
                int rowOut = (y + pad) * outW;

                for (int x = 0; x < w; x++)
                {
                    int xm = x > 0 ? x - 1 : 0;
                    int xp = x + 1 < w ? x + 1 : w - 1;
                    int cnt = closed[rU + xm] + closed[rU + x] + closed[rU + xp] +
                              closed[rC + xm] + (closed[rC + x] << 2) + closed[rC + xp] +
                              closed[rD + xm] + closed[rD + x] + closed[rD + xp];
                    if (cnt > 0)
                    {
                        byte pv = (byte)(255 - (cnt * 255 / 12));
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

                // For CJK (Japanese/Chinese/Korean), compute max line glyph height so tiny furigana ruby-text lines (< 50% height) are filtered out!
                double maxCjkLineH = 0;
                if (isCjk && res.Lines.Count >= 2)
                {
                    foreach (OcrLine lnH in res.Lines)
                    {
                        if (lnH.Words == null || lnH.Words.Count == 0) continue;
                        double sumH = 0;
                        foreach (OcrWord wH in lnH.Words) sumH += wH.BoundingRect.Height;
                        double avgH = sumH / lnH.Words.Count;
                        if (avgH > maxCjkLineH) maxCjkLineH = avgH;
                    }
                }

                foreach (OcrLine ln in res.Lines)
                {
                    if (ln.Words == null || ln.Words.Count == 0) continue;

                    if (isCjk && maxCjkLineH >= 16.0)
                    {
                        double sumH = 0;
                        foreach (OcrWord wH in ln.Words) sumH += wH.BoundingRect.Height;
                        if ((sumH / ln.Words.Count) < maxCjkLineH * 0.50) continue;
                    }

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

    }
}
