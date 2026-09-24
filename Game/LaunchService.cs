using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    public enum StepState { Pending, Active, Done, Skipped, Failed, Warning }

    public sealed class LaunchUpdate
    {
        public int Step;
        public StepState State;
        public string Detail;
    }

    public sealed class LaunchOptions
    {
        public bool RestartIfNeeded;
        public bool ForceRestart;
    }

    public sealed class LaunchReport
    {
        public bool Success;
        public string Message;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>Runs a launch end to end. The player never has to touch the console.</summary>
    public sealed class LaunchService
    {
        public static readonly string[] Steps =
        {
            "Check mods, mutators and the scenario",
            "Write match rules to Game.ini",
            "Start Insurgency: Sandstorm",
            "Wait for the main menu and mods",
            "Send the match to the game",
            "Load the map",
            "Apply live settings",
        };

        private readonly AppState state;
        private readonly GameMonitor monitor;
        private readonly ConsoleBridge console;

        public LaunchService(AppState state, GameMonitor monitor, ConsoleBridge console)
        {
            this.state = state;
            this.monitor = monitor;
            this.console = console;
        }

        /// <summary>True when the game is running with older start-only rules than the plan needs.</summary>
        public bool NeedsRestart(LaunchPlan plan)
        {
            if (!monitor.IsRunning) return false;
            string active = ActiveRestartKey();
            return active != plan.RestartKey;
        }

        /// <summary>Restart fingerprint the running game was started with.</summary>
        public string ActiveRestartKey()
        {
            var s = state.Settings;
            if (monitor.ProcessStartUtc.HasValue && s.LastRulesWriteUtc != default && s.LastRulesWriteUtc <= monitor.ProcessStartUtc.Value)
                return s.LastWrittenRulesHash;
            if (s.LastRulesWriteUtc == default) return LaunchPlanner.RestartKeyFor(new Profile(), state.Rules);
            return s.GameStartedWithRulesHash ?? "unknown";
        }

        public async Task<LaunchReport> Run(LaunchPlan plan, LaunchOptions options, IProgress<LaunchUpdate> progress, CancellationToken ct)
        {
            var report = new LaunchReport();
            var clock = Stopwatch.StartNew();
            string lastLogged = null;
            void Report(int step, StepState st, string detail = null)
            {
                progress?.Report(new LaunchUpdate { Step = step, State = st, Detail = detail });
                string entry = $"Launch step {step + 1} {st}: {detail ?? Steps[step]}";
                // Waiting loops report every half second; keep the log readable.
                if (st != StepState.Active || entry != lastLogged) AppLog.Debug($"{entry} ({clock.ElapsedMilliseconds} ms)");
                lastLogged = entry;
            }
            int current = 0;
            AppLog.Info("Launch: " + plan.Title + " | game " + monitor.Phase + (monitor.IsRunning ? " pid " + monitor.ProcessId : "") +
                        " | restart " + (options.ForceRestart ? "forced" : options.RestartIfNeeded ? "if needed" : "no"));
            AppLog.Info("Launch command: " + plan.OpenCommand);
            if (plan.AfterLoad.Count > 0) AppLog.Info("Launch after-load: " + string.Join(" | ", plan.AfterLoad));
            try
            {
                // 1. Validate
                Report(0, StepState.Active);
                if (!plan.IsValid) throw new LaunchException(plan.Error ?? "Nothing to launch.");
                if (!state.Install.IsValid) throw new LaunchException("Insurgency: Sandstorm was not found. Set the game folder in Settings.");
                report.Warnings.AddRange(plan.Warnings);
                Report(0, plan.MissingMutators.Count > 0 ? StepState.Warning : StepState.Done,
                    $"{plan.Mutators.Count} mutator{(plan.Mutators.Count == 1 ? "" : "s")}" + (plan.MissingMutators.Count > 0 ? $", {plan.MissingMutators.Count} missing" : ""));

                // 2. Game.ini: only while the game is closed. A running game keeps its own copy in memory and
                // writes that back when it changes map or exits, which would undo the change.
                current = 1;
                Report(1, StepState.Active);
                bool startedNow = false;
                if (GameProcessRunning()) Report(1, StepState.Skipped, "The game is running: the rules are sent after the map loads, and Game.ini is updated before the next start");
                else Report(1, StepState.Done, WriteGameIni(plan));

                // 3. Start / restart
                current = 2;
                bool restart = monitor.IsRunning && (options.ForceRestart || (options.RestartIfNeeded && NeedsRestart(plan)));
                if (restart)
                {
                    Report(2, StepState.Active, "Restarting so the new AI teammate count applies");
                    await StopGame(ct);
                }
                if (!monitor.IsRunning)
                {
                    if (!state.Settings.AutoStartGame && !restart)
                        throw new LaunchException("The game is not running. Start Insurgency: Sandstorm, or turn on \"Start the game automatically\" in Settings.");
                    bool steamCold = state.Install.Store != "Epic" && Process.GetProcessesByName("steam").Length == 0;
                    Report(2, StepState.Active, steamCold ? "Starting Steam, then the game" : "Starting through " + state.Install.Store);
                    if (restart) WriteGameIni(plan); // the closing game has just rewritten it
                    PrepareConsoleKey();
                    StartGame(plan);
                    startedNow = true;
                    state.Settings.GameStartedWithRulesHash = plan.RestartKey;
                    var sw = Stopwatch.StartNew();
                    while (!monitor.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(Math.Max(120, state.Settings.StartTimeoutSec)))
                    {
                        await Task.Delay(500, ct);
                        monitor.Poll();
                        if (sw.Elapsed.TotalSeconds >= 20 && (int)sw.Elapsed.TotalSeconds % 5 == 0)
                            Report(2, StepState.Active, "Waiting for the game to start (" + (int)sw.Elapsed.TotalSeconds + " s). If Steam shows a dialog, answer it.");
                    }
                    if (!monitor.IsRunning) throw new LaunchException("The game did not start. Check that " + (state.Install.Store == "Epic" ? "the Epic Games Launcher" : "Steam") + " is running and the game is installed.");
                    Report(2, StepState.Done, "Started in " + (int)sw.Elapsed.TotalSeconds + " s");
                }
                else Report(2, StepState.Skipped, "Already running");

                // 4. Wait for the menu (or accept an existing match)
                current = 3;
                Report(3, StepState.Active);
                var wait = Stopwatch.StartNew();
                while (monitor.Phase != GamePhase.Menu && monitor.Phase != GamePhase.InMatch)
                {
                    if (!monitor.IsRunning) throw new LaunchException("The game closed while starting.");
                    if (wait.Elapsed > TimeSpan.FromSeconds(state.Settings.StartTimeoutSec))
                        throw new LaunchException("Timed out waiting for the main menu. If a dialog or news popup is open in the game, close it and press Launch again.");
                    Report(3, StepState.Active, monitor.ModsMounted > 0 ? monitor.ModsMounted + " mods mounted" : "Waiting...");
                    await Task.Delay(500, ct);
                    monitor.Poll();
                }
                // The main menu sets up its widgets (and the keyboard focus) for a few seconds after it shows.
                if (wait.Elapsed > TimeSpan.FromSeconds(2)) await Task.Delay(3000, ct);
                while (monitor.Window == IntPtr.Zero && wait.Elapsed < TimeSpan.FromSeconds(state.Settings.StartTimeoutSec)) { await Task.Delay(300, ct); monitor.Poll(); }
                Report(3, StepState.Done, monitor.Phase == GamePhase.InMatch ? "In a match - it will switch maps" : (monitor.ModsMounted + " mods mounted"));

                // 5. Send the open command
                current = 4;
                var keyPlan = console.PlanKey();
                if (!keyPlan.Ok) throw new LaunchException(keyPlan.Problem);
                Report(4, StepState.Active, "Console key " + keyPlan.KeyName + (state.Settings.InputMethod == "Type" ? " · typing" : " · paste"));
                string scenarioId = plan.Scenario.Id;
                string level = plan.Scenario.Level.Substring(plan.Scenario.Level.LastIndexOf('/') + 1);
                Func<string, bool> browsed = l => l.Contains("LogNet: Browse:") && l.IndexOf(scenarioId, StringComparison.OrdinalIgnoreCase) >= 0;
                var loadLines = new List<string>();
                void Collect(string l) { lock (loadLines) loadLines.Add(l); }
                monitor.LineReceived += Collect;
                try
                {
                    // Never re-sent automatically: a second try could land while the first one is still loading.
                    var sent = await console.Run(plan.OpenCommand, ct, browsed, TimeSpan.FromSeconds(12));
                    if (!sent.Sent && !sent.NothingTyped)
                    {
                        // The command may be sitting on the console line: the player can press Enter in the game.
                        Report(4, StepState.Active, sent.Detail + " If the command is in the game's console, press Enter there; waiting a minute for it.");
                        var until = DateTime.UtcNow.AddSeconds(60);
                        while (DateTime.UtcNow < until && !SnapshotHas(loadLines, browsed)) { await Task.Delay(250, ct); monitor.Poll(); }
                        if (SnapshotHas(loadLines, browsed)) { AppLog.Info("The open command was run by hand after the console send failed"); sent.Sent = true; sent.Verified = true; }
                    }
                    if (!sent.Sent) throw new LaunchException(sent.Detail ?? "The command could not be sent.");
                    if (!sent.Verified)
                    {
                        if (sent.NotRecognized) throw new LaunchException(sent.Detail);
                        // The map may still be on its way; the next step waits for it.
                        Report(4, StepState.Warning, "Sent; the game has not confirmed it yet");
                    }
                    else Report(4, StepState.Done, "Accepted by the game");

                    // 6. Wait for the map
                    current = 5;
                    Report(5, StepState.Active, "Loading " + (plan.Map?.DisplayName ?? plan.Level));
                    await WaitForMap(level, loadLines, sent.Verified ? 240 : 20, ct);
                    await WaitForLoadingScreen(ct);
                    List<string> snapshot;
                    lock (loadLines) snapshot = new List<string>(loadLines);
                    report.Warnings.AddRange(MutatorWarnings(plan, snapshot));
                    Report(5, report.Warnings.Count > plan.Warnings.Count ? StepState.Warning : StepState.Done, "Map loaded");

                    if (plan.Profile.ForceReload)
                    {
                        Report(5, StepState.Active, "Force reload: loading again");
                        lock (loadLines) loadLines.Clear();
                        var again = await console.Run(plan.OpenCommand, ct, browsed, TimeSpan.FromSeconds(12));
                        if (!again.Sent) throw new LaunchException("The reload was not sent: " + again.Detail);
                        await WaitForMap(level, loadLines, 240, ct);
                        await WaitForLoadingScreen(ct);
                        Report(5, StepState.Done, "Map loaded (reloaded)");
                    }
                }
                finally { monitor.LineReceived -= Collect; }

                // 7. After-load commands
                current = 6;
                // A game started by this launch already read the rules from Game.ini; sending them again is not needed.
                var afterLoad = startedNow ? plan.AfterLoad.Where(c => !c.StartsWith("AdminSetGamemodeProperty ", StringComparison.OrdinalIgnoreCase)).ToList() : plan.AfterLoad;
                if (afterLoad.Count > 0)
                {
                    Report(6, StepState.Active, afterLoad.Count + " command(s)");
                    var res = await console.Run(string.Join(" | ", afterLoad), ct, null, TimeSpan.FromSeconds(3));
                    if (!res.Sent) { report.Warnings.Add("Live settings were not applied: " + res.Detail); Report(6, StepState.Warning, res.Detail); }
                    else
                    {
                        var bad = res.Lines.Where(l => l.Contains("Command not recognized")).Select(l => l.Substring(l.IndexOf("Command not recognized", StringComparison.Ordinal))).ToList();
                        foreach (var b in bad) report.Warnings.Add(b);
                        Report(6, bad.Count > 0 ? StepState.Warning : StepState.Done, bad.Count > 0 ? bad.Count + " not recognised" : "Applied");
                    }
                }
                else Report(6, StepState.Skipped, startedNow && plan.AfterLoad.Count > 0 ? "Rules were read from Game.ini at game start" : "Nothing to apply");

                report.Success = true;
                report.Message = "You're in. Have a good fight.";
                return report;
            }
            catch (OperationCanceledException)
            {
                Report(current, StepState.Failed, "Cancelled");
                report.Message = "Launch cancelled.";
                return report;
            }
            catch (LaunchException ex)
            {
                Report(current, StepState.Failed, ex.Message);
                report.Message = ex.Message;
                AppLog.Warn("Launch failed: " + ex.Message);
                return report;
            }
            catch (Exception ex)
            {
                Report(current, StepState.Failed, ex.Message);
                report.Message = "Unexpected error: " + ex.Message;
                AppLog.Error("Launch crashed", ex);
                return report;
            }
        }

        /// <summary>The loading picture stays up for a few seconds after the map is loaded; the console cannot open under it.</summary>
        private async Task WaitForLoadingScreen(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(500, ct);
            monitor.Poll();
            while (monitor.LoadingScreenUp && sw.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (!monitor.IsRunning) throw new LaunchException("The game closed while loading the map.");
                await Task.Delay(250, ct);
                monitor.Poll();
            }
            AppLog.Debug("Loading screen " + (monitor.LoadingScreenUp ? "still up after 45 s" : "gone") + " after " + sw.ElapsedMilliseconds + " ms");
            await Task.Delay(1200, ct);
            monitor.Poll();
        }

        private static bool SnapshotHas(List<string> lines, Func<string, bool> match)
        {
            lock (lines) return lines.Any(match);
        }

        private bool GameProcessRunning() => monitor.IsRunning || Process.GetProcessesByName(GameInstall.ClientProcess).Length > 0;

        /// <summary>Waits until the log says the level finished loading (lines already collected count too).</summary>
        private async Task WaitForMap(string level, List<string> lines, int timeoutSec, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSec))
            {
                List<string> snapshot;
                lock (lines) snapshot = new List<string>(lines);
                if (snapshot.Any(l => l.Contains("seconds to LoadMap(") && l.IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0)) return;
                string fail = snapshot.FirstOrDefault(l => l.Contains("TravelFailure") || l.Contains("Travel Failure") || l.Contains("LoadMap failed"));
                if (fail != null) throw new LaunchException("The game could not load the map: " + Regex.Replace(fail, @"^\[[^\]]*\]\[[^\]]*\]", "").Trim());
                if (!monitor.IsRunning) throw new LaunchException("The game closed while loading the map.");
                await Task.Delay(250, ct);
                monitor.Poll();
            }
            throw new LaunchException(timeoutSec < 100
                ? "The game did not start loading the map. Look at the game window: if the console is still open with the command in it, press Enter there; otherwise press Launch again."
                : "The map did not finish loading. Check the game window for an error message.");
        }

        private string WriteGameIni(LaunchPlan plan)
        {
            var p = plan.Profile;
            string path = GameInstall.GameIniPath;
            string current = UeIni.ReadText(path);
            var db = state.Rules;
            // Extra lines the player added last time are the launcher's too, so removing them from the profile removes them here.
            var earlier = new HashSet<string>(state.Settings.ManagedIniKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            string updated = string.Equals(p.CustomIniMode, "Replace", StringComparison.OrdinalIgnoreCase)
                ? UeIni.Render(plan.IniSections) + "\r\n"
                : UeIni.MergeSections(current, plan.IniSections, (s, k) => LaunchPlanner.IsManagedIniKey(db, s, k) || earlier.Contains(s + "\n" + k));
            int sections = plan.IniSections.Count(s => s.Values.Count > 0);
            if (Normalize(updated) != Normalize(current))
            {
                ConsoleBridge.BackupFile(path);
                UeIni.WriteText(path, updated);
                AppLog.Info("Game.ini updated (" + sections + " section(s), " + UeIni.Split(current).Count + " -> " + UeIni.Split(updated).Count + " lines)");
                AppLog.Debug("Game.ini rules:\r\n" + plan.GameIniBlock);
            }
            state.Settings.ManagedIniKeys = plan.IniSections.SelectMany(s => s.Values.Where(v => !LaunchPlanner.IsManagedIniKey(db, s.Name, v.Key)).Select(v => s.Name + "\n" + v.Key))
                                                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.Settings.LastWrittenRulesHash = plan.RestartKey;
            state.Settings.LastRulesWriteUtc = DateTime.UtcNow;
            return plan.Overrides.Count == 0 && sections == 0 ? "Game defaults" : $"{plan.Overrides.Count} change{(plan.Overrides.Count == 1 ? "" : "s")} for {plan.ModeTitle}";
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        /// <summary>Writes the rules (so AI teammates apply) and starts the game without loading a match.</summary>
        public void StartOnly(LaunchPlan plan)
        {
            if (monitor.IsRunning) return;
            if (plan != null && plan.IsValid) WriteGameIni(plan);
            PrepareConsoleKey();
            StartGame(plan);
            state.Settings.GameStartedWithRulesHash = plan?.RestartKey ?? LaunchPlanner.RestartKeyFor(new Profile(), state.Rules);
        }

        /// <summary>Makes sure a function key opens the console before the game starts (the ` key is missing on many layouts).</summary>
        private void PrepareConsoleKey()
        {
            if (!state.Settings.AutoConsoleKey) return;
            KeyBindings.Backup("before starting the game");
            string key = ConsoleBridge.EnsureLayoutFreeKey(state.Official, state.Settings, Process.GetProcessesByName(GameInstall.ClientProcess).Length > 0);
            if (key != null) AppLog.Debug("Console key for this start: " + key);
        }

        private void StartGame(LaunchPlan plan)
        {
            AppLog.Info("Starting the game (" + state.Install.Store + ")");
            var install = state.Install;
            var args = new List<string>();
            string ruleset = plan?.Profile?.LaunchRuleset;
            if (!string.IsNullOrWhiteSpace(ruleset)) args.Add("-ruleset=" + ruleset.Trim());
            if (!string.IsNullOrWhiteSpace(state.Settings.LaunchArgs)) args.Add(state.Settings.LaunchArgs.Trim());
            string argLine = string.Join(" ", args);
            if (install.Store == "Epic" && install.EpicAppName != null)
            {
                Process.Start(new ProcessStartInfo("com.epicgames.launcher://apps/" + install.EpicAppName + "?action=launch&silent=true") { UseShellExecute = true });
                return;
            }
            if (install.SteamExe != null && File.Exists(install.SteamExe))
            {
                Process.Start(new ProcessStartInfo(install.SteamExe, "-applaunch " + GameInstall.SteamAppId + (argLine.Length > 0 ? " " + argLine : "")) { UseShellExecute = false });
                return;
            }
            Process.Start(new ProcessStartInfo("steam://rungameid/" + GameInstall.SteamAppId) { UseShellExecute = true });
        }

        public async Task StopGame(CancellationToken ct)
        {
            var p = Process.GetProcessesByName(GameInstall.ClientProcess).FirstOrDefault();
            if (p == null) return;
            try { p.CloseMainWindow(); } catch { }
            for (int i = 0; i < 60 && !p.HasExited; i++) await Task.Delay(500, ct);
            if (!p.HasExited)
            {
                await console.Run("exit", ct, null, TimeSpan.FromSeconds(1));
                for (int i = 0; i < 40 && !p.HasExited; i++) await Task.Delay(500, ct);
            }
            if (!p.HasExited) throw new LaunchException("The game did not close. Close it yourself and press Launch again.");
            await Task.Delay(1500, ct);
            monitor.Poll();
        }

        private static IEnumerable<string> MutatorWarnings(LaunchPlan plan, List<string> lines)
        {
            var list = new List<string>();
            foreach (var m in plan.MutatorInfos)
            {
                string pkg = m.AssetPath?.Split('.')[0];
                if (pkg == null) continue;
                if (lines.Any(l => l.Contains("LogStreaming: Error") && l.IndexOf(pkg, StringComparison.OrdinalIgnoreCase) >= 0))
                    list.Add(m.DisplayName + " reported a loading error on this game version, so it may not work (the mod probably needs an update).");
            }
            foreach (var l in lines.Where(l => l.IndexOf("mutator", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                               (l.Contains("Warning") || l.Contains("Error")) && !l.Contains("LogStreaming")).Take(5))
                list.Add(Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", ""));
            return list.Distinct();
        }
    }

    public sealed class LaunchException : Exception
    {
        public LaunchException(string message) : base(message) { }
    }
}
