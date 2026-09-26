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
                    s.AppendLine(HidePasswords(Json.Serialize(state.Settings, true)));
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

        // ------------------------------------------------------------------ report for GitHub

        public const string IssueForm = "problem-report.yml";

        /// <summary>
        /// A report that can be posted publicly: versions, settings, the active setup, the launcher's own log lines
        /// and the game's map/mode log lines, all passed through LogSanitizer.Public (no user or PC name, no user
        /// folders, no Steam IDs, IPs, e-mail addresses or account/online lines). No screenshots, no console history.
        /// Short: fits in the GitHub link. Full: goes on the clipboard for the player to paste into the form.
        /// </summary>
        public static void BuildPublic(string note, AppState state, GameMonitor monitor, out string shortText, out string fullText)
        {
            var head = new StringBuilder();
            head.AppendLine("Launcher " + typeof(DebugReport).Assembly.GetName().Version.ToString(3) + " | " + Environment.OSVersion.VersionString + " | .NET " + Environment.Version);
            head.AppendLine("Keyboard: " + ConsoleBridge.LayoutName(Native.GetKeyboardLayout(0)) + " (installed: " + string.Join(", ", KeyboardLayouts()) + ")");
            var install = state?.Install;
            head.AppendLine("Game: " + (install?.IsValid == true ? "found" : "not found") + ", " + install?.Store + " build " + install?.BuildId);
            if (monitor != null)
                head.AppendLine($"Game state: {monitor.Phase}, running {monitor.IsRunning}, window {(monitor.Window != IntPtr.Zero ? "yes" : "no")}, level {monitor.CurrentLevel}, round {monitor.RoundState}, mods mounted {monitor.ModsMounted}");
            try { head.AppendLine("Console keys: " + string.Join(", ", ConsoleBridge.ConfiguredKeys(state?.Official))); } catch { }
            head.AppendLine("Display: " + string.Join("  ", ReadLines(Path.Combine(GameInstall.ConfigDir, "GameUserSettings.ini"))
                .Where(l => l.StartsWith("FullscreenMode") || l.StartsWith("ResolutionSize"))));
            head.AppendLine("Key bindings: " + string.Join("; ", KeyBindings.ControlFiles().Select(f => { try { return KeyBindings.BoundCount(File.ReadAllText(f)) + " keys bound"; } catch { return "?"; } }))
                            + (KeyBindings.DropWarning != null ? " WARNING " + KeyBindings.DropWarning : ""));

            var launcherLines = Clean(ReadLines(AppLog.SessionPath)).ToList();
            // Reported right after a restart of the launcher: the problem is in the previous run's log.
            if (launcherLines.Count(l => l.Contains(" WARN ") || l.Contains(" ERROR ") || l.Contains("Launch")) == 0)
            {
                try
                {
                    string prev = Directory.GetFiles(Path.GetDirectoryName(AppLog.SessionPath), "*.log").OrderBy(f => f)
                                           .LastOrDefault(f => string.CompareOrdinal(f, AppLog.SessionPath) < 0);
                    if (prev != null) launcherLines = Clean(ReadLines(prev)).Concat(new[] { "---- launcher restarted" }).Concat(launcherLines).ToList();
                }
                catch { }
            }
            var history = Clean(ReadLines(AppLog.FilePath)).ToList();
            var problems = history.Where(l => l.Contains(" WARN ") || l.Contains(" ERROR ")).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Launcher problem report (names, user folders, IDs and account lines removed)");
            if (!string.IsNullOrWhiteSpace(note)) sb.AppendLine("Note: " + LogSanitizer.Public(note.Trim()));
            sb.Append(head);
            sb.AppendLine();
            sb.AppendLine("-- recent warnings and errors");
            foreach (var l in problems.Skip(Math.Max(0, problems.Count - 12))) sb.AppendLine(Cut(l, 400));
            sb.AppendLine();
            sb.AppendLine("-- end of this run's log");
            string top = sb.ToString();
            // Fill with the newest log lines while the link stays short enough for GitHub (about 7 KB once encoded).
            var tail = new List<string>();
            int budget = 6000 - Uri.EscapeDataString(top).Length;
            for (int i = launcherLines.Count - 1; i >= 0; i--)
            {
                string l = Cut(launcherLines[i], 400);
                budget -= Uri.EscapeDataString(l).Length + 6;
                if (budget < 0) break;
                tail.Insert(0, l);
            }
            shortText = top + string.Join(Environment.NewLine, tail);

            var full = new StringBuilder();
            full.AppendLine("Launcher problem report, full (names, user folders, IDs and account lines removed)");
            if (!string.IsNullOrWhiteSpace(note)) full.AppendLine("Note: " + LogSanitizer.Public(note.Trim()));
            full.Append(head);
            if (state?.Store?.IsLoaded == true)
            {
                full.AppendLine().AppendLine("== settings");
                full.AppendLine(string.Join(Environment.NewLine, Clean(Json.Serialize(state.Settings, true).Split('\n'))));
                full.AppendLine().AppendLine("== active profile");
                full.AppendLine(string.Join(Environment.NewLine, Clean(Json.Serialize(state.Store.Active, true).Split('\n'))));
            }
            full.AppendLine().AppendLine("== launcher log, this run (last 300 lines)");
            foreach (var l in launcherLines.Skip(Math.Max(0, launcherLines.Count - 300))) full.AppendLine(Cut(l, 600));
            full.AppendLine().AppendLine("== launcher warnings and errors, earlier runs (last 40)");
            foreach (var l in problems.Skip(Math.Max(0, problems.Count - 40))) full.AppendLine(Cut(l, 600));
            full.AppendLine().AppendLine("== Game.ini (game mode and mutator lines)");
            string section = "";
            foreach (var l in ReadLines(GameInstall.GameIniPath))
            {
                if (l.StartsWith("[")) { section = l; continue; }
                if (l.Trim().Length == 0 || section.IndexOf("GameMode", StringComparison.OrdinalIgnoreCase) < 0 && section.IndexOf("Mutator", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string c = LogSanitizer.Public(section + " " + l);
                if (c != null) full.AppendLine(Cut(c, 300));
            }
            full.AppendLine().AppendLine("== Input.ini console keys");
            foreach (var l in ReadLines(GameInstall.InputIniPath).Where(l => l.IndexOf("ConsoleKeys", StringComparison.OrdinalIgnoreCase) >= 0)) full.AppendLine(l.Trim());
            full.AppendLine().AppendLine("== game log: map, mode, loading and command lines (last 80)");
            // Same message again and again (bot quota spam): kept once with a count.
            var game = new List<string>();
            string lastMsg = null;
            int repeats = 0;
            foreach (var l in Clean(ReadLines(GameInstall.LogPath)).Where(l => GameLine.IsMatch(l)))
            {
                string msg = System.Text.RegularExpressions.Regex.Replace(l, @"^(\[[^\]]*\])+", "");
                if (msg == lastMsg) { repeats++; continue; }
                if (repeats > 0) game.Add("    (same line " + repeats + " more times)");
                game.Add(l);
                lastMsg = msg;
                repeats = 0;
            }
            if (repeats > 0) game.Add("    (same line " + repeats + " more times)");
            foreach (var l in game.Skip(Math.Max(0, game.Count - 80))) full.AppendLine(Cut(l, 300));
            fullText = full.ToString();
        }

        // Game log lines about loading maps, game modes, mods and console commands; nothing about the account or network.
        private static readonly System.Text.RegularExpressions.Regex GameLine = new System.Text.RegularExpressions.Regex(
            @"LogLoad|LogGameMode|LogGameState|Browse:|BeginLoadingScreen|EndLoadingScreen|Command not recognized|LoadMap|Mount|LogINSGameMode|LogMutator|Fatal|Error",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static IEnumerable<string> Clean(IEnumerable<string> lines) =>
            lines.Select(l => LogSanitizer.Public(l.TrimEnd('\r'))).Where(l => !string.IsNullOrWhiteSpace(l));

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

        public sealed class NoInboxException : Exception
        {
            public NoInboxException() : base("no report inbox is set up") { }
        }

        /// <summary>
        /// Where reports go: the address in report-endpoint.txt in this project's repository (so the inbox can be set
        /// up or moved without a new release). Only https addresses are used.
        /// </summary>
        public static string InboxAddress()
        {
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://raw.githubusercontent.com/" + Updater.Repo + "/main/report-endpoint.txt");
                req.UserAgent = "SandstormModLauncher";
                req.Timeout = 8000;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream()))
                {
                    string url = r.ReadToEnd().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#"));
                    return url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
                }
            }
            catch (System.Net.WebException ex) when ((ex.Response as System.Net.HttpWebResponse)?.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        }

        /// <summary>
        /// Sends the cleaned report (the same text the player was shown) to the report inbox. Blocking; call it off the
        /// UI thread. Returns the id the inbox gives it. Throws NoInboxException when no inbox is set up.
        /// </summary>
        public static string Send(string note, string shortText, string fullText, string inboxOverride = null)
        {
            string inbox = inboxOverride ?? InboxAddress() ?? throw new NoInboxException();
            string json = Json.Serialize(new Dictionary<string, object>
            {
                ["app"] = "SandstormModLauncher",
                ["version"] = typeof(DebugReport).Assembly.GetName().Version.ToString(3),
                ["note"] = LogSanitizer.Public(note ?? "") ?? "",
                ["summary"] = shortText ?? "",
                ["full"] = (fullText ?? "").Length > 150000 ? fullText.Substring(0, 150000) : fullText ?? "",
            }, false);
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(inbox);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = "SandstormModLauncher/" + typeof(DebugReport).Assembly.GetName().Version.ToString(3);
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.AllowAutoRedirect = true;
            byte[] body = Encoding.UTF8.GetBytes(json);
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream()))
            {
                string answer = r.ReadToEnd();
                var m = System.Text.RegularExpressions.Regex.Match(answer, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                AppLog.Info("Problem report sent to the report inbox (" + body.Length + " bytes)");
                return m.Success ? m.Groups[1].Value : "ok";
            }
        }

        /// <summary>The new-issue link with the form filled in (title, note and the short report).</summary>
        public static string IssueUrl(string note, string shortText)
        {
            string title = "Problem: " + (string.IsNullOrWhiteSpace(note) ? "(describe it)" : Cut(LogSanitizer.Public(note.Trim()) ?? "", 80));
            return "https://github.com/" + Updater.Repo + "/issues/new?template=" + IssueForm +
                   "&title=" + Uri.EscapeDataString(title) +
                   "&what=" + Uri.EscapeDataString(LogSanitizer.Public(note ?? "") ?? "") +
                   "&summary=" + Uri.EscapeDataString(shortText);
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
            IEnumerable<string> tail = lines.Skip(Math.Max(0, lines.Count - lastLines)).Select(HidePasswords);
            if (sanitize) tail = tail.Select(LogSanitizer.Clean).Where(l => l != null);
            File.WriteAllLines(to, tail, Encoding.UTF8);
        }

        private static readonly System.Text.RegularExpressions.Regex PasswordValue =
            new System.Text.RegularExpressions.Regex(@"(?i)(""?\w*Password""?\s*[:=]\s*""?)[^""\r\n\s]*", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Any password value (the RCON password in settings.json and Game.ini) replaced by &lt;hidden&gt;.</summary>
        public static string HidePasswords(string text) => text == null ? null : PasswordValue.Replace(text, "$1<hidden>");

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
