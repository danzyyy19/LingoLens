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
        History
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
                p.Bg = Color.FromArgb(33, 32, 30);
                p.Sidebar = Color.FromArgb(26, 25, 23);
                p.Card = Color.FromArgb(42, 40, 37);
                p.CardHover = Color.FromArgb(51, 49, 45);
                p.Well = Color.FromArgb(28, 27, 25);
                p.Border = Color.FromArgb(58, 55, 50);
                p.BorderStrong = Color.FromArgb(78, 74, 68);
                p.Shadow = Color.FromArgb(55, 0, 0, 0);

                p.Text = Color.FromArgb(238, 236, 230);
                p.TextSub = Color.FromArgb(169, 165, 156);
                p.TextMute = Color.FromArgb(124, 120, 112);

                p.Accent = Color.FromArgb(217, 119, 87);
                p.AccentHover = Color.FromArgb(199, 101, 70);
                p.AccentSoft = Color.FromArgb(38, 217, 119, 87);
                p.AccentText = Color.FromArgb(235, 145, 114);

                p.Live = Color.FromArgb(45, 168, 122);
                p.LiveHover = Color.FromArgb(34, 142, 102);
                p.LiveSoft = Color.FromArgb(36, 45, 168, 122);

                p.Danger = Color.FromArgb(229, 92, 92);
                p.DangerSoft = Color.FromArgb(36, 229, 92, 92);
            }
            else
            {
                p.Bg = Color.FromArgb(250, 249, 245);
                p.Sidebar = Color.FromArgb(243, 241, 234);
                p.Card = Color.FromArgb(255, 255, 255);
                p.CardHover = Color.FromArgb(246, 244, 238);
                p.Well = Color.FromArgb(244, 242, 236);
                p.Border = Color.FromArgb(230, 226, 216);
                p.BorderStrong = Color.FromArgb(207, 202, 190);
                p.Shadow = Color.FromArgb(14, 30, 25, 18);

                p.Text = Color.FromArgb(31, 30, 28);
                p.TextSub = Color.FromArgb(94, 90, 82);
                p.TextMute = Color.FromArgb(142, 137, 128);

                p.Accent = Color.FromArgb(201, 100, 66);
                p.AccentHover = Color.FromArgb(179, 84, 52);
                p.AccentSoft = Color.FromArgb(28, 201, 100, 66);
                p.AccentText = Color.FromArgb(182, 82, 48);

                p.Live = Color.FromArgb(28, 138, 97);
                p.LiveHover = Color.FromArgb(20, 114, 79);
                p.LiveSoft = Color.FromArgb(26, 28, 138, 97);

                p.Danger = Color.FromArgb(204, 62, 62);
                p.DangerSoft = Color.FromArgb(26, 204, 62, 62);
            }
            return p;
        }
    }

    public static class Theme
    {
        private static string _uiFontName;
        private static string _serifFontName;

        static Theme()
        {
            _uiFontName = PickFont("Segoe UI Variable Text", "Segoe UI");
            _serifFontName = PickFont("Georgia", "Cambria");
        }

        private static string PickFont(string preferred, string fallback)
        {
            foreach (FontFamily ff in FontFamily.Families)
            {
                if (string.Equals(ff.Name, preferred, StringComparison.OrdinalIgnoreCase)) return ff.Name;
            }
            return fallback;
        }

        public static Font SerifFont(float px, bool bold)
        {
            return new Font(_serifFontName, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static Font UiFont(float px, bool bold)
        {
            return new Font(_uiFontName, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
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

        public static void DrawCard(Graphics g, Palette pal, RectangleF r, float rad = 14f)
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

        // PREMIUM MINIMALIST VECTOR ICONS (Lucide / Feather Precision)
        public static void DrawIcon(Graphics g, IconKind k, RectangleF r, Color col, float sw = 1.6f)
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
                        g.FillPolygon(b, new PointF[] { new PointF(x + w * 0.25f, y + h * 0.14f), new PointF(x + w * 0.85f, cy), new PointF(x + w * 0.25f, r.Bottom - h * 0.14f) });
                        break;
                    case IconKind.Stop:
                        FillRR(g, new RectangleF(x + w * 0.18f, y + h * 0.18f, w * 0.64f, h * 0.64f), 2.2f, col);
                        break;
                    case IconKind.Plus:
                        g.DrawLine(p, cx, y + h * 0.16f, cx, r.Bottom - h * 0.16f);
                        g.DrawLine(p, x + w * 0.16f, cy, r.Right - w * 0.16f, cy);
                        break;
                    case IconKind.EditBox:
                        DrawRR(g, new RectangleF(x + w * 0.10f, y + h * 0.10f, w * 0.80f, h * 0.80f), 2.5f, col, sw);
                        g.DrawLine(p, x + w * 0.10f, y + h * 0.38f, r.Right - w * 0.10f, y + h * 0.38f);
                        g.DrawLine(p, x + w * 0.38f, y + h * 0.38f, x + w * 0.38f, r.Bottom - h * 0.10f);
                        break;
                    case IconKind.Refresh:
                        g.DrawArc(p, x + w * 0.12f, y + h * 0.12f, w * 0.76f, h * 0.76f, 20f, 300f);
                        g.DrawLine(p, r.Right - w * 0.34f, y + h * 0.12f, r.Right - w * 0.12f, y + h * 0.34f);
                        g.DrawLine(p, r.Right - w * 0.12f, y + h * 0.34f, r.Right - w * 0.38f, y + h * 0.36f);
                        break;
                    case IconKind.Eye:
                        g.DrawArc(p, x + w * 0.08f, y + h * 0.22f, w * 0.84f, h * 0.56f, 0f, 360f);
                        g.FillEllipse(b, cx - w * 0.15f, cy - h * 0.15f, w * 0.30f, h * 0.30f);
                        break;
                    case IconKind.Sun:
                        g.DrawEllipse(p, cx - w * 0.20f, cy - h * 0.20f, w * 0.40f, h * 0.40f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            g.DrawLine(p, cx + (float)Math.Cos(a) * w * 0.32f, cy + (float)Math.Sin(a) * h * 0.32f,
                                          cx + (float)Math.Cos(a) * w * 0.44f, cy + (float)Math.Sin(a) * h * 0.44f);
                        }
                        break;
                    case IconKind.Moon:
                        g.DrawArc(p, x + w * 0.16f, y + h * 0.16f, w * 0.68f, h * 0.68f, 45f, 250f);
                        break;
                    case IconKind.Tray:
                        g.DrawLine(p, cx, y + h * 0.12f, cx, cy + h * 0.12f);
                        g.DrawLine(p, cx - w * 0.20f, cy - h * 0.06f, cx, cy + h * 0.15f);
                        g.DrawLine(p, cx + w * 0.20f, cy - h * 0.06f, cx, cy + h * 0.15f);
                        g.DrawLine(p, x + w * 0.14f, cy + h * 0.14f, x + w * 0.14f, r.Bottom - h * 0.14f);
                        g.DrawLine(p, x + w * 0.14f, r.Bottom - h * 0.14f, r.Right - w * 0.14f, r.Bottom - h * 0.14f);
                        g.DrawLine(p, r.Right - w * 0.14f, r.Bottom - h * 0.14f, r.Right - w * 0.14f, cy + h * 0.14f);
                        break;
                    case IconKind.Minimize:
                        g.DrawLine(p, x + w * 0.16f, cy, r.Right - w * 0.16f, cy);
                        break;
                    case IconKind.Maximize:
                        DrawRR(g, new RectangleF(x + w * 0.16f, y + h * 0.16f, w * 0.68f, h * 0.68f), 2f, col, sw);
                        break;
                    case IconKind.Restore:
                        DrawRR(g, new RectangleF(x + w * 0.14f, y + h * 0.28f, w * 0.58f, h * 0.58f), 1.5f, col, sw);
                        g.DrawLine(p, x + w * 0.28f, y + h * 0.14f, r.Right - w * 0.14f, y + h * 0.14f);
                        g.DrawLine(p, r.Right - w * 0.14f, y + h * 0.14f, r.Right - w * 0.14f, r.Bottom - h * 0.28f);
                        break;
                    case IconKind.Fullscreen:
                        float fl = w * 0.24f;
                        g.DrawLine(p, x + w * 0.12f, y + h * 0.12f + fl, x + w * 0.12f, y + h * 0.12f); g.DrawLine(p, x + w * 0.12f, y + h * 0.12f, x + w * 0.12f + fl, y + h * 0.12f);
                        g.DrawLine(p, r.Right - w * 0.12f - fl, y + h * 0.12f, r.Right - w * 0.12f, y + h * 0.12f); g.DrawLine(p, r.Right - w * 0.12f, y + h * 0.12f, r.Right - w * 0.12f, y + h * 0.12f + fl);
                        g.DrawLine(p, x + w * 0.12f, r.Bottom - h * 0.12f - fl, x + w * 0.12f, r.Bottom - h * 0.12f); g.DrawLine(p, x + w * 0.12f, r.Bottom - h * 0.12f, x + w * 0.12f + fl, r.Bottom - h * 0.12f);
                        g.DrawLine(p, r.Right - w * 0.12f - fl, r.Bottom - h * 0.12f, r.Right - w * 0.12f, r.Bottom - h * 0.12f); g.DrawLine(p, r.Right - w * 0.12f, r.Bottom - h * 0.12f, r.Right - w * 0.12f, r.Bottom - h * 0.12f - fl);
                        break;
                    case IconKind.Close:
                        g.DrawLine(p, x + w * 0.18f, y + h * 0.18f, r.Right - w * 0.18f, r.Bottom - h * 0.18f);
                        g.DrawLine(p, r.Right - w * 0.18f, y + h * 0.18f, x + w * 0.18f, r.Bottom - h * 0.18f);
                        break;
                    case IconKind.Trash:
                        // Crisp Trash Can: Top Handle + Lid + Tapered Bin + 2 Vertical Lines
                        g.DrawLine(p, x + w * 0.36f, y + h * 0.14f, x + w * 0.64f, y + h * 0.14f);
                        g.DrawLine(p, x + w * 0.16f, y + h * 0.26f, r.Right - w * 0.16f, y + h * 0.26f);
                        g.DrawPolygon(p, new PointF[]
                        {
                            new PointF(x + w * 0.24f, y + h * 0.26f),
                            new PointF(r.Right - w * 0.24f, y + h * 0.26f),
                            new PointF(r.Right - w * 0.28f, r.Bottom - h * 0.14f),
                            new PointF(x + w * 0.28f, r.Bottom - h * 0.14f)
                        });
                        g.DrawLine(p, x + w * 0.42f, y + h * 0.42f, x + w * 0.42f, r.Bottom - h * 0.26f);
                        g.DrawLine(p, x + w * 0.58f, y + h * 0.42f, x + w * 0.58f, r.Bottom - h * 0.26f);
                        break;
                    case IconKind.Snap:
                        // Crisp Target / Align-to-OCR Icon (Crosshair Anchor)
                        g.DrawEllipse(p, x + w * 0.22f, y + h * 0.22f, w * 0.56f, h * 0.56f);
                        g.FillEllipse(b, cx - w * 0.12f, cy - h * 0.12f, w * 0.24f, h * 0.24f);
                        g.DrawLine(p, cx, y + h * 0.08f, cx, y + h * 0.22f);
                        g.DrawLine(p, cx, r.Bottom - h * 0.22f, cx, r.Bottom - h * 0.08f);
                        g.DrawLine(p, x + w * 0.08f, cy, x + w * 0.22f, cy);
                        g.DrawLine(p, r.Right - w * 0.22f, cy, r.Right - w * 0.08f, cy);
                        break;
                    case IconKind.Crop:
                        // Clean Crop Frame
                        g.DrawLine(p, x + w * 0.26f, y + h * 0.10f, x + w * 0.26f, r.Bottom - h * 0.26f);
                        g.DrawLine(p, x + w * 0.26f, r.Bottom - h * 0.26f, r.Right - w * 0.10f, r.Bottom - h * 0.26f);
                        g.DrawLine(p, x + w * 0.10f, y + h * 0.26f, r.Right - w * 0.26f, y + h * 0.26f);
                        g.DrawLine(p, r.Right - w * 0.26f, y + h * 0.26f, r.Right - w * 0.26f, r.Bottom - h * 0.10f);
                        break;
                    case IconKind.ResultBox:
                        DrawRR(g, new RectangleF(x + w * 0.10f, y + h * 0.18f, w * 0.80f, h * 0.64f), 2.5f, col, sw);
                        g.DrawLine(p, x + w * 0.26f, cy - h * 0.08f, r.Right - w * 0.26f, cy - h * 0.08f);
                        g.DrawLine(p, x + w * 0.26f, cy + h * 0.12f, cx + w * 0.10f, cy + h * 0.12f);
                        break;
                    case IconKind.Swap:
                        g.DrawLine(p, x + w * 0.14f, y + h * 0.34f, r.Right - w * 0.14f, y + h * 0.34f);
                        g.DrawLine(p, r.Right - w * 0.34f, y + h * 0.18f, r.Right - w * 0.14f, y + h * 0.34f);
                        g.DrawLine(p, r.Right - w * 0.34f, y + h * 0.50f, r.Right - w * 0.14f, y + h * 0.34f);
                        g.DrawLine(p, r.Right - w * 0.14f, r.Bottom - h * 0.34f, x + w * 0.14f, r.Bottom - h * 0.34f);
                        g.DrawLine(p, x + w * 0.34f, r.Bottom - h * 0.50f, x + w * 0.14f, r.Bottom - h * 0.34f);
                        g.DrawLine(p, x + w * 0.34f, r.Bottom - h * 0.18f, x + w * 0.14f, r.Bottom - h * 0.34f);
                        break;
                    case IconKind.Copy:
                        DrawRR(g, new RectangleF(x + w * 0.30f, y + h * 0.30f, w * 0.56f, h * 0.56f), 2f, col, sw);
                        g.DrawLine(p, x + w * 0.16f, r.Bottom - h * 0.30f, x + w * 0.16f, y + h * 0.16f);
                        g.DrawLine(p, x + w * 0.16f, y + h * 0.16f, r.Right - w * 0.30f, y + h * 0.16f);
                        break;
                    case IconKind.Check:
                        g.DrawLine(p, x + w * 0.18f, cy, cx - w * 0.06f, r.Bottom - h * 0.22f);
                        g.DrawLine(p, cx - w * 0.06f, r.Bottom - h * 0.22f, r.Right - w * 0.16f, y + h * 0.24f);
                        break;
                    case IconKind.Layers:
                        PointF[] dm = new PointF[] { new PointF(cx, y + h * 0.12f), new PointF(r.Right - w * 0.12f, cy - h * 0.08f), new PointF(cx, cy + h * 0.12f), new PointF(x + w * 0.12f, cy - h * 0.08f) };
                        g.DrawPolygon(p, dm);
                        g.DrawLine(p, x + w * 0.14f, cy + h * 0.16f, cx, r.Bottom - h * 0.14f);
                        g.DrawLine(p, cx, r.Bottom - h * 0.14f, r.Right - w * 0.14f, cy + h * 0.16f);
                        break;
                    case IconKind.Palette:
                        g.DrawEllipse(p, x + w * 0.12f, y + h * 0.12f, w * 0.76f, h * 0.76f);
                        g.FillEllipse(b, cx - w * 0.22f, cy - h * 0.16f, w * 0.15f, h * 0.15f);
                        g.FillEllipse(b, cx + w * 0.08f, cy - h * 0.16f, w * 0.15f, h * 0.15f);
                        g.FillEllipse(b, cx - w * 0.08f, cy + h * 0.10f, w * 0.15f, h * 0.15f);
                        break;
                    case IconKind.Sliders:
                        g.DrawLine(p, x + w * 0.14f, y + h * 0.26f, r.Right - w * 0.14f, y + h * 0.26f);
                        g.DrawLine(p, x + w * 0.14f, cy, r.Right - w * 0.14f, cy);
                        g.DrawLine(p, x + w * 0.14f, r.Bottom - h * 0.26f, r.Right - w * 0.14f, r.Bottom - h * 0.26f);
                        g.FillEllipse(b, x + w * 0.26f, y + h * 0.16f, w * 0.20f, h * 0.20f);
                        g.FillEllipse(b, r.Right - w * 0.46f, cy - h * 0.10f, w * 0.20f, h * 0.20f);
                        g.FillEllipse(b, cx - w * 0.10f, r.Bottom - h * 0.36f, w * 0.20f, h * 0.20f);
                        break;
                    case IconKind.Sparkle:
                        float spR = w * 0.44f, spI = w * 0.14f;
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(cx, cy - spR), new PointF(cx + spI, cy - spI),
                            new PointF(cx + spR, cy), new PointF(cx + spI, cy + spI),
                            new PointF(cx, cy + spR), new PointF(cx - spI, cy + spI),
                            new PointF(cx - spR, cy), new PointF(cx - spI, cy - spI)
                        });
                        break;
                    case IconKind.ZoomIn:
                        g.DrawLine(p, x + w * 0.22f, cy, r.Right - w * 0.22f, cy);
                        g.DrawLine(p, cx, y + h * 0.22f, cx, r.Bottom - h * 0.22f);
                        break;
                    case IconKind.ZoomOut:
                        g.DrawLine(p, x + w * 0.22f, cy, r.Right - w * 0.22f, cy);
                        break;
                    case IconKind.Gamepad:
                        DrawRR(g, new RectangleF(x + w * 0.08f, y + h * 0.24f, w * 0.84f, h * 0.52f), h * 0.24f, col, sw);
                        g.DrawLine(p, x + w * 0.28f, cy, x + w * 0.42f, cy);
                        g.DrawLine(p, x + w * 0.35f, cy - h * 0.10f, x + w * 0.35f, cy + h * 0.10f);
                        g.FillEllipse(b, r.Right - w * 0.36f, cy - h * 0.12f, w * 0.11f, h * 0.11f);
                        g.FillEllipse(b, r.Right - w * 0.26f, cy + h * 0.02f, w * 0.11f, h * 0.11f);
                        break;
                    case IconKind.Save:
                        DrawRR(g, new RectangleF(x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f), 2f, col, sw);
                        g.DrawRectangle(p, x + w * 0.30f, y + h * 0.14f, w * 0.40f, h * 0.26f);
                        g.DrawRectangle(p, x + w * 0.28f, r.Bottom - h * 0.38f, w * 0.44f, h * 0.24f);
                        break;
                    case IconKind.History:
                        g.DrawArc(p, x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f, -150f, 300f);
                        g.DrawLine(p, cx, y + h * 0.28f, cx, cy);
                        g.DrawLine(p, cx, cy, cx + w * 0.18f, cy + h * 0.12f);
                        break;
                }
            }
        }
    }
}
