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
using static SandstormModLauncher.Core.Loc;

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
        /// <summary>The game's console may be typed into when the game cannot be reached over RCON.</summary>
        public bool AllowConsole = true;
        /// <summary>Bring the game to the front once the match is ready (the player launched from the launcher window).</summary>
        public bool BringToFront;
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
            N("Check mods, mutators and the scenario"),
            N("Write match rules to Game.ini"),
            N("Start Insurgency: Sandstorm"),
            N("Wait for the main menu and mods"),
            N("Send the match to the game"),
            N("Load the map"),
            N("Apply live settings"),
        };

        private readonly AppState state;
        private readonly GameMonitor monitor;
        private readonly ConsoleBridge console;
        private readonly GameRcon rcon;

        public LaunchService(AppState state, GameMonitor monitor, ConsoleBridge console, GameRcon rcon)
        {
            this.state = state;
            this.monitor = monitor;
            this.console = console;
            this.rcon = rcon;
        }

        /// <summary>
        /// Why RCON cannot be used right now, or null when the game answers on it. A game that takes the connection
        /// but is busy (loading) counts as reachable: it answers once it is done.
        /// </summary>
        public Task<string> RconProblem() => Task.Run(() => rcon.Probe(out bool busy) is string p && !busy ? p : null);

        /// <summary>Waits until the game answers on RCON (it starts listening while the game starts up; a busy game gets up to a minute).</summary>
        private async Task<string> WaitForRcon(int seconds, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                bool busy = false;
                string problem = await Task.Run(() => rcon.Probe(out busy), ct);
                if (problem == null) return null;
                if (!monitor.IsRunning) return T("the game is not running");
                if (sw.Elapsed > TimeSpan.FromSeconds(busy ? Math.Max(seconds, 60) : seconds)) return problem;
                await Task.Delay(700, ct);
            }
        }

        public static string NoRconMessage(string problem) =>
            F("The launcher cannot reach the game over RCON ({0}). The game was probably started before the launcher set RCON up: " +
              "close it and press Launch again (the launcher starts it with RCON), or allow typing into the game console in Settings.", problem);

        /// <summary>True when the game is running with older start-only rules than the plan needs.</summary>
        public bool NeedsRestart(LaunchPlan plan)
        {
            if (!monitor.IsRunning) return false;
            string active = ActiveRestartKey();
            return active != plan.RestartKey;
        }

        /// <summary>Restart fingerprint the running game was started with.</summary>
        public string ActiveRestartKey() => ActiveRestartKey(state.Settings, monitor.ProcessStartUtc, state.Rules);

        /// <summary>"Mode|Key" of the rules the running game read from Game.ini at its start.</summary>
        public List<string> ActiveRuleKeys() => ActiveRuleKeys(state.Settings, monitor.ProcessStartUtc);

        public static string ActiveRestartKey(AppSettings s, DateTime? processStartUtc, RulesDb db)
        {
            // Started by the launcher: with its rules and its official ruleset (a start argument).
            if (StartedByLauncher(s, processStartUtc) && s.GameStartedWithRulesHash != null) return s.GameStartedWithRulesHash;
            // Started some other way after the last write (Steam, a shortcut): Game.ini's rules, but no ruleset
            // (the ruleset of an earlier launcher start was taken for active, 2026-10-02 audit).
            if (processStartUtc.HasValue && s.LastRulesWriteUtc != default && s.LastRulesWriteUtc <= processStartUtc.Value)
                return s.LastWrittenIniPart != null ? LaunchPlanner.CombineRestartKey(s.LastWrittenIniPart, "") : s.LastWrittenRulesHash;
            if (s.LastRulesWriteUtc == default) return LaunchPlanner.RestartKeyFor(new Profile(), db);
            return s.GameStartedWithRulesHash ?? "unknown";
        }

        public static List<string> ActiveRuleKeys(AppSettings s, DateTime? processStartUtc)
        {
            if (StartedByLauncher(s, processStartUtc) && s.GameStartedWithRuleKeys != null) return s.GameStartedWithRuleKeys;
            if (processStartUtc.HasValue && s.LastRulesWriteUtc != default && s.LastRulesWriteUtc <= processStartUtc.Value)
                return s.LastWrittenRuleKeys ?? new List<string>();
            return s.GameStartedWithRuleKeys ?? new List<string>();
        }

        /// <summary>The running game is the one the launcher started last (its process began within the start wait).</summary>
        public static bool StartedByLauncher(AppSettings s, DateTime? processStartUtc)
        {
            if (!processStartUtc.HasValue || s.GameStartedAtUtc == default) return false;
            return processStartUtc.Value >= s.GameStartedAtUtc.AddSeconds(-5) && processStartUtc.Value <= s.GameStartedAtUtc.AddSeconds(Math.Max(120, s.StartTimeoutSec));
        }

        /// <summary>Remembers what the game the launcher starts now reads at its start.</summary>
        private void NoteGameStart(LaunchPlan plan)
        {
            var s = state.Settings;
            s.GameStartedWithRulesHash = plan?.RestartKey ?? LaunchPlanner.RestartKeyFor(new Profile(), state.Rules);
            s.GameStartedWithRuleKeys = new List<string>(s.LastWrittenRuleKeys ?? new List<string>());
            s.GameStartedAtUtc = DateTime.UtcNow;
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
                if (!plan.IsValid) throw new LaunchException(plan.Error ?? T("Nothing to launch."));
                if (!state.Install.IsValid) throw new LaunchException(T("Insurgency: Sandstorm was not found. Set the game folder in Settings."));
                report.Warnings.AddRange(plan.Warnings);
                Report(0, plan.MissingMutators.Count > 0 ? StepState.Warning : StepState.Done,
                    (plan.Mutators.Count == 1 ? T("1 mutator") : F("{0} mutators", plan.Mutators.Count)) + (plan.MissingMutators.Count > 0 ? ", " + F("{0} missing", plan.MissingMutators.Count) : ""));

                // 2. Game.ini: only while the game is closed. A running game keeps its own copy in memory and
                // writes that back when it changes map or exits, which would undo the change.
                current = 1;
                Report(1, StepState.Active);
                bool startedNow = false;
                if (!GameProcessRunning() && !RconSetup.PortFree(state.Settings.RconPort))
                {
                    int old = state.Settings.RconPort;
                    state.Settings.RconPort = 0;
                    RconSetup.EnsureSettings(state.Settings);
                    AppLog.Info("RCON port " + old + " is used by another program; the launcher uses " + state.Settings.RconPort + " now");
                }
                if (GameProcessRunning()) Report(1, StepState.Skipped, plan.OwnRules ? T("The game is running: it keeps the rules it read from your Game.ini when it started")
                                                                     : T("The game is running: the rules are sent after the map loads, and Game.ini is updated before the next start"));
                else Report(1, StepState.Done, WriteGameIni(plan));

                // 3. Start / restart
                current = 2;
                bool restart = monitor.IsRunning && (options.ForceRestart || (options.RestartIfNeeded && NeedsRestart(plan)));
                if (restart)
                {
                    Report(2, StepState.Active, T("Restarting so the settings read at game start apply"));
                    await StopGame(ct);
                }
                if (!monitor.IsRunning)
                {
                    if (!state.Settings.AutoStartGame && !restart)
                        throw new LaunchException(T("The game is not running. Start Insurgency: Sandstorm, or turn on \"Start the game automatically\" in Settings."));
                    bool steamCold = state.Install.Store != "Epic" && Process.GetProcessesByName("steam").Length == 0;
                    string how = state.Settings.StartWith;
                    Report(2, StepState.Active, how == "Exe" ? T("Starting the game's exe")
                                              : how == "Command" && !string.IsNullOrWhiteSpace(state.Settings.StartCommand) ? T("Starting the game with your own command")
                                              : steamCold ? T("Starting Steam, then the game") : F("Starting through {0}", state.Install.Store));
                    if (restart) WriteGameIni(plan); // the closing game has just rewritten it
                    PrepareConsoleKey();
                    StartGame(plan);
                    startedNow = true;
                    NoteGameStart(plan);
                    var sw = Stopwatch.StartNew();
                    while (!monitor.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(Math.Max(120, state.Settings.StartTimeoutSec)))
                    {
                        await Task.Delay(500, ct);
                        monitor.Poll();
                        if (sw.Elapsed.TotalSeconds >= 20 && (int)sw.Elapsed.TotalSeconds % 5 == 0)
                            Report(2, StepState.Active, F("Waiting for the game to start ({0} s). If Steam shows a dialog, answer it.", (int)sw.Elapsed.TotalSeconds));
                    }
                    if (!monitor.IsRunning) throw new LaunchException(state.Install.Store == "Epic" ? T("The game did not start. Check that the Epic Games Launcher is running and the game is installed.")
                                                                                   : T("The game did not start. Check that Steam is running and the game is installed."));
                    Report(2, StepState.Done, F("Started in {0} s", (int)sw.Elapsed.TotalSeconds));
                }
                else Report(2, StepState.Skipped, T("Already running"));

                // 4. Wait for the menu (or accept an existing match)
                current = 3;
                Report(3, StepState.Active);
                var wait = Stopwatch.StartNew();
                while (monitor.Phase != GamePhase.Menu && monitor.Phase != GamePhase.InMatch)
                {
                    if (!monitor.IsRunning) throw new LaunchException(T("The game closed while starting."));
                    if (wait.Elapsed > TimeSpan.FromSeconds(state.Settings.StartTimeoutSec))
                        throw new LaunchException(T("Timed out waiting for the main menu. If a dialog or news popup is open in the game, close it and press Launch again."));
                    Report(3, StepState.Active, monitor.ModsMounted > 0 ? F("{0} mods mounted", monitor.ModsMounted) : T("Waiting..."));
                    await Task.Delay(500, ct);
                    monitor.Poll();
                }
                // The main menu sets up its widgets (and the keyboard focus) for a few seconds after it shows.
                if (wait.Elapsed > TimeSpan.FromSeconds(2)) await Task.Delay(3000, ct);
                while (monitor.Window == IntPtr.Zero && wait.Elapsed < TimeSpan.FromSeconds(state.Settings.StartTimeoutSec)) { await Task.Delay(300, ct); monitor.Poll(); }
                Report(3, StepState.Done, monitor.Phase == GamePhase.InMatch ? T("In a match - it will switch maps") : F("{0} mods mounted", monitor.ModsMounted));

                // 5. Send the match: over RCON (the game's own remote console), the keyboard console only as a fallback
                current = 4;
                Report(4, StepState.Active, T("Connecting to the game (RCON)"));
                string scenarioId = plan.Scenario.Id;
                string level = plan.Scenario.Level.Substring(plan.Scenario.Level.LastIndexOf('/') + 1);
                Func<string, bool> browsed = l => l.Contains("LogNet: Browse:") && l.IndexOf(scenarioId, StringComparison.OrdinalIgnoreCase) >= 0;
                var loadLines = new List<string>();
                void Collect(string l) { lock (loadLines) loadLines.Add(l); }
                monitor.LineReceived += Collect;
                bool useRcon;
                try
                {
                    string rconProblem = await WaitForRcon(startedNow ? 45 : 3, ct);
                    useRcon = rconProblem == null;
                    CommandResult sent;
                    if (useRcon)
                    {
                        // Never re-sent automatically: a second try could land while the first one is still loading.
                        sent = await Travel(plan, loadLines, browsed, ct);
                    }
                    else
                    {
                        if (!options.AllowConsole) throw new LaunchException(NoRconMessage(rconProblem));
                        var keyPlan = console.PlanKey();
                        if (!keyPlan.Ok) throw new LaunchException(NoRconMessage(rconProblem) + " " + keyPlan.Problem);
                        AppLog.Warn("RCON not available (" + rconProblem + "); using the game console");
                        report.Warnings.Add(F("RCON was not available ({0}), so the command was typed into the game console. Restart the game from the launcher to avoid that.", rconProblem));
                        Report(4, StepState.Active, F("RCON not available: typing into the console ({0})", keyPlan.KeyName));
                        sent = await console.Run(plan.OpenCommand, ct, browsed, TimeSpan.FromSeconds(12));
                        if (!sent.Sent && !sent.NothingTyped)
                        {
                            // The command may be sitting on the console line: the player can press Enter in the game.
                            Report(4, StepState.Active, sent.Detail + " " + T("If the command is in the game's console, press Enter there; waiting a minute for it."));
                            var until = DateTime.UtcNow.AddSeconds(60);
                            while (DateTime.UtcNow < until && !SnapshotHas(loadLines, browsed)) { await Task.Delay(250, ct); monitor.Poll(); }
                            if (SnapshotHas(loadLines, browsed)) { AppLog.Info("The open command was run by hand after the console send failed"); sent.Sent = true; sent.Verified = true; }
                        }
                    }
                    if (!sent.Sent) throw new LaunchException(sent.Detail ?? T("The command could not be sent."));
                    if (!sent.Verified)
                    {
                        // Over RCON the load shows within a moment, and Travel has already waited for it.
                        if (sent.NotRecognized || useRcon) throw new LaunchException(sent.Detail ?? T("The game did not start loading the map."));
                        // The map may still be on its way; the next step waits for it.
                        Report(4, StepState.Warning, T("Sent; the game has not confirmed it yet"));
                    }
                    else Report(4, StepState.Done, useRcon ? T("Loading (sent over RCON)") : T("Accepted by the game"));

                    // 6. Wait for the map
                    current = 5;
                    Report(5, StepState.Active, F("Loading {0}", plan.Map?.DisplayName ?? plan.Level));
                    await WaitForMap(level, loadLines, sent.Verified ? 240 : 20, ct);
                    await WaitForLoadingScreen(ct);
                    List<string> snapshot;
                    lock (loadLines) snapshot = new List<string>(loadLines);
                    report.Warnings.AddRange(MutatorWarnings(plan, snapshot));
                    // The game plays properly only once it has switched from its menu into play (see GameRcon.AsPlayer).
                    if (monitor.InstanceState != null && monitor.InstanceState != "Playing")
                    {
                        AppLog.Warn("After loading, the game state is " + monitor.InstanceState + " instead of Playing");
                        report.Warnings.Add(T("The map loaded, but the game did not switch from its menu into play, so it may keep you on the class screen. If you cannot move, close the game and press Launch again."));
                    }
                    Report(5, report.Warnings.Count > plan.Warnings.Count ? StepState.Warning : StepState.Done, T("Map loaded"));

                    if (plan.Profile.ForceReload)
                    {
                        Report(5, StepState.Active, T("Force reload: loading again"));
                        lock (loadLines) loadLines.Clear();
                        var again = useRcon ? await Travel(plan, loadLines, browsed, ct) : await console.Run(plan.OpenCommand, ct, browsed, TimeSpan.FromSeconds(12));
                        if (!again.Sent) throw new LaunchException(F("The reload was not sent: {0}", again.Detail));
                        if (useRcon && !again.Verified) throw new LaunchException(F("The reload did not start: {0}", again.Detail));
                        await WaitForMap(level, loadLines, again.Verified ? 240 : 20, ct);
                        await WaitForLoadingScreen(ct);
                        Report(5, StepState.Done, T("Map loaded (reloaded)"));
                    }
                }
                finally { monitor.LineReceived -= Collect; }

                if (options.BringToFront) BringToFront();

                // 7. After the map: game mode properties over RCON (a game started by this launch already read them
                // from Game.ini), then the console commands (cheats, the versus AI difficulty, the player's own), run
                // by the game as the player's own, also over RCON.
                current = 6;
                // A game that was already running: also the rules its Game.ini set at start that this match leaves at the default.
                var props = startedNow ? new List<KeyValuePair<string, string>>()
                          : plan.LiveProperties.Concat(state.Settings.ApplyLiveRules ? LaunchPlanner.LiveResets(plan, state.Rules, ActiveRuleKeys()) : Enumerable.Empty<KeyValuePair<string, string>>()).ToList();
                var done = new List<string>();
                var problems = new List<string>();
                if (props.Count > 0)
                {
                    Report(6, StepState.Active, F("{0} setting(s)", props.Count));
                    if (useRcon || await RconProblem() == null)
                    {
                        Dictionary<string, string> failed;
                        try { failed = await Task.Run(() => rcon.SetProperties(props), ct); }
                        catch (RconException ex) { failed = props.ToDictionary(kv => kv.Key, kv => F("no confirmation from the game ({0})", ex.Message)); }
                        foreach (var f in failed) problems.Add(f.Key + ": " + f.Value);
                        done.Add(F("{0} setting(s) set", props.Count - failed.Count));
                        AppLog.Info("RCON properties: " + (props.Count - failed.Count) + " set" + (failed.Count > 0 ? ", not taken: " + string.Join("; ", failed.Select(f => f.Key + " (" + f.Value + ")")) : ""));
                    }
                    else if (options.AllowConsole)
                    {
                        var res = await console.Run(string.Join(" | ", props.Select(kv => "AdminSetGamemodeProperty " + kv.Key + " " + kv.Value)), ct, null, TimeSpan.FromSeconds(3));
                        if (!res.Sent) problems.Add(F("settings not sent: {0}", res.Detail)); else done.Add(F("{0} setting(s) typed into the console", props.Count));
                    }
                    else problems.Add(T("settings not sent: RCON is not available and typing into the console is off"));
                }
                if (plan.ConsoleOnly.Count > 0)
                {
                    var commands = plan.ConsoleOnly.SelectMany(GameRcon.SplitCommands).ToList();
                    string what = string.Join(" | ", commands);
                    if (useRcon || await RconProblem() == null)
                    {
                        // Run by the game as the player's own console commands, over RCON: nothing is typed.
                        Report(6, StepState.Active, F("Console commands over RCON: {0}", what));
                        var lines = new List<string>();
                        void Grab(string l) { lock (lines) lines.Add(l); }
                        monitor.LineReceived += Grab;
                        try
                        {
                            await Task.Run(() => rcon.RunAsPlayer(commands), ct);
                            done.Add(F("{0} console command(s)", commands.Count));
                            AppLog.Info("RCON console commands: " + what);
                            // The versus AI difficulty is the one the game confirms, in its log.
                            string difficulty = commands.LastOrDefault(c => c.StartsWith("AIDifficulty ", StringComparison.OrdinalIgnoreCase))?.Substring(13).Trim();
                            if (difficulty != null && !await WaitForLine(lines, l => DifficultyIs(l, difficulty), 4, ct))
                                problems.Add(F("the game did not confirm the AI difficulty {0}", difficulty));
                        }
                        catch (RconException ex) { problems.Add(F("console commands: no answer from the game ({0})", ex.Message) + (ex.Delivered ? "; " + T("they may still arrive") : "")); }
                        finally { monitor.LineReceived -= Grab; }
                    }
                    else if (!options.AllowConsole) problems.Add(F("the game cannot be reached over RCON, and typing into its console is off in Settings: {0}", what));
                    else if (!options.BringToFront && !new GameInput(monitor.Window).IsForeground) problems.Add(F("the game cannot be reached over RCON, and it was not in front to type into its console: {0}", what));
                    else
                    {
                        Report(6, StepState.Active, F("Typing into the game console: {0}", what));
                        var res = await console.Run(what, ct, null, TimeSpan.FromSeconds(3));
                        if (!res.Sent) problems.Add(F("console: {0}", res.Detail));
                        else
                        {
                            var bad = res.Lines.Where(l => l.Contains("Command not recognized")).Select(l => l.Substring(l.IndexOf("Command not recognized", StringComparison.Ordinal))).ToList();
                            problems.AddRange(bad);
                            done.Add(F("{0} console command(s)", plan.ConsoleOnly.Count));
                        }
                    }
                }
                foreach (var pr in problems) report.Warnings.Add(F("After loading: {0}", pr));
                if (props.Count == 0 && plan.ConsoleOnly.Count == 0)
                    Report(6, StepState.Skipped, startedNow && plan.LiveProperties.Count > 0 ? T("Rules were read from Game.ini at game start") : T("Nothing to apply"));
                else Report(6, problems.Count > 0 ? StepState.Warning : StepState.Done, string.Join(", ", done.Concat(problems.Take(2))));

                report.Success = true;
                report.Message = T("You're in. Have a good fight.");
                return report;
            }
            catch (OperationCanceledException)
            {
                Report(current, StepState.Failed, "Cancelled");
                report.Message = T("Launch cancelled.");
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
                report.Message = F("Unexpected error: {0}", ex.Message);
                AppLog.Error("Launch crashed", ex);
                return report;
            }
        }

        /// <summary>
        /// Loads the map over RCON, confirmed by the game's Browse line. No keys are pressed and the game does not have
        /// to be in front. The console's own open command (a fresh URL), run as the player's console command: only that
        /// makes the game switch from its menu into play (see GameRcon.AsPlayer). For a game already in play, if it does
        /// not start loading, RCON's travel with every option the launcher may have set before spelled out (travel keeps
        /// the options of the map before, e.g. hardcore or the mutators).
        /// </summary>
        private async Task<CommandResult> Travel(LaunchPlan plan, List<string> loadLines, Func<string, bool> browsed, CancellationToken ct)
        {
            var result = new CommandResult();
            try { await Task.Run(() => rcon.Run(GameRcon.AsPlayer(plan.OpenCommand)), ct); }
            catch (RconException ex) when (ex.Delivered)
            {
                // The game answers once the map has loaded (loading freezes it), so a long load outlasts the wait; its log tells.
                AppLog.Debug("RCON open: no answer yet (" + ex.Message + ")");
            }
            catch (RconException ex)
            {
                AppLog.Warn("RCON open failed: " + ex.Message);
                result.Detail = F("The game could not be reached over RCON: {0}", ex.Message);
                result.NothingTyped = true;
                return result;
            }
            result.Sent = true;
            if (await WaitForLine(loadLines, browsed, 12, ct)) { AppLog.Info("RCON open: the game is loading the map"); result.Verified = true; return result; }
            if (!monitor.IsRunning) { result.Detail = T("The game closed."); return result; }
            // Any sign that the open is under way (another Browse line, a loading phase) means waiting, never a second load.
            if (SnapshotHas(loadLines, l => l.Contains("LogNet: Browse:") || l.Contains("LoadMap: ")) || monitor.Phase == GamePhase.Loading || monitor.LoadingScreenUp)
            {
                result.Verified = await WaitForLine(loadLines, browsed, 30, ct);
                if (!result.Verified) result.Detail = T("The game started travelling, but not to the map that was sent.");
                return result;
            }
            // From the menu, RCON's travel would load the map with the game still in its menu state: stuck on the class screen.
            if (monitor.InstanceState != "Playing")
            {
                AppLog.Warn("RCON open gave no map load (game state " + (monitor.InstanceState ?? "unknown") + ")");
                result.Detail = T("The game did not start loading the map. If a message is open in the game, close it and press Launch again.");
                return result;
            }

            string url = plan.TravelUrl + TravelResets(plan);
            var reply = await Task.Run(() => rcon.Travel(url), ct);
            AppLog.Info("RCON open gave no map load; travel: " + (reply.Ok ? reply.Text : reply.Delivered ? "sent, no answer yet" : "failed: " + reply.Error));
            if (!reply.Ok && !reply.Delivered) { result.Detail = F("The game did not take the map over RCON: {0}", reply.Error); return result; }
            result.Verified = await WaitForLine(loadLines, browsed, 20, ct);
            if (!result.Verified) result.Detail = T("The game answered but has not started loading the map.");
            return result;
        }

        /// <summary>True for the game's log line that confirms the AI difficulty (numbers compared as numbers).</summary>
        public static bool DifficultyIs(string line, string wanted)
        {
            const string Marker = "AI difficulty set to ";
            int i = line?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
            if (i < 0) return false;
            string got = line.Substring(i + Marker.Length).Trim();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var fl = System.Globalization.NumberStyles.Float;
            return double.TryParse(got, fl, inv, out var a) && double.TryParse(wanted, fl, inv, out var b) ? Math.Abs(a - b) < 1e-3 : got == (wanted ?? "").Trim();
        }

        private async Task<bool> WaitForLine(List<string> lines, Func<string, bool> match, int seconds, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && !SnapshotHas(lines, match))
            {
                if (!monitor.IsRunning) break;
                await Task.Delay(200, ct);
                monitor.Poll();
            }
            return SnapshotHas(lines, match);
        }

        /// <summary>
        /// Options a travel would otherwise carry over from the map before: those the launcher may set, given their
        /// neutral value when this plan does not set them (checked in the game: game= and Mutators= empty work).
        /// </summary>
        public static string TravelResets(LaunchPlan plan) => TravelResets(plan, plan.TravelUrl, !plan.OwnRules);

        /// <summary>
        /// The same for another URL of the plan's match (a dedicated server's). <paramref name="rules"/> false = the rules are the
        /// server's own (its Game.ini): no rule option is reset, or the URL would override them with the game's defaults.
        /// </summary>
        public static string TravelResets(LaunchPlan plan, string url, bool rules = true)
        {
            var have = new HashSet<string>(url.Split('?').Skip(1).Select(o => o.Split('=')[0]), StringComparer.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder();
            void Reset(string key, string value) { if (!have.Contains(key)) sb.Append('?').Append(key).Append('=').Append(value); }
            Reset("game", "");
            Reset("Mutators", "");
            Reset("bSoloGame", "0");
            string UrlValue(string v) => LaunchPlanner.IsTrue(v) ? "1" : v.Equals("False", StringComparison.OrdinalIgnoreCase) ? "0" : v;
            if (plan.Mode != null && rules)
                foreach (var key in LaunchPlanner.UrlOptions)
                {
                    // A switch the match turns off is not in its URL (only "on" is written): the reset must say 0, not the
                    // mode's default (kill feed and death camera came back on with Realism, 2026-10-02 audit).
                    if (plan.Overrides.TryGetValue(key, out var own) && own != null) Reset(key, UrlValue(own));
                    else if (plan.Mode.Defaults.TryGetValue(key, out var def) && def != null) Reset(key, UrlValue(def));
                }
            return sb.ToString();
        }

        /// <summary>Brings the game window to the front (a plain request; the launcher is in front when this runs).</summary>
        private void BringToFront()
        {
            try
            {
                var input = new GameInput(monitor.Window);
                if (!input.IsForeground) input.Focus(2500);
            }
            catch (Exception ex) { AppLog.Warn("Could not bring the game to the front: " + ex.Message); }
        }

        /// <summary>The loading picture stays up for a few seconds after the map is loaded; the console cannot open under it.</summary>
        private async Task WaitForLoadingScreen(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(500, ct);
            monitor.Poll();
            while (monitor.LoadingScreenUp && sw.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (!monitor.IsRunning) throw new LaunchException(T("The game closed while loading the map."));
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
                if (fail != null) throw new LaunchException(F("The game could not load the map: {0}", Regex.Replace(fail, @"^\[[^\]]*\]\[[^\]]*\]", "").Trim()));
                if (!monitor.IsRunning) throw new LaunchException(T("The game closed while loading the map."));
                await Task.Delay(250, ct);
                monitor.Poll();
            }
            throw new LaunchException(timeoutSec < 100
                ? T("The game did not start loading the map. Look at the game window: if the console is still open with the command in it, press Enter there; otherwise press Launch again.")
                : T("The map did not finish loading. Check the game window for an error message."));
        }

        private string WriteGameIni(LaunchPlan plan)
        {
            var p = plan.Profile;
            string path = GameInstall.GameIniPath;
            string current = UeIni.ReadText(path);
            var db = state.Rules;
            // Extra lines the player added last time are the launcher's too, so removing them from the profile removes them here.
            string updated = LaunchPlanner.GameIniForLaunch(current, plan, db, state.Settings.ManagedIniKeys, state.Settings);
            int sections = plan.IniSections.Count(s => s.Values.Count > 0);
            if (Normalize(updated) != Normalize(current))
            {
                ConsoleBridge.BackupFile(path);
                UeIni.WriteText(path, updated);
                AppLog.Info("Game.ini updated (" + sections + " section(s), " + UeIni.Split(current).Count + " -> " + UeIni.Split(updated).Count + " lines)");
                AppLog.Debug("Game.ini rules:\r\n" + plan.GameIniBlock);
            }
            if (plan.OwnRules)
            {
                // The player's own rules: the extra lines written earlier stay the launcher's (they go when the switch is off
                // again), and the game reads the rules in the file as it is.
                state.Settings.LastWrittenRulesHash = plan.RestartKey;
                state.Settings.LastWrittenIniPart = LaunchPlanner.OwnRulesPart;
                state.Settings.LastWrittenRuleKeys = LaunchPlanner.RuleKeysIn(UeIni.Parse(updated), db);
                state.Settings.LastRulesWriteUtc = DateTime.UtcNow;
                return T("Your Game.ini's rules");
            }
            state.Settings.ManagedIniKeys = LaunchPlanner.PlayerIniKeys(plan, db);
            state.Settings.LastWrittenRulesHash = plan.RestartKey;
            state.Settings.LastWrittenIniPart = LaunchPlanner.IniRestartPart(plan.Profile, db);
            state.Settings.LastWrittenRuleKeys = LaunchPlanner.WrittenRuleKeys(plan, db);
            state.Settings.LastRulesWriteUtc = DateTime.UtcNow;
            return plan.Overrides.Count == 0 && sections == 0 ? T("Game defaults") : plan.Overrides.Count == 1 ? F("1 change for {0}", T(plan.ModeTitle)) : F("{0} changes for {1}", plan.Overrides.Count, T(plan.ModeTitle));
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        /// <summary>Writes the rules (so AI teammates apply) and starts the game without loading a match.</summary>
        public void StartOnly(LaunchPlan plan)
        {
            if (monitor.IsRunning) return;
            if (plan != null && plan.IsValid) WriteGameIni(plan);
            PrepareConsoleKey();
            StartGame(plan);
            NoteGameStart(plan);
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
            RconSetup.EnsureSettings(state.Settings);
            args.Add(RconSetup.CommandLine(state.Settings));
            string ruleset = plan?.StartRuleset;
            if (!string.IsNullOrWhiteSpace(ruleset)) args.Add("-ruleset=" + ruleset.Trim());
            if (!string.IsNullOrWhiteSpace(state.Settings.LaunchArgs)) args.Add(state.Settings.LaunchArgs.Trim());
            string argLine = string.Join(" ", args);
            // The player's choice: the game's own exe, or their own command, instead of Steam or Epic starting it.
            if (state.Settings.StartWith == "Exe" && install.ClientExe != null && File.Exists(install.ClientExe))
            {
                AppLog.Info("Starting the game's exe directly");
                var psi = new ProcessStartInfo(install.ClientExe, argLine) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(install.ClientExe) };
                // Without its Steam app id the exe quits at once and asks Steam to start it (tested: it then runs on its own).
                if (install.Store != "Epic") { psi.EnvironmentVariables["SteamAppId"] = GameInstall.SteamAppId; psi.EnvironmentVariables["SteamGameId"] = GameInstall.SteamAppId; }
                Process.Start(psi);
                return;
            }
            if (state.Settings.StartWith == "Command" && !string.IsNullOrWhiteSpace(state.Settings.StartCommand))
            {
                var (file, cmdArgs) = SplitCommand(state.Settings.StartCommand, argLine);
                AppLog.Info("Starting the game with the player's own command: " + Path.GetFileName(file));
                try
                {
                    string dir = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;
                    // A command that runs the game's exe needs its Steam app id too; programs it starts inherit it. Only for
                    // this start: a dedicated server or SteamCMD started later must not run as the game (app 581320).
                    string oldApp = Environment.GetEnvironmentVariable("SteamAppId"), oldGame = Environment.GetEnvironmentVariable("SteamGameId");
                    try
                    {
                        if (install.Store != "Epic") { Environment.SetEnvironmentVariable("SteamAppId", GameInstall.SteamAppId); Environment.SetEnvironmentVariable("SteamGameId", GameInstall.SteamAppId); }
                        Process.Start(new ProcessStartInfo(file, cmdArgs) { UseShellExecute = true, WorkingDirectory = dir ?? "" });
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("SteamAppId", oldApp);
                        Environment.SetEnvironmentVariable("SteamGameId", oldGame);
                    }
                }
                catch (Exception ex) { throw new LaunchException(F("Your start command could not run ({0}). Check it in Settings > Launching.", ex.Message)); }
                return;
            }
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

        /// <summary>
        /// The player's start command as program and arguments. The program is in quotes, or the longest start of the
        /// line that is a file (a path with spaces works without quotes). The launcher's options go where {options}
        /// is written, else at the end.
        /// </summary>
        public static (string file, string args) SplitCommand(string command, string options)
        {
            string c = (command ?? "").Trim();
            options = (options ?? "").Trim();
            string file = null, rest = "";
            if (c.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = c.IndexOf('"', 1);
                file = end > 0 ? c.Substring(1, end - 1) : c.Substring(1);
                rest = end > 0 ? c.Substring(end + 1) : "";
            }
            else
            {
                for (int i = c.Length; i > 0 && file == null; i--)
                    if ((i == c.Length || c[i] == ' ') && File.Exists(c.Substring(0, i))) { file = c.Substring(0, i); rest = c.Substring(i); }
                if (file == null)
                {
                    int space = c.IndexOf(' ');
                    file = space < 0 ? c : c.Substring(0, space);
                    rest = space < 0 ? "" : c.Substring(space);
                }
            }
            rest = rest.Trim();
            if (rest.Contains("{options}")) rest = rest.Replace("{options}", options);
            else if (options.Length > 0) rest = (rest + " " + options).Trim();
            return (file, Regex.Replace(rest, " {2,}", " ").Trim());
        }

        public async Task StopGame(CancellationToken ct)
        {
            var p = Process.GetProcessesByName(GameInstall.ClientProcess).FirstOrDefault();
            if (p == null) return;
            // RCON asks the game to quit without touching its window; the window close request is the fallback.
            if (await Task.Run(() => rcon.Exit(), ct))
                for (int i = 0; i < 40 && !p.HasExited; i++) await Task.Delay(500, ct);
            if (!p.HasExited)
            {
                try { p.CloseMainWindow(); } catch { }
                for (int i = 0; i < 60 && !p.HasExited; i++) await Task.Delay(500, ct);
            }
            if (!p.HasExited && state.Settings.AllowConsoleTyping)
            {
                await console.Run("exit", ct, null, TimeSpan.FromSeconds(1));
                for (int i = 0; i < 40 && !p.HasExited; i++) await Task.Delay(500, ct);
            }
            if (!p.HasExited) throw new LaunchException(T("The game did not close. Close it yourself and press Launch again."));
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
                    list.Add(F("{0} reported a loading error on this game version, so it may not work (the mod probably needs an update).", m.DisplayName));
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
