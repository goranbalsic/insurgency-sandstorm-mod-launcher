using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    public sealed class KeyBindingsBackup
    {
        public string Path { get; set; }
        public string Profile { get; set; }
        public DateTime Time { get; set; }
        public int Bound { get; set; }
        public string Label => Time.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture) + "  ·  " + Bound + " keys bound" + (Profile == "SteamProfile" ? "" : "  ·  " + Profile);
    }

    /// <summary>
    /// Keeps copies of the game's key bindings (Saved\SaveGames\&lt;profile&gt;\Controls.json) so they can
    /// always be put back. A copy is taken at start-up and before the launcher sends any key to the game.
    /// </summary>
    public static class KeyBindings
    {
        private static readonly object sync = new object();
        private static readonly Regex KeyField = new Regex("\"key\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex Revision = new Regex("(\"revisionTimestamp\"\\s*:\\s*)(\\d+)", RegexOptions.Compiled);

        public static string BackupDir => System.IO.Path.Combine(AppPaths.DataDir, "backups", "controls");
        public static string SaveGamesDir => System.IO.Path.Combine(GameInstall.SavedDir, "SaveGames");

        /// <summary>Set when the number of bound keys dropped sharply between two copies.</summary>
        public static string DropWarning { get; private set; }
        public static event Action DropDetected;

        public static List<string> ControlFiles()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(SaveGamesDir)) return list;
                foreach (var d in Directory.GetDirectories(SaveGamesDir))
                {
                    string f = System.IO.Path.Combine(d, "Controls.json");
                    if (File.Exists(f)) list.Add(f);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Number of keyboard/mouse/gamepad bindings that point at a real key.</summary>
        public static int BoundCount(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            return KeyField.Matches(json).Cast<Match>().Count(m => m.Groups[1].Value.Length > 0 && !m.Groups[1].Value.Equals("None", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsKeyBound(string key)
        {
            foreach (var f in ControlFiles())
            {
                try
                {
                    if (KeyField.Matches(ReadShared(f)).Cast<Match>().Any(m => m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Copies each Controls.json whose content differs from its newest copy. Cheap enough to call often.</summary>
        public static void Backup(string reason)
        {
            lock (sync)
            {
                foreach (var file in ControlFiles())
                {
                    try
                    {
                        string text = ReadShared(file);
                        if (text.Length < 200 || text.IndexOf("actionMappings", StringComparison.Ordinal) < 0) continue;
                        string profile = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file));
                        Directory.CreateDirectory(BackupDir);
                        var newest = CopiesOf(profile).FirstOrDefault();
                        string newestText = newest != null ? File.ReadAllText(newest) : null;
                        if (newestText != null && Hash(newestText) == Hash(text)) continue;
                        int bound = BoundCount(text);
                        string target = System.IO.Path.Combine(BackupDir, profile + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                        File.WriteAllText(target, text, new UTF8Encoding(false));
                        AppLog.Info($"Key bindings copied ({profile}, {bound} keys bound, {reason})");
                        if (newestText != null)
                        {
                            int before = BoundCount(newestText);
                            if (before >= 20 && bound < before * 0.7)
                            {
                                DropWarning = $"Your game key bindings went from {before} to {bound} bound keys (noticed {DateTime.Now:HH:mm}). A copy from before is kept; restore it in Settings > Game key bindings.";
                                AppLog.Warn("Key bindings dropped from " + before + " to " + bound + " bound keys (" + reason + ")");
                                try { DropDetected?.Invoke(); } catch { }
                            }
                        }
                        foreach (var old in CopiesOf(profile).Skip(40)) File.Delete(old);
                    }
                    catch (Exception ex) { AppLog.Warn("Key bindings copy failed: " + ex.Message); }
                }
            }
        }

        public static List<KeyBindingsBackup> List()
        {
            var list = new List<KeyBindingsBackup>();
            try
            {
                if (!Directory.Exists(BackupDir)) return list;
                foreach (var f in Directory.GetFiles(BackupDir, "*-*.json"))
                {
                    if (!ParseCopyName(f, out string profile, out DateTime t)) continue;
                    int bound = 0;
                    try { bound = BoundCount(File.ReadAllText(f)); } catch { }
                    list.Add(new KeyBindingsBackup { Path = f, Profile = profile, Time = t, Bound = bound });
                }
                list.Sort((a, b) => b.Time.CompareTo(a.Time));
            }
            catch { }
            return list;
        }

        /// <summary>Puts a copy back. The game must be closed, because it rewrites the file while it runs.</summary>
        public static string Restore(KeyBindingsBackup b)
        {
            if (b == null || !File.Exists(b.Path)) return "That copy no longer exists.";
            if (Process.GetProcessesByName(GameInstall.ClientProcess).Length > 0) return "Close the game first. It rewrites its key bindings while it runs.";
            string target = System.IO.Path.Combine(SaveGamesDir, b.Profile, "Controls.json");
            try
            {
                Backup("before restoring a copy");
                string text = File.ReadAllText(b.Path);
                // Newer than any other copy, so the game keeps this one.
                text = Revision.Replace(text, m => m.Groups[1].Value + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), 1);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                string tmp = target + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
                DropWarning = null;
                AppLog.Info("Key bindings restored from " + System.IO.Path.GetFileName(b.Path) + " (" + b.Bound + " keys bound)");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Key bindings restore failed", ex);
                return "Could not restore: " + ex.Message;
            }
        }

        public static void ClearWarning() => DropWarning = null;

        /// <summary>Copies are named &lt;profile&gt;-yyyyMMdd-HHmmss.json; the profile folder name may contain dashes itself.</summary>
        private static bool ParseCopyName(string file, out string profile, out DateTime time)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(file);
            profile = null; time = default;
            const int stampLength = 15; // yyyyMMdd-HHmmss
            if (name.Length < stampLength + 2 || name[name.Length - stampLength - 1] != '-') return false;
            if (!DateTime.TryParseExact(name.Substring(name.Length - stampLength), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) return false;
            profile = name.Substring(0, name.Length - stampLength - 1);
            return true;
        }

        /// <summary>This profile's copies, newest first.</summary>
        private static IEnumerable<string> CopiesOf(string profile) =>
            Directory.GetFiles(BackupDir, profile + "-*.json")
                     .Where(f => ParseCopyName(f, out string p, out _) && p.Equals(profile, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) return r.ReadToEnd();
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
        }
    }
}
