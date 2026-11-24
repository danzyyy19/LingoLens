using System;
using System.IO;
using System.Text;

namespace TranslatorLive
{
    public static class AppLogger
    {
        private static readonly object _lock = new object();
        private const long MaxLogBytes = 512 * 1024; // 512 KB cap

        public static string LogFilePath
        {
            get { return Path.Combine(AppConfig.BaseDataDir, "debug.log"); }
        }

        public static void Info(string category, string message)
        {
            Write("INFO", category, message);
        }

        public static void Error(string category, string message, Exception ex = null)
        {
            string full = ex == null ? message : (message + " | " + ex.GetType().Name + ": " + ex.Message);
            Write("ERROR", category, full);
        }

        private static void Write(string level, string category, string message)
        {
            try
            {
                lock (_lock)
                {
                    string path = LogFilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (File.Exists(path))
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Length > MaxLogBytes)
                        {
                            string oldPath = path + ".old";
                            if (File.Exists(oldPath)) File.Delete(oldPath);
                            File.Move(path, oldPath);
                        }
                    }

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] [" + category + "] " + (message ?? "");
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
