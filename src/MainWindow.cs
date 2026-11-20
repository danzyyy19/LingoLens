using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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
        private Color _toastColor = Color.FromArgb(217, 119, 87);

        private readonly Timer _toastTimer;
        private readonly Timer _copyTimer;
        private readonly NotifyIcon _tray;

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

        private float Z { get { return Math.Max(0.75f, Math.Min(1.50f, _cfg.UiZoom / 100f)); } }
        private float S(float v) { return (float)Math.Round(v * Z); }
        private RectangleF SR(float x, float y, float w, float h)
        {
            return new RectangleF((float)Math.Round(x), (float)Math.Round(y), (float)Math.Round(w), (float)Math.Round(h));
        }

        public MainWindow(AppConfig cfg)
        {
            _cfg = cfg;
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
            Size = new Size(1080, 740);
            MinimumSize = new Size(800, 580);
            DoubleBuffered = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
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

            _toastTimer = new Timer { Interval = 2400 };
            _toastTimer.Tick += delegate { _toastText = ""; _toastTimer.Stop(); Invalidate(); };

            _copyTimer = new Timer { Interval = 1400 };
            _copyTimer.Tick += delegate { _copiedFlash = false; _copyTimer.Stop(); Invalidate(); };

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
            Native.RegisterHotKey(Handle, 1, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.E);
            Native.RegisterHotKey(Handle, 2, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.Q);
            Native.RegisterHotKey(Handle, 3, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.W);
            Native.RegisterHotKey(Handle, 4, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.R);
            Native.RegisterHotKey(Handle, 5, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.H);
            Native.RegisterHotKey(Handle, 6, Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.A);
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
                    ShowToast(_overlay.UserHidden ? "Overlay disembunyikan" : "Overlay ditampilkan", Color.FromArgb(217, 119, 87));
                }
                else if (id == 6)
                {
                    int targetId = _cfg.Placement == 2 ? 0 : _selectedAreaId;
                    BeginDesktopSnip(targetId, SnipTargetKind.Result);
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

            if (e.ClearedByIdle)
            {
                _liveStatus = "Idle · overlay disembunyikan";
            }
            else
            {
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
            int next = Math.Max(75, Math.Min(150, _cfg.UiZoom + delta));
            if (next != _cfg.UiZoom)
            {
                _cfg.UiZoom = next;
                _cfg.Save();
                _scrollY = 0f;
                ShowToast("Zoom " + next + "%", Color.FromArgb(217, 119, 87));
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
            else { _engine.ForceScanNow(); ShowToast("Memindai ulang layar...", Color.FromArgb(217, 119, 87)); }
        }

        private void ToggleEditOnScreen()
        {
            bool next = !_overlay.EditMode;
            _engine.SetPausedForEdit(next);
            _overlay.SetEditMode(next);
            if (next) ShowToast("Geser & ubah ukuran di layar desktop, tekan ESC selesai", Color.FromArgb(217, 119, 87));
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
            using (Form dlg = new Form())
            {
                dlg.Text = "Simpan Template Game";
                dlg.Size = new Size(400, 175);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = _cfg.Dark ? Color.FromArgb(33, 32, 30) : Color.FromArgb(250, 249, 245);
                dlg.ForeColor = _cfg.Dark ? Color.FromArgb(238, 236, 230) : Color.FromArgb(31, 30, 28);

                Label lbl = new Label { Text = "Nama Game (mis. Zelda Ocarina of Time):", Location = new Point(18, 16), AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
                TextBox tb = new TextBox
                {
                    Text = _cfg.ActiveProfile == "Default" ? "" : _cfg.ActiveProfile,
                    Location = new Point(18, 42),
                    Size = new Size(346, 26),
                    Font = new Font("Segoe UI", 10f)
                };
                Button okBtn = new Button
                {
                    Text = "Simpan Template",
                    DialogResult = DialogResult.OK,
                    Location = new Point(234, 86),
                    Size = new Size(130, 34),
                    BackColor = Color.FromArgb(217, 119, 87),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                okBtn.FlatAppearance.BorderSize = 0;

                dlg.Controls.Add(lbl); dlg.Controls.Add(tb); dlg.Controls.Add(okBtn);
                dlg.AcceptButton = okBtn;

                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(tb.Text))
                {
                    _cfg.SaveGameProfile(tb.Text.Trim());
                    ShowToast("Template \"" + _cfg.ActiveProfile + "\" tersimpan", Color.FromArgb(45, 168, 122));
                    Invalidate();
                }
            }
        }

        private void PromptGeminiKey()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Gemini AI API Key (Opsional)";
                dlg.Size = new Size(440, 175);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = _cfg.Dark ? Color.FromArgb(33, 32, 30) : Color.FromArgb(250, 249, 245);
                dlg.ForeColor = _cfg.Dark ? Color.FromArgb(238, 236, 230) : Color.FromArgb(31, 30, 28);

                Label lbl = new Label { Text = "Masukkan Google Gemini API Key (untuk terjemahan AI kontekstual):", Location = new Point(18, 16), AutoSize = true, Font = new Font("Segoe UI", 9f) };
                TextBox tb = new TextBox { Text = _cfg.GeminiApiKey, Location = new Point(18, 42), Size = new Size(386, 26), Font = new Font("Segoe UI", 9.5f) };
                Button okBtn = new Button
                {
                    Text = "Simpan Key",
                    DialogResult = DialogResult.OK,
                    Location = new Point(284, 86),
                    Size = new Size(120, 34),
                    BackColor = Color.FromArgb(217, 119, 87),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                okBtn.FlatAppearance.BorderSize = 0;
                dlg.Controls.Add(lbl); dlg.Controls.Add(tb); dlg.Controls.Add(okBtn);
                dlg.AcceptButton = okBtn;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _cfg.GeminiApiKey = tb.Text.Trim();
                    if (!string.IsNullOrEmpty(_cfg.GeminiApiKey)) _cfg.Engine = "gemini";
                    _cfg.Save();
                    _engine.ForceScanNow();
                    ShowToast("Gemini API Key disimpan", Color.FromArgb(45, 168, 122));
                    Invalidate();
                }
            }
        }

        private void PromptGlossary()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Glosarium & Kamus Nama Karakter";
                dlg.Size = new Size(460, 310);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = _cfg.Dark ? Color.FromArgb(33, 32, 30) : Color.FromArgb(250, 249, 245);
                dlg.ForeColor = _cfg.Dark ? Color.FromArgb(238, 236, 230) : Color.FromArgb(31, 30, 28);

                Label lbl = new Label
                {
                    Text = "Masukkan nama karakter, lokasi, atau istilah khusus (1 per baris atau pisah ';').\nGunakan '=' untuk terjemahan khusus (contoh: Master Sword=Pedang Master):",
                    Location = new Point(18, 14),
                    Size = new Size(410, 36),
                    Font = new Font("Segoe UI", 8.8f)
                };
                TextBox tb = new TextBox
                {
                    Text = (_cfg.Glossary ?? "").Replace(";", "\r\n"),
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Location = new Point(18, 56),
                    Size = new Size(406, 154),
                    Font = new Font("Segoe UI", 9.5f)
                };
                Button okBtn = new Button
                {
                    Text = "Simpan Glosarium",
                    DialogResult = DialogResult.OK,
                    Location = new Point(274, 222),
                    Size = new Size(150, 36),
                    BackColor = Color.FromArgb(217, 119, 87),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };
                okBtn.FlatAppearance.BorderSize = 0;
                dlg.Controls.Add(lbl); dlg.Controls.Add(tb); dlg.Controls.Add(okBtn);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _cfg.Glossary = tb.Text.Replace("\r\n", ";").Replace("\n", ";").Trim(';', ' ');
                    _cfg.Save();
                    _engine.ClearState();
                    _engine.ForceScanNow();
                    ShowToast("Glosarium game disimpan", Color.FromArgb(45, 168, 122));
                    Invalidate();
                }
            }
        }

        // =========================================================================
        // RENDERING
        // =========================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Default;

            float cw = ClientSize.Width;
            float ch = ClientSize.Height;
            if (cw < 100 || ch < 100) return;

            Palette pal = Palette.For(_cfg.Dark);
            _hits.Clear();

            using (SolidBrush bg = new SolidBrush(pal.Bg)) g.FillRectangle(bg, 0, 0, cw, ch);

            using (Font fSerifLg = Theme.SerifFont(S(24f), false))
            using (Font fSerifMd = Theme.SerifFont(S(16f), false))
            using (Font fSec = Theme.UiFont(S(13.5f), true))
            using (Font fBody = Theme.UiFont(S(13f), false))
            using (Font fBodyB = Theme.UiFont(S(13f), true))
            using (Font fSm = Theme.UiFont(S(12f), false))
            using (Font fSmB = Theme.UiFont(S(12f), true))
            using (Font fCap = Theme.UiFont(S(11f), true))
            {
                float sideW = S(224f);
                float topH = S(44f);

                // 1. LEFT CLAUDE SIDEBAR
                using (SolidBrush sb = new SolidBrush(pal.Sidebar))
                using (Pen bp = new Pen(pal.Border, 1f))
                {
                    g.FillRectangle(sb, 0, 0, sideW, ch);
                    g.DrawLine(bp, sideW, 0, sideW, ch);
                }

                RectangleF logo = SR(S(18f), S(16f), S(28f), S(28f));
                Theme.FillGradRR(g, logo, S(8f), Color.FromArgb(232, 130, 94), Color.FromArgb(182, 78, 46), 135f);
                Theme.DrawIcon(g, IconKind.Sparkle, SR(logo.X + S(5f), logo.Y + S(5f), S(18f), S(18f)), Color.White);
                Theme.DrawTextLeft(g, "LingoLens", fSerifMd, pal.Text, SR(S(54f), S(14f), sideW - S(60f), S(32f)));

                // Hero Live CTA
                bool live = _engine.IsRunning;
                RectangleF liveBtn = SR(S(16f), S(64f), sideW - S(32f), S(40f));
                DrawAccentBtn(g, pal, "BTN_LIVE", liveBtn,
                    live ? "Hentikan Live" : "Mulai Live",
                    live ? IconKind.Stop : IconKind.Play,
                    live ? pal.Danger : pal.Accent,
                    live ? Color.FromArgb(196, 65, 65) : pal.AccentHover,
                    fBodyB, S(10f));

                using (SolidBrush stDot = new SolidBrush(live ? pal.Live : pal.TextMute))
                    g.FillEllipse(stDot, S(22f), S(117f), S(7f), S(7f));
                string statusTxt = live ? ("Aktif" + (_lastMs > 0 ? (" · " + _lastMs + " ms") : "")) : "Standby · Alt+E";
                Theme.DrawTextLeft(g, statusTxt, fSm, live ? pal.Live : pal.TextMute, SR(S(35f), S(111f), sideW - S(48f), S(18f)));

                // Navigation (4 items including Pembaruan)
                string[] navNames = new string[] { "Live & Area", "Tampilan", "Sistem", "Pembaruan" };
                IconKind[] navIcons = new IconKind[] { IconKind.Layers, IconKind.Palette, IconKind.Sliders, IconKind.History };
                float ny = S(150f);
                for (int i = 0; i < 4; i++)
                {
                    RectangleF nr = SR(S(12f), ny + i * S(40f), sideW - S(24f), S(36f));
                    bool act = (_tab == i);
                    bool hov = (_hoverId == "TAB_" + i);
                    if (act) Theme.FillRR(g, nr, S(9f), pal.CardHover);
                    else if (hov) Theme.FillRR(g, nr, S(9f), pal.Card);
                    float isz = S(14f);
                    Theme.DrawIcon(g, navIcons[i], SR(nr.X + S(12f), nr.Y + (nr.Height - isz) * 0.5f, isz, isz),
                        act ? pal.Accent : pal.TextSub, 1.65f);
                    Theme.DrawTextLeft(g, navNames[i], act ? fBodyB : fBody, act ? pal.Text : pal.TextSub,
                        SR(nr.X + S(34f), nr.Y, nr.Width - S(40f), nr.Height));
                    _hits.Add(new Hit { Id = "TAB_" + i, Kind = HitKind.Button, Box = nr, Index = i });
                }

                // Sidebar Bottom: Zoom + Theme + Tray + Credit "by Danzyyy"
                float by = ch - S(152f);
                using (Pen bp = new Pen(pal.Border, 1f)) g.DrawLine(bp, S(16f), by - S(10f), sideW - S(16f), by - S(10f));

                RectangleF zoomRow = SR(S(16f), by, sideW - S(32f), S(32f));
                Theme.FillRR(g, zoomRow, S(8f), pal.Well);
                Theme.DrawRR(g, zoomRow, S(8f), pal.Border, 1f);
                DrawZoomBtn(g, pal, "ZOOM_OUT", SR(zoomRow.X + S(2f), zoomRow.Y + S(2f), S(34f), zoomRow.Height - S(4f)), IconKind.ZoomOut);
                DrawZoomBtn(g, pal, "ZOOM_IN", SR(zoomRow.Right - S(36f), zoomRow.Y + S(2f), S(34f), zoomRow.Height - S(4f)), IconKind.ZoomIn);
                RectangleF zReset = SR(zoomRow.X + S(38f), zoomRow.Y + S(2f), zoomRow.Width - S(76f), zoomRow.Height - S(4f));
                Theme.DrawTextCenter(g, "Zoom " + _cfg.UiZoom + "%", fCap, pal.TextSub, zReset);
                _hits.Add(new Hit { Id = "ZOOM_RESET", Kind = HitKind.Button, Box = zReset });

                DrawSidebarBtn(g, pal, "BTN_THEME", SR(S(16f), by + S(38f), sideW - S(32f), S(34f)),
                    _cfg.Dark ? "Mode Terang" : "Mode Gelap", _cfg.Dark ? IconKind.Sun : IconKind.Moon, fSmB);

                DrawSidebarBtn(g, pal, "BTN_TRAY", SR(S(16f), by + S(76f), sideW - S(32f), S(34f)),
                    "Sembunyikan ke Tray", IconKind.Tray, fSm);

                Theme.DrawTextCenter(g, "v2.2  •  by Danzyyy", fCap, pal.TextMute, SR(S(16f), ch - S(28f), sideW - S(32f), S(20f)));

                // 2. TOP WINDOW CHROME BAR
                float wx = cw - S(10f);
                float btnSz = S(34f);
                wx -= btnSz; DrawChromeIcon(g, pal, "WIN_CLOSE", SR(wx, S(6f), btnSz, S(32f)), IconKind.Close, true);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_MAX", SR(wx, S(6f), btnSz, S(32f)), _isMaximized ? IconKind.Restore : IconKind.Maximize, false);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_FULL", SR(wx, S(6f), btnSz, S(32f)), IconKind.Fullscreen, false);
                wx -= (btnSz + S(2f)); DrawChromeIcon(g, pal, "WIN_MIN", SR(wx, S(6f), btnSz, S(32f)), IconKind.Minimize, false);

                // Top-left Game Profile Quick Selector Bar
                RectangleF profDrop = SR(sideW + S(24f), S(6f), S(210f), S(32f));
                DrawProfilePill(g, pal, profDrop, fSmB);
                RectangleF saveProf = SR(profDrop.Right + S(6f), S(6f), S(96f), S(32f));
                DrawOutlineBtn(g, pal, "BTN_SAVE_PROFILE", saveProf, "Simpan", IconKind.Save, fCap, S(8f));

                string modeLabel = AppConfig.IsPortableMode ? "Portable" : "Terinstal";
                Theme.DrawTextLeft(g, modeLabel + (_isFullscreen ? " • Fullscreen" : ""), fCap, pal.TextMute,
                    SR(saveProf.Right + S(14f), S(12f), S(180f), S(20f)));

                // 3. MAIN VIEWPORT
                RectangleF vp = SR(sideW, topH, cw - sideW, ch - topH);
                GraphicsState vpState = g.Save();
                g.SetClip(vp);

                float contentW = Math.Min(S(1120f), vp.Width - S(48f));
                float cx = (float)Math.Round(vp.X + (vp.Width - contentW) * 0.5f);
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
                    Theme.FillRR(g, tr, S(17f), Color.FromArgb(245, 28, 27, 25));
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
            Theme.DrawTextLeft(g, "Area & Terjemahan", fSerifLg, pal.Text, SR(x, cy, S(260f), S(34f)));

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

            DrawAccentBtn(g, pal, "BTN_ADD_AREA", SR(x, cy, S(142f), S(36f)), "Tambah Area", IconKind.Plus, pal.Accent, pal.AccentHover, fSmB, S(9f));
            DrawOutlineBtn(g, pal, "BTN_EDIT_SCREEN", SR(x + S(150f), cy, S(138f), S(36f)),
                _overlay.EditMode ? "Selesai Edit" : "Edit di Layar", IconKind.EditBox, fSmB, S(9f));
            DrawOutlineBtn(g, pal, "BTN_SCAN", SR(x + S(296f), cy, S(92f), S(36f)), "Scan", IconKind.Refresh, fSmB, S(9f));
            DrawOutlineBtn(g, pal, "BTN_PREVIEW_LIVE", SR(x + S(396f), cy, S(104f), S(36f)), "Tes Layar", IconKind.Eye, fSmB, S(9f));

            float pmW = S(248f);
            RectangleF pmBox = SR(x + w - pmW, cy + S(1f), pmW, S(34f));
            Theme.FillRR(g, pmBox, S(9f), pal.Well);
            Theme.DrawRR(g, pmBox, S(9f), pal.Border, 1f);
            string[] pmLbl = new string[] { "Terpisah", "Tutup Asli", "Gabung" };
            float pw = (pmW - S(6f)) / 3f;
            for (int m = 0; m < 3; m++)
                DrawSegItem(g, pal, "PLACE_" + m, SR(pmBox.X + S(3f) + m * pw, pmBox.Y + S(3f), pw, S(28f)), pmLbl[m], _cfg.Placement == m, fCap);

            cy += S(52f);

            if (_cfg.Placement == 2)
            {
                RectangleF mc = SR(x, cy, w, S(66f));
                Theme.DrawCard(g, pal, mc, S(12f));
                Theme.DrawTextLeft(g, "Kotak Hasil Gabungan", fBodyB, pal.Text, SR(mc.X + S(18f), mc.Y + S(11f), S(200f), S(20f)));
                string mDim = _cfg.MergedRect.Width > 0
                    ? ("Lebar " + _cfg.MergedRect.Width + " × Tinggi " + _cfg.MergedRect.Height + " px  •  " + _cfg.MergedRect.X + ", " + _cfg.MergedRect.Y)
                    : "Belum diatur";
                Theme.DrawTextLeft(g, mDim, fSm, pal.TextSub, SR(mc.X + S(18f), mc.Y + S(33f), mc.Width - S(230f), S(20f)));
                DrawAccentBtn(g, pal, "SNIP_MERGED_RES", SR(mc.Right - S(186f), mc.Y + S(15f), S(170f), S(36f)),
                    "Blok Layar Hasil", IconKind.ResultBox, pal.Accent, pal.AccentHover, fSmB, S(9f));
                cy += S(80f);
            }

            List<Area> list;
            lock (_cfg.Sync) list = new List<Area>(_cfg.Areas);

            for (int i = 0; i < list.Count; i++)
            {
                Area a = list[i];
                bool isSeparate = (_cfg.Placement == 0);
                float cardH = isSeparate ? S(132f) : S(90f);
                RectangleF card = SR(x, cy, w, cardH);
                Theme.DrawCard(g, pal, card, S(13f));

                RectangleF swHit = SR(card.X + S(14f), card.Y + S(10f), S(42f), S(28f));
                DrawSwitchScaled(g, pal, swHit, a.Enabled);
                _hits.Add(new Hit { Id = "AREA_TOG", Kind = HitKind.Button, Box = swHit, Index = a.Id });

                Theme.DrawTextLeft(g, "Area " + (i + 1), fSec, a.Enabled ? pal.Text : pal.TextMute,
                    SR(card.X + S(64f), card.Y + S(10f), S(80f), S(28f)));

                // Mode Switch: Dialog | Pilihan
                RectangleF modePill = SR(card.X + S(140f), card.Y + S(10f), S(150f), S(28f));
                Theme.FillRR(g, modePill, S(8f), pal.Well);
                Theme.DrawRR(g, modePill, S(8f), pal.Border, 1f);
                DrawSegItemIdx(g, pal, "AREA_MODE_DLG", SR(modePill.X + S(2f), modePill.Y + S(2f), S(72f), S(24f)),
                    "Dialog", a.Mode == AreaTextMode.Dialog, a.Id, fCap);
                DrawSegItemIdx(g, pal, "AREA_MODE_CHC", SR(modePill.X + S(76f), modePill.Y + S(2f), S(72f), S(24f)),
                    "Pilihan", a.Mode == AreaTextMode.Choice, a.Id, fCap);

                // Per-Area Style Quick Pill ("Gaya: 24px" or "Kustom: 18px") -> Opens Tampilan directly on this Area!
                OverlayStyle eff = a.EffectiveStyle(_cfg);
                string styleTxt = a.Style.UseCustom ? ("Gaya Khusus • " + eff.FontSize + "px") : ("Gaya Global • " + eff.FontSize + "px");
                RectangleF stylePill = SR(card.Right - S(204f), card.Y + S(10f), S(152f), S(28f));
                DrawAreaActionBtn(g, pal, "AREA_STYLE", stylePill, styleTxt, IconKind.Palette, a.Id,
                    a.Style.UseCustom ? pal.Accent : pal.TextSub, fCap);

                DrawMiniIcon(g, pal, "AREA_DEL", SR(card.Right - S(42f), card.Y + S(9f), S(30f), S(30f)), IconKind.Trash, a.Id, true, list.Count > 1);

                // Row 1: OCR Area
                RectangleF ocrRow = SR(card.X + S(14f), card.Y + S(44f), card.Width - S(28f), S(36f));
                Theme.FillRR(g, ocrRow, S(8f), pal.Well);
                using (SolidBrush bBlue = new SolidBrush(Color.FromArgb(86, 152, 255)))
                    g.FillEllipse(bBlue, ocrRow.X + S(12f), ocrRow.Y + S(14f), S(8f), S(8f));
                Theme.DrawTextLeft(g, "Area OCR", fSmB, pal.Text, SR(ocrRow.X + S(28f), ocrRow.Y, S(84f), ocrRow.Height));
                string ocrDim = "Lebar " + a.Ocr.Width + " × Tinggi " + a.Ocr.Height + " px   •   " + a.Ocr.X + ", " + a.Ocr.Y;
                Theme.DrawTextLeft(g, ocrDim, fSm, pal.TextSub, SR(ocrRow.X + S(114f), ocrRow.Y, ocrRow.Width - S(270f), ocrRow.Height));
                DrawAreaActionBtn(g, pal, "SNIP_OCR", SR(ocrRow.Right - S(146f), ocrRow.Y + S(4f), S(142f), S(28f)),
                    "Blok Layar OCR", IconKind.Crop, a.Id, Color.FromArgb(86, 152, 255), fCap);

                // Row 2: Result Box
                if (isSeparate)
                {
                    RectangleF resRow = SR(card.X + S(14f), card.Y + S(86f), card.Width - S(28f), S(36f));
                    Theme.FillRR(g, resRow, S(8f), pal.Well);
                    using (SolidBrush bCor = new SolidBrush(pal.Accent))
                        g.FillEllipse(bCor, resRow.X + S(12f), resRow.Y + S(14f), S(8f), S(8f));
                    Theme.DrawTextLeft(g, "Kotak Hasil", fSmB, pal.Text, SR(resRow.X + S(28f), resRow.Y, S(84f), resRow.Height));
                    string resDim = "Lebar " + a.Result.Width + " × Tinggi " + a.Result.Height + " px   •   " + a.Result.X + ", " + a.Result.Y;
                    Theme.DrawTextLeft(g, resDim, fSm, pal.TextSub, SR(resRow.X + S(114f), resRow.Y, resRow.Width - S(338f), resRow.Height));

                    DrawMiniIcon(g, pal, "AREA_SNAP", SR(resRow.Right - S(182f), resRow.Y + S(4f), S(28f), S(28f)), IconKind.Snap, a.Id, false, true);
                    DrawAreaActionBtn(g, pal, "SNIP_RES", SR(resRow.Right - S(146f), resRow.Y + S(4f), S(142f), S(28f)),
                        "Blok Layar Hasil", IconKind.ResultBox, a.Id, pal.Accent, fCap);
                }

                cy += cardH + S(12f);
            }

            // Live Feed Card
            RectangleF feed = SR(x, cy + S(4f), w, S(110f));
            Theme.DrawCard(g, pal, feed, S(13f));

            float halfW = (feed.Width - S(134f)) * 0.5f;
            RectangleF wSrc = SR(feed.X + S(14f), feed.Y + S(14f), halfW, feed.Height - S(28f));
            Theme.FillRR(g, wSrc, S(9f), pal.Well);
            Theme.DrawTextLeft(g, "OCR", fCap, Color.FromArgb(86, 152, 255), SR(wSrc.X + S(12f), wSrc.Y + S(8f), S(50f), S(16f)));
            Theme.DrawTextRight(g, _liveStatus, fCap, pal.TextMute, SR(wSrc.X + S(64f), wSrc.Y + S(8f), wSrc.Width - S(76f), S(16f)));
            Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveSource) ? "Menunggu teks layar..." : _liveSource,
                fSm, string.IsNullOrEmpty(_liveSource) ? pal.TextMute : pal.TextSub,
                SR(wSrc.X + S(12f), wSrc.Y + S(28f), wSrc.Width - S(24f), S(48f)));

            RectangleF wRes = SR(wSrc.Right + S(10f), feed.Y + S(14f), halfW, feed.Height - S(28f));
            Theme.FillRR(g, wRes, S(9f), pal.Well);
            Theme.DrawTextLeft(g, "Terjemahan", fCap, pal.AccentText, SR(wRes.X + S(12f), wRes.Y + S(8f), S(90f), S(16f)));
            Theme.DrawTextWrap(g, string.IsNullOrEmpty(_liveResult) ? "Hasil terjemahan tampil di sini" : _liveResult,
                fBodyB, string.IsNullOrEmpty(_liveResult) ? pal.TextMute : pal.Text,
                SR(wRes.X + S(12f), wRes.Y + S(28f), wRes.Width - S(24f), S(48f)));

            DrawOutlineBtn(g, pal, "BTN_COPY", SR(wRes.Right + S(10f), feed.Y + S(35f), S(86f), S(38f)),
                _copiedFlash ? "Tersalin" : "Salin", _copiedFlash ? IconKind.Check : IconKind.Copy, fSmB, S(9f));

            cy += S(128f);
            return cy - y;
        }

        // =========================================================================
        // TAB 1: TAMPILAN (Supports Global OR Per-Area Custom Styling!)
        // =========================================================================
        private float RenderTabStyle(Graphics g, Palette pal, float x, float y, float w,
            Font fSerifLg, Font fSec, Font fBody, Font fBodyB, Font fSm, Font fSmB, Font fCap)
        {
            float cy = y;
            Theme.DrawTextLeft(g, "Tampilan Overlay", fSerifLg, pal.Text, SR(x, cy, S(240f), S(34f)));

            // Target Selector: Global | Area 1 | Area 2 ...
            List<Area> areas;
            lock (_cfg.Sync) areas = new List<Area>(_cfg.Areas);

            int targetCount = Math.Min(4, 1 + areas.Count);
            float tPillW = targetCount * S(82f) + S(6f);
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
                "Tes di Layar", IconKind.Eye, pal.Accent, pal.AccentHover, fSmB, S(9f));
            cy += S(48f);

            // If editing specific Area, show toggle "Gunakan Gaya Khusus Area Ini"
            OverlayStyle st = ActiveStyle;
            if (_styleTargetAreaId > 0)
            {
                RectangleF custBar = SR(x, cy, w, S(44f));
                Theme.DrawCard(g, pal, custBar, S(10f));
                Theme.DrawTextLeft(g, "Gunakan tampilan khusus untuk Area ini (berbeda dari Global)", fBodyB, pal.Text,
                    SR(custBar.X + S(16f), custBar.Y, custBar.Width - S(80f), custBar.Height));
                DrawSwitchScaled(g, pal, SR(custBar.Right - S(56f), custBar.Y, S(42f), custBar.Height), st.UseCustom);
                _hits.Add(new Hit { Id = "TOG_AREA_CUSTOM_STYLE", Kind = HitKind.Button, Box = custBar });
                cy += S(54f);
            }

            // Stage Preview
            RectangleF stage = SR(x, cy, w, S(176f));
            Theme.FillGradRR(g, stage, S(14f), Color.FromArgb(24, 22, 20), Color.FromArgb(36, 33, 29), 120f);
            Theme.DrawRR(g, stage, S(14f), pal.BorderStrong, 1f);

            string[] presets = new string[] { "Sinema", "Novel", "Kontras", "Tanpa Kotak" };
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
            string samplePrev = (_styleTargetAreaId > 0 && _cfg.FindArea(_styleTargetAreaId) != null && _cfg.FindArea(_styleTargetAreaId).Mode == AreaTextMode.Choice)
                ? "• Aku belum yakin.\n• Aku ikut!"
                : "Kamu harus cepat. Susul aku nanti!";
            OverlayPane.DrawOverlayText(g, samplePrev, SR(ovBox.X + st.Padding, ovBox.Y + 8f, ovBox.Width - st.Padding * 2f, ovBox.Height - 16f), st);

            cy += S(190f);

            float colGap = S(14f);
            float colW = (w - colGap) * 0.5f;
            float colH = S(326f);

            RectangleF left = SR(x, cy, colW, colH);
            Theme.DrawCard(g, pal, left, S(14f));
            float lx = left.X + S(18f), lw2 = left.Width - S(36f), ly = left.Y + S(18f);

            Theme.DrawTextLeft(g, "Font", fSmB, pal.TextSub, SR(lx, ly, S(84f), S(34f)));
            DrawSelect(g, pal, "DROP_FONT", SR(lx + S(88f), ly, lw2 - S(88f), S(34f)), st.Font, fSmB);
            ly += S(48f);

            DrawSliderRow(g, pal, "SL_SIZE", SR(lx, ly, lw2, S(34f)), "Ukuran", st.FontSize + " px", st.FontSize, 12, 56, fSmB);
            ly += S(46f);

            Theme.DrawTextLeft(g, "Perataan", fSmB, pal.TextSub, SR(lx, ly, S(84f), S(34f)));
            RectangleF alBox = SR(lx + S(88f), ly, lw2 - S(88f), S(34f));
            Theme.FillRR(g, alBox, S(9f), pal.Well);
            Theme.DrawRR(g, alBox, S(9f), pal.Border, 1f);
            string[] aligns = new string[] { "Kiri", "Tengah", "Kanan" };
            float aw = (alBox.Width - S(6f)) / 3f;
            for (int i = 0; i < 3; i++)
                DrawSegItem(g, pal, "ALIGN_" + i, SR(alBox.X + S(3f) + i * aw, alBox.Y + S(3f), aw, S(28f)), aligns[i], st.Align == i, fCap);
            ly += S(48f);

            DrawToggleRow(g, pal, "TOG_BOLD", SR(lx, ly, lw2, S(38f)), "Huruf Tebal", st.Bold, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "TOG_SHADOW", SR(lx, ly, lw2, S(38f)), "Bayangan Teks", st.Shadow, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "TOG_AUTOFIT", SR(lx, ly, lw2, S(38f)), "Auto-Fit Kotak Hasil", st.AutoFit, fBody);

            RectangleF right = SR(x + colW + colGap, cy, colW, colH);
            Theme.DrawCard(g, pal, right, S(14f));
            float rx = right.X + S(18f), rw2 = right.Width - S(36f), ry = right.Y + S(18f);

            DrawSliderRow(g, pal, "SL_OPAC", SR(rx, ry, rw2, S(34f)), "Opasitas", st.BgOpacity + "%", st.BgOpacity, 0, 100, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_OUTLINE", SR(rx, ry, rw2, S(34f)), "Garis Tepi", st.Outline + " px", st.Outline, 0, 6, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_CORNER", SR(rx, ry, rw2, S(34f)), "Lengkungan", st.Corner + " px", st.Corner, 0, 28, fSmB); ry += S(46f);
            DrawSliderRow(g, pal, "SL_PAD", SR(rx, ry, rw2, S(34f)), "Jarak Dalam", st.Padding + " px", st.Padding, 4, 32, fSmB); ry += S(50f);

            Theme.DrawTextLeft(g, "Warna", fSmB, pal.TextSub, SR(rx, ry, S(76f), S(30f)));
            for (int i = 0; i < QuickTextColors.Length; i++)
            {
                RectangleF sw = SR(rx + S(82f) + i * S(34f), ry + S(2f), S(26f), S(26f));
                Theme.FillRR(g, sw, S(13f), QuickTextColors[i]);
                bool sel = (st.TextColor.ToArgb() == QuickTextColors[i].ToArgb());
                Theme.DrawRR(g, sw, S(13f), sel ? pal.Accent : pal.BorderStrong, sel ? 2f : 1f);
                _hits.Add(new Hit { Id = "SWATCH_" + i, Kind = HitKind.Button, Box = sw, Index = i });
            }
            ry += S(42f);

            float c3 = (rw2 - S(14f)) / 3f;
            DrawColorBtn(g, pal, "PICK_TEXT", SR(rx, ry, c3, S(34f)), "Teks", st.TextColor, fCap);
            DrawColorBtn(g, pal, "PICK_BG", SR(rx + c3 + S(7f), ry, c3, S(34f)), "Latar", st.BgColor, fCap);
            DrawColorBtn(g, pal, "PICK_OUT", SR(rx + (c3 + S(7f)) * 2f, ry, c3, S(34f)), "Outline", st.OutlineColor, fCap);

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
            Theme.DrawTextLeft(g, "Sistem & Mesin", fSerifLg, pal.Text, SR(x, cy, S(300f), S(34f)));
            cy += S(48f);

            float colGap = S(14f);
            float colW = (w - colGap) * 0.5f;
            float colH = S(556f);

            RectangleF left = SR(x, cy, colW, colH);
            Theme.DrawCard(g, pal, left, S(14f));
            float lx = left.X + S(18f), lw2 = left.Width - S(36f), ly = left.Y + S(18f);

            DrawSliderRow(g, pal, "SL_INTERVAL", SR(lx, ly, lw2, S(34f)), "Interval Scan", _cfg.Interval + " ms", _cfg.Interval, 100, 1200, fSmB);
            ly += S(44f);

            string idleLabel = _cfg.AutoHideSeconds == 0 ? "Mati" : (_cfg.AutoHideSeconds + " dtk");
            DrawSliderRow(g, pal, "SL_AUTOHIDE", SR(lx, ly, lw2, S(34f)), "Auto Blank", idleLabel, _cfg.AutoHideSeconds, 0, 30, fSmB);
            ly += S(48f);

            DrawToggleRow(g, pal, "SYS_NATURAL", SR(lx, ly, lw2, S(38f)), "Bahasa Sehari-hari Sopan & Natural", _cfg.NaturalTone, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_SPEAKER", SR(lx, ly, lw2, S(38f)), "Deteksi Nama Pembicara (Speaker Badge)", _cfg.DetectSpeaker, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_STABLE", SR(lx, ly, lw2, S(38f)), "Tunggu teks berhenti mengetik", _cfg.WaitStable, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_MERGE", SR(lx, ly, lw2, S(38f)), "Gabung baris (mode Dialog)", _cfg.MergeLines, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_HIDEEMPTY", SR(lx, ly, lw2, S(38f)), "Sembunyikan kotak saat kosong", _cfg.HideEmpty, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_AUTOLIVE", SR(lx, ly, lw2, S(38f)), "Otomatis Live saat dibuka", _cfg.AutoLive, fBody); ly += S(44f);
            DrawToggleRow(g, pal, "SYS_TRAY", SR(lx, ly, lw2, S(38f)), "Tutup ke System Tray", _cfg.CloseToTray, fBody); ly += S(48f);

            float halfBtn = (lw2 - S(8f)) * 0.5f;
            int glossCount = string.IsNullOrWhiteSpace(_cfg.Glossary) ? 0 : _cfg.Glossary.Split(new char[] { ';', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
            string glossLbl = glossCount > 0 ? ("Glosarium (" + glossCount + ")") : "Glosarium Game";
            DrawOutlineBtn(g, pal, "BTN_GLOSSARY", SR(lx, ly, halfBtn, S(36f)), glossLbl, IconKind.Gamepad, fSmB, S(9f));

            // Gemini AI API Key Button
            string gLbl = string.IsNullOrEmpty(_cfg.GeminiApiKey) ? "Gemini AI Key" : "Gemini Key Aktif";
            DrawOutlineBtn(g, pal, "BTN_GEMINI_KEY", SR(lx + halfBtn + S(8f), ly, halfBtn, S(36f)), gLbl, IconKind.Sparkle, fSmB, S(9f));

            List<string> packs = _engine.InstalledOcrLanguages;
            string pStr = packs.Count > 0 ? ("WinRT OCR Lokal: " + string.Join(", ", packs.ToArray())) : "OCR: Online Fallback";
            RectangleF pb = SR(lx, left.Bottom - S(40f), lw2, S(26f));
            Theme.FillRR(g, pb, S(8f), pal.Well);
            Theme.DrawTextLeft(g, pStr, fCap, pal.TextSub, SR(pb.X + S(10f), pb.Y, pb.Width - S(20f), pb.Height));

            RectangleF right = SR(x + colW + colGap, cy, colW, colH);
            Theme.DrawCard(g, pal, right, S(14f));
            float rx = right.X + S(18f), rw2 = right.Width - S(36f), ry = right.Y + S(16f);

            string[,] keys = new string[,]
            {
                { "Alt + E", "Mulai / Hentikan Live" },
                { "Alt + Q", "Blok Layar Area OCR Baru" },
                { "Alt + A", "Blok Layar Kotak Hasil" },
                { "Alt + W", "Edit Semua Kotak di Layar" },
                { "Alt + R", "Scan Ulang Sekarang" },
                { "Alt + H", "Sembunyikan Overlay" },
                { "F11",     "Fullscreen Penuh" },
                { "Ctrl + / -", "Zoom In / Out Tampilan" }
            };

            for (int i = 0; i < 8; i++)
            {
                RectangleF kr = SR(rx, ry, rw2, S(36f));
                Theme.FillRR(g, kr, S(8f), pal.Well);
                Theme.DrawTextLeft(g, keys[i, 1], fSm, pal.Text, SR(kr.X + S(12f), kr.Y, kr.Width - S(92f), kr.Height));
                RectangleF kb = SR(kr.Right - S(76f), kr.Y + S(6f), S(66f), S(24f));
                Theme.FillRR(g, kb, S(6f), pal.Card);
                Theme.DrawRR(g, kb, S(6f), pal.BorderStrong, 1f);
                Theme.DrawTextCenter(g, keys[i, 0], fCap, pal.AccentText, kb);
                ry += S(42f);
            }

            RectangleF tip = SR(rx, right.Bottom - S(86f), rw2, S(70f));
            Theme.FillRR(g, tip, S(10f), pal.AccentSoft);
            Theme.DrawRR(g, tip, S(10f), Color.FromArgb(110, pal.Accent), 1f);
            Theme.DrawTextLeft(g, "Tampil di Atas Game", fSmB, pal.AccentText, SR(tip.X + S(12f), tip.Y + S(8f), tip.Width - S(24f), S(18f)));
            Theme.DrawTextWrap(g, "Setel Display Mode game ke Borderless Windowed atau Windowed agar overlay tampil mulus & click-through.",
                fCap, pal.Text, SR(tip.X + S(12f), tip.Y + S(28f), tip.Width - S(24f), S(36f)));

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
            Theme.DrawTextLeft(g, "Log Pembaruan", fSerifLg, pal.Text, SR(x, cy, S(280f), S(34f)));
            Theme.DrawTextRight(g, "Dibuat oleh Danzyyy", fSmB, pal.AccentText, SR(x + w - S(200f), cy + S(6f), S(200f), S(24f)));
            cy += S(48f);

            string[,] logs = new string[,]
            {
                { "v2.3", "Glosarium Nama Karakter, Speaker Badge & Bahasa Sehari-hari",
                  "• Glosarium & Kamus Nama Karakter per game (Hyrule, Saria, Triforce, atau Istilah=Terjemahan) agar tidak diterjemahkan harfiah.\n" +
                  "• Deteksi Nama Pembicara (Speaker Tag) otomatis tampil sebagai badge warna terpisah di atas kotak dialog.\n" +
                  "• Peningkatan bahasa Indonesia sehari-hari yang natural & sopan (aku/kamu, hindari kata kaku seperti Anda/Mengapa/Apakah)." },
                { "v2.2", "Terjemahan Natural, Template Game & Gaya Per-Area",
                  "• Mesin Naturalisasi Dialog Game & Idiom (memperbaiki kalimat kaku/literal seperti 'Catch me up') + dukungan opsional Gemini AI.\n" +
                  "• Template Profil Game: simpan & muat layout per judul game dari header atas.\n" +
                  "• Pengaturan Tampilan Per-Area: Area Dialog & Area Pilihan kini bisa punya ukuran font, warna, dan perataan berbeda.\n" +
                  "• Perbaikan bug tombol ESC saat blok area OCR/Hasil menggunakan Global Hotkey, serta teks UI 100% tajam native ClearType." },
                { "v2.1", "Auto-Blank Idle & Mode Pilihan Terpisah",
                  "• Fitur Auto Blank otomatis mengosongkan overlay saat tidak ada dialog baru.\n" +
                  "• Mode Dialog vs Pilihan agar opsi pilihan game diterjemahkan terpisah per baris.\n" +
                  "• Optimasi kecepatan HTTP Keep-Alive & bypass WPAD proxy untuk respons instan." },
                { "v2.0", "Claude-Style Minimalist UI, Portable & Installer",
                  "• Desain ulang penuh bertema hangat ala Claude AI (Dark & Light Mode), Zoom 75%-150%, Fullscreen F11.\n" +
                  "• Blok Layar Desktop langsung untuk Area OCR dan Kotak Hasil.\n" +
                  "• Tersedia versi Portable dan Windows Installer lengkap dengan Start Menu & Uninstaller." }
            };

            for (int i = 0; i < 4; i++)
            {
                float cardH = i <= 1 ? S(146f) : S(124f);
                RectangleF card = SR(x, cy, w, cardH);
                Theme.DrawCard(g, pal, card, S(13f));

                RectangleF vPill = SR(card.X + S(18f), card.Y + S(14f), S(54f), S(24f));
                Theme.FillRR(g, vPill, S(12f), i == 0 ? pal.Accent : pal.Well);
                Theme.DrawTextCenter(g, logs[i, 0], fCap, i == 0 ? Color.White : pal.TextSub, vPill);

                Theme.DrawTextLeft(g, logs[i, 1], fSec, pal.Text, SR(vPill.Right + S(12f), card.Y + S(14f), card.Width - S(104f), S(24f)));
                Theme.DrawTextWrap(g, logs[i, 2], fSm, pal.TextSub, SR(card.X + S(18f), card.Y + S(46f), card.Width - S(36f), cardH - S(56f)));
                cy += cardH + S(12f);
            }

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
            using (SolidBrush w = new SolidBrush(Color.White))
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
            float lx = r.X + S(13f);
            if (ic != IconKind.None)
            {
                float isz = S(14f);
                Theme.DrawIcon(g, ic, SR(lx, r.Y + (r.Height - isz) * 0.5f, isz, isz), Color.White, 1.8f);
                lx += S(20f);
            }
            Theme.DrawTextLeft(g, text, f, Color.White, SR(lx, r.Y, r.Right - lx - S(10f), r.Height));
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawOutlineBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, Font f, float rad)
        {
            bool hov = (_hoverId == id);
            Theme.FillRR(g, r, rad, hov ? pal.CardHover : pal.Card);
            Theme.DrawRR(g, r, rad, hov ? pal.Accent : pal.BorderStrong, 1f);
            float isz = S(14f);
            if (string.IsNullOrEmpty(text) && ic != IconKind.None)
                Theme.DrawIcon(g, ic, SR(r.X + (r.Width - isz) * 0.5f, r.Y + (r.Height - isz) * 0.5f, isz, isz), pal.TextSub, 1.65f);
            else if (ic != IconKind.None)
            {
                Theme.DrawIcon(g, ic, SR(r.X + S(11f), r.Y + (r.Height - isz) * 0.5f, isz, isz), pal.AccentText, 1.65f);
                Theme.DrawTextLeft(g, text, f, pal.Text, SR(r.X + S(31f), r.Y, r.Width - S(36f), r.Height));
            }
            else Theme.DrawTextCenter(g, text, f, pal.Text, r);
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r });
        }

        private void DrawAreaActionBtn(Graphics g, Palette pal, string id, RectangleF r, string text, IconKind ic, int areaId, Color icCol, Font f)
        {
            bool hov = (_hoverId == id && _hoverIdx == areaId);
            Theme.FillRR(g, r, S(7f), hov ? pal.CardHover : pal.Card);
            Theme.DrawRR(g, r, S(7f), hov ? icCol : pal.BorderStrong, 1f);
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
            if (active) { Theme.FillRR(g, r, S(6f), pal.Accent); Theme.DrawTextCenter(g, text, f, Color.White, r); }
            else { if (hov) Theme.FillRR(g, r, S(6f), pal.CardHover); Theme.DrawTextCenter(g, text, f, hov ? pal.Text : pal.TextSub, r); }
            _hits.Add(new Hit { Id = id, Kind = HitKind.Button, Box = r, Index = idx });
        }

        private void DrawSelect(Graphics g, Palette pal, string id, RectangleF r, string label, Font f)
        {
            bool open = (_openDrop == id), hov = (_hoverId == id);
            Theme.FillRR(g, r, S(9f), pal.Card);
            Theme.DrawRR(g, r, S(9f), (open || hov) ? pal.Accent : pal.BorderStrong, 1f);
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
            using (SolidBrush b2 = new SolidBrush(Color.White))
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
                Invalidate();
            }
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
                if (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0) { _cfg.UiZoom = 100; _cfg.Save(); ShowToast("Zoom 100%", Color.FromArgb(217, 119, 87)); Invalidate(); e.Handled = true; return; }
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
                    else if (id == "SL_INTERVAL") _cfg.Interval = Math.Max(100, ((v + 20) / 40) * 40);
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
            Color cur = which == 0 ? st.TextColor : (which == 1 ? st.BgColor : st.OutlineColor);
            using (ColorDialog cd = new ColorDialog { Color = cur, FullOpen = true })
            {
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    if (which == 0) st.TextColor = cd.Color;
                    else if (which == 1) st.BgColor = cd.Color;
                    else st.OutlineColor = cd.Color;
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
                case "BTN_GEMINI_KEY": PromptGeminiKey(); break;
                case "BTN_GLOSSARY": PromptGlossary(); break;

                case "ZOOM_IN": AdjustZoom(10); break;
                case "ZOOM_OUT": AdjustZoom(-10); break;
                case "ZOOM_RESET": _cfg.UiZoom = 100; _cfg.Save(); ShowToast("Zoom 100%", Color.FromArgb(217, 119, 87)); Invalidate(); break;

                case "BTN_LIVE": ToggleLive(); break;
                case "BTN_ADD_AREA": BeginDesktopSnip(-1, SnipTargetKind.Ocr); break;
                case "BTN_EDIT_SCREEN": ToggleEditOnScreen(); break;
                case "BTN_SCAN": TriggerManualScan(); break;
                case "BTN_PREVIEW_LIVE":
                    _overlay.ShowPreviewSample();
                    ShowToast("Preview tampil di desktop", Color.FromArgb(217, 119, 87));
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
                    ShowToast("Mode Dialog (gabung kalimat)", Color.FromArgb(217, 119, 87));
                    break;
                case "AREA_MODE_CHC":
                    lock (_cfg.Sync) { Area a = _cfg.FindArea(idx); if (a != null) a.Mode = AreaTextMode.Choice; }
                    _cfg.Save(); _engine.ClearAreaState(idx); _engine.ForceScanNow();
                    ShowToast("Mode Pilihan (terjemah pisah per baris)", Color.FromArgb(217, 119, 87));
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
                    _cfg.Save(); _overlay.Rebuild(); ShowToast("Kotak hasil disejajarkan dengan OCR", Color.FromArgb(217, 119, 87));
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

                case "SYS_NATURAL": _cfg.NaturalTone = !_cfg.NaturalTone; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_SPEAKER": _cfg.DetectSpeaker = !_cfg.DetectSpeaker; _cfg.Save(); _engine.ClearState(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_STABLE": _cfg.WaitStable = !_cfg.WaitStable; _cfg.Save(); Invalidate(); break;
                case "SYS_MERGE": _cfg.MergeLines = !_cfg.MergeLines; _cfg.Save(); _engine.ForceScanNow(); Invalidate(); break;
                case "SYS_HIDEEMPTY": _cfg.HideEmpty = !_cfg.HideEmpty; _cfg.Save(); _overlay.UpdateTexts(); Invalidate(); break;
                case "SYS_AUTOLIVE": _cfg.AutoLive = !_cfg.AutoLive; _cfg.Save(); Invalidate(); break;
                case "SYS_TRAY": _cfg.CloseToTray = !_cfg.CloseToTray; _cfg.Save(); Invalidate(); break;

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
