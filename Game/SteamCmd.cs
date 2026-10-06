using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    /// <summary>What SteamCMD is doing, from one line of its output.</summary>
    public sealed class SteamCmdProgress
    {
        /// <summary>A short text for the player ("Downloading", "Checking files", ...).</summary>
        public string Stage;
        /// <summary>0-100, or null when the step has no percentage.</summary>
        public double? Percent;
        /// <summary>Bytes done and in all (downloads), 0 when unknown.</summary>
        public long Done, Total;
        public bool Success, UpToDate;
        /// <summary>Why it failed, in plain words (null = no failure on this line).</summary>
        public string Error;
        /// <summary>A failure that usually goes away when SteamCMD simply runs again (its first run, Steam busy).</summary>
        public bool Retryable;
    }

    public sealed class SteamCmdResult
    {
        public bool Ok, UpToDate, Cancelled, Retryable;
        public string Error;
    }

    /// <summary>
    /// SteamCMD, Valve's command-line Steam client, installs and updates the dedicated server (app 581330) as the
    /// server admin guide describes: +force_install_dir &lt;folder&gt; +login anonymous +app_update 581330 +quit.
    /// It is downloaded from Valve only when the player installs or updates the server, and checked (a zip with
    /// steamcmd.exe signed by Valve) before it is run.
    /// </summary>
    public static class SteamCmd
    {
        public const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
        private const long MaxZipBytes = 30L * 1024 * 1024;
        /// <summary>No output at all for this long: SteamCMD is stuck (a big download still prints its progress).</summary>
        private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(15);

        public static string Dir => Path.Combine(AppPaths.LocalDir, "steamcmd");
        public static string Exe => Path.Combine(Dir, "steamcmd.exe");

        // ------------------------------------------------------------------ output

        private static readonly Regex SelfUpdate = new Regex(@"^\[\s*(?<p>\d+)%\]\s*(?<t>.*)$|^\[----\]\s*(?<t>.*)$", RegexOptions.Compiled);
        private static readonly Regex UpdateState = new Regex(@"Update state \(0x[0-9a-fA-F]+\)\s*(?<what>[A-Za-z ]+?),\s*progress:\s*(?<p>[\d.]+)\s*\((?<a>\d+)\s*/\s*(?<b>\d+)\)", RegexOptions.Compiled);
        private static readonly Regex StateError = new Regex(@"Error! App '\d+' state is 0x(?<code>[0-9a-fA-F]+) after update job", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex InstallError = new Regex(@"ERROR! Failed to install app '\d+' \((?<why>[^)]*)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>What one line of SteamCMD's output says, or null for a line that says nothing of interest.</summary>
        public static SteamCmdProgress ParseLine(string line)
        {
            string t = (line ?? "").Trim();
            if (t.Length == 0) return null;
            var inv = CultureInfo.InvariantCulture;
            if (t.StartsWith("Success! App", StringComparison.OrdinalIgnoreCase))
                return new SteamCmdProgress { Success = true, UpToDate = t.IndexOf("already up to date", StringComparison.OrdinalIgnoreCase) >= 0, Stage = N("Done"), Percent = 100 };
            var se = StateError.Match(t);
            if (se.Success) return new SteamCmdProgress { Error = StateMeaning(se.Groups["code"].Value), Retryable = RetryableState(se.Groups["code"].Value) };
            var ie = InstallError.Match(t);
            if (ie.Success)
            {
                string why = ie.Groups["why"].Value.ToLowerInvariant();
                return new SteamCmdProgress { Error = ReasonMeaning(ie.Groups["why"].Value), Retryable = why.Contains("missing configuration") || why.Contains("timeout") || why.Contains("no connection") };
            }
            if (t.IndexOf("FAILED (No Connection)", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Login Failure", StringComparison.OrdinalIgnoreCase) >= 0
                || (t.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase) && t.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0))
                return new SteamCmdProgress { Error = T("SteamCMD could not connect to Steam. Check the internet connection and try again.") };
            var us = UpdateState.Match(t);
            if (us.Success)
            {
                string what = us.Groups["what"].Value.Trim().ToLowerInvariant();
                double.TryParse(us.Groups["p"].Value, NumberStyles.Float, inv, out double p);
                long.TryParse(us.Groups["a"].Value, NumberStyles.Integer, inv, out long a);
                long.TryParse(us.Groups["b"].Value, NumberStyles.Integer, inv, out long b);
                string stage = what.StartsWith("download") ? N("Downloading")
                             : what.StartsWith("verif") || what.StartsWith("validat") ? N("Checking files")
                             : what.StartsWith("commit") ? N("Installing")
                             : what.StartsWith("prealloc") ? N("Making room on the disk")
                             : what.StartsWith("stag") ? N("Unpacking")
                             : N("Preparing");
                return new SteamCmdProgress { Stage = stage, Percent = Math.Max(0, Math.Min(100, p)), Done = a, Total = b };
            }
            var su = SelfUpdate.Match(t);
            if (su.Success)
            {
                double? pct = su.Groups["p"].Success && double.TryParse(su.Groups["p"].Value, NumberStyles.Float, inv, out double sp) ? sp : (double?)null;
                return new SteamCmdProgress { Stage = N("Updating SteamCMD"), Percent = pct };
            }
            if (t.StartsWith("Connecting anonymously", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Logging in", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Waiting for", StringComparison.OrdinalIgnoreCase))
                return new SteamCmdProgress { Stage = N("Connecting to Steam") };
            return null;
        }

        /// <summary>The state codes SteamCMD ends a failed update with (the common ones), in plain words.</summary>
        public static string StateMeaning(string hex)
        {
            switch ((hex ?? "").ToLowerInvariant().TrimStart('0'))
            {
                case "202": return T("Not enough free space on the disk for the server. Free some space, or pick a folder on another disk.");
                case "212": case "402": return T("The download did not finish. Press it again: SteamCMD carries on where it stopped.");
                case "602": case "606": case "2": case "6": return T("Steam's servers did not answer in time. Try again in a few minutes.");
                case "1": return T("SteamCMD could not start the download. Try again, or pick another folder.");
                default: return F("SteamCMD stopped with state 0x{0}. Try again; if it keeps happening, pick another folder.", hex);
            }
        }

        /// <summary>States a second run usually gets past: the download stopped, Steam's servers were slow.</summary>
        private static bool RetryableState(string hex)
        {
            string h = (hex ?? "").ToLowerInvariant().TrimStart('0');
            return h == "212" || h == "402" || h == "602" || h == "606" || h == "2" || h == "6" || h == "1";
        }

        public static string ReasonMeaning(string why)
        {
            string w = (why ?? "").Trim().ToLowerInvariant();
            // Seen live on a PC's very first SteamCMD run: it has no information about the app yet; the next run has.
            if (w.Contains("missing configuration")) return T("SteamCMD had no information about the server yet. Press it again.");
            if (w.Contains("disk write") || w.Contains("disk read")) return T("SteamCMD could not write the server's files: the folder may be read-only, or the disk full.");
            if (w.Contains("no connection") || w.Contains("timeout") || w.Contains("time out")) return T("SteamCMD could not connect to Steam. Check the internet connection and try again.");
            if (w.Contains("disk space")) return T("Not enough free space on the disk for the server. Free some space, or pick a folder on another disk.");
            return F("SteamCMD could not install the server ({0}). Try again.", why);
        }

        // ------------------------------------------------------------------ running it

        /// <summary>The arguments that install or update the server into a folder.</summary>
        public static string ServerArgs(string dir, bool validate) =>
            "+force_install_dir \"" + dir.TrimEnd('\\') + "\" +login anonymous +app_update " + ServerInstall.SteamAppId + (validate ? " validate" : "") + " +quit";

        /// <summary>
        /// Installs or updates the server into <paramref name="dir"/>. Downloads SteamCMD first when needed. Blocking parts
        /// run on worker threads; the progress callback gets every step. Cancelling stops SteamCMD (a later run goes on
        /// where it stopped).
        /// </summary>
        public static async Task<SteamCmdResult> InstallServer(string dir, bool validate, IProgress<SteamCmdProgress> progress, CancellationToken ct)
        {
            try
            {
                string problem = CheckFolder(dir);
                if (problem != null) return new SteamCmdResult { Error = problem };
                Directory.CreateDirectory(dir);
                // One left from a launcher that closed during an install would get in the way.
                if (IsRunning) StopAll();
                if (!File.Exists(Exe))
                {
                    progress?.Report(new SteamCmdProgress { Stage = N("Downloading SteamCMD") });
                    string error = await Task.Run(() => Download(ct), ct);
                    if (error != null) return new SteamCmdResult { Error = error };
                }
                // SteamCMD updates itself on its first start and then starts again; a run of its own for that keeps the
                // server install from being cut short when the first process ends.
                progress?.Report(new SteamCmdProgress { Stage = N("Updating SteamCMD") });
                var prep = await Run("+quit", progress, ct);
                if (prep.Cancelled) return prep;
                AppLog.Info("SteamCMD: " + (validate ? "installing and checking" : "installing") + " the server in " + dir);
                var result = await Run(ServerArgs(dir, validate), progress, ct);
                // A first SteamCMD run often fails once ("Missing configuration"), and Steam can be slow: one more try.
                if (!result.Ok && !result.Cancelled && result.Retryable)
                {
                    AppLog.Info("SteamCMD: trying once more (" + result.Error + ")");
                    progress?.Report(new SteamCmdProgress { Stage = N("Trying again") });
                    result = await Run(ServerArgs(dir, validate), progress, ct);
                }
                if (result.Ok) AppLog.Info("SteamCMD: server " + (result.UpToDate ? "already up to date" : "installed") + " in " + dir);
                else if (!result.Cancelled) AppLog.Warn("SteamCMD: " + result.Error);
                return result;
            }
            catch (OperationCanceledException) { StopAll(); return new SteamCmdResult { Cancelled = true, Error = T("Stopped.") }; }
            catch (Exception ex) { AppLog.Error("SteamCMD failed", ex); return new SteamCmdResult { Error = F("SteamCMD failed: {0}", ex.Message) }; }
        }

        /// <summary>Why a folder cannot take the server (null = fine): not a full path, a system folder, or not writable.</summary>
        public static string CheckFolder(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return T("Pick a folder for the server first.");
            string full;
            try { full = Path.GetFullPath(dir.Trim()); } catch { return T("That is not a folder path."); }
            if (!Path.IsPathRooted(dir.Trim()) || full.Length <= 3) return T("Pick a folder for the server, not a whole drive.");
            if (full.IndexOf('"') >= 0) return T("That is not a folder path.");
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (full.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || full.Equals(win, StringComparison.OrdinalIgnoreCase))
                return T("Pick a folder outside the Windows folder.");
            if (File.Exists(full)) return T("That is a file, not a folder.");
            // A folder that holds something else (not a server) is not used: SteamCMD would mix its files in. A server, or an
            // install that was stopped or failed (SteamCMD's own steamapps folder is there), is carried on.
            try
            {
                if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !ServerInstall.IsServerDir(full)
                    && !Directory.Exists(Path.Combine(full, "steamapps")))
                    return T("That folder has other files in it. Pick an empty folder (or a new one) for the server.");
                Directory.CreateDirectory(full);
                string probe = Path.Combine(full, ".launcher-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch (Exception ex) { return F("The folder cannot be written: {0}", ex.Message); }
            return null;
        }

        private static async Task<SteamCmdResult> Run(string args, IProgress<SteamCmdProgress> progress, CancellationToken ct)
        {
            var result = new SteamCmdResult();
            var last = DateTime.UtcNow;
            var info = new ProcessStartInfo(Exe, args)
            {
                WorkingDirectory = Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string error = null;
            bool success = false, upToDate = false, retryable = false;
            void Line(string l)
            {
                if (l == null) return;
                last = DateTime.UtcNow;
                // Its output in the session log, for problem reports (an anonymous login: nothing private in it).
                if (l.Trim().Length > 0) AppLog.Debug("SteamCMD: " + l.Trim());
                var p = ParseLine(l);
                if (p == null) return;
                // After "Success" SteamCMD only closes (its lines then would show an old step again).
                if (success && !p.Success && p.Error == null) return;
                if (p.Error != null && error == null) { error = p.Error; retryable = p.Retryable; }
                if (p.Success) { success = true; upToDate = p.UpToDate; }
                progress?.Report(p);
            }
            using (var proc = new Process { StartInfo = info, EnableRaisingEvents = true })
            {
                proc.OutputDataReceived += (s, e) => Line(e.Data);
                proc.ErrorDataReceived += (s, e) => Line(e.Data);
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                while (!proc.HasExited)
                {
                    if (ct.IsCancellationRequested) { StopAll(); throw new OperationCanceledException(ct); }
                    if (DateTime.UtcNow - last > StallLimit) { StopAll(); return new SteamCmdResult { Error = T("SteamCMD stopped answering. Try again; it carries on where it stopped.") }; }
                    await Task.Delay(250);
                }
                // The output is read to its end (SteamCMD's own restart after updating itself writes to the same pipe).
                await Task.Run(() => proc.WaitForExit());
                result.Ok = error == null && (success || args == "+quit");
                result.UpToDate = upToDate;
                result.Retryable = retryable;
                result.Error = result.Ok ? null : error ?? F("SteamCMD ended without finishing (exit code {0}). Try again.", proc.ExitCode);
            }
            // After updating itself SteamCMD may still be finishing in a second process of its own.
            for (int i = 0; i < 120 && IsRunning; i++)
            {
                if (ct.IsCancellationRequested) { StopAll(); throw new OperationCanceledException(ct); }
                await Task.Delay(500);
            }
            return result;
        }

        /// <summary>SteamCMD processes started from the launcher's copy.</summary>
        private static IEnumerable<Process> Running()
        {
            foreach (var p in Process.GetProcessesByName("steamcmd"))
            {
                string exe = null;
                try { exe = p.MainModule?.FileName; } catch { }
                if (exe != null && exe.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase)) yield return p;
                else p.Dispose();
            }
        }

        /// <summary>Stops the launcher's SteamCMD (a cancelled install); never another SteamCMD on the PC.</summary>
        public static void StopAll()
        {
            foreach (var p in Running().ToList())
                using (p) try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { AppLog.Warn("SteamCMD stop: " + ex.Message); }
        }

        /// <summary>Asked every half second after a run: the process handles are closed right away.</summary>
        public static bool IsRunning
        {
            get
            {
                var list = Running().ToList();
                foreach (var p in list) p.Dispose();
                return list.Count > 0;
            }
        }

        // ------------------------------------------------------------------ getting it

        /// <summary>Downloads steamcmd.zip from Valve, unpacks steamcmd.exe and checks it is signed by Valve. Null = done.</summary>
        private static string Download(CancellationToken ct)
        {
            byte[] zip;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(DownloadUrl);
                req.UserAgent = "SandstormModLauncher";
                req.Timeout = 30000;
                req.ReadWriteTimeout = 60000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var m = new MemoryStream())
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        m.Write(buf, 0, n);
                        if (m.Length > MaxZipBytes) return T("The SteamCMD download is not what was expected (too large).");
                    }
                    zip = m.ToArray();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return F("SteamCMD could not be downloaded from Valve: {0}", ex.Message); }

            byte[] exe = null;
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null || entry.Length > MaxZipBytes) return T("The SteamCMD download is not what was expected (no steamcmd.exe).");
                    using (var s = entry.Open())
                    using (var m = new MemoryStream()) { s.CopyTo(m); exe = m.ToArray(); }
                }
            }
            catch (InvalidDataException) { return T("The SteamCMD download is damaged. Try again."); }

            Directory.CreateDirectory(Dir);
            string tmp = Exe + ".download";
            File.WriteAllBytes(tmp, exe);
            string signer = ValveSigned(tmp);
            if (signer != null)
            {
                TryDelete(tmp);
                return signer;
            }
            if (File.Exists(Exe)) File.Delete(Exe);
            File.Move(tmp, Exe);
            AppLog.Info("SteamCMD downloaded from Valve (" + exe.Length / 1024 + " KB, signature checked)");
            return null;
        }

        public static readonly HashSet<string> ValveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Valve Corp.", "Valve Corporation", "Valve" };

        /// <summary>Null when the file carries a valid signature by Valve, else why not.</summary>
        public static string ValveSigned(string path)
        {
            if (!VerifySignature(path)) return T("The SteamCMD download has no valid signature, so it was not used.");
            try
            {
                var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                string name = cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
                // Valve's own name only (steam.exe: "Valve Corp."): any certificate with "Valve" somewhere in it passed before.
                if (!ValveNames.Contains(name.Trim())) return F("The SteamCMD download is signed by {0}, not Valve, so it was not used.", name);
            }
            catch (Exception ex) { return F("The signature of the SteamCMD download could not be read: {0}", ex.Message); }
            return null;
        }

        private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>Windows' own check of a file's Authenticode signature (no window, no revocation lookup).</summary>
        private static bool VerifySignature(string path)
        {
            var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                    dwProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL: no network
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustData)));
                Marshal.StructureToPtr(data, pData, false);
                return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, pData) == 0;
            }
            catch (Exception ex) { AppLog.Warn("Signature check: " + ex.Message); return false; }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
            }
        }
    }
}
