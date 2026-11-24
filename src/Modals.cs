using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TranslatorLive
{
    internal static class Modals
    {
        [DllImport("user32.dll", EntryPoint = "ReleaseCapture")]
        private static extern bool ReleaseCaptureNative();
        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        private static extern IntPtr SendMessageNative(IntPtr h, int m, int w, int l);

        private static int D(float v, float scale)
        {
            return (int)Math.Round(v * scale);
        }

        private static Font MF(float px, float scale, bool bold)
        {
            return Theme.UiFont(Math.Max(10f, (float)Math.Round(px * scale)), bold);
        }

        private static Form CreateStyledModal(string title, string subtitle, int logW, int logH, float scale, Palette pal, out int contentTopY)
        {
            Form dlg = new Form();
            dlg.FormBorderStyle = FormBorderStyle.None;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.BackColor = pal.Bg;
            dlg.ForeColor = pal.Text;
            dlg.ShowInTaskbar = false;
            dlg.KeyPreview = true;

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Min(D(logW, scale), work.Width * 92 / 100);
            int h = Math.Min(D(logH, scale), work.Height * 92 / 100);
            dlg.ClientSize = new Size(w, h);

            int hdrH = D(66f, scale);
            contentTopY = hdrH + D(14f, scale);

            dlg.HandleCreated += delegate
            {
                try
                {
                    int darkAttr = pal.Dark ? 1 : 0;
                    Native.DwmSetWindowAttribute(dlg.Handle, 20, ref darkAttr, sizeof(int));
                    int corner = 2; // DWMWCP_ROUND
                    Native.DwmSetWindowAttribute(dlg.Handle, 33, ref corner, sizeof(int));
                }
                catch { }
            };

            dlg.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    dlg.Close();
                }
            };

            dlg.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF full = new RectangleF(0, 0, dlg.ClientSize.Width, dlg.ClientSize.Height);

                // Subtle architectural textured canvas on whole modal
                Theme.DrawCanvasBackground(g, pal, full, 0);

                // Crisp header strip
                RectangleF hdrR = new RectangleF(0, 0, dlg.ClientSize.Width, hdrH);
                using (SolidBrush hb = new SolidBrush(pal.Sidebar))
                    g.FillRectangle(hb, hdrR);

                // Signature 3px Cinema Subtitle Yellow top rail
                using (SolidBrush ab = new SolidBrush(pal.Accent))
                    g.FillRectangle(ab, 0, 0, dlg.ClientSize.Width, D(3f, scale));

                // Header bottom divider
                using (Pen dp = new Pen(pal.BorderStrong, 1f))
                    g.DrawLine(dp, 0, hdrH - 1, dlg.ClientSize.Width, hdrH - 1);

                // Outer crisp 1px border
                using (Pen bp = new Pen(pal.BorderStrong, 1f))
                    g.DrawRectangle(bp, 0, 0, dlg.ClientSize.Width - 1, dlg.ClientSize.Height - 1);

                using (Font fTitle = MF(16.5f, scale, true))
                using (Font fSub = MF(12f, scale, false))
                {
                    int tx = D(22f, scale);
                    int tw = dlg.ClientSize.Width - D(74f, scale);
                    Theme.DrawTextLeft(g, title, fTitle, pal.Text, new RectangleF(tx, D(12f, scale), tw, D(24f, scale)));
                    if (!string.IsNullOrEmpty(subtitle))
                        Theme.DrawTextLeft(g, subtitle, fSub, pal.TextSub, new RectangleF(tx, D(37f, scale), tw, D(20f, scale)));
                }
            };

            // Drag modal by header
            dlg.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && e.Y <= hdrH)
                {
                    ReleaseCaptureNative();
                    SendMessageNative(dlg.Handle, 0xA1, 2, 0);
                }
            };

            // Custom dark/light close button (top-right)
            Button btnX = new Button();
            btnX.Text = "×";
            btnX.Size = new Size(D(36f, scale), D(36f, scale));
            btnX.Location = new Point(dlg.ClientSize.Width - D(48f, scale), D(16f, scale));
            btnX.FlatStyle = FlatStyle.Flat;
            btnX.FlatAppearance.BorderSize = 0;
            btnX.FlatAppearance.MouseOverBackColor = pal.Danger;
            btnX.BackColor = pal.Sidebar;
            btnX.ForeColor = pal.TextSub;
            btnX.Font = MF(20f, scale, true);
            btnX.Cursor = Cursors.Hand;
            btnX.TabStop = false;
            btnX.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
            btnX.MouseEnter += delegate { btnX.ForeColor = Color.White; };
            btnX.MouseLeave += delegate { btnX.ForeColor = pal.TextSub; };
            dlg.Controls.Add(btnX);

            return dlg;
        }

        private static Label MakeLabel(string text, int x, int y, int w, int h, float px, float scale, Color col, bool bold)
        {
            Label lbl = new Label();
            lbl.UseMnemonic = false;
            lbl.Text = text;
            lbl.Location = new Point(x, y);
            lbl.Size = new Size(w, h);
            lbl.ForeColor = col;
            lbl.BackColor = Color.Transparent;
            lbl.Font = MF(px, scale, bold);
            return lbl;
        }

        private static Panel MakeInputFrame(int x, int y, int w, int h, float scale, Palette pal, TextBox tb)
        {
            Panel wrap = new Panel();
            wrap.Location = new Point(x, y);
            wrap.Size = new Size(w, h);
            wrap.BackColor = pal.Well;
            wrap.Padding = new Padding(D(10f, scale), D(8f, scale), D(10f, scale), D(8f, scale));
            wrap.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen bp = new Pen(tb.Focused ? pal.Accent : pal.BorderStrong, 1f))
                    e.Graphics.DrawRectangle(bp, 0, 0, wrap.Width - 1, wrap.Height - 1);
            };
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = pal.Well;
            tb.ForeColor = pal.Text;
            tb.Dock = DockStyle.Fill;
            tb.GotFocus += delegate { wrap.Invalidate(); };
            tb.LostFocus += delegate { wrap.Invalidate(); };
            wrap.Controls.Add(tb);
            return wrap;
        }

        private static Panel MakeStyledListBox(int x, int y, int w, int h, float scale, Palette pal, out ListBox lb)
        {
            Panel wrap = new Panel();
            wrap.Location = new Point(x, y);
            wrap.Size = new Size(w, h);
            wrap.BackColor = pal.Well;
            wrap.Padding = new Padding(1);
            wrap.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen bp = new Pen(pal.BorderStrong, 1f))
                    e.Graphics.DrawRectangle(bp, 0, 0, wrap.Width - 1, wrap.Height - 1);
            };

            ListBox list = new ListBox();
            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = pal.Well;
            list.ForeColor = pal.Text;
            list.Font = MF(13f, scale, false);
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = D(34f, scale);
            list.IntegralHeight = false;

            list.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= list.Items.Count) return;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color bg = sel ? pal.Accent : ((e.Index % 2 == 0) ? pal.Well : pal.Card);
                Color fg = sel ? pal.AccentBtnText : pal.Text;
                using (SolidBrush br = new SolidBrush(bg))
                    e.Graphics.FillRectangle(br, e.Bounds);

                string itemTxt = list.Items[e.Index].ToString();
                using (Font itf = MF(13f, scale, sel))
                {
                    RectangleF tr = new RectangleF(e.Bounds.X + D(12f, scale), e.Bounds.Y, e.Bounds.Width - D(20f, scale), e.Bounds.Height);
                    Theme.DrawTextLeft(e.Graphics, itemTxt, itf, fg, tr);
                }
                if (!sel)
                {
                    using (Pen sep = new Pen(pal.Border, 1f))
                        e.Graphics.DrawLine(sep, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
            };

            wrap.Controls.Add(list);
            lb = list;
            return wrap;
        }

        private static Button MakeButton(string text, int x, int y, int w, int h, float scale, Palette pal, bool primary, bool danger)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(w, h);
            btn.FlatStyle = FlatStyle.Flat;
            btn.Font = MF(13f, scale, true);
            btn.Cursor = Cursors.Hand;
            if (primary)
            {
                btn.BackColor = pal.Accent;
                btn.ForeColor = pal.AccentBtnText;
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = pal.AccentHover;
            }
            else if (danger)
            {
                btn.BackColor = pal.Card;
                btn.ForeColor = pal.Danger;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = pal.Danger;
                btn.FlatAppearance.MouseOverBackColor = pal.CardHover;
            }
            else
            {
                btn.BackColor = pal.Card;
                btn.ForeColor = pal.Text;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = pal.BorderStrong;
                btn.FlatAppearance.MouseOverBackColor = pal.CardHover;
            }
            return btn;
        }

        // =====================================================================
        // 1. MODAL: KUNCI JENDELA GAME / APLIKASI
        // =====================================================================
        public static Form BuildWindowLockModal(AppConfig cfg, float scale, Palette pal, bool isEn, Action onApplied)
        {
            int topY;
            Form dlg = CreateStyledModal(
                isEn ? "Lock OCR to Game / App Window" : "Kunci OCR ke Jendela Game / Aplikasi",
                isEn ? "Auto-pauses OCR & hides overlay when you Alt+Tab to other apps" : "Saat pindah ke aplikasi lain (chat/browser/MyASUS), OCR otomatis jeda & overlay sembunyi",
                660, 500, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            List<WindowTargetInfo> wins = Native.ListOpenWindows();

            string curLock = string.IsNullOrEmpty(cfg.TargetProcessName)
                ? (isEn ? "Current Target: All Screens (Unlocked)" : "Target Aktif: Semua Layar (Belum Dikunci)")
                : ((isEn ? "Locked Target: " : "Terkunci ke: ") + cfg.TargetWindowTitle + "  [" + cfg.TargetProcessName + ".exe]");

            Label lblCur = MakeLabel(curLock, pad, topY, innerW, D(24f, scale), 13f, scale,
                string.IsNullOrEmpty(cfg.TargetProcessName) ? pal.AccentText : pal.Live, true);

            int btnY = dlg.ClientSize.Height - D(58f, scale);
            int listTop = topY + D(32f, scale);
            int listH = btnY - listTop - D(16f, scale);

            ListBox lb;
            Panel listWrap = MakeStyledListBox(pad, listTop, innerW, listH, scale, pal, out lb);

            Action fillList = delegate
            {
                lb.Items.Clear();
                wins = Native.ListOpenWindows();
                foreach (WindowTargetInfo w in wins) lb.Items.Add(w.ToString());
                if (lb.Items.Count > 0) lb.SelectedIndex = 0;
            };
            fillList();

            Button btnRef = MakeButton(isEn ? "Refresh" : "Refresh", pad, btnY, D(112f, scale), D(40f, scale), scale, pal, false, false);
            btnRef.Click += delegate { fillList(); };

            Button btnUnlock = MakeButton(isEn ? "Unlock (All)" : "Lepas Kunci", pad + D(122f, scale), btnY, D(136f, scale), D(40f, scale), scale, pal, false, true);
            btnUnlock.Click += delegate
            {
                lock (cfg.Sync) { cfg.TargetProcessName = ""; cfg.TargetWindowTitle = ""; }
                cfg.Save();
                if (onApplied != null) onApplied();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            Button btnLock = MakeButton(isEn ? "Lock to Window" : "Kunci ke Jendela Ini",
                dlg.ClientSize.Width - pad - D(198f, scale), btnY, D(198f, scale), D(40f, scale), scale, pal, true, false);

            Action commitLock = delegate
            {
                if (lb.SelectedIndex < 0 || lb.SelectedIndex >= wins.Count)
                {
                    lblCur.ForeColor = pal.Danger;
                    lblCur.Text = isEn ? "Select a window from the list first!" : "Pilih salah satu jendela game di daftar dulu!";
                    return;
                }
                WindowTargetInfo chosen = wins[lb.SelectedIndex];
                lock (cfg.Sync)
                {
                    cfg.TargetProcessName = chosen.ProcessName;
                    cfg.TargetWindowTitle = chosen.Title;
                }
                cfg.Save();
                if (onApplied != null) onApplied();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            btnLock.Click += delegate { commitLock(); };
            lb.DoubleClick += delegate { commitLock(); };

            dlg.Controls.AddRange(new Control[] { lblCur, listWrap, btnRef, btnUnlock, btnLock });
            dlg.AcceptButton = btnLock;
            return dlg;
        }

        // =====================================================================
        // 2. MODAL: PILIH GAME & CARI LORE OTOMATIS
        // =====================================================================
        public static Form BuildGameLoreModal(AppConfig cfg, float scale, Palette pal, bool isEn, Action onApplied)
        {
            int topY;
            Form dlg = CreateStyledModal(
                isEn ? "Game Selector & Auto Lore Finder" : "Pilih Game & Cari Lore Otomatis",
                isEn ? "Pick a preset or search any game title to auto-load story lore & character glossary" : "Pilih preset instan atau cari game apa saja secara online untuk muat lore cerita & glosarium nama",
                840, 610, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            List<GameLoreEntry> allPresets = GameLoreService.GetPresets();
            List<GameLoreEntry> filtered = new List<GameLoreEntry>(allPresets);
            GameLoreEntry selectedEntry = null;

            // Search bar row
            int searchBtnW = D(168f, scale);
            int searchW = innerW - searchBtnW - D(10f, scale);
            TextBox tbSearch = new TextBox { Text = cfg.ActiveGameTitle ?? "", Font = MF(13.5f, scale, false) };
            Panel searchWrap = MakeInputFrame(pad, topY, searchW, D(40f, scale), scale, pal, tbSearch);

            Button btnOnline = MakeButton(isEn ? "Fetch Online Lore" : "Cari Lore Online",
                pad + searchW + D(10f, scale), topY, searchBtnW, D(40f, scale), scale, pal, true, false);

            Label lblStatus = MakeLabel(
                isEn ? "Tip: Type 'oot', 'elden', 'p5r', or click Fetch Online Lore for any game." : "Tip: Ketik 'oot', 'zelda', 'elden', 'p5r' untuk filter preset, atau klik 'Cari Lore Online' untuk game apa saja.",
                pad, topY + D(46f, scale), innerW, D(22f, scale), 12f, scale, pal.Live, false);

            int bodyTop = topY + D(76f, scale);
            int btnY = dlg.ClientSize.Height - D(58f, scale);
            int bodyH = btnY - bodyTop - D(16f, scale);

            // Left column: Preset Game List
            int leftW = D(286f, scale);
            Label lblList = MakeLabel(isEn ? "POPULAR GAME PRESETS (OFFLINE)" : "PRESET GAME POPULER (INSTAN)",
                pad, bodyTop, leftW, D(20f, scale), 11.5f, scale, pal.TextMute, true);

            ListBox lbGames;
            Panel listWrap = MakeStyledListBox(pad, bodyTop + D(24f, scale), leftW, bodyH - D(24f, scale), scale, pal, out lbGames);

            // Right column: Title + Lore + Glossary
            int rx = pad + leftW + D(18f, scale);
            int rw = innerW - leftW - D(18f, scale);

            Label lblTitle = MakeLabel(isEn ? "ACTIVE GAME TITLE" : "JUDUL GAME AKTIF", rx, bodyTop, rw, D(20f, scale), 11.5f, scale, pal.TextMute, true);
            TextBox tbTitle = new TextBox { Text = cfg.ActiveGameTitle ?? "", Font = MF(13.5f, scale, true) };
            Panel titleWrap = MakeInputFrame(rx, bodyTop + D(24f, scale), rw, D(38f, scale), scale, pal, tbTitle);

            int loreY = bodyTop + D(74f, scale);
            int remH = (bodyTop + bodyH) - loreY;
            int loreBoxH = (remH - D(58f, scale)) / 2;
            int glossY = loreY + D(24f, scale) + loreBoxH + D(12f, scale);
            int glossBoxH = (bodyTop + bodyH) - (glossY + D(24f, scale));

            Label lblLore = MakeLabel(isEn ? "GAME LORE & STORY CONTEXT" : "RINGKASAN LORE & KONTEKS CERITA", rx, loreY, rw, D(20f, scale), 11.5f, scale, pal.TextMute, true);
            TextBox tbLore = new TextBox { Text = cfg.GameLoreSummary ?? "", Multiline = true, ScrollBars = ScrollBars.Vertical, Font = MF(12.5f, scale, false) };
            Panel loreWrap = MakeInputFrame(rx, loreY + D(24f, scale), rw, Math.Max(D(72f, scale), loreBoxH), scale, pal, tbLore);

            Label lblGloss = MakeLabel(isEn ? "CHARACTERS, PLACES & TERMS GLOSSARY" : "GLOSARIUM KARAKTER, LOKASI & ISTILAH (AUTO-KOREKSI OCR)", rx, glossY, rw, D(20f, scale), 11.5f, scale, pal.TextMute, true);
            TextBox tbGloss = new TextBox { Text = cfg.Glossary ?? "", Multiline = true, ScrollBars = ScrollBars.Vertical, Font = MF(12.5f, scale, false) };
            Panel glossWrap = MakeInputFrame(rx, glossY + D(24f, scale), rw, Math.Max(D(72f, scale), glossBoxH), scale, pal, tbGloss);

            Action<string> refreshPresets = delegate(string q)
            {
                lbGames.Items.Clear();
                filtered.Clear();
                string ql = (q ?? "").Trim().ToLowerInvariant();
                GameLoreEntry aliasHit = string.IsNullOrEmpty(ql) ? null : GameLoreService.FindPreset(ql);
                foreach (GameLoreEntry ge in allPresets)
                {
                    if (string.IsNullOrEmpty(ql) || ge.Title.ToLowerInvariant().Contains(ql) || ge == aliasHit)
                    {
                        filtered.Add(ge);
                        lbGames.Items.Add(ge.Title);
                    }
                }
                if (filtered.Count == 0)
                {
                    foreach (GameLoreEntry ge in allPresets)
                    {
                        filtered.Add(ge);
                        lbGames.Items.Add(ge.Title);
                    }
                }
            };
            refreshPresets("");

            lbGames.SelectedIndexChanged += delegate
            {
                if (lbGames.SelectedIndex >= 0 && lbGames.SelectedIndex < filtered.Count)
                {
                    GameLoreEntry ge = filtered[lbGames.SelectedIndex];
                    selectedEntry = ge;
                    tbTitle.Text = ge.Title;
                    tbLore.Text = ge.LoreSummary;
                    tbGloss.Text = ge.Glossary;
                    lblStatus.ForeColor = pal.Live;
                    lblStatus.Text = "Preset dimuat: " + ge.Title + " (" + ge.Genre + " · " + GameLoreService.CountTerms(ge.Glossary) + " istilah)";
                }
            };

            tbSearch.TextChanged += delegate
            {
                string q = tbSearch.Text.Trim();
                refreshPresets(q);
                GameLoreEntry exactAlias = GameLoreService.FindPreset(q);
                if (exactAlias != null && filtered.Count > 0 && filtered[0] == exactAlias)
                {
                    lbGames.SelectedIndex = 0;
                }
            };

            btnOnline.Click += delegate
            {
                string q = tbSearch.Text.Trim();
                if (string.IsNullOrEmpty(q))
                {
                    lblStatus.ForeColor = pal.Danger;
                    lblStatus.Text = isEn ? "Type a game title first." : "Ketik judul game dulu di kotak pencarian.";
                    return;
                }
                btnOnline.Enabled = false;
                lblStatus.ForeColor = pal.AccentText;
                lblStatus.Text = (isEn ? "Searching online lore for '" : "Mencari lore online untuk '") + q + "'...";
                string gKey = cfg.GeminiApiKey;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string msg;
                    GameLoreEntry found = GameLoreService.SearchOnlineLore(q, gKey, out msg);
                    try
                    {
                        if (!dlg.IsDisposed && dlg.IsHandleCreated)
                        {
                            dlg.BeginInvoke((Action)delegate
                            {
                                btnOnline.Enabled = true;
                                if (found != null)
                                {
                                    selectedEntry = found;
                                    tbTitle.Text = found.Title;
                                    tbLore.Text = found.LoreSummary;
                                    tbGloss.Text = found.Glossary;
                                    lblStatus.ForeColor = pal.Live;
                                    lblStatus.Text = msg;
                                }
                                else
                                {
                                    lblStatus.ForeColor = pal.Danger;
                                    lblStatus.Text = msg;
                                }
                            });
                        }
                    }
                    catch { }
                });
            };

            Button btnCancel = MakeButton(isEn ? "Cancel" : "Batal",
                dlg.ClientSize.Width - pad - D(302f, scale), btnY, D(112f, scale), D(40f, scale), scale, pal, false, false);
            btnCancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            Button btnApply = MakeButton(isEn ? "Use This Game" : "Pakai Game Ini",
                dlg.ClientSize.Width - pad - D(180f, scale), btnY, D(180f, scale), D(40f, scale), scale, pal, true, false);
            btnApply.Click += delegate
            {
                string finalTitle = tbTitle.Text.Trim();
                if (string.IsNullOrEmpty(finalTitle)) finalTitle = tbSearch.Text.Trim();
                // If user typed a short alias like "oot" and left lore blank, auto-apply matching preset!
                if (string.IsNullOrWhiteSpace(tbLore.Text) && !string.IsNullOrEmpty(finalTitle))
                {
                    GameLoreEntry autoPreset = GameLoreService.FindPreset(finalTitle);
                    if (autoPreset != null)
                    {
                        finalTitle = autoPreset.Title;
                        tbLore.Text = autoPreset.LoreSummary;
                        if (string.IsNullOrWhiteSpace(tbGloss.Text)) tbGloss.Text = autoPreset.Glossary;
                    }
                }
                lock (cfg.Sync)
                {
                    cfg.ActiveGameTitle = finalTitle;
                    cfg.GameLoreSummary = tbLore.Text.Trim();
                    cfg.Glossary = tbGloss.Text.Replace("\r\n", ";").Replace("\n", ";").Trim(';', ' ');
                    if (selectedEntry != null && !string.IsNullOrEmpty(selectedEntry.DefaultSrc))
                        cfg.Src = selectedEntry.DefaultSrc;
                }
                if (!string.IsNullOrEmpty(finalTitle)) cfg.SaveGameProfile(finalTitle);
                else cfg.Save();
                if (onApplied != null) onApplied();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[]
            {
                searchWrap, btnOnline, lblStatus,
                lblList, listWrap,
                lblTitle, titleWrap, lblLore, loreWrap, lblGloss, glossWrap,
                btnCancel, btnApply
            });
            dlg.Shown += delegate
            {
                try { tbSearch.SelectionStart = tbSearch.Text.Length; tbSearch.SelectionLength = 0; } catch { }
            };
            dlg.AcceptButton = btnApply;
            return dlg;
        }

        // =====================================================================
        // 3. MODAL: RIWAYAT DIALOG SESI INI
        // =====================================================================
        public static Form BuildDialogBacklogModal(List<DialogLogEntry> logs, float scale, Palette pal, bool isEn, Action onClear, Action onCopied)
        {
            int topY;
            Form dlg = CreateStyledModal(
                isEn ? "Session Dialog Backlog" : "Riwayat Dialog Sesi Ini",
                (logs.Count > 0 ? (logs.Count + (isEn ? " lines recorded in this session" : " baris percakapan tersimpan di sesi ini")) : (isEn ? "No translated lines yet" : "Belum ada dialog yang diterjemahkan")),
                680, 520, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            int btnY = dlg.ClientSize.Height - D(58f, scale);
            int boxH = btnY - topY - D(16f, scale);

            StringBuilder sb = new StringBuilder();
            if (logs.Count == 0)
            {
                sb.AppendLine(isEn
                    ? "No translated lines yet in this session. Click Start Live (Alt+E) while playing."
                    : "Belum ada riwayat terjemahan di sesi ini.\r\nKlik 'Mulai Live' (Alt+E) saat bermain untuk mulai merekam dialog.");
            }
            else
            {
                for (int i = logs.Count - 1; i >= 0; i--)
                {
                    DialogLogEntry d = logs[i];
                    string spk = string.IsNullOrEmpty(d.Speaker) ? "" : ("[" + d.Speaker + "] ");
                    sb.AppendLine("[" + d.Time.ToString("HH:mm:ss") + "] " + spk + d.Translated);
                    sb.AppendLine("    OCR: " + d.Source);
                    sb.AppendLine();
                }
            }

            TextBox tb = new TextBox
            {
                Text = sb.ToString(),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = MF(13f, scale, false)
            };
            Panel boxWrap = MakeInputFrame(pad, topY, innerW, boxH, scale, pal, tb);

            Button btnClear = MakeButton(isEn ? "Clear Log" : "Bersihkan", pad, btnY, D(130f, scale), D(40f, scale), scale, pal, false, true);
            btnClear.Click += delegate
            {
                if (onClear != null) onClear();
                tb.Text = isEn ? "History cleared." : "Riwayat dialog berhasil dibersihkan.";
            };

            Button btnCopy = MakeButton(isEn ? "Copy All" : "Salin Semua",
                dlg.ClientSize.Width - pad - D(274f, scale), btnY, D(136f, scale), D(40f, scale), scale, pal, false, false);
            btnCopy.Click += delegate
            {
                try
                {
                    if (!string.IsNullOrEmpty(tb.Text)) Clipboard.SetText(tb.Text);
                    if (onCopied != null) onCopied();
                }
                catch { }
            };

            Button btnClose = MakeButton(isEn ? "Close" : "Tutup",
                dlg.ClientSize.Width - pad - D(128f, scale), btnY, D(128f, scale), D(40f, scale), scale, pal, true, false);
            btnClose.Click += delegate { dlg.DialogResult = DialogResult.OK; dlg.Close(); };

            dlg.Controls.AddRange(new Control[] { boxWrap, btnClear, btnCopy, btnClose });
            dlg.Shown += delegate
            {
                try { tb.SelectionStart = 0; tb.SelectionLength = 0; btnClose.Focus(); } catch { }
            };
            dlg.AcceptButton = btnClose;
            return dlg;
        }

        // =====================================================================
        // 4. MODAL: GLOSARIUM GAME
        // =====================================================================
        public static Form BuildGlossaryModal(AppConfig cfg, float scale, Palette pal, bool isEn, Action onSaved)
        {
            int topY;
            Form dlg = CreateStyledModal(
                isEn ? "Game Glossary & Character Guard" : "Glosarium & Penjaga Nama Game",
                isEn ? "One term per line or separated by ';'. Use '=' for custom mapping (e.g. Master Sword=Pedang Master)" : "1 istilah per baris atau pisah ';'. Pakai '=' untuk arti khusus (misal: Master Sword=Pedang Master)",
                580, 420, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            int btnY = dlg.ClientSize.Height - D(58f, scale);
            int boxH = btnY - topY - D(16f, scale);

            TextBox tb = new TextBox
            {
                Text = (cfg.Glossary ?? "").Replace(";", "\r\n"),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = MF(13f, scale, false)
            };
            Panel boxWrap = MakeInputFrame(pad, topY, innerW, boxH, scale, pal, tb);

            Button btnCancel = MakeButton(isEn ? "Cancel" : "Batal",
                dlg.ClientSize.Width - pad - D(294f, scale), btnY, D(112f, scale), D(40f, scale), scale, pal, false, false);
            btnCancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            Button btnSave = MakeButton(isEn ? "Save Glossary" : "Simpan Glosarium",
                dlg.ClientSize.Width - pad - D(172f, scale), btnY, D(172f, scale), D(40f, scale), scale, pal, true, false);
            btnSave.Click += delegate
            {
                cfg.Glossary = tb.Text.Replace("\r\n", ";").Replace("\n", ";").Trim(';', ' ');
                cfg.Save();
                if (onSaved != null) onSaved();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { boxWrap, btnCancel, btnSave });
            dlg.Shown += delegate
            {
                try { tb.SelectionStart = tb.Text.Length; tb.SelectionLength = 0; } catch { }
            };
            dlg.AcceptButton = btnSave;
            return dlg;
        }

        // =====================================================================
        // 5. MODAL: SIMPAN TEMPLATE GAME
        // =====================================================================
        public static Form BuildSaveProfileModal(AppConfig cfg, float scale, Palette pal, bool isEn, Action onSaved)
        {
            int topY;
            Form dlg = CreateStyledModal(
                isEn ? "Save Game Profile Template" : "Simpan Template Game",
                isEn ? "Saves areas, lore, glossary, window lock & overlay style for this game" : "Menyimpan area OCR, lore, glosarium, kunci jendela & gaya overlay untuk game ini",
                500, 230, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            int btnY = dlg.ClientSize.Height - D(58f, scale);

            Label lbl = MakeLabel(isEn ? "GAME PROFILE NAME" : "NAMA TEMPLATE GAME", pad, topY, innerW, D(20f, scale), 11.5f, scale, pal.TextMute, true);
            TextBox tb = new TextBox
            {
                Text = !string.IsNullOrEmpty(cfg.ActiveGameTitle) ? cfg.ActiveGameTitle : (cfg.ActiveProfile == "Default" ? "" : cfg.ActiveProfile),
                Font = MF(13.5f, scale, false)
            };
            Panel inWrap = MakeInputFrame(pad, topY + D(24f, scale), innerW, D(42f, scale), scale, pal, tb);

            Button btnCancel = MakeButton(isEn ? "Cancel" : "Batal",
                dlg.ClientSize.Width - pad - D(284f, scale), btnY, D(106f, scale), D(40f, scale), scale, pal, false, false);
            btnCancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            Button btnOk = MakeButton(isEn ? "Save Profile" : "Simpan Template",
                dlg.ClientSize.Width - pad - D(168f, scale), btnY, D(168f, scale), D(40f, scale), scale, pal, true, false);
            btnOk.Click += delegate
            {
                if (!string.IsNullOrWhiteSpace(tb.Text))
                {
                    cfg.ActiveGameTitle = tb.Text.Trim();
                    cfg.SaveGameProfile(tb.Text.Trim());
                    if (onSaved != null) onSaved();
                }
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lbl, inWrap, btnCancel, btnOk });
            dlg.AcceptButton = btnOk;
            return dlg;
        }

        // =====================================================================
        // 6. MODAL: GEMINI AI API KEY
        // =====================================================================
        public static Form BuildGeminiKeyModal(AppConfig cfg, float scale, Palette pal, bool isEn, Action onSaved)
        {
            int topY;
            Form dlg = CreateStyledModal(
                "Gemini AI API Key",
                isEn ? "Optional: unlocks lore-aware AI translation using your Google Gemini key" : "Opsional: aktifkan terjemahan AI sadar lore cerita & gaya gaul natural pakai API Key Gemini",
                540, 236, scale, pal, out topY);

            int pad = D(22f, scale);
            int innerW = dlg.ClientSize.Width - pad * 2;
            int btnY = dlg.ClientSize.Height - D(58f, scale);

            Label lbl = MakeLabel("GOOGLE GEMINI API KEY", pad, topY, innerW, D(20f, scale), 11.5f, scale, pal.TextMute, true);
            TextBox tb = new TextBox { Text = cfg.GeminiApiKey ?? "", Font = MF(13.5f, scale, false) };
            Panel inWrap = MakeInputFrame(pad, topY + D(24f, scale), innerW, D(42f, scale), scale, pal, tb);

            Button btnCancel = MakeButton(isEn ? "Cancel" : "Batal",
                dlg.ClientSize.Width - pad - D(264f, scale), btnY, D(106f, scale), D(40f, scale), scale, pal, false, false);
            btnCancel.Click += delegate { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

            Button btnOk = MakeButton(isEn ? "Save Key" : "Simpan Key",
                dlg.ClientSize.Width - pad - D(148f, scale), btnY, D(148f, scale), D(40f, scale), scale, pal, true, false);
            btnOk.Click += delegate
            {
                cfg.GeminiApiKey = tb.Text.Trim();
                if (!string.IsNullOrEmpty(cfg.GeminiApiKey)) cfg.Engine = "gemini";
                cfg.Save();
                if (onSaved != null) onSaved();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lbl, inWrap, btnCancel, btnOk });
            dlg.AcceptButton = btnOk;
            return dlg;
        }

        // =====================================================================
        // AUDIT HELPER: RENDER ALL MODALS TO PNG FOR VISUAL VERIFICATION!
        // =====================================================================
        public static void RenderAuditScreenshots(string outDir, float dpiScale)
        {
            Palette pal = Palette.For(true);
            AppConfig cfg = new AppConfig();
            cfg.ActiveGameTitle = "The Legend of Zelda: Ocarina of Time";
            GameLoreEntry preset = GameLoreService.FindPreset("oot");
            if (preset != null)
            {
                cfg.GameLoreSummary = preset.LoreSummary;
                cfg.Glossary = preset.Glossary;
            }

            List<DialogLogEntry> sampleLogs = new List<DialogLogEntry>();
            sampleLogs.Add(new DialogLogEntry { Time = DateTime.Now, Speaker = "Navi", Source = "The Great Deku Tree is asking for you. Come on—you gotta wake up!", Translated = "Great Deku Tree nyariin lo. Cepetan deh—lo mesti bangun!" });
            sampleLogs.Add(new DialogLogEntry { Time = DateTime.Now, Speaker = "", Source = "Farore, the goddess of courage.", Translated = "Farore, dewi keberanian." });

            SaveFormShot(BuildWindowLockModal(cfg, dpiScale, pal, false, null), Path.Combine(outDir, "audit_modal_window_lock.png"));
            SaveFormShot(BuildGameLoreModal(cfg, dpiScale, pal, false, null), Path.Combine(outDir, "audit_modal_game_lore.png"));
            SaveFormShot(BuildDialogBacklogModal(sampleLogs, dpiScale, pal, false, null, null), Path.Combine(outDir, "audit_modal_backlog.png"));
            SaveFormShot(BuildGlossaryModal(cfg, dpiScale, pal, false, null), Path.Combine(outDir, "audit_modal_glossary.png"));
        }

        private static void SaveFormShot(Form f, string path)
        {
            using (f)
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(40, 40);
                f.Show();
                f.Refresh();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
                f.Close();
            }
        }
    }
}
