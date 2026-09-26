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
        private string launchResult, launchTitle = "Launch", launchSubtitle = "", launchHeadline = "";
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
        }

        public string PlanError => currentPlan?.Error;
        public string PlanCommand => currentPlan?.OpenCommand ?? "";
        public string PlanIni => string.IsNullOrWhiteSpace(currentPlan?.GameIniBlock) ? "; No Game.ini changes. The game uses its default rules." : currentPlan.GameIniBlock;
        public string PlanAfterLoad => currentPlan == null || currentPlan.AfterLoad.Count == 0 ? "Nothing. The match uses the rules above as loaded." : string.Join("\n", currentPlan.AfterLoad);

        public string MapSummary => currentPlan?.Scenario == null ? "No map selected"
            : (currentPlan.Map?.DisplayName ?? currentPlan.Scenario.MapKey) + " · " + (Profile.Lighting == "Night" ? "Night" : "Day");

        public string ModeSummary => currentPlan?.Scenario == null ? "" : currentPlan.ModeTitle + (string.IsNullOrEmpty(currentPlan.Scenario.Side) ? "" : " · " + currentPlan.Scenario.Side);

        public string SquadSummary
        {
            get
            {
                var mode = CurrentMode;
                if (mode == null) return "Game defaults";
                string diff = AiDifficulty.ToString("0.00", CultureInfo.InvariantCulture);
                if (mode.Coop)
                {
                    string who = Teammates == 0 ? "Lone wolf" : "You + " + Teammates + " AI";
                    return who + " vs " + SoloEnemies + " enemies · AI " + diff;
                }
                if (mode.Defaults.ContainsKey("bBots"))
                    return !BotsEnabled ? "No bots · players only"
                         : BotQuota <= 1 ? "You vs 1 bot · AI " + diff
                         : "You + " + (BotQuota - 1) + " AI vs " + BotQuota + " bots · AI " + diff;
                return "Versus · AI " + diff;
            }
        }

        public string MutatorSummary
        {
            get
            {
                if (!Profile.MutatorsEnabled) return Profile.Mutators.Count > 0 ? "Mutators off (" + Profile.Mutators.Count + " saved)" : "No mutators";
                int n = Profile.Mutators.Count;
                if (n == 0) return "No mutators";
                var names = Profile.Mutators.Select(id => State.FindMutator(id)?.DisplayName ?? id).ToList();
                return n <= 2 ? string.Join(", ", names) : names[0] + ", " + names[1] + " +" + (n - 2) + " more";
            }
        }

        // ------------------------------------------------------------------ launch button

        public bool CanLaunch => !launchRunning && currentPlan?.IsValid == true && State.Install?.IsValid == true && !Loading;
        public string LaunchTitle { get => launchTitle; set => Set(ref launchTitle, value); }
        public string LaunchSubtitle { get => launchSubtitle; set => Set(ref launchSubtitle, value); }

        private void UpdateLaunchButton()
        {
            Raise(nameof(CanLaunch));
            if (Monitor == null) { LaunchTitle = "Launch"; LaunchSubtitle = "Getting ready..."; return; }
            if (launchRunning) { LaunchTitle = "Launching..."; LaunchSubtitle = "Follow the steps in the window"; return; }
            if (currentPlan != null && !currentPlan.IsValid) { LaunchTitle = "Launch"; LaunchSubtitle = currentPlan.Error; return; }
            bool restart = currentPlan != null && Launcher != null && Launcher.NeedsRestart(currentPlan);
            switch (Monitor.Phase)
            {
                case GamePhase.NotRunning:
                    LaunchTitle = State.Settings.AutoStartGame ? "Start and launch" : "Launch";
                    LaunchSubtitle = State.Settings.AutoStartGame ? "Starts the game, then loads your match" : "Start the game first, then press Launch";
                    break;
                case GamePhase.InMatch:
                    LaunchTitle = "Launch";
                    LaunchSubtitle = restart ? "Restarts the game so the AI teammate count applies" : "Switches the running match to this setup";
                    break;
                case GamePhase.Menu:
                    LaunchTitle = "Launch";
                    LaunchSubtitle = restart ? "Restarts the game so the AI teammate count applies" : "Game is ready at the main menu";
                    break;
                default:
                    LaunchTitle = "Launch";
                    LaunchSubtitle = "Waits for the game to finish loading";
                    break;
            }
        }

        // ------------------------------------------------------------------ overlay state

        public bool LaunchOverlayOpen { get => launchOverlayOpen; set => Set(ref launchOverlayOpen, value); }
        public bool LaunchRunning
        {
            get => launchRunning;
            set { if (Set(ref launchRunning, value)) { RaiseMany(nameof(CanLaunch), nameof(CanSwitchProfile)); UpdateLaunchButton(); CommandManager.InvalidateRequerySuggested(); } }
        }
        public bool LaunchSucceeded { get => launchSucceeded; set => Set(ref launchSucceeded, value); }
        public string LaunchResult { get => launchResult; set => Set(ref launchResult, value); }
        public string LaunchHeadline { get => launchHeadline; set => Set(ref launchHeadline, value); }
        public double LaunchProgress { get => launchProgress; set => Set(ref launchProgress, value); }
        public bool LaunchFailed => !launchRunning && !launchSucceeded && launchResult != null;

        private async Task Launch(bool forceRestart)
        {
            UpdatePlan();
            var plan = currentPlan;
            if (plan == null || !plan.IsValid) { await ShowMessage("Nothing to launch yet", plan?.Error ?? "Pick a map and a scenario first."); return; }
            if (!State.Install.IsValid) { Page = "Settings"; await ShowMessage("Game not found", "Set the Insurgency: Sandstorm folder in Settings first."); return; }
            SaveNow();

            var options = new LaunchOptions { ForceRestart = forceRestart };
            if (!forceRestart && Launcher.NeedsRestart(plan))
            {
                switch (State.Settings.RestartPolicy)
                {
                    case "Always": options.RestartIfNeeded = true; break;
                    case "Never": break;
                    default:
                        string answer = await Ask("Restart the game?",
                            "The game was started with a different number of AI teammates (or a different official ruleset). The game only reads that when it starts, so it has to restart once for this setup to be exact.\n\nEverything else (enemies, difficulty, rules, mutators) works without a restart.",
                            "Restart and launch", "Launch without restart", "Cancel");
                        if (answer == null || answer == "Cancel") return;
                        options.RestartIfNeeded = answer == "Restart and launch";
                        break;
                }
            }

            LaunchSteps.Clear();
            foreach (var t in LaunchService.Steps) LaunchSteps.Add(new LaunchStepItem { Title = t, State = StepState.Pending });
            LaunchWarnings.Clear();
            LaunchResult = null;
            LaunchSucceeded = false;
            LaunchProgress = 0;
            LaunchHeadline = plan.Title;
            LaunchOverlayOpen = true;
            LaunchRunning = true;
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
            if (!report.Success && report.Message != "Launch cancelled.")
                DebugReport.Auto("launch-failed", report.Message, State, Monitor);
            if (report.Success)
            {
                AppLog.Info("Launched " + plan.Title);
                Page = "Play";
                PlayTab = "Live";
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
