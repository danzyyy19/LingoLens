using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TranslatorLive
{
    public class MainWindow : Form
    {
        [DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
        private static extern bool ReleaseCaptureNative();
        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        private static extern IntPtr SendMessageNative(IntPtr h, int m, int w, int l);

        private enum HitKind { Button, Slider, Dropdown, DropItem }

        private class Hit
        {
            public string Id;
            public HitKind Kind;
            public RectangleF Box;
            public int Index;
            public int Min;
            public int Max;
            public bool Enabled = true;
        }

        private readonly AppConfig _cfg;
        private readonly TranslationEngine _engine;
        private readonly OverlayManager _overlay;

        private int _tab = 0; // 0 = Live & Area, 1 = Tampilan, 2 = Sistem, 3 = Pembaruan
        private int _styleTargetAreaId = 0; // 0 = Global, >0 = specific Area.Id
        private int _selectedAreaId = -1;
        private bool _isMaximized = false;
        private bool _isFullscreen = false;
        private Rectangle _restoreBounds;
        private bool _forceExit = false;

        private readonly List<Hit> _hits = new List<Hit>();
        private string _hoverId = "";
        private int _hoverIdx = -1;
        private string _activeSlider = "";

        private float _scrollY = 0f;
        private float _maxScrollY = 0f;

        private string _openDrop = "";
        private RectangleF _dropAnchor;
        private RectangleF _dropPopup;
        private int _dropScroll = 0;

        // Live state
        private string _liveSource = "";
        private string _liveResult = "";
        private string _liveStatus = "Siap";
        private int _lastMs = 0;
        private bool _copiedFlash = false;
        private string _toastText = "";
        private Color _toastColor = Color.FromArgb(16, 185, 129);

        private readonly Timer _toastTimer;
        private readonly Timer _copyTimer;
        private readonly NotifyIcon _tray;
        private readonly ToolTip _toolTip;
        private string _lastTipId = "";
        private int _lastTipIdx = -1;
        private Image _logoBmp;
        private bool _showUpdateBanner = false;
        private bool _dismissUpdateBanner = false;

        private string T(string id, string en) { return _cfg.UiLang == "en" ? en : id; }

        private static readonly string[,] SrcLangs = new string[,]
        {
            { "en", "English" }, { "ja", "Japanese" }, { "ko", "Korean" },
            { "zh-CN", "Chinese (Simple)" }, { "zh-TW", "Chinese (Trad)" },
            { "ru", "Russian" }, { "es", "Spanish" }, { "fr", "French" },
            { "de", "German" }, { "auto", "Auto Detect" }
        };

        private static readonly string[,] TgtLangs = new string[,]
        {
            { "id", "Indonesia" }, { "en", "English" }, { "ms", "Melayu" },
            { "ja", "Japanese" }, { "ko", "Korean" }, { "zh-CN", "Chinese" }
        };

        private static readonly Color[] QuickTextColors = new Color[]
        {
            Color.White,
            Color.FromArgb(255, 244, 218),
            Color.FromArgb(255, 225, 77),
            Color.FromArgb(147, 210, 255),
            Color.FromArgb(134, 239, 172)
        };

        private readonly List<string> _fonts = new List<string>();
        private float _dpiScale = 1.0f;

        private float Z { get { return Math.Max(0.80f, Math.Min(2.65f, _dpiScale * (_cfg.UiZoom / 100f))); } }
        private float S(float v) { return (float)Math.Round(v * Z); }
        private RectangleF SR(float x, float y, float w, float h)
        {
            return new RectangleF((float)Math.Round(x), (float)Math.Round(y), (float)Math.Round(w), (float)Math.Round(h));
        }

        public MainWindow(AppConfig cfg)
        {
            _cfg = cfg;
            Rectangle scrInit = Screen.PrimaryScreen.WorkingArea;
            try
            {
                uint sysDpi = Native.GetDpiForSystem();
                if (sysDpi >= 96) _dpiScale = Math.Max(1.0f, Math.Min(2.25f, sysDpi / 96f));
            }
            catch { _dpiScale = 1.0f; }
            // Auto resolution-aware scaling for 2K (2560px) and 4K (3840px) monitors running at 100% Windows DPI
            if (scrInit.Width >= 3200 && _dpiScale < 1.45f) _dpiScale = 1.45f;
            else if (scrInit.Width >= 2400 && _dpiScale < 1.18f) _dpiScale = 1.18f;

            if (_cfg.Areas.Count == 0)
            {
                Rectangle pri = Screen.PrimaryScreen.Bounds;
                int w = Math.Min(920, pri.Width * 56 / 100);
                int x = pri.X + (pri.Width - w) / 2;
                Area defA = new Area();
                defA.Id = _cfg.NextId++;
                defA.Enabled = true;
                defA.Mode = AreaTextMode.Dialog;
                defA.Ocr = new Rectangle(x, pri.Bottom - 260, w, 110);
                defA.Result = AppConfig.DefaultResultFor(defA.Ocr);
                _cfg.Areas.Add(defA);
            }
            _selectedAreaId = _cfg.Areas[0].Id;

            Text = "LingoLens — by Danzyyy";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            int initW = Math.Min((int)Math.Round(1240 * _dpiScale), scrInit.Width * 88 / 100);
            int initH = Math.Min((int)Math.Round(810 * _dpiScale), scrInit.Height * 88 / 100);
            Size = new Size(Math.Max(860, initW), Math.Max(600, initH));
            MinimumSize = new Size((int)Math.Round(780 * _dpiScale), (int)Math.Round(540 * _dpiScale));
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try
            {
                using (System.IO.Stream rs = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("LingoLensIcon.png"))
                {
                    if (rs != null) _logoBmp = Image.FromStream(rs);
                }
            }
            catch { }
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            _restoreBounds = new Rectangle(scr.X + (scr.Width - Width) / 2, scr.Y + (scr.Height - Height) / 2, Width, Height);

            LoadFonts();

            _overlay = new OverlayManager(_cfg);
            _overlay.OnLayoutUpdated += delegate { Invalidate(); };
            _overlay.OnRequestAddArea += delegate { BeginDesktopSnip(-1, SnipTargetKind.Ocr); };
            _overlay.OnEditModeEnded += delegate
            {
                _engine.SetPausedForEdit(false);
                _engine.ForceScanNow();
                ShowToast("Posisi & ukuran disimpan", Color.FromArgb(45, 168, 122));
                Invalidate();
            };

            _engine = new TranslationEngine(_cfg);
            _engine.OnAreaTranslated += OnEngineAreaTranslated;
            _engine.OnTargetWindowActiveChanged += delegate(bool active)
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((Action)delegate
                        {
                            _overlay.SetTargetPaused(!active);
                            if (!active && !string.IsNullOrEmpty(_cfg.TargetProcessName))
                                _liveStatus = T("Siaga · Menunggu jendela " + _cfg.TargetProcessName, "Armed · Waiting for " + _cfg.TargetProcessName);
                            else if (active)
                                _liveStatus = T("Aktif · " + _cfg.TargetProcessName, "Active · " + _cfg.TargetProcessName);
                            Invalidate();
                        });
                    }
                }
                catch { }
            };

            _toastTimer = new Timer { Interval = 2400 };
            _toastTimer.Tick += delegate { _toastText = ""; _toastTimer.Stop(); Invalidate(); };

            _copyTimer = new Timer { Interval = 1400 };
            _copyTimer.Tick += delegate { _copiedFlash = false; _copyTimer.Stop(); Invalidate(); };

            _toolTip = new ToolTip();
            _toolTip.InitialDelay = 350;
            _toolTip.ReshowDelay = 80;
            _toolTip.AutoPopDelay = 6000;
            _toolTip.ShowAlways = true;
            _toolTip.OwnerDraw = true;
            _toolTip.Draw += delegate(object s, DrawToolTipEventArgs ev)
            {
                Palette pal = Palette.For(_cfg.Dark);
                ev.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                ev.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Rectangle r = ev.Bounds;
                Color bgCol = pal.Dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(255, 255, 255);
                Color bdrCol = pal.Dark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(228, 228, 231);
                Color txtCol = pal.Dark ? Color.FromArgb(244, 244, 245) : Color.FromArgb(24, 24, 27);

                Theme.FillRR(ev.Graphics, r, 6f, bgCol);
                Theme.DrawRR(ev.Graphics, r, 6f, bdrCol, 1f);
                using (Font tf = Theme.UiFont(9.5f, false))
                {
                    Rectangle textRect = new Rectangle(r.X + 8, r.Y + 4, r.Width - 16, r.Height - 8);
                    TextRenderer.DrawText(ev.Graphics, ev.ToolTipText, tf, textRect, txtCol,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
            };
            _toolTip.Popup += delegate(object s, PopupEventArgs ev)
            {
                using (Font tf = Theme.UiFont(9.5f, false))
                {
                    string txt = _toolTip.GetToolTip(this);
                    Size sz = TextRenderer.MeasureText(txt, tf);
                    ev.ToolTipSize = new Size(sz.Width + 20, Math.Max(26, sz.Height + 10));
                }
            };

            _tray = new NotifyIcon();
            _tray.Text = "LingoLens by Danzyyy";
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Visible = true;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("Buka LingoLens", null, delegate { RestoreFromTray(); });
            m.Items.Add("Mulai / Hentikan Live (Alt+E)", null, delegate { ToggleLive(); });
            m.Items.Add("Blok Area OCR Baru (Alt+Q)", null, delegate { BeginDesktopSnip(-1, SnipTargetKind.Ocr); });
            m.Items.Add("Blok Area Hasil (Alt+A)", null, delegate { BeginDesktopSnip(_selectedAreaId, SnipTargetKind.Result); });
            m.Items.Add("Edit Semua di Layar (Alt+W)", null, delegate { ToggleEditOnScreen(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Keluar", null, delegate { _forceExit = true; Close(); });
            _tray.ContextMenuStrip = m;
            _tray.DoubleClick += delegate { RestoreFromTray(); };
            _tray.BalloonTipClicked += delegate
            {
                RestoreFromTray();
                _tab = 3;
                _scrollY = 0f;
                Invalidate();
            };

            UpdateChecker.OnStatusChanged += delegate
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (UpdateChecker.Info.HasUpdate && !_dismissUpdateBanner)
                            {
                                _showUpdateBanner = true;
                                if (_cfg.TrayTipShown && _tray != null)
                                {
                                    _tray.ShowBalloonTip(3500, "LingoLens Update Tersedia",
                                        "Versi " + UpdateChecker.Info.LatestVersion + " telah dirilis oleh Danzyyy! Klik untuk memperbarui.",
                                        ToolTipIcon.Info);
                                }
                            }
                            Invalidate();
                        }));
                    }
                }
                catch { }
            };

            if (_cfg.AutoCheckUpdate)
            {
                UpdateChecker.CheckAsync(silent: true);
            }
        }

        private void LoadFonts()
        {
            string[] pref = new string[] { "Segoe UI Variable Display", "Segoe UI", "Georgia", "Bahnschrift", "Arial", "Trebuchet MS", "Verdana", "Tahoma" };
            foreach (string p in pref) _fonts.Add(p);
            foreach (FontFamily ff in FontFamily.Families)
            {
                if (!string.IsNullOrEmpty(ff.Name) && !ff.Name.StartsWith("@") && !_fonts.Contains(ff.Name))
                    _fonts.Add(ff.Name);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            float winDpi = Native.DpiScale(Handle);
            if (winDpi >= 1.0f) _dpiScale = Math.Min(2.25f, winDpi);
            ApplyDwm();
            RegisterHotkeys();
            if (_cfg.AutoLive) ToggleLive();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceExit && _cfg.CloseToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            UnregisterHotkeys();
            _cfg.Save();
            _engine.Dispose();
            _overlay.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosing(e);
        }

        private void ApplyDwm()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = _cfg.Dark ? 1 : 0;
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                int corner = (_isFullscreen || _isMaximized) ? 1 : 2;
                Native.DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
            }
            catch { }
        }

        private void RegisterHotkeys()
        {
            List<string> failed = new List<string>();
            if (!Native.RegisterHotKey(Handle, 1, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.E)) failed.Add("Alt+E");
            if (!Native.RegisterHotKey(Handle, 2, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.Q)) failed.Add("Alt+Q");
            if (!Native.RegisterHotKey(Handle, 3, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.W)) failed.Add("Alt+W");
            if (!Native.RegisterHotKey(Handle, 4, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.R)) failed.Add("Alt+R");
            if (!Native.RegisterHotKey(Handle, 5, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.H)) failed.Add("Alt+H");
            if (!Native.RegisterHotKey(Handle, 6, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.A)) failed.Add("Alt+A");
            if (failed.Count > 0)
            {
                string list = string.Join(", ", failed.ToArray());
                AppLogger.Error("Hotkeys", "Global hotkey conflict: " + list);
                ShowToast(T("Hotkey bentrok: " + list, "Hotkey conflict: " + list), Color.FromArgb(217, 119, 6));
            }
        }

        private void UnregisterHotkeys()
        {
            for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(Handle, i);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) ToggleLive();
                else if (id == 2) BeginDesktopSnip(-1, SnipTargetKind.Ocr);
                else if (id == 3) ToggleEditOnScreen();
                else if (id == 4) TriggerManualScan();
                else if (id == 5)
                {
                    _overlay.UserHidden = !_overlay.UserHidden;
                    _overlay.Rebuild();
                    ShowToast(_overlay.UserHidden ? "Overlay disembunyikan" : "Overlay ditampilkan", Color.FromArgb(16, 185, 129));
                }
                else if (id == 6)
                {
                    int targetId = _cfg.Placement == 2 ? 0 : _selectedAreaId;
                    BeginDesktopSnip(targetId, SnipTargetKind.Result);
                }
                return;
            }

            if (m.Msg == Native.WM_DPICHANGED)
            {
                int newDpi = m.WParam.ToInt32() & 0xFFFF;
                if (newDpi >= 96)
                {
                    _dpiScale = Math.Max(1.0f, Math.Min(2.25f, newDpi / 96f));
                    MinimumSize = new Size((int)Math.Round(780 * _dpiScale), (int)Math.Round(540 * _dpiScale));
                    if (m.LParam != IntPtr.Zero)
                    {
                        Native.RECT suggested = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                        Bounds = new Rectangle(suggested.Left, suggested.Top,
                            Math.Max(MinimumSize.Width, suggested.Right - suggested.Left),
                            Math.Max(MinimumSize.Height, suggested.Bottom - suggested.Top));
                    }
                    Invalidate();
                }
                return;
            }

            if (m.Msg == Native.WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                if (m.LParam != IntPtr.Zero)
                {
                    Native.MINMAXINFO mmi = (Native.MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(Native.MINMAXINFO));
                    mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, MinimumSize.Width);
                    mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, MinimumSize.Height);
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                }
                return;
            }

            if (m.Msg == Native.WM_NCHITTEST && !_isFullscreen && !_isMaximized)
            {
                base.WndProc(ref m);
                Point pt = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
                const int g = 7;
                if (pt.X <= g && pt.Y <= g) { m.Result = (IntPtr)Native.HTTOPLEFT; return; }
                if (pt.X >= Width - g && pt.Y <= g) { m.Result = (IntPtr)Native.HTTOPRIGHT; return; }
                if (pt.X <= g && pt.Y >= Height - g) { m.Result = (IntPtr)Native.HTBOTTOMLEFT; return; }
                if (pt.X >= Width - g && pt.Y >= Height - g) { m.Result = (IntPtr)Native.HTBOTTOMRIGHT; return; }
                if (pt.X <= g) { m.Result = (IntPtr)Native.HTLEFT; return; }
                if (pt.X >= Width - g) { m.Result = (IntPtr)Native.HTRIGHT; return; }
                if (pt.Y <= g) { m.Result = (IntPtr)Native.HTTOP; return; }
                if (pt.Y >= Height - g) { m.Result = (IntPtr)Native.HTBOTTOM; return; }
                return;
            }

            base.WndProc(ref m);
        }

        private void OnEngineAreaTranslated(object sender, ScanUpdateEventArgs e)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<object, ScanUpdateEventArgs>(OnEngineAreaTranslated), sender, e); }
                catch { }
                return;
            }

            // Bug fix: discard late async result if user already paused/stopped Live!
            if (!_engine.IsRunning) return;

            if (e.ClearedByIdle)
            {
                lock (_cfg.Sync)
                {
                    Area a = _cfg.FindArea(e.AreaId);
                    if (a != null)
                    {
                        a.LastText = "";
                        a.LastSpeaker = "";
                    }
                }
                _liveStatus = "Idle · overlay disembunyikan";
            }
            else
            {
                lock (_cfg.Sync)
                {
                    Area a = _cfg.FindArea(e.AreaId);
                    if (a != null)
                    {
                        a.LastSource = e.SourceText;
                        a.LastText = e.TranslatedText;
                        a.LastSpeaker = e.SpeakerName ?? "";
                    }
                }
                if (!string.IsNullOrEmpty(e.SourceText)) _liveSource = e.SourceText;
                if (!string.IsNullOrEmpty(e.TranslatedText))
                    _liveResult = string.IsNullOrEmpty(e.SpeakerName) ? e.TranslatedText : ("[" + e.SpeakerName + "] " + e.TranslatedText);
                if (e.LatencyMs > 0) _lastMs = e.LatencyMs;
                if (!string.IsNullOrEmpty(e.StatusMessage)) _liveStatus = e.StatusMessage;
            }

            _overlay.UpdateTexts();
            Invalidate();
        }

        public void ToggleMaximize()
        {
            if (_isFullscreen) { ToggleFullscreen(); return; }
            _isMaximized = !_isMaximized;
            if (_isMaximized) { _restoreBounds = Bounds; Bounds = Screen.FromControl(this).WorkingArea; }
            else Bounds = _restoreBounds;
            ApplyDwm(); Invalidate();
        }

        public void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            if (_isFullscreen) { if (!_isMaximized) _restoreBounds = Bounds; _isMaximized = false; Bounds = Screen.FromControl(this).Bounds; }
            else Bounds = _restoreBounds;
            ApplyDwm(); Invalidate();
        }

        public void AdjustZoom(int delta)
        {
            int next = Math.Max(80, Math.Min(175, _cfg.UiZoom + delta));
            if (next != _cfg.UiZoom)
            {
                _cfg.UiZoom = next;
                _cfg.Save();
                _scrollY = 0f;
                ShowToast("Zoom " + next + "%", Palette.For(_cfg.Dark).Live);
                Invalidate();
            }
        }

        public void SetPreviewState(int tab, bool dark)
        {
            _tab = tab; _cfg.Dark = dark; _scrollY = 0f;
            ApplyDwm(); Invalidate();
        }

        private void HideToTray()
        {
            Hide();
            if (!_cfg.TrayTipShown)
            {
                _cfg.TrayTipShown = true;
                _cfg.Save();
                _tray.ShowBalloonTip(2200, "LingoLens berjalan di background",
                    "Alt+E (Live) • Alt+Q (Blok OCR) • Alt+A (Blok Hasil) • Alt+W (Edit Layar)", ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        private void ShowToast(string text, Color dot)
        {
            _toastText = text; _toastColor = dot;
            _toastTimer.Stop(); _toastTimer.Start();
            Invalidate();
        }

        private void ToggleLive()
        {
            bool next = !_engine.IsRunning;
            if (next)
            {
                int active = 0;
                lock (_cfg.Sync) foreach (Area a in _cfg.Areas) if (a.Enabled) active++;
                if (active == 0) { ShowToast("Aktifkan minimal 1 area dulu", Color.FromArgb(229, 92, 92)); return; }
            }

            _engine.SetRunning(next);
            try { if (IsHandleCreated) Native.SetWindowDisplayAffinity(Handle, next ? 0x00000011u : 0x00000000u); } catch { }
            _overlay.UserHidden = false;
            _overlay.SetLiveVisible(next);
            _liveStatus = next ? "Memindai..." : "Berhenti";
            _tray.Text = next ? "LingoLens (Aktif)" : "LingoLens by Danzyyy";
            ShowToast(next ? "Terjemahan Live aktif" : "Terjemahan dijeda",
                next ? Color.FromArgb(45, 168, 122) : Color.FromArgb(140, 136, 128));
        }

        private void TriggerManualScan()
        {
            if (!_engine.IsRunning) ToggleLive();
            else { _engine.ForceScanNow(); ShowToast("Memindai ulang layar...", Color.FromArgb(16, 185, 129)); }
        }

        private void ToggleEditOnScreen()
        {
            bool next = !_overlay.EditMode;
            _engine.SetPausedForEdit(next);
            _overlay.SetEditMode(next);
            if (next) ShowToast("Geser & ubah ukuran di layar desktop, tekan ESC selesai", Color.FromArgb(16, 185, 129));
            Invalidate();
        }

        private void BeginDesktopSnip(int targetAreaId, SnipTargetKind kind)
        {
            if (_overlay.EditMode)
            {
                _overlay.SetEditMode(false);
                _engine.SetPausedForEdit(false);
            }

            Rectangle refRect = Rectangle.Empty;
            string refLabel = "";
            string title;

            lock (_cfg.Sync)
            {
                if (kind == SnipTargetKind.Result && targetAreaId == 0)
                {
                    title = "Atur Lebar & Tinggi Kotak Hasil Gabungan";
                    if (_cfg.Areas.Count > 0) { refRect = _cfg.Areas[0].Ocr; refLabel = "Referensi OCR 1"; }
                }
                else if (targetAreaId > 0)
                {
                    Area ex = _cfg.FindArea(targetAreaId);
                    int num = _cfg.AreaNumber(targetAreaId);
                    if (kind == SnipTargetKind.Ocr)
                    {
                        title = "Blok Area OCR " + num;
                        if (ex != null) { refRect = ex.Result; refLabel = "Kotak Hasil " + num; }
                    }
                    else
                    {
                        title = "Blok Lebar & Tinggi Kotak Hasil " + num;
                        if (ex != null) { refRect = ex.Ocr; refLabel = "Area OCR " + num; }
                    }
                }
                else title = "Blok Area OCR Baru";
            }

            bool wasVisible = Visible;
            if (wasVisible) Hide();

            Timer delay = new Timer { Interval = 110 };
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();

                SnipperForm snipper = new SnipperForm(title, kind, refRect, refLabel, delegate(bool ok, Rectangle rc)
                {
                    if (ok)
                    {
                        lock (_cfg.Sync)
                        {
                            if (kind == SnipTargetKind.Result && targetAreaId == 0) _cfg.MergedRect = rc;
                            else if (targetAreaId > 0)
                            {
                                Area ex = _cfg.FindArea(targetAreaId);
                                if (ex != null)
                                {
                                    if (kind == SnipTargetKind.Ocr) { ex.Ocr = rc; _engine.ClearAreaState(ex.Id); }
                                    else ex.Result = rc;
                                    ex.Enabled = true;
                                    _selectedAreaId = ex.Id;
                                }
                            }
                            else
                            {
                                Area na = new Area();
                                na.Id = _cfg.NextId++;
                                na.Enabled = true;
                                na.Mode = _cfg.Areas.Count >= 1 ? AreaTextMode.Choice : AreaTextMode.Dialog;
                                if (na.Mode == AreaTextMode.Choice)
                                {
                                    na.Style = _cfg.GlobalStyle.Clone();
                                    na.Style.UseCustom = true;
                                    na.Style.FontSize = 18;
                                    na.Style.Align = 0; // Left-aligned choices
                                }
                                na.Ocr = rc;
                                na.Result = AppConfig.DefaultResultFor(rc);
                                _cfg.Areas.Add(na);
                                _selectedAreaId = na.Id;
                            }
                        }
                        _cfg.Save();
                        _overlay.Rebuild();
                        if (kind == SnipTargetKind.Ocr)
                        {
                            if (!_engine.IsRunning) ToggleLive();
                            else _engine.ForceScanNow();
                        }
                        else _overlay.ShowPreviewSample();

                        ShowToast((kind == SnipTargetKind.Ocr ? "Area OCR" : "Kotak Hasil") + ": " + rc.Width + " × " + rc.Height + " px",
                            Color.FromArgb(45, 168, 122));
                    }

                    if (wasVisible) { Show(); Activate(); }
                    Invalidate();
                });
                snipper.Show();
            };
            delay.Start();
        }

        private OverlayStyle ActiveStyle
        {
            get
            {
                if (_styleTargetAreaId > 0)
                {
                    Area a = _cfg.FindArea(_styleTargetAreaId);
                    if (a != null) return a.Style;
                }
                return _cfg.GlobalStyle;
            }
        }

        private void PromptSaveGameProfile()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildSaveProfileModal(_cfg, _dpiScale, pal, _cfg.UiLang == "en", delegate
            {
                ShowToast("Template \"" + _cfg.ActiveProfile + "\" tersimpan", pal.Live);
                Invalidate();
            }))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptGeminiKey()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildGeminiKeyModal(_cfg, _dpiScale, pal, _cfg.UiLang == "en", delegate
            {
                _engine.ForceScanNow();
                ShowToast("Gemini API Key disimpan", pal.Live);
                Invalidate();
            }))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptGlossary()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildGlossaryModal(_cfg, _dpiScale, pal, _cfg.UiLang == "en", delegate
            {
                _engine.ClearState();
                _engine.ForceScanNow();
                ShowToast("Glosarium game disimpan", pal.Live);
                Invalidate();
            }))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptGameLorePicker()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildGameLoreModal(_cfg, _dpiScale, pal, _cfg.UiLang == "en", delegate
            {
                _engine.ClearState();
                _engine.ForceScanNow();
                ShowToast("Game aktif: " + (string.IsNullOrEmpty(_cfg.ActiveGameTitle) ? "Umum" : _cfg.ActiveGameTitle), pal.Live);
                Invalidate();
            }))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptDialogBacklog()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildDialogBacklogModal(_engine.GetDialogHistory(), _dpiScale, pal, _cfg.UiLang == "en",
                delegate { _engine.ClearDialogHistory(); },
                delegate { ShowToast("Riwayat disalin", pal.Live); }))
            {
                dlg.ShowDialog(this);
            }
        }

        private void PromptWindowLockPicker()
        {
            Palette pal = Palette.For(_cfg.Dark);
            using (Form dlg = Modals.BuildWindowLockModal(_cfg, _dpiScale, pal, _cfg.UiLang == "en", delegate
            {
                if (string.IsNullOrEmpty(_cfg.TargetProcessName))
                {
                    _overlay.SetTargetPaused(false);
                    ShowToast(T("Kunci jendela dilepas (Semua Layar)", "Window lock cleared"), pal.Live);
                }
                else
                {
                    ShowToast(T("OCR dikunci ke: ", "Locked OCR to: ") + _cfg.TargetProcessName + ".exe", pal.Live);
                }
                _engine.ForceScanNow();
                Invalidate();
            }))
            {
                dlg.ShowDialog(this);
            }
        }

        // =========================================================================
        // RENDERING
        // =========================================================================
        public void RenderWindow(Graphics g)
        {
            RenderContent(g);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderContent(e.Graphics);
        }

        private void RenderContent(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Default;

            float cw = ClientSize.Width;
            float ch = ClientSize.Height;
            if (cw < 100 || ch < 100) return;

            Palette pal = Palette.For(_cfg.Dark);
            _hits.Clear();

            // Architectural Textured Canvas Background (Diagonal Hatch / Blueprint Grid / Dot Matrix / Solid — ZERO AI-Slop Gradients!)
            Theme.DrawCanvasBackground(g, pal, new RectangleF(0, 0, cw, ch), _cfg.BgPattern);

            using (Font fSerifLg = Theme.SerifFont(S(24f), true))
            using (Font fSerifMd = Theme.SerifFont(S(16.5f), true))
            using (Font fSec = Theme.UiFont(S(15f), true))
            using (Font fBody = Theme.UiFont(S(14f), false))
            using (Font fBodyB = Theme.UiFont(S(14f), true))
            using (Font fSm = Theme.UiFont(S(13f), false))
            using (Font fSmB = Theme.UiFont(S(13f), true))
            using (Font fCap = Theme.UiFont(S(11.8f), true))
            {
                float sideW = S(244f);
                float topH = S(48f);

                // 1. LEFT EDITORIAL SIDEBAR
                using (SolidBrush sb = new SolidBrush(pal.Sidebar))
                using (Pen bp = new Pen(pal.Border, 1f))
                {
                    g.FillRectangle(sb, 0, 0, sideW, ch);
                    g.DrawLine(bp, sideW, 0, sideW, ch);
                }

                RectangleF logo = SR(S(16f), S(14f), S(36f), S(36f));
                if (_logoBmp != null)
                {
                    InterpolationMode prevIm = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_logoBmp, logo);
                    g.InterpolationMode = prevIm;
                }
                else
                {
                    Theme.FillRR(g, logo, S(8f), pal.Accent);
                    Theme.DrawIcon(g, IconKind.Gamepad, SR(logo.X + S(6f), logo.Y + S(6f), S(24f), S(24f)), pal.AccentBtnText, 2f);
                }
                Theme.DrawTextLeft(g, "LingoLens", fSerifMd, pal.Text, SR(S(60f), S(13f), sideW - S(68f), S(22f)));
                Theme.DrawTextLeft(g, "GAMING EDITION", fCap, pal.AccentText, SR(S(60f), S(34f), sideW - S(68f), S(16f)));

                // Hero Live CTA
                bool live = _engine.IsRunning;
                RectangleF liveBtn = SR(S(16f), S(68f), sideW - S(32f), S(42f));
                DrawAccentBtn(g, pal, "BTN_LIVE", liveBtn,
                    live ? T("Hentikan Live", "Stop Live") : T("Mulai Live", "Start Live"),
                    live ? IconKind.Stop : IconKind.Play,
                    live ? pal.Danger : pal.Accent,
                    live ? Color.FromArgb(215, 55, 45) : pal.AccentHover,
                    fBodyB, S(9f));

                using (SolidBrush stDot = new SolidBrush(live ? pal.Live : pal.TextMute))
                    g.FillEllipse(stDot, S(22f), S(119f), S(7f), S(7f));
                string statusTxt = live ? (T("Aktif", "Active") + (_lastMs > 0 ? (" · " + _lastMs + " ms") : "")) : "Standby · Alt+E";
                Theme.DrawTextLeft(g, statusTxt, fSm, live ? pal.Live : pal.TextMute, SR(S(35f), S(113f), sideW - S(48f), S(20f)));

                // Navigation (4 items)
                string[] navNames = new string[] { T("Live & Area", "Live & Areas"), T("Tampilan", "Appearance"), T("Sistem", "System"), T("Pembaruan", "Changelog") };
                IconKind[] navIcons = new IconKind[] { IconKind.ScanArea, IconKind.Palette, IconKind.Settings, IconKind.Rocket };
                float ny = S(138f);
                float itemH = S(32f);
                float itemPitch = S(36f);
                for (int i = 0; i < 4; i++)
                {
                    RectangleF nr = SR(S(12f), ny + i * itemPitch, sideW - S(24f), itemH);
                    bool act = (_tab == i);
                    bool hov = (_hoverId == "TAB_" + i);
                    if (act)
                    {
                        Theme.FillRR(g, nr, S(8f), pal.Card);
                        Theme.DrawRR(g, nr, S(8f), pal.BorderStrong, 1f);
                        Theme.FillRR(g, SR(nr.X + S(3f), nr.Y + S(6f), S(3.5f), nr.Height - S(12f)), S(1.5f), pal.Accent);
                    }
                    else if (hov)
                    {
                        Theme.FillRR(g, nr, S(8f), pal.CardHover);
                    }
                    float isz = S(15f);
                    Theme.DrawIcon(g, navIcons[i], SR(nr.X + S(13f), nr.Y + (nr.Height - isz) * 0.5f, isz, isz),
                        act ? pal.AccentText : pal.TextSub, 1.75f);
                    Theme.DrawTextLeft(g, navNames[i], act ? fBodyB : fBody, act ? pal.Text : pal.TextSub,
                        SR(nr.X + S(36f), nr.Y, nr.Width - S(42f), nr.Height));

                    if (i == 3 && UpdateChecker.Info.HasUpdate)
                    {
                        float badgeW = S(36f), badgeH = S(17f);
                        RectangleF badgeR = SR(nr.Right - badgeW - S(8f), nr.Y + (nr.Height - badgeH) * 0.5f, badgeW, badgeH);
                        Theme.FillRR(g, badgeR, badgeH * 0.5f, pal.Accent);
                        Theme.DrawTextCenter(g, "NEW", fCap, pal.AccentBtnText, badgeR);
                    }

                    _hits.Add(new Hit { Id = "TAB_" + i, Kind = HitKind.Button, Box = nr, Index = i });
                }

                // Quick Sidebar Shortcuts: Target Window Lock + Game & Lore + Backlog
                float qy = ny + 4 * itemPitch + S(10f);
                using (Pen bp = new Pen(pal.Border, 1f)) g.DrawLine(bp, S(16f), qy - S(5f), sideW - S(16f), qy - S(5f));
                bool isLocked = !string.IsNullOrEmpty(_cfg.TargetProcessName);
                DrawSidebarBtn(g, pal, "BTN_LOCK_WINDOW", SR(S(12f), qy, sideW - S(24f), S(30f)),
                    isLocked ? (T("Kunci: ", "Lock: ") + _cfg.TargetProcessName) : T("Kunci Jendela Game", "Lock Game Window"),
                    IconKind.WindowLock, fSmB);
                DrawSidebarBtn(g, pal, "BTN_GAME_LORE", SR(S(12f), qy + S(32f), sideW - S(24f), S(30f)),
                    T("Pilih Game & Lore", "Game & Lore"), IconKind.Book, fSmB);
                DrawSidebarBtn(g, pal, "BTN_HISTORY", SR(S(12f), qy + S(64f), sideW - S(24f), S(30f)),
                    T("Riwayat Dialog", "Dialog Backlog"), IconKind.History, fSm);

                // Sidebar Bottom (Compact 2-column Theme + Pattern row so footer NEVER clips at 100%-150% zoom!)
                float by = Math.Max(qy + S(98f), ch - S(140f));
                using (Pen bp = new Pen(pal.Border, 1f)) g.DrawLine(bp, S(16f), by - S(6f), sideW - S(16f), by - S(6f));

                RectangleF zoomRow = SR(S(16f), by, sideW - S(32f), S(28f));
                Theme.FillRR(g, zoomRow, S(8f), pal.Well);
                Theme.DrawRR(g, zoomRow, S(8f), pal.Border, 1f);
                DrawZoomBtn(g, pal, "ZOOM_OUT", SR(zoomRow.X + S(2f), zoomRow.Y + S(2f), S(30f), zoomRow.Height - S(4f)), IconKind.ZoomOut);
                DrawZoomBtn(g, pal, "ZOOM_IN", SR(zoomRow.Right - S(32f), zoomRow.Y + S(2f), S(30f), zoomRow.Height - S(4f)), IconKind.ZoomIn);
                RectangleF zReset = SR(zoomRow.X + S(34f), zoomRow.Y + S(2f), zoomRow.Width - S(68f), zoomRow.Height - S(4f));
                Theme.DrawTextCenter(g, "Zoom " + _cfg.UiZoom + "%", fCap, pal.TextSub, zReset);
                _hits.Add(new Hit { Id = "ZOOM_RESET", Kind = HitKind.Button, Box = zReset });

                float halfSide = (sideW - S(38f)) * 0.5f;
                string[] patNames = new string[] { T("Arsir", "Hatch"), "Grid", "Dot", T("Polos", "Flat") };
                DrawOutlineBtn(g, pal, "BTN_CYCLE_PATTERN", SR(S(16f), by + S(34f), halfSide, S(30f)),
                    patNames[Math.Max(0, Math.Min(3, _cfg.BgPattern))], IconKind.Pattern, fCap, S(7f));
                DrawOutlineBtn(g, pal, "BTN_THEME", SR(S(16f) + halfSide + S(6f), by + S(34f), halfSide, S(30f)),
                    _cfg.Dark ? T("Terang", "Light") : T("Gelap", "Dark"), _cfg.Dark ? IconKind.Sun : IconKind.Moon, fCap, S(7f));

                DrawSidebarBtn(g, pal, "BTN_TRAY", SR(S(16f), by + S(68f), sideW - S(32f), S(28f)),
                    T("Sembunyikan ke Tray", "Minimize to Tray"), IconKind.Tray, fCap);

                if (ch - (by + S(100f)) >= S(22f))
                    Theme.DrawTextCenter(g, "v0.4 Beta  •  by Danzyyy", fCap, pal.TextMute, SR(S(16f), ch - S(26f), sideW - S(32f), S(22f)));

                // 2. TOP WINDOW CHROME BAR
                float wx = cw - S(10f);
                float btnSz = S(34f);
                wx -= btnSz; DrawChromeIcon(g, pal, "WIN_CLOSE", SR(wx, S(6f), btnSz, S(32f)), IconKind.Close, true);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_MAX", SR(wx, S(6f), btnSz, S(32f)), _isMaximized ? IconKind.Restore : IconKind.Maximize, false);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_FULL", SR(wx, S(6f), btnSz, S(32f)), IconKind.Fullscreen, false);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_MIN", SR(wx, S(6f), btnSz, S(32f)), IconKind.Minimize, false);

                // Top-left Game Profile Quick Selector Bar + Target Window Lock Pill
                float profW = Math.Min(S(186f), Math.Max(S(110f), wx - sideW - S(360f)));
                RectangleF profDrop = SR(sideW + S(16f), S(7f), profW, S(34f));
                DrawProfilePill(g, pal, profDrop, fSmB);
                RectangleF saveProf = SR(profDrop.Right + S(6f), S(7f), S(88f), S(34f));
                DrawOutlineBtn(g, pal, "BTN_SAVE_PROFILE", saveProf, T("Simpan", "Save"), IconKind.Save, fCap, S(8f));

                string lockTxt = string.IsNullOrEmpty(_cfg.TargetProcessName)
                    ? T("Target: Semua Layar", "Target: All Screens")
                    : (T("Terkunci: ", "Locked: ") + _cfg.TargetProcessName);
                float lockW = Math.Min(S(216f), Math.Max(S(136f), wx - saveProf.Right - S(86f)));
                if (lockW >= S(136f))
                {
                    RectangleF lockBtn = SR(saveProf.Right + S(8f), S(7f), lockW, S(34f));
                    if (!string.IsNullOrEmpty(_cfg.TargetProcessName))
                        DrawAccentBtn(g, pal, "BTN_LOCK_WINDOW", lockBtn, lockTxt, IconKind.Target, pal.Live, pal.LiveHover, fCap, S(8f));
                    else
                        DrawOutlineBtn(g, pal, "BTN_LOCK_WINDOW", lockBtn, lockTxt, IconKind.WindowLock, fCap, S(8f));
                }

                // 3. MAIN VIEWPORT
                RectangleF vp = SR(sideW, topH, cw - sideW, ch - topH);

                if (_showUpdateBanner && !_dismissUpdateBanner && UpdateChecker.Info.HasUpdate)
                {
                    float banH = S(38f);
                    RectangleF banR = SR(vp.X + S(16f), vp.Y + S(6f), vp.Width - S(32f), banH);
                    Theme.FillRR(g, banR, S(8f), pal.Dark ? Color.FromArgb(42, 32, 24) : Color.FromArgb(254, 243, 199));
                    Theme.DrawRR(g, banR, S(8f), pal.Accent, 1f);

                    float icSz = S(15f);
                    Theme.DrawIcon(g, IconKind.Rocket, SR(banR.X + S(12f), banR.Y + (banH - icSz) * 0.5f, icSz, icSz), pal.Accent, 1.8f);

                    string banTxt = T("Pembaruan Tersedia: " + UpdateChecker.Info.ReleaseName + " siap diunduh!",
                                      "Update Available: " + UpdateChecker.Info.ReleaseName + " is ready!");
                    Theme.DrawTextLeft(g, banTxt, fSmB, pal.Dark ? Color.FromArgb(254, 215, 170) : Color.FromArgb(154, 52, 18),
                        SR(banR.X + S(36f), banR.Y, Math.Max(S(100f), banR.Width - S(220f)), banH));

                    float btnW = S(126f);
                    RectangleF btnView = SR(banR.Right - btnW - S(36f), banR.Y + S(5f), btnW, banH - S(10f));
                    DrawAccentBtn(g, pal, "BANNER_UPDATE", btnView, T("Lihat Update", "View Update"), IconKind.Rocket, pal.Accent, pal.AccentHover, fCap, S(6f));

                    RectangleF btnClose = SR(banR.Right - S(32f), banR.Y + S(5f), S(26f), banH - S(10f));
                    DrawChromeIcon(g, pal, "BANNER_DISMISS", btnClose, IconKind.Close, false);

                    vp = SR(vp.X, vp.Y + banH + S(8f), vp.Width, vp.Height - banH - S(8f));
                }

                GraphicsState vpState = g.Save();
                g.SetClip(vp);

                // Responsive content width: adapts cleanly from narrow/175%-zoom windows up to 2K/4K/ultrawide screens without ever overlapping the sidebar
                float availW = Math.Max(220f, vp.Width - S(28f));
                float contentW = Math.Min(S(1760f), availW);
                float cx = (float)Math.Round(vp.X + Math.Max(S(14f), (vp.Width - contentW) * 0.5f));
                float cy = (float)Math.Round(vp.Y + S(12f) - _scrollY);

                float totalH = 0f;
                if (_tab == 0) totalH = RenderTabLive(g, pal, cx, cy, contentW, fSerifLg, fSec, fBody, fBodyB, fSm, fSmB, fCap);
                else if (_tab == 1) totalH = RenderTabStyle(g, pal, cx, cy, contentW, fSerifLg, fSec, fBody, fBodyB, fSm, fSmB, fCap);
                else if (_tab == 2) totalH = RenderTabSystem(g, pal, cx, cy, contentW, fSerifLg, fSec, fBody, fBodyB, fSm, fSmB, fCap);
                else totalH = RenderTabChangelog(g, pal, cx, cy, contentW, fSerifLg, fSec, fBody, fBodyB, fSm, fSmB, fCap);

                _maxScrollY = Math.Max(0f, totalH - (vp.Height - S(24f)));
                if (_maxScrollY > 2f)
                {
                    float trH = vp.Height - S(20f);
                    float thH = Math.Max(S(32f), trH * (vp.Height / (totalH + S(24f))));
                    float thY = vp.Y + S(10f) + (trH - thH) * (_scrollY / _maxScrollY);
                    Theme.FillRR(g, SR(cw - S(9f), thY, S(5f), thH), S(2.5f), pal.BorderStrong);
                }
                g.Restore(vpState);

                // 4. DROPDOWN POPUP
                if (!string.IsNullOrEmpty(_openDrop))
                    RenderDropdownPopup(g, pal, cw, ch, fSm, fSmB);

                // 5. TOAST
                if (!string.IsNullOrEmpty(_toastText))
                {
                    SizeF tsz = TextRenderer.MeasureText(_toastText, fSmB);
                    float tw = tsz.Width + S(42f);
                    RectangleF tr = SR(sideW + (vp.Width - tw) * 0.5f, ch - S(50f), tw, S(34f));
                    Theme.FillRR(g, tr, S(17f), pal.Dark ? Color.FromArgb(245, 24, 24, 27) : Color.FromArgb(245, 18, 18, 20));
                    Theme.DrawRR(g, tr, S(17f), pal.BorderStrong, 1f);
                    using (SolidBrush db = new SolidBrush(_toastColor)) g.FillEllipse(db, tr.X + S(13f), tr.Y + S(12f), S(10f), S(10f));
                    Theme.DrawTextLeft(g, _toastText, fSmB, Color.White, SR(tr.X + S(29f), tr.Y, tw - S(34f), tr.Height));
                }
            }
        }

        private void DrawProfilePill(Graphics g, Palette pal, RectangleF r, Font f)
        {
            bool hov = (_hoverId == "DROP_PROFILE") || (_openDrop == "DROP_PROFILE");
            Theme.FillRR(g, r, S(8f), hov ? pal.CardHover : pal.Well);
            Theme.DrawRR(g, r, S(8f), hov ? pal.Accent : pal.Border, 1f);
            float isz = S(14f);
            Theme.DrawIcon(g, IconKind.Gamepad, SR(r.X + S(10f), r.Y + (r.Height - isz) * 0.5f, isz, isz), pal.Accent, 1.6f);
            Theme.DrawTextLeft(g, _cfg.ActiveProfile, f, pal.Text, SR(r.X + S(30f), r.Y, r.Width - S(50f), r.Height));
            using (Pen p = new Pen(pal.TextSub, 1.5f))
            {
                float ax = r.Right - S(15f), ay = r.Y + r.Height * 0.5f - S(2f);
                g.DrawLine(p, ax, ay, ax + S(4f), ay + S(4f));
                g.DrawLine(p, ax + S(4f), ay + S(4f), ax + S(8f), ay);
            }
            _hits.Add(new Hit { Id = "DROP_PROFILE", Kind = HitKind.Dropdown, Box = r });
        }

        // =========================================================================
        // TAB 0: LIVE & AREA
        // =========================================================================
        private float RenderTabLive(Graphics g, Palette pal, float x, float y, float w,
            Font fSerifLg, Font fSec, Font fBody, Font fBodyB, Font fSm, Font fSmB, Font fCap)
        {
            float cy = y;

            // 1. Responsive Row 0: Title and Language & Engine selectors
            bool wrapHeader = (w < S(730f));
            if (!wrapHeader)
            {
                Theme.DrawTextLeft(g, T("Area & Terjemahan", "Areas & Translation"), fSerifLg, pal.Text, SR(x, cy, S(250f), S(34f)));

                float rx = x + w;
                // Engine pill (Google | MyMem | Gemini AI)
                RectangleF engBox = SR(rx - S(196f), cy + S(1f), S(196f), S(34f));
                Theme.FillRR(g, engBox, S(9f), pal.Well);
                Theme.DrawRR(g, engBox, S(9f), pal.Border, 1f);
                DrawSegItem(g, pal, "ENG_google", SR(engBox.X + S(3f), engBox.Y + S(3f), S(62f), S(28f)), "Google", _cfg.Engine == "google", fCap);
                DrawSegItem(g, pal, "ENG_mymemory", SR(engBox.X + S(66f), engBox.Y + S(3f), S(64f), S(28f)), "MyMem", _cfg.Engine == "mymemory", fCap);
                DrawSegItem(g, pal, "ENG_gemini", SR(engBox.X + S(131f), engBox.Y + S(3f), S(62f), S(28f)), "Gemini", _cfg.Engine == "gemini", fCap);
                rx = engBox.X - S(8f);

                RectangleF tgtBox = SR(rx - S(104f), cy + S(1f), S(104f), S(34f));
                DrawSelect(g, pal, "DROP_TGT", tgtBox, FindLabel(TgtLangs, _cfg.Tgt), fSmB);
                rx = tgtBox.X - S(6f);

                RectangleF swBox = SR(rx - S(32f), cy + S(1f), S(32f), S(34f));
                DrawOutlineBtn(g, pal, "BTN_SWAP", swBox, "", IconKind.Swap, fSmB, S(8f));
                rx = swBox.X - S(6f);

                RectangleF srcBox = SR(rx - S(106f), cy + S(1f), S(106f), S(34f));
                DrawSelect(g, pal, "DROP_SRC", srcBox, FindLabel(SrcLangs, _cfg.Src), fSmB);

                cy += S(50f);
            }
            else
            {
                // Title on first line
                Theme.DrawTextLeft(g, T("Area & Terjemahan", "Areas & Translation"), fSerifLg, pal.Text, SR(x, cy, w, S(34f)));
                cy += S(42f);

                // Language selectors & Engine controls on second line
                float langW = Math.Max(S(80f), Math.Min(S(106f), (w - S(44f)) * 0.5f));
                RectangleF srcBox = SR(x, cy + S(1f), langW, S(34f));
                DrawSelect(g, pal, "DROP_SRC", srcBox, FindLabel(SrcLangs, _cfg.Src), fSmB);

                RectangleF swBox = SR(srcBox.Right + S(6f), cy + S(1f), S(32f), S(34f));
                DrawOutlineBtn(g, pal, "BTN_SWAP", swBox, "", IconKind.Swap, fSmB, S(8f));

                RectangleF tgtBox = SR(swBox.Right + S(6f), cy + S(1f), langW, S(34f));
                DrawSelect(g, pal, "DROP_TGT", tgtBox, FindLabel(TgtLangs, _cfg.Tgt), fSmB);

                if (tgtBox.Right + S(8f) + S(196f) <= x + w)
                {
                    RectangleF engBox = SR(x + w - S(196f), cy + S(1f), S(196f), S(34f));
                    Theme.FillRR(g, engBox, S(9f), pal.Well);
                    Theme.DrawRR(g, engBox, S(9f), pal.Border, 1f);
                    DrawSegItem(g, pal, "ENG_google", SR(engBox.X + S(3f), engBox.Y + S(3f), S(62f), S(28f)), "Google", _cfg.Engine == "google", fCap);
                    DrawSegItem(g, pal, "ENG_mymemory", SR(engBox.X + S(66f), engBox.Y + S(3f), S(64f), S(28f)), "MyMem", _cfg.Engine == "mymemory", fCap);
                    DrawSegItem(g, pal, "ENG_gemini", SR(engBox.X + S(131f), engBox.Y + S(3f), S(62f), S(28f)), "Gemini", _cfg.Engine == "gemini", fCap);
                    cy += S(48f);
                }
                else
                {
                    cy += S(40f);
                    RectangleF engBox = SR(x, cy + S(1f), S(196f), S(34f));
                    Theme.FillRR(g, engBox, S(9f), pal.Well);
                    Theme.DrawRR(g, engBox, S(9f), pal.Border, 1f);
                    DrawSegItem(g, pal, "ENG_google", SR(engBox.X + S(3f), engBox.Y + S(3f), S(62f), S(28f)), "Google", _cfg.Engine == "google", fCap);
                    DrawSegItem(g, pal, "ENG_mymemory", SR(engBox.X + S(66f), engBox.Y + S(3f), S(64f), S(28f)), "MyMem", _cfg.Engine == "mymemory", fCap);
                    DrawSegItem(g, pal, "ENG_gemini", SR(engBox.X + S(131f), engBox.Y + S(3f), S(62f), S(28f)), "Gemini", _cfg.Engine == "gemini", fCap);
                    cy += S(48f);
                }
            }

            // 1b. ACTIVE GAME & LORE BANNER CARD (Fully Responsive — Zero Overlap & Zero Clipped Lore Text!)
            bool hasGame = !string.IsNullOrWhiteSpace(_cfg.ActiveGameTitle);
            int gTerms = GameLoreService.CountTerms(_cfg.Glossary);
            int histCount = _engine.GetDialogHistory().Count;
            string histLbl = histCount > 0 ? (T("Riwayat (", "Log (") + histCount + ")") : T("Riwayat", "Log");
            bool stackGameCard = (w < S(700f));
            float gameCardH = stackGameCard ? S(138f) : S(104f);
            RectangleF gameCard = SR(x, cy, w, gameCardH);
            Theme.DrawCard(g, pal, gameCard, S(12f));
            Theme.FillRR(g, SR(gameCard.X + S(2f), gameCard.Y + S(12f), S(3.5f), gameCard.Height - S(24f)), S(2f), hasGame ? pal.Accent : pal.BorderStrong);

            string gTitleTxt = hasGame ? _cfg.ActiveGameTitle : T("Pilih Game & Cari Lore Sebelum Main", "Pick a Game & Fetch Lore Before Playing");
            string gLoreTxt = !string.IsNullOrWhiteSpace(_cfg.GameLoreSummary)
                ? _cfg.GameLoreSummary.Replace("\r", " ").Replace("\n", " ")
                : T("Pilih preset game atau ketik judul game apa saja buat tarik lore cerita & glosarium nama karakter otomatis biar OCR dan terjemahan presisi.",
                    "Select a game or search any title online to auto-load story lore & character glossary for accurate OCR and translation.");
            if (gLoreTxt.Length > 155) gLoreTxt = gLoreTxt.Substring(0, 152).TrimEnd() + "...";

            string toneBadge = _cfg.ToneMode == 0 ? "GAUL" : (_cfg.ToneMode == 1 ? "SANTAI" : "BAKU");
            string statBadge = toneBadge + (gTerms > 0 ? (" · " + gTerms + " ISTILAH") : "");
            float badgeW2 = gTerms > 0 ? S(132f) : S(72f);
            float btnPickW = S(168f);
            float btnHistW = histCount > 0 ? S(116f) : S(100f);

            if (!stackGameCard)
            {
                float rightBtnsW = btnPickW + btnHistW + S(28f);
                float gTextW = Math.Max(S(180f), gameCard.Width - rightBtnsW - S(28f));

                Theme.DrawTextLeft(g, gTitleTxt, fSec, pal.Text, SR(gameCard.X + S(18f), gameCard.Y + S(12f), Math.Max(S(100f), gTextW - badgeW2 - S(10f)), S(24f)));

                RectangleF pillR = SR(gameCard.X + S(18f) + gTextW - badgeW2, gameCard.Y + S(13f), badgeW2, S(22f));
                Theme.FillRR(g, pillR, S(11f), hasGame ? pal.LiveSoft : pal.AccentSoft);
                Theme.DrawTextCenter(g, statBadge, fCap, hasGame ? pal.Live : pal.AccentText, pillR);

                Theme.DrawTextWrap(g, gLoreTxt, fSm, pal.TextSub, SR(gameCard.X + S(18f), gameCard.Y + S(40f), gTextW, S(54f)));

                DrawAccentBtn(g, pal, "BTN_GAME_LORE", SR(gameCard.Right - btnPickW - S(14f), gameCard.Y + S(33f), btnPickW, S(38f)),
                    hasGame ? T("Ganti / Cari Lore", "Change / Lore") : T("Pilih Game & Lore", "Pick Game & Lore"),
                    IconKind.Book, pal.Accent, pal.AccentHover, fSmB, S(8f));
                DrawOutlineBtn(g, pal, "BTN_HISTORY", SR(gameCard.Right - btnPickW - btnHistW - S(22f), gameCard.Y + S(33f), btnHistW, S(38f)),
                    histLbl, IconKind.History, fSmB, S(8f));
            }
            else
            {
                float innerW = gameCard.Width - S(34f);
                Theme.DrawTextLeft(g, gTitleTxt, fSec, pal.Text, SR(gameCard.X + S(18f), gameCard.Y + S(11f), Math.Max(S(100f), innerW - badgeW2 - S(8f)), S(24f)));

                RectangleF pillR = SR(gameCard.Right - S(14f) - badgeW2, gameCard.Y + S(12f), badgeW2, S(22f));
                Theme.FillRR(g, pillR, S(11f), hasGame ? pal.LiveSoft : pal.AccentSoft);
                Theme.DrawTextCenter(g, statBadge, fCap, hasGame ? pal.Live : pal.AccentText, pillR);

                Theme.DrawTextWrap(g, gLoreTxt, fSm, pal.TextSub, SR(gameCard.X + S(18f), gameCard.Y + S(38f), innerW, S(44f)));

                DrawAccentBtn(g, pal, "BTN_GAME_LORE", SR(gameCard.X + S(18f), gameCard.Bottom - S(46f), btnPickW, S(34f)),
                    hasGame ? T("Ganti / Cari Lore", "Change / Lore") : T("Pilih Game & Lore", "Pick Game & Lore"),
                    IconKind.Book, pal.Accent, pal.AccentHover, fSmB, S(8f));
                DrawOutlineBtn(g, pal, "BTN_HISTORY", SR(gameCard.X + S(18f) + btnPickW + S(8f), gameCard.Bottom - S(46f), btnHistW, S(34f)),
                    histLbl, IconKind.History, fSmB, S(8f));
            }

            cy += gameCardH + S(14f);

            // 2. Responsive Row 1: Action buttons & Placement Mode pill
            float pmW = S(248f);
            bool wrapActions = (w < S(764f));

            if (!wrapActions)
            {
                DrawAccentBtn(g, pal, "BTN_ADD_AREA", SR(x, cy, S(142f), S(36f)), T("Tambah Area", "Add Area"), IconKind.Plus, pal.Accent, pal.AccentHover, fSmB, S(9f));
                DrawOutlineBtn(g, pal, "BTN_EDIT_SCREEN", SR(x + S(150f), cy, S(138f), S(36f)),
                    _overlay.EditMode ? T("Selesai Edit", "Done Editing") : T("Edit di Layar", "Edit on Screen"), IconKind.EditBox, fSmB, S(9f));
                DrawOutlineBtn(g, pal, "BTN_SCAN", SR(x + S(296f), cy, S(92f), S(36f)), "Scan", IconKind.Refresh, fSmB, S(9f));
                DrawOutlineBtn(g, pal, "BTN_PREVIEW_LIVE", SR(x + S(396f), cy, S(104f), S(36f)), T("Tes Layar", "Preview"), IconKind.Eye, fSmB, S(9f));

                RectangleF pmBox = SR(x + w - pmW, cy + S(1f), pmW, S(34f));
                Theme.FillRR(g, pmBox, S(9f), pal.Well);
                Theme.DrawRR(g, pmBox, S(9f), pal.Border, 1f);
                string[] pmLbl = new string[] { T("Terpisah", "Separate"), T("Tutup Asli", "Cover"), T("Gabung", "Merged") };
                float pw = (pmW - S(6f)) / 3f;
                for (int m = 0; m < 3; m++)
                    DrawSegItem(g, pal, "PLACE_" + m, SR(pmBox.X + S(3f) + m * pw, pmBox.Y + S(3f), pw, S(28f)), pmLbl[m], _cfg.Placement == m, fCap);

                cy += S(50f);
            }
            else
            {
                float curBx = x;
                float bH = S(36f);

                Action<float> checkWrap = (bw) =>
                {
                    if (curBx + bw > x + w && curBx > x)
                    {
                        curBx = x;
                        cy += bH + S(8f);
                    }
                };

                float bw1 = S(142f);
                checkWrap(bw1);
                DrawAccentBtn(g, pal, "BTN_ADD_AREA", SR(curBx, cy, bw1, bH), T("Tambah Area", "Add Area"), IconKind.Plus, pal.Accent, pal.AccentHover, fSmB, S(9f));
                curBx += bw1 + S(8f);

                float bw2 = S(138f);
                checkWrap(bw2);
                DrawOutlineBtn(g, pal, "BTN_EDIT_SCREEN", SR(curBx, cy, bw2, bH),
                    _overlay.EditMode ? T("Selesai Edit", "Done Editing") : T("Edit di Layar", "Edit on Screen"), IconKind.EditBox, fSmB, S(9f));
                curBx += bw2 + S(8f);

                float bw3 = S(92f);
                checkWrap(bw3);
                DrawOutlineBtn(g, pal, "BTN_SCAN", SR(curBx, cy, bw3, bH), "Scan", IconKind.Refresh, fSmB, S(9f));
                curBx += bw3 + S(8f);

                float bw4 = S(104f);
                checkWrap(bw4);
                DrawOutlineBtn(g, pal, "BTN_PREVIEW_LIVE", SR(curBx, cy, bw4, bH), T("Tes Layar", "Preview"), IconKind.Eye, fSmB, S(9f));

                cy += bH + S(10f);

                // Placement Mode pill on its own row
                RectangleF pmBox = SR(x, cy + S(1f), Math.Min(pmW, w), S(34f));
                Theme.FillRR(g, pmBox, S(9f), pal.Well);
                Theme.DrawRR(g, pmBox, S(9f), pal.Border, 1f);
                string[] pmLbl = new string[] { T("Terpisah", "Separate"), T("Tutup Asli", "Cover"), T("Gabung", "Merged") };
                float pw = (pmBox.Width - S(6f)) / 3f;
                for (int m = 0; m < 3; m++)
                    DrawSegItem(g, pal, "PLACE_" + m, SR(pmBox.X + S(3f) + m * pw, pmBox.Y + S(3f), pw, S(28f)), pmLbl[m], _cfg.Placement == m, fCap);

                cy += S(48f);
            }

            if (_cfg.Placement == 2)
            {
                RectangleF mc = SR(x, cy, w, S(66f));
                Theme.DrawCard(g, pal, mc, S(16f));
                Theme.DrawTextLeft(g, "Kotak Hasil Gabungan", fBodyB, pal.Text, SR(mc.X + S(18f), mc.Y + S(11f), S(200f), S(20f)));
                string mDim = _cfg.MergedRect.Width > 0
                    ? ("Lebar " + _cfg.MergedRect.Width + " × Tinggi " + _cfg.MergedRect.Height + " px  •  " + _cfg.MergedRect.X + ", " + _cfg.MergedRect.Y)
                    : "Belum diatur";
                RectangleF snipMerged = SR(mc.Right - S(186f), mc.Y + S(15f), S(170f), S(36f));
                float mDimW = Math.Max(0f, snipMerged.X - S(8f) - (mc.X + S(18f)));
                Theme.DrawTextLeft(g, mDim, fSm, pal.TextSub, SR(mc.X + S(18f), mc.Y + S(33f), mDimW, S(20f)));
                DrawAccentBtn(g, pal, "SNIP_MERGED_RES", snipMerged,
                    "Blok Layar Hasil", IconKind.ResultBox, pal.Accent, pal.AccentHover, fSmB, S(9f));
                cy += S(80f);
            }

            List<Area> list;
            lock (_cfg.Sync) list = new List<Area>(_cfg.Areas);

            for (int i = 0; i < list.Count; i++)
            {
                Area a = list[i];
                bool isSeparate = (_cfg.Placement == 0);
                bool wrapCardHeader = (w < S(480f));
                float cardH = (isSeparate ? S(132f) : S(90f)) + (wrapCardHeader ? S(34f) : 0f);
                RectangleF card = SR(x, cy, w, cardH);
                Theme.DrawCard(g, pal, card, S(16f));

                RectangleF swHit = SR(card.X + S(14f), card.Y + S(10f), S(42f), S(28f));
                DrawSwitchScaled(g, pal, swHit, a.Enabled);
                _hits.Add(new Hit { Id = "AREA_TOG", Kind = HitKind.Button, Box = swHit, Index = a.Id });

                Theme.DrawTextLeft(g, "Area " + (i + 1), fSec, a.Enabled ? pal.Text : pal.TextMute,
                    SR(card.X + S(64f), card.Y + S(10f), S(66f), S(28f)));

                // Delete button in top-right
                RectangleF delBox = SR(card.Right - S(40f), card.Y + S(9f), S(28f), S(28f));
                DrawMiniIcon(g, pal, "AREA_DEL", delBox, IconKind.Trash, a.Id, true, list.Count > 1);

                OverlayStyle eff = a.EffectiveStyle(_cfg);
                string styleTxt;
                float styleW;
                if (card.Width < S(520f))
                {
                    styleTxt = (a.Style.UseCustom ? T("Khusus", "Custom") : T("Global", "Global")) + " · " + eff.FontSize + "px";
                    styleW = S(112f);
                }
                else
                {
                    styleTxt = a.Style.UseCustom
                        ? (T("Gaya Khusus", "Custom") + " • " + eff.FontSize + "px")
                        : (T("Gaya Global", "Global") + " • " + eff.FontSize + "px");
                    styleW = S(152f);
                }

                if (!wrapCardHeader)
                {
                    RectangleF modePill = SR(card.X + S(136f), card.Y + S(10f), S(146f), S(28f));
                    Theme.FillRR(g, modePill, S(8f), pal.Well);
                    Theme.DrawRR(g, modePill, S(8f), pal.Border, 1f);
                    DrawSegItemIdx(g, pal, "AREA_MODE_DLG", SR(modePill.X + S(2f), modePill.Y + S(2f), S(70f), S(24f)),
                        "Dialog", a.Mode == AreaTextMode.Dialog, a.Id, fCap);
                    DrawSegItemIdx(g, pal, "AREA_MODE_CHC", SR(modePill.X + S(74f), modePill.Y + S(2f), S(70f), S(24f)),
                        T("Pilihan", "Choices"), a.Mode == AreaTextMode.Choice, a.Id, fCap);

                    float styleRight = delBox.X - S(6f);
                    RectangleF stylePill = SR(styleRight - styleW, card.Y + S(10f), styleW, S(28f));
                    DrawAreaActionBtn(g, pal, "AREA_STYLE", stylePill, styleTxt, IconKind.Palette, a.Id,
                        a.Style.UseCustom ? pal.Accent : pal.TextSub, fCap);
                }
                else
                {
                    float h2Y = card.Y + S(42f);
                    RectangleF modePill = SR(card.X + S(14f), h2Y, S(146f), S(28f));
                    Theme.FillRR(g, modePill, S(8f), pal.Well);
                    Theme.DrawRR(g, modePill, S(8f), pal.Border, 1f);
                    DrawSegItemIdx(g, pal, "AREA_MODE_DLG", SR(modePill.X + S(2f), modePill.Y + S(2f), S(70f), S(24f)),
                        "Dialog", a.Mode == AreaTextMode.Dialog, a.Id, fCap);
                    DrawSegItemIdx(g, pal, "AREA_MODE_CHC", SR(modePill.X + S(74f), modePill.Y + S(2f), S(70f), S(24f)),
                        T("Pilihan", "Choices"), a.Mode == AreaTextMode.Choice, a.Id, fCap);

                    RectangleF stylePill = SR(card.Right - S(14f) - styleW, h2Y, styleW, S(28f));
                    DrawAreaActionBtn(g, pal, "AREA_STYLE", stylePill, styleTxt, IconKind.Palette, a.Id,
                        a.Style.UseCustom ? pal.Accent : pal.TextSub, fCap);
                }

                // Sub-rows Y offset
                float ocrY = card.Y + (wrapCardHeader ? S(76f) : S(44f));
                float resY = ocrY + S(42f);

                // Row 1: OCR Area
                RectangleF ocrRow = SR(card.X + S(14f), ocrY, card.Width - S(28f), S(36f));
                Theme.FillRR(g, ocrRow, S(8f), pal.Well);
                using (SolidBrush bBlue = new SolidBrush(Color.FromArgb(86, 152, 255)))
                    g.FillEllipse(bBlue, ocrRow.X + S(12f), ocrRow.Y + S(14f), S(8f), S(8f));
                Theme.DrawTextLeft(g, T("Area OCR", "OCR Area"), fSmB, pal.Text, SR(ocrRow.X + S(28f), ocrRow.Y, S(84f), ocrRow.Height));

                bool compactSnip = ocrRow.Width < S(400f);
                float snipW = compactSnip ? S(100f) : S(142f);
                string snipOcrLbl = compactSnip ? T("Blok OCR", "Snip OCR") : T("Blok Layar OCR", "Snip OCR Area");

                RectangleF snipOcr = SR(ocrRow.Right - snipW - S(4f), ocrRow.Y + S(4f), snipW, S(28f));
                float ocrTxtW = Math.Max(0f, snipOcr.X - S(8f) - (ocrRow.X + S(114f)));
                string ocrDim = (ocrTxtW < S(150f))
                    ? (a.Ocr.Width + " × " + a.Ocr.Height + "  •  " + a.Ocr.X + ", " + a.Ocr.Y)
                    : (T("Lebar ", "W ") + a.Ocr.Width + T(" × Tinggi ", " × H ") + a.Ocr.Height + " px   •   " + a.Ocr.X + ", " + a.Ocr.Y);
                Theme.DrawTextLeft(g, ocrDim, fSm, pal.TextSub, SR(ocrRow.X + S(114f), ocrRow.Y, ocrTxtW, ocrRow.Height));
                DrawAreaActionBtn(g, pal, "SNIP_OCR", snipOcr, snipOcrLbl, IconKind.Crop, a.Id, Color.FromArgb(86, 152, 255), fCap);

                // Row 2: Result Box
                if (isSeparate)
                {
                    RectangleF resRow = SR(card.X + S(14f), resY, card.Width - S(28f), S(36f));
                    Theme.FillRR(g, resRow, S(8f), pal.Well);
                    using (SolidBrush bCor = new SolidBrush(pal.Accent))
                        g.FillEllipse(bCor, resRow.X + S(12f), resRow.Y + S(14f), S(8f), S(8f));
                    Theme.DrawTextLeft(g, T("Kotak Hasil", "Result Box"), fSmB, pal.Text, SR(resRow.X + S(28f), resRow.Y, S(84f), resRow.Height));

                    string snipResLbl = compactSnip ? T("Blok Hasil", "Snip Res") : T("Blok Layar Hasil", "Snip Result Box");
                    RectangleF snipRes = SR(resRow.Right - snipW - S(4f), resRow.Y + S(4f), snipW, S(28f));
                    RectangleF snapBtn = SR(snipRes.X - S(36f), resRow.Y + S(4f), S(28f), S(28f));
                    float resTxtW = Math.Max(0f, snapBtn.X - S(8f) - (resRow.X + S(114f)));
                    string resDim = (resTxtW < S(150f))
                        ? (a.Result.Width + " × " + a.Result.Height + "  •  " + a.Result.X + ", " + a.Result.Y)
                        : (T("Lebar ", "W ") + a.Result.Width + T(" × Tinggi ", " × H ") + a.Result.Height + " px   •   " + a.Result.X + ", " + a.Result.Y);
                    Theme.DrawTextLeft(g, resDim, fSm, pal.TextSub, SR(resRow.X + S(114f), resRow.Y, resTxtW, resRow.Height));

                    DrawMiniIcon(g, pal, "AREA_SNAP", snapBtn, IconKind.Snap, a.Id, false, true);
                    DrawAreaActionBtn(g, pal, "SNIP_RES", snipRes, snipResLbl, IconKind.ResultBox, a.Id, pal.Accent, fCap);
                }

                cy += cardH + S(12f);
            }

            // Live Feed Card
            bool stackFeed = (w < S(520f));
            float feedH = stackFeed ? S(180f) : S(110f);
            RectangleF feed = SR(x, cy + S(4f), w, feedH);
            Theme.DrawCard(g, pal, feed, S(16f));

            if (!stackFeed)
            {
                float halfW = (feed.Width - S(134f)) * 0.5f;
                RectangleF wSrc = SR(feed.X + S(14f), feed.Y + S(14f), halfW, feed.Height - S(28f));
                Theme.FillRR(g, wSrc, S(9f), pal.Well);
                Theme.DrawTextLeft(g, "OCR", fCap, Color.FromArgb(86, 152, 255), SR(wSrc.X + S(12f), wSrc.Y + S(8f), S(50f), S(16f)));
                Theme.DrawTextRight(g, _liveStatus, fCap, pal.TextMute, SR(wSrc.X + S(64f), wSrc.Y + S(8f), wSrc.Width - S(76f), S(16f)));
                Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveSource) ? T("Menunggu teks layar...", "Waiting for screen text...") : _liveSource,
                    fSm, string.IsNullOrEmpty(_liveSource) ? pal.TextMute : pal.TextSub,
                    SR(wSrc.X + S(12f), wSrc.Y + S(28f), wSrc.Width - S(24f), S(48f)));

                RectangleF wRes = SR(wSrc.Right + S(10f), feed.Y + S(14f), halfW, feed.Height - S(28f));
                Theme.FillRR(g, wRes, S(9f), pal.Well);
                Theme.DrawTextLeft(g, T("Terjemahan", "Translation"), fCap, pal.AccentText, SR(wRes.X + S(12f), wRes.Y + S(8f), S(90f), S(16f)));
                Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveResult) ? T("Hasil terjemahan tampil di sini", "Translated text appears here") : _liveResult,
                    fBodyB, string.IsNullOrEmpty(_liveResult) ? pal.TextMute : pal.Text,
                    SR(wRes.X + S(12f), wRes.Y + S(28f), wRes.Width - S(24f), S(48f)));

                DrawOutlineBtn(g, pal, "BTN_COPY", SR(wRes.Right + S(10f), feed.Y + S(35f), S(86f), S(38f)),
                    _copiedFlash ? T("Tersalin", "Copied") : T("Salin", "Copy"), _copiedFlash ? IconKind.Check : IconKind.Copy, fSmB, S(9f));
            }
            else
            {
                float subW = feed.Width - S(28f);
                float boxH = S(68f);
                RectangleF wSrc = SR(feed.X + S(14f), feed.Y + S(12f), subW, boxH);
                Theme.FillRR(g, wSrc, S(9f), pal.Well);
                Theme.DrawTextLeft(g, "OCR", fCap, Color.FromArgb(86, 152, 255), SR(wSrc.X + S(12f), wSrc.Y + S(6f), S(50f), S(16f)));
                Theme.DrawTextRight(g, _liveStatus, fCap, pal.TextMute, SR(wSrc.X + S(64f), wSrc.Y + S(6f), wSrc.Width - S(76f), S(16f)));
                Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveSource) ? T("Menunggu teks layar...", "Waiting for screen text...") : _liveSource,
                    fSm, string.IsNullOrEmpty(_liveSource) ? pal.TextMute : pal.TextSub,
                    SR(wSrc.X + S(12f), wSrc.Y + S(24f), wSrc.Width - S(24f), S(38f)));

                RectangleF wRes = SR(feed.X + S(14f), wSrc.Bottom + S(8f), subW - S(94f), boxH);
                Theme.FillRR(g, wRes, S(9f), pal.Well);
                Theme.DrawTextLeft(g, T("Terjemahan", "Translation"), fCap, pal.AccentText, SR(wRes.X + S(12f), wRes.Y + S(6f), S(90f), S(16f)));
                Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveResult) ? T("Hasil terjemahan tampil di sini", "Translated text appears here") : _liveResult,
                    fBodyB, string.IsNullOrEmpty(_liveResult) ? pal.TextMute : pal.Text,
                    SR(wRes.X + S(12f), wRes.Y + S(24f), wRes.Width - S(24f), S(38f)));

                DrawOutlineBtn(g, pal, "BTN_COPY", SR(wRes.Right + S(8f), wSrc.Bottom + S(18f), S(86f), S(38f)),
                    _copiedFlash ? T("Tersalin", "Copied") : T("Salin", "Copy"), _copiedFlash ? IconKind.Check : IconKind.Copy, fSmB, S(9f));
            }

            cy += feedH + S(18f);
            return cy - y;
        }

        // =========================================================================
        // TAB 1: TAMPILAN (Supports Global OR Per-Area Custom Styling!)
        // =========================================================================
        private float RenderTabStyle(Graphics g, Palette pal, float x, float y, float w,
            Font fSerifLg, Font fSec, Font fBody, Font fBodyB, Font fSm, Font fSmB, Font fCap)
        {
            float cy = y;

            // Target Selector: Global | Area 1 | Area 2 ...
            List<Area> areas;
            lock (_cfg.Sync) areas = new List<Area>(_cfg.Areas);

            int targetCount = Math.Min(4, 1 + areas.Count);
            float tPillW = targetCount * S(82f) + S(6f);
            float topCtrlW = tPillW + S(10f) + S(126f);
            bool wrapStyleTop = (w < S(240f) + S(16f) + topCtrlW);

            if (!wrapStyleTop)
            {
                Theme.DrawTextLeft(g, T("Tampilan Overlay", "Overlay Appearance"), fSerifLg, pal.Text, SR(x, cy, S(240f), S(34f)));

                RectangleF tWell = SR(x + w - S(136f) - tPillW, cy + S(1f), tPillW, S(34f));
                Theme.FillRR(g, tWell, S(9f), pal.Well);
                Theme.DrawRR(g, tWell, S(9f), pal.Border, 1f);
                DrawSegItemIdx(g, pal, "ST_TARGET", SR(tWell.X + S(3f), tWell.Y + S(3f), S(80f), S(28f)), "Global", _styleTargetAreaId == 0, 0, fCap);
                for (int i = 0; i < Math.Min(3, areas.Count); i++)
                {
                    DrawSegItemIdx(g, pal, "ST_TARGET", SR(tWell.X + S(3f) + (i + 1) * S(82f), tWell.Y + S(3f), S(80f), S(28f)),
                        "Area " + (i + 1), _styleTargetAreaId == areas[i].Id, areas[i].Id, fCap);
                }

                DrawAccentBtn(g, pal, "BTN_PREVIEW_LIVE", SR(x + w - S(126f), cy + S(1f), S(126f), S(34f)),
                    T("Tes di Layar", "Preview"), IconKind.Eye, pal.Accent, pal.AccentHover, fSmB, S(9f));
                cy += S(48f);
            }
            else
            {
                Theme.DrawTextLeft(g, T("Tampilan Overlay", "Overlay Appearance"), fSerifLg, pal.Text, SR(x, cy, w, S(34f)));
                cy += S(42f);

                RectangleF tWell = SR(x, cy + S(1f), tPillW, S(34f));
                Theme.FillRR(g, tWell, S(9f), pal.Well);
                Theme.DrawRR(g, tWell, S(9f), pal.Border, 1f);
                DrawSegItemIdx(g, pal, "ST_TARGET", SR(tWell.X + S(3f), tWell.Y + S(3f), S(80f), S(28f)), "Global", _styleTargetAreaId == 0, 0, fCap);
                for (int i = 0; i < Math.Min(3, areas.Count); i++)
                {
                    DrawSegItemIdx(g, pal, "ST_TARGET", SR(tWell.X + S(3f) + (i + 1) * S(82f), tWell.Y + S(3f), S(80f), S(28f)),
                        "Area " + (i + 1), _styleTargetAreaId == areas[i].Id, areas[i].Id, fCap);
                }

                if (tWell.Right + S(10f) + S(126f) <= x + w)
                {
                    DrawAccentBtn(g, pal, "BTN_PREVIEW_LIVE", SR(x + w - S(126f), cy + S(1f), S(126f), S(34f)),
                        T("Tes di Layar", "Preview"), IconKind.Eye, pal.Accent, pal.AccentHover, fSmB, S(9f));
                    cy += S(46f);
                }
                else
                {
                    cy += S(40f);
                    DrawAccentBtn(g, pal, "BTN_PREVIEW_LIVE", SR(x, cy + S(1f), S(126f), S(34f)),
                        T("Tes di Layar", "Preview"), IconKind.Eye, pal.Accent, pal.AccentHover, fSmB, S(9f));
                    cy += S(46f);
                }
            }

            // If editing specific Area, show toggle "Gunakan Gaya Khusus Area Ini"
            OverlayStyle st = ActiveStyle;
            if (_styleTargetAreaId > 0)
            {
                RectangleF custBar = SR(x, cy, w, S(44f));
                Theme.DrawCard(g, pal, custBar, S(12f));
                Theme.DrawTextLeft(g, T("Gunakan tampilan khusus untuk Area ini (berbeda dari Global)", "Use custom style for this Area (overrides Global)"), fBodyB, pal.Text,
                    SR(custBar.X + S(16f), custBar.Y, custBar.Width - S(80f), custBar.Height));
                DrawSwitchScaled(g, pal, SR(custBar.Right - S(56f), custBar.Y, S(42f), custBar.Height), st.UseCustom);
                _hits.Add(new Hit { Id = "TOG_AREA_CUSTOM_STYLE", Kind = HitKind.Button, Box = custBar });
                cy += S(54f);
            }

            // Stage Preview (Crisp Architectural Studio Stage — Zero AI-Slop Gradients!)
            RectangleF stage = SR(x, cy, w, S(176f));
            Theme.DrawStudioStage(g, pal, stage, S(12f));

            string[] presets = new string[] { T("Sinema", "Cinema"), "Novel", T("Kontras", "Contrast"), T("Tanpa Kotak", "No Box") };
            float px = stage.X + S(14f);
            for (int i = 0; i < 4; i++)
            {
                RectangleF pr = SR(px, stage.Y + S(12f), S(92f), S(28f));
                Theme.FillRR(g, pr, S(14f), _hoverId == "PRESET_" + i ? Color.FromArgb(75, 255, 255, 255) : Color.FromArgb(36, 255, 255, 255));
                Theme.DrawTextCenter(g, presets[i], fCap, Color.White, pr);
                _hits.Add(new Hit { Id = "PRESET_" + i, Kind = HitKind.Button, Box = pr, Index = i });
                px += S(98f);
            }

            float ovW = Math.Min(S(640f), stage.Width - S(56f));
            RectangleF ovBox = SR(stage.X + (stage.Width - ovW) * 0.5f, stage.Bottom - S(104f), ovW, S(86f));
            if (st.BgOpacity > 0)
            {
                int a = Math.Max(0, Math.Min(255, st.BgOpacity * 255 / 100));
                Theme.FillRR(g, ovBox, st.Corner, Color.FromArgb(a, st.BgColor.R, st.BgColor.G, st.BgColor.B));
                Theme.DrawRR(g, ovBox, st.Corner, Color.FromArgb(Math.Min(50, a / 3), 255, 255, 255), 1f);
            }
            float topPad = S(8f);
            if (_cfg.DetectSpeaker && (_styleTargetAreaId == 0 || (_cfg.FindArea(_styleTargetAreaId) != null && _cfg.FindArea(_styleTargetAreaId).Mode == AreaTextMode.Dialog)))
            {
                using (Font fs = Theme.UiFont(Math.Max(11f, Math.Min(14f, st.FontSize * 0.56f)), true))
                {
                    string spkSample = "Saria";
                    SizeF ssz = TextRenderer.MeasureText(spkSample, fs);
                    float bw = Math.Min(ovBox.Width - st.Padding * 2f, ssz.Width + S(18f));
                    float bh = S(22f);
                    float bx = st.Align == 1 ? Math.Max(st.Padding, (ovBox.Width - bw) * 0.5f) : (st.Align == 2 ? Math.Max(st.Padding, ovBox.Width - st.Padding - bw) : st.Padding);
                    RectangleF badge = new RectangleF(ovBox.X + bx, ovBox.Y + topPad, bw, bh);
                    Color spkCol = Color.FromArgb(245, st.SpeakerColor.R, st.SpeakerColor.G, st.SpeakerColor.B);
                    Theme.FillRR(g, badge, bh * 0.5f, spkCol);
                    using (StringFormat sfB = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (SolidBrush wBr = new SolidBrush(Color.White))
                        g.DrawString(spkSample, fs, wBr, badge, sfB);
                    topPad += bh + S(4f);
                }
            }
            string samplePrev = (_styleTargetAreaId > 0 && _cfg.FindArea(_styleTargetAreaId) != null && _cfg.FindArea(_styleTargetAreaId).Mode == AreaTextMode.Choice)
                ? (_cfg.ToneMode == 0 ? "• Gue belom yakin sih.\n• Gas, gue ikut!" : "• Aku belum yakin.\n• Aku ikut!")
                : (_cfg.ToneMode == 0 ? "Lo mesti cepet. Susul gue nanti ya!" : "Kamu harus cepat. Susul aku nanti!");
            OverlayPane.DrawOverlayText(g, samplePrev, SR(ovBox.X + st.Padding, ovBox.Y + topPad, ovBox.Width - st.Padding * 2f, ovBox.Height - topPad - S(8f)), st);

            cy += S(190f);

            float colGap = S(14f);
            bool twoCol = (w >= S(620f));
            float colW = twoCol ? (w - colGap) * 0.5f : w;
            float colH = S(366f);

            RectangleF left = SR(x, cy, colW, colH);
            Theme.DrawCard(g, pal, left, S(16f));
            float lx = left.X + S(18f), lw2 = left.Width - S(36f), ly = left.Y + S(18f);

            Theme.DrawTextLeft(g, "Font", fSmB, pal.TextSub, SR(lx, ly, S(84f), S(34f)));
            DrawSelect(g, pal, "DROP_FONT", SR(lx + S(88f), ly, lw2 - S(88f), S(34f)), st.Font, fSmB);
            ly += S(48f);

            DrawSliderRow(g, pal, "SL_SIZE", SR(lx, ly, lw2, S(34f)), T("Ukuran", "Size"), st.FontSize + " px", st.FontSize, 12, 56, fSmB);
            ly += S(46f);

            Theme.DrawTextLeft(g, T("Perataan", "Align"), fSmB, pal.TextSub, SR(lx, ly, S(84f), S(34f)));
            RectangleF alBox = SR(lx + S(88f), ly, lw2 - S(88f), S(34f));
            Theme.FillRR(g, alBox, S(9f), pal.Well);
            Theme.DrawRR(g, alBox, S(9f), pal.Border, 1f);
            string[] aligns = new string[] { T("Kiri", "Left"), T("Tengah", "Center"), T("Kanan", "Right") };
            float aw = (alBox.Width - S(6f)) / 3f;
            for (int i = 0; i < 3; i++)
                DrawSegItem(g, pal, "ALIGN_" + i, SR(alBox.X + S(3f) + i * aw, alBox.Y + S(3f), aw, S(28f)), aligns[i], st.Align == i, fCap);
            ly += S(48f);

            DrawToggleRow(g, pal, "TOG_BOLD", SR(lx, ly, lw2, S(38f)), T("Huruf Tebal", "Bold Text"), st.Bold, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "TOG_SHADOW", SR(lx, ly, lw2, S(38f)), T("Bayangan Teks", "Text Shadow"), st.Shadow, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "TOG_AUTOFIT", SR(lx, ly, lw2, S(38f)), T("Auto-Fit Kotak Hasil", "Auto-Fit Result Box"), st.AutoFit, fBody);

            RectangleF right;
            if (twoCol)
            {
                right = SR(x + colW + colGap, cy, colW, colH);
            }
            else
            {
                cy += colH + S(14f);
                right = SR(x, cy, colW, colH);
            }

            Theme.DrawCard(g, pal, right, S(16f));
            float rx = right.X + S(18f), rw2 = right.Width - S(36f), ry = right.Y + S(18f);

            DrawSliderRow(g, pal, "SL_OPAC", SR(rx, ry, rw2, S(34f)), T("Opasitas", "Opacity"), st.BgOpacity + "%", st.BgOpacity, 0, 100, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_OUTLINE", SR(rx, ry, rw2, S(34f)), T("Garis Tepi", "Outline"), st.Outline + " px", st.Outline, 0, 6, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_CORNER", SR(rx, ry, rw2, S(34f)), T("Lengkungan", "Radius"), st.Corner + " px", st.Corner, 0, 28, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_PAD", SR(rx, ry, rw2, S(34f)), T("Jarak Dalam", "Padding"), st.Padding + " px", st.Padding, 4, 32, fSmB); ry += S(50f);

            Theme.DrawTextLeft(g, T("Warna", "Colors"), fSmB, pal.TextSub, SR(rx, ry, S(76f), S(30f)));
            for (int i = 0; i < QuickTextColors.Length; i++)
            {
                RectangleF sw = SR(rx + S(82f) + i * S(34f), ry + S(2f), S(26f), S(26f));
                Theme.FillRR(g, sw, S(13f), QuickTextColors[i]);
                bool sel = (st.TextColor.ToArgb() == QuickTextColors[i].ToArgb());
                Theme.DrawRR(g, sw, S(13f), sel ? pal.Accent : pal.BorderStrong, sel ? 2f : 1f);
                _hits.Add(new Hit { Id = "SWATCH_" + i, Kind = HitKind.Button, Box = sw, Index = i });
            }
            ry += S(42f);

            float c2 = (rw2 - S(8f)) * 0.5f;
            DrawColorBtn(g, pal, "PICK_TEXT", SR(rx, ry, c2, S(34f)), T("Warna Teks", "Text Color"), st.TextColor, fCap);
            DrawColorBtn(g, pal, "PICK_BG", SR(rx + c2 + S(8f), ry, c2, S(34f)), T("Latar Kotak", "Background"), st.BgColor, fCap);
            ry += S(40f);
            DrawColorBtn(g, pal, "PICK_OUT", SR(rx, ry, c2, S(34f)), T("Garis Tepi", "Outline"), st.OutlineColor, fCap);
            DrawColorBtn(g, pal, "PICK_SPEAKER", SR(rx + c2 + S(8f), ry, c2, S(34f)), T("Badge Aktor", "Speaker Badge"), st.SpeakerColor, fCap);

            cy += colH + S(16f);
            return cy - y;
        }

        // =========================================================================
        // TAB 2: SISTEM
        // =========================================================================
        private float RenderTabSystem(Graphics g, Palette pal, float x, float y, float w,
            Font fSerifLg, Font fSec, Font fBody, Font fBodyB, Font fSm, Font fSmB, Font fCap)
        {
            float cy = y;
            Theme.DrawTextLeft(g, T("Sistem & Mesin", "System & Engine"), fSerifLg, pal.Text, SR(x, cy, S(300f), S(34f)));
            cy += S(48f);

            float colGap = S(14f);
            bool twoCol = (w >= S(620f));
            float colW = twoCol ? (w - colGap) * 0.5f : w;
            float colH = S(676f);

            RectangleF left = SR(x, cy, colW, colH);
            Theme.DrawCard(g, pal, left, S(12f));
            float lx = left.X + S(18f), lw2 = left.Width - S(36f), ly = left.Y + S(14f);

            // UI Language Selector (Indonesia | English)
            if (lw2 >= S(316f))
            {
                Theme.DrawTextLeft(g, T("Bahasa Aplikasi", "App Language"), fSmB, pal.TextSub, SR(lx, ly, S(130f), S(30f)));
                RectangleF langBox = SR(lx + lw2 - S(176f), ly, S(176f), S(30f));
                Theme.FillRR(g, langBox, S(9f), pal.Well);
                Theme.DrawRR(g, langBox, S(9f), pal.Border, 1f);
                DrawSegItem(g, pal, "UILANG_id", SR(langBox.X + S(3f), langBox.Y + S(3f), S(84f), S(24f)), "Indonesia", _cfg.UiLang != "en", fCap);
                DrawSegItem(g, pal, "UILANG_en", SR(langBox.X + S(89f), langBox.Y + S(3f), S(84f), S(24f)), "English", _cfg.UiLang == "en", fCap);
                ly += S(38f);
            }
            else
            {
                Theme.DrawTextLeft(g, T("Bahasa Aplikasi", "App Language"), fSmB, pal.TextSub, SR(lx, ly, lw2, S(24f)));
                ly += S(26f);
                RectangleF langBox = SR(lx, ly, S(176f), S(30f));
                Theme.FillRR(g, langBox, S(9f), pal.Well);
                Theme.DrawRR(g, langBox, S(9f), pal.Border, 1f);
                DrawSegItem(g, pal, "UILANG_id", SR(langBox.X + S(3f), langBox.Y + S(3f), S(84f), S(24f)), "Indonesia", _cfg.UiLang != "en", fCap);
                DrawSegItem(g, pal, "UILANG_en", SR(langBox.X + S(89f), langBox.Y + S(3f), S(84f), S(24f)), "English", _cfg.UiLang == "en", fCap);
                ly += S(38f);
            }

            // Translation Tone Selector (Gaul | Santai | Baku)
            Theme.DrawTextLeft(g, T("Gaya Bahasa", "Tone Style"), fSmB, pal.TextSub, SR(lx, ly, S(112f), S(30f)));
            float toneW = Math.Min(S(236f), lw2 - S(116f));
            RectangleF toneBox = SR(lx + lw2 - toneW, ly, toneW, S(30f));
            Theme.FillRR(g, toneBox, S(9f), pal.Well);
            Theme.DrawRR(g, toneBox, S(9f), pal.Border, 1f);
            float twSeg = (toneW - S(6f)) / 3f;
            DrawSegItem(g, pal, "TONE_0", SR(toneBox.X + S(3f), toneBox.Y + S(3f), twSeg, S(24f)), "Gaul", _cfg.ToneMode == 0, fCap);
            DrawSegItem(g, pal, "TONE_1", SR(toneBox.X + S(3f) + twSeg, toneBox.Y + S(3f), twSeg, S(24f)), "Santai", _cfg.ToneMode == 1, fCap);
            DrawSegItem(g, pal, "TONE_2", SR(toneBox.X + S(3f) + twSeg * 2f, toneBox.Y + S(3f), twSeg, S(24f)), "Baku", _cfg.ToneMode == 2, fCap);
            ly += S(38f);

            // Background Texture Selector (Arsiran | Grid | Dot | Polos)
            Theme.DrawTextLeft(g, T("Pola Latar", "Bg Texture"), fSmB, pal.TextSub, SR(lx, ly, S(112f), S(30f)));
            RectangleF patBox = SR(lx + lw2 - toneW, ly, toneW, S(30f));
            Theme.FillRR(g, patBox, S(9f), pal.Well);
            Theme.DrawRR(g, patBox, S(9f), pal.Border, 1f);
            float pwSeg = (toneW - S(6f)) / 4f;
            string[] patLbls = new string[] { T("Arsir", "Hatch"), "Grid", "Dot", T("Polos", "Flat") };
            for (int pi = 0; pi < 4; pi++)
                DrawSegItem(g, pal, "PAT_" + pi, SR(patBox.X + S(3f) + pi * pwSeg, patBox.Y + S(3f), pwSeg, S(24f)), patLbls[pi], _cfg.BgPattern == pi, fCap);
            ly += S(38f);

            DrawSliderRow(g, pal, "SL_INTERVAL", SR(lx, ly, lw2, S(30f)), T("Interval Scan", "Scan Interval"), _cfg.Interval + " ms", _cfg.Interval, 60, 1000, fSmB);
            ly += S(36f);

            string idleLabel = _cfg.AutoHideSeconds == 0 ? T("Mati", "Off") : (_cfg.AutoHideSeconds + (_cfg.UiLang == "en" ? " sec" : " dtk"));
            DrawSliderRow(g, pal, "SL_AUTOHIDE", SR(lx, ly, lw2, S(30f)), "Auto Blank", idleLabel, _cfg.AutoHideSeconds, 0, 30, fSmB);
            ly += S(38f);

            DrawToggleRow(g, pal, "SYS_ENHANCE_OCR", SR(lx, ly, lw2, S(32f)), T("Perjelas Huruf OCR Tanpa Jeda (Anti-Bg)", "Zero-Delay Borderless OCR Enhancer"), _cfg.EnhanceGameOcr, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_INSTANT_SUB", SR(lx, ly, lw2, S(32f)), T("Mode Subtitle Langsung & Anti-Ganti Arti", "Instant Subtitle + Anti-Flip-Flop Lock"), _cfg.InstantSubtitleMode, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_SPEAKER", SR(lx, ly, lw2, S(32f)), T("Deteksi Nama Pembicara (Speaker Badge)", "Detect Speaker Name (Speaker Badge)"), _cfg.DetectSpeaker, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_STABLE", SR(lx, ly, lw2, S(32f)), T("Tunggu animasi mesin ketik (RPG klasik)", "Wait for typewriter animation (classic RPG)"), _cfg.WaitStable, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_MERGE", SR(lx, ly, lw2, S(32f)), T("Gabung baris (mode Dialog)", "Merge lines (Dialog mode)"), _cfg.MergeLines, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_HIDEEMPTY", SR(lx, ly, lw2, S(32f)), T("Sembunyikan kotak saat kosong", "Hide overlay when empty"), _cfg.HideEmpty, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_ALLOW_CAP", SR(lx, ly, lw2, S(32f)), T("Izinkan Screenshot / Rekam Overlay Hasil", "Allow Screenshot / Record Result Overlay"), _cfg.AllowOverlayCapture, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_AUTOLIVE", SR(lx, ly, lw2, S(32f)), T("Otomatis Live saat dibuka", "Auto-start Live on launch"), _cfg.AutoLive, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_TRAY", SR(lx, ly, lw2, S(32f)), T("Tutup ke System Tray", "Minimize to System Tray on close"), _cfg.CloseToTray, fSm); ly += S(36f);
            DrawToggleRow(g, pal, "SYS_AUTO_UPDATE", SR(lx, ly, lw2, S(32f)), T("Periksa Pembaruan Otomatis", "Check updates automatically"), _cfg.AutoCheckUpdate, fSm); ly += S(40f);

            float halfBtn = (lw2 - S(8f)) * 0.5f;
            int glossCount = string.IsNullOrWhiteSpace(_cfg.Glossary) ? 0 : _cfg.Glossary.Split(new char[] { ';', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
            string glossLbl = glossCount > 0 ? (T("Glosarium (", "Glossary (") + glossCount + ")") : T("Glosarium Game", "Game Glossary");
            DrawOutlineBtn(g, pal, "BTN_GLOSSARY", SR(lx, ly, halfBtn, S(36f)), glossLbl, IconKind.Gamepad, fSmB, S(9f));

            // Gemini AI API Key Button
            string gLbl = string.IsNullOrEmpty(_cfg.GeminiApiKey) ? "Gemini AI Key" : T("Gemini Key Aktif", "Gemini Key Active");
            DrawOutlineBtn(g, pal, "BTN_GEMINI_KEY", SR(lx + halfBtn + S(8f), ly, halfBtn, S(36f)), gLbl, IconKind.Sparkle, fSmB, S(9f));

            List<string> packs = _engine.InstalledOcrLanguages;
            string pStr = packs.Count > 0 ? (T("WinRT OCR Lokal: ", "Local WinRT OCR: ") + string.Join(", ", packs.ToArray())) : "OCR: Online Fallback";
            RectangleF pb = SR(lx, left.Bottom - S(40f), lw2, S(26f));
            Theme.FillRR(g, pb, S(8f), pal.Well);
            Theme.DrawTextLeft(g, pStr, fCap, pal.TextSub, SR(pb.X + S(10f), pb.Y, pb.Width - S(20f), pb.Height));

            RectangleF right;
            if (twoCol)
            {
                right = SR(x + colW + colGap, cy, colW, colH);
            }
            else
            {
                cy += colH + S(14f);
                right = SR(x, cy, colW, colH);
            }

            Theme.DrawCard(g, pal, right, S(16f));
            float rx = right.X + S(18f), rw2 = right.Width - S(36f), ry = right.Y + S(16f);

            string[,] keys = new string[,]
            {
                { "Alt + E", T("Mulai / Hentikan Live", "Start / Stop Live") },
                { "Alt + Q", T("Blok Layar Area OCR Baru", "Snip New OCR Area") },
                { "Alt + A", T("Blok Layar Kotak Hasil", "Snip Result Box") },
                { "Alt + W", T("Edit Semua Kotak di Layar", "Edit All Boxes on Screen") },
                { "Alt + R", T("Scan Ulang Sekarang", "Rescan Now") },
                { "Alt + H", T("Sembunyikan Overlay", "Toggle Hide Overlay") },
                { "F11",     T("Fullscreen Penuh", "Toggle Fullscreen") },
                { "Ctrl + / -", T("Zoom In / Out Tampilan", "Zoom In / Out UI") }
            };

            for (int i = 0; i < 8; i++)
            {
                RectangleF kr = SR(rx, ry, rw2, S(36f));
                Theme.FillRR(g, kr, S(8f), pal.Well);
                Theme.DrawTextLeft(g, keys[i, 1], fSm, pal.Text, SR(kr.X + S(12f), kr.Y, kr.Width - S(92f), kr.Height));
                RectangleF kb = SR(kr.Right - S(76f), kr.Y + S(6f), S(66f), S(24f));
                Theme.FillRR(g, kb, S(6f), pal.Card);
                Theme.DrawRR(g, kb, S(6f), pal.BorderStrong, 1f);
                Theme.DrawTextCenter(g, keys[i, 0], fCap, pal.Text, kb);
                ry += S(42f);
            }

            RectangleF tip = SR(rx, right.Bottom - S(86f), rw2, S(70f));
            Theme.FillRR(g, tip, S(12f), pal.Well);
            Theme.DrawRR(g, tip, S(12f), pal.Border, 1f);
            Theme.DrawTextLeft(g, T("Tampil di Atas Game", "Display Over Games"), fSmB, pal.Text, SR(tip.X + S(12f), tip.Y + S(8f), tip.Width - S(24f), S(18f)));
            Theme.DrawTextWrap(g, T("Setel Display Mode game ke Borderless Windowed atau Windowed agar overlay tampil mulus & click-through.",
                "Set your game Display Mode to Borderless Windowed or Windowed for smooth click-through overlays."),
                fCap, pal.TextSub, SR(tip.X + S(12f), tip.Y + S(28f), tip.Width - S(24f), S(36f)));

            cy += colH + S(16f);
            return cy - y;
        }

        // =========================================================================
        // TAB 3: LOG PEMBARUAN (CHANGELOG) & CREDIT BY DANZYYY
        // =========================================================================
        private float RenderTabChangelog(Graphics g, Palette pal, float x, float y, float w,
            Font fSerifLg, Font fSec, Font fBody, Font fBodyB, Font fSm, Font fSmB, Font fCap)
        {
            float cy = y;
            if (w >= S(500f))
            {
                Theme.DrawTextLeft(g, T("Log Pembaruan", "Changelog"), fSerifLg, pal.Text, SR(x, cy, S(280f), S(34f)));
                Theme.DrawTextRight(g, T("Dibuat oleh Danzyyy", "Created by Danzyyy"), fSmB, pal.TextSub, SR(x + w - S(200f), cy + S(6f), S(200f), S(24f)));
                cy += S(48f);
            }
            else
            {
                Theme.DrawTextLeft(g, T("Log Pembaruan", "Changelog"), fSerifLg, pal.Text, SR(x, cy, w, S(34f)));
                cy += S(36f);
                Theme.DrawTextLeft(g, T("Dibuat oleh Danzyyy", "Created by Danzyyy"), fSmB, pal.TextSub, SR(x, cy, w, S(24f)));
                cy += S(32f);
            }

            // 0. UPDATE STATUS & ACTION CARD
            bool hasUp = UpdateChecker.Info.HasUpdate;
            bool isChecking = UpdateChecker.Info.IsChecking;
            float chkH = S(106f);
            RectangleF upCard = SR(x, cy, w, chkH);
            Theme.DrawCard(g, pal, upCard, S(16f));

            float icSz = S(22f);
            Color icCol = hasUp ? pal.Accent : (isChecking ? pal.TextSub : (pal.Dark ? Color.FromArgb(74, 222, 128) : Color.FromArgb(34, 197, 94)));
            Theme.DrawIcon(g, hasUp ? IconKind.Rocket : (isChecking ? IconKind.Refresh : IconKind.Check),
                SR(upCard.X + S(18f), upCard.Y + S(18f), icSz, icSz), icCol, 2f);

            string statusTitle = hasUp
                ? T("Tersedia Pembaruan: " + UpdateChecker.Info.LatestVersion + "!", "New Update Available: " + UpdateChecker.Info.LatestVersion + "!")
                : (isChecking
                    ? T("Memeriksa Pembaruan ke GitHub...", "Checking for updates on GitHub...")
                    : T("LingoLens v0.4 Beta — Versi Terbaru", "LingoLens v0.4 Beta — Latest Version"));

            string statusDesc = hasUp
                ? T("Rilis resmi terbaru dari Danzyyy telah tersedia di GitHub. Klik tombol untuk mengunduh sekarang.",
                    "Latest official release by Danzyyy is now live on GitHub. Click button to download now.")
                : (UpdateChecker.Info.LastChecked.HasValue
                    ? T("Terakhir dicek: " + UpdateChecker.Info.LastChecked.Value.ToString("HH:mm:ss") + " • Aplikasi Anda sudah menggunakan versi paling mutakhir.",
                        "Last checked: " + UpdateChecker.Info.LastChecked.Value.ToString("HH:mm:ss") + " • Your app is fully up-to-date.")
                    : T("Klik 'Periksa Pembaruan' untuk memeriksa rilis terbaru langsung dari GitHub.",
                        "Click 'Check for Updates' to fetch latest releases directly from GitHub."));

            float txtW = Math.Max(S(120f), upCard.Width - S(330f));
            Theme.DrawTextLeft(g, statusTitle, fSec, pal.Text, SR(upCard.X + S(48f), upCard.Y + S(14f), txtW, S(26f)));
            Theme.DrawTextWrap(g, statusDesc, fSm, pal.TextSub, SR(upCard.X + S(48f), upCard.Y + S(42f), txtW, S(48f)));

            // Action buttons on the right
            float btnRowW = hasUp ? S(266f) : S(176f);
            float btnX = upCard.Right - btnRowW - S(18f);
            float btnY = upCard.Y + (chkH - S(36f)) * 0.5f;

            if (hasUp)
            {
                RectangleF btnDown = SR(btnX, btnY, S(130f), S(36f));
                DrawAccentBtn(g, pal, "BTN_DOWNLOAD_UPDATE", btnDown, T("Unduh Update", "Download"), IconKind.Download, pal.Accent, pal.AccentHover, fSmB, S(8f));

                RectangleF btnRel = SR(btnX + S(136f), btnY, S(130f), S(36f));
                DrawOutlineBtn(g, pal, "BTN_OPEN_RELEASE", btnRel, T("Halaman Rilis", "Release Page"), IconKind.Rocket, fSmB, S(8f));
            }
            else
            {
                RectangleF btnChk = SR(btnX, btnY, S(176f), S(36f));
                string btnTxt = isChecking ? T("Memeriksa...", "Checking...") : T("Periksa Pembaruan", "Check for Updates");
                DrawOutlineBtn(g, pal, "BTN_CHECK_UPDATE", btnChk, btnTxt, IconKind.Refresh, fSmB, S(8f));
            }

            cy += chkH + S(16f);

            // Card v0.4 Beta
            RectangleF card4 = SR(x, cy, w, S(248f));
            Theme.DrawCard(g, pal, card4, S(12f));

            RectangleF vPill4 = SR(card4.X + S(18f), card4.Y + S(14f), S(86f), S(24f));
            Theme.FillRR(g, vPill4, S(12f), pal.Accent);
            Theme.DrawTextCenter(g, "v0.4 Beta", fCap, pal.AccentBtnText, vPill4);

            string title4 = T("OCR Border Tipis di Background Putih, Anti-Flip-Flop, Screenshot Overlay & 2K/4K UI", "White-on-White Border OCR, Anti-Flip-Flop, Overlay Screenshot & 2K/4K UI");
            string notes4 = _cfg.UiLang == "en"
                ? "• Pass 4 Thin-Border Fill OCR (Morphological Closing) for white text with thin black outline on white/snow backgrounds.\n" +
                  "• Lower latency (Smart Early-Exit Pass 1-4, 40ms fast-poll) + stronger Anti-Flip-Flop lock when background changes.\n" +
                  "• Edge-case OCR: colored/neon subtitles, textured parchment, CJK furigana filter & auto-typewriter detection.\n" +
                  "• Allow/Hide Result Overlay in Screenshots/OBS toggle + Dialog History export (.txt/.csv) + DPAPI key encryption.\n" +
                  "• Responsive 2K/4K layout scaling, Per-Monitor DPI (WM_DPICHANGED), hotkey conflict alert & modular engine."
                : "• Pass 4 OCR Border Tipis (Morphological Closing) untuk font putih + garis hitam tipis di background putih/salju.\n" +
                  "• Latensi lebih cepat (Smart Early-Exit Pass 1-4, fast-poll 40ms) + kunci Anti-Flip-Flop lebih kuat saat background berubah.\n" +
                  "• Edge-case OCR: teks warna/neon, kertas perkamen bertekstur, filter furigana CJK & auto-deteksi typewriter.\n" +
                  "• Toggle Izinkan Screenshot/Rekam Overlay Hasil + Ekspor Riwayat Dialog (.txt/.csv) + enkripsi API Key DPAPI.\n" +
                  "• Layout responsif layar 2K/4K, Per-Monitor DPI (WM_DPICHANGED), deteksi konflik hotkey & arsitektur modular.";

            Theme.DrawTextLeft(g, title4, fBodyB, pal.Text, SR(card4.X + S(114f), card4.Y + S(14f), card4.Width - S(132f), S(24f)));
            Theme.DrawTextWrap(g, notes4, fSm, pal.TextSub, SR(card4.X + S(18f), card4.Y + S(48f), card4.Width - S(36f), S(186f)));

            cy += card4.Height + S(14f);

            // Card v0.3 Beta
            RectangleF card3 = SR(x, cy, w, S(268f));
            Theme.DrawCard(g, pal, card3, S(12f));

            RectangleF vPill3 = SR(card3.X + S(18f), card3.Y + S(14f), S(86f), S(24f));
            Theme.FillRR(g, vPill3, S(12f), pal.Accent);
            Theme.DrawTextCenter(g, "v0.3 Beta", fCap, pal.AccentBtnText, vPill3);

            string title3 = T("Rombak UI Arsiran, OCR Temporal Tanpa Background, Bahasa Gaul & Lore Game", "Editorial Hatch UI, Temporal Borderless OCR, Slang Mode & Game Lore");
            string notes3 = _cfg.UiLang == "en"
                ? "• Redesigned Studio UI with architectural diagonal hatch / grid background, High-DPI scaling & custom dark-chrome modals.\n" +
                  "• 2-Frame Temporal Background Eraser + Auto-Crop Active Text Band + Local High-Pass OCR for borderless 3D subtitles.\n" +
                  "• Target Game Window Lock: lock OCR to your active game/browser window and auto-pause on Alt+Tab.\n" +
                  "• Indonesian Gaul Mode (Gue/Lo) default + Santai + Formal tone selector, plus Lore-aware Gemini context.\n" +
                  "• Pre-Game Selector & Auto Game Lore Finder: 14 built-in presets + online Wikipedia lore & auto-glossary fetcher.\n" +
                  "• Pixel-Space Static Subtitle Lock & Trigram Anti-Flip-Flop + Session Dialog Backlog viewer."
                : "• Rombak total UI dengan latar arsiran diagonal / grid, skala High-DPI otomatis, ikon HUD gaming & modal dark-chrome kustom.\n" +
                  "• Temporal Background Eraser 2-Frame + Auto-Crop Pita Teks + Local High-Pass OCR untuk subtitle tanpa kotak di adegan 3D.\n" +
                  "• Kunci Jendela Game: kunci OCR ke proses game/browser aktif — otomatis jeda & sembunyikan overlay saat Alt+Tab.\n" +
                  "• Mode Bahasa Gaul (Gue/Lo) bawaan + pilihan Santai & Baku, lengkap dengan konteks cerita buat Gemini AI.\n" +
                  "• Pilih Game & Cari Lore Otomatis: 14 preset game populer + pencarian lore Wikipedia & auto-glosarium karakter.\n" +
                  "• Pengunci Piksel Subtitle Statis & Trigram Anti-Flip-Flop + fitur Riwayat Dialog sesi.";

            Theme.DrawTextLeft(g, title3, fSec, pal.Text, SR(vPill3.Right + S(12f), card3.Y + S(14f), card3.Width - S(136f), S(24f)));
            Theme.DrawTextWrap(g, notes3, fSm, pal.TextSub, SR(card3.X + S(18f), card3.Y + S(48f), card3.Width - S(36f), card3.Height - S(58f)));
            cy += card3.Height + S(16f);

            // Card v0.2 Beta
            RectangleF card2 = SR(x, cy, w, S(280f));
            Theme.DrawCard(g, pal, card2, S(16f));

            RectangleF vPill2 = SR(card2.X + S(18f), card2.Y + S(14f), S(78f), S(24f));
            Theme.FillRR(g, vPill2, S(12f), pal.Accent);
            Theme.DrawTextCenter(g, "v0.2 Beta", fCap, pal.AccentBtnText, vPill2);

            string title2 = T("Pembaruan Ikon Modern, Penjernih OCR Game & Anti-Flicker", "Modern Geometric Icons, Game OCR Clarity & Anti-Flicker");
            string notes2 = _cfg.UiLang == "en"
                ? "• Redesigned modern geometric vector icons across the entire UI with informative hover tooltips.\n" +
                  "• Smart Game OCR Engine: dynamic upscale & background noise zeroing eliminates garbled characters from 3D scenes.\n" +
                  "• Game Text Sanitizer: fixes retro game font misreads (Link, Hyrule, Navi) and strips noise tokens.\n" +
                  "• Faster translation responsiveness: background HTTPS/TLS pre-warming and optimized network timeouts.\n" +
                  "• Smart Anti-Flicker stabilization: core text normalization ignores punctuation jitter and prevents meaning flip-flopping.\n" +
                  "• Game Typewriter Debounce: waits for character-by-character dialogue to finish before translating."
                : "• Desain ikon vektor geometris modern dan profesional dengan tooltip informatif saat kursor diarahkan.\n" +
                  "• Mesin OCR Game Cerdas: penjernih teks dinamis menyingkirkan noise background 3D game & mempertajam font karakter.\n" +
                  "• Game Text Sanitizer: memperbaiki salah baca font game retro (Link, Hyrule, Navi) & membuang karakter sampah.\n" +
                  "• Respon terjemahan lebih kencang dengan pre-warming koneksi HTTPS/TLS di latar belakang & optimasi timeout.\n" +
                  "• Stabilisasi Cerdas Anti-Flicker: normalisasi teks inti mengabaikan tanda baca & mencegah arti berganti-ganti.\n" +
                  "• Debounce Efek Mesin Ketik Game: menunggu kalimat selesai diketik sebelum diterjemahkan secara utuh.";

            Theme.DrawTextLeft(g, title2, fSec, pal.Text, SR(vPill2.Right + S(12f), card2.Y + S(14f), card2.Width - S(128f), S(24f)));
            Theme.DrawTextWrap(g, notes2, fSm, pal.TextSub, SR(card2.X + S(18f), card2.Y + S(48f), card2.Width - S(36f), card2.Height - S(58f)));
            cy += card2.Height + S(16f);

            // Card v0.1 Beta
            RectangleF card = SR(x, cy, w, S(240f));
            Theme.DrawCard(g, pal, card, S(16f));

            RectangleF vPill = SR(card.X + S(18f), card.Y + S(14f), S(78f), S(24f));
            Theme.FillRR(g, vPill, S(12f), pal.CardHover);
            Theme.DrawTextCenter(g, "v0.1 Beta", fCap, pal.Text, vPill);

            string title = T("Rilis Perdana LingoLens (C# Native)", "LingoLens Initial Release (C# Native)");
            string notes = _cfg.UiLang == "en"
                ? "• Fast Windows WinRT OCR with online fallback & dual-area support (Dialog + line-by-line Choices).\n" +
                  "• Polite conversational Indonesian naturalization + optional Gemini AI contextual translation.\n" +
                  "• Game Glossary & Character Name protection + automatic Speaker Name Badge above dialog.\n" +
                  "• Per-Area custom overlay styling, Game Profile templates, Auto-Blank idle, and ESC hotkey cancel fix.\n" +
                  "• Bilingual UI (Indonesia / English), Dark & Light Mode, Zoom 75%-150%, Fullscreen F11, Portable & Installer."
                : "• WinRT OCR cepat lokal + fallback online dengan mode Dual-Area (Dialog + Pilihan pisah per baris).\n" +
                  "• Bahasa terjemahan sehari-hari yang natural & sopan + dukungan opsional Gemini AI.\n" +
                  "• Glosarium Nama Karakter/Istilah per game + Deteksi Nama Pembicara (Speaker Badge) otomatis.\n" +
                  "• Pengaturan tampilan Per-Area, Template Profil Game, Auto-Blank saat idle, & perbaikan ESC saat blok layar.\n" +
                  "• Pilihan bahasa aplikasi (Indonesia / English), Dark & Light Mode, Zoom 75%-150%, Fullscreen F11, Portable & Installer.";

            Theme.DrawTextLeft(g, title, fSec, pal.TextSub, SR(vPill.Right + S(12f), card.Y + S(14f), card.Width - S(128f), S(24f)));
            Theme.DrawTextWrap(g, notes, fSm, pal.TextSub, SR(card.X + S(18f), card.Y + S(48f), card.Width - S(36f), card.Height - S(58f)));
            cy += card.Height + S(16f);

            return cy - y;
        }

        // =========================================================================
        // COMPONENT HELPERS
        // =========================================================================
        private void DrawSwitchScaled(Graphics g, Palette pal, RectangleF box, bool on)
        {
            float sw = S(38f), sh = S(20f);
            RectangleF pill = SR(box.X + (box.Width - sw) * 0.5f, box.Y + (box.Height - sh) * 0.5f, sw, sh);
            Theme.FillRR(g, pill, sh * 0.5f, on ? pal.Accent : pal.BorderStrong);
            float kd = sh - S(6f);
            float kx = on ? (pill.Right - kd - S(3f)) : (pill.X + S(3f));
            Color knobCol = on ? (pal.Dark ? Color.FromArgb(18, 18, 20) : Color.White) : (pal.Dark ? Color.FromArgb(161, 161, 170) : Color.White);
            using (SolidBrush w = new SolidBrush(knobCol))
                g.FillEllipse(w, kx, pill.Y + S(3f), kd, kd);
        }

        private void DrawChromeIcon(Graphics g, Palette pal, string id, RectangleF r, IconKind ic, bool danger)
        {
            bool hov = (_hoverId == id);
            if (hov) Theme.FillRR(g, r, S(7f), danger ? Color.FromArgb(204, 62, 62) : pal.CardHover);
            float isz = S(14f);
            Theme.DrawIcon(g, ic, SR(r.X + (r.Width - isz) * 0.5f, r.Y + (r.Height - isz) * 0.5f, isz, isz),
                (hov && danger) ? Color.White : (hov ? pal.Text : pal.TextSub), 1.55f);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawZoomBtn(Graphics g, Palette pal, string id, RectangleF r, IconKind ic)
        {
            bool hov = (_hoverId == id);
            if (hov) Theme.FillRR(g, r, S(6f), pal.CardHover);
            float isz = S(14f);
            Theme.DrawIcon(g, ic, SR(r.X + (r.Width - isz) * 0.5f, r.Y + (r.Height - isz) * 0.5f, isz, isz), hov ? pal.Text : pal.TextSub, 1.7f);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawSidebarBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, Font f)
        {
            bool hov = (_hoverId == id);
            if (hov) Theme.FillRR(g, r, S(8f), pal.CardHover);
            float isz = S(14f);
            Theme.DrawIcon(g, ic, SR(r.X + S(10f), r.Y + (r.Height - isz) * 0.5f, isz, isz), hov ? pal.Text : pal.TextSub, 1.6f);
            Theme.DrawTextLeft(g, text, f, hov ? pal.Text : pal.TextSub, SR(r.X + S(32f), r.Y, r.Width - S(36f), r.Height));
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawAccentBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, Color c1, Color c2, Font f, float rad)
        {
            bool hov = (_hoverId == id);
            Theme.FillRR(g, r, rad, hov ? c2 : c1);
            Color fg = (c1 == pal.Accent) ? pal.AccentBtnText : Color.White;
            float lx = r.X + S(13f);
            if (ic != IconKind.None)
            {
                float isz = S(14f);
                Theme.DrawIcon(g, ic, SR(lx, r.Y + (r.Height - isz) * 0.5f, isz, isz), fg, 1.8f);
                lx += S(20f);
            }
            Theme.DrawTextLeft(g, text, f, fg, SR(lx, r.Y, r.Right - lx - S(10f), r.Height));
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawOutlineBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, Font f, float rad)
        {
            bool hov = (_hoverId == id);
            Theme.FillRR(g, r, rad, hov ? pal.CardHover : pal.Card);
            Theme.DrawRR(g, r, rad, hov ? (pal.Dark ? pal.Text : pal.Accent) : pal.Border, 1f);
            float isz = S(14f);
            if (string.IsNullOrEmpty(text) && ic != IconKind.None)
                Theme.DrawIcon(g, ic, SR(r.X + (r.Width - isz) * 0.5f, r.Y + (r.Height - isz) * 0.5f, isz, isz), hov ? pal.Text : pal.TextSub, 1.65f);
            else if (ic != IconKind.None)
            {
                Theme.DrawIcon(g, ic, SR(r.X + S(11f), r.Y + (r.Height - isz) * 0.5f, isz, isz), hov ? pal.Text : pal.TextSub, 1.65f);
                Theme.DrawTextLeft(g, text, f, pal.Text, SR(r.X + S(31f), r.Y, r.Width - S(36f), r.Height));
            }
            else Theme.DrawTextCenter(g, text, f, pal.Text, r);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawAreaActionBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, int areaId, Color icCol, Font f)
        {
            bool hov = (_hoverId == id && _hoverIdx == areaId);
            Theme.FillRR(g, r, S(7f), hov ? pal.CardHover : pal.Card);
            Theme.DrawRR(g, r, S(7f), hov ? (pal.Dark ? pal.Text : pal.Accent) : pal.Border, 1f);
            float isz = S(13f);
            Theme.DrawIcon(g, ic, SR(r.X + S(9f), r.Y + (r.Height - isz) * 0.5f, isz, isz), icCol, 1.7f);
            Theme.DrawTextLeft(g, text, f, pal.Text, SR(r.X + S(27f), r.Y, r.Width - S(30f), r.Height));
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r, Index = areaId });
        }

        private void DrawMiniIcon(Graphics g, Palette pal, string id, RectangleF r, IconKind ic, int index, bool danger, bool enabled)
        {
            bool hov = enabled && (_hoverId == id && _hoverIdx == index);
            if (hov) Theme.FillRR(g, r, S(7f), danger ? pal.DangerSoft : pal.CardHover);
            Color col = !enabled ? pal.BorderStrong : (hov && danger ? pal.Danger : (hov ? pal.Text : pal.TextSub));
            float isz = S(14f);
            Theme.DrawIcon(g, ic, SR(r.X + (r.Width - isz) * 0.5f, r.Y + (r.Height - isz) * 0.5f, isz, isz), col, 1.65f);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r, Index = index, Enabled = enabled });
        }

        private void DrawSegItem(Graphics g, Palette pal, string id, RectangleF r, string text, bool active, Font f)
        {
            DrawSegItemIdx(g, pal, id, r, text, active, -1, f);
        }

        private void DrawSegItemIdx(Graphics g, Palette pal, string id, RectangleF r, string text, bool active, int idx, Font f)
        {
            bool hov = (_hoverId == id && _hoverIdx == idx);
            if (active)
            {
                Theme.FillRR(g, r, S(6f), pal.Accent);
                Theme.DrawTextCenter(g, text, f, pal.AccentBtnText, r);
            }
            else
            {
                if (hov) Theme.FillRR(g, r, S(6f), pal.CardHover);
                Theme.DrawTextCenter(g, text, f, hov ? pal.Text : pal.TextSub, r);
            }
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r, Index = idx });
        }

        private void DrawSelect(Graphics g, Palette pal, string id, RectangleF r, string label, Font f)
        {
            bool open = (_openDrop == id), hov = (_hoverId == id);
            Theme.FillRR(g, r, S(9f), pal.Card);
            Theme.DrawRR(g, r, S(9f), (open || hov) ? (pal.Dark ? pal.Text : pal.Accent) : pal.Border, 1f);
            Theme.DrawTextLeft(g, label, f, pal.Text, SR(r.X + S(10f), r.Y, r.Width - S(26f), r.Height));
            using (Pen p = new Pen(pal.TextSub, 1.6f))
            {
                float ax = r.Right - S(15f), ay = r.Y + r.Height * 0.5f - S(2f);
                g.DrawLine(p, ax, ay, ax + S(4f), ay + S(4f));
                g.DrawLine(p, ax + S(4f), ay + S(4f), ax + S(8f), ay);
            }
            _hits.Add(new Hit { Id = id, Kind = HitKind.Dropdown, Box = r });
        }

        private void DrawSliderRow(Graphics g, Palette pal, string id, RectangleF r, string label, string valText, int val, int min, int max, Font f)
        {
            Theme.DrawTextLeft(g, label, f, pal.TextSub, SR(r.X, r.Y, S(98f), r.Height));
            Theme.DrawTextRight(g, valText, f, pal.Text, SR(r.Right - S(58f), r.Y, S(58f), r.Height));
            float tx = r.X + S(102f), tw = Math.Max(S(40f), r.Width - S(166f)), cy = r.Y + r.Height * 0.5f, th = S(6f);
            Theme.FillRR(g, SR(tx, cy - th * 0.5f, tw, th), th * 0.5f, pal.Well);
            float t = (float)(Math.Max(min, Math.Min(max, val)) - min) / Math.Max(1, max - min);
            Theme.FillRR(g, SR(tx, cy - th * 0.5f, Math.Max(th, tw * t), th), th * 0.5f, pal.Accent);
            float kx = tx + tw * t, kr = S(7.5f), ki = S(4f);
            using (SolidBrush b1 = new SolidBrush(pal.Accent))
            using (SolidBrush b2 = new SolidBrush(pal.Dark ? Color.FromArgb(9, 9, 11) : Color.White))
            {
                g.FillEllipse(b1, kx - kr, cy - kr, kr * 2f, kr * 2f);
                g.FillEllipse(b2, kx - ki, cy - ki, ki * 2f, ki * 2f);
            }
            _hits.Add(new Hit { Id = id, Kind = HitKind.Slider, Box = SR(tx - S(6f), r.Y, tw + S(12f), r.Height), Min = min, Max = max });
        }

        private void DrawToggleRow(Graphics g, Palette pal, string id, RectangleF r, string label, bool on, Font f)
        {
            bool hov = (_hoverId == id);
            Theme.FillRR(g, r, S(9f), hov ? pal.CardHover : pal.Well);
            Theme.DrawTextLeft(g, label, f, pal.Text, SR(r.X + S(13f), r.Y, r.Width - S(68f), r.Height));
            DrawSwitchScaled(g, pal, SR(r.Right - S(48f), r.Y, S(40f), r.Height), on);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawColorBtn(Graphics g, Palette pal, string id, RectangleF r, string label, Color swatch, Font f)
        {
            bool hov = (_hoverId == id);
            Theme.FillRR(g, r, S(8f), hov ? pal.CardHover : pal.Well);
            Theme.DrawRR(g, r, S(8f), hov ? pal.Accent : pal.BorderStrong, 1f);
            float ssz = S(18f);
            RectangleF sw = SR(r.X + S(9f), r.Y + (r.Height - ssz) * 0.5f, ssz, ssz);
            Theme.FillRR(g, sw, S(5f), swatch);
            Theme.DrawRR(g, sw, S(5f), pal.BorderStrong, 1f);
            Theme.DrawTextLeft(g, label, f, pal.Text, SR(sw.Right + S(7f), r.Y, r.Width - S(36f), r.Height));
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void RenderDropdownPopup(Graphics g, Palette pal, float cw, float ch, Font f, Font fB)
        {
            List<string> items = GetDropItems(_openDrop);
            if (items.Count == 0) return;

            int vis = Math.Min(7, items.Count);
            float ih = S(30f);
            float pw = Math.Max(S(180f), _dropAnchor.Width);
            float ph = vis * ih + S(8f);
            float px = Math.Min(cw - pw - S(12f), _dropAnchor.X);
            float py = _dropAnchor.Bottom + S(4f);
            if (py + ph > ch - S(12f)) py = _dropAnchor.Y - ph - S(4f);

            _dropPopup = SR(px, py, pw, ph);
            Theme.FillRR(g, SR(px, py + S(3f), pw, ph), S(10f), pal.Shadow);
            Theme.FillRR(g, _dropPopup, S(10f), pal.Card);
            Theme.DrawRR(g, _dropPopup, S(10f), pal.Accent, 1.2f);

            int maxScr = Math.Max(0, items.Count - vis);
            _dropScroll = Math.Max(0, Math.Min(_dropScroll, maxScr));

            for (int i = 0; i < vis; i++)
            {
                int idx = _dropScroll + i;
                RectangleF ir = SR(px + S(4f), py + S(4f) + i * ih, pw - S(8f), ih - S(2f));
                bool hov = (_hoverId == "DROP_ITEM" && _hoverIdx == idx);
                if (hov) Theme.FillRR(g, ir, S(6f), pal.AccentSoft);
                Theme.DrawTextLeft(g, items[idx], hov ? fB : f, hov ? pal.AccentText : pal.Text, SR(ir.X + S(8f), ir.Y, ir.Width - S(12f), ir.Height));
                _hits.Add(new Hit { Id = "DROP_ITEM", Kind = HitKind.DropItem, Box = ir, Index = idx });
            }
        }

        private List<string> GetDropItems(string id)
        {
            List<string> res = new List<string>();
            if (id == "DROP_SRC") for (int i = 0; i < SrcLangs.GetLength(0); i++) res.Add(SrcLangs[i, 1]);
            else if (id == "DROP_TGT") for (int i = 0; i < TgtLangs.GetLength(0); i++) res.Add(TgtLangs[i, 1]);
            else if (id == "DROP_FONT") res.AddRange(_fonts);
            else if (id == "DROP_PROFILE")
            {
                res.Add("Default");
                foreach (string p in AppConfig.ListGameProfiles()) if (p != "Default") res.Add(p);
            }
            return res;
        }

        private static string FindLabel(string[,] tbl, string code)
        {
            for (int i = 0; i < tbl.GetLength(0); i++) if (tbl[i, 0] == code) return tbl[i, 1];
            return code;
        }

        // =========================================================================
        // INTERACTION
        // =========================================================================
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (!string.IsNullOrEmpty(_openDrop))
            {
                for (int i = _hits.Count - 1; i >= 0; i--)
                {
                    if (_hits[i].Id == "DROP_ITEM" && _hits[i].Box.Contains(e.Location))
                    {
                        ApplyDrop(_openDrop, _hits[i].Index);
                        _openDrop = "";
                        Invalidate();
                        return;
                    }
                }
                _openDrop = "";
                Invalidate();
                return;
            }

            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                Hit h = _hits[i];
                if (!h.Enabled || !h.Box.Contains(e.Location)) continue;

                if (h.Kind == HitKind.Slider) { _activeSlider = h.Id; UpdateSlider(h.Id, e.X); return; }
                if (h.Kind == HitKind.Dropdown) { _openDrop = h.Id; _dropAnchor = h.Box; _dropScroll = 0; Invalidate(); return; }

                HandleAction(h.Id, h.Index);
                return;
            }

            if (e.Y <= S(46f))
            {
                if (e.Clicks == 2) ToggleMaximize();
                else if (!_isFullscreen && !_isMaximized)
                {
                    ReleaseCaptureNative();
                    SendMessageNative(Handle, 0x00A1, 2, 0);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!string.IsNullOrEmpty(_activeSlider)) { UpdateSlider(_activeSlider, e.X); return; }

            string nh = "";
            int ni = -1;
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (_hits[i].Enabled && _hits[i].Box.Contains(e.Location))
                {
                    nh = _hits[i].Id; ni = _hits[i].Index; break;
                }
            }
            if (nh != _hoverId || ni != _hoverIdx)
            {
                _hoverId = nh; _hoverIdx = ni;
                Cursor = string.IsNullOrEmpty(nh) ? Cursors.Default : Cursors.Hand;
                UpdateTooltip(nh, ni);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverId = "";
            _hoverIdx = -1;
            UpdateTooltip("", -1);
            Cursor = Cursors.Default;
            Invalidate();
        }

        private void UpdateTooltip(string id, int idx)
        {
            if (id == _lastTipId && idx == _lastTipIdx) return;
            _lastTipId = id;
            _lastTipIdx = idx;
            string tip = GetTooltipText(id, idx);
            if (!string.IsNullOrEmpty(tip))
            {
                _toolTip.SetToolTip(this, tip);
            }
            else
            {
                _toolTip.SetToolTip(this, null);
            }
        }

        private string GetTooltipText(string id, int idx)
        {
            if (string.IsNullOrEmpty(id)) return null;

            if (id.StartsWith("TAB_"))
            {
                int tabIdx = idx >= 0 ? idx : 0;
                switch (tabIdx)
                {
                    case 0: return T("Area & Live: Pengaturan area tangkapan OCR dan terjemahan langsung", "Area & Live: Manage OCR capture areas and live translation");
                    case 1: return T("Tampilan: Kustomisasi font, ukuran, warna, dan gaya kotak overlay", "Appearance: Customize font, size, colors, and overlay style");
                    case 2: return T("Sistem: Pengaturan tombol pintas, OCR, dan preferensi aplikasi", "System: Hotkeys, OCR engine, and general app settings");
                    case 3: return T("Catatan Rilis: Riwayat pembaruan dan fitur versi terbaru", "Changelog: Update history and newest release features");
                }
            }

            switch (id)
            {
                case "BTN_LIVE":
                    return (_engine != null && _engine.IsRunning)
                        ? T("Hentikan Penerjemahan Layar Real-Time (Alt+E)", "Stop Live Translation (Alt+E)")
                        : T("Mulai Penerjemahan Layar Real-Time (Alt+E)", "Start Live Translation (Alt+E)");

                case "BTN_EDIT_SCREEN":
                    return T("Buka Editor Visual: Geser & atur ukuran semua kotak langsung di layar (Alt+W)", "Open Visual Screen Editor: Move & resize all boxes live on screen (Alt+W)");

                case "BTN_SCAN":
                    return T("Pindai & Terjemahkan layar saat ini satu kali (Scan Sekali)", "Capture & translate current screen once");

                case "BTN_PREVIEW_LIVE":
                    return T("Uji coba terjemahan dialog contoh ke overlay", "Test sample dialog translation to overlay");

                case "BTN_ADD_AREA":
                    return T("Tambah area tangkapan baru untuk dialog kedua atau teks terpisah", "Add new capture area for secondary dialog or separate UI text");

                case "BTN_SWAP":
                    return T("Tukar bahasa sumber dan bahasa target", "Swap source and target languages");

                case "BTN_COPY":
                    return T("Salin teks hasil terjemahan terakhir ke clipboard", "Copy latest translated text to clipboard");

                case "BTN_GLOSSARY":
                    return T("Kamus Istilah (Glossary): Lindungi nama karakter & istilah game agar tidak salah diterjemahkan", "Glossary: Protect character names & game terms from incorrect translation");

                case "BTN_GEMINI_KEY":
                    return T("Masukkan API Key Google Gemini (opsional, untuk terjemahan berbasis AI)", "Set Google Gemini API Key (optional, for AI-powered translation)");

                case "BTN_SAVE_PROFILE":
                    return T("Simpan konfigurasi profil game saat ini ke file penyimpanan", "Save current game profile configuration to disk");

                case "BTN_THEME":
                    return _cfg.Dark
                        ? T("Beralih ke Mode Terang (Light Mode)", "Switch to Light Mode")
                        : T("Beralih ke Mode Gelap (Dark Mode)", "Switch to Dark Mode");

                case "BTN_TRAY":
                    return T("Sembunyikan jendela ke System Tray di pojok kanan bawah taskbar", "Minimize window to system tray");

                case "ZOOM_IN":
                    return T("Perbesar Skala Tampilan UI (Zoom In • Ctrl+Scroll)", "Increase UI scale (Zoom In • Ctrl+Scroll)");

                case "ZOOM_OUT":
                    return T("Perkecil Skala Tampilan UI (Zoom Out • Ctrl+Scroll)", "Decrease UI scale (Zoom Out • Ctrl+Scroll)");

                case "ZOOM_RESET":
                    return T("Kembalikan Skala Tampilan ke Ukuran Standar (100%)", "Reset UI scale to default (100%)");

                case "DROP_PROFILE":
                    return T("Pilih profil konfigurasi game aktif", "Select active game profile configuration");

                case "DROP_SRC":
                    return T("Pilih bahasa sumber teks di dalam game", "Select game source language");

                case "DROP_TGT":
                    return T("Pilih bahasa hasil terjemahan", "Select translation target language");

                case "DROP_ENGINE":
                    return T("Pilih mesin penerjemah: Google GTX (gratis/cepat), Gemini AI (sangat cerdas), MyMemory", "Select translation engine: Google GTX (free/fast), Gemini AI (smart), MyMemory");

                case "DROP_FONT":
                    return T("Pilih jenis huruf (Font Family) untuk teks terjemahan overlay", "Select font family for overlay translated text");

                case "WIN_MIN":
                    return T("Minimalkan Jendela (Minimize)", "Minimize Window");

                case "WIN_FULL":
                    return _isFullscreen
                        ? T("Keluar dari Layar Penuh", "Exit Fullscreen")
                        : T("Mode Layar Penuh Tanpa Batas (Fullscreen)", "Borderless Fullscreen Mode");

                case "WIN_MAX":
                    return _isMaximized
                        ? T("Pulihkan Ukuran Jendela", "Restore Window Size")
                        : T("Maksimalkan Jendela (Maximize)", "Maximize Window");

                case "WIN_CLOSE":
                    return T("Tutup Aplikasi (Alt+F4)", "Close Application (Alt+F4)");

                case "SNIP_OCR":
                    return T("Tentukan area kotak OCR di layar (area teks asli game) (Alt+Q)", "Select OCR capture box on screen (Alt+Q)");

                case "SNIP_RES":
                    return T("Tentukan posisi kotak teks hasil terjemahan di layar (Alt+A)", "Select translated result box position on screen (Alt+A)");

                case "SNIP_MERGED_RES":
                    return T("Tentukan posisi kotak gabungan untuk seluruh area", "Select global merged result box position");

                case "AREA_SNAP":
                    return T("Ratakan posisi kotak hasil terjemahan persis menutupi kotak OCR", "Snap result box position to match OCR capture box");

                case "AREA_DEL":
                    return T("Hapus area tangkapan ini", "Delete this capture area");

                case "AREA_STYLE":
                    return T("Atur gaya font & warna khusus untuk area ini", "Configure custom style for this area");

                case "AREA_TOG":
                    return T("Aktifkan atau nonaktifkan pemindaian area ini", "Enable or disable scanning for this area");

                case "PICK_TEXT":
                    return T("Pilih warna huruf teks terjemahan", "Pick translated text font color");

                case "PICK_BG":
                    return T("Pilih warna kotak latar belakang overlay", "Pick overlay background box color");

                case "PICK_OUT":
                    return T("Pilih warna garis tepi (outline) teks", "Pick text outline border color");

                case "PICK_SPEAKER":
                    return T("Pilih warna badge nama aktor/pembicara", "Pick speaker/actor badge color");

                case "SL_SIZE":
                    return T("Geser untuk mengatur ukuran huruf teks terjemahan", "Drag to adjust font size");

                case "SL_OPAC":
                    return T("Geser untuk mengatur tingkat transparansi kotak overlay", "Drag to adjust overlay background opacity");

                case "SL_PAD":
                    return T("Geser untuk mengatur jarak bantalan (padding) kotak", "Drag to adjust box padding");

                case "SL_CORNER":
                    return T("Geser untuk mengatur kelengkungan sudut kotak (radius)", "Drag to adjust corner radius");

                case "SL_OUTLINE":
                    return T("Geser untuk mengatur ketebalan garis tepi teks", "Drag to adjust text outline thickness");

                case "SL_INTERVAL":
                    return T("Geser untuk mengatur interval pemindaian layar (kecepatan deteksi)", "Drag to adjust screen scanning interval");

                case "SL_AUTOHIDE":
                    return T("Geser untuk mengatur waktu otomatis menyembunyikan overlay saat tidak ada teks", "Drag to adjust idle timeout before auto-hiding overlay");

                case "SYS_ENHANCE_OCR":
                    return T("Perjelas Huruf OCR Tanpa Jeda: isolasi outline gelap teks tanpa kotak background agar tidak ngawur", "Zero-Delay Borderless OCR Enhancer: isolates dark-outlined subtitle glyphs from 3D scenes");

                case "SYS_INSTANT_SUB":
                    return T("Mode Subtitle Langsung & Anti-Ganti Arti: terjemah langsung tanpa jeda ketik & kunci arti agar tidak berubah-ubah", "Instant Subtitle + Anti-Flip-Flop Lock: translates immediately and prevents meaning jitter");

                case "BTN_GAME_LORE":
                    return T("Pilih game sebelum main & cari lore + glosarium nama karakter otomatis", "Pick a game and auto-fetch lore & character glossary");

                case "BTN_LOCK_WINDOW":
                    return T("Kunci OCR hanya ke jendela game/video tertentu agar saat Alt+Tab ke aplikasi lain OCR otomatis jeda", "Lock OCR to a specific game/app window so Alt+Tab pauses OCR automatically");

                case "BTN_HISTORY":
                    return T("Buka riwayat dialog yang sudah diterjemahkan di sesi ini", "Open session dialog translation history");

                case "BTN_CYCLE_PATTERN":
                    return T("Ganti tekstur latar belakang aplikasi (Arsiran Diagonal, Grid Blueprint, Dot Matrix, Polos)", "Cycle app background texture (Diagonal Hatch, Blueprint Grid, Dot Matrix, Solid)");

                case "SYS_NATURAL":
                    return T("Gunakan gaya bahasa santai sehari-hari yang luwes & natural (hindari kata kaku/robot)", "Use natural polite everyday gaming tone (avoids stiff robotic translations)");

                case "SYS_SPEAKER":
                    return T("Deteksi & pisahkan nama pembicara secara otomatis (misal: [Saria])", "Detect & separate speaker name tags automatically");

                case "SYS_MERGE":
                    return T("Gabungkan teks multi-baris menjadi satu kalimat utuh", "Merge multi-line text into a single cohesive sentence");

                case "SYS_AUTOLIVE":
                    return T("Langsung mulai penerjemahan live otomatis saat aplikasi dibuka", "Automatically start live translation when app launches");

                case "SYS_HIDEEMPTY":
                    return T("Sembunyikan kotak overlay jika tidak ada teks yang terdeteksi", "Hide overlay box when no text is detected");

                case "SYS_STABLE":
                    return T("Penstabil OCR: Cegah teks berkedip akibat perbedaan 1-2 huruf OCR", "OCR Stabilizer: Prevent flickering caused by minor OCR jitter");

                case "SYS_TRAY":
                    return T("Minimalkan ke System Tray saat tombol minimize ditekan", "Minimize to system tray when minimize button is clicked");

                case "SYS_AUTO_UPDATE":
                    return T("Otomatis periksa pembaruan rilis baru dari GitHub setiap kali aplikasi dibuka", "Automatically check for new releases on GitHub when app starts");

                case "BANNER_UPDATE":
                    return T("Buka tab Pembaruan untuk melihat catatan rilis dan mengunduh versi terbaru", "Open Changelog tab to see latest release details");

                case "BANNER_DISMISS":
                    return T("Tutup pemberitahuan pembaruan ini", "Dismiss this update notification");

                case "BTN_CHECK_UPDATE":
                    return T("Periksa versi terbaru LingoLens dari GitHub sekarang", "Check for newer LingoLens versions on GitHub now");

                case "BTN_DOWNLOAD_UPDATE":
                    return T("Unduh file pembaruan terbaru LingoLens dari GitHub", "Download latest LingoLens release file from GitHub");

                case "BTN_OPEN_RELEASE":
                    return T("Buka halaman rilis resmi LingoLens di browser", "Open official LingoLens GitHub releases page in browser");

                case "TOG_BOLD":
                    return T("Tebalkan huruf teks terjemahan", "Toggle bold text styling");

                case "TOG_SHADOW":
                    return T("Beri bayangan lembut di belakang teks agar mudah dibaca di game terang", "Toggle drop shadow behind text for readability on bright backgrounds");

                case "TOG_AUTOFIT":
                    return T("Sesuaikan ukuran kotak otomatis mengikuti panjang kalimat", "Auto-fit box size to match sentence length");

                case "TOG_AREA_CUSTOM_STYLE":
                    return T("Gunakan gaya font dan warna khusus yang berbeda untuk area ini saja", "Use unique custom font & colors for this area only");
            }

            return null;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!string.IsNullOrEmpty(_activeSlider)) { _activeSlider = ""; _cfg.Save(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control) { AdjustZoom(e.Delta > 0 ? 10 : -10); return; }
            if (!string.IsNullOrEmpty(_openDrop)) { _dropScroll += (e.Delta > 0) ? -2 : 2; Invalidate(); return; }
            if (_maxScrollY > 0f)
            {
                _scrollY = Math.Max(0f, Math.Min(_maxScrollY, _scrollY - (e.Delta > 0 ? S(44f) : -S(44f))));
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control)
            {
                if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) { AdjustZoom(10); e.Handled = true; return; }
                if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) { AdjustZoom(-10); e.Handled = true; return; }
                if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) { _cfg.UiZoom = 100; _cfg.Save(); ShowToast("Zoom 100%", Color.FromArgb(16, 185, 129)); Invalidate(); e.Handled = true; return; }
            }
            if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape)
            {
                if (!string.IsNullOrEmpty(_openDrop)) { _openDrop = ""; Invalidate(); }
                else if (_isFullscreen) ToggleFullscreen();
            }
        }

        private void UpdateSlider(string id, float mx)
        {
            OverlayStyle st = ActiveStyle;
            foreach (Hit h in _hits)
            {
                if (h.Id == id && h.Kind == HitKind.Slider)
                {
                    float t = Math.Max(0f, Math.Min(1f, (mx - h.Box.X) / Math.Max(1f, h.Box.Width)));
                    int v = h.Min + (int)Math.Round(t * (h.Max - h.Min));
                    if (id == "SL_SIZE") { st.FontSize = v; st.UseCustom = (_styleTargetAreaId > 0); }
                    else if (id == "SL_OPAC") { st.BgOpacity = v; st.UseCustom = (_styleTargetAreaId > 0); }
                    else if (id == "SL_OUTLINE") { st.Outline = v; st.UseCustom = (_styleTargetAreaId > 0); }
                    else if (id == "SL_CORNER") { st.Corner = v; st.UseCustom = (_styleTargetAreaId > 0); }
                    else if (id == "SL_PAD") { st.Padding = v; st.UseCustom = (_styleTargetAreaId > 0); }
                    else if (id == "SL_INTERVAL") _cfg.Interval = Math.Max(60, ((v + 10) / 20) * 20);
                    else if (id == "SL_AUTOHIDE") _cfg.AutoHideSeconds = v;
                    _overlay.UpdateTexts();
                    Invalidate();
                    return;
                }
            }
        }

        private void ApplyDrop(string id, int idx)
        {
            if (id == "DROP_SRC" && idx >= 0 && idx < SrcLangs.GetLength(0)) _cfg.Src = SrcLangs[idx, 0];
            else if (id == "DROP_TGT" && idx >= 0 && idx < TgtLangs.GetLength(0)) _cfg.Tgt = TgtLangs[idx, 0];
            else if (id == "DROP_FONT" && idx >= 0 && idx < _fonts.Count)
            {
                ActiveStyle.Font = _fonts[idx];
                if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true;
                _overlay.UpdateTexts();
            }
            else if (id == "DROP_PROFILE")
            {
                List<string> profs = GetDropItems("DROP_PROFILE");
                if (idx >= 0 && idx < profs.Count)
                {
                    string name = profs[idx];
                    if (name == "Default") _cfg.ActiveProfile = "Default";
                    else if (_cfg.LoadGameProfile(name))
                    {
                        _overlay.Rebuild();
                        _engine.ClearState();
                        _engine.ForceScanNow();
                        ShowToast("Template \"" + name + "\" dimuat", Color.FromArgb(45, 168, 122));
                    }
                }
            }
            _cfg.Save();
            _engine.ForceScanNow();
        }

        private void PickColor(int which)
        {
            OverlayStyle st = ActiveStyle;
            Color cur = which == 0 ? st.TextColor : (which == 1 ? st.BgColor : (which == 2 ? st.OutlineColor : st.SpeakerColor));
            using (ColorDialog cd = new ColorDialog { Color = cur, FullOpen = true })
            {
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    if (which == 0) st.TextColor = cd.Color;
                    else if (which == 1) st.BgColor = cd.Color;
                    else if (which == 2) st.OutlineColor = cd.Color;
                    else st.SpeakerColor = cd.Color;
                    if (_styleTargetAreaId > 0) st.UseCustom = true;
                    _cfg.Save();
                    _overlay.UpdateTexts();
                    Invalidate();
                }
            }
        }

        private void HandleAction(string id, int idx)
        {
            if (id.StartsWith("TAB_")) { _tab = idx; _scrollY = 0f; Invalidate(); return; }
            if (id.StartsWith("PLACE_"))
            {
                _cfg.Placement = int.Parse(id.Substring(6));
                _cfg.Save(); _overlay.Rebuild(); Invalidate(); return;
            }
            if (id.StartsWith("UILANG_"))
            {
                _cfg.UiLang = id.Substring(7);
                _cfg.Save();
                Invalidate();
                return;
            }
            if (id.StartsWith("TONE_"))
            {
                _cfg.ToneMode = Math.Max(0, Math.Min(2, int.Parse(id.Substring(5))));
                _cfg.NaturalTone = (_cfg.ToneMode != 2);
                _cfg.Save();
                _engine.ClearState();
                _engine.ForceScanNow();
                string tn = _cfg.ToneMode == 0 ? "Gaul (Gue/Lo)" : (_cfg.ToneMode == 1 ? "Santai (Aku/Kamu)" : "Baku / Formal");
                ShowToast("Gaya bahasa: " + tn, Palette.For(_cfg.Dark).Live);
                Invalidate();
                return;
            }
            if (id.StartsWith("PAT_"))
            {
                _cfg.BgPattern = Math.Max(0, Math.Min(3, int.Parse(id.Substring(4))));
                _cfg.Save();
                Invalidate();
                return;
            }
            if (id.StartsWith("ENG_"))
            {
                string nextEng = id.Substring(4);
                if (nextEng == "gemini" && string.IsNullOrWhiteSpace(_cfg.GeminiApiKey))
                {
                    PromptGeminiKey();
                    return;
                }
                _cfg.Engine = nextEng;
                _cfg.Save(); _engine.ForceScanNow(); Invalidate(); return;
            }
            if (id.StartsWith("PRESET_"))
            {
                AppConfig.ApplyPresetTo(ActiveStyle, idx);
                if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true;
                _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); return;
            }
            if (id.StartsWith("ALIGN_"))
            {
                ActiveStyle.Align = int.Parse(id.Substring(6));
                if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true;
                _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); return;
            }
            if (id.StartsWith("SWATCH_"))
            {
                ActiveStyle.TextColor = QuickTextColors[idx];
                if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true;
                _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); return;
            }

            switch (id)
            {
                case "WIN_MIN": WindowState = FormWindowState.Minimized; break;
                case "WIN_MAX": ToggleMaximize(); break;
                case "WIN_FULL": ToggleFullscreen(); break;
                case "WIN_CLOSE": Close(); break;
                case "BTN_TRAY": HideToTray(); break;
                case "BTN_THEME": _cfg.Dark = !_cfg.Dark; _cfg.Save(); ApplyDwm(); Invalidate(); break;

                case "BTN_SAVE_PROFILE": PromptSaveGameProfile(); break;
                case "BTN_LOCK_WINDOW": PromptWindowLockPicker(); break;
                case "BTN_GAME_LORE": PromptGameLorePicker(); break;
                case "BTN_HISTORY": PromptDialogBacklog(); break;
                case "BTN_CYCLE_PATTERN":
                    _cfg.BgPattern = (_cfg.BgPattern + 1) % 4;
                    _cfg.Save();
                    string[] pNames = new string[] { "Arsiran Diagonal", "Grid Blueprint", "Dot Matrix", "Polos" };
                    ShowToast("Latar: " + pNames[_cfg.BgPattern], Palette.For(_cfg.Dark).Live);
                    Invalidate();
                    break;
                case "BTN_GEMINI_KEY": PromptGeminiKey(); break;
                case "BTN_GLOSSARY": PromptGlossary(); break;

                case "ZOOM_IN": AdjustZoom(10); break;
                case "ZOOM_OUT": AdjustZoom(-10); break;
                case "ZOOM_RESET": _cfg.UiZoom = 100; _cfg.Save(); ShowToast("Zoom 100%", Color.FromArgb(16, 185, 129)); Invalidate(); break;

                case "BTN_LIVE": ToggleLive(); break;
                case "BTN_ADD_AREA": BeginDesktopSnip(-1, SnipTargetKind.Ocr); break;
                case "BTN_EDIT_SCREEN": ToggleEditOnScreen(); break;
                case "BTN_SCAN": TriggerManualScan(); break;
                case "BTN_PREVIEW_LIVE":
                    _overlay.ShowPreviewSample();
                    ShowToast("Preview tampil di desktop", Color.FromArgb(16, 185, 129));
                    break;

                case "SNIP_OCR": BeginDesktopSnip(idx, SnipTargetKind.Ocr); break;
                case "SNIP_RES": BeginDesktopSnip(idx, SnipTargetKind.Result); break;
                case "SNIP_MERGED_RES": BeginDesktopSnip(0, SnipTargetKind.Result); break;

                case "AREA_STYLE":
                    _styleTargetAreaId = idx;
                    _tab = 1;
                    _scrollY = 0f;
                    Invalidate();
                    break;
                case "ST_TARGET":
                    _styleTargetAreaId = idx;
                    Invalidate();
                    break;
                case "TOG_AREA_CUSTOM_STYLE":
                    if (_styleTargetAreaId > 0)
                    {
                        Area ar = _cfg.FindArea(_styleTargetAreaId);
                        if (ar != null)
                        {
                            ar.Style.UseCustom = !ar.Style.UseCustom;
                            _cfg.Save(); _overlay.UpdateTexts(); Invalidate();
                        }
                    }
                    break;

                case "AREA_MODE_DLG":
                    lock (_cfg.Sync) { Area a = _cfg.FindArea(idx); if (a != null) a.Mode = AreaTextMode.Dialog; }
                    _cfg.Save(); _engine.ClearAreaState(idx); _engine.ForceScanNow();
                    ShowToast("Mode Dialog (gabung kalimat)", Color.FromArgb(16, 185, 129));
                    break;
                case "AREA_MODE_CHC":
                    lock (_cfg.Sync) { Area a = _cfg.FindArea(idx); if (a != null) a.Mode = AreaTextMode.Choice; }
                    _cfg.Save(); _engine.ClearAreaState(idx); _engine.ForceScanNow();
                    ShowToast("Mode Pilihan (terjemah pisah per baris)", Color.FromArgb(16, 185, 129));
                    break;

                case "BTN_SWAP":
                    if (_cfg.Src != "auto")
                    {
                        string tmp = _cfg.Src; _cfg.Src = _cfg.Tgt; _cfg.Tgt = tmp;
                        _cfg.Save(); _engine.ForceScanNow(); Invalidate();
                    }
                    break;

                case "AREA_TOG":
                    lock (_cfg.Sync) { Area a = _cfg.FindArea(idx); if (a != null) a.Enabled = !a.Enabled; }
                    _cfg.Save(); _overlay.Rebuild(); _engine.ForceScanNow(); Invalidate();
                    break;
                case "AREA_SNAP":
                    lock (_cfg.Sync) { Area a = _cfg.FindArea(idx); if (a != null) a.Result = AppConfig.DefaultResultFor(a.Ocr); }
                    _cfg.Save(); _overlay.Rebuild(); ShowToast("Kotak hasil disejajarkan dengan OCR", Color.FromArgb(16, 185, 129));
                    break;
                case "AREA_DEL":
                    lock (_cfg.Sync)
                    {
                        if (_cfg.Areas.Count > 1)
                        {
                            Area a = _cfg.FindArea(idx);
                            if (a != null) { _cfg.Areas.Remove(a); _engine.ClearAreaState(idx); }
                            if (_selectedAreaId == idx && _cfg.Areas.Count > 0) _selectedAreaId = _cfg.Areas[0].Id;
                            if (_styleTargetAreaId == idx) _styleTargetAreaId = 0;
                        }
                    }
                    _cfg.Save(); _overlay.Rebuild(); Invalidate();
                    break;

                case "TOG_BOLD": ActiveStyle.Bold = !ActiveStyle.Bold; if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true; _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); break;
                case "TOG_SHADOW": ActiveStyle.Shadow = !ActiveStyle.Shadow; if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true; _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); break;
                case "TOG_AUTOFIT": ActiveStyle.AutoFit = !ActiveStyle.AutoFit; if (_styleTargetAreaId > 0) ActiveStyle.UseCustom = true; _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); break;

                case "PICK_TEXT": PickColor(0); break;
                case "PICK_BG": PickColor(1); break;
                case "PICK_OUT": PickColor(2); break;
                case "PICK_SPEAKER": PickColor(3); break;

                case "SYS_ENHANCE_OCR": _cfg.EnhanceGameOcr = !_cfg.EnhanceGameOcr; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_INSTANT_SUB": _cfg.InstantSubtitleMode = !_cfg.InstantSubtitleMode; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_NATURAL": _cfg.NaturalTone = !_cfg.NaturalTone; _cfg.ToneMode = _cfg.NaturalTone ? 0 : 2; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_SPEAKER": _cfg.DetectSpeaker = !_cfg.DetectSpeaker; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_STABLE": _cfg.WaitStable = !_cfg.WaitStable; _cfg.Save(); Invalidate(); break;
                case "SYS_MERGE": _cfg.MergeLines = !_cfg.MergeLines; _cfg.Save(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_HIDEEMPTY": _cfg.HideEmpty = !_cfg.HideEmpty; _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); break;
                case "SYS_ALLOW_CAP":
                    _cfg.AllowOverlayCapture = !_cfg.AllowOverlayCapture;
                    _cfg.Save();
                    _overlay.Rebuild();
                    if (_cfg.AllowOverlayCapture && _cfg.Placement == 1)
                        ShowToast(T("Aktif (gunakan mode Terpisah/Gabung agar tidak menimpa OCR)", "Enabled (use Separate/Merged mode so OCR is not covered)"), Color.FromArgb(217, 119, 6));
                    else
                        ShowToast(_cfg.AllowOverlayCapture
                            ? T("Overlay hasil kini bisa di-screenshot / direkam", "Result overlay can now be screenshotted / recorded")
                            : T("Overlay hasil disembunyikan dari screenshot", "Result overlay hidden from screenshots"), Color.FromArgb(16, 185, 129));
                    Invalidate();
                    break;
                case "SYS_AUTOLIVE": _cfg.AutoLive = !_cfg.AutoLive; _cfg.Save(); Invalidate(); break;
                case "SYS_TRAY": _cfg.CloseToTray = !_cfg.CloseToTray; _cfg.Save(); Invalidate(); break;
                case "SYS_AUTO_UPDATE": _cfg.AutoCheckUpdate = !_cfg.AutoCheckUpdate; _cfg.Save(); Invalidate(); break;

                case "BANNER_UPDATE": _tab = 3; _scrollY = 0f; Invalidate(); break;
                case "BANNER_DISMISS": _dismissUpdateBanner = true; Invalidate(); break;
                case "BTN_CHECK_UPDATE": UpdateChecker.CheckAsync(silent: false); Invalidate(); break;
                case "BTN_DOWNLOAD_UPDATE": UpdateChecker.OpenDownloadUrl(); break;
                case "BTN_OPEN_RELEASE": UpdateChecker.OpenReleaseUrl(); break;

                case "BTN_COPY":
                    if (!string.IsNullOrEmpty(_liveResult))
                    {
                        try { Clipboard.SetText(_liveResult); _copiedFlash = true; _copyTimer.Start(); Invalidate(); }
                        catch { }
                    }
                    break;
            }
        }
    }
}
