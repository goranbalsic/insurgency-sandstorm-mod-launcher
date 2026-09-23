using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SandstormModLauncher.Game;
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
        public ICommand RunLiveActionCommand { get; private set; }
        public ICommand ReadLiveRulesCommand { get; private set; }
        public ICommand ApplyLiveRulesCommand { get; private set; }
        public ICommand SendCustomCommand { get; private set; }
        public ICommand CountBotsCommand { get; private set; }
        private string liveOutput = "", customCommand = "", liveModeName = "";
        private bool liveBusy, restartAfterApply = true;
        private string liveModeCls;

        private void InitLiveCommands()
        {
            RunLiveActionCommand = new AsyncCommand(p => RunLiveAction(p as LiveAction), p => CanLive);
            ReadLiveRulesCommand = new AsyncCommand(ReadLiveRules, () => CanLive && LiveRules.Count > 0);
            ApplyLiveRulesCommand = new AsyncCommand(ApplyLiveRuleChanges, () => CanLive && LiveRules.Any(r => !string.IsNullOrWhiteSpace(r.Value)));
            SendCustomCommand = new AsyncCommand(SendCustom, () => Monitor?.IsRunning == true && !string.IsNullOrWhiteSpace(customCommand) && !liveBusy);
            CountBotsCommand = new AsyncCommand(CountBots, () => CanLive);

            LiveGroup G(string title, bool cheat, params (string label, string cmd, string tip)[] actions) => new LiveGroup
            {
                Title = title, Cheat = cheat, Badge = cheat ? "CHEAT" : "ADMIN",
                Actions = actions.Select(a => new LiveAction { Group = title, Label = a.label, Command = a.cmd, Tip = a.tip, Cheat = cheat }).ToList()
            };
            LiveGroups.Add(G("ROUND", false,
                ("Restart round", "AdminRestartRound 0", "Starts the round again with the current settings."),
                ("Restart and switch sides", "AdminRestartRound 1", "Restarts the round with the teams swapped."),
                ("Add 5 minutes", "AdminExtendRoundTimer 300", "Adds time to the round clock."),
                ("End the match", "AdminForceGameOver", "Ends the match immediately.")));
            LiveGroups.Add(G("OBJECTIVES", true,
                ("Capture current objective", "InstaCap", "Captures the objective you are attacking."),
                ("Start a counter-attack", "CheatCounterAttack", "Checkpoint: triggers a counter-attack now."),
                ("Finish the counter-attack", "CheatFinishCounterAttack", "Checkpoint: ends the running counter-attack.")));
            LiveGroups.Add(G("BOTS", true,
                ("Respawn enemy bots", "AIRespawnEnemyBots", "Brings enemy bots back."),
                ("Respawn AI teammates", "AIRespawnFriendlyBots", "Brings dead AI teammates back."),
                ("Remove all enemies", "AIPurgeEnemy", "Removes every enemy bot."),
                ("Freeze or unfreeze all AI", "AIToggle", "Stops every bot in place, press again to resume."),
                ("Bots ignore me", "AINoTargetPlayer", "Enemies stop targeting you.")));
            LiveGroups.Add(G("YOUR SOLDIER", true,
                ("God mode", "GodMode", "You can't take damage. Press again to turn off."),
                ("Refill ammo and gear", "ResupplyNow", "Instant resupply."),
                ("Give 10 supply points", "GiveSupplyPointsUnrestricted 10", "Extra supply for your loadout."),
                ("Respawn me", "RespawnMe", "Respawns your soldier."),
                ("Fly through walls", "Noclip", "Free movement through geometry. Press again to land.")));
            LiveGroups.Add(G("TEAM", false,
                ("Respawn every player", "AdminRespawnAllPlayers", "Respawns all players on both teams."),
                ("Free camera", "EnableCheats | ToggleDebugCamera", "Detached camera for screenshots. Press again to return.")));
        }

        public bool CanLive => Monitor?.Phase == GamePhase.InMatch && !liveBusy;
        public bool LiveBusy { get => liveBusy; set { if (Set(ref liveBusy, value)) { Raise(nameof(CanLive)); CommandManager.InvalidateRequerySuggested(); } } }
        public string LiveOutput { get => liveOutput; set => Set(ref liveOutput, value); }
        public string CustomCommand { get => customCommand; set => Set(ref customCommand, value ?? ""); }
        public string LiveModeName { get => liveModeName; set => Set(ref liveModeName, value); }
        public bool RestartAfterApply { get => restartAfterApply; set => Set(ref restartAfterApply, value); }

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
            LiveModeName = mode?.Name ?? "";
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
                                                          || l.Contains(") ") || l.Contains("LogGameMode: Display: State") || l.Contains("LogExec")))
                                           .Select(l => Regex.Replace(l, @"^\[[^\]]*\]\[[^\]]*\]", "")).Take(12).ToList();
                LiveOutput = !res.Sent ? "Not sent: " + res.Detail : interesting.Count > 0 ? string.Join("\n", interesting) : "Sent: " + command;
                return res;
            }
            finally { LiveBusy = false; }
        }

        private async Task RunLiveAction(LiveAction action)
        {
            if (action == null) return;
            string cmd = action.Cheat && !action.Command.StartsWith("EnableCheats") ? "EnableCheats | " + action.Command : action.Command;
            var res = await RunConsole(cmd);
            if (res.Sent) ShowToast(res.NotRecognized ? "The game did not recognise that command" : action.Label + ": sent");
        }

        private async Task ReadLiveRules()
        {
            if (liveModeCls == null) return;
            LiveBusy = true;
            try
            {
                var values = await Console.ReadLive(liveModeCls, LiveRules.Select(r => r.Key), CancellationToken.None);
                foreach (var r in LiveRules) r.Current = values.TryGetValue(r.Key, out var v) ? Trim(v) : "?";
                LiveOutput = values.Count > 0 ? "Read " + values.Count + " live values from the match." : "No values came back. Make sure the match has finished loading.";
            }
            finally { LiveBusy = false; }
        }

        private static string Trim(string v)
        {
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && v.Contains("."))
                return d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return v;
        }

        private async Task ApplyLiveRuleChanges()
        {
            var cmds = LiveRules.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r => "AdminSetGamemodeProperty " + r.Key + " " + r.Value.Trim()).ToList();
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
            var res = await RunConsole(cmd, TimeSpan.FromSeconds(3));
            if (res.Sent) CustomCommand = "";
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
            if (teams.Count == 0) return;
            if (bots.Count > 0 && bots.Values.All(b => !b))
            {
                LiveOutput = "No bots have joined yet. They join when the round starts (after you pick a class).";
                ShowToast("No bots yet: they join when the round starts");
                return;
            }
            string humanTeam = teams.FirstOrDefault(kv => bots.TryGetValue(kv.Key, out var isBot) && !isBot).Value ?? "0";
            int mates = teams.Count(kv => kv.Value == humanTeam && bots.TryGetValue(kv.Key, out var isBot) && isBot);
            int enemies = teams.Count(kv => kv.Value != humanTeam);
            LiveOutput = $"Your team: you + {mates} AI teammate{(mates == 1 ? "" : "s")}\nEnemies alive right now: {enemies}";
            ShowToast($"You + {mates} AI vs {enemies} enemies alive");
        }
    }
}
