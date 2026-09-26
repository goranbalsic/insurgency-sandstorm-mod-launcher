using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using SandstormModLauncher.Services;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IDisposable
    {
        public AppState State { get; } = new AppState();
        public GameMonitor Monitor { get; private set; }
        public ConsoleBridge Console { get; private set; }
        public LaunchService Launcher { get; private set; }
        private readonly Dispatcher ui = Application.Current.Dispatcher;
        private readonly DispatcherTimer saveTimer, toastTimer, watchTimer;
        private FileSystemWatcher modWatcher;
        private bool loading = true, suppressProfileSave;
        private string loadingText = "Starting up...";
        private string page = "Play";

        public MainViewModel()
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); Toast = null; };
            watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            watchTimer.Tick += async (s, e) => { watchTimer.Stop(); await RescanMods(true); };

            NavigateCommand = new RelayCommand(p => Page = p as string ?? "Play");
            NewProfileCommand = new AsyncCommand(NewProfile, () => CanSwitchProfile);
            DuplicateProfileCommand = new AsyncCommand(DuplicateProfile, () => CanSwitchProfile);
            RenameProfileCommand = new AsyncCommand(RenameProfile, () => CanSwitchProfile);
            DeleteProfileCommand = new AsyncCommand(DeleteProfile, () => State.Store.Profiles.Count > 1 && CanSwitchProfile);
            DialogCommand = new RelayCommand(p => CloseDialog(p as string));
            InitPlayCommands();
            InitMutatorCommands();
            InitRulesCommands();
            InitModCommands();
            InitLiveCommands();
            InitSettingsCommands();
            InitLaunchCommands();
            InitUpdateCommands();
        }

        // ------------------------------------------------------------------ startup

        public async Task InitializeAsync()
        {
            try
            {
                if (!State.Store.IsLoaded) State.Store.Load();
                State.Rules = RulesDb.LoadEmbedded();
                SetupEngine.CleanStored(State);
                State.Install = GameInstall.Detect(State.Settings.GameDirOverride);
                AppLog.Info($"Game: {State.Install.GameDir ?? "not found"} ({State.Install.Store})");
                Monitor = new GameMonitor();
                Console = new ConsoleBridge(Monitor, () => State.Settings, () => State.Official);
                Launcher = new LaunchService(State, Monitor, Console);
                Monitor.StateChanged += () => ui.BeginInvoke(new Action(OnGameStateChanged));

                KeyBindings.DropDetected += () => ui.BeginInvoke(new Action(() =>
                {
                    RefreshKeyBackups();
                    ShowToast("Your game key bindings changed a lot. A copy from before is in Settings > Game key bindings.");
                }));

                LoadingText = "Reading the game and your mods...";
                await Task.Run(() =>
                {
                    KeyBindings.Backup("launcher started");
                    State.Official = GameCatalog.Load(State.Install, AppPaths.CacheDir, t => ui.BeginInvoke(new Action(() => LoadingText = t)));
                    LoadCommands();
                    State.Mods = ModScanner.Scan(State.Install, State.Settings.ExtraModFolders, AppPaths.CacheDir, t => ui.BeginInvoke(new Action(() => LoadingText = t)));
                    State.Rebuild();
                });

                RebuildProfilesList();
                ApplyProfileToUi();
                BuildMods();
                BuildMutatorPresets();
                RefreshConsoleKeyStatus();
                RefreshKeyBackups();
                StartModWatcher();
                lastPhase = Monitor.Phase;
                lastRunning = Monitor.IsRunning;
                RaiseSettings();
                Page = State.Settings.LastPage == "Mods" || State.Settings.LastPage == "Settings" || State.Settings.LastPage == "Playlists" ? State.Settings.LastPage : State.Settings.LastPage == "Mutators" ? "Mods" : "Play";
                Loading = false;
                OnGameStateChanged();
                if (!State.Install.IsValid)
                {
                    Page = "Settings";
                    await ShowMessage("Game not found", "Insurgency: Sandstorm was not found automatically. Pick the game folder in Settings (the folder that contains \"Insurgency\" and \"Insurgency.exe\").");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Startup failed", ex);
                LoadingText = "Startup failed: " + ex.Message;
            }
        }

        public void Dispose()
        {
            updateTimer?.Stop();
            SaveNow();
            Monitor?.Dispose();
            modWatcher?.Dispose();
        }

        private void StartModWatcher()
        {
            try
            {
                string root = GameInstall.ModioRoot;
                if (!Directory.Exists(root)) return;
                modWatcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite };
                FileSystemEventHandler h = (s, e) =>
                {
                    if (e.FullPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || e.FullPath.EndsWith("state.json", StringComparison.OrdinalIgnoreCase)
                        || Path.GetDirectoryName(e.FullPath)?.EndsWith("mods", StringComparison.OrdinalIgnoreCase) == true)
                        ui.BeginInvoke(new Action(() => { watchTimer.Stop(); watchTimer.Start(); }));
                };
                modWatcher.Created += h; modWatcher.Changed += h; modWatcher.Deleted += h;
                modWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { AppLog.Warn("Mod watcher: " + ex.Message); }
        }

        public async Task RescanMods(bool quiet)
        {
            if (Loading) return;
            try
            {
                if (!quiet) { Loading = true; LoadingText = "Rescanning mods..."; }
                await Task.Run(() =>
                {
                    State.Mods = ModScanner.Scan(State.Install, State.Settings.ExtraModFolders, AppPaths.CacheDir, null);
                    State.Rebuild();
                });
                BuildMods();
                RefreshCatalogUi();
                ShowToast($"Found {State.Mods.Count} mods and {State.AllMutators.Count} mutators");
            }
            finally { Loading = false; }
        }

        private void RefreshCatalogUi()
        {
            suppressProfileSave = true;
            try
            {
                BuildMaps();
                SyncMapSelection();
                BuildMutatorList();
                BuildActiveMutators();
                BuildRules();
            }
            finally { suppressProfileSave = false; }
            UpdatePlan();
        }

        // ------------------------------------------------------------------ navigation / status

        public ICommand NavigateCommand { get; }

        public string Page
        {
            get => page;
            set
            {
                // Mods is a tab of Play since 1.3.7 (older settings and links still say "Mods").
                if (value == "Mods") { value = "Play"; PlayTab = "Mods"; }
                // Playlists are presets in Play > Rules since 1.4.0.
                if (value == "Playlists") { value = "Play"; PlayTab = "Playlists"; }
                if (!Set(ref page, value)) return;
                State.Settings.LastPage = value;
                SaveSettingsSoon();
            }
        }

        public bool Loading { get => loading; set { if (Set(ref loading, value)) Raise(nameof(CanSwitchProfile)); } }
        public string LoadingText { get => loadingText; set => Set(ref loadingText, value); }
        public string AppVersion => "v" + typeof(MainViewModel).Assembly.GetName().Version.ToString(3);

        private string gameStatus = "Checking the game...";
        private string gameStatusKind = "Off";
        public string GameStatus { get => gameStatus; set => Set(ref gameStatus, value); }
        public string GameStatusKind { get => gameStatusKind; set => Set(ref gameStatusKind, value); }
        public bool InMatch => Monitor?.Phase == GamePhase.InMatch;

        private GamePhase lastPhase = GamePhase.NotRunning;
        private bool lastRunning;

        private void OnGameStateChanged()
        {
            if (Monitor == null) return;
            if (Monitor.Phase != lastPhase)
            {
                AppLog.Debug("Game: " + lastPhase + " -> " + Monitor.Phase + (Monitor.CurrentLevel != null ? " (" + Monitor.CurrentLevel + ")" : ""));
                lastPhase = Monitor.Phase;
            }
            bool running = Monitor.IsRunning;
            if (running != lastRunning)
            {
                AppLog.Info(running ? "Game process started (pid " + Monitor.ProcessId + ")" : "Game process exited");
                if (!running && !Loading)
                {
                    // The game saves its settings when it exits: keep a copy and add the extra console key now that it is safe.
                    Task.Run(() => KeyBindings.Backup("game closed")).ContinueWith(_ => ui.BeginInvoke(new Action(() => { RefreshConsoleKeyStatus(); RefreshKeyBackups(); })));
                }
                lastRunning = running;
            }
            GameStatus = Monitor.Describe();
            GameStatusKind = Monitor.Phase == GamePhase.Menu ? "Ready" : Monitor.Phase == GamePhase.InMatch ? "Match"
                           : Monitor.Phase == GamePhase.NotRunning ? "Off" : "Busy";
            Raise(nameof(InMatch));
            UpdateLaunchButton();
            RefreshLive();
        }

        // ------------------------------------------------------------------ toast + dialog

        private string toast;
        public string Toast { get => toast; set => Set(ref toast, value); }

        public void ShowToast(string text)
        {
            Toast = text;
            toastTimer.Stop();
            toastTimer.Start();
        }

        private TaskCompletionSource<string> dialogTcs;
        private bool dialogOpen, dialogHasInput;
        private string dialogTitle, dialogMessage, dialogInput, dialogPrimary, dialogSecondary, dialogTertiary;
        public bool DialogOpen { get => dialogOpen; set => Set(ref dialogOpen, value); }
        public string DialogTitle { get => dialogTitle; set => Set(ref dialogTitle, value); }
        public string DialogMessage { get => dialogMessage; set => Set(ref dialogMessage, value); }
        public string DialogInput { get => dialogInput; set => Set(ref dialogInput, value); }
        public bool DialogHasInput { get => dialogHasInput; set => Set(ref dialogHasInput, value); }
        public string DialogPrimary { get => dialogPrimary; set => Set(ref dialogPrimary, value); }
        public string DialogSecondary { get => dialogSecondary; set => Set(ref dialogSecondary, value); }
        public string DialogTertiary { get => dialogTertiary; set => Set(ref dialogTertiary, value); }
        public ICommand DialogCommand { get; }

        public Task<string> Ask(string title, string message, string primary, string secondary = "Cancel", string tertiary = null)
        {
            dialogTcs?.TrySetResult(null);
            dialogTcs = new TaskCompletionSource<string>();
            DialogTitle = title; DialogMessage = message; DialogHasInput = false;
            DialogPrimary = primary; DialogSecondary = secondary; DialogTertiary = tertiary;
            DialogOpen = true;
            return dialogTcs.Task;
        }

        public async Task<string> Prompt(string title, string message, string initial, string primary = "OK")
        {
            dialogTcs?.TrySetResult(null);
            dialogTcs = new TaskCompletionSource<string>();
            DialogTitle = title; DialogMessage = message; DialogInput = initial ?? ""; DialogHasInput = true;
            DialogPrimary = primary; DialogSecondary = "Cancel"; DialogTertiary = null;
            DialogOpen = true;
            string r = await dialogTcs.Task;
            return r == primary ? (DialogInput ?? "").Trim() : null;
        }

        public Task ShowMessage(string title, string message) => Ask(title, message, "OK", null);

        private void CloseDialog(string result)
        {
            DialogOpen = false;
            var t = dialogTcs;
            dialogTcs = null;
            t?.TrySetResult(result);
        }

        // ------------------------------------------------------------------ profiles

        public ObservableCollection<string> ProfileNames { get; } = new ObservableCollection<string>();
        public ICommand NewProfileCommand { get; }
        public ICommand DuplicateProfileCommand { get; }
        public ICommand RenameProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }

        public Profile Profile => State.Store.Active;

        /// <summary>Profiles cannot change while the catalog is being read or a launch is running.</summary>
        public bool CanSwitchProfile => !loading && !launchRunning;

        public string SelectedProfile
        {
            get => State.Settings.ActiveProfile;
            set
            {
                if (value == null || value == State.Settings.ActiveProfile) return;
                SaveNow();
                State.Settings.ActiveProfile = value;
                SaveSettingsSoon();
                Raise();
                ApplyProfileToUi();
                ShowToast("Profile \"" + value + "\" loaded");
            }
        }

        private void RebuildProfilesList()
        {
            ProfileNames.Clear();
            foreach (var p in State.Store.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) ProfileNames.Add(p.Name);
            Raise(nameof(SelectedProfile));
        }

        private void ApplyProfileToUi()
        {
            suppressProfileSave = true;
            try
            {
                BuildMaps();
                SyncMapSelection();
                BuildMutatorList();
                BuildActiveMutators();
                BuildRules();
                RaiseProfileFields();
            }
            finally { suppressProfileSave = false; }
            UpdatePlan();
        }

        private async Task NewProfile()
        {
            string name = await Prompt("New profile", "Name for the new profile:", "My setup", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = UniqueProfileName(name);
            var p = new Profile { Name = name, MapKey = Profile.MapKey, ScenarioId = Profile.ScenarioId, Lighting = Profile.Lighting };
            State.Store.Profiles.Add(p);
            State.Store.SaveProfile(p);
            RebuildProfilesList();
            SelectedProfile = name;
        }

        private async Task DuplicateProfile()
        {
            string name = await Prompt("Duplicate profile", "Name for the copy:", Profile.Name + " copy", "Duplicate");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = UniqueProfileName(name);
            var p = Profile.Clone(name);
            State.Store.Profiles.Add(p);
            State.Store.SaveProfile(p);
            RebuildProfilesList();
            SelectedProfile = name;
        }

        private async Task RenameProfile()
        {
            string name = await Prompt("Rename profile", "New name:", Profile.Name, "Rename");
            if (string.IsNullOrWhiteSpace(name) || name == Profile.Name) return;
            name = UniqueProfileName(name, Profile);
            State.Store.RenameProfile(Profile, name);
            State.Settings.ActiveProfile = name;
            SaveSettingsSoon();
            RebuildProfilesList();
        }

        private async Task DeleteProfile()
        {
            if (await Ask("Delete profile", "Delete \"" + Profile.Name + "\"? This cannot be undone.", "Delete") != "Delete") return;
            State.Store.DeleteProfile(Profile);
            State.Settings.ActiveProfile = State.Store.Profiles[0].Name;
            SaveSettingsSoon();
            RebuildProfilesList();
            ApplyProfileToUi();
        }

        /// <summary>The name, with a number added when another profile already has it (renaming ignores the profile itself).</summary>
        private string UniqueProfileName(string name, Profile self = null) => State.Store.UniqueName(name, self);

        public void ProfileChanged()
        {
            if (suppressProfileSave) return;
            saveTimer.Stop();
            saveTimer.Start();
            UpdatePlan();
        }

        private void SaveSettingsSoon()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        public void SaveNow()
        {
            try
            {
                if (State.Store.Profiles.Count > 0) State.Store.SaveProfile(Profile);
                State.Store.SaveSettings();
            }
            catch (Exception ex) { AppLog.Error("Save failed", ex); }
        }

        public static void Open(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Warn("Could not open " + target + ": " + ex.Message); }
        }
    }
}
