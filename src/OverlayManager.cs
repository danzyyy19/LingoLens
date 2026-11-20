using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TranslatorLive
{
    public enum SnipTargetKind
    {
        Ocr = 0,
        Result = 1
    }

    // =========================================================================
    // 1. DIRECT DESKTOP-BLOCKING SNIPPER (100% RELIABLE ESC CANCEL VIA HOTKEY!)
    // =========================================================================
    public class SnipperForm : Form
    {
        private readonly string _title;
        private readonly SnipTargetKind _kind;
        private readonly Rectangle _referenceRect;
        private readonly string _referenceLabel;
        private readonly Action<bool, Rectangle> _done;
        private Point _p0;
        private Point _p1;
        private bool _dragging;
        private bool _finished;
        private const int HK_ESC = 9901;

        public SnipperForm(string title, SnipTargetKind kind, Rectangle referenceRect, string referenceLabel, Action<bool, Rectangle> done)
        {
            _title = title;
            _kind = kind;
            _referenceRect = referenceRect;
            _referenceLabel = referenceLabel;
            _done = done;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 13, 12);
            Opacity = 0.48;
            Cursor = Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Register global Escape hotkey while Snipper is open so ESC ALWAYS cancels even if game steals focus!
            Native.RegisterHotKey(Handle, HK_ESC, 0, (uint)Keys.Escape);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetForegroundWindow(Handle);
            Activate();
            Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { Native.UnregisterHotKey(Handle, HK_ESC); } catch { }
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HK_ESC)
            {
                Finish(false, Rectangle.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        private Rectangle SelRect()
        {
            int x1 = Math.Min(_p0.X, _p1.X);
            int y1 = Math.Min(_p0.Y, _p1.Y);
            int x2 = Math.Max(_p0.X, _p1.X);
            int y2 = Math.Max(_p0.Y, _p1.Y);
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Finish(false, Rectangle.Empty); return; }
            if (e.Button == MouseButtons.Left) { _dragging = true; _p0 = _p1 = e.Location; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) { _p1 = e.Location; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragging && e.Button == MouseButtons.Left)
            {
                _dragging = false;
                Rectangle r = SelRect();
                if (r.Width >= 24 && r.Height >= 16)
                {
                    Rectangle abs = new Rectangle(r.X + Left, r.Y + Top, r.Width, r.Height);
                    Finish(true, abs);
                }
                else Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Finish(false, Rectangle.Empty);
        }

        private void Finish(bool ok, Rectangle rc)
        {
            if (_finished) return;
            _finished = true;
            try { Native.UnregisterHotKey(Handle, HK_ESC); } catch { }
            Close();
            if (_done != null) _done(ok, rc);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color mainAccent = _kind == SnipTargetKind.Ocr
                ? Color.FromArgb(255, 86, 152, 255)
                : Color.FromArgb(255, 226, 126, 86);

            Color refColor = _kind == SnipTargetKind.Ocr
                ? Color.FromArgb(200, 226, 126, 86)
                : Color.FromArgb(200, 86, 152, 255);

            if (_referenceRect.Width > 10 && _referenceRect.Height > 10)
            {
                Rectangle rel = new Rectangle(_referenceRect.X - Left, _referenceRect.Y - Top, _referenceRect.Width, _referenceRect.Height);
                using (SolidBrush rb = new SolidBrush(Color.FromArgb(30, refColor.R, refColor.G, refColor.B)))
                using (Pen rp = new Pen(refColor, 1.8f))
                {
                    rp.DashPattern = new float[] { 5f, 4f };
                    g.FillRectangle(rb, rel);
                    g.DrawRectangle(rp, rel);
                }
                if (!string.IsNullOrEmpty(_referenceLabel))
                {
                    using (Font fr = Theme.UiFont(11.5f, true))
                    {
                        SizeF rsz = TextRenderer.MeasureText(_referenceLabel, fr);
                        RectangleF rtag = new RectangleF(rel.X, Math.Max(4, rel.Y - 24), rsz.Width + 14f, 22f);
                        Theme.FillRR(g, rtag, 6f, Color.FromArgb(220, refColor.R, refColor.G, refColor.B));
                        Theme.DrawTextCenter(g, _referenceLabel, fr, Color.White, rtag);
                    }
                }
            }

            Rectangle pri = Screen.PrimaryScreen.Bounds;
            int px = pri.X - Left;
            int py = pri.Y - Top;

            using (Font f = Theme.UiFont(13f, true))
            {
                string hint = _kind == SnipTargetKind.Ocr
                    ? "Seret area teks game  •  ESC / Klik Kanan batal"
                    : "Seret ukuran kotak hasil  •  ESC / Klik Kanan batal";
                string msg = _title + "   —   " + hint;
                SizeF sz = TextRenderer.MeasureText(msg, f);
                RectangleF pill = new RectangleF(px + (pri.Width - sz.Width - 40f) * 0.5f, py + 26f, sz.Width + 40f, 40f);
                Theme.FillRR(g, pill, 20f, Color.FromArgb(245, 26, 25, 23));
                Theme.DrawRR(g, pill, 20f, mainAccent, 1.8f);
                Theme.DrawTextCenter(g, msg, f, Color.White, pill);
            }

            if (_dragging)
            {
                Rectangle r = SelRect();
                if (r.Width > 2 && r.Height > 2)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(52, mainAccent.R, mainAccent.G, mainAccent.B)))
                    using (Pen pen = new Pen(mainAccent, 2.2f))
                    {
                        g.FillRectangle(fill, r);
                        g.DrawRectangle(pen, r);
                    }
                    string dim = "Lebar " + r.Width + "  ×  Tinggi " + r.Height + " px";
                    using (Font fd = Theme.UiFont(12f, true))
                    {
                        SizeF dsz = TextRenderer.MeasureText(dim, fd);
                        RectangleF tag = new RectangleF(r.X, Math.Max(4, r.Y - 28), dsz.Width + 16f, 24f);
                        Theme.FillRR(g, tag, 6f, mainAccent);
                        Theme.DrawTextCenter(g, dim, fd, Color.White, tag);
                    }
                }
            }
        }
    }

    // =========================================================================
    // 2. FLOATING EDIT TOOLBAR (With Global ESC Hotkey!)
    // =========================================================================
    public class EditBannerForm : Form
    {
        private readonly Action _onDone;
        private readonly Action _onAdd;
        private RectangleF _btnAdd;
        private RectangleF _btnDone;
        private string _hover = "";
        private const int HK_ESC = 9902;

        public EditBannerForm(Action onAdd, Action onDone)
        {
            _onAdd = onAdd;
            _onDone = onDone;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Size = new Size(560, 48);
            Rectangle pri = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(pri.X + (pri.Width - Width) / 2, pri.Y + 18);
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, 0x00000011);
            Native.RegisterHotKey(Handle, HK_ESC, 0, (uint)Keys.Escape);
            Render();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { Native.UnregisterHotKey(Handle, HK_ESC); } catch { }
            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HK_ESC && _onDone != null)
            {
                _onDone();
                return;
            }
            base.WndProc(ref m);
        }

        public void Render()
        {
            if (!IsHandleCreated) return;
            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    RectangleF rc = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
                    Theme.FillRR(g, rc, 24f, Color.FromArgb(245, 28, 27, 25));
                    Theme.DrawRR(g, rc, 24f, Color.FromArgb(160, 217, 119, 87), 1.4f);

                    using (Font f = Theme.UiFont(12.5f, true))
                    using (Font fs = Theme.UiFont(11.5f, false))
                    using (StringFormat sfL = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                    using (StringFormat sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (SolidBrush wBr = new SolidBrush(Color.White))
                    using (SolidBrush sBr = new SolidBrush(Color.FromArgb(255, 235, 232, 226)))
                    {
                        using (SolidBrush bBlue = new SolidBrush(Color.FromArgb(255, 86, 152, 255)))
                        using (SolidBrush bCoral = new SolidBrush(Color.FromArgb(255, 217, 119, 87)))
                        {
                            g.FillEllipse(bBlue, 18f, 19f, 10f, 10f);
                            g.FillEllipse(bCoral, 108f, 19f, 10f, 10f);
                        }
                        g.DrawString("Area OCR", fs, sBr, new RectangleF(34f, 0f, 70f, Height), sfL);
                        g.DrawString("Kotak Hasil", fs, sBr, new RectangleF(124f, 0f, 92f, Height), sfL);

                        _btnAdd = new RectangleF(Width - 260f, 8f, 116f, 32f);
                        Theme.FillRR(g, _btnAdd, 16f, _hover == "add" ? Color.FromArgb(255, 58, 55, 50) : Color.FromArgb(255, 44, 42, 39));
                        Theme.DrawRR(g, _btnAdd, 16f, Color.FromArgb(80, 255, 255, 255), 1f);
                        g.DrawString("+ Area Baru", f, wBr, _btnAdd, sfC);

                        _btnDone = new RectangleF(Width - 136f, 8f, 126f, 32f);
                        Theme.FillRR(g, _btnDone, 16f, _hover == "done" ? Color.FromArgb(255, 199, 101, 70) : Color.FromArgb(255, 217, 119, 87));
                        g.DrawString("Selesai (ESC)", f, wBr, _btnDone, sfC);
                    }
                }
                Native.PushLayered(Handle, bmp, Location);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = _btnDone.Contains(e.Location) ? "done" : (_btnAdd.Contains(e.Location) ? "add" : "");
            if (h != _hover)
            {
                _hover = h;
                Cursor = string.IsNullOrEmpty(h) ? Cursors.Default : Cursors.Hand;
                Render();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_btnDone.Contains(e.Location) && _onDone != null) _onDone();
                else if (_btnAdd.Contains(e.Location) && _onAdd != null) _onAdd();
            }
        }
    }

    // =========================================================================
    // 3. PER-PIXEL LAYERED OVERLAY PANE (Supports Per-Area Custom Styles!)
    // =========================================================================
    public class OverlayPane : Form
    {
        public int AreaId;
        public int DisplayNumber;
        public bool IsOcrFrame;
        public bool EditMode;
        public string TextContent = "";
        public string SpeakerName = "";

        private AppConfig _cfg;
        private readonly Action<OverlayPane> _onGeometryChanged;
        private readonly Action _onExitEdit;

        private bool _dragging;
        private int _resizeEdge;
        private Point _startMouse;
        private Rectangle _startBounds;

        public OverlayPane(int areaId, int displayNum, bool isOcrFrame, AppConfig cfg,
            Action<OverlayPane> onGeometryChanged, Action onExitEdit)
        {
            AreaId = areaId;
            DisplayNumber = displayNum;
            IsOcrFrame = isOcrFrame;
            _cfg = cfg;
            _onGeometryChanged = onGeometryChanged;
            _onExitEdit = onExitEdit;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                if (!EditMode) cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return !EditMode; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Native.SetWindowDisplayAffinity(Handle, 0x00000011); } catch { }
        }

        public void KeepTopmost()
        {
            if (IsHandleCreated && Visible)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            }
        }

        private OverlayStyle ResolveStyle(AppConfig cfg)
        {
            Area a = cfg.FindArea(AreaId);
            return a != null ? a.EffectiveStyle(cfg) : cfg.GlobalStyle;
        }

        public void Render(AppConfig cfg)
        {
            _cfg = cfg;
            if (!IsHandleCreated || Width < 8 || Height < 8) return;

            OverlayStyle st = ResolveStyle(cfg);

            using (Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    RectangleF rc = new RectangleF(1f, 1f, Width - 2f, Height - 2f);

                    if (IsOcrFrame)
                    {
                        Theme.FillRR(g, rc, 8f, Color.FromArgb(44, 86, 152, 255));
                        using (GraphicsPath p = Theme.RoundPath(rc, 8f))
                        using (Pen pen = new Pen(Color.FromArgb(245, 86, 152, 255), 2f))
                        {
                            pen.DashPattern = new float[] { 5f, 3.5f };
                            g.DrawPath(pen, p);
                        }
                        RectangleF tag = new RectangleF(8f, 8f, Math.Min(Width - 16f, 200f), 24f);
                        Theme.FillRR(g, tag, 6f, Color.FromArgb(238, 59, 130, 246));
                        using (Font ft = Theme.UiFont(11.5f, true))
                        using (StringFormat sfL = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                        using (SolidBrush wBr = new SolidBrush(Color.White))
                        {
                            g.DrawString("OCR " + DisplayNumber + "  •  " + Width + " × " + Height, ft, wBr,
                                new RectangleF(tag.X + 8f, tag.Y, tag.Width - 12f, tag.Height), sfL);
                        }
                        DrawCornerHandles(g, Color.FromArgb(255, 147, 197, 253));
                    }
                    else
                    {
                        float rad = Math.Max(0, st.Corner);
                        if (st.BgOpacity > 0)
                        {
                            int a = Math.Max(0, Math.Min(255, st.BgOpacity * 255 / 100));
                            Theme.FillRR(g, rc, rad, Color.FromArgb(a, st.BgColor.R, st.BgColor.G, st.BgColor.B));
                            Theme.DrawRR(g, rc, rad, Color.FromArgb(Math.Min(55, a / 3), 255, 255, 255), 1f);
                        }
                        else if (EditMode)
                        {
                            Theme.FillRR(g, rc, rad, Color.FromArgb(48, 217, 119, 87));
                        }

                        if (EditMode)
                        {
                            Theme.DrawRR(g, rc, rad, Color.FromArgb(245, 217, 119, 87), 2f);
                            RectangleF tag = new RectangleF(8f, 8f, Math.Min(Width - 16f, 220f), 24f);
                            Theme.FillRR(g, tag, 6f, Color.FromArgb(238, 201, 100, 66));
                            using (Font ft = Theme.UiFont(11.5f, true))
                            using (StringFormat sfL = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
                            using (SolidBrush wBr = new SolidBrush(Color.White))
                            {
                                string lbl = DisplayNumber == 0 ? "Hasil Gabungan" : ("Hasil " + DisplayNumber);
                                g.DrawString(lbl + "  •  " + Width + " × " + Height, ft, wBr,
                                    new RectangleF(tag.X + 8f, tag.Y, tag.Width - 12f, tag.Height), sfL);
                            }
                            DrawCornerHandles(g, Color.FromArgb(255, 240, 160, 130));
                        }

                        string txt = TextContent;
                        if (string.IsNullOrEmpty(txt) && EditMode) txt = "Hasil terjemahan tampil di sini";

                        if (!string.IsNullOrEmpty(txt))
                        {
                            float pad = Math.Max(6f, st.Padding);
                            float topPad = EditMode ? 36f : pad;
                            if (!string.IsNullOrEmpty(SpeakerName))
                            {
                                using (Font fs = Theme.UiFont(Math.Max(11f, Math.Min(14f, st.FontSize * 0.56f)), true))
                                {
                                    SizeF ssz = TextRenderer.MeasureText(SpeakerName, fs);
                                    float bw = Math.Min(Width - pad * 2f, ssz.Width + 18f);
                                    float bh = 22f;
                                    float bx = st.Align == 1 ? Math.Max(pad, (Width - bw) * 0.5f) : (st.Align == 2 ? Math.Max(pad, Width - pad - bw) : pad);
                                    RectangleF badge = new RectangleF(bx, topPad, bw, bh);
                                    Theme.FillRR(g, badge, 11f, Color.FromArgb(240, 217, 119, 87));
                                    using (StringFormat sfB = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                    using (SolidBrush wBr = new SolidBrush(Color.White))
                                        g.DrawString(SpeakerName, fs, wBr, badge, sfB);
                                    topPad += bh + 4f;
                                }
                            }
                            RectangleF textRc = new RectangleF(pad, topPad, Math.Max(20f, Width - pad * 2f), Math.Max(20f, Height - topPad - pad));
                            DrawOverlayText(g, txt, textRc, st);
                        }
                    }
                }
                Native.PushLayered(Handle, bmp, Location);
            }
        }

        private void DrawCornerHandles(Graphics g, Color c)
        {
            using (SolidBrush br = new SolidBrush(c))
            {
                g.FillEllipse(br, 3f, 3f, 8f, 8f);
                g.FillEllipse(br, Width - 11f, 3f, 8f, 8f);
                g.FillEllipse(br, 3f, Height - 11f, 8f, 8f);
                g.FillEllipse(br, Width - 11f, Height - 11f, 8f, 8f);
            }
        }

        public static void DrawOverlayText(Graphics g, string text, RectangleF rect, OverlayStyle st)
        {
            FontFamily fam;
            try { fam = new FontFamily(st.Font); }
            catch { fam = new FontFamily("Segoe UI"); }

            using (fam)
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = st.Align == 0 ? StringAlignment.Near : (st.Align == 2 ? StringAlignment.Far : StringAlignment.Center);
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisWord;

                int style = st.Bold ? (int)FontStyle.Bold : (int)FontStyle.Regular;
                float em = Math.Max(11f, st.FontSize);

                if (st.AutoFit && rect.Width > 30f && rect.Height > 20f)
                {
                    while (em > 12f)
                    {
                        using (Font measureF = new Font(fam, em, st.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                        {
                            SizeF sz = g.MeasureString(text, measureF, (int)rect.Width);
                            if (sz.Height <= rect.Height + 4f) break;
                        }
                        em -= 1.5f;
                    }
                }

                if (st.Shadow)
                {
                    using (GraphicsPath sp = new GraphicsPath())
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                    {
                        sp.AddString(text, fam, style, em, new RectangleF(rect.X + 2f, rect.Y + 2.5f, rect.Width, rect.Height), sf);
                        g.FillPath(sb, sp);
                    }
                }

                using (GraphicsPath tp = new GraphicsPath())
                {
                    tp.AddString(text, fam, style, em, rect, sf);
                    if (st.Outline > 0)
                    {
                        using (Pen op = new Pen(st.OutlineColor, st.Outline * 1.5f))
                        {
                            op.LineJoin = LineJoin.Round;
                            g.DrawPath(op, tp);
                        }
                    }
                    using (SolidBrush tb = new SolidBrush(st.TextColor))
                        g.FillPath(tb, tp);
                }
            }
        }

        private int HitEdge(Point p)
        {
            int edge = 0;
            const int g = 14;
            if (p.X <= g) edge |= 1;
            if (p.X >= Width - g) edge |= 2;
            if (p.Y <= g) edge |= 4;
            if (p.Y >= Height - g) edge |= 8;
            return edge;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!EditMode) return;
            if (e.Button == MouseButtons.Right && _onExitEdit != null) { _onExitEdit(); return; }
            if (e.Button == MouseButtons.Left)
            {
                _startMouse = Control.MousePosition;
                _startBounds = Bounds;
                _resizeEdge = HitEdge(e.Location);
                _dragging = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!EditMode) return;
            if (!_dragging)
            {
                int ed = HitEdge(e.Location);
                if (ed == 5 || ed == 10) Cursor = Cursors.SizeNWSE;
                else if (ed == 6 || ed == 9) Cursor = Cursors.SizeNESW;
                else if ((ed & 3) != 0) Cursor = Cursors.SizeWE;
                else if ((ed & 12) != 0) Cursor = Cursors.SizeNS;
                else Cursor = Cursors.SizeAll;
                return;
            }

            Point cur = Control.MousePosition;
            int dx = cur.X - _startMouse.X;
            int dy = cur.Y - _startMouse.Y;
            Rectangle nb = _startBounds;

            if (_resizeEdge == 0) { nb.X += dx; nb.Y += dy; }
            else
            {
                if ((_resizeEdge & 1) != 0) { int nx = Math.Min(_startBounds.Right - 60, _startBounds.X + dx); nb.Width = _startBounds.Right - nx; nb.X = nx; }
                if ((_resizeEdge & 2) != 0) nb.Width = Math.Max(60, _startBounds.Width + dx);
                if ((_resizeEdge & 4) != 0) { int ny = Math.Min(_startBounds.Bottom - 28, _startBounds.Y + dy); nb.Height = _startBounds.Bottom - ny; nb.Y = ny; }
                if ((_resizeEdge & 8) != 0) nb.Height = Math.Max(28, _startBounds.Height + dy);
            }

            Bounds = nb;
            Render(_cfg);
            if (_onGeometryChanged != null) _onGeometryChanged(this);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; _resizeEdge = 0; }
    }

    // =========================================================================
    // 4. OVERLAY MANAGER
    // =========================================================================
    public class OverlayManager : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly List<OverlayPane> _resultPanes = new List<OverlayPane>();
        private readonly List<OverlayPane> _ocrPanes = new List<OverlayPane>();
        private EditBannerForm _banner;
        private readonly Timer _topmostTimer;

        public bool LiveVisible { get; private set; }
        public bool EditMode { get; private set; }
        public bool UserHidden { get; set; }

        public event Action OnLayoutUpdated;
        public event Action OnRequestAddArea;
        public event Action OnEditModeEnded;

        public OverlayManager(AppConfig cfg)
        {
            _cfg = cfg;
            _topmostTimer = new Timer();
            _topmostTimer.Interval = 400;
            _topmostTimer.Tick += delegate
            {
                foreach (OverlayPane p in _resultPanes) p.KeepTopmost();
                foreach (OverlayPane p in _ocrPanes) p.KeepTopmost();
            };
            _topmostTimer.Start();
        }

        public void SetLiveVisible(bool visible) { LiveVisible = visible; Rebuild(); }

        public void SetEditMode(bool edit)
        {
            if (EditMode == edit) return;
            EditMode = edit;
            Rebuild();
            if (!edit && OnEditModeEnded != null) OnEditModeEnded();
        }

        public void ShowPreviewSample()
        {
            LiveVisible = true;
            UserHidden = false;
            lock (_cfg.Sync)
            {
                for (int i = 0; i < _cfg.Areas.Count; i++)
                {
                    if (_cfg.Areas[i].Enabled && string.IsNullOrEmpty(_cfg.Areas[i].LastText))
                    {
                        if (_cfg.Areas[i].Mode == AreaTextMode.Choice)
                        {
                            _cfg.Areas[i].LastSpeaker = "";
                            _cfg.Areas[i].LastText = "• Aku belum yakin.\n• Aku ikut!";
                        }
                        else
                        {
                            _cfg.Areas[i].LastSpeaker = _cfg.DetectSpeaker ? "Saria" : "";
                            _cfg.Areas[i].LastText = "Kamu harus cepat. Susul aku nanti!";
                        }
                    }
                }
            }
            Rebuild();
        }

        public void UpdateTexts()
        {
            if (!LiveVisible && !EditMode) return;
            if (UserHidden && !EditMode) { ClearWindows(); return; }

            if (_cfg.Placement == 2)
            {
                if (_resultPanes.Count != 1) { Rebuild(); return; }
                string merged = BuildMergedText();
                OverlayPane p = _resultPanes[0];
                p.TextContent = merged;
                bool show = EditMode || !(_cfg.HideEmpty && string.IsNullOrEmpty(merged));
                if (show) { if (!p.Visible) p.Show(); p.Render(_cfg); p.KeepTopmost(); }
                else if (p.Visible) p.Hide();
                return;
            }

            foreach (OverlayPane p in _resultPanes)
            {
                Area a = _cfg.FindArea(p.AreaId);
                if (a == null) continue;
                p.TextContent = a.LastText ?? "";
                p.SpeakerName = a.LastSpeaker ?? "";
                bool show = EditMode || (a.Enabled && !(_cfg.HideEmpty && string.IsNullOrEmpty(p.TextContent)));
                if (show) { if (!p.Visible) p.Show(); p.Render(_cfg); p.KeepTopmost(); }
                else if (p.Visible) p.Hide();
            }
        }

        private string BuildMergedText()
        {
            List<string> lines = new List<string>();
            lock (_cfg.Sync)
            {
                foreach (Area a in _cfg.Areas)
                    if (a.Enabled && !string.IsNullOrEmpty(a.LastText)) lines.Add(a.LastText);
            }
            return string.Join("\n", lines.ToArray());
        }

        public void Rebuild()
        {
            ClearWindows();
            if (!LiveVisible && !EditMode) return;
            if (UserHidden && !EditMode) return;

            List<Area> list;
            lock (_cfg.Sync)
            {
                list = new List<Area>(_cfg.Areas);
                if (_cfg.MergedRect.Width < 120 || _cfg.MergedRect.Height < 40)
                {
                    Rectangle pri = Screen.PrimaryScreen.Bounds;
                    int mw = Math.Min(980, pri.Width * 58 / 100);
                    _cfg.MergedRect = new Rectangle(pri.X + (pri.Width - mw) / 2, pri.Bottom - 160, mw, 110);
                }
            }

            if (_cfg.Placement == 2)
            {
                string merged = BuildMergedText();
                OverlayPane mp = new OverlayPane(0, 0, false, _cfg, HandlePaneMoved, delegate { SetEditMode(false); });
                mp.EditMode = EditMode;
                mp.Bounds = _cfg.MergedRect;
                mp.TextContent = merged;
                if (EditMode || !(_cfg.HideEmpty && string.IsNullOrEmpty(merged))) { mp.Show(); mp.Render(_cfg); }
                _resultPanes.Add(mp);
            }
            else
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Area a = list[i];
                    if (!a.Enabled && !EditMode) continue;
                    Rectangle box = (_cfg.Placement == 1) ? a.Ocr : a.Result;
                    OverlayPane rp = new OverlayPane(a.Id, i + 1, false, _cfg, HandlePaneMoved, delegate { SetEditMode(false); });
                    rp.EditMode = EditMode;
                    rp.Bounds = box;
                    rp.TextContent = a.LastText ?? "";
                    if (EditMode || (a.Enabled && !(_cfg.HideEmpty && string.IsNullOrEmpty(rp.TextContent)))) { rp.Show(); rp.Render(_cfg); }
                    _resultPanes.Add(rp);
                }
            }

            if (EditMode && _cfg.Placement != 1)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Area a = list[i];
                    OverlayPane op = new OverlayPane(a.Id, i + 1, true, _cfg, HandlePaneMoved, delegate { SetEditMode(false); });
                    op.EditMode = true;
                    op.Bounds = a.Ocr;
                    op.Show();
                    op.Render(_cfg);
                    _ocrPanes.Add(op);
                }
            }

            if (EditMode)
            {
                _banner = new EditBannerForm(
                    delegate { SetEditMode(false); if (OnRequestAddArea != null) OnRequestAddArea(); },
                    delegate { SetEditMode(false); });
                _banner.Show();
            }
        }

        private void HandlePaneMoved(OverlayPane pane)
        {
            lock (_cfg.Sync)
            {
                if (pane.AreaId == 0 && !pane.IsOcrFrame) _cfg.MergedRect = pane.Bounds;
                else
                {
                    Area a = _cfg.FindArea(pane.AreaId);
                    if (a != null)
                    {
                        if (pane.IsOcrFrame || _cfg.Placement == 1) a.Ocr = pane.Bounds;
                        else a.Result = pane.Bounds;
                    }
                }
            }
            _cfg.Save();
            if (OnLayoutUpdated != null) OnLayoutUpdated();
        }

        public void ClearWindows()
        {
            if (_banner != null) { _banner.Close(); _banner.Dispose(); _banner = null; }
            foreach (OverlayPane p in _resultPanes) { p.Close(); p.Dispose(); }
            foreach (OverlayPane p in _ocrPanes) { p.Close(); p.Dispose(); }
            _resultPanes.Clear();
            _ocrPanes.Clear();
        }

        public void Dispose() { _topmostTimer.Stop(); _topmostTimer.Dispose(); ClearWindows(); }
    }
}
