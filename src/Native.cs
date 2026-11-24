using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace TranslatorLive
{
    public class WindowTargetInfo
    {
        public IntPtr Hwnd;
        public string ProcessName;
        public string Title;
        public override string ToString()
        {
            return Title + "  [" + ProcessName + ".exe]";
        }
    }

    internal static class Native
    {
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_EXITSIZEMOVE = 0x0232;
        public const int WM_GETMINMAXINFO = 0x0024;

        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NCCALCSIZE_PARAMS
        {
            public RECT r0, r1, r2;
            public IntPtr lppos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static List<WindowTargetInfo> ListOpenWindows()
        {
            List<WindowTargetInfo> list = new List<WindowTargetInfo>();
            int myPid = 0;
            try { myPid = Process.GetCurrentProcess().Id; } catch { }

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    int len = GetWindowTextLength(hWnd);
                    if (len <= 0) return true;
                    StringBuilder sb = new StringBuilder(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString().Trim();
                    if (string.IsNullOrEmpty(title)) return true;
                    if (title == "Program Manager" || title == "Settings" || title == "Windows Input Experience") return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if ((int)pid == myPid) return true;

                    string procName = "";
                    try
                    {
                        using (Process p = Process.GetProcessById((int)pid))
                            procName = p.ProcessName;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(procName)) return true;
                    if (string.Equals(procName, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep ApplicationFrameHost only if title is meaningful
                        if (string.Equals(procName, "TextInputHost", StringComparison.OrdinalIgnoreCase)) return true;
                    }

                    list.Add(new WindowTargetInfo { Hwnd = hWnd, ProcessName = procName, Title = title });
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            return list;
        }

        public static bool IsTargetWindowForeground(string targetProcess, string targetTitle)
        {
            if (string.IsNullOrWhiteSpace(targetProcess) && string.IsNullOrWhiteSpace(targetTitle))
                return true; // Global / unlocked mode

            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;

                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                string fgProc = "";
                try
                {
                    using (Process p = Process.GetProcessById((int)pid))
                        fgProc = p.ProcessName;
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(targetProcess) &&
                    !string.Equals(fgProc, targetProcess.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // If user locked a browser/player window (like Chrome/Edge/VLC), also check title fragment so switching tabs/other browser windows stops OCR!
                if (!string.IsNullOrWhiteSpace(targetTitle))
                {
                    bool isBrowserOrPlayer =
                        string.Equals(fgProc, "chrome", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fgProc, "msedge", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fgProc, "firefox", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fgProc, "brave", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fgProc, "opera", StringComparison.OrdinalIgnoreCase);

                    if (isBrowserOrPlayer)
                    {
                        int len = GetWindowTextLength(fg);
                        string fgTitle = "";
                        if (len > 0)
                        {
                            StringBuilder sb = new StringBuilder(len + 1);
                            GetWindowText(fg, sb, sb.Capacity);
                            fgTitle = sb.ToString();
                        }
                        // Strip volatile YouTube notification prefix like "(217) " from both titles so lock NEVER breaks when a notification arrives!
                        string cleanTarget = System.Text.RegularExpressions.Regex.Replace(targetTitle, @"^\(\d+\)\s*", "").Trim();
                        string cleanFg = System.Text.RegularExpressions.Regex.Replace(fgTitle, @"^\(\d+\)\s*", "").Trim();
                        string needle = cleanTarget.Length > 16 ? cleanTarget.Substring(0, 16) : cleanTarget;
                        if (!string.IsNullOrEmpty(needle) && cleanFg.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }
                }
                return true;
            }
            catch { return true; }
        }

        public static float DpiScale(IntPtr hwnd)
        {
            try
            {
                uint d = GetDpiForWindow(hwnd);
                if (d > 0) return d / 96f;
            }
            catch { }
            return 1f;
        }

        public static void PushLayered(IntPtr hwnd, Bitmap bmp, Point location)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                SIZE size = new SIZE { CX = bmp.Width, CY = bmp.Height };
                POINT src = new POINT { X = 0, Y = 0 };
                POINT dst = new POINT { X = location.X, Y = location.Y };
                BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
