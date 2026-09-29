using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    /// <summary>
    /// The Insurgency: Sandstorm dedicated server on this PC (the free "Insurgency: Sandstorm Dedicated Server" tool
    /// on Steam, app 581330, or a SteamCMD install) and where it keeps its files.
    /// </summary>
    public sealed class ServerInstall
    {
        public const string SteamAppId = "581330";
        public const string ProcessName = "InsurgencyServer-Win64-Shipping";

        public string Root { get; private set; }
        public string BuildId { get; private set; } = "";
        public bool Found => Root != null;

        /// <summary>The start program in the server folder (what the server admin guide starts).</summary>
        public string StartExe => Root == null ? null : Path.Combine(Root, "InsurgencyServer.exe");
        public string ShippingExe => Root == null ? null : Path.Combine(Root, "Insurgency", "Binaries", "Win64", ProcessName + ".exe");
        /// <summary>Game.ini and Engine.ini of the server.</summary>
        public string SavedConfigDir => Root == null ? null : Path.Combine(Root, "Insurgency", "Saved", "Config", "WindowsServer");
        public string GameIniPath => SavedConfigDir == null ? null : Path.Combine(SavedConfigDir, "Game.ini");
        public string EngineIniPath => SavedConfigDir == null ? null : Path.Combine(SavedConfigDir, "Engine.ini");
        /// <summary>Admins.txt, MapCycle.txt, Mods.txt.</summary>
        public string ServerConfigDir => Root == null ? null : Path.Combine(Root, "Insurgency", "Config", "Server");
        public string LogPath => Root == null ? null : Path.Combine(Root, "Insurgency", "Saved", "Logs", "Insurgency.log");

        /// <summary>Installed with SteamCMD into its own folder (it keeps its manifest there): SteamCMD also updates it.
        /// A server in a Steam library is kept up to date by Steam instead.</summary>
        public bool ManagedBySteamCmd => Root != null && File.Exists(OwnManifest(Root));

        /// <summary>A SteamCMD install that was stopped or failed before the end (its manifest is not "fully installed"):
        /// the server's files are not all there yet.</summary>
        public bool Unfinished { get; private set; }

        private static string OwnManifest(string root) => Path.Combine(root, "steamapps", "appmanifest_" + SteamAppId + ".acf");

        /// <summary>The Microsoft Visual C++ runtime files the server needs (the admin guide's prerequisite) that are missing.</summary>
        public static List<string> MissingRuntime()
        {
            var missing = new List<string>();
            try
            {
                string sys = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
                    ? Environment.SystemDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative");
                foreach (var dll in new[] { "vcruntime140.dll", "msvcp140.dll" })
                    if (!File.Exists(Path.Combine(sys, dll))) missing.Add(dll);
            }
            catch { }
            return missing;
        }

        /// <summary>Where the Visual C++ runtime comes from (Microsoft's own download, 64-bit).</summary>
        public const string RuntimeUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        public static bool IsServerDir(string dir)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(dir) &&
                       (File.Exists(Path.Combine(dir, "InsurgencyServer.exe")) || File.Exists(Path.Combine(dir, "Insurgency", "Binaries", "Win64", ProcessName + ".exe")));
            }
            catch { return false; }
        }

        public static ServerInstall At(string dir)
        {
            var s = new ServerInstall();
            if (IsServerDir(dir)) { s.Root = ActualCase(Path.GetFullPath(dir).TrimEnd('\\')); s.ReadBuild(); }
            return s;
        }

        /// <summary>The folder as Windows spells it (Steam's registry keeps some paths in lower case).</summary>
        private static string ActualCase(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                if (di.Parent == null) return di.FullName.ToUpperInvariant();
                string parent = ActualCase(di.Parent.FullName);
                var match = new DirectoryInfo(parent).GetDirectories(di.Name).FirstOrDefault();
                return match?.FullName ?? Path.Combine(parent, di.Name);
            }
            catch { return path; }
        }

        /// <summary>The saved folder when it is valid, else a running server, the Steam libraries and the usual SteamCMD folders.</summary>
        public static ServerInstall Detect(string overrideDir)
        {
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                if (IsServerDir(overrideDir)) return At(overrideDir);
                AppLog.Warn("The saved server folder is not valid any more (" + overrideDir + "), searching automatically");
            }
            string running = RunningServerDir();
            if (running != null) return At(running);
            foreach (var lib in GameInstall.SteamLibraryFolders())
            {
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                string installDir = "sandstorm_server";
                try
                {
                    if (File.Exists(manifest))
                    {
                        var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success) installDir = m.Groups[1].Value;
                    }
                }
                catch { }
                string dir = Path.Combine(lib, "steamapps", "common", installDir);
                if (IsServerDir(dir)) return At(dir);
            }
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId })
                {
                    try
                    {
                        using (var k = hive.OpenSubKey(key))
                        {
                            string dir = k?.GetValue("InstallLocation") as string;
                            if (IsServerDir(dir)) return At(dir);
                        }
                    }
                    catch { }
                }
            // SteamCMD installs (force_install_dir or its own steamapps folder) in the usual places.
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                string r = drive.RootDirectory.FullName;
                foreach (var candidate in new[]
                {
                    Path.Combine(r, "steamcmd", "steamapps", "common", "sandstorm_server"),
                    Path.Combine(r, "SteamCMD", "steamapps", "common", "sandstorm_server"),
                    Path.Combine(r, "sandstorm_server"),
                    Path.Combine(r, "sandstorm-server"),
                    Path.Combine(r, "Servers", "sandstorm_server"),
                    Path.Combine(r, "Servers", "sandstorm"),
                    Path.Combine(r, "SandstormServer"),
                })
                    if (IsServerDir(candidate)) return At(candidate);
            }
            return new ServerInstall();
        }

        private static string RunningServerDir()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(ProcessName))
                {
                    string exe;
                    try { exe = p.MainModule?.FileName; } catch { continue; }
                    // <server>\Insurgency\Binaries\Win64\InsurgencyServer-Win64-Shipping.exe
                    string dir = exe == null ? null : Directory.GetParent(exe)?.Parent?.Parent?.Parent?.FullName;
                    if (IsServerDir(dir)) return dir;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The server process of this install (a server from another folder is not ours).</summary>
        public Process FindProcess()
        {
            if (Root == null) return null;
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    string exe = p.MainModule?.FileName;
                    if (exe != null && exe.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) return p;
                }
                catch { }
            }
            return null;
        }

        private void ReadBuild()
        {
            try
            {
                // SteamCMD keeps the manifest in the server folder; a Steam library two levels up.
                string lib = Directory.GetParent(Directory.GetParent(Root).FullName)?.FullName;
                string manifest = File.Exists(OwnManifest(Root)) ? OwnManifest(Root) : lib == null ? null : Path.Combine(lib, "appmanifest_" + SteamAppId + ".acf");
                if (manifest != null && File.Exists(manifest))
                {
                    string text = File.ReadAllText(manifest);
                    var m = Regex.Match(text, "\"buildid\"\\s+\"(\\d+)\"");
                    if (m.Success) BuildId = m.Groups[1].Value;
                    // StateFlags 4 = fully installed (seen in Steam's and SteamCMD's manifests); without it an install stopped half way.
                    var f = Regex.Match(text, "\"StateFlags\"\\s+\"(\\d+)\"");
                    if (manifest == OwnManifest(Root) && f.Success && int.TryParse(f.Groups[1].Value, out int flags) && (flags & 4) == 0) Unfinished = true;
                }
            }
            catch { }
        }
    }
}
