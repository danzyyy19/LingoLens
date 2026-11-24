using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TranslatorLive
{
    public enum IconKind
    {
        None = 0,
        Play,
        Stop,
        Plus,
        EditBox,
        Refresh,
        Eye,
        Sun,
        Moon,
        Tray,
        Minimize,
        Maximize,
        Restore,
        Fullscreen,
        Close,
        Trash,
        Snap,
        Crop,
        ResultBox,
        Swap,
        Copy,
        Check,
        Sliders,
        Palette,
        Layers,
        Sparkle,
        ZoomIn,
        ZoomOut,
        Gamepad,
        Save,
        History,
        Settings,
        ScanArea,
        Rocket,
        Download,
        Book,
        Search,
        Shield,
        Pattern,
        WindowLock,
        Target
    }

    public class Palette
    {
        public bool Dark;
        public Color Bg;
        public Color Sidebar;
        public Color Card;
        public Color CardHover;
        public Color Well;
        public Color Border;
        public Color BorderStrong;
        public Color Shadow;
        public Color PatternInk;
        public Color PatternAccent;

        public Color Text;
        public Color TextSub;
        public Color TextMute;

        public Color Accent;
        public Color AccentHover;
        public Color AccentSoft;
        public Color AccentText;
        public Color AccentBtnText;

        public Color Live;
        public Color LiveHover;
        public Color LiveSoft;

        public Color Danger;
        public Color DangerSoft;

        public static Palette For(bool dark)
        {
            Palette p = new Palette();
            p.Dark = dark;
            if (dark)
            {
                // Studio Editorial Dark — Matte Ink, Architectural Slate & Cinema Subtitle Yellow (Zero AI-Slop Gradients)
                p.Bg = Color.FromArgb(13, 14, 16);              // #0D0E10 matte studio ink
                p.Sidebar = Color.FromArgb(17, 19, 22);         // #111316 graphite sidebar
                p.Card = Color.FromArgb(22, 24, 28);            // #16181C elevated panel
                p.CardHover = Color.FromArgb(30, 33, 39);       // #1E2127 crisp hover
                p.Well = Color.FromArgb(15, 17, 20);            // #0F1114 recessed well
                p.Border = Color.FromArgb(38, 42, 49);          // #262A31 1px hairline border
                p.BorderStrong = Color.FromArgb(56, 62, 72);    // #383E48 structural border
                p.Shadow = Color.FromArgb(95, 0, 0, 0);
                p.PatternInk = Color.FromArgb(26, 29, 35);      // subtle architectural hatch line
                p.PatternAccent = Color.FromArgb(38, 43, 52);   // micro drafting cross tick

                p.Text = Color.FromArgb(244, 241, 234);         // #F4F1EA warm editorial off-white
                p.TextSub = Color.FromArgb(164, 171, 182);      // #A4ABB6 cool silver subtext
                p.TextMute = Color.FromArgb(110, 118, 130);     // #6E7682 muted caption

                p.Accent = Color.FromArgb(255, 210, 63);        // #FFD23F cinema subtitle yellow
                p.AccentHover = Color.FromArgb(238, 192, 46);   // #EEC02E warm press/hover
                p.AccentSoft = Color.FromArgb(44, 38, 18);      // warm muted amber tint
                p.AccentText = Color.FromArgb(255, 210, 63);    // #FFD23F
                p.AccentBtnText = Color.FromArgb(13, 14, 16);   // #0D0E10 AAA contrast dark ink

                p.Live = Color.FromArgb(61, 219, 192);          // #3DDBC0 crisp teal signal
                p.LiveHover = Color.FromArgb(43, 194, 168);
                p.LiveSoft = Color.FromArgb(28, 61, 219, 192);

                p.Danger = Color.FromArgb(255, 77, 61);         // #FF4D3D editorial vermilion
                p.DangerSoft = Color.FromArgb(28, 255, 77, 61);
            }
            else
            {
                // Studio Editorial Light — Warm Drafting Paper, Crisp Ink & Brass Signal
                p.Bg = Color.FromArgb(243, 240, 232);           // #F3F0E8 warm architectural paper
                p.Sidebar = Color.FromArgb(250, 248, 243);      // #FAF8F3 light ivory sidebar
                p.Card = Color.FromArgb(255, 255, 255);         // #FFFFFF pure card surface
                p.CardHover = Color.FromArgb(239, 236, 228);    // #EFECE4 warm hover
                p.Well = Color.FromArgb(234, 230, 220);         // #EAE6DC recessed paper well
                p.Border = Color.FromArgb(218, 212, 199);       // #DAD4C7 hairline paper border
                p.BorderStrong = Color.FromArgb(192, 184, 168); // #C0B8A8 structural rule
                p.Shadow = Color.FromArgb(14, 21, 23, 26);
                p.PatternInk = Color.FromArgb(228, 223, 212);   // subtle drafting hatch
                p.PatternAccent = Color.FromArgb(208, 201, 186);

                p.Text = Color.FromArgb(21, 23, 26);            // #15171A sharp studio ink
                p.TextSub = Color.FromArgb(90, 96, 106);        // #5A606A drafting slate
                p.TextMute = Color.FromArgb(138, 144, 154);     // #8A909A muted caption

                p.Accent = Color.FromArgb(21, 23, 26);          // #15171A crisp ink button
                p.AccentHover = Color.FromArgb(42, 46, 54);
                p.AccentSoft = Color.FromArgb(248, 238, 204);   // subtle warm yellow highlight pill
                p.AccentText = Color.FromArgb(180, 118, 0);     // deep brass amber readable on paper
                p.AccentBtnText = Color.FromArgb(255, 210, 63); // subtitle yellow text on ink button

                p.Live = Color.FromArgb(15, 157, 132);          // crisp deep teal
                p.LiveHover = Color.FromArgb(11, 130, 109);
                p.LiveSoft = Color.FromArgb(24, 15, 157, 132);

                p.Danger = Color.FromArgb(220, 38, 38);
                p.DangerSoft = Color.FromArgb(24, 220, 38, 38);
            }
            return p;
        }
    }

    public static class Theme
    {
        private static string _uiFontName;
        private static string _uiBoldFontName;
        private static string _displayFontName;

        static Theme()
        {
            _uiFontName = PickFont("Segoe UI Variable Text", "Segoe UI");
            _uiBoldFontName = PickFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
            _displayFontName = PickFont("Segoe UI Variable Display Semib", "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI");
        }

        private static string PickFont(params string[] candidates)
        {
            foreach (string preferred in candidates)
            {
                foreach (FontFamily ff in FontFamily.Families)
                {
                    if (string.Equals(ff.Name, preferred, StringComparison.OrdinalIgnoreCase)) return ff.Name;
                }
            }
            return "Segoe UI";
        }

        public static Font SerifFont(float px, bool bold)
        {
            bool isSemiboldName = _displayFontName.IndexOf("Semibold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  _displayFontName.IndexOf("Semib", StringComparison.OrdinalIgnoreCase) >= 0;
            return new Font(_displayFontName, px, (bold && !isSemiboldName) ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static Font UiFont(float px, bool bold)
        {
            if (bold)
            {
                bool isSemiboldName = _uiBoldFontName.IndexOf("Semibold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      _uiBoldFontName.IndexOf("Semib", StringComparison.OrdinalIgnoreCase) >= 0;
                return new Font(_uiBoldFontName, px, isSemiboldName ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Pixel);
            }
            return new Font(_uiFontName, px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static GraphicsPath RoundPath(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad <= 0.5f || r.Width <= 2f || r.Height <= 2f) { p.AddRectangle(r); return p; }
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static void FillRR(Graphics g, RectangleF r, float rad, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0 || c.A == 0) return;
            using (GraphicsPath p = RoundPath(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void FillGradRR(Graphics g, RectangleF r, float rad, Color top, Color bot, float angle = 90f)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            using (GraphicsPath p = RoundPath(r, rad))
            using (LinearGradientBrush b = new LinearGradientBrush(r, top, bot, angle)) g.FillPath(b, p);
        }

        public static void DrawRR(Graphics g, RectangleF r, float rad, Color c, float w = 1f)
        {
            if (r.Width <= 0 || r.Height <= 0 || c.A == 0) return;
            using (GraphicsPath p = RoundPath(r, rad))
            using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        private static Bitmap _cachedPatternTile;
        private static TextureBrush _cachedPatternBrush;
        private static int _cachedPatternKey = -1;

        public static void DrawCanvasBackground(Graphics g, Palette pal, RectangleF bounds, int patternMode)
        {
            using (SolidBrush bg = new SolidBrush(pal.Bg))
                g.FillRectangle(bg, bounds);

            if (patternMode == 3) return; // Polos

            int key = (pal.Dark ? 100 : 0) + patternMode;
            if (_cachedPatternBrush == null || _cachedPatternKey != key)
            {
                if (_cachedPatternBrush != null) { _cachedPatternBrush.Dispose(); _cachedPatternBrush = null; }
                if (_cachedPatternTile != null) { _cachedPatternTile.Dispose(); _cachedPatternTile = null; }

                int tileSz = (patternMode == 0) ? 16 : (patternMode == 1 ? 24 : 18);
                Bitmap tile = new Bitmap(tileSz, tileSz, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics tg = Graphics.FromImage(tile))
                {
                    tg.Clear(Color.Transparent);
                    if (patternMode == 0)
                    {
                        // Arsiran Diagonal 45° — Crisp Architectural Hatch (Zero AI-Slop Gradient)
                        tg.SmoothingMode = SmoothingMode.None;
                        using (Pen hp = new Pen(pal.PatternInk, 1f))
                        {
                            // Seamless 45-degree diagonal lines across 16x16 tile
                            tg.DrawLine(hp, 0, tileSz - 1, tileSz - 1, 0);
                            tg.DrawLine(hp, -tileSz, tileSz - 1, tileSz - 1, -tileSz);
                            tg.DrawLine(hp, 0, tileSz * 2 - 1, tileSz * 2 - 1, 0);
                        }
                        // Tiny architectural dot at corner intersection
                        using (SolidBrush db = new SolidBrush(pal.PatternAccent))
                            tg.FillRectangle(db, 0, 0, 1, 1);
                    }
                    else if (patternMode == 1)
                    {
                        // Blueprint Drafting Grid with precision crosshairs
                        tg.SmoothingMode = SmoothingMode.None;
                        using (Pen gp = new Pen(pal.PatternInk, 1f))
                        {
                            tg.DrawLine(gp, 0, 0, tileSz, 0);
                            tg.DrawLine(gp, 0, 0, 0, tileSz);
                        }
                        using (SolidBrush cb = new SolidBrush(pal.PatternAccent))
                        {
                            tg.FillRectangle(cb, 0, 0, 3, 1);
                            tg.FillRectangle(cb, 0, 0, 1, 3);
                        }
                    }
                    else if (patternMode == 2)
                    {
                        // Precision Dot Matrix
                        using (SolidBrush db = new SolidBrush(pal.PatternAccent))
                            tg.FillRectangle(db, 2, 2, 2, 2);
                    }
                }
                _cachedPatternTile = tile;
                _cachedPatternBrush = new TextureBrush(tile, WrapMode.Tile);
                _cachedPatternKey = key;
            }

            g.FillRectangle(_cachedPatternBrush, bounds);
        }

        public static void DrawStudioStage(Graphics g, Palette pal, RectangleF r, float rad)
        {
            // Clean architectural matte cinema stage with fine crosshatch (replaces gradient slop)
            Color baseCol = pal.Dark ? Color.FromArgb(10, 11, 13) : Color.FromArgb(24, 26, 30);
            Color hatchCol = Color.FromArgb(24, 255, 255, 255);
            FillRR(g, r, rad, baseCol);

            GraphicsState gs = g.Save();
            try
            {
                using (GraphicsPath clipP = RoundPath(r, rad))
                {
                    g.SetClip(clipP);
                    using (HatchBrush hb = new HatchBrush(HatchStyle.LightUpwardDiagonal, hatchCol, Color.Transparent))
                        g.FillRectangle(hb, r);
                    // Subtle cinema frame corner brackets inside preview stage
                    using (Pen cp = new Pen(Color.FromArgb(65, 255, 210, 63), 1.4f))
                    {
                        float m = 12f, cl = 10f;
                        g.DrawLine(cp, r.X + m, r.Y + m + cl, r.X + m, r.Y + m);
                        g.DrawLine(cp, r.X + m, r.Y + m, r.X + m + cl, r.Y + m);
                        g.DrawLine(cp, r.Right - m - cl, r.Y + m, r.Right - m, r.Y + m);
                        g.DrawLine(cp, r.Right - m, r.Y + m, r.Right - m, r.Y + m + cl);
                        g.DrawLine(cp, r.X + m, r.Bottom - m - cl, r.X + m, r.Bottom - m);
                        g.DrawLine(cp, r.X + m, r.Bottom - m, r.X + m + cl, r.Bottom - m);
                        g.DrawLine(cp, r.Right - m - cl, r.Bottom - m, r.Right - m, r.Bottom - m);
                        g.DrawLine(cp, r.Right - m, r.Bottom - m, r.Right - m, r.Bottom - m - cl);
                    }
                }
            }
            finally { g.Restore(gs); }
            DrawRR(g, r, rad, pal.BorderStrong, 1f);
        }

        public static void DrawCard(Graphics g, Palette pal, RectangleF r, float rad = 12f)
        {
            FillRR(g, new RectangleF(r.X, r.Y + 2f, r.Width, r.Height), rad, pal.Shadow);
            FillRR(g, r, rad, pal.Card);
            DrawRR(g, r, rad, pal.Border, 1f);
            if (pal.Dark && r.Width > 32f)
            {
                // Crisp subtle architectural top edge catchlight (no gradient)
                using (Pen hl = new Pen(Color.FromArgb(18, 255, 255, 255), 1f))
                    g.DrawLine(hl, r.X + rad, r.Y + 1f, r.Right - rad, r.Y + 1f);
            }
        }

        // CRISP ANTI-ALIASED TEXT RENDERING — ZERO CLEARTYPE BLACK-FRINGE BLEED ON YELLOW/LIGHT SURFACES!
        public static void DrawTextLeft(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 1f || r.Height <= 1f) return;
            TextRenderingHint prev = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush br = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None;
                g.DrawString(text, f, br, r, sf);
            }
            g.TextRenderingHint = prev;
        }

        public static void DrawTextCenter(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 1f || r.Height <= 1f) return;
            TextRenderingHint prev = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush br = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None;
                g.DrawString(text, f, br, r, sf);
            }
            g.TextRenderingHint = prev;
        }

        public static void DrawTextRight(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 1f || r.Height <= 1f) return;
            TextRenderingHint prev = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush br = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None;
                g.DrawString(text, f, br, r, sf);
            }
            g.TextRenderingHint = prev;
        }

        public static void DrawTextWrap(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text) || r.Width <= 1f || r.Height <= 1f) return;
            TextRenderingHint prev = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush br = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;
                sf.Trimming = StringTrimming.EllipsisWord;
                sf.HotkeyPrefix = System.Drawing.Text.HotkeyPrefix.None;
                g.DrawString(text, f, br, r, sf);
            }
            g.TextRenderingHint = prev;
        }

        // PREMIUM MINIMALIST VECTOR ICONS (Lucide / Feather / Figma Precision)
        public static void DrawIcon(Graphics g, IconKind k, RectangleF r, Color col, float sw = 1.6f)
        {
            if (k == IconKind.None || r.Width <= 0 || r.Height <= 0) return;

            GraphicsState gs = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            try
            {
                using (Pen p = new Pen(col, sw))
                using (SolidBrush b = new SolidBrush(col))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;

                    float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                    float cx = x + w * 0.5f, cy = y + h * 0.5f;

                    switch (k)
                    {
                        case IconKind.Play:
                            using (GraphicsPath gp = new GraphicsPath())
                            {
                                float px0 = x + w * 0.28f, px1 = r.Right - w * 0.20f;
                                float py0 = y + h * 0.18f, py1 = r.Bottom - h * 0.18f;
                                gp.AddLine(px0, py0, px1, cy);
                                gp.AddLine(px1, cy, px0, py1);
                                gp.CloseFigure();
                                p.LineJoin = LineJoin.Round;
                                g.DrawPath(p, gp);
                                g.FillPath(b, gp);
                            }
                            break;

                        case IconKind.Stop:
                            FillRR(g, new RectangleF(x + w * 0.22f, y + h * 0.22f, w * 0.56f, h * 0.56f), 3.5f, col);
                            break;

                        case IconKind.Plus:
                            float pPad = w * 0.24f;
                            g.DrawLine(p, cx, y + pPad, cx, r.Bottom - pPad);
                            g.DrawLine(p, x + pPad, cy, r.Right - pPad, cy);
                            break;

                        case IconKind.ScanArea:
                            // Tactical Gaming Sniper Reticle & Corner HUD Brackets
                            float cl2 = w * 0.25f;
                            float scx0 = x + w * 0.10f, scx1 = r.Right - w * 0.10f;
                            float scy0 = y + h * 0.10f, scy1 = r.Bottom - h * 0.10f;
                            g.DrawLine(p, scx0, scy0 + cl2, scx0, scy0); g.DrawLine(p, scx0, scy0, scx0 + cl2, scy0);
                            g.DrawLine(p, scx1 - cl2, scy0, scx1, scy0); g.DrawLine(p, scx1, scy0, scx1, scy0 + cl2);
                            g.DrawLine(p, scx0, scy1 - cl2, scx0, scy1); g.DrawLine(p, scx0, scy1, scx0 + cl2, scy1);
                            g.DrawLine(p, scx1 - cl2, scy1, scx1, scy1); g.DrawLine(p, scx1, scy1, scx1, scy1 - cl2);
                            float cr = w * 0.18f;
                            g.DrawEllipse(p, cx - cr, cy - cr, cr * 2f, cr * 2f);
                            g.FillEllipse(b, cx - w * 0.06f, cy - h * 0.06f, w * 0.12f, h * 0.12f);
                            break;

                        case IconKind.EditBox:
                            // Interactive Selection Bounding Box with 4 Solid Transform Anchor Handles
                            RectangleF eBox = new RectangleF(x + w * 0.20f, y + h * 0.20f, w * 0.60f, h * 0.60f);
                            DrawRR(g, eBox, 1.5f, col, sw * 0.9f);
                            float hSz = Math.Max(3f, w * 0.12f);
                            FillRR(g, new RectangleF(eBox.X - hSz * 0.5f, eBox.Y - hSz * 0.5f, hSz, hSz), 1f, col);
                            FillRR(g, new RectangleF(eBox.Right - hSz * 0.5f, eBox.Y - hSz * 0.5f, hSz, hSz), 1f, col);
                            FillRR(g, new RectangleF(eBox.X - hSz * 0.5f, eBox.Bottom - hSz * 0.5f, hSz, hSz), 1f, col);
                            FillRR(g, new RectangleF(eBox.Right - hSz * 0.5f, eBox.Bottom - hSz * 0.5f, hSz, hSz), 1f, col);
                            break;

                        case IconKind.ResultBox:
                            // Subtitle Speech Balloon with Subtitle Dialogue Lines
                            RectangleF sb = new RectangleF(x + w * 0.13f, y + h * 0.14f, w * 0.74f, h * 0.52f);
                            DrawRR(g, sb, 3f, col, sw);
                            PointF[] tail = new PointF[] {
                                new PointF(sb.X + w * 0.14f, sb.Bottom),
                                new PointF(sb.X + w * 0.06f, r.Bottom - h * 0.14f),
                                new PointF(sb.X + w * 0.28f, sb.Bottom)
                            };
                            g.DrawLines(p, tail);
                            g.DrawLine(p, sb.X + w * 0.12f, cy - h * 0.06f, sb.Right - w * 0.12f, cy - h * 0.06f);
                            g.DrawLine(p, sb.X + w * 0.12f, cy + h * 0.08f, sb.X + w * 0.44f, cy + h * 0.08f);
                            break;

                        case IconKind.Snap:
                            // Precision Alignment Guide with Magnetic Snapping Guides
                            g.DrawLine(p, cx, y + h * 0.12f, cx, r.Bottom - h * 0.12f);
                            DrawRR(g, new RectangleF(x + w * 0.14f, cy - h * 0.20f, w * 0.26f, h * 0.40f), 2f, col, sw);
                            DrawRR(g, new RectangleF(cx + w * 0.10f, cy - h * 0.20f, w * 0.26f, h * 0.40f), 2f, col, sw);
                            break;

                        case IconKind.Crop:
                            // Precision Viewfinder Target: 4 sleek corner brackets + center crosshair
                            float cLen = w * 0.22f;
                            float x0 = x + w * 0.14f, x1 = r.Right - w * 0.14f;
                            float y0 = y + h * 0.14f, y1 = r.Bottom - h * 0.14f;
                            g.DrawLine(p, x0, y0 + cLen, x0, y0); g.DrawLine(p, x0, y0, x0 + cLen, y0);
                            g.DrawLine(p, x1 - cLen, y0, x1, y0); g.DrawLine(p, x1, y0, x1, y0 + cLen);
                            g.DrawLine(p, x0, y1 - cLen, x0, y1); g.DrawLine(p, x0, y1, x0 + cLen, y1);
                            g.DrawLine(p, x1 - cLen, y1, x1, y1); g.DrawLine(p, x1, y1, x1, y1 - cLen);
                            g.DrawLine(p, cx - w * 0.12f, cy, cx + w * 0.12f, cy);
                            g.DrawLine(p, cx, cy - h * 0.12f, cx, cy + h * 0.12f);
                            g.FillEllipse(b, cx - w * 0.04f, cy - h * 0.04f, w * 0.08f, h * 0.08f);
                            break;

                        case IconKind.Trash:
                            // Modern Lucide Trash Can
                            g.DrawLine(p, x + w * 0.14f, y + h * 0.24f, r.Right - w * 0.14f, y + h * 0.24f);
                            DrawRR(g, new RectangleF(cx - w * 0.10f, y + h * 0.14f, w * 0.20f, h * 0.10f), 1f, col, sw);
                            PointF[] can = new PointF[]
                            {
                                new PointF(x + w * 0.22f, y + h * 0.24f),
                                new PointF(r.Right - w * 0.22f, y + h * 0.24f),
                                new PointF(r.Right - w * 0.26f, r.Bottom - h * 0.15f),
                                new PointF(x + w * 0.26f, r.Bottom - h * 0.15f)
                            };
                            g.DrawPolygon(p, can);
                            g.DrawLine(p, cx - w * 0.08f, y + h * 0.36f, cx - w * 0.08f, r.Bottom - h * 0.24f);
                            g.DrawLine(p, cx + w * 0.08f, y + h * 0.36f, cx + w * 0.08f, r.Bottom - h * 0.24f);
                            break;

                        case IconKind.Gamepad:
                            // Pro Gaming Controller with Sculpted Grips, Bold D-Pad & 4 Diamond Action Buttons
                            using (GraphicsPath gp = new GraphicsPath())
                            {
                                gp.AddBezier(x + w * 0.28f, y + h * 0.22f, cx - w * 0.10f, y + h * 0.22f, cx + w * 0.10f, y + h * 0.22f, r.Right - w * 0.28f, y + h * 0.22f);
                                gp.AddBezier(r.Right - w * 0.28f, y + h * 0.22f, r.Right - w * 0.08f, y + h * 0.22f, r.Right - w * 0.06f, r.Bottom - h * 0.16f, r.Right - w * 0.20f, r.Bottom - h * 0.16f);
                                gp.AddLine(r.Right - w * 0.20f, r.Bottom - h * 0.16f, cx + w * 0.14f, cy + h * 0.14f);
                                gp.AddLine(cx + w * 0.14f, cy + h * 0.14f, cx - w * 0.14f, cy + h * 0.14f);
                                gp.AddLine(cx - w * 0.14f, cy + h * 0.14f, x + w * 0.20f, r.Bottom - h * 0.16f);
                                gp.AddBezier(x + w * 0.20f, r.Bottom - h * 0.16f, x + w * 0.06f, r.Bottom - h * 0.16f, x + w * 0.08f, y + h * 0.22f, x + w * 0.28f, y + h * 0.22f);
                                gp.CloseFigure();
                                g.DrawPath(p, gp);
                            }
                            float dpx = x + w * 0.30f, dpy = cy - h * 0.04f;
                            g.DrawLine(p, dpx - w * 0.09f, dpy, dpx + w * 0.09f, dpy);
                            g.DrawLine(p, dpx, dpy - h * 0.09f, dpx, dpy + h * 0.09f);
                            float abx = r.Right - w * 0.30f, aby = cy - h * 0.04f;
                            float bsz = Math.Max(2.2f, w * 0.085f);
                            g.FillEllipse(b, abx - bsz * 0.5f, aby - h * 0.09f - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx - bsz * 0.5f, aby + h * 0.09f - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx - w * 0.09f - bsz * 0.5f, aby - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx + w * 0.09f - bsz * 0.5f, aby - bsz * 0.5f, bsz, bsz);
                            break;

                        case IconKind.Swap:
                            // Clean Dual Horizontal Directional Arrows
                            float sy1 = cy - h * 0.14f, sy2 = cy + h * 0.14f;
                            float sah = h * 0.10f;
                            g.DrawLine(p, x + w * 0.16f, sy1, r.Right - w * 0.16f, sy1);
                            g.DrawLine(p, r.Right - w * 0.28f, sy1 - sah, r.Right - w * 0.16f, sy1);
                            g.DrawLine(p, r.Right - w * 0.28f, sy1 + sah, r.Right - w * 0.16f, sy1);
                            g.DrawLine(p, r.Right - w * 0.16f, sy2, x + w * 0.16f, sy2);
                            g.DrawLine(p, x + w * 0.28f, sy2 - sah, x + w * 0.16f, sy2);
                            g.DrawLine(p, x + w * 0.28f, sy2 + sah, x + w * 0.16f, sy2);
                            break;

                        case IconKind.Eye:
                            // Almond Shaped Precision Eye with Center Iris
                            using (GraphicsPath ep = new GraphicsPath())
                            {
                                ep.AddBezier(x + w * 0.10f, cy, cx - w * 0.18f, y + h * 0.18f, cx + w * 0.18f, y + h * 0.18f, r.Right - w * 0.10f, cy);
                                ep.AddBezier(r.Right - w * 0.10f, cy, cx + w * 0.18f, r.Bottom - h * 0.18f, cx - w * 0.18f, r.Bottom - h * 0.18f, x + w * 0.10f, cy);
                                g.DrawPath(p, ep);
                            }
                            g.FillEllipse(b, cx - w * 0.11f, cy - h * 0.11f, w * 0.22f, h * 0.22f);
                            break;

                        case IconKind.Sun:
                            // Sun Core + 8 Crisp Radial Rays
                            float sunR = w * 0.18f;
                            g.DrawEllipse(p, cx - sunR, cy - sunR, sunR * 2f, sunR * 2f);
                            for (int i = 0; i < 8; i++)
                            {
                                double rad = i * Math.PI / 4.0;
                                float cos = (float)Math.Cos(rad), sin = (float)Math.Sin(rad);
                                g.DrawLine(p, cx + cos * w * 0.28f, cy + sin * h * 0.28f, cx + cos * w * 0.42f, cy + sin * h * 0.42f);
                            }
                            break;

                        case IconKind.Moon:
                            // Sleek Slender Crescent Moon (Zero chunky 'C' shape)
                            using (GraphicsPath mp = new GraphicsPath())
                            {
                                PointF pTop = new PointF(cx + w * 0.14f, y + h * 0.12f);
                                PointF pBot = new PointF(cx + w * 0.14f, r.Bottom - h * 0.12f);
                                mp.AddBezier(pTop, new PointF(x + w * 0.06f, y + h * 0.22f), new PointF(x + w * 0.06f, r.Bottom - h * 0.22f), pBot);
                                mp.AddBezier(pBot, new PointF(x + w * 0.28f, r.Bottom - h * 0.32f), new PointF(x + w * 0.28f, y + h * 0.32f), pTop);
                                mp.CloseFigure();
                                g.DrawPath(p, mp);
                                g.FillPath(b, mp);
                            }
                            break;

                        case IconKind.Tray:
                            // Minimize to Tray: Window descending down into taskbar dock
                            RectangleF winR = new RectangleF(x + w * 0.15f, y + h * 0.14f, w * 0.70f, h * 0.50f);
                            DrawRR(g, winR, 2.5f, col, sw);
                            g.DrawLine(p, winR.X, winR.Y + h * 0.15f, winR.Right, winR.Y + h * 0.15f);
                            float dockY = r.Bottom - h * 0.14f;
                            g.DrawLine(p, x + w * 0.15f, dockY, r.Right - w * 0.15f, dockY);
                            float arrY1 = winR.Y + h * 0.22f, arrY2 = dockY - h * 0.05f;
                            g.DrawLine(p, cx, arrY1, cx, arrY2);
                            g.DrawLine(p, cx - w * 0.12f, arrY2 - h * 0.10f, cx, arrY2);
                            g.DrawLine(p, cx + w * 0.12f, arrY2 - h * 0.10f, cx, arrY2);
                            break;

                        case IconKind.Sparkle:
                            // Gemini Concave 4-Point AI Star
                            using (GraphicsPath sp = new GraphicsPath())
                            {
                                float outR = w * 0.42f, inR = w * 0.10f;
                                sp.AddLine(cx, cy - outR, cx + inR, cy - inR);
                                sp.AddLine(cx + inR, cy - inR, cx + outR, cy);
                                sp.AddLine(cx + outR, cy, cx + inR, cy + inR);
                                sp.AddLine(cx + inR, cy + inR, cx, cy + outR);
                                sp.AddLine(cx, cy + outR, cx - inR, cy + inR);
                                sp.AddLine(cx - inR, cy + inR, cx - outR, cy);
                                sp.AddLine(cx - outR, cy, cx - inR, cy - inR);
                                sp.CloseFigure();
                                g.FillPath(b, sp);
                            }
                            break;

                        case IconKind.ZoomIn:
                        case IconKind.ZoomOut:
                            // Crisp Magnifying Glass with Badge Indicator
                            float lr = w * 0.26f;
                            float lcx = x + w * 0.16f + lr, lcy = y + h * 0.16f + lr;
                            g.DrawEllipse(p, lcx - lr, lcy - lr, lr * 2f, lr * 2f);
                            g.DrawLine(p, lcx + lr * 0.707f, lcy + lr * 0.707f, r.Right - w * 0.14f, r.Bottom - h * 0.14f);
                            g.DrawLine(p, lcx - lr * 0.45f, lcy, lcx + lr * 0.45f, lcy);
                            if (k == IconKind.ZoomIn)
                                g.DrawLine(p, lcx, lcy - lr * 0.45f, lcx, lcy + lr * 0.45f);
                            break;

                        case IconKind.Save:
                            // Floppy Disk with Shutter Notch and Label
                            DrawRR(g, new RectangleF(x + w * 0.15f, y + h * 0.15f, w * 0.70f, h * 0.70f), 2.5f, col, sw);
                            g.DrawRectangle(p, x + w * 0.30f, y + h * 0.15f, w * 0.40f, h * 0.24f);
                            g.DrawLine(p, x + w * 0.38f, y + h * 0.19f, x + w * 0.38f, y + h * 0.33f);
                            DrawRR(g, new RectangleF(x + w * 0.25f, cy + h * 0.06f, w * 0.50f, h * 0.32f), 1.5f, col, sw);
                            break;

                        case IconKind.Refresh:
                            // Precision Dual-Arrow Circular Sync Loop (Lucide RefreshCw)
                            float rR = w * 0.32f;
                            RectangleF arcBox = new RectangleF(cx - rR, cy - rR, rR * 2f, rR * 2f);
                            g.DrawArc(p, arcBox, -65f, 130f);
                            double a65 = 65.0 * Math.PI / 180.0;
                            float ax1 = cx + (float)Math.Cos(a65) * rR;
                            float ay1 = cy + (float)Math.Sin(a65) * rR;
                            g.DrawLine(p, ax1, ay1, ax1 - w * 0.13f, ay1);
                            g.DrawLine(p, ax1, ay1, ax1, ay1 - h * 0.13f);

                            g.DrawArc(p, arcBox, 115f, 130f);
                            double a245 = 245.0 * Math.PI / 180.0;
                            float ax2 = cx + (float)Math.Cos(a245) * rR;
                            float ay2 = cy + (float)Math.Sin(a245) * rR;
                            g.DrawLine(p, ax2, ay2, ax2 + w * 0.13f, ay2);
                            g.DrawLine(p, ax2, ay2, ax2, ay2 + h * 0.13f);
                            break;

                        case IconKind.Minimize:
                            g.DrawLine(p, x + w * 0.20f, cy, r.Right - w * 0.20f, cy);
                            break;

                        case IconKind.Maximize:
                            DrawRR(g, new RectangleF(x + w * 0.20f, y + h * 0.20f, w * 0.60f, h * 0.60f), 2f, col, sw);
                            break;

                        case IconKind.Restore:
                            DrawRR(g, new RectangleF(x + w * 0.18f, y + h * 0.30f, w * 0.52f, h * 0.52f), 1.5f, col, sw);
                            g.DrawLine(p, x + w * 0.30f, y + h * 0.18f, r.Right - w * 0.18f, y + h * 0.18f);
                            g.DrawLine(p, r.Right - w * 0.18f, y + h * 0.18f, r.Right - w * 0.18f, r.Bottom - h * 0.30f);
                            break;

                        case IconKind.Fullscreen:
                            float fcl = w * 0.20f;
                            g.DrawLine(p, x + w * 0.14f, y + h * 0.14f + fcl, x + w * 0.14f, y + h * 0.14f); g.DrawLine(p, x + w * 0.14f, y + h * 0.14f, x + w * 0.14f + fcl, y + h * 0.14f);
                            g.DrawLine(p, r.Right - w * 0.14f - fcl, y + h * 0.14f, r.Right - w * 0.14f, y + h * 0.14f); g.DrawLine(p, r.Right - w * 0.14f, y + h * 0.14f, r.Right - w * 0.14f, y + h * 0.14f + fcl);
                            g.DrawLine(p, x + w * 0.14f, r.Bottom - h * 0.14f - fcl, x + w * 0.14f, r.Bottom - h * 0.14f); g.DrawLine(p, x + w * 0.14f, r.Bottom - h * 0.14f, x + w * 0.14f + fcl, r.Bottom - h * 0.14f);
                            g.DrawLine(p, r.Right - w * 0.14f - fcl, r.Bottom - h * 0.14f, r.Right - w * 0.14f, r.Bottom - h * 0.14f); g.DrawLine(p, r.Right - w * 0.14f, r.Bottom - h * 0.14f, r.Right - w * 0.14f, r.Bottom - h * 0.14f - fcl);
                            break;

                        case IconKind.Close:
                            g.DrawLine(p, x + w * 0.22f, y + h * 0.22f, r.Right - w * 0.22f, r.Bottom - h * 0.22f);
                            g.DrawLine(p, r.Right - w * 0.22f, y + h * 0.22f, x + w * 0.22f, r.Bottom - h * 0.22f);
                            break;

                        case IconKind.Copy:
                            DrawRR(g, new RectangleF(x + w * 0.30f, y + h * 0.30f, w * 0.54f, h * 0.54f), 2f, col, sw);
                            PointF[] bs = new PointF[] { new PointF(x + w * 0.16f, r.Bottom - h * 0.32f), new PointF(x + w * 0.16f, y + h * 0.16f), new PointF(r.Right - w * 0.32f, y + h * 0.16f) };
                            g.DrawLines(p, bs);
                            break;

                        case IconKind.Check:
                            g.DrawLine(p, x + w * 0.18f, cy + h * 0.02f, cx - w * 0.06f, r.Bottom - h * 0.24f);
                            g.DrawLine(p, cx - w * 0.06f, r.Bottom - h * 0.24f, r.Right - w * 0.16f, y + h * 0.22f);
                            break;

                        case IconKind.Layers:
                            PointF[] ld1 = new PointF[] { new PointF(cx, y + h * 0.14f), new PointF(r.Right - w * 0.14f, cy - h * 0.10f), new PointF(cx, cy + h * 0.08f), new PointF(x + w * 0.14f, cy - h * 0.10f) };
                            g.DrawPolygon(p, ld1);
                            g.DrawLine(p, x + w * 0.14f, cy + h * 0.08f, cx, cy + h * 0.26f);
                            g.DrawLine(p, cx, cy + h * 0.26f, r.Right - w * 0.14f, cy + h * 0.08f);
                            g.DrawLine(p, x + w * 0.14f, cy + h * 0.26f, cx, r.Bottom - h * 0.12f);
                            g.DrawLine(p, cx, r.Bottom - h * 0.12f, r.Right - w * 0.14f, cy + h * 0.26f);
                            break;

                        case IconKind.Palette:
                            // Gaming HUD Visor / Subtitle Display Frame (Replaces generic paint palette!)
                            RectangleF hudMon = new RectangleF(x + w * 0.08f, y + h * 0.14f, w * 0.84f, h * 0.56f);
                            DrawRR(g, hudMon, 3f, col, sw);
                            // Stand base
                            g.DrawLine(p, cx, hudMon.Bottom, cx, r.Bottom - h * 0.14f);
                            g.DrawLine(p, cx - w * 0.24f, r.Bottom - h * 0.14f, cx + w * 0.24f, r.Bottom - h * 0.14f);
                            // Bold subtitle bars inside visor
                            g.DrawLine(p, hudMon.X + w * 0.14f, hudMon.Bottom - h * 0.24f, hudMon.Right - w * 0.14f, hudMon.Bottom - h * 0.24f);
                            g.DrawLine(p, hudMon.X + w * 0.24f, hudMon.Bottom - h * 0.11f, hudMon.Right - w * 0.24f, hudMon.Bottom - h * 0.11f);
                            break;

                        case IconKind.Settings:
                            // Gaming Tactical Hex-Core / Hardware Chipset (Replaces generic office gear!)
                            float hr = w * 0.36f;
                            PointF[] hexPts = new PointF[6];
                            for (int i = 0; i < 6; i++)
                            {
                                double ang = (i * 60.0 - 30.0) * Math.PI / 180.0;
                                hexPts[i] = new PointF(cx + (float)Math.Cos(ang) * hr, cy + (float)Math.Sin(ang) * hr);
                            }
                            g.DrawPolygon(p, hexPts);
                            float coreR = w * 0.14f;
                            g.DrawEllipse(p, cx - coreR, cy - coreR, coreR * 2f, coreR * 2f);
                            g.FillEllipse(b, cx - w * 0.05f, cy - h * 0.05f, w * 0.10f, h * 0.10f);
                            break;

                        case IconKind.Rocket:
                            // Gaming Level-Up / Tactical Upgrade Chevrons inside Hex Badge
                            g.DrawLine(p, x + w * 0.22f, cy - h * 0.02f, cx, y + h * 0.18f);
                            g.DrawLine(p, cx, y + h * 0.18f, r.Right - w * 0.22f, cy - h * 0.02f);
                            g.DrawLine(p, x + w * 0.22f, cy + h * 0.22f, cx, cy);
                            g.DrawLine(p, cx, cy, r.Right - w * 0.22f, cy + h * 0.22f);
                            g.DrawLine(p, x + w * 0.28f, r.Bottom - h * 0.14f, r.Right - w * 0.28f, r.Bottom - h * 0.14f);
                            break;

                        case IconKind.Download:
                            float dsx = w / 24f, dsy = h / 24f;
                            g.DrawLine(p, cx, y + 3.5f * dsy, cx, y + 14.5f * dsy);
                            g.DrawLine(p, cx - 4f * dsx, y + 11f * dsy, cx, y + 15f * dsy);
                            g.DrawLine(p, cx, y + 15f * dsy, cx + 4f * dsx, y + 11f * dsy);
                            PointF[] trayPts = new PointF[]
                            {
                                new PointF(x + 4.5f * dsx, y + 13.5f * dsy),
                                new PointF(x + 4.5f * dsx, y + 19.5f * dsy),
                                new PointF(x + 19.5f * dsx, y + 19.5f * dsy),
                                new PointF(x + 19.5f * dsx, y + 13.5f * dsy)
                            };
                            g.DrawLines(p, trayPts);
                            break;

                        case IconKind.Sliders:
                            float ly1 = y + h * 0.26f, ly2 = cy, ly3 = r.Bottom - h * 0.26f;
                            g.DrawLine(p, x + w * 0.14f, ly1, r.Right - w * 0.14f, ly1);
                            g.DrawLine(p, x + w * 0.14f, ly2, r.Right - w * 0.14f, ly2);
                            g.DrawLine(p, x + w * 0.14f, ly3, r.Right - w * 0.14f, ly3);
                            float skr = h * 0.09f;
                            g.FillEllipse(b, x + w * 0.32f - skr, ly1 - skr, skr * 2f, skr * 2f);
                            g.FillEllipse(b, r.Right - w * 0.34f - skr, ly2 - skr, skr * 2f, skr * 2f);
                            g.FillEllipse(b, cx - skr, ly3 - skr, skr * 2f, skr * 2f);
                            break;

                        case IconKind.History:
                            // RPG Dialogue HUD Bubble with Subtitle Lines
                            RectangleF dlgBox = new RectangleF(x + w * 0.10f, y + h * 0.14f, w * 0.80f, h * 0.56f);
                            DrawRR(g, dlgBox, 3.5f, col, sw);
                            PointF[] dlgTail = new PointF[]
                            {
                                new PointF(dlgBox.X + w * 0.16f, dlgBox.Bottom),
                                new PointF(dlgBox.X + w * 0.08f, r.Bottom - h * 0.12f),
                                new PointF(dlgBox.X + w * 0.34f, dlgBox.Bottom)
                            };
                            g.DrawLines(p, dlgTail);
                            g.DrawLine(p, dlgBox.X + w * 0.14f, dlgBox.Y + h * 0.20f, dlgBox.Right - w * 0.14f, dlgBox.Y + h * 0.20f);
                            g.DrawLine(p, dlgBox.X + w * 0.14f, dlgBox.Y + h * 0.36f, dlgBox.Right - w * 0.30f, dlgBox.Y + h * 0.36f);
                            break;

                        case IconKind.Book:
                            // RPG Quest Shield & Sword Lore Crest!
                            PointF[] crest = new PointF[]
                            {
                                new PointF(cx, y + h * 0.10f),
                                new PointF(r.Right - w * 0.14f, y + h * 0.22f),
                                new PointF(r.Right - w * 0.18f, cy + h * 0.12f),
                                new PointF(cx, r.Bottom - h * 0.10f),
                                new PointF(x + w * 0.18f, cy + h * 0.12f),
                                new PointF(x + w * 0.14f, y + h * 0.22f)
                            };
                            g.DrawPolygon(p, crest);
                            // Sword vertical blade + crossguard inside shield
                            g.DrawLine(p, cx, y + h * 0.26f, cx, r.Bottom - h * 0.24f);
                            g.DrawLine(p, cx - w * 0.14f, cy - h * 0.04f, cx + w * 0.14f, cy - h * 0.04f);
                            break;

                        case IconKind.WindowLock:
                            // Tactical Gaming Target Crosshair (Lock-On Game Window!)
                            float wr = w * 0.34f;
                            g.DrawEllipse(p, cx - wr, cy - wr, wr * 2f, wr * 2f);
                            g.DrawLine(p, cx, y + h * 0.06f, cx, y + h * 0.28f);
                            g.DrawLine(p, cx, r.Bottom - h * 0.28f, cx, r.Bottom - h * 0.06f);
                            g.DrawLine(p, x + w * 0.06f, cy, x + w * 0.28f, cy);
                            g.DrawLine(p, r.Right - w * 0.28f, cy, r.Right - w * 0.06f, cy);
                            g.FillEllipse(b, cx - w * 0.08f, cy - h * 0.08f, w * 0.16f, h * 0.16f);
                            break;

                        case IconKind.Target:
                            // Gaming Sniper / Lock-on Reticle
                            float tr = w * 0.32f;
                            g.DrawEllipse(p, cx - tr, cy - tr, tr * 2f, tr * 2f);
                            g.DrawLine(p, cx, y + h * 0.08f, cx, y + h * 0.26f);
                            g.DrawLine(p, cx, r.Bottom - h * 0.26f, cx, r.Bottom - h * 0.08f);
                            g.DrawLine(p, x + w * 0.08f, cy, x + w * 0.26f, cy);
                            g.DrawLine(p, r.Right - w * 0.26f, cy, r.Right - w * 0.08f, cy);
                            g.FillEllipse(b, cx - w * 0.07f, cy - h * 0.07f, w * 0.14f, h * 0.14f);
                            break;

                        case IconKind.Search:
                            float sr = w * 0.28f;
                            float scx = x + w * 0.16f + sr, scy = y + h * 0.16f + sr;
                            g.DrawEllipse(p, scx - sr, scy - sr, sr * 2f, sr * 2f);
                            g.DrawLine(p, scx + sr * 0.72f, scy + sr * 0.72f, r.Right - w * 0.14f, r.Bottom - h * 0.14f);
                            break;

                        case IconKind.Shield:
                            PointF[] shPts = new PointF[]
                            {
                                new PointF(cx, y + h * 0.12f),
                                new PointF(r.Right - w * 0.16f, y + h * 0.24f),
                                new PointF(r.Right - w * 0.18f, cy + h * 0.10f),
                                new PointF(cx, r.Bottom - h * 0.12f),
                                new PointF(x + w * 0.18f, cy + h * 0.10f),
                                new PointF(x + w * 0.16f, y + h * 0.24f)
                            };
                            g.DrawPolygon(p, shPts);
                            g.DrawLine(p, cx - w * 0.12f, cy, cx - w * 0.02f, cy + h * 0.10f);
                            g.DrawLine(p, cx - w * 0.02f, cy + h * 0.10f, cx + w * 0.16f, cy - h * 0.10f);
                            break;

                        case IconKind.Pattern:
                            // Diagonal hatch square icon
                            DrawRR(g, new RectangleF(x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f), 2.5f, col, sw);
                            g.DrawLine(p, x + w * 0.18f, cy, cx, y + h * 0.18f);
                            g.DrawLine(p, x + w * 0.20f, r.Bottom - h * 0.20f, r.Right - w * 0.20f, y + h * 0.20f);
                            g.DrawLine(p, cx, r.Bottom - h * 0.18f, r.Right - w * 0.18f, cy);
                            break;
                    }
                }
            }
            finally
            {
                g.Restore(gs);
            }
        }
    }
}
