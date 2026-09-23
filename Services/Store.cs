using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher
{
    public static class AppPaths
    {
        public static string ExeDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        /// <summary>%APPDATA%\SandstormModLauncher, or a Data folder next to the exe when a "portable" file exists.</summary>
        private static string overrideDir;

        /// <summary>Keeps all launcher data in another folder (command line --data, used for testing).</summary>
        public static void UseDataDir(string dir) => overrideDir = Path.GetFullPath(dir);

        /// <summary>True in test runs (--data): the game's own files are then never written.</summary>
        public static bool TestRun => overrideDir != null;

        public static string DataDir
        {
            get
            {
                if (overrideDir != null) return overrideDir;
                if (File.Exists(Path.Combine(ExeDir, "portable")) || File.Exists(Path.Combine(ExeDir, "portable.txt")))
                    return Path.Combine(ExeDir, "Data");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SandstormModLauncher");
            }
        }

        public static string CacheDir => Path.Combine(DataDir, "cache");
        public static string ProfilesDir => Path.Combine(DataDir, "profiles");
        public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    }
}

namespace SandstormModLauncher.Services
{
    /// <summary>Loads and saves settings and profiles as JSON with atomic writes.</summary>
    public sealed class Store
    {
        public AppSettings Settings { get; private set; } = new AppSettings();
        public List<Profile> Profiles { get; } = new List<Profile>();
        public bool IsLoaded { get; private set; }

        public void Load()
        {
            IsLoaded = true;
            Directory.CreateDirectory(AppPaths.DataDir);
            Directory.CreateDirectory(AppPaths.ProfilesDir);
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    Settings = Json.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8)) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings unreadable, starting fresh", ex);
                TryQuarantine(AppPaths.SettingsFile);
                Settings = new AppSettings();
            }
            Profiles.Clear();
            foreach (var f in Directory.GetFiles(AppPaths.ProfilesDir, "*.json"))
            {
                try
                {
                    var p = Json.Deserialize<Profile>(File.ReadAllText(f, Encoding.UTF8));
                    if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    Normalize(p);
                    Profiles.Add(p);
                }
                catch (Exception ex) { AppLog.Warn("Profile " + Path.GetFileName(f) + " unreadable: " + ex.Message); TryQuarantine(f); }
            }
            if (Profiles.Count == 0) Profiles.Add(new Profile { Name = "Default" });
            Profiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (!Profiles.Any(p => p.Name == Settings.ActiveProfile)) Settings.ActiveProfile = Profiles[0].Name;
            Settings.ExtraModFolders = Settings.ExtraModFolders ?? new List<string>();
            Settings.CustomMutators = Settings.CustomMutators ?? new List<string>();
            Settings.MutatorPresets = Settings.MutatorPresets ?? new List<MutatorPreset>();
            Settings.CustomMaps = Settings.CustomMaps ?? new List<CustomMapEntry>();
            Settings.RulesPresets = Settings.RulesPresets ?? new List<RulesPreset>();
        }

        private static void Normalize(Profile p)
        {
            p.Mutators = p.Mutators ?? new List<string>();
            p.Rules = p.Rules ?? new Dictionary<string, Dictionary<string, string>>();
            var fixedRules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in p.Rules)
                if (kv.Value != null) fixedRules[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            p.Rules = fixedRules;
            if (p.MaxPlayers <= 0) p.MaxPlayers = 8;
            if (string.IsNullOrEmpty(p.Lighting)) p.Lighting = "Day";
        }

        public Profile Active => Profiles.FirstOrDefault(p => p.Name == Settings.ActiveProfile) ?? Profiles[0];

        public void SaveSettings() => WriteAtomic(AppPaths.SettingsFile, Json.Serialize(Settings));

        public void SaveProfile(Profile p) => WriteAtomic(ProfilePath(p.Name), Json.Serialize(p));

        public void DeleteProfile(Profile p)
        {
            Profiles.Remove(p);
            try { File.Delete(ProfilePath(p.Name)); } catch { }
            if (Profiles.Count == 0) Profiles.Add(new Profile { Name = "Default" });
        }

        public void RenameProfile(Profile p, string newName)
        {
            string old = ProfilePath(p.Name);
            p.Name = newName;
            SaveProfile(p);
            try { if (!string.Equals(old, ProfilePath(newName), StringComparison.OrdinalIgnoreCase)) File.Delete(old); } catch { }
        }

        public static string ProfilePath(string name)
        {
            var safe = new StringBuilder();
            foreach (char c in name) safe.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return Path.Combine(AppPaths.ProfilesDir, safe.ToString().Trim() + ".json");
        }

        private static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { AppLog.Error("Could not save " + Path.GetFileName(path), ex); }
        }

        private static void TryQuarantine(string path)
        {
            try { File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
    }
}
