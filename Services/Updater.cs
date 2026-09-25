using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Services
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, PageUrl, ZipUrl, SumsUrl;
    }

    public sealed class InstallResult
    {
        public bool Ok;
        public string Error;
        /// <summary>The release is broken for updating (e.g. its exe is not newer): do not try it again.</summary>
        public bool SkipRelease;
    }

    /// <summary>
    /// Updates the launcher from this repository's GitHub releases. The zip is checked against the release's
    /// SHA256SUMS.txt, then the new exe is put in place of the running one (Windows lets a running exe be
    /// renamed), so it is used from the next start. No installer, no admin rights, no window.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "goranbalsic/insurgency-sandstorm-mod-launcher";
        private const string ExeName = "SandstormModLauncher.exe";
        private const long MaxZipBytes = 50L * 1024 * 1024;

        /// <summary>Only exes built by the release workflow replace themselves; one built from source only reports updates.</summary>
#if OFFICIAL_BUILD
        public const bool CanInstall = true;
#else
        public const bool CanInstall = false;
#endif

        public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version);
        public static string ExePath => Assembly.GetExecutingAssembly().Location;
        private static string OldPath => ExePath + ".old";
        private static string NewPath => ExePath + ".new";

        private static Version Normalize(Version v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        /// <summary>Removes what an earlier update left next to the exe (the old exe can only go once it has exited).</summary>
        public static void CleanUp()
        {
            foreach (var f in new[] { OldPath, NewPath, NewPath + ".config" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static string etag;
        private static UpdateInfo lastInfo;

        /// <summary>
        /// The latest release. Blocking; call it off the UI thread. First a HEAD request to the release page, which only
        /// redirects to the newest tag (no body, not an API call). The release details come from the API only when that
        /// tag differs from the last answer, asked with its ETag so an unchanged release is a bodiless 304.
        /// </summary>
        public static UpdateInfo Check()
        {
            if (lastInfo != null)
            {
                string tag = LatestTag();
                if (tag != null && string.Equals(tag, lastInfo.Tag, StringComparison.OrdinalIgnoreCase)) return lastInfo;
            }
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.UserAgent = "SandstormModLauncher/" + Current.ToString(3);
            req.Accept = "application/vnd.github+json";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            if (etag != null && lastInfo != null) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
            string json;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    json = r.ReadToEnd();
                    if (json.Length > 1024 * 1024) throw new InvalidDataException("answer too large");
                    etag = resp.Headers[HttpResponseHeader.ETag];
                }
            }
            catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotModified && lastInfo != null)
            {
                return lastInfo;
            }
            lastInfo = Parse(json);
            return lastInfo;
        }

        /// <summary>The newest release tag from where github.com/.../releases/latest redirects to, or null.</summary>
        private static string LatestTag()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://github.com/" + Repo + "/releases/latest");
                req.Method = "HEAD";
                req.AllowAutoRedirect = false;
                req.UserAgent = "SandstormModLauncher/" + Current.ToString(3);
                req.Timeout = 10000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string location = resp.Headers[HttpResponseHeader.Location] ?? "";
                    int i = location.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
                    return i < 0 ? null : Uri.UnescapeDataString(location.Substring(i + 5)).Trim('/');
                }
            }
            catch { return null; }
        }

        private static UpdateInfo Parse(string json)
        {
            var root = Json.Parse(json);
            string tag = root.Get("tag_name").Str();
            if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version)) throw new InvalidDataException("The latest release has no version tag.");
            var info = new UpdateInfo { Tag = tag, Version = Normalize(version), PageUrl = root.Get("html_url").Str() };
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            foreach (var a in root.Get("assets").Arr())
            {
                string name = a.Get("name").Str() ?? "", url = a.Get("browser_download_url").Str() ?? "";
                if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("SandstormModLauncher", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) info.ZipUrl = url;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
            }
            return info;
        }

        /// <summary>Downloads, verifies and puts the new version in place. Blocking; call it off the UI thread.</summary>
        public static InstallResult Install(UpdateInfo info)
        {
            if (info.ZipUrl == null || info.SumsUrl == null) return Fail("the release has no zip or checksum file", true);
            var sums = ParseSums(Encoding.UTF8.GetString(Download(info.SumsUrl, 64 * 1024, null)));
            byte[] zip = Download(info.ZipUrl, MaxZipBytes, null);
            string zipName = info.ZipUrl.Substring(info.ZipUrl.LastIndexOf('/') + 1);
            if (!sums.TryGetValue(zipName, out var zipHash) || zipHash != Sha256(zip)) return Fail("the download does not match the release checksum", false);

            byte[] exe = null, config = null;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                foreach (var e in archive.Entries)
                {
                    if (e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) exe = Read(e);
                    else if (e.Name.Equals(ExeName + ".config", StringComparison.OrdinalIgnoreCase)) config = Read(e);
                }
            if (exe == null) return Fail("the zip has no " + ExeName, true);
            if (sums.TryGetValue(ExeName, out var exeHash) && exeHash != Sha256(exe)) return Fail("the exe does not match the release checksum", false);

            // Written next to the exe first: same drive, and a folder the launcher cannot write to fails here, before anything moved.
            File.WriteAllBytes(NewPath, exe);
            var built = Normalize(AssemblyName.GetAssemblyName(NewPath).Version);
            if (built <= Current)
            {
                TryDelete(NewPath);
                return Fail("release " + info.Tag + " contains version " + built.ToString(3), true);
            }

            // The running exe may already be the .old one (an update was installed earlier in this session).
            if (File.Exists(OldPath) && !IsRunningImage(OldPath)) TryDelete(OldPath);
            if (File.Exists(OldPath)) File.Delete(ExePath);
            else File.Move(ExePath, OldPath);
            try { File.Move(NewPath, ExePath); }
            catch
            {
                if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath);
                throw;
            }
            if (config != null) try { File.WriteAllBytes(ExePath + ".config", config); } catch (Exception ex) { AppLog.Warn("Update: config not replaced: " + ex.Message); }
            AppLog.Info("Update: " + info.Tag + " installed next to " + Current.ToString(3) + ", used from the next start");
            return new InstallResult { Ok = true };
        }

        private static bool IsRunningImage(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
            catch { return true; }
        }

        private static InstallResult Fail(string error, bool skip)
        {
            AppLog.Warn("Update not installed: " + error);
            return new InstallResult { Error = error, SkipRelease = skip };
        }

        private static Dictionary<string, string> ParseSums(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Length == 64) map[parts[1]] = parts[0].ToLowerInvariant();
            }
            return map;
        }

        private static byte[] Read(ZipArchiveEntry e)
        {
            if (e.Length > MaxZipBytes) throw new InvalidDataException("zip entry too large");
            using (var s = e.Open())
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        private static byte[] Download(string url, long maxBytes, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "SandstormModLauncher/" + Current.ToString(3);
            if (accept != null) req.Accept = accept;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var m = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    m.Write(buf, 0, n);
                    if (m.Length > maxBytes) throw new InvalidDataException("download too large");
                }
                return m.ToArray();
            }
        }
    }
}
