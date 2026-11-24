using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TranslatorLive
{
    public class UpdateInfo
    {
        public bool HasUpdate = false;
        public string CurrentVersion = "v0.3-beta";
        public string LatestVersion = "";
        public string ReleaseName = "";
        public string ReleaseUrl = "https://github.com/danzyyy19/LingoLens/releases/latest";
        public string ReleaseBody = "";
        public string DownloadPortableUrl = "";
        public string DownloadSetupUrl = "";
        public string StatusMessage = "";
        public bool IsChecking = false;
        public DateTime? LastChecked = null;
    }

    public static class UpdateChecker
    {
        public static readonly string CurrentVersion = ResolveCurrentVersion();
        public const string RepoApiUrl = "https://api.github.com/repos/danzyyy19/LingoLens/releases/latest";
        public const string RepoHtmlUrl = "https://github.com/danzyyy19/LingoLens/releases/latest";

        private static string ResolveCurrentVersion()
        {
            try
            {
                object[] attrs = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attrs != null && attrs.Length > 0)
                {
                    string v = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        v = v.Trim().Replace(' ', '-').ToLowerInvariant();
                        return v.StartsWith("v") ? v : ("v" + v);
                    }
                }
            }
            catch { }
            return "v0.3-beta";
        }

        private static readonly UpdateInfo _info = new UpdateInfo();
        public static UpdateInfo Info { get { return _info; } }

        public static event Action OnStatusChanged;

        public static void CheckAsync(bool silent = true)
        {
            if (Info.IsChecking) return;
            Info.IsChecking = true;
            Info.StatusMessage = "Memeriksa pembaruan...";
            Notify();

            Task.Run(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(RepoApiUrl);
                    req.Method = "GET";
                    req.UserAgent = "LingoLens-App";
                    req.Timeout = 7000;
                    req.ReadWriteTimeout = 7000;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                    string json = null;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        json = reader.ReadToEnd();
                    }

                    if (!string.IsNullOrEmpty(json))
                    {
                        ParseReleaseJson(json);
                        Info.LastChecked = DateTime.Now;
                        if (Info.HasUpdate)
                        {
                            Info.StatusMessage = "Tersedia pembaruan: " + Info.LatestVersion;
                        }
                        else
                        {
                            Info.StatusMessage = "Versi Anda sudah paling baru (" + CurrentVersion + ")";
                        }
                    }
                    else
                    {
                        Info.StatusMessage = "Tidak ada respon dari server.";
                    }
                }
                catch (Exception ex)
                {
                    if (!silent)
                    {
                        Info.StatusMessage = "Gagal memeriksa: " + ex.Message;
                    }
                    else
                    {
                        Info.StatusMessage = "";
                    }
                }
                finally
                {
                    Info.IsChecking = false;
                    Notify();
                }
            });
        }

        private static void Notify()
        {
            try
            {
                Action handler = OnStatusChanged;
                if (handler != null) handler();
            }
            catch { }
        }

        private static void ParseReleaseJson(string json)
        {
            string tag = ExtractJsonField(json, "tag_name");
            string name = ExtractJsonField(json, "name");
            string htmlUrl = ExtractJsonField(json, "html_url");
            string body = ExtractJsonField(json, "body");

            Info.LatestVersion = (tag ?? "").Trim();
            Info.ReleaseName = string.IsNullOrEmpty(name) ? tag : name.Trim();
            if (!string.IsNullOrEmpty(htmlUrl)) Info.ReleaseUrl = htmlUrl.Trim();
            Info.ReleaseBody = body ?? "";

            Info.HasUpdate = IsNewer(Info.LatestVersion, CurrentVersion);

            Match mPort = Regex.Match(json, @"\""browser_download_url\""\s*:\s*\""([^\""]*LingoLens-Portable\.exe)\""");
            if (mPort.Success) Info.DownloadPortableUrl = mPort.Groups[1].Value;

            Match mSetup = Regex.Match(json, @"\""browser_download_url\""\s*:\s*\""([^\""]*Setup-LingoLens\.exe)\""");
            if (mSetup.Success) Info.DownloadSetupUrl = mSetup.Groups[1].Value;
        }

        private static string ExtractJsonField(string json, string field)
        {
            Match m = Regex.Match(json, @"\""" + Regex.Escape(field) + @"\""\s*:\s*\""((?:\\\""|[^\""])*)\""");
            if (m.Success)
            {
                return Regex.Unescape(m.Groups[1].Value);
            }
            return null;
        }

        public static bool IsNewer(string latestTag, string currentTag)
        {
            if (string.IsNullOrEmpty(latestTag)) return false;
            if (string.Equals(latestTag.Trim(), currentTag.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

            int[] vLatest = ParseSemVer(latestTag);
            int[] vCurr = ParseSemVer(currentTag);

            for (int i = 0; i < 3; i++)
            {
                if (vLatest[i] > vCurr[i]) return true;
                if (vLatest[i] < vCurr[i]) return false;
            }
            return false;
        }

        private static int[] ParseSemVer(string tag)
        {
            int[] ver = new int[3] { 0, 0, 0 };
            if (string.IsNullOrEmpty(tag)) return ver;
            Match m = Regex.Match(tag, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (m.Success)
            {
                if (m.Groups[1].Success) int.TryParse(m.Groups[1].Value, out ver[0]);
                if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out ver[1]);
                if (m.Groups[3].Success) int.TryParse(m.Groups[3].Value, out ver[2]);
            }
            return ver;
        }

        public static void OpenReleaseUrl()
        {
            try
            {
                string url = !string.IsNullOrEmpty(Info.ReleaseUrl) ? Info.ReleaseUrl : RepoHtmlUrl;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        public static void OpenDownloadUrl()
        {
            try
            {
                string url = !string.IsNullOrEmpty(Info.DownloadPortableUrl) ? Info.DownloadPortableUrl :
                             (!string.IsNullOrEmpty(Info.DownloadSetupUrl) ? Info.DownloadSetupUrl :
                             (!string.IsNullOrEmpty(Info.ReleaseUrl) ? Info.ReleaseUrl : RepoHtmlUrl));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
