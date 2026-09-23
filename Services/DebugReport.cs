using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// Writes everything needed to understand a problem into logs\reports\&lt;time&gt;-&lt;kind&gt;\ :
    /// the launcher logs, settings, the active profile, the game's config files, the end of the
    /// game log (account lines removed) and the console screenshots. Stays on this PC.
    /// </summary>
    public static class DebugReport
    {
        private static DateTime lastAuto = DateTime.MinValue;

        public static string ReportsDir => Path.Combine(AppPaths.DataDir, "logs", "reports");

        /// <summary>Automatic reports (after a failed launch or console send), at most one every two minutes.</summary>
        public static void Auto(string kind, string note, AppState state, GameMonitor monitor)
        {
            if (DateTime.UtcNow - lastAuto < TimeSpan.FromMinutes(2)) return;
            lastAuto = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() => Write(kind, note, state, monitor));
        }

        public static string Write(string kind, string note, AppState state, GameMonitor monitor)
        {
            try
            {
                string dir = Path.Combine(ReportsDir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "-" + kind);
                Directory.CreateDirectory(dir);
                var s = new StringBuilder();
                s.AppendLine("Report: " + kind + "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if (!string.IsNullOrWhiteSpace(note)) s.AppendLine("Note: " + note.Trim());
                s.AppendLine("Launcher: " + typeof(DebugReport).Assembly.GetName().Version.ToString(3) + "   data " + LogSanitizer.Clean(AppPaths.DataDir));
                s.AppendLine("Windows: " + Environment.OSVersion.VersionString + "   .NET " + Environment.Version);
                s.AppendLine("Keyboard now: " + ConsoleBridge.LayoutName(Native.GetKeyboardLayout(0)) + "   installed: " + string.Join(", ", KeyboardLayouts()));
                var install = state?.Install;
                s.AppendLine("Game: " + LogSanitizer.Clean(install?.GameDir ?? "not found") + "   store " + install?.Store + "   build " + install?.BuildId);
                if (monitor != null)
                {
                    s.AppendLine($"Game state: {monitor.Phase}   running {monitor.IsRunning}   window {(monitor.Window != IntPtr.Zero ? "yes" : "no")}   level {monitor.CurrentLevel}   round {monitor.RoundState}   mods mounted {monitor.ModsMounted}");
                    s.AppendLine("Game url: " + LogSanitizer.Clean(monitor.CurrentUrl ?? ""));
                }
                try { s.AppendLine("Console keys: " + string.Join(", ", ConsoleBridge.ConfiguredKeys(state?.Official))); } catch { }
                s.AppendLine("Display: " + string.Join("  ", ReadLines(Path.Combine(GameInstall.ConfigDir, "GameUserSettings.ini"))
                    .Where(l => l.StartsWith("FullscreenMode") || l.StartsWith("LastConfirmedFullscreenMode") || l.StartsWith("ResolutionSize"))));
                var controls = KeyBindings.ControlFiles().Select(f => { try { return Path.GetFileName(Path.GetDirectoryName(f)) + " " + KeyBindings.BoundCount(File.ReadAllText(f)) + " keys bound"; } catch { return f; } });
                s.AppendLine("Key bindings: " + string.Join("; ", controls) + "   copies " + KeyBindings.List().Count + (KeyBindings.DropWarning != null ? "   WARNING " + KeyBindings.DropWarning : ""));
                if (state?.Store?.IsLoaded == true)
                {
                    s.AppendLine();
                    s.AppendLine("== settings.json");
                    s.AppendLine(Json.Serialize(state.Settings, true));
                    s.AppendLine();
                    s.AppendLine("== active profile");
                    s.AppendLine(Json.Serialize(state.Store.Active, true));
                }
                File.WriteAllText(Path.Combine(dir, "summary.txt"), s.ToString(), Encoding.UTF8);

                CopyText(AppLog.SessionPath, Path.Combine(dir, "launcher-session.log"), 4000, false);
                CopyText(AppLog.FilePath, Path.Combine(dir, "launcher-history.log"), 600, false);
                CopyText(GameInstall.GameIniPath, Path.Combine(dir, "Game.ini.txt"), 2000, false);
                CopyText(GameInstall.InputIniPath, Path.Combine(dir, "Input.ini.txt"), 2000, false);
                CopyText(GameInstall.LogPath, Path.Combine(dir, "game-log-tail.txt"), 4000, true);
                // Screenshots of the console line from the last hour.
                if (Directory.Exists(ConsoleBridge.ShotsDir))
                    foreach (var png in Directory.GetFiles(ConsoleBridge.ShotsDir, "*.png").Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddHours(-1)).OrderByDescending(f => f).Take(24))
                    {
                        Directory.CreateDirectory(Path.Combine(dir, "console"));
                        File.Copy(png, Path.Combine(dir, "console", Path.GetFileName(png)), true);
                    }
                foreach (var old in Directory.GetDirectories(ReportsDir).OrderByDescending(d => d).Skip(30))
                    try { Directory.Delete(old, true); } catch { }
                AppLog.Info("Problem report saved: " + Path.GetFileName(dir));
                return dir;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not write the problem report", ex);
                return null;
            }
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return Enumerable.Empty<string>();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs))
                    return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            }
            catch { return Enumerable.Empty<string>(); }
        }

        private static void CopyText(string from, string to, int lastLines, bool sanitize)
        {
            if (string.IsNullOrEmpty(from)) return;
            var lines = ReadLines(from).ToList();
            if (lines.Count == 0) return;
            IEnumerable<string> tail = lines.Skip(Math.Max(0, lines.Count - lastLines));
            if (sanitize) tail = tail.Select(LogSanitizer.Clean).Where(l => l != null);
            File.WriteAllLines(to, tail, Encoding.UTF8);
        }

        private static IEnumerable<string> KeyboardLayouts()
        {
            var list = new List<string>();
            try
            {
                int n = Native.GetKeyboardLayoutList(0, null);
                var arr = new IntPtr[Math.Max(n, 1)];
                n = Native.GetKeyboardLayoutList(arr.Length, arr);
                for (int i = 0; i < n; i++) list.Add(ConsoleBridge.LayoutName(arr[i]));
            }
            catch { }
            return list;
        }
    }
}
