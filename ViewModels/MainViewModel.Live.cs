using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;
using SandstormModLauncher.Game;
using SandstormModLauncher.Services;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed class LiveRule : ObservableObject
    {
        private string value, current;
        public string Key { get; set; }
        /// <summary>The rule's name in English; <see cref="Label"/> shows it in the player's language.</summary>
        public string English { get; set; }
        public string Label => T(English);
        public string Default { get; set; }
        public void Relabel() => Raise(nameof(Label));
        public string Value { get => value; set => Set(ref this.value, value); }
        public string Current { get => current; set => Set(ref current, value); }
    }

    public sealed partial class MainViewModel
    {
        public ObservableCollection<LiveGroup> LiveGroups { get; } = new ObservableCollection<LiveGroup>();
        public ObservableCollection<LiveRule> LiveRules { get; } = new ObservableCollection<LiveRule>();
        public ObservableCollection<ExecCommand> CommandSuggestions { get; } = new ObservableCollection<ExecCommand>();
        public ObservableCollection<ExecCommand> GameCommands { get; } = new ObservableCollection<ExecCommand>();
        public ICollectionView GameCommandsView { get; private set; }
        public ICommand RunLiveActionCommand { get; private set; }
        public ICommand ReadLiveRulesCommand { get; private set; }
        public ICommand ApplyLiveRulesCommand { get; private set; }
        public ICommand SendCustomCommand { get; private set; }
        public ICommand CountBotsCommand { get; private set; }
        public ICommand UseGameCommand { get; private set; }
        private string liveOutput = "", customCommand = "", liveModeName = "", commandSearch = "";
        private bool liveBusy, restartAfterApply = true, cheatsForCustom = true;
        private string liveModeCls;

        private void InitLiveCommands()
        {
            RunLiveActionCommand = new AsyncCommand(p => RunLiveAction(p as LiveAction), p => CanLive && !(p is LiveAction a && a.CoopOnly && !liveIsCoop));
            ReadLiveRulesCommand = new AsyncCommand(ReadLiveRules, () => CanLive && LiveRules.Count > 0);
            ApplyLiveRulesCommand = new AsyncCommand(ApplyLiveRuleChanges, () => CanLive && LiveRules.Any(r => !string.IsNullOrWhiteSpace(r.Value)));
            SendCustomCommand = new AsyncCommand(SendCustom, () => Monitor?.IsRunning == true && !string.IsNullOrWhiteSpace(customCommand) && !liveBusy);
            CountBotsCommand = new AsyncCommand(CountBots, () => CanLive);
            UseGameCommand = new RelayCommand(p =>
            {
                if (!(p is ExecCommand c)) return;
                string text = customCommand ?? "";
                int bar = text.LastIndexOf('|');
                string prefix = bar >= 0 ? text.Substring(0, bar + 1) + " " : "";
                CustomCommand = prefix + c.Name + (c.Params.Count > 0 ? " " : "");
                CommandSuggestions.Clear();
            });

            RebuildLiveGroups();
        }

        /// <summary>The Live tab's buttons (again after a language change).</summary>
        private void RebuildLiveGroups()
        {
            LiveGroups.Clear();
            LiveGroup G(string title, bool cheat, params (string label, string cmd, string tip)[] actions) => new LiveGroup
            {
                Title = title, Cheat = cheat, Badge = cheat ? T("CHEAT") : T("ADMIN"),
                Actions = actions.Select(a => new LiveAction { Label = a.label, Command = a.cmd, Tip = a.tip }).ToList()
            };
            LiveGroups.Add(G(T("Round"), false,
                (T("Restart round"), "AdminRestartRound 0", T("Starts the round again with the current settings.")),
                (T("Restart and switch sides"), "AdminRestartRound 1", T("Restarts the round with the teams swapped.")),
                (T("5 minutes left"), "SetRoundTimer 300", T("Sets the round clock to 5:00.")),
                (T("15 minutes left"), "SetRoundTimer 900", T("Sets the round clock to 15:00.")),
                (T("30 minutes left"), "SetRoundTimer 1800", T("Sets the round clock to 30:00.")),
                (T("Add about 1 hour"), "AdminExtendRoundTimer", T("The game's own round extension: adds roughly an hour to the clock.")),
                (T("Round never ends"), "IgnoreRoundOver 1", T("The round keeps going when it would end.")),
                (T("Round can end again"), "IgnoreRoundOver 0", T("Turns \"Round never ends\" off.")),
                (T("End the match"), "AdminForceGameOver", T("Ends the match immediately."))));
            LiveGroups.Add(G(T("Objectives"), true,
                (T("Capture current objective"), "InstaCap", T("Captures the objective you are attacking.")),
                (T("Start a counter-attack"), "CheatCounterAttack", T("Co-op: triggers a counter-attack now.")),
                (T("Finish the counter-attack"), "CheatFinishCounterAttack", T("Co-op: ends the running counter-attack.")),
                (T("Skip to extraction"), "SkipToExtraction", T("Co-op with extraction: jumps to the final extraction."))));
            LiveGroups.Add(G(T("Bots"), true,
                (T("Respawn all bots"), "RespawnAllBots", T("Brings every bot back.")),
                (T("Respawn enemy bots"), "AIRespawnEnemyBots", T("Brings enemy bots back.")),
                (T("Respawn AI teammates"), "AIRespawnFriendlyBots", T("Brings dead AI teammates back.")),
                (T("Remove all enemies"), "AIPurgeEnemy", T("Removes every enemy bot.")),
                (T("Remove AI teammates"), "AIPurgeFriendly", T("Removes the bots on your team.")),
                (T("Freeze or unfreeze all AI"), "AIToggle", T("Stops every bot in place, press again to resume.")),
                (T("Bots ignore everyone"), "AIIgnorePlayers", T("Co-op: bots stop attacking players. Press again to undo.")),
                (T("Bots ignore me"), "AINoTargetPlayer", T("Enemies stop targeting you."))));
            LiveGroups.Add(G(T("Your soldier"), true,
                (T("God mode"), "GodMode", T("You can't take damage. Press again to turn off.")),
                (T("Refill ammo and gear"), "ResupplyNow", T("Instant resupply.")),
                (T("Give 10 supply points"), "GiveSupplyPointsUnrestricted 10", T("Extra supply for your loadout.")),
                (T("Respawn me"), "RespawnMe", T("Respawns your soldier.")),
                (T("Revive me"), "Revive", T("Gets you back up.")),
                (T("Fly through walls"), "Noclip", T("Free movement through geometry. Press again to land."))));
            LiveGroups.Add(G(T("Match & view"), true,
                (T("Respawn every player"), "AdminRespawnAllPlayers", T("Respawns all players on both teams.")),
                (T("Slow motion"), "Slomo 0.4", T("Game runs at 40% speed.")),
                (T("Normal speed"), "Slomo 1", T("Back to normal game speed.")),
                (T("Free camera"), "ToggleDebugCamera", T("Detached camera for screenshots. Press again to return.")),
                (T("Hide or show HUD"), "ShowHUD", T("Toggles the HUD for clean screenshots."))));

            // Measured in the game: these only exist in the co-op game modes and are rejected in versus.
            var coopOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CheatCounterAttack", "CheatFinishCounterAttack", "SkipToExtraction", "AIIgnorePlayers" };
            foreach (var a in LiveGroups.SelectMany(g => g.Actions))
                if (coopOnly.Contains(a.Command.Split(' ')[0])) a.CoopOnly = true;
        }

        private bool liveIsCoop = true;

        public bool CanLive => Monitor?.Phase == GamePhase.InMatch && !liveBusy;
        public bool LiveBusy { get => liveBusy; set { if (Set(ref liveBusy, value)) { Raise(nameof(CanLive)); CommandManager.InvalidateRequerySuggested(); } } }
        public string LiveOutput { get => liveOutput; set => Set(ref liveOutput, value); }
        public string LiveModeName { get => liveModeName; set => Set(ref liveModeName, value); }
        public bool RestartAfterApply { get => restartAfterApply; set => Set(ref restartAfterApply, value); }
        public bool CheatsForCustom { get => cheatsForCustom; set => Set(ref cheatsForCustom, value); }

        public string CustomCommand
        {
            get => customCommand;
            set { if (Set(ref customCommand, value ?? "")) UpdateSuggestions(); }
        }

        // ------------------------------------------------------------------ command catalog

        /// <summary>Background thread: reads (or loads the cached) list of console commands from the game exe.</summary>
        private void LoadCommands()
        {
            try { State.Commands = ExecCatalog.Load(State.Install?.ClientExe, AppPaths.CacheDir); }
            catch (Exception ex) { AppLog.Warn("Command catalog: " + ex.Message); State.Commands = new List<ExecCommand>(); }
            ui.BeginInvoke(new Action(BuildCommandList));
        }

        private void BuildCommandList()
        {
            GameCommands.Clear();
            foreach (var c in State.Commands) GameCommands.Add(c);
            GameCommandsView = CollectionViewSource.GetDefaultView(GameCommands);
            GameCommandsView.Filter = o =>
            {
                var c = (ExecCommand)o;
                return string.IsNullOrWhiteSpace(commandSearch) || c.Name.IndexOf(commandSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || c.Note.IndexOf(commandSearch, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            RaiseMany(nameof(GameCommandsView), nameof(GameCommandCount));
        }

        public int GameCommandCount => GameCommands.Count;
        public string CommandSearch { get => commandSearch; set { if (Set(ref commandSearch, value ?? "")) GameCommandsView?.Refresh(); } }

        private void UpdateSuggestions()
        {
            CommandSuggestions.Clear();
            string text = customCommand ?? "";
            int bar = text.LastIndexOf('|');
            string part = (bar >= 0 ? text.Substring(bar + 1) : text).TrimStart();
            if (part.Length < 2 || part.Contains(' ')) return;
            foreach (var c in State.Commands.Where(c => c.Name.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                                          .Concat(State.Commands.Where(c => c.Name.IndexOf(part, 1, StringComparison.OrdinalIgnoreCase) > 0))
                                          .Distinct().Take(8))
                CommandSuggestions.Add(c);
            if (CommandSuggestions.Count == 1 && CommandSuggestions[0].Name.Equals(part, StringComparison.OrdinalIgnoreCase)) CommandSuggestions.Clear();
        }

        // ------------------------------------------------------------------ live state

        private void RefreshLive()
        {
            Raise(nameof(CanLive));
            if (Monitor == null) return;
            string url = Monitor.CurrentUrl ?? "";
            var m = Regex.Match(url, @"Scenario=([^?]+)");
            ScenarioInfo sc = m.Success ? State.AllScenarios.FirstOrDefault(s => s.Id.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) : null;
            bool hardcore = url.IndexOf("game=CheckpointHardcore", StringComparison.OrdinalIgnoreCase) >= 0;
            var mode = sc != null ? State.Rules.ResolveMode(sc.GameModeClass, hardcore) : CurrentMode;
            if (mode?.Cls == liveModeCls) return;
            liveModeCls = mode?.Cls;
            liveIsCoop = mode?.Coop ?? true;
            LiveModeName = mode?.Name ?? "";
            CommandManager.InvalidateRequerySuggested();
            LiveRules.Clear();
            if (mode == null) return;
            string[] keys = mode.Coop
                ? new[] { "AIDifficulty", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "RespawnDelay", "SoloWaves", "RoundTime", "ObjectiveCaptureTime",
                          "InitialSupply", "DefendTimer" }
                : new[] { "RoundTime", "RoundLimit", "WinLimit", "ObjectiveCaptureTime", "InitialSupply", "BotQuota", "RespawnDelay", "DefendTimer",
                          "AttackerWavesPerObjective", "DefaultReinforcementWaves", "NumWaves" };
            foreach (var k in keys)
            {
                if (!mode.Defaults.TryGetValue(k, out var def)) continue;
                LiveRules.Add(new LiveRule { Key = k, English = State.Rules.Prop(k)?.Label ?? k, Default = def });
            }
        }

        /// <summary>Game log lines worth showing after a console command (its answers, errors, state changes).</summary>
        private static List<string> Interesting(IEnumerable<string> lines) =>
            lines.Where(l => !l.Contains("LogViewport") && !l.Contains("LogCosmetics") && !l.Contains("LogFX") && !l.Contains("LogRagdoll") && !l.Contains("LogRcon")
                             && (l.Contains("Command not recognized") || l.Contains("Bad or missing") || l.Contains("LogAI: Display: AI difficulty")
                                 || l.Contains(") ") || l.Contains("LogGameMode: Display: State") || l.Contains("LogExec") || l.Contains("Cheat")))
                 .Select(l => Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", "")).Take(12).ToList();

        /// <summary>
        /// Runs console commands in the game as the player, over RCON: the game runs them as if they were typed into its
        /// console, but nothing is typed and the game can stay in the background. Null when the game cannot be reached
        /// over RCON. The game does not answer these commands, so what it writes to its log right after is returned.
        /// </summary>
        private async Task<List<string>> RunAsPlayer(IEnumerable<string> commands)
        {
            if (Rcon == null || Monitor == null) return null;
            var list = commands.SelectMany(GameRcon.SplitCommands).ToList();
            var lines = new List<string>();
            void Grab(string l) { lock (lines) lines.Add(l); }
            LiveBusy = true;
            Monitor.LineReceived += Grab;
            try
            {
                bool busy = false;
                string problem = await Task.Run(() => Rcon.Probe(out busy));
                if (problem != null && !busy) return null;
                try { await Task.Run(() => Rcon.RunAsPlayer(list)); }
                catch (RconException ex)
                {
                    AppLog.Warn("RCON: " + ex.Message);
                    LiveOutput = F("No answer from the game over RCON ({0}).", ex.Message) + (ex.Delivered ? " " + T("The command may still arrive.") : "");
                    ShowToast("No answer from the game");
                    throw new OperationCanceledException();
                }
                AppLog.Info("Live over RCON: " + string.Join(" | ", list));
                await Task.Delay(1500);
                Monitor.Poll();
                lock (lines) return lines.ToList();
            }
            finally { Monitor.LineReceived -= Grab; LiveBusy = false; }
        }

        private async Task<CommandResult> RunConsole(string command, TimeSpan? verify = null)
        {
            LiveBusy = true;
            try
            {
                var res = await Console.Run(command, CancellationToken.None, null, verify ?? TimeSpan.FromSeconds(2.5));
                var interesting = Interesting(res.Lines);
                LiveOutput = !res.Sent ? F("Not sent: {0}", res.Detail) : res.NotRecognized ? res.Detail : interesting.Count > 0 ? string.Join("\n", interesting) : F("Sent: {0}", command);
                if (!res.Sent)
                {
                    ShowToast(F("Not sent: {0}", res.Detail));
                    DebugReport.Auto("console-failed", res.Detail, State, Monitor);
                }
                return res;
            }
            finally { LiveBusy = false; }
        }

        private async Task RunLiveAction(LiveAction action)
        {
            if (action == null) return;
            try { await RunLiveActionCore(action); } catch (OperationCanceledException) { }
        }

        private async Task RunLiveActionCore(LiveAction action)
        {
            // Round restarts are RCON's own command (the game answers it); the rest run as the player's console
            // commands over RCON. Nothing is typed and the game can stay where it is.
            if (action.Command == "AdminRestartRound 0" || action.Command == "AdminRestartRound 1")
            {
                var r = await OverRcon(() => Rcon.RestartRound(action.Command.EndsWith("1")));
                if (r != null)
                {
                    LiveOutput = r.Ok ? F("{0}: done", action.Label) : F("Not done: {0}", r.Error);
                    ShowToast(r.Ok ? F("{0}: done", action.Label) : F("Not done: {0}", r.Error));
                    return;
                }
            }
            else
            {
                // Cheats are only needed for cheat commands, but switching them on is harmless in local play.
                var lines = await RunAsPlayer(new[] { "EnableCheats", action.Command });
                if (lines != null)
                {
                    var shown = Interesting(lines);
                    LiveOutput = F("{0}: sent ({1})", action.Label, action.Command) + (shown.Count > 0 ? "\n" + string.Join("\n", shown) : "");
                    ShowToast(F("{0}: sent", action.Label));
                    return;
                }
            }
            if (!State.Settings.AllowConsoleTyping)
            {
                LiveOutput = NoRconLive;
                ShowToast("The game cannot be reached over RCON");
                return;
            }
            var res = await RunConsole("EnableCheats | " + action.Command);
            if (res.Sent) ShowToast(res.NotRecognized ? res.Detail : F("{0}: done", action.Label));
        }

        /// <summary>
        /// Runs something over RCON on a worker thread. Null when the game cannot be reached over RCON at all (the
        /// caller may fall back to the console). A failure in the middle of the call is shown and throws
        /// OperationCanceledException: the change may have reached the game, so it is not typed in again.
        /// </summary>
        private async Task<T> OverRcon<T>(Func<T> work) where T : class
        {
            if (Rcon == null) return null;
            LiveBusy = true;
            try
            {
                bool busy = false;
                string problem = await Task.Run(() => Rcon.Probe(out busy));
                if (problem != null && !busy) return null;
                try { return await Task.Run(work); }
                catch (RconException ex)
                {
                    AppLog.Warn("RCON: " + ex.Message);
                    LiveOutput = F("No answer from the game over RCON ({0}). The change may still arrive; press Read to check.", ex.Message);
                    ShowToast("No answer from the game");
                    throw new OperationCanceledException();
                }
            }
            finally { LiveBusy = false; }
        }

        private async Task ReadLiveRules()
        {
            try { await ReadLiveRulesCore(); } catch (OperationCanceledException) { }
        }

        private async Task ReadLiveRulesCore()
        {
            if (liveModeCls == null) return;
            var read = await OverRcon(() => Rcon.ReadProperties(null, out _));
            if (read != null)
            {
                foreach (var r in LiveRules) r.Current = read.TryGetValue(r.Key, out var v) ? TrimNumber(v) : "?";
                LiveOutput = read.Count > 0 ? F("Read {0} live values from the match (RCON).", LiveRules.Count(r => r.Current != "?")) : T("No values came back. Make sure the match has finished loading.");
                return;
            }
            if (!State.Settings.AllowConsoleTyping) { LiveOutput = NoRconLive; return; }
            LiveBusy = true;
            try
            {
                var values = await Console.ReadLive(liveModeCls, LiveRules.Select(r => r.Key), CancellationToken.None);
                foreach (var r in LiveRules) r.Current = values.TryGetValue(r.Key, out var v) ? TrimNumber(v) : "?";
                LiveOutput = values.Count > 0 ? F("Read {0} live values from the match.", values.Count) : T("No values came back. Make sure the match has finished loading.");
            }
            finally { LiveBusy = false; }
        }

        private static string NoRconLive => T("The launcher cannot reach the game over RCON (the game was probably started before the launcher set it up). Restart the game from the launcher, or allow typing into the game console in Settings.");

        private static string TrimNumber(string v)
        {
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Contains("."))
                return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return v;
        }

        private async Task ApplyLiveRuleChanges()
        {
            try { await ApplyLiveRuleChangesCore(); } catch (OperationCanceledException) { }
        }

        private async Task ApplyLiveRuleChangesCore()
        {
            var cmds = new List<string>();
            var bad = new List<string>();
            foreach (var r in LiveRules.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
            {
                // One value per setting: a space, | or ; would run a second console command.
                string clean = SetupEngine.NormalizeValue(State.Rules.Prop(r.Key), r.Value);
                if (clean == null) bad.Add(r.Label);
                else cmds.Add("AdminSetGamemodeProperty " + r.Key + " " + clean);
            }
            if (bad.Count > 0) { ShowToast(F("Not a valid value: {0}", string.Join(", ", bad))); return; }
            if (cmds.Count == 0) return;
            var props = cmds.Select(c => c.Split(' ')).Select(w => new KeyValuePair<string, string>(w[1], w[2])).ToList();
            var failed = await OverRcon(() =>
            {
                var f = Rcon.SetProperties(props);
                if (restartAfterApply) { var rr = Rcon.RestartRound(false); if (!rr.Ok) f["restart"] = rr.Error; }
                return f;
            });
            if (failed != null)
            {
                int ok = props.Count - failed.Count(f => f.Key != "restart");
                LiveOutput = F("{0} live change(s) set over RCON", ok) + (restartAfterApply && !failed.ContainsKey("restart") ? ", " + T("round restarted") : "") +
                             (failed.Count > 0 ? "\n" + T("Not taken:") + " " + string.Join("; ", failed.Select(f => f.Key + " (" + f.Value + ")")) : "");
                ShowToast(failed.Count == 0 ? F("{0} live change(s) set", ok) : T("Some changes were not taken"));
                await ReadLiveRules();
                return;
            }
            if (!State.Settings.AllowConsoleTyping) { LiveOutput = NoRconLive; return; }
            if (restartAfterApply) cmds.Add("AdminRestartRound 0");
            var res = await RunConsole(string.Join(" | ", cmds));
            if (res.Sent)
            {
                ShowToast(F("{0} live change(s) sent", cmds.Count - (restartAfterApply ? 1 : 0)) + (restartAfterApply ? ", " + T("round restarted") : ""));
                await Task.Delay(800);
                await ReadLiveRules();
            }
        }

        private async Task SendCustom()
        {
            try { await SendCustomCore(); } catch (OperationCanceledException) { }
        }

        private async Task SendCustomCore()
        {
            var commands = GameRcon.SplitCommands(customCommand);
            if (commands.Count == 0) return;
            if (cheatsForCustom && !commands[0].StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase)) commands.Insert(0, "EnableCheats");
            var lines = await RunAsPlayer(commands);
            if (lines != null)
            {
                var shown = Interesting(lines);
                LiveOutput = F("Sent: {0}", string.Join(" | ", commands)) + "\n" +
                             (shown.Count > 0 ? string.Join("\n", shown) : T("The game does not answer console commands. If nothing happens, check the name in the list below."));
                CustomCommand = "";
                return;
            }
            if (!State.Settings.AllowConsoleTyping) { LiveOutput = NoRconLive; return; }
            var res = await RunConsole(string.Join(" | ", commands), TimeSpan.FromSeconds(3));
            if (res.Sent && !res.NotRecognized) CustomCommand = "";
        }

        private async Task CountBots()
        {
            try { await CountBotsCore(); } catch (OperationCanceledException) { }
        }

        private async Task CountBotsCore()
        {
            var counted = await OverRcon(() => Rcon.CountPlayers());
            if (counted != null)
            {
                if (counted.Count == 0) { LiveOutput = T("No players came back. Make sure the match has finished loading."); return; }
                if (counted.Values.All(c => c.bots == 0))
                {
                    LiveOutput = T("No bots have joined yet. They join when the round starts (after you pick a class).");
                    ShowToast("No bots yet: they join when the round starts");
                    return;
                }
                var mine = counted.FirstOrDefault(kv => kv.Value.humans > 0);
                int myMates = mine.Value.bots, theirs = counted.Where(kv => kv.Key != mine.Key).Sum(kv => kv.Value.humans + kv.Value.bots);
                LiveOutput = $"Your team: you + {myMates} AI teammate{(myMates == 1 ? "" : "s")}\nEnemy team: {theirs} bot{(theirs == 1 ? "" : "s")}";
                ShowToast(F("You + {0} AI vs {1} enemies", myMates, theirs));
                return;
            }
            if (!State.Settings.AllowConsoleTyping) { LiveOutput = NoRconLive; return; }
            var res = await RunConsole("getall INSPlayerState TeamId | getall INSPlayerState bIsABot", TimeSpan.FromSeconds(2.5));
            var teams = new Dictionary<string, string>();
            var bots = new Dictionary<string, bool>();
            foreach (var l in res.Lines)
            {
                var t = Regex.Match(l, @"(INSPlayerState_\d+)\.TeamId = (\d+)");
                if (t.Success) teams[t.Groups[1].Value] = t.Groups[2].Value;
                var b = Regex.Match(l, @"(INSPlayerState_\d+)\.bIsABot = (True|False)");
                if (b.Success) bots[b.Groups[1].Value] = b.Groups[2].Value == "True";
            }
            if (teams.Count == 0)
            {
                if (res.Sent) LiveOutput = T("No players came back. Make sure the match has finished loading.");
                return;
            }
            if (bots.Count > 0 && bots.Values.All(b => !b))
            {
                LiveOutput = T("No bots have joined yet. They join when the round starts (after you pick a class).");
                ShowToast("No bots yet: they join when the round starts");
                return;
            }
            string humanTeam = teams.FirstOrDefault(kv => bots.TryGetValue(kv.Key, out var isBot) && !isBot).Value ?? "0";
            int mates = teams.Count(kv => kv.Value == humanTeam && bots.TryGetValue(kv.Key, out var isBot) && isBot);
            int enemies = teams.Count(kv => kv.Value != humanTeam);
            LiveOutput = $"Your team: you + {mates} AI teammate{(mates == 1 ? "" : "s")}\nEnemy team: {enemies} bot{(enemies == 1 ? "" : "s")}";
            ShowToast(F("You + {0} AI vs {1} enemies", mates, enemies));
        }
    }
}
