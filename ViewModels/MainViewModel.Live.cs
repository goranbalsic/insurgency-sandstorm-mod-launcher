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
using SandstormModLauncher.Game;
using SandstormModLauncher.Services;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed class LiveRule : ObservableObject
    {
        private string value, current;
        public string Key { get; set; }
        public string Label { get; set; }
        public string Default { get; set; }
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

            LiveGroup G(string title, bool cheat, params (string label, string cmd, string tip)[] actions) => new LiveGroup
            {
                Title = title, Cheat = cheat, Badge = cheat ? "CHEAT" : "ADMIN",
                Actions = actions.Select(a => new LiveAction { Label = a.label, Command = a.cmd, Tip = a.tip }).ToList()
            };
            LiveGroups.Add(G("Round", false,
                ("Restart round", "AdminRestartRound 0", "Starts the round again with the current settings."),
                ("Restart and switch sides", "AdminRestartRound 1", "Restarts the round with the teams swapped."),
                ("5 minutes left", "SetRoundTimer 300", "Sets the round clock to 5:00."),
                ("15 minutes left", "SetRoundTimer 900", "Sets the round clock to 15:00."),
                ("30 minutes left", "SetRoundTimer 1800", "Sets the round clock to 30:00."),
                ("Add about 1 hour", "AdminExtendRoundTimer", "The game's own round extension: adds roughly an hour to the clock."),
                ("Round never ends", "IgnoreRoundOver 1", "The round keeps going when it would end."),
                ("Round can end again", "IgnoreRoundOver 0", "Turns \"Round never ends\" off."),
                ("End the match", "AdminForceGameOver", "Ends the match immediately.")));
            LiveGroups.Add(G("Objectives", true,
                ("Capture current objective", "InstaCap", "Captures the objective you are attacking."),
                ("Start a counter-attack", "CheatCounterAttack", "Co-op: triggers a counter-attack now."),
                ("Finish the counter-attack", "CheatFinishCounterAttack", "Co-op: ends the running counter-attack."),
                ("Skip to extraction", "SkipToExtraction", "Co-op with extraction: jumps to the final extraction.")));
            LiveGroups.Add(G("Bots", true,
                ("Respawn all bots", "RespawnAllBots", "Brings every bot back."),
                ("Respawn enemy bots", "AIRespawnEnemyBots", "Brings enemy bots back."),
                ("Respawn AI teammates", "AIRespawnFriendlyBots", "Brings dead AI teammates back."),
                ("Remove all enemies", "AIPurgeEnemy", "Removes every enemy bot."),
                ("Remove AI teammates", "AIPurgeFriendly", "Removes the bots on your team."),
                ("Freeze or unfreeze all AI", "AIToggle", "Stops every bot in place, press again to resume."),
                ("Bots ignore everyone", "AIIgnorePlayers", "Co-op: bots stop attacking players. Press again to undo."),
                ("Bots ignore me", "AINoTargetPlayer", "Enemies stop targeting you.")));
            LiveGroups.Add(G("Your soldier", true,
                ("God mode", "GodMode", "You can't take damage. Press again to turn off."),
                ("Refill ammo and gear", "ResupplyNow", "Instant resupply."),
                ("Give 10 supply points", "GiveSupplyPointsUnrestricted 10", "Extra supply for your loadout."),
                ("Respawn me", "RespawnMe", "Respawns your soldier."),
                ("Revive me", "Revive", "Gets you back up."),
                ("Fly through walls", "Noclip", "Free movement through geometry. Press again to land.")));
            LiveGroups.Add(G("Match & view", true,
                ("Respawn every player", "AdminRespawnAllPlayers", "Respawns all players on both teams."),
                ("Slow motion", "Slomo 0.4", "Game runs at 40% speed."),
                ("Normal speed", "Slomo 1", "Back to normal game speed."),
                ("Free camera", "ToggleDebugCamera", "Detached camera for screenshots. Press again to return."),
                ("Hide or show HUD", "ShowHUD", "Toggles the HUD for clean screenshots.")));

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
                LiveRules.Add(new LiveRule { Key = k, Label = State.Rules.Prop(k)?.Label ?? k, Default = def });
            }
        }

        private async Task<CommandResult> RunConsole(string command, TimeSpan? verify = null)
        {
            LiveBusy = true;
            try
            {
                var res = await Console.Run(command, CancellationToken.None, null, verify ?? TimeSpan.FromSeconds(2.5));
                var interesting = res.Lines.Where(l => !l.Contains("LogViewport") && !l.Contains("LogCosmetics") && !l.Contains("LogFX") && !l.Contains("LogRagdoll")
                                                      && (l.Contains("Command not recognized") || l.Contains("Bad or missing") || l.Contains("LogAI: Display: AI difficulty")
                                                          || l.Contains(") ") || l.Contains("LogGameMode: Display: State") || l.Contains("LogExec") || l.Contains("Cheat")))
                                           .Select(l => Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", "")).Take(12).ToList();
                LiveOutput = !res.Sent ? "Not sent: " + res.Detail : res.NotRecognized ? res.Detail : interesting.Count > 0 ? string.Join("\n", interesting) : "Sent: " + command;
                if (!res.Sent)
                {
                    ShowToast("Not sent: " + res.Detail);
                    DebugReport.Auto("console-failed", res.Detail, State, Monitor);
                }
                return res;
            }
            finally { LiveBusy = false; }
        }

        private async Task RunLiveAction(LiveAction action)
        {
            if (action == null) return;
            // Cheats are only needed for cheat commands, but switching them on is harmless in local play.
            var res = await RunConsole("EnableCheats | " + action.Command);
            if (res.Sent) ShowToast(res.NotRecognized ? res.Detail : action.Label + ": done");
        }

        private async Task ReadLiveRules()
        {
            if (liveModeCls == null) return;
            LiveBusy = true;
            try
            {
                var values = await Console.ReadLive(liveModeCls, LiveRules.Select(r => r.Key), CancellationToken.None);
                foreach (var r in LiveRules) r.Current = values.TryGetValue(r.Key, out var v) ? TrimNumber(v) : "?";
                LiveOutput = values.Count > 0 ? "Read " + values.Count + " live values from the match." : "No values came back. Make sure the match has finished loading.";
            }
            finally { LiveBusy = false; }
        }

        private static string TrimNumber(string v)
        {
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Contains("."))
                return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return v;
        }

        private async Task ApplyLiveRuleChanges()
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
            if (bad.Count > 0) { ShowToast("Not a valid value: " + string.Join(", ", bad)); return; }
            if (cmds.Count == 0) return;
            if (restartAfterApply) cmds.Add("AdminRestartRound 0");
            var res = await RunConsole(string.Join(" | ", cmds));
            if (res.Sent)
            {
                ShowToast(cmds.Count - (restartAfterApply ? 1 : 0) + " live change(s) sent" + (restartAfterApply ? ", round restarted" : ""));
                await Task.Delay(800);
                await ReadLiveRules();
            }
        }

        private async Task SendCustom()
        {
            string cmd = customCommand.Trim();
            if (cmd.Length == 0) return;
            if (cheatsForCustom && !cmd.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase)) cmd = "EnableCheats | " + cmd;
            var res = await RunConsole(cmd, TimeSpan.FromSeconds(3));
            if (res.Sent && !res.NotRecognized) CustomCommand = "";
        }

        private async Task CountBots()
        {
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
                if (res.Sent) LiveOutput = "No players came back. Make sure the match has finished loading.";
                return;
            }
            if (bots.Count > 0 && bots.Values.All(b => !b))
            {
                LiveOutput = "No bots have joined yet. They join when the round starts (after you pick a class).";
                ShowToast("No bots yet: they join when the round starts");
                return;
            }
            string humanTeam = teams.FirstOrDefault(kv => bots.TryGetValue(kv.Key, out var isBot) && !isBot).Value ?? "0";
            int mates = teams.Count(kv => kv.Value == humanTeam && bots.TryGetValue(kv.Key, out var isBot) && isBot);
            int enemies = teams.Count(kv => kv.Value != humanTeam);
            LiveOutput = $"Your team: you + {mates} AI teammate{(mates == 1 ? "" : "s")}\nEnemy team: {enemies} bot{(enemies == 1 ? "" : "s")}";
            ShowToast($"You + {mates} AI vs {enemies} enemies");
        }
    }
}
