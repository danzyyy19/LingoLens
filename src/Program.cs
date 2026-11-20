using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

        private static int RunSelfTest()
        {
            AttachConsole(-1);
            try
            {
                Console.WriteLine("=== LingoLens C# Claude UI + Engine Self-Test ===");

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
                        Console.WriteLine("[OCR] '" + norm + "'");
                        if (string.IsNullOrEmpty(norm) || !norm.ToLowerInvariant().Contains("gate"))
                        {
                            Console.WriteLine("[FAIL] WinRT OCR failed.");
                            return 1;
                        }

                        string prov, terr;
                        string tr = eng.TranslateText(norm, "en", "id", "google", "", true, out prov, out terr);
                        Console.WriteLine("[TRANS-DIALOG] '" + tr + "' (" + prov + ")");
                        if (string.IsNullOrEmpty(tr))
                        {
                            Console.WriteLine("[FAIL] Dialog translation failed.");
                            return 1;
                        }

                        // Test Natural Idiom / Polite Everyday Tone + Speaker Tag + Glossary
                        string speakerTest;
                        string bodyOnly = TranslationEngine.ExtractSpeakerTag("Saria: You should hurry. Catch me up later!", out speakerTest);
                        Console.WriteLine("[SPEAKER] '" + speakerTest + "' -> '" + bodyOnly + "'");
                        if (speakerTest != "Saria" || !bodyOnly.Contains("hurry"))
                        {
                            Console.WriteLine("[FAIL] Speaker tag extraction failed.");
                            return 1;
                        }
                        string idiomTr = eng.TranslateText(bodyOnly, "en", "id", "google", "", true, out prov, out terr, "Hyrule;Saria;Triforce");
                        Console.WriteLine("[TRANS-IDIOM] '" + idiomTr + "' (" + prov + ")");
                        if (string.IsNullOrEmpty(idiomTr) || idiomTr.ToLowerInvariant().Contains("tangkap aku") || idiomTr.ToLowerInvariant().Contains("anda"))
                        {
                            Console.WriteLine("[FAIL] Idiom / polite everyday naturalization failed.");
                            return 1;
                        }

                        string glossTr = eng.TranslateText("Protect the Triforce in Hyrule!", "en", "id", "google", "", true, out prov, out terr, "Hyrule;Triforce");
                        Console.WriteLine("[GLOSSARY] '" + glossTr + "'");
                        if (!glossTr.Contains("Triforce") || !glossTr.Contains("Hyrule"))
                        {
                            Console.WriteLine("[FAIL] Glossary term protection failed.");
                            return 1;
                        }

                        // Test Choice Mode (line-by-line separate translation!)
                        string choicesRaw = "> I'm not sure.\n  I do! x";
                        string choicesNorm = TranslationEngine.NormalizeChoices(choicesRaw);
                        string choicesTr = eng.TranslateChoicesLineByLine(choicesNorm, "en", "id", "google", "", true, out prov, out terr);
                        Console.WriteLine("[TRANS-CHOICES]\n" + choicesTr);
                        if (string.IsNullOrEmpty(choicesTr) || !choicesTr.Contains("\n"))
                        {
                            Console.WriteLine("[FAIL] Choice line-by-line separation failed.");
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
                    Console.WriteLine("[FAIL] Per-area EffectiveStyle failed.");
                    return 1;
                }
                string tmpIni = Path.Combine(Path.GetTempPath(), "tl_selftest_profile.ini");
                profCfg.SaveToFile(tmpIni);
                AppConfig loadedProf = AppConfig.LoadFromFile(tmpIni);
                try { File.Delete(tmpIni); } catch { }
                if (loadedProf == null || loadedProf.Areas.Count < 2 || !loadedProf.Areas[1].Style.UseCustom || loadedProf.Areas[1].Style.FontSize != 12)
                {
                    Console.WriteLine("[FAIL] GameProfile INI save/load roundtrip failed.");
                    return 1;
                }
                Console.WriteLine("[PROFILE-OK] GameProfile & Per-Area Style roundtrip passed.");

                string outDir = Path.GetDirectoryName(Application.ExecutablePath);
                using (MainWindow w = new MainWindow(new AppConfig()))
                {
                    w.Size = new Size(1080, 730);
                    w.Show();

                    w.SetPreviewState(0, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "claude_dark_live.png"));

                    w.SetPreviewState(1, false); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "claude_light_style.png"));

                    w.SetPreviewState(3, true); w.Refresh();
                    SaveShot(w, Path.Combine(outDir, "claude_dark_changelog.png"));

                    w.Close();
                }

                Console.WriteLine("[PASS] All Claude UI, Zoom, Fullscreen, WinRT OCR & Translation Tests Passed!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EXCEPTION] " + ex.ToString());
                return 1;
            }
        }

        private static void SaveShot(Form f, string path)
        {
            using (Bitmap b = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
            {
                f.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(path, ImageFormat.Png);
            }
        }
    }
}
