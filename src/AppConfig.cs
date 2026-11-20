using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace TranslatorLive
{
    public enum AreaTextMode
    {
        Dialog = 0,
        Choice = 1
    }

    public class OverlayStyle
    {
        public bool UseCustom = false;
        public string Font = "Segoe UI Variable Display";
        public int FontSize = 24;
        public bool Bold = true;
        public int Align = 1; // 0 kiri, 1 tengah, 2 kanan
        public Color TextColor = Color.FromArgb(255, 255, 255);
        public Color BgColor = Color.FromArgb(22, 21, 19);
        public Color OutlineColor = Color.FromArgb(0, 0, 0);
        public int BgOpacity = 76;
        public int Outline = 2;
        public int Corner = 12;
        public int Padding = 14;
        public bool Shadow = true;
        public bool AutoFit = true;

        public OverlayStyle Clone()
        {
            return (OverlayStyle)MemberwiseClone();
        }
    }

    public class Area
    {
        public int Id;
        public bool Enabled = true;
        public AreaTextMode Mode = AreaTextMode.Dialog;
        public Rectangle Ocr;
        public Rectangle Result;
        public OverlayStyle Style = new OverlayStyle();

        // Runtime only
        public string LastSource = "";
        public string LastText = "";
        public string LastSpeaker = "";

        public OverlayStyle EffectiveStyle(AppConfig globalCfg)
        {
            return (Style != null && Style.UseCustom) ? Style : globalCfg.GlobalStyle;
        }
    }

    public class AppConfig
    {
        public bool Dark = true;
        public int UiZoom = 100; // 75..150%
        public string ActiveProfile = "Default";
        public string Src = "en";
        public string Tgt = "id";
        public string Engine = "google"; // "google", "mymemory", "gemini"
        public string GeminiApiKey = "";
        public bool NaturalTone = true;  // Bahasa luwes / tidak kaku (Anda -> kamu, idiom game)
        public bool DetectSpeaker = true; // Deteksi tag nama pembicara (mis. "Saria: ...")
        public string Glossary = "";      // Istilah/nama karakter per game (pisah ';' atau baris baru)
        public int Interval = 180;
        public int AutoHideSeconds = 8;
        public bool WaitStable = false;
        public bool MergeLines = true;
        public bool HideEmpty = true;
        public bool AutoLive = false;
        public bool CloseToTray = true;
        public bool TrayTipShown = false;

        public int Placement = 0; // 0 = terpisah, 1 = tutup asli, 2 = gabung
        public Rectangle MergedRect = Rectangle.Empty;

        public OverlayStyle GlobalStyle = new OverlayStyle();

        public List<Area> Areas = new List<Area>();
        public int NextId = 1;

        public readonly object Sync = new object();

        public static bool IsPortableMode
        {
            get
            {
                try
                {
                    string exeDir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                    string installedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LingoLens");
                    if (string.Equals(exeDir.TrimEnd('\\'), installedDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return false;
                    string probe = Path.Combine(exeDir, ".tl_write_test");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return true;
                }
                catch { return false; }
            }
        }

        public static string BaseDataDir
        {
            get
            {
                if (IsPortableMode)
                    return Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LingoLens");
            }
        }

        public static string FilePath { get { return Path.Combine(BaseDataDir, "settings.ini"); } }
        public static string ProfilesDir { get { return Path.Combine(BaseDataDir, "profiles"); } }

        public Area FindArea(int id)
        {
            foreach (Area a in Areas) if (a.Id == id) return a;
            return null;
        }

        public int AreaNumber(int id)
        {
            for (int i = 0; i < Areas.Count; i++) if (Areas[i].Id == id) return i + 1;
            return 0;
        }

        public static Rectangle DefaultResultFor(Rectangle ocr)
        {
            Rectangle scr = System.Windows.Forms.Screen.FromRectangle(ocr).Bounds;
            int h = Math.Max(72, Math.Min(ocr.Height, 220));
            int top = ocr.Bottom + 12;
            if (top + h > scr.Bottom - 8) top = Math.Max(scr.Top + 8, ocr.Top - h - 12);
            return new Rectangle(ocr.X, top, Math.Max(240, ocr.Width), h);
        }

        public static void ApplyPresetTo(OverlayStyle st, int i)
        {
            if (i == 0)
            {
                st.Font = "Segoe UI Variable Display"; st.FontSize = 24; st.Bold = true; st.TextColor = Color.White;
                st.BgColor = Color.FromArgb(22, 21, 19); st.BgOpacity = 76; st.Outline = 2; st.Shadow = true; st.Corner = 12;
            }
            else if (i == 1)
            {
                st.Font = "Georgia"; st.FontSize = 22; st.Bold = true; st.TextColor = Color.FromArgb(255, 244, 218);
                st.BgColor = Color.FromArgb(28, 22, 18); st.BgOpacity = 86; st.Outline = 0; st.Shadow = true; st.Corner = 16;
            }
            else if (i == 2)
            {
                st.Font = "Bahnschrift"; st.FontSize = 24; st.Bold = true; st.TextColor = Color.FromArgb(255, 225, 77);
                st.BgColor = Color.Black; st.BgOpacity = 88; st.Outline = 2; st.Shadow = false; st.Corner = 6;
            }
            else
            {
                st.Font = "Segoe UI Variable Display"; st.FontSize = 26; st.Bold = true; st.TextColor = Color.White;
                st.BgColor = Color.FromArgb(22, 21, 19); st.BgOpacity = 0; st.Outline = 3; st.Shadow = true; st.Corner = 0;
            }
        }

        static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

        static Color ParseColor(string s, Color def)
        {
            try
            {
                s = s.Trim().TrimStart('#');
                if (s.Length != 6) return def;
                return Color.FromArgb(int.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                                      int.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                                      int.Parse(s.Substring(4, 2), NumberStyles.HexNumber));
            }
            catch { return def; }
        }

        static string R(Rectangle r) { return r.X + "," + r.Y + "," + r.Width + "," + r.Height; }

        static Rectangle ParseRect(string s)
        {
            try
            {
                string[] p = s.Split(',');
                return new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            }
            catch { return Rectangle.Empty; }
        }

        static string SerializeStyle(OverlayStyle s)
        {
            return (s.UseCustom ? 1 : 0) + ";" + s.Font.Replace(';', ' ') + ";" + s.FontSize + ";" + (s.Bold ? 1 : 0) + ";" +
                   s.Align + ";" + Hex(s.TextColor) + ";" + Hex(s.BgColor) + ";" + Hex(s.OutlineColor) + ";" +
                   s.BgOpacity + ";" + s.Outline + ";" + s.Corner + ";" + s.Padding + ";" + (s.Shadow ? 1 : 0) + ";" + (s.AutoFit ? 1 : 0);
        }

        static OverlayStyle ParseStyle(string raw, OverlayStyle fallback)
        {
            OverlayStyle s = fallback.Clone();
            try
            {
                string[] p = raw.Split(';');
                if (p.Length < 14) return s;
                s.UseCustom = p[0] != "0";
                if (!string.IsNullOrEmpty(p[1])) s.Font = p[1];
                int n;
                if (int.TryParse(p[2], out n)) s.FontSize = Math.Max(10, Math.Min(64, n));
                s.Bold = p[3] != "0";
                if (int.TryParse(p[4], out n)) s.Align = Math.Max(0, Math.Min(2, n));
                s.TextColor = ParseColor(p[5], s.TextColor);
                s.BgColor = ParseColor(p[6], s.BgColor);
                s.OutlineColor = ParseColor(p[7], s.OutlineColor);
                if (int.TryParse(p[8], out n)) s.BgOpacity = Math.Max(0, Math.Min(100, n));
                if (int.TryParse(p[9], out n)) s.Outline = Math.Max(0, Math.Min(8, n));
                if (int.TryParse(p[10], out n)) s.Corner = Math.Max(0, Math.Min(32, n));
                if (int.TryParse(p[11], out n)) s.Padding = Math.Max(4, Math.Min(40, n));
                s.Shadow = p[12] != "0";
                s.AutoFit = p[13] != "0";
            }
            catch { }
            return s;
        }

        public void Save() { SaveToFile(FilePath); }

        public void SaveToFile(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (Sync)
                {
                    sb.AppendLine("dark=" + (Dark ? 1 : 0));
                    sb.AppendLine("zoom=" + UiZoom);
                    sb.AppendLine("profile=" + ActiveProfile);
                    sb.AppendLine("src=" + Src);
                    sb.AppendLine("tgt=" + Tgt);
                    sb.AppendLine("engine=" + Engine);
                    sb.AppendLine("geminiKey=" + GeminiApiKey);
                    sb.AppendLine("natural=" + (NaturalTone ? 1 : 0));
                    sb.AppendLine("detectSpeaker=" + (DetectSpeaker ? 1 : 0));
                    sb.AppendLine("glossary=" + (Glossary ?? "").Replace("\r", "").Replace("\n", ";"));
                    sb.AppendLine("interval=" + Interval);
                    sb.AppendLine("autoHideSec=" + AutoHideSeconds);
                    sb.AppendLine("waitStable=" + (WaitStable ? 1 : 0));
                    sb.AppendLine("mergeLines=" + (MergeLines ? 1 : 0));
                    sb.AppendLine("hideEmpty=" + (HideEmpty ? 1 : 0));
                    sb.AppendLine("autoLive=" + (AutoLive ? 1 : 0));
                    sb.AppendLine("closeToTray=" + (CloseToTray ? 1 : 0));
                    sb.AppendLine("trayTip=" + (TrayTipShown ? 1 : 0));
                    sb.AppendLine("placement=" + Placement);
                    sb.AppendLine("merged=" + R(MergedRect));
                    sb.AppendLine("globalStyle=" + SerializeStyle(GlobalStyle));
                    sb.AppendLine("nextId=" + NextId);
                    foreach (Area a in Areas)
                    {
                        sb.AppendLine("area=" + a.Id + "|" + (a.Enabled ? 1 : 0) + "|" + R(a.Ocr) + "|" + R(a.Result) + "|" + (int)a.Mode + "|" + SerializeStyle(a.Style));
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static AppConfig Load() { return LoadFromFile(FilePath); }

        public static AppConfig LoadFromFile(string path)
        {
            AppConfig c = new AppConfig();
            try
            {
                if (!File.Exists(path)) return c;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    int n;
                    int.TryParse(v, out n);
                    switch (k)
                    {
                        case "dark": c.Dark = n != 0; break;
                        case "zoom": c.UiZoom = Math.Max(75, Math.Min(150, n)); break;
                        case "profile": if (!string.IsNullOrEmpty(v)) c.ActiveProfile = v; break;
                        case "src": c.Src = v; break;
                        case "tgt": c.Tgt = v; break;
                        case "engine": c.Engine = v; break;
                        case "geminiKey": c.GeminiApiKey = v; break;
                        case "natural": c.NaturalTone = n != 0; break;
                        case "detectSpeaker": c.DetectSpeaker = n != 0; break;
                        case "glossary": c.Glossary = v; break;
                        case "interval": c.Interval = Math.Max(100, Math.Min(3000, n)); break;
                        case "autoHideSec": c.AutoHideSeconds = Math.Max(0, Math.Min(60, n)); break;
                        case "waitStable": c.WaitStable = n != 0; break;
                        case "mergeLines": c.MergeLines = n != 0; break;
                        case "hideEmpty": c.HideEmpty = n != 0; break;
                        case "autoLive": c.AutoLive = n != 0; break;
                        case "closeToTray": c.CloseToTray = n != 0; break;
                        case "trayTip": c.TrayTipShown = n != 0; break;
                        case "placement": c.Placement = Math.Max(0, Math.Min(2, n)); break;
                        case "merged": c.MergedRect = ParseRect(v); break;
                        case "globalStyle": c.GlobalStyle = ParseStyle(v, c.GlobalStyle); break;
                        case "nextId": c.NextId = Math.Max(1, n); break;
                        case "area":
                            string[] p = v.Split('|');
                            if (p.Length >= 4)
                            {
                                Area a = new Area();
                                int.TryParse(p[0], out a.Id);
                                a.Enabled = p[1] != "0";
                                a.Ocr = ParseRect(p[2]);
                                a.Result = ParseRect(p[3]);
                                if (p.Length >= 5) { int m; int.TryParse(p[4], out m); a.Mode = (m == 1) ? AreaTextMode.Choice : AreaTextMode.Dialog; }
                                if (p.Length >= 6) a.Style = ParseStyle(p[5], c.GlobalStyle);
                                if (a.Ocr.Width > 8 && a.Ocr.Height > 8)
                                {
                                    if (a.Result.Width < 20 || a.Result.Height < 20) a.Result = DefaultResultFor(a.Ocr);
                                    c.Areas.Add(a);
                                    if (a.Id >= c.NextId) c.NextId = a.Id + 1;
                                }
                            }
                            break;
                    }
                }
            }
            catch { }
            return c;
        }

        // Game Template / Preset Management
        public static List<string> ListGameProfiles()
        {
            List<string> list = new List<string>();
            try
            {
                if (Directory.Exists(ProfilesDir))
                {
                    foreach (string f in Directory.GetFiles(ProfilesDir, "*.ini"))
                        list.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            catch { }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static string SanitizeProfileName(string name)
        {
            string s = (name ?? "").Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return string.IsNullOrEmpty(s) ? "Game Baru" : s;
        }

        public void SaveGameProfile(string gameTitle)
        {
            string clean = SanitizeProfileName(gameTitle);
            ActiveProfile = clean;
            string p = Path.Combine(ProfilesDir, clean + ".ini");
            SaveToFile(p);
            Save();
        }

        public bool LoadGameProfile(string gameTitle)
        {
            string p = Path.Combine(ProfilesDir, SanitizeProfileName(gameTitle) + ".ini");
            if (!File.Exists(p)) return false;
            AppConfig loaded = LoadFromFile(p);
            lock (Sync)
            {
                ActiveProfile = SanitizeProfileName(gameTitle);
                Src = loaded.Src;
                Tgt = loaded.Tgt;
                Engine = loaded.Engine;
                DetectSpeaker = loaded.DetectSpeaker;
                Glossary = loaded.Glossary;
                Placement = loaded.Placement;
                MergedRect = loaded.MergedRect;
                GlobalStyle = loaded.GlobalStyle.Clone();
                Areas = new List<Area>(loaded.Areas);
                NextId = loaded.NextId;
            }
            Save();
            return true;
        }

        public void DeleteGameProfile(string gameTitle)
        {
            try
            {
                string p = Path.Combine(ProfilesDir, SanitizeProfileName(gameTitle) + ".ini");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }
    }
}
