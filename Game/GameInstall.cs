using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    /// <summary>Where the game, its configs, logs and mods live on this PC.</summary>
    public sealed class GameInstall
    {
        public const string SteamAppId = "581320";
        public const string ModioGameId = "254";
        public const string ClientProcess = "InsurgencyClient-Win64-Shipping";

        public string GameDir { get; private set; }
        public string Store { get; private set; } = "Unknown";
        public string SteamExe { get; private set; }
        public string EpicAppName { get; private set; }
        public string BuildId { get; private set; } = "";

        public string PaksDir => GameDir == null ? null : Path.Combine(GameDir, "Insurgency", "Content", "Paks");
        public string ClientExe => GameDir == null ? null : Path.Combine(GameDir, "Insurgency", "Binaries", "Win64", ClientProcess + ".exe");
        public static string SavedDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Insurgency", "Saved");
        public static string ConfigDir => Path.Combine(SavedDir, "Config", "WindowsClient");
        public static string GameIniPath => Path.Combine(ConfigDir, "Game.ini");
        public static string InputIniPath => Path.Combine(ConfigDir, "Input.ini");
        public static string EngineIniPath => Path.Combine(ConfigDir, "Engine.ini");
        public static string LogPath => Path.Combine(SavedDir, "Logs", "Insurgency.log");

        public static string ModioRoot
        {
            get
            {
                string pub = Environment.GetEnvironmentVariable("PUBLIC");
                if (string.IsNullOrEmpty(pub)) pub = @"C:\Users\Public";
                return Path.Combine(pub, "mod.io", ModioGameId);
            }
        }

        public string LegacyModioDir => GameDir == null ? null : Path.Combine(GameDir, "Insurgency", "Mods", "modio");

        public bool IsValid => GameDir != null && Directory.Exists(PaksDir);

        public static GameInstall Detect(string overrideDir)
        {
            var g = new GameInstall();
            g.SteamExe = FindSteamExe();

            if (!string.IsNullOrWhiteSpace(overrideDir) && IsGameDir(overrideDir))
            {
                g.GameDir = Path.GetFullPath(overrideDir);
                g.Store = "Manual";
                g.ReadSteamManifest();
                return g;
            }

            foreach (var lib in SteamLibraries(g.SteamExe))
            {
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + SteamAppId + ".acf");
                string installDir = "sandstorm";
                if (File.Exists(manifest))
                {
                    var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) installDir = m.Groups[1].Value;
                }
                string dir = Path.Combine(lib, "steamapps", "common", installDir);
                if (IsGameDir(dir))
                {
                    g.GameDir = dir;
                    g.Store = "Steam";
                    g.ReadSteamManifest(manifest);
                    return g;
                }
            }

            var epic = FindEpicInstall();
            if (epic.dir != null)
            {
                g.GameDir = epic.dir;
                g.EpicAppName = epic.app;
                g.Store = "Epic";
                return g;
            }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "sandstorm"),
                    Path.Combine(drive.RootDirectory.FullName, "Steam", "steamapps", "common", "sandstorm"),
                    Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam", "steamapps", "common", "sandstorm"),
                    Path.Combine(drive.RootDirectory.FullName, "Games", "sandstorm"),
                })
                {
                    if (IsGameDir(candidate)) { g.GameDir = candidate; g.Store = "Steam"; return g; }
                }
            }
            return g;
        }

        public static bool IsGameDir(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "Insurgency", "Content", "Paks")); }
            catch { return false; }
        }

        private void ReadSteamManifest(string manifest = null)
        {
            try
            {
                if (manifest == null && GameDir != null)
                {
                    string lib = Directory.GetParent(Directory.GetParent(GameDir).FullName)?.FullName;
                    if (lib != null) manifest = Path.Combine(lib, "appmanifest_" + SteamAppId + ".acf");
                }
                if (manifest != null && File.Exists(manifest))
                {
                    var m = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
                    if (m.Success) BuildId = m.Groups[1].Value;
                }
            }
            catch { }
        }

        private static string FindSteamExe()
        {
            foreach (var (hive, key, value) in new[]
            {
                (Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
                (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    using (var k = hive.OpenSubKey(key))
                    {
                        string v = k?.GetValue(value) as string;
                        if (string.IsNullOrEmpty(v)) continue;
                        v = v.Replace('/', '\\');
                        string exe = v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v : Path.Combine(v, "steam.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            string fallback = @"C:\Program Files (x86)\Steam\steam.exe";
            return File.Exists(fallback) ? fallback : null;
        }

        private static IEnumerable<string> SteamLibraries(string steamExe)
        {
            var libs = new List<string>();
            if (steamExe == null) return libs;
            string root = Path.GetDirectoryName(steamExe);
            libs.Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!libs.Any(x => string.Equals(Path.GetFullPath(x).TrimEnd('\\'), Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                            libs.Add(p);
                    }
            }
            catch { }
            return libs;
        }

        private static (string dir, string app) FindEpicInstall()
        {
            try
            {
                string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (!Directory.Exists(manifests)) return (null, null);
                foreach (var f in Directory.GetFiles(manifests, "*.item"))
                {
                    var json = Json.Parse(File.ReadAllText(f));
                    string name = json.Get("DisplayName").Str() ?? "";
                    if (name.IndexOf("Sandstorm", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string dir = json.Get("InstallLocation").Str();
                    if (IsGameDir(dir)) return (dir, json.Get("AppName").Str());
                }
            }
            catch { }
            return (null, null);
        }
    }
}
