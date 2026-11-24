using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("LingoLens")]
[assembly: AssemblyDescription("Penerjemah Layar Game Real-Time untuk Windows")]
[assembly: AssemblyCompany("Danzyyy")]
[assembly: AssemblyProduct("LingoLens")]
[assembly: AssemblyCopyright("Copyright © 2026 Danzyyy")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]
[assembly: AssemblyInformationalVersion("0.4 Beta")]

namespace TranslatorLive
{
    static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        private static void EnableHighDpi()
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }
        }

        private static void TakeOverExistingInstances()
        {
            try
            {
                int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == myPid) continue;
                        string n = (p.ProcessName ?? "").ToLowerInvariant();
                        if (n.StartsWith("lingolens") || n.StartsWith("translatorlive"))
                        {
                            try { p.CloseMainWindow(); } catch { }
                            if (!p.WaitForExit(350))
                            {
                                try { p.Kill(); p.WaitForExit(500); } catch { }
                            }
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            EnableHighDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && args.Length > 0 && args[0] == "--self-test")
            {
                return RunSelfTest();
            }

            // Guarantee ONLY ONE LingoLens instance runs (auto-closes any old tray instance so hotkeys, status & history never desync!)
            TakeOverExistingInstances();

            AppConfig cfg = AppConfig.Load();
            Application.Run(new MainWindow(cfg));
            return 0;
        }

        private static void LogTest(string msg)
        {
            try { Console.WriteLine(msg); } catch { }
            try { File.AppendAllText("selftest.log", msg + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private static int RunSelfTest()
        {
            AttachConsole(-1);
            try
            {
                var stdOutStream = Console.OpenStandardOutput();
                if (stdOutStream != null)
                {
                    var writer = new StreamWriter(stdOutStream, Encoding.UTF8) { AutoFlush = true };
                    Console.SetOut(writer);
                    Console.SetError(writer);
                }
            }
            catch { }

            try { File.WriteAllText("selftest.log", ""); } catch { }

            try
            {
                LogTest("=== LingoLens C# Claude UI + Engine Self-Test ===");

                AppConfig cfg = new AppConfig();
                using (TranslationEngine eng = new TranslationEngine(cfg))
                {
                    using (Bitmap bmp = new Bitmap(680, 120, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(20, 19, 17));
                            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                            using (Font f = new Font("Segoe UI", 28f, FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                g.DrawString("The ancient gate is open!", f, Brushes.White, 18f, 36f);
                            }
                        }
                        bool usedOnline;
                        string err;
                        string ocr = eng.RecognizeBitmap(bmp, "en", out usedOnline, out err);
                        string norm = TranslationEngine.NormalizeText(ocr, "en", true);
                        LogTest("[OCR] '" + norm + "'");
                        if (string.IsNullOrEmpty(norm) || !norm.ToLowerInvariant().Contains("gate"))
                        {
                            LogTest("[FAIL] WinRT OCR failed.");
                            return 1;
                        }

                        string prov, terr;
                        string tr = eng.TranslateText(norm, "en", "id", "google", "", true, out prov, out terr);
                        LogTest("[TRANS-DIALOG] '" + tr + "' (" + prov + ")");
                        if (string.IsNullOrEmpty(tr))
                        {
                            LogTest("[FAIL] Dialog translation failed.");
                            return 1;
                        }

                        // Test Natural Idiom / Polite Everyday Tone + Speaker Tag + Glossary
                        string speakerTest;
                        string bodyOnly = TranslationEngine.ExtractSpeakerTag("Saria: You should hurry. Catch me up later!", out speakerTest);
                        LogTest("[SPEAKER] '" + speakerTest + "' -> '" + bodyOnly + "'");
                        if (speakerTest != "Saria" || !bodyOnly.Contains("hurry"))
                        {
                            LogTest("[FAIL] Speaker tag extraction failed.");
                            return 1;
                        }
                        string idiomTr = eng.TranslateText(bodyOnly, "en", "id", "google", "", true, out prov, out terr, "Hyrule;Saria;Triforce");
                        LogTest("[TRANS-IDIOM] '" + idiomTr + "' (" + prov + ")");
                        if (string.IsNullOrEmpty(idiomTr) || idiomTr.ToLowerInvariant().Contains("tangkap") || idiomTr.ToLowerInvariant().Contains("anda"))
                        {
                            LogTest("[FAIL] Idiom / polite everyday naturalization failed.");
                            return 1;
                        }

                        // Test Floating Cutscene Subtitle (White font without black box over bright golden glow) + High-Res Speed Benchmark (1437x345)
                        using (Bitmap glowBmp = new Bitmap(1437, 345, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics gg = Graphics.FromImage(glowBmp))
                            {
                                gg.Clear(Color.FromArgb(225, 185, 85)); // Bright golden Triforce-like background
                                gg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                                using (Font ff = new Font("Georgia", 28f, FontStyle.Regular))
                                {
                                    gp.AddString("Therefore, it is forbidden to\ntouch the Triforce carelessly.", ff.FontFamily, 0, 34f, new PointF(180f, 110f), StringFormat.GenericTypographic);
                                    using (Pen outlinePen = new Pen(Color.FromArgb(180, 35, 25, 15), 3.2f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                                        gg.DrawPath(outlinePen, gp);
                                    using (SolidBrush fillBr = new SolidBrush(Color.FromArgb(248, 248, 248)))
                                        gg.FillPath(fillBr, gp);
                                }
                            }
                            System.Diagnostics.Stopwatch swFast = System.Diagnostics.Stopwatch.StartNew();
                            string glowOcr = eng.RecognizeBitmap(glowBmp, "en", out usedOnline, out err);
                            long ocrElapsed = swFast.ElapsedMilliseconds;
                            string glowNorm = TranslationEngine.NormalizeText(glowOcr, "en", true);
                            string glowTr = eng.TranslateText(glowNorm, "en", "id", "google", "", true, out prov, out terr);
                            swFast.Stop();
                            LogTest("[OCR-FLOATING-CUTSCENE] (" + ocrElapsed + "ms OCR, " + swFast.ElapsedMilliseconds + "ms Total) '" + glowNorm + "' -> '" + glowTr + "'");
                            if (string.IsNullOrEmpty(glowNorm) || !glowNorm.ToLowerInvariant().Contains("forbidden") || !glowNorm.ToLowerInvariant().Contains("triforce"))
                            {
                                LogTest("[FAIL] Floating cutscene subtitle OCR failed: " + glowNorm);
                                return 1;
                            }
                        }

                        // Test White Font + Thin Black Border over Pure White Background (Bug #1 regression test!)
                        using (Bitmap whiteBgBmp = new Bitmap(920, 140, PixelFormat.Format32bppArgb))
                        {
                            using (Graphics gw = Graphics.FromImage(whiteBgBmp))
                            {
                                gw.Clear(Color.FromArgb(255, 255, 255)); // Pure white game background
                                gw.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                                using (Font ff = new Font("Segoe UI", 26f, FontStyle.Bold))
                                {
                                    gp.AddString("The white snow hides the secret path.", ff.FontFamily, (int)FontStyle.Bold, 30f, new PointF(40f, 48f), StringFormat.GenericTypographic);
                                    using (Pen thinBorder = new Pen(Color.FromArgb(220, 18, 18, 18), 2.0f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                                        gw.DrawPath(thinBorder, gp);
                                    using (SolidBrush whiteFill = new SolidBrush(Color.White))
                                        gw.FillPath(whiteFill, gp);
                                }
                            }
                            string wbOcr = eng.RecognizeBitmap(whiteBgBmp, "en", out usedOnline, out err);
                            string wbNorm = TranslationEngine.NormalizeText(wbOcr, "en", true);
                            LogTest("[OCR-WHITE-ON-WHITE-BORDER] '" + wbNorm + "'");
                            if (string.IsNullOrEmpty(wbNorm) || !wbNorm.ToLowerInvariant().Contains("snow") || !wbNorm.ToLowerInvariant().Contains("secret"))
                            {
                                LogTest("[FAIL] White font with thin black border on white background OCR failed: " + wbNorm);
                                return 1;
                            }
                        }

                        // Test Game OCR Cleaning & Universal Digit-to-Letter / Ligature Restoration across ALL games
                        string dirtySample = "The man I spoke of<, dreams each night, Linle... my 9äUxyfios exhausted... mal<e your way to frule Castle.";
                        string cleanedSample = TranslationEngine.CleanGameOcrText(dirtySample, "en");
                        LogTest("[CLEAN-GAME-OCR] " + cleanedSample);
                        if (!cleanedSample.Contains("Link") || !cleanedSample.Contains("Hyrule Castle") || !cleanedSample.Contains("make") || cleanedSample.Contains("9äUxyfios"))
                        {
                            LogTest("[FAIL] CleanGameOcrText failed: " + cleanedSample);
                            return 1;
                        }

                        string univDirty = "11 going to 1001 at that! 1'm sorry, l don't know vvhat y0u rnean. He wi11 te11 y0u in 1st place for 11 years.";
                        string univClean = TranslationEngine.CleanGameOcrText(univDirty, "en");
                        LogTest("[UNIVERSAL-OCR-REPAIR] " + univClean);
                        if (!univClean.Contains("I'm going to look at that!") ||
                            !univClean.Contains("I'm sorry, I don't know what you mean.") ||
                            !univClean.Contains("He will tell you in 1st place for 11 years."))
                        {
                            LogTest("[FAIL] Universal OCR repair failed: " + univClean);
                            return 1;
                        }

                        // Test Saria's Song small-font OCR repair ("Whenever\" wapt%talk..." -> "Whenever you want to talk...") + possessive naturalization ("laguku")
                        string sariaDirty = "Whenever\" wapt%talk to me, just play my song. You can call it Saria's Song. And here it goes...";
                        string sariaClean = TranslationEngine.CleanGameOcrText(sariaDirty, "en");
                        string sariaTr = eng.TranslateText(sariaClean, "en", "id", "google", "", true, out prov, out terr, "Saria");
                        LogTest("[SARIA-OCR-CLEAN] " + sariaClean);
                        LogTest("[SARIA-TRANS] " + sariaTr);
                        if (!sariaClean.Contains("Whenever you want to talk to me") || sariaClean.Contains("wapt") || sariaClean.Contains("%") ||
                            string.IsNullOrEmpty(sariaTr) || !sariaTr.ToLowerInvariant().Contains("laguku") || sariaTr.ToLowerInvariant().Contains("lagu aku"))
                        {
                            LogTest("[FAIL] Saria small-font repair or possessive naturalization failed: " + sariaClean + " -> " + sariaTr);
                            return 1;
                        }

                        // Direct verification of Indonesian everyday naturalization rules
                        string testStiff1 = TranslationEngine.NaturalizeIndonesian("Tangkap saya nanti. Saya punya ini.", "id", true);
                        string testStiff2 = TranslationEngine.NaturalizeIndonesian("Apakah Anda tidak bisa melakukannya? Jangan khawatir, tentu saja!", "id", true);
                        string testStiff3 = TranslationEngine.NaturalizeIndonesian("Dia sedang tidur karena sangat lelah.", "id", true);
                        string testZelda = TranslationEngine.NaturalizeIndonesian("Temui Pohon Deku yang baik dan bawa Pedang guru.", "id", true);
                        LogTest("[NATURAL-1] " + testStiff1);
                        LogTest("[NATURAL-2] " + testStiff2);
                        LogTest("[NATURAL-3] " + testStiff3);
                        LogTest("[NATURAL-ZELDA] " + testZelda);

                        if (testStiff1.ToLowerInvariant().Contains("tangkap") || testStiff1.ToLowerInvariant().Contains("saya punya"))
                        {
                            LogTest("[FAIL] testStiff1 failed: " + testStiff1);
                            return 1;
                        }
                        if (testStiff2.ToLowerInvariant().Contains("anda") || testStiff2.ToLowerInvariant().Contains("tidak bisa") || testStiff2.ToLowerInvariant().Contains("saja"))
                        {
                            LogTest("[FAIL] testStiff2 failed: " + testStiff2);
                            return 1;
                        }
                        if (!testStiff3.Contains("lagi tidur") || !testStiff3.Contains("lelah banget"))
                        {
                            LogTest("[FAIL] testStiff3 failed: " + testStiff3);
                            return 1;
                        }
                        if (testZelda.Contains("Pohon Deku yang baik") || testZelda.Contains("Pedang guru"))
                        {
                            LogTest("[FAIL] testZelda failed: " + testZelda);
                            return 1;
                        }

                        // Test v0.3 Indonesian Gaul Mode (Gue/Lo, gak, udah, banget, temen, liat, tau)
                        string gaul1 = TranslationEngine.NaturalizeIndonesian("Apakah Anda tidak tahu di mana teman saya? Saya sangat lelah dan harus cepat melihat mereka.", "id", 0);
                        LogTest("[GAUL-MODE] " + gaul1);
                        if (!gaul1.ToLowerInvariant().Contains("lo") || !gaul1.ToLowerInvariant().Contains("gue") ||
                            !gaul1.ToLowerInvariant().Contains("gak tau") || !gaul1.ToLowerInvariant().Contains("capek") ||
                            gaul1.ToLowerInvariant().Contains("anda") || gaul1.ToLowerInvariant().Contains("saya"))
                        {
                            LogTest("[FAIL] Gaul mode test failed: " + gaul1);
                            return 1;
                        }

                        // Test v0.3 Fuzzy Glossary OCR Auto-Repair
                        string fuzzyIn = "Linh must reach Hyrulc Castle before Ganondrof steals the Trlforce.";
                        string fuzzyFixed = TranslationEngine.RepairOcrWithGlossary(fuzzyIn, "Link;Hyrule;Ganondorf;Triforce");
                        LogTest("[GLOSSARY-FUZZY] " + fuzzyFixed);
                        if (!fuzzyFixed.Contains("Link") || !fuzzyFixed.Contains("Hyrule") || !fuzzyFixed.Contains("Triforce"))
                        {
                            LogTest("[FAIL] Glossary fuzzy repair failed: " + fuzzyFixed);
                            return 1;
                        }

                        // Test v0.3 Anti-Flip-Flop Lock (moving background drops/corrupts 1 word on static instant subtitle)
                        bool sameSub, expansion;
                        TranslationEngine.AnalyzeSubtitleRelation(
                            "we need to find the master sword before dark falls",
                            "we need to flnd the master sword before dark",
                            true, out sameSub, out expansion);
                        LogTest("[ANTI-FLIPFLOP] sameSub=" + sameSub + " expansion=" + expansion);
                        if (!sameSub || expansion)
                        {
                            LogTest("[FAIL] Anti-flip-flop lock failed to lock noisy variation of same instant subtitle!");
                            return 1;
                        }

                        // Test v0.4 Real User Thin-Font Repair (Delcu -> Deku, talce -> take, fib Din -> Din, uOHMA/Nisciorn rejected)
                        string thinFix1 = TranslationEngine.CleanGameOcrText("The Great Delcu Tree asked you to talce the sword.", "en");
                        string thinFix2 = TranslationEngine.CleanGameOcrText("fib Din, the goddess of power.", "en");
                        string glossDelcu = TranslationEngine.RepairOcrWithGlossary("The Great Delcu Tree is calling.", "Great Deku Tree;Deku;Link;Hyrule");
                        LogTest("[V04-THINFONT] " + thinFix1 + " | " + thinFix2 + " | " + glossDelcu);
                        if (!thinFix1.Contains("Deku") || !thinFix1.Contains("take") || !thinFix2.StartsWith("Din,") || !glossDelcu.Contains("Deku"))
                        {
                            LogTest("[FAIL] v0.4 thin-font split-letter repair failed!");
                            return 1;
                        }
                        if (TranslationEngine.IsMeaningfulSubtitle("uOHMA", "en") || TranslationEngine.IsMeaningfulSubtitle("Nisciorn", "en"))
                        {
                            LogTest("[FAIL] v0.4 hallucination filter failed to reject uOHMA / Nisciorn!");
                            return 1;
                        }

                        // Test v0.7 Temporal 2-Frame Background Eraser (static borderless subtitle over chaotic moving bright 3D sparks/bars!)
                        using (Bitmap f1 = new Bitmap(960, 140, PixelFormat.Format32bppArgb))
                        using (Bitmap f2 = new Bitmap(960, 140, PixelFormat.Format32bppArgb))
                        {
                            for (int fi = 0; fi < 2; fi++)
                            {
                                using (Graphics gT = Graphics.FromImage(fi == 0 ? f1 : f2))
                                {
                                    gT.Clear(Color.FromArgb(24, 34, 48));
                                    // Draw bright moving 3D glare/sparks at DIFFERENT positions in f1 vs f2!
                                    int shift = fi * 95;
                                    using (SolidBrush brGlow = new SolidBrush(Color.FromArgb(210, 235, 245, 255)))
                                    {
                                        for (int k = 0; k < 6; k++)
                                            gT.FillEllipse(brGlow, (k * 165 + shift) % 920, 16 + (k * 19) % 70, 58, 42);
                                    }
                                    gT.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                                    using (Font tf = new Font("Segoe UI", 28f, FontStyle.Regular, GraphicsUnit.Pixel))
                                    {
                                        gT.DrawString("Together, you and the princess must protect this world.", tf, Brushes.Black, 34f, 52f);
                                        gT.DrawString("Together, you and the princess must protect this world.", tf, Brushes.White, 32f, 50f);
                                    }
                                }
                            }
                            byte[] b1 = TranslationEngine.CopyBitmapToTightBgra(f1);
                            byte[] b2 = TranslationEngine.CopyBitmapToTightBgra(f2);
                            bool uoT, chT; string errT;
                            System.Diagnostics.Stopwatch swT = System.Diagnostics.Stopwatch.StartNew();
                            string tRaw = eng.RecognizeBitmapWithTemporal(f2, b2, b1, "en", true, out uoT, out chT, out errT);
                            long tMs = swT.ElapsedMilliseconds;
                            string tNorm = TranslationEngine.NormalizeText(tRaw, "en", true);
                            LogTest("[V07-TEMPORAL] (" + tMs + "ms OCR) '" + tNorm + "'");
                            if (!tNorm.Contains("Together") || !tNorm.Contains("princess") || !tNorm.Contains("protect"))
                            {
                                LogTest("[FAIL] v0.7 Temporal 2-Frame Background Eraser failed: " + tNorm);
                                return 1;
                            }
                        }

                        // Test v0.7 Garbage Gate rejects real fade-in / 3D-noise junk from user log (< 68)
                        int badScore1 = TranslationEngine.ScoreOcrQuality("a effi'hTIet's make good our promisetONabooru.u", "en");
                        int badScore2 = TranslationEngine.ScoreOcrQuality("Together...you and•the princess•e mustiprotect", "en");
                        int badScore3 = TranslationEngine.ScoreOcrQuality("you truly expect that {uch juv nile tricksw uld foolr e?", "en");
                        LogTest("[V07-GARBAGE-GATE] scores=" + badScore1 + "," + badScore2 + "," + badScore3);
                        if (badScore1 >= 68 || badScore2 >= 68 || badScore3 >= 68)
                        {
                            LogTest("[FAIL] v0.7 Garbage Gate failed to reject corrupted OCR lines!");
                            return 1;
                        }

                        // Test v0.8 FULL 10-FRAME ANIMATED CUTSCENE STRESS TEST inside user's REAL 1758x319 wide ROI!
                        // Verifies:
                        //  1. Fade-in frame (35% opacity) does NOT emit garbage
                        //  2. Moving 3D bright glare behind static Subtitle A emits ZERO flip-flops
                        //  3. 1-frame camera flash does NOT wipe lock or re-trigger Subtitle A
                        //  4. Brand-new Subtitle B is NOT erased by Temporal Eraser and cleanly replaces A ONCE!
                        {
                            eng.ClearState();
                            List<ScanUpdateEventArgs> cutsceneEvents = new List<ScanUpdateEventArgs>();
                            EventHandler<ScanUpdateEventArgs> handler = delegate(object sObj, ScanUpdateEventArgs ev)
                            {
                                if (!ev.IsError && !string.IsNullOrEmpty(ev.TranslatedText))
                                    cutsceneEvents.Add(ev);
                            };
                            eng.OnAreaTranslated += handler;
                            try
                            {
                                string subA = "Together, you and the princess must protect Hyrule.";
                                string subB = "I have been waiting for you at the Temple of Time.";
                                for (int stepIdx = 0; stepIdx <= 10; stepIdx++)
                                {
                                    using (Bitmap wideFrame = new Bitmap(1758, 319, PixelFormat.Format32bppArgb))
                                    {
                                        using (Graphics gw = Graphics.FromImage(wideFrame))
                                        {
                                            if (stepIdx == 6)
                                            {
                                                // Frame 6: 1-frame bright white camera flash (transient empty OCR!)
                                                gw.Clear(Color.FromArgb(245, 245, 245));
                                            }
                                            else
                                            {
                                                gw.Clear(Color.FromArgb(22, 30, 42));
                                                // Moving chaotic 3D glare/sparks shifting every frame across the wide 1758x319 ROI
                                                int shift = stepIdx * 137;
                                                using (SolidBrush brGlow = new SolidBrush(Color.FromArgb(205, 230, 242, 255)))
                                                {
                                                    for (int k = 0; k < 8; k++)
                                                        gw.FillEllipse(brGlow, (k * 230 + shift) % 1680, 20 + (k * 37 + stepIdx * 19) % 240, 72, 48);
                                                }
                                                if (stepIdx >= 1)
                                                {
                                                    string txtToDraw = (stepIdx <= 7) ? subA : subB;
                                                    int alpha = (stepIdx == 1) ? 85 : 255; // Frame 1 = 33% fade-in!
                                                    gw.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                                                    using (Font wf = new Font("Segoe UI", 28f, FontStyle.Regular, GraphicsUnit.Pixel))
                                                    using (SolidBrush shBr = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                                                    using (SolidBrush txBr = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                                                    {
                                                        gw.DrawString(txtToDraw, wf, shBr, 482f, 178f);
                                                        gw.DrawString(txtToDraw, wf, txBr, 480f, 176f);
                                                    }
                                                }
                                            }
                                        }
                                        eng.ProcessCapturedFrame(99, wideFrame, AreaTextMode.Dialog, "en", "id", "google", "",
                                            "Hyrule;Temple of Time;Link;Zelda", "Zelda", "", 0, true, true, false, true, true, 8, false);
                                    }
                                }
                            }
                            finally
                            {
                                eng.OnAreaTranslated -= handler;
                            }

                            LogTest("[V08-CUTSCENE-10FRAME] emitted=" + cutsceneEvents.Count);
                            foreach (ScanUpdateEventArgs cev in cutsceneEvents)
                                LogTest("   -> OCR='" + cev.SourceText + "' | TRANS='" + cev.TranslatedText + "' (" + cev.StatusMessage + ")");

                            if (cutsceneEvents.Count != 2 ||
                                !cutsceneEvents[0].SourceText.Contains("princess") ||
                                !cutsceneEvents[1].SourceText.Contains("Temple of Time"))
                            {
                                LogTest("[FAIL] v0.8 10-frame 1758x319 cutscene stability test failed! Expected exactly 2 clean emissions, got " + cutsceneEvents.Count);
                                return 1;
                            }
                        }

                        // Test optional real cutscene fixtures if present in ./tests/fixtures
                        string fixturesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tests", "fixtures");
                        string[] realShots = Directory.Exists(fixturesDir) ? Directory.GetFiles(fixturesDir, "*.jpg") : new string[0];
                        foreach (string rsPath in realShots)
                        {
                            if (File.Exists(rsPath))
                            {
                                using (Bitmap fullImg = new Bitmap(rsPath))
                                {
                                    // Crop wide realistic bottom subtitle zone (y = 66%..93%, x = 8%..92% -> wide noisy ROI!)
                                    Rectangle subCrop = new Rectangle(fullImg.Width * 8 / 100, fullImg.Height * 66 / 100, fullImg.Width * 84 / 100, fullImg.Height * 27 / 100);
                                    using (Bitmap cropped = fullImg.Clone(subCrop, PixelFormat.Format32bppArgb))
                                    {
                                        System.Diagnostics.Stopwatch swR = System.Diagnostics.Stopwatch.StartNew();
                                        bool uo; string oe;
                                        string rRaw = eng.RecognizeBitmap(cropped, "en", out uo, out oe);
                                        long rOcrMs = swR.ElapsedMilliseconds;
                                        string rNorm = TranslationEngine.NormalizeText(rRaw, "en", true);
                                        rNorm = TranslationEngine.RepairOcrWithGlossary(rNorm, "Great Deku Tree;Deku;Farore;Nayru;Din;Hyrule;Link;Navi");
                                        string rProv, rErr;
                                        string rTr = eng.TranslateText(rNorm, "en", "id", "google", "", 0, out rProv, out rErr, "Great Deku Tree;Deku;Farore;Nayru;Din;Hyrule;Link;Navi");
                                        swR.Stop();
                                        LogTest("[REAL-SHOT-WIDE] (" + cropped.Width + "x" + cropped.Height + ", " + rOcrMs + "ms OCR, " + swR.ElapsedMilliseconds + "ms Total, " + rProv + ") '" + rNorm + "' -> '" + rTr + "'");
                                        if (string.IsNullOrEmpty(rNorm) || rNorm.Length < 15)
                                        {
                                            LogTest("[FAIL] Real wide-crop screenshot OCR failed on " + Path.GetFileName(rsPath));
                                            return 1;
                                        }
                                    }
                                }
                            }
                        }

                        // Test v0.3 GameLoreService offline presets
                        var presets = GameLoreService.GetPresets();
                        if (presets.Count < 10 || GameLoreService.FindPreset("zelda") == null)
                        {
                            LogTest("[FAIL] GameLoreService presets check failed.");
                            return 1;
                        }
                        LogTest("[GAME-LORE] Loaded " + presets.Count + " built-in game lore presets.");

                        string glossTr = eng.TranslateText("Protect the Triforce in Hyrule!", "en", "id", "google", "", true, out prov, out terr, "Hyrule;Triforce");
                        LogTest("[GLOSSARY] '" + glossTr + "'");
                        if (!glossTr.Contains("Triforce") || !glossTr.Contains("Hyrule"))
                        {
                            LogTest("[FAIL] Glossary term protection failed.");
                            return 1;
                        }

                        // Test Choice Mode (line-by-line separate translation + universal OCR digit/apostrophe repair inside choices!)
                        string choicesRaw = "> 1'm not sure.\n  l dont know! x";
                        string choicesNorm = TranslationEngine.NormalizeChoices(choicesRaw, "en");
                        string choicesTr = eng.TranslateChoicesLineByLine(choicesNorm, "en", "id", "google", "", true, out prov, out terr);
                        LogTest("[TRANS-CHOICES]\n" + choicesTr);
                        if (string.IsNullOrEmpty(choicesTr) || !choicesTr.Contains("\n") || !choicesNorm.Contains("I'm") || !choicesNorm.Contains("I don't"))
                        {
                            LogTest("[FAIL] Choice line-by-line separation or OCR repair failed: " + choicesNorm);
                            return 1;
                        }

                        // Test Speaker Extraction False-Positive Guard ("Look\nThere is a chest!" or "Warning: Low HP" must NOT extract "Look"/"Warning" as speaker)
                        string falseSpk1, falseSpk2;
                        string body1 = TranslationEngine.ExtractSpeakerTag("Look\nThere is a treasure chest ahead!", out falseSpk1);
                        string body2 = TranslationEngine.ExtractSpeakerTag("Warning: Entering dangerous area.", out falseSpk2);
                        if (!string.IsNullOrEmpty(falseSpk1) || !string.IsNullOrEmpty(falseSpk2))
                        {
                            LogTest("[FAIL] Speaker false positive guard failed: '" + falseSpk1 + "', '" + falseSpk2 + "'");
                            return 1;
                        }
                    }
                }

                // Test Per-Area OverlayStyle + Profile INI serialization roundtrip (without touching user config)
                AppConfig profCfg = new AppConfig();
                profCfg.Areas.Add(new Area { Id = 1, Mode = AreaTextMode.Dialog, Ocr = new Rectangle(100, 100, 400, 120), Result = new Rectangle(100, 240, 400, 90) });
                profCfg.Areas.Add(new Area { Id = 2, Mode = AreaTextMode.Choice, Ocr = new Rectangle(600, 200, 300, 160), Result = new Rectangle(600, 380, 300, 120) });
                profCfg.ActiveProfile = "Zelda Ocarina";
                profCfg.Areas[1].Style.UseCustom = true;
                profCfg.Areas[1].Style.FontSize = 12;
                if (profCfg.Areas[1].EffectiveStyle(profCfg).FontSize != 12)
                {
                    LogTest("[FAIL] Per-area EffectiveStyle failed.");
                    return 1;
                }
                string tmpIni = Path.Combine(Path.GetTempPath(), "tl_selftest_profile.ini");
                profCfg.SaveToFile(tmpIni);
                AppConfig loadedProf = AppConfig.LoadFromFile(tmpIni);
                try { File.Delete(tmpIni); } catch { }
                if (loadedProf == null || loadedProf.Areas.Count < 2 || !loadedProf.Areas[1].Style.UseCustom || loadedProf.Areas[1].Style.FontSize != 12)
                {
                    LogTest("[FAIL] GameProfile INI save/load roundtrip failed.");
                    return 1;
                }
                LogTest("[PROFILE-OK] GameProfile & Per-Area Style roundtrip passed.");

                string outDir = Path.GetDirectoryName(Application.ExecutablePath);
                string brainDir = Environment.GetEnvironmentVariable("LINGOLENS_AUDIT_DIR") ?? "";
                using (MainWindow w = new MainWindow(new AppConfig()))
                {
                    w.Show();

                    // Dark Mode Live Tab
                    w.SetPreviewState(0, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_dark_live.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_dark_live.png"));

                    // Light Mode Live Tab
                    w.SetPreviewState(0, false); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_light_live.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_light_live.png"));

                    // Dark Mode Appearance Tab
                    w.SetPreviewState(1, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_dark_style.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_dark_style.png"));

                    // Light Mode Appearance Tab
                    w.SetPreviewState(1, false); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_light_style.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_light_style.png"));

                    // Dark Mode System Tab
                    w.SetPreviewState(2, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_dark_system.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_dark_system.png"));

                    // Light Mode System Tab
                    w.SetPreviewState(2, false); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_light_system.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_light_system.png"));

                    // Dark Mode Changelog Tab
                    w.SetPreviewState(3, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "minimalist_dark_changelog.png"));
                    if (Directory.Exists(brainDir)) SaveShot(w, Path.Combine(brainDir, "minimalist_dark_changelog.png"));

                    float auditDpi = 1.0f;
                    try { uint d = Native.GetDpiForSystem(); if (d >= 96) auditDpi = Math.Max(1.0f, Math.Min(2.25f, d / 96f)); } catch { }
                    Modals.RenderAuditScreenshots(outDir, auditDpi);

                    w.Close();
                }

                LogTest("[PASS] All Minimalist UI, Dark & Light Mode, WinRT OCR & Translation Tests Passed!");
                return 0;
            }
            catch (Exception ex)
            {
                LogTest("[EXCEPTION] " + ex.ToString());
                return 1;
            }
        }

        private static void SaveShot(MainWindow f, string path)
        {
            using (Bitmap b = new Bitmap(f.ClientSize.Width, f.ClientSize.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    f.RenderWindow(g);
                }
                b.Save(path, ImageFormat.Png);
            }
        }
    }
}
