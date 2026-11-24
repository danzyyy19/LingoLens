using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Setup-LingoLens")]
[assembly: AssemblyDescription("Instalasi LingoLens - Penerjemah Layar Game Windows")]
[assembly: AssemblyCompany("Danzyyy")]
[assembly: AssemblyProduct("LingoLens")]
[assembly: AssemblyCopyright("Copyright © 2026 Danzyyy")]
[assembly: AssemblyVersion("0.4.0.0")]
[assembly: AssemblyFileVersion("0.4.0.0")]
[assembly: AssemblyInformationalVersion("0.4 Beta")]

namespace TranslatorLiveInstaller
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && args.Length > 0 && args[0] == "--uninstall")
            {
                RunUninstall();
                return;
            }

            Application.Run(new SetupForm());
        }

        private static void RunUninstall()
        {
            DialogResult dr = MessageBox.Show(
                "Hapus instalasi LingoLens dari Windows?",
                "Uninstall LingoLens",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;

            try
            {
                foreach (Process p in Process.GetProcessesByName("LingoLens"))
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }

                string deskLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LingoLens.lnk");
                if (File.Exists(deskLink)) File.Delete(deskLink);

                string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "LingoLens");
                if (Directory.Exists(startDir)) Directory.Delete(startDir, true);

                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LingoLens", false);
                }
                catch { }

                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LingoLens");
                string cmd = "/C timeout /t 2 /nobreak > NUL & rmdir /S /Q \"" + installDir + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });

                MessageBox.Show("LingoLens berhasil di-uninstall.", "Selesai", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal uninstall: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class SetupForm : Form
    {
        private readonly CheckBox _chkDesktop;
        private readonly CheckBox _chkStartMenu;
        private readonly CheckBox _chkLaunch;
        private readonly Button _btnInstall;
        private readonly Label _lblStatus;
        private readonly string _installDir;

        public SetupForm()
        {
            _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LingoLens");

            Text = "Instalasi LingoLens";
            Size = new Size(490, 380);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(33, 32, 30);
            ForeColor = Color.FromArgb(238, 236, 230);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label
            {
                Text = "LingoLens",
                Font = new Font("Georgia", 20f, FontStyle.Regular),
                ForeColor = Color.FromArgb(238, 236, 230),
                AutoSize = true,
                Location = new Point(28, 24)
            };
            Controls.Add(title);

            Label sub = new Label
            {
                Text = "Penerjemah layar game real-time untuk Windows  •  v0.4 Beta by Danzyyy",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(169, 165, 156),
                AutoSize = true,
                Location = new Point(30, 62)
            };
            Controls.Add(sub);

            Label pathLbl = new Label
            {
                Text = "Folder instalasi:\n" + _installDir,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(148, 144, 136),
                Size = new Size(420, 42),
                Location = new Point(30, 98)
            };
            Controls.Add(pathLbl);

            _chkStartMenu = new CheckBox
            {
                Text = "Tambahkan ke Windows Start Menu (bisa dicari langsung)",
                Checked = true,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(238, 236, 230),
                Location = new Point(30, 152)
            };
            Controls.Add(_chkStartMenu);

            _chkDesktop = new CheckBox
            {
                Text = "Buat shortcut di Desktop",
                Checked = true,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(238, 236, 230),
                Location = new Point(30, 182)
            };
            Controls.Add(_chkDesktop);

            _chkLaunch = new CheckBox
            {
                Text = "Jalankan LingoLens setelah instalasi selesai",
                Checked = true,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(238, 236, 230),
                Location = new Point(30, 212)
            };
            Controls.Add(_chkLaunch);

            _lblStatus = new Label
            {
                Text = "Siap dipasang (tidak memerlukan hak Administrator).",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(169, 165, 156),
                Size = new Size(420, 24),
                Location = new Point(30, 250)
            };
            Controls.Add(_lblStatus);

            _btnInstall = new Button
            {
                Text = "Install Sekarang",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.FromArgb(217, 119, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(160, 40),
                Location = new Point(290, 286),
                Cursor = Cursors.Hand
            };
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Click += OnInstallClick;
            Controls.Add(_btnInstall);
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            _btnInstall.Enabled = false;
            _lblStatus.Text = "Memasang LingoLens...";
            Application.DoEvents();

            try
            {
                // 1. Stop any running LingoLens instance
                foreach (Process p in Process.GetProcessesByName("LingoLens"))
                {
                    try { p.Kill(); p.WaitForExit(1500); } catch { }
                }

                Directory.CreateDirectory(_installDir);
                string targetExe = Path.Combine(_installDir, "LingoLens.exe");
                string uninstExe = Path.Combine(_installDir, "Uninstall-LingoLens.exe");

                // 2. Extract embedded LingoLens.exe resource
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream res = asm.GetManifestResourceStream("LingoLens.exe"))
                {
                    if (res == null) throw new Exception("Resource LingoLens.exe tidak ditemukan.");
                    using (FileStream fs = File.Create(targetExe))
                    {
                        res.CopyTo(fs);
                    }
                }

                // Copy self as Uninstaller
                try { File.Copy(Application.ExecutablePath, uninstExe, true); } catch { }

                // 3. Create Start Menu & Desktop Shortcuts via WScript.Shell COM
                if (_chkStartMenu.Checked)
                {
                    string smDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "LingoLens");
                    Directory.CreateDirectory(smDir);
                    CreateShortcut(Path.Combine(smDir, "LingoLens.lnk"), targetExe, "", _installDir);
                    CreateShortcut(Path.Combine(smDir, "Uninstall LingoLens.lnk"), uninstExe, "--uninstall", _installDir);
                }

                if (_chkDesktop.Checked)
                {
                    string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(Path.Combine(desk, "LingoLens.lnk"), targetExe, "", _installDir);
                }

                // 4. Register in Windows Installed Apps (Add/Remove Programs)
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LingoLens"))
                {
                    key.SetValue("DisplayName", "LingoLens");
                    key.SetValue("DisplayVersion", "0.4.0");
                    key.SetValue("Publisher", "Danzyyy");
                    key.SetValue("DisplayIcon", targetExe);
                    key.SetValue("InstallLocation", _installDir);
                    key.SetValue("UninstallString", "\"" + uninstExe + "\" --uninstall");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }

                _lblStatus.Text = "Instalasi selesai!";
                if (_chkLaunch.Checked)
                {
                    Process.Start(new ProcessStartInfo(targetExe) { WorkingDirectory = _installDir });
                }
                Close();
            }
            catch (Exception ex)
            {
                _btnInstall.Enabled = true;
                _lblStatus.Text = "Gagal: " + ex.Message;
                MessageBox.Show("Instalasi gagal:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void CreateShortcut(string lnkPath, string targetPath, string args, string workDir)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { args });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workDir });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
    }
}
