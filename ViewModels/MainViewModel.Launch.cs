using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Services;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<LaunchStepItem> LaunchSteps { get; } = new ObservableCollection<LaunchStepItem>();
        public ObservableCollection<string> LaunchWarnings { get; } = new ObservableCollection<string>();
        public ICommand LaunchCommand { get; private set; }
        public ICommand CancelLaunchCommand { get; private set; }
        public ICommand CloseLaunchCommand { get; private set; }
        public ICommand CopyCommandCommand { get; private set; }
        public ICommand StopGameCommand { get; private set; }
        public ICommand StartGameCommand { get; private set; }
        public ICommand RetryLaunchCommand { get; private set; }

        private LaunchPlan currentPlan;
        private CancellationTokenSource launchCts;
        private bool launchOverlayOpen, launchRunning, launchSucceeded;
        private string launchResult, launchTitle = T("Launch"), launchSubtitle = "", launchHeadline = "";
        private double launchProgress;

        private void InitLaunchCommands()
        {
            LaunchCommand = new AsyncCommand(() => Launch(false), () => CanLaunch);
            RetryLaunchCommand = new AsyncCommand(() => Launch(false), () => !launchRunning);
            CancelLaunchCommand = new RelayCommand(() => launchCts?.Cancel(), () => launchRunning);
            CloseLaunchCommand = new RelayCommand(() => LaunchOverlayOpen = false, () => !launchRunning);
            CopyCommandCommand = new RelayCommand(() => CopyTextCommand.Execute(currentPlan?.OpenCommand), () => currentPlan?.IsValid == true);
            StopGameCommand = new AsyncCommand(StopGame, () => Monitor?.IsRunning == true && !launchRunning);
            StartGameCommand = new AsyncCommand(StartGameOnly, () => Monitor != null && !Monitor.IsRunning && State.Install?.IsValid == true && !launchRunning);
        }

        // ------------------------------------------------------------------ plan

        public LaunchPlan CurrentPlan { get => currentPlan; private set => Set(ref currentPlan, value); }

        public void UpdatePlan()
        {
            if (State.Rules == null || State.Install == null) return;
            try { CurrentPlan = LaunchPlanner.Build(Profile, State); }
            catch (Exception ex) { AppLog.Error("Could not build the launch plan", ex); CurrentPlan = null; }
            RaiseMany(nameof(PlanCommand), nameof(PlanIni), nameof(PlanAfterLoad), nameof(MapSummary), nameof(ModeSummary), nameof(SquadSummary),
                      nameof(MutatorSummary), nameof(RulesSummary), nameof(CanLaunch), nameof(PlanError), nameof(ActivePresetName));
            UpdateLaunchButton();
            UpdateServerPlan();
        }

        public string PlanError => currentPlan?.Error;
        public string PlanCommand => currentPlan?.OpenCommand ?? "";
        public string PlanIni => string.IsNullOrWhiteSpace(currentPlan?.GameIniBlock) ? T("; No Game.ini changes. The game uses its default rules.") : currentPlan.GameIniBlock;
        public string PlanAfterLoad => currentPlan == null || currentPlan.AfterLoad.Count == 0 ? T("Nothing. The match uses the rules above as loaded.") : string.Join("\n", currentPlan.AfterLoad);

        public string MapSummary => currentPlan?.Scenario == null ? T("No map selected")
            : (currentPlan.Map?.DisplayName ?? currentPlan.Scenario.MapKey) + " · " + (Profile.Lighting == "Night" ? T("Night") : T("Day"));

        public string ModeSummary => currentPlan?.Scenario == null ? "" : T(currentPlan.ModeTitle) + (string.IsNullOrEmpty(currentPlan.Scenario.Side) ? "" : " · " + currentPlan.Scenario.Side);

        public string SquadSummary
        {
            get
            {
                var mode = CurrentMode;
                if (mode == null) return T("Game defaults");
                string diff = AiDifficulty.ToString("0.00", CultureInfo.InvariantCulture);
                if (mode.Coop)
                {
                    return Teammates == 0 ? F("Lone wolf vs {0} enemies · AI {1}", SoloEnemies, diff) : F("You + {0} AI vs {1} enemies · AI {2}", Teammates, SoloEnemies, diff);
                }
                if (mode.Defaults.ContainsKey("bBots"))
                    return !BotsEnabled ? T("No bots · players only")
                         : NoTeams ? F("You vs {0} bots · AI {1}", BotQuota, diff)
                         : BotQuota <= 1 ? F("You vs 1 bot · AI {0}", diff)
                         : F("You + {0} AI vs {1} bots · AI {2}", BotQuota - 1, BotQuota, diff);
                return F("Versus · AI {0}", diff);
            }
        }

        public string MutatorSummary
        {
            get
            {
                if (!Profile.MutatorsEnabled) return Profile.Mutators.Count > 0 ? F("Mutators off ({0} saved)", Profile.Mutators.Count) : T("No mutators");
                int n = Profile.Mutators.Count;
                if (n == 0) return T("No mutators");
                var names = Profile.Mutators.Select(id => State.FindMutator(id)?.DisplayName ?? id).ToList();
                return n <= 2 ? string.Join(", ", names) : F("{0}, {1} +{2} more", names[0], names[1], n - 2);
            }
        }

        // ------------------------------------------------------------------ launch button

        public bool CanLaunch => !launchRunning && currentPlan?.IsValid == true && State.Install?.IsValid == true && !Loading;
        public string LaunchTitle { get => launchTitle; set { if (Set(ref launchTitle, value)) RaiseMainAction(); } }
        public string LaunchSubtitle { get => launchSubtitle; set { if (Set(ref launchSubtitle, value)) RaiseMainAction(); } }

        private void UpdateLaunchButton()
        {
            Raise(nameof(CanLaunch));
            RaiseMainAction();
            if (Monitor == null) { LaunchTitle = T("Launch"); LaunchSubtitle = T("Getting ready..."); return; }
            if (launchRunning) { LaunchTitle = T("Launching..."); LaunchSubtitle = T("Follow the steps in the window"); return; }
            if (currentPlan != null && !currentPlan.IsValid) { LaunchTitle = T("Launch"); LaunchSubtitle = currentPlan.Error; return; }
            bool restart = currentPlan != null && Launcher != null && Launcher.NeedsRestart(currentPlan);
            switch (Monitor.Phase)
            {
                case GamePhase.NotRunning:
                    LaunchTitle = State.Settings.AutoStartGame ? T("Start and launch") : T("Launch");
                    LaunchSubtitle = State.Settings.AutoStartGame ? T("Starts the game, then loads your match") : T("Start the game first, then press Launch");
                    break;
                case GamePhase.InMatch:
                    LaunchTitle = T("Launch");
                    LaunchSubtitle = restart ? T("Restarts the game so the AI teammate count applies") : T("Switches the running match to this setup");
                    break;
                case GamePhase.Menu:
                    LaunchTitle = T("Launch");
                    LaunchSubtitle = restart ? T("Restarts the game so the AI teammate count applies") : T("Game is ready at the main menu");
                    break;
                default:
                    LaunchTitle = T("Launch");
                    LaunchSubtitle = T("Waits for the game to finish loading");
                    break;
            }
        }

        // ------------------------------------------------------------------ overlay state

        public bool LaunchOverlayOpen { get => launchOverlayOpen; set => Set(ref launchOverlayOpen, value); }
        public bool LaunchRunning
        {
            get => launchRunning;
            set { if (Set(ref launchRunning, value)) { RaiseMany(nameof(CanLaunch), nameof(CanChangeSetup)); UpdateLaunchButton(); CommandManager.InvalidateRequerySuggested(); } }
        }
        public bool LaunchSucceeded { get => launchSucceeded; set => Set(ref launchSucceeded, value); }
        public string LaunchResult { get => launchResult; set => Set(ref launchResult, value); }
        public string LaunchHeadline { get => launchHeadline; set => Set(ref launchHeadline, value); }
        public double LaunchProgress { get => launchProgress; set => Set(ref launchProgress, value); }
        public bool LaunchFailed => !launchRunning && !launchSucceeded && launchResult != null;

        // From the press until the launch runs (the game is checked, questions asked) the setup may not change: the plan is made.
        private bool launchPreparing;

        private void SetLaunchPreparing(bool value)
        {
            if (launchPreparing == value) return;
            launchPreparing = value;
            Raise(nameof(CanChangeSetup));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task Launch(bool forceRestart)
        {
            SetLaunchPreparing(true);
            try { await LaunchCore(forceRestart); }
            finally { SetLaunchPreparing(false); }
        }

        private async Task LaunchCore(bool forceRestart)
        {
            UpdatePlan();
            var plan = currentPlan;
            if (plan == null || !plan.IsValid) { await ShowMessage("Nothing to launch yet", plan?.Error ?? "Pick a map and a scenario first."); return; }
            if (!State.Install.IsValid) { Page = "Settings"; await ShowMessage("Game not found", "Set the Insurgency: Sandstorm folder in Settings first."); return; }
            SaveNow();

            var options = new LaunchOptions
            {
                ForceRestart = forceRestart,
                AllowConsole = State.Settings.AllowConsoleTyping,
                // The player launched from here: once the match is ready, the game comes to the front.
                BringToFront = Application.Current?.MainWindow?.IsActive == true,
            };
            // A game started before the launcher set up RCON cannot be reached without typing into it.
            if (!forceRestart && Monitor.IsRunning)
            {
                string problem = await Launcher.RconProblem();   // null for a reachable or merely busy game
                if (problem != null)
                {
                    AppLog.Info("RCON not reachable before launch: " + problem);
                    string answer = await Ask("Restart the game?",
                        T("The launcher cannot reach the running game directly (RCON), probably because the game was started before the launcher set it up. ") +
                        T("Restarting the game once fixes that for good; after that, maps load without the launcher typing anything.") +
                        (options.AllowConsole ? T("\n\nOr type the command into the game console this time (the older way, needs the game in front).") : ""),
                        "Restart and launch", options.AllowConsole ? "Type into the console" : "Cancel", options.AllowConsole ? "Cancel" : null);
                    if (answer == null || answer == "Cancel") return;
                    if (answer == "Restart and launch") { options.ForceRestart = true; forceRestart = true; }
                }
            }
            if (!forceRestart && Launcher.NeedsRestart(plan))
            {
                switch (State.Settings.RestartPolicy)
                {
                    case "Always": options.RestartIfNeeded = true; break;
                    case "Never": break;
                    default:
                        string answer = await Ask("Restart the game?",
                            T("The game was started with a different number of AI teammates (or a different official ruleset). The game only reads that when it starts, so it has to restart once for this setup to be exact.\n\nEverything else (enemies, difficulty, rules, mutators) works without a restart."),
                            "Restart and launch", "Launch without restart", "Cancel");
                        if (answer == null || answer == "Cancel") return;
                        options.RestartIfNeeded = answer == "Restart and launch";
                        break;
                }
            }

            LaunchSteps.Clear();
            foreach (var t in LaunchService.Steps) LaunchSteps.Add(new LaunchStepItem { Title = T(t), State = StepState.Pending });
            LaunchWarnings.Clear();
            LaunchResult = null;
            LaunchSucceeded = false;
            LaunchProgress = 0;
            LaunchHeadline = plan.Title;
            LaunchOverlayOpen = true;
            LaunchRunning = true;
            SetLaunchPreparing(false);
            Raise(nameof(LaunchFailed));
            launchCts = new CancellationTokenSource();
            var progress = new Progress<LaunchUpdate>(u =>
            {
                if (u.Step < 0 || u.Step >= LaunchSteps.Count) return;
                var s = LaunchSteps[u.Step];
                s.State = u.State;
                if (u.Detail != null) s.Detail = u.Detail;
                int done = LaunchSteps.Count(x => x.State == StepState.Done || x.State == StepState.Skipped || x.State == StepState.Warning);
                LaunchProgress = Math.Max(LaunchProgress, (double)done / LaunchSteps.Count);
            });
            LaunchReport report;
            try { report = await Launcher.Run(plan, options, progress, launchCts.Token); }
            finally { LaunchRunning = false; }
            SaveSettingsSoon();
            LaunchSucceeded = report.Success;
            LaunchResult = report.Message;
            foreach (var w in report.Warnings.Distinct()) LaunchWarnings.Add(w);
            if (report.Success) LaunchProgress = 1;
            Raise(nameof(LaunchFailed));
            UpdateLaunchButton();
            if (!report.Success && report.Message != T("Launch cancelled."))
                DebugReport.Auto("launch-failed", report.Message, State, Monitor);
            if (report.Success)
            {
                AppLog.Info("Launched " + plan.Title);
                Page = "Live";
                if (State.Settings.MinimizeOnLaunch && Application.Current?.MainWindow != null) Application.Current.MainWindow.WindowState = WindowState.Minimized;
                if (LaunchWarnings.Count == 0)
                {
                    await Task.Delay(2500);
                    if (!launchRunning && launchSucceeded) LaunchOverlayOpen = false;
                }
            }
        }

        private async Task StopGame()
        {
            if (await Ask("Close the game?", "Close Insurgency: Sandstorm now?", "Close game") != "Close game") return;
            try { await Launcher.StopGame(CancellationToken.None); ShowToast("Game closed"); }
            catch (Exception ex) { await ShowMessage("Could not close the game", ex.Message); }
        }

        private async Task StartGameOnly()
        {
            UpdatePlan();
            try
            {
                Launcher.StartOnly(currentPlan);
                SaveSettingsSoon();
                ShowToast("Starting Insurgency: Sandstorm...");
            }
            catch (Exception ex) { await ShowMessage("Could not start the game", ex.Message); }
        }
    }
}
