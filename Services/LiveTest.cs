using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using SandstormModLauncher.ViewModels;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// --live-test script.txt out.txt: drives the real game through the launcher's own launch and console code
    /// (screen-verified input) to check game behaviour. Only for when the player says the game is free.
    /// Script lines:
    ///   launch &lt;ScenarioId&gt; [Cls.Key=Value ...] [slots=N] [night]   full launch (writes Game.ini, starts the game if needed)
    ///   console &lt;command | command&gt;                              run console commands, log the answer
    ///   wait &lt;seconds&gt;                                             sleep
    ///   waitround [seconds] [state]                                wait for the round (pre-round or active, or the given state)
    ///   count [label]                                              count players and bots per team
    ///   key &lt;F1|Enter|...&gt;                                         press one key in the game (only when it is in front)
    ///   rcon &lt;command&gt;                                             one RCON command, the reply is logged
    ///   prop &lt;Name&gt; [value] [expect=value]                         game mode property over RCON (FAIL when not as expected)
    ///   start                                                      start the game without a match and wait for the main menu
    ///   expect &lt;seconds&gt; [!]&lt;text&gt;                                a game log line since the last launch/start/rcon/console/key step
    ///                                                              (or after the line the previous expect found) contains the text
    ///                                                              (with !: no such line within the time); FAIL otherwise
    ///   dilation [value]                                           the loaded map's game speed (Slomo); FAIL when not the value
    ///   (launch also takes: reload, cheats, after=Cmd+arg;Cmd2 for the player's own after-load commands)
    ///   ui-live &lt;button label&gt;                                    a Live tab button, through the launcher's own (hidden) window
    ///   ui-console &lt;command | command&gt;                           the Live tab's console box
    ///   ui-read / ui-count                                         the Live tab's "Read current values" / "Count bots"
    ///   ui-apply Key=Value ...                                     the Live tab's rule boxes and "Apply now"
    ///   quit                                                       close the game (over RCON)
    /// launch takes "console" to allow typing into the game console; without it the launch must work over RCON alone.
    /// </summary>
    public static class LiveTest
    {
        public static async Task Run(string scriptFile, string outFile)
        {
            var log = new StringBuilder();
            void Out(string s)
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + s;
                log.AppendLine(line);
                AppLog.Info("LiveTest: " + s);
                try { File.WriteAllText(outFile, log.ToString()); } catch { }
            }
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            state.Official = GameCatalog.Load(state.Install, AppPaths.CacheDir, m => { });
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, AppPaths.CacheDir, m => { });
            state.Rebuild();
            state.Settings.AutoStartGame = true;
            state.Settings.RestartPolicy = "Always";
            var monitor = new GameMonitor();
            var console = new ConsoleBridge(monitor, () => state.Settings, () => state.Official);
            var rcon = new GameRcon(() => state.Settings);
            var launcher = new LaunchService(state, monitor, console, rcon);
            string gameIni = GameInstall.GameIniPath;
            string savedIni = File.Exists(gameIni) ? File.ReadAllText(gameIni) : null;
            Out("Game.ini saved for restore (" + (savedIni?.Length ?? 0) + " chars)");
            // Game log lines, for "expect": searched from the start of the last step that makes the game do something.
            var seen = new List<string>();
            int mark = 0;
            monitor.LineReceived += l => { lock (seen) seen.Add(l); };
            try
            {
                foreach (var raw in File.ReadAllLines(scriptFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sp = line.IndexOf(' ');
                    string cmd = sp < 0 ? line : line.Substring(0, sp), arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                    Out("> " + line);
                    monitor.Poll();
                    if (new[] { "launch", "start", "rcon", "console", "key", "ui-live", "ui-console", "ui-apply", "ui-read", "ui-count" }.Contains(cmd.ToLowerInvariant())) lock (seen) mark = seen.Count;
                    try
                    {
                        switch (cmd.ToLowerInvariant())
                        {
                            case "launch": await Launch(arg, state, launcher, rcon, Out); break;
                            case "start":
                            {
                                if (monitor.IsRunning) { Out("  already running"); break; }
                                launcher.StartOnly(null);
                                var until = DateTime.UtcNow.AddSeconds(180);
                                while (DateTime.UtcNow < until && monitor.Phase != GamePhase.Menu) { await Task.Delay(500); monitor.Poll(); }
                                Out("  phase " + monitor.Phase);
                                break;
                            }
                            case "expect":
                            {
                                int sp2 = arg.IndexOf(' ');
                                double max = double.Parse(arg.Substring(0, sp2), System.Globalization.CultureInfo.InvariantCulture);
                                string text = arg.Substring(sp2 + 1);
                                bool absent = text.StartsWith("!", StringComparison.Ordinal);
                                if (absent) text = text.Substring(1);
                                // The game's echo of an RCON command ("LogRcon: ... << ...") holds the command's own text: never a match.
                                int Found() { lock (seen) { for (int i = mark; i < seen.Count; i++) if (seen[i].Contains(text) && !seen[i].Contains("LogRcon: ")) return i; } return -1; }
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                while (sw.Elapsed.TotalSeconds < max && Found() < 0) { await Task.Delay(250); monitor.Poll(); }
                                int at = Found();
                                string hit = null;
                                if (at >= 0) lock (seen) hit = seen[at];
                                // Expectations are met in order: the next one looks after this line.
                                if (at >= 0 && !absent) mark = at + 1;
                                string shown = hit == null ? null : LogSanitizer.Clean(Regex.Replace(hit, @"^\[[^\]]*\]\[[^\]]*\]", "")) ?? "<hidden>";
                                if (absent) Out(hit == null ? "  OK not seen in " + max + " s" : "  FAIL seen: " + shown);
                                else if (hit != null) Out("  OK after " + sw.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s: " + shown);
                                else
                                {
                                    // What the log gave meanwhile tells a quiet game from a log that is not being read.
                                    string last;
                                    int since;
                                    lock (seen) { since = seen.Count - mark; last = seen.Count > 0 ? seen[seen.Count - 1] : ""; }
                                    Out("  FAIL not seen in " + max + " s: " + text + "  (" + since + " log lines since; last: " +
                                        (LogSanitizer.Clean(last) ?? "<hidden>").Substring(0, Math.Min(120, (LogSanitizer.Clean(last) ?? "<hidden>").Length)) + ")");
                                }
                                break;
                            }
                            case "console":
                            {
                                var res = await console.Run(arg, CancellationToken.None, null, TimeSpan.FromSeconds(3));
                                Out(res.Sent ? (res.NotRecognized ? "  not recognised: " + res.Detail : "  sent") : "  NOT SENT: " + res.Detail);
                                foreach (var l in res.Lines.Where(l => !LogSanitizer.IsSensitive(l)).Select(l => Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", ""))
                                                            .Where(l => l.Contains(" = ") || l.Contains("not recognized") || l.Contains("LogGameMode") || l.Contains("Bot") || l.Contains("bot")).Take(40))
                                    Out("  | " + LogSanitizer.Clean(l));
                                break;
                            }
                            case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture))); break;
                            case "waitround":
                            {
                                // waitround [seconds] [state]: without a state, pre-round counts too
                                var w = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                double max = w.Length > 0 ? double.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture) : 60;
                                string want = w.Length > 1 ? w[1] : null;
                                var until = DateTime.UtcNow.AddSeconds(max);
                                bool Reached() => want != null ? monitor.RoundState == want : monitor.RoundState == "RoundActive" || monitor.RoundState == "PreRound";
                                while (DateTime.UtcNow < until && !Reached()) { await Task.Delay(500); monitor.Poll(); }
                                Out("  round state: " + (monitor.RoundState ?? "none") + ", phase " + monitor.Phase);
                                break;
                            }
                            case "count": Count(rcon, arg, Out); break;
                            case "dilation":
                            {
                                // The game speed of the loaded map (Slomo), from its WorldSettings: dilation [expected value]
                                string reply = rcon.Run(GameRcon.Quote("getall WorldSettings TimeDilation"))[0];
                                var m = Regex.Match(reply, @"/Game/Maps/(\w+)/\1\.\1:PersistentLevel\.\w+\.TimeDilation = (-?\d+\.\d{6})");
                                string now = m.Success ? m.Groups[2].Value : "(not found)";
                                Out("  game speed " + now + (arg.Length > 0 ? (SameNumber(now, arg) ? "  (as expected)" : "  FAIL expected " + arg) : ""));
                                break;
                            }
                            case "rcon":
                            {
                                var replies = rcon.Run(arg);
                                foreach (var l in replies[0].Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(40))
                                    Out("  | " + (LogSanitizer.Clean(l) ?? "<hidden>"));
                                break;
                            }
                            case "prop":
                            {
                                var w = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                string expect = w.FirstOrDefault(x => x.StartsWith("expect=", StringComparison.Ordinal))?.Substring(7);
                                var parts = w.Where(x => !x.StartsWith("expect=", StringComparison.Ordinal)).ToList();
                                if (parts.Count >= 2)
                                {
                                    var failed = rcon.SetProperties(new[] { new KeyValuePair<string, string>(parts[0], parts[1]) });
                                    Out(failed.Count == 0 ? "  set " + parts[0] + " = " + parts[1] : "  FAIL set " + parts[0] + ": " + failed.Values.First());
                                }
                                var values = rcon.ReadProperties(parts[0], out var modeCls);
                                string now = values.TryGetValue(parts[0], out var v) ? v : "(none)";
                                Out("  " + modeCls + "." + parts[0] + " = " + now + (expect != null ? (now == expect ? "  (as expected)" : "  FAIL expected " + expect) : ""));
                                break;
                            }
                            case "key":
                            {
                                var input = new GameInput(monitor.Window);
                                ushort vk = arg.Equals("Enter", StringComparison.OrdinalIgnoreCase) ? (ushort)0x0D : input.VirtualKeyFor(arg);
                                if (vk == 0 || !input.Focus(3000)) { Out("  key not sent"); break; }
                                await Task.Delay(500);
                                input.Tap(vk);
                                Out("  pressed " + arg);
                                break;
                            }
                            case "ui-live":
                            {
                                var vm = await Ui(Out);
                                var action = vm.LiveGroups.SelectMany(g => g.Actions).FirstOrDefault(a => a.Label.Equals(arg, StringComparison.OrdinalIgnoreCase));
                                if (action == null) { Out("  FAIL no Live button called " + arg); break; }
                                ShowOutput(await UiRun(vm, vm.RunLiveActionCommand, action), Out);
                                break;
                            }
                            case "ui-console":
                            {
                                var vm = await Ui(Out);
                                vm.CustomCommand = arg;
                                ShowOutput(await UiRun(vm, vm.SendCustomCommand, null), Out);
                                break;
                            }
                            case "ui-read":
                            {
                                var vm = await Ui(Out);
                                ShowOutput(await UiRun(vm, vm.ReadLiveRulesCommand, null), Out);
                                Out("  " + string.Join(", ", vm.LiveRules.Select(r => r.Key + "=" + r.Current)));
                                break;
                            }
                            case "ui-apply":
                            {
                                // ui-apply Key=Value ... : the Live tab's rule boxes, then "Apply now" (restarts the round when that box is ticked)
                                var vm = await Ui(Out);
                                foreach (var kv in arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')))
                                {
                                    var rule = vm.LiveRules.FirstOrDefault(r => r.Key.Equals(kv[0], StringComparison.OrdinalIgnoreCase));
                                    if (rule == null) Out("  FAIL no live rule " + kv[0]); else rule.Value = kv[1];
                                }
                                ShowOutput(await UiRun(vm, vm.ApplyLiveRulesCommand, null), Out);
                                // "Apply now" reads the values back when it is done.
                                for (int k = 0; k < 50 && (vm.LiveBusy || !vm.LiveOutput.StartsWith("Read", StringComparison.Ordinal)); k++) await Task.Delay(200);
                                foreach (var r in vm.LiveRules.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
                                    Out("  " + r.Key + ": set " + r.Value + ", game has " + r.Current + (SameNumber(r.Value, r.Current) ? "  (as expected)" : "  FAIL"));
                                foreach (var r in vm.LiveRules) r.Value = "";
                                break;
                            }
                            case "ui-count":
                            {
                                var vm = await Ui(Out);
                                ShowOutput(await UiRun(vm, vm.CountBotsCommand, null), Out);
                                break;
                            }
                            case "quit": await launcher.StopGame(CancellationToken.None); Out("  game closed"); break;
                            default: Out("  unknown step"); break;
                        }
                    }
                    catch (Exception ex) { Out("  ERROR " + ex.GetType().Name + ": " + ex.Message); }
                    monitor.Poll();
                }
            }
            finally
            {
                try
                {
                    if (System.Diagnostics.Process.GetProcessesByName(GameInstall.ClientProcess).Length == 0 && savedIni != null)
                    {
                        File.WriteAllText(gameIni, savedIni);
                        Out("Game.ini restored");
                    }
                    else Out("Game.ini NOT restored (game still running)");
                }
                catch (Exception ex) { Out("Game.ini restore failed: " + ex.Message); }
                monitor.Dispose();
                Out("done");
            }
        }

        private static async Task Launch(string arg, AppState state, LaunchService launcher, GameRcon rcon, Action<string> Out)
        {
            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool allowConsole = parts.Contains("console");
            var p = new Profile { Name = "LiveTest", ScenarioId = parts[0], MaxPlayers = 8, Lighting = "Day", MutatorsEnabled = false };
            state.Settings.SoloGameFlag = !parts.Contains("nosolo");
            foreach (var kv in parts.Skip(1))
            {
                if (kv == "night") { p.Lighting = "Night"; continue; }
                if (kv == "nosolo" || kv == "console") continue;
                if (kv.StartsWith("mutators=")) { p.Mutators = kv.Substring(9).Split(',').ToList(); p.MutatorsEnabled = true; continue; }
                if (kv.StartsWith("*.")) { var gv = kv.Substring(2).Split('='); if (!p.Rules.TryGetValue("*", out var g)) p.Rules["*"] = g = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); g[gv[0]] = gv[1]; continue; }
                if (kv == "hardcore") { p.Hardcore = true; continue; }
                if (kv == "reload") { p.ForceReload = true; continue; }
                if (kv == "cheats") { p.EnableCheatsAfterLoad = true; continue; }
                // after=Cmd+arg;Cmd2 : the player's own after-load commands ("+" for a space, ";" between lines)
                if (kv.StartsWith("after=")) { p.AfterLoadCommands = kv.Substring(6).Replace('+', ' ').Replace(';', '\n'); continue; }
                if (kv.StartsWith("slots=")) { p.MaxPlayers = int.Parse(kv.Substring(6)); continue; }
                int dot = kv.IndexOf('.'), eq = kv.IndexOf('=');
                if (dot < 0 || eq < dot) continue;
                string cls = kv.Substring(0, dot), key = kv.Substring(dot + 1, eq - dot - 1), val = kv.Substring(eq + 1);
                if (!p.Rules.TryGetValue(cls, out var d)) p.Rules[cls] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                d[key] = val;
            }
            var map = state.Maps.FirstOrDefault(m => m.Scenarios.Any(s => s.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase)));
            p.MapKey = map?.Key;
            var plan = LaunchPlanner.Build(p, state);
            Out("  plan: " + plan.OpenCommand + (plan.Error != null ? " ERROR " + plan.Error : ""));
            if (plan.GameIniBlock.Length > 0) Out("  ini:\r\n" + plan.GameIniBlock);
            var progress = new Progress<LaunchUpdate>(u => { if (u.State != StepState.Active) Out("  step " + (u.Step + 1) + " " + u.State + ": " + u.Detail); });
            if (plan.LiveProperties.Count > 0) Out("  after load (RCON): " + string.Join(", ", plan.LiveProperties.Select(kv => kv.Key + "=" + kv.Value)));
            if (plan.ConsoleOnly.Count > 0) Out("  after load (console): " + string.Join(" | ", plan.ConsoleOnly));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = await launcher.Run(plan, new LaunchOptions { RestartIfNeeded = true, AllowConsole = allowConsole, BringToFront = false }, progress, CancellationToken.None);
            Out("  took " + (int)sw.Elapsed.TotalSeconds + " s");
            Out("  launch " + (report.Success ? "OK" : "FAILED: " + report.Message));
            foreach (var w in report.Warnings) Out("  warning: " + w);
        }

        private static MainViewModel uiVm;

        /// <summary>The launcher's real main window and view model (never shown), for steps that go through the Live tab's own code.</summary>
        private static async Task<MainViewModel> Ui(Action<string> Out)
        {
            if (uiVm != null) return uiVm;
            var win = new Views.MainWindow();
            var vm = (MainViewModel)win.DataContext;
            var root = (System.Windows.FrameworkElement)win.Content;
            win.Content = null;
            root.DataContext = vm;
            var host = new System.Windows.Controls.Border { Child = root };
            host.Measure(new System.Windows.Size(1536, 864));
            host.Arrange(new System.Windows.Rect(0, 0, 1536, 864));
            await vm.InitializeAsync();
            for (int k = 0; k < 600 && vm.Loading; k++) await Task.Delay(100);
            if (vm.DialogOpen) vm.DialogCommand.Execute(vm.DialogPrimary);
            Out("  launcher window ready (not shown)");
            return uiVm = vm;
        }

        /// <summary>Runs one of the view model's commands like a click (once it is allowed) and waits for the Live output it writes.</summary>
        private static async Task<string> UiRun(MainViewModel vm, System.Windows.Input.ICommand cmd, object param)
        {
            var until = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < until && !cmd.CanExecute(param)) await Task.Delay(250);
            if (!cmd.CanExecute(param)) return null;
            vm.LiveOutput = "";
            cmd.Execute(param);
            await Task.Delay(300);
            while (DateTime.UtcNow < until && (vm.LiveBusy || vm.LiveOutput.Length == 0))
            {
                if (vm.DialogOpen) vm.DialogCommand.Execute(vm.DialogPrimary);
                await Task.Delay(200);
            }
            await Task.Delay(300);
            return vm.LiveOutput;
        }

        private static void ShowOutput(string output, Action<string> Out)
        {
            if (output == null) { Out("  FAIL the button could not be used (no match running?)"); return; }
            foreach (var l in output.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(20))
                Out("  | " + (LogSanitizer.Clean(l) ?? "<hidden>"));
        }

        private static bool SameNumber(string a, string b) =>
            a == b || (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                       double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-4);

        private static void Count(GameRcon rcon, string label, Action<string> Out)
        {
            var teams = rcon.CountPlayers();
            Out("  COUNT " + label + ": " + (teams.Count == 0 ? "no player states" : string.Join(" | ", teams.Select(t => "team " + t.Key + ": " + t.Value.humans + " human, " + t.Value.bots + " bots"))));
        }
    }
}
