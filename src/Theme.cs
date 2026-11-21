using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        Download
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
                // Figma Minimalist UI Kit — Modern Dark Mode (Deep Zinc / Slate)
                p.Bg = Color.FromArgb(9, 9, 11);               // #09090B deep matte black canvas
                p.Sidebar = Color.FromArgb(14, 14, 17);         // #0E0E11 sidebar with subtle separation
                p.Card = Color.FromArgb(24, 24, 27);            // #18181B (Zinc 900) card background
                p.CardHover = Color.FromArgb(34, 34, 39);       // #222227 soft card hover
                p.Well = Color.FromArgb(18, 18, 20);            // #121214 inset container well
                p.Border = Color.FromArgb(39, 39, 42);          // #27272A (Zinc 800) 1px clean border
                p.BorderStrong = Color.FromArgb(63, 63, 70);    // #3F3F46 (Zinc 700) prominent borders
                p.Shadow = Color.FromArgb(80, 0, 0, 0);

                p.Text = Color.FromArgb(244, 244, 245);         // #F4F4F5 (Zinc 100) high contrast readable text
                p.TextSub = Color.FromArgb(161, 161, 170);      // #A1A1AA (Zinc 400) secondary text
                p.TextMute = Color.FromArgb(113, 113, 122);     // #71717A (Zinc 500) subtle muted text

                p.Accent = Color.FromArgb(250, 250, 250);       // #FAFAFA solid light primary button
                p.AccentHover = Color.FromArgb(228, 228, 231);  // #E4E4E7 hover
                p.AccentSoft = Color.FromArgb(39, 39, 42);      // #27272A
                p.AccentText = Color.FromArgb(250, 250, 250);   // #FAFAFA
                p.AccentBtnText = Color.FromArgb(9, 9, 11);     // #09090B high-contrast black text on button

                p.Live = Color.FromArgb(52, 211, 153);          // Emerald 400
                p.LiveHover = Color.FromArgb(16, 185, 129);
                p.LiveSoft = Color.FromArgb(30, 52, 211, 153);

                p.Danger = Color.FromArgb(248, 113, 113);       // Red 400
                p.DangerSoft = Color.FromArgb(30, 248, 113, 113);
            }
            else
            {
                // Figma Minimalist UI Kit — Modern Light Mode (Pure White, Neutral Gray, Solid Black Accents)
                p.Bg = Color.FromArgb(248, 249, 250);           // #F8F9FA crisp off-white canvas
                p.Sidebar = Color.FromArgb(255, 255, 255);      // #FFFFFF pure white sidebar with 1px border
                p.Card = Color.FromArgb(255, 255, 255);         // #FFFFFF pure white card surface
                p.CardHover = Color.FromArgb(244, 244, 245);    // #F4F4F5 (Zinc 100) hover
                p.Well = Color.FromArgb(244, 244, 245);         // #F4F4F5 inset container well
                p.Border = Color.FromArgb(228, 228, 231);       // #E4E4E7 (5% black stroke in Figma)
                p.BorderStrong = Color.FromArgb(212, 212, 216); // #D4D4D8 (Zinc 300)
                p.Shadow = Color.FromArgb(10, 0, 0, 0);         // soft subtle ambient shadow

                p.Text = Color.FromArgb(24, 24, 27);            // #18181B (Zinc 900) sharp dark text
                p.TextSub = Color.FromArgb(113, 113, 122);      // #71717A (Zinc 500) clean subtext
                p.TextMute = Color.FromArgb(161, 161, 170);     // #A1A1AA (Zinc 400) placeholders
                
                p.Accent = Color.FromArgb(24, 24, 27);          // #18181B solid dark primary button in Figma
                p.AccentHover = Color.FromArgb(39, 39, 42);     // #27272A
                p.AccentSoft = Color.FromArgb(244, 244, 245);   // #F4F4F5
                p.AccentText = Color.FromArgb(24, 24, 27);      // #18181B
                p.AccentBtnText = Color.White;                  // White text on dark button

                p.Live = Color.FromArgb(16, 185, 129);          // Emerald 500
                p.LiveHover = Color.FromArgb(5, 150, 105);
                p.LiveSoft = Color.FromArgb(25, 16, 185, 129);

                p.Danger = Color.FromArgb(239, 68, 68);         // Red 500
                p.DangerSoft = Color.FromArgb(25, 239, 68, 68);
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

        public static void DrawCard(Graphics g, Palette pal, RectangleF r, float rad = 16f)
        {
            FillRR(g, new RectangleF(r.X, r.Y + 1.5f, r.Width, r.Height), rad, pal.Shadow);
            FillRR(g, r, rad, pal.Card);
            DrawRR(g, r, rad, pal.Border, 1f);
        }

        // NATIVE WINDOWS GDI CLEARTYPE TEXT RENDERING — 100% RAZOR SHARP AT ANY DPI/ZOOM!
        public static void DrawTextLeft(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text)) return;
            Rectangle ir = Rectangle.Round(r);
            TextRenderer.DrawText(g, text, f, ir, c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static void DrawTextCenter(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text)) return;
            Rectangle ir = Rectangle.Round(r);
            TextRenderer.DrawText(g, text, f, ir, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static void DrawTextRight(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text)) return;
            Rectangle ir = Rectangle.Round(r);
            TextRenderer.DrawText(g, text, f, ir, c,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        public static void DrawTextWrap(Graphics g, string text, Font f, Color c, RectangleF r)
        {
            if (string.IsNullOrEmpty(text)) return;
            Rectangle ir = Rectangle.Round(r);
            TextRenderer.DrawText(g, text, f, ir, c,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
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
                            // Sleek Display Frame with Active Center Scanline
                            RectangleF mon = new RectangleF(x + w * 0.12f, y + h * 0.14f, w * 0.76f, h * 0.54f);
                            DrawRR(g, mon, 3f, col, sw);
                            g.DrawLine(p, cx, mon.Bottom, cx, r.Bottom - h * 0.14f);
                            g.DrawLine(p, cx - w * 0.20f, r.Bottom - h * 0.14f, cx + w * 0.20f, r.Bottom - h * 0.14f);
                            // Laser scan beam + center focus dot
                            float sY = mon.Y + mon.Height * 0.5f;
                            g.DrawLine(p, mon.X + w * 0.12f, sY, mon.Right - w * 0.12f, sY);
                            g.FillEllipse(b, cx - w * 0.05f, sY - h * 0.05f, w * 0.10f, h * 0.10f);
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
                            // Sleek Ergonomic Gamepad with D-pad & 4 Action Buttons
                            RectangleF gpBox = new RectangleF(x + w * 0.12f, y + h * 0.20f, w * 0.76f, h * 0.58f);
                            DrawRR(g, gpBox, h * 0.24f, col, sw);
                            float dpx = gpBox.X + w * 0.20f, dpy = cy + h * 0.02f;
                            g.DrawLine(p, dpx - w * 0.08f, dpy, dpx + w * 0.08f, dpy);
                            g.DrawLine(p, dpx, dpy - h * 0.08f, dpx, dpy + h * 0.08f);
                            float abx = gpBox.Right - w * 0.20f, aby = cy + h * 0.02f;
                            float bsz = Math.Max(2f, w * 0.06f);
                            g.FillEllipse(b, abx - bsz * 0.5f, aby - h * 0.08f - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx - bsz * 0.5f, aby + h * 0.08f - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx - w * 0.08f - bsz * 0.5f, aby - bsz * 0.5f, bsz, bsz);
                            g.FillEllipse(b, abx + w * 0.08f - bsz * 0.5f, aby - bsz * 0.5f, bsz, bsz);
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
                            // Authentic Lucide Artist Palette with Thumb Notch & Outer Color Drops
                            float sx = w / 24f, sy = h / 24f;
                            using (GraphicsPath pp = new GraphicsPath())
                            {
                                pp.AddBezier(new PointF(x + 12f * sx, y + 2f * sy), new PointF(x + 6.48f * sx, y + 2f * sy), new PointF(x + 2f * sx, y + 6.48f * sy), new PointF(x + 2f * sx, y + 12f * sy));
                                pp.AddBezier(new PointF(x + 2f * sx, y + 12f * sy), new PointF(x + 2f * sx, y + 17.52f * sy), new PointF(x + 6.48f * sx, y + 22f * sy), new PointF(x + 12f * sx, y + 22f * sy));
                                pp.AddBezier(new PointF(x + 12f * sx, y + 22f * sy), new PointF(x + 13.1f * sx, y + 22f * sy), new PointF(x + 13.8f * sx, y + 21.2f * sy), new PointF(x + 13.8f * sx, y + 20.3f * sy));
                                pp.AddBezier(new PointF(x + 13.8f * sx, y + 20.3f * sy), new PointF(x + 13.8f * sx, y + 19.8f * sy), new PointF(x + 13.4f * sx, y + 19.3f * sy), new PointF(x + 13.1f * sx, y + 18.9f * sy));
                                pp.AddBezier(new PointF(x + 13.1f * sx, y + 18.9f * sy), new PointF(x + 12.8f * sx, y + 18.5f * sy), new PointF(x + 12.6f * sx, y + 18.0f * sy), new PointF(x + 12.6f * sx, y + 17.5f * sy));
                                pp.AddBezier(new PointF(x + 12.6f * sx, y + 17.5f * sy), new PointF(x + 12.6f * sx, y + 16.4f * sy), new PointF(x + 13.5f * sx, y + 15.5f * sy), new PointF(x + 14.6f * sx, y + 15.5f * sy));
                                pp.AddLine(new PointF(x + 14.6f * sx, y + 15.5f * sy), new PointF(x + 16.6f * sx, y + 15.5f * sy));
                                pp.AddBezier(new PointF(x + 16.6f * sx, y + 15.5f * sy), new PointF(x + 19.7f * sx, y + 15.5f * sy), new PointF(x + 22f * sx, y + 13.0f * sy), new PointF(x + 22f * sx, y + 9.9f * sy));
                                pp.AddBezier(new PointF(x + 22f * sx, y + 9.9f * sy), new PointF(x + 22f * sx, y + 5.5f * sy), new PointF(x + 17.5f * sx, y + 2f * sy), new PointF(x + 12f * sx, y + 2f * sy));
                                pp.CloseFigure();
                                g.DrawPath(p, pp);
                            }
                            float dotR = Math.Max(1.4f, 1.2f * sx);
                            g.FillEllipse(b, x + 8.5f * sx - dotR, y + 7.5f * sy - dotR, dotR * 2f, dotR * 2f);
                            g.FillEllipse(b, x + 13.5f * sx - dotR, y + 6.5f * sy - dotR, dotR * 2f, dotR * 2f);
                            g.FillEllipse(b, x + 17.5f * sx - dotR, y + 9.5f * sy - dotR, dotR * 2f, dotR * 2f);
                            g.FillEllipse(b, x + 6.5f * sx - dotR, y + 12.5f * sy - dotR, dotR * 2f, dotR * 2f);
                            break;

                        case IconKind.Settings:
                            // Precision 6-Tooth Mechanical Gear
                            using (GraphicsPath gearP = new GraphicsPath())
                            {
                                float rOut = w * 0.40f;
                                float rIn = w * 0.28f;
                                for (int i = 0; i < 6; i++)
                                {
                                    double deg = i * 60.0;
                                    double a1 = (deg - 14.0) * Math.PI / 180.0;
                                    double a2 = (deg - 9.0) * Math.PI / 180.0;
                                    double a3 = (deg + 9.0) * Math.PI / 180.0;
                                    double a4 = (deg + 14.0) * Math.PI / 180.0;

                                    PointF p1 = new PointF(cx + (float)Math.Cos(a1) * rIn, cy + (float)Math.Sin(a1) * rIn);
                                    PointF p2 = new PointF(cx + (float)Math.Cos(a2) * rOut, cy + (float)Math.Sin(a2) * rOut);
                                    PointF p3 = new PointF(cx + (float)Math.Cos(a3) * rOut, cy + (float)Math.Sin(a3) * rOut);
                                    PointF p4 = new PointF(cx + (float)Math.Cos(a4) * rIn, cy + (float)Math.Sin(a4) * rIn);

                                    if (i == 0) gearP.AddLine(p1, p2);
                                    else { gearP.AddLine(gearP.GetLastPoint(), p1); gearP.AddLine(p1, p2); }
                                    gearP.AddLine(p2, p3);
                                    gearP.AddLine(p3, p4);
                                }
                                gearP.CloseFigure();
                                g.DrawPath(p, gearP);
                                float rHole = w * 0.13f;
                                g.DrawEllipse(p, cx - rHole, cy - rHole, rHole * 2f, rHole * 2f);
                            }
                            break;

                        case IconKind.Rocket:
                            // Precision Lucide Aerodynamic Launch Rocket
                            float rsx = w / 24f, rsy = h / 24f;
                            using (GraphicsPath fuseP = new GraphicsPath())
                            {
                                fuseP.AddLine(new PointF(x + 9f * rsx, y + 12f * rsy), new PointF(x + 12f * rsx, y + 15f * rsy));
                                fuseP.AddBezier(new PointF(x + 12f * rsx, y + 15f * rsy), new PointF(x + 16f * rsx, y + 13f * rsy), new PointF(x + 22f * rsx, y + 4.7f * rsy), new PointF(x + 22f * rsx, y + 2f * rsy));
                                fuseP.AddBezier(new PointF(x + 22f * rsx, y + 2f * rsy), new PointF(x + 19.3f * rsx, y + 2f * rsy), new PointF(x + 11f * rsx, y + 8f * rsy), new PointF(x + 9f * rsx, y + 12f * rsy));
                                fuseP.CloseFigure();
                                g.DrawPath(p, fuseP);
                            }
                            float rPwh = Math.Max(2f, 1.6f * rsx);
                            g.DrawEllipse(p, x + 15f * rsx - rPwh, y + 9f * rsy - rPwh, rPwh * 2f, rPwh * 2f);
                            // Left fin
                            g.DrawLine(p, x + 9f * rsx, y + 12f * rsy, x + 5f * rsx, y + 12f * rsy);
                            g.DrawLine(p, x + 5f * rsx, y + 12f * rsy, x + 6.5f * rsx, y + 9f * rsy);
                            g.DrawLine(p, x + 6.5f * rsx, y + 9f * rsy, x + 10.5f * rsx, y + 9f * rsy);
                            // Right fin
                            g.DrawLine(p, x + 12f * rsx, y + 15f * rsy, x + 12f * rsx, y + 19f * rsy);
                            g.DrawLine(p, x + 12f * rsx, y + 19f * rsy, x + 15f * rsx, y + 17.5f * rsy);
                            g.DrawLine(p, x + 15f * rsx, y + 17.5f * rsy, x + 15f * rsx, y + 13.5f * rsy);
                            // Exhaust flame
                            g.DrawLine(p, x + 9.5f * rsx, y + 14.5f * rsy, x + 5f * rsx, y + 19f * rsy);
                            g.DrawLine(p, x + 8f * rsx, y + 13f * rsy, x + 4f * rsx, y + 17f * rsy);
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
                            g.DrawEllipse(p, x + w * 0.15f, y + h * 0.15f, w * 0.70f, h * 0.70f);
                            g.DrawLine(p, cx, y + h * 0.28f, cx, cy);
                            g.DrawLine(p, cx, cy, cx + w * 0.16f, cy + h * 0.10f);
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
