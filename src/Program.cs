using System;
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
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
[assembly: AssemblyInformationalVersion("0.2 Beta")]

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
                string brainDir = @"C:\Users\danic\.gemini\antigravity\brain\a4be1715-04e8-4b64-be13-1a44e8d8f78d";
                using (MainWindow w = new MainWindow(new AppConfig()))
                {
                    w.Size = new Size(1080, 730);
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
