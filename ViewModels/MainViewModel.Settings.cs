using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using SandstormModLauncher.Services;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<string> ExtraModFolders { get; } = new ObservableCollection<string>();
        public ICommand SetupF10Command { get; private set; }
        public ICommand ChangeGameDirCommand { get; private set; }
        public ICommand ResetGameDirCommand { get; private set; }
        public ICommand AddModFolderCommand { get; private set; }
        public ICommand RemoveModFolderCommand { get; private set; }
        public ICommand ImportLegacyCommand { get; private set; }
        public ICommand OpenPathCommand { get; private set; }
        public ICommand CreateShortcutCommand { get; private set; }
        public ICommand ClearCacheCommand { get; private set; }
        public ICommand RemoveIniRulesCommand { get; private set; }
        public ICommand DiagnosticsCommand { get; private set; }
        public ICommand RefreshConsoleKeyCommand { get; private set; }
        public ICommand AddCustomMapCommand { get; private set; }
        public ICommand EditCustomMapCommand { get; private set; }
        public ICommand SaveCustomMapCommand { get; private set; }
        public ICommand DeleteCustomMapCommand { get; private set; }
        public ICommand CancelCustomMapCommand { get; private set; }

        private string consoleKeyStatus = "", consoleKeysText = "";
        private bool consoleKeyOk = true, hasF10, f10Pending;

        private void InitSettingsCommands()
        {
            SetupF10Command = new RelayCommand(SetupF10);
            ChangeGameDirCommand = new AsyncCommand(ChangeGameDir);
            ResetGameDirCommand = new AsyncCommand(async () => { State.Settings.GameDirOverride = ""; await ReloadEverything("Looking for the game..."); }, () => !string.IsNullOrEmpty(State.Settings.GameDirOverride));
            AddModFolderCommand = new AsyncCommand(AddModFolder);
            RemoveModFolderCommand = new AsyncCommand(async p =>
            {
                if (!(p is string f)) return;
                State.Settings.ExtraModFolders.RemoveAll(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
                ExtraModFolders.Remove(f);
                SaveSettingsSoon();
                await RescanMods(false);
            });
            ImportLegacyCommand = new AsyncCommand(ImportLegacyInteractive);
            OpenPathCommand = new RelayCommand(p => OpenKnownPath(p as string));
            CreateShortcutCommand = new RelayCommand(CreateShortcut);
            ClearCacheCommand = new AsyncCommand(ClearCache);
            RemoveIniRulesCommand = new AsyncCommand(RemoveIniRules);
            DiagnosticsCommand = new AsyncCommand(WriteDiagnostics);
            RefreshConsoleKeyCommand = new RelayCommand(RefreshConsoleKeyStatus);
            AddCustomMapCommand = new RelayCommand(() => OpenMapEditor(null));
            EditCustomMapCommand = new RelayCommand(p => OpenMapEditor((p as MapItem)?.Custom));
            SaveCustomMapCommand = new RelayCommand(SaveCustomMap, () => !string.IsNullOrWhiteSpace(editMapLevel) && !string.IsNullOrWhiteSpace(editMapScenario));
            DeleteCustomMapCommand = new AsyncCommand(p => DeleteCustomMap((p as MapItem)?.Custom ?? editingMap));
            CancelCustomMapCommand = new RelayCommand(() => MapEditorOpen = false);
            HelpCommand = new AsyncCommand(() => ShowMessage("How it works",
                "1. PLAY: pick a map and scenario, then set your squad and the enemies. Every value starts at that mode's own default. Changed values turn gold.\n\n" +
                "2. RULES has every match setting, play-style presets and the official rulesets. MUTATORS: tick what you want, in load order. PLAYLISTS sets up any official online playlist for offline play.\n\n" +
                "3. Press LAUNCH (or F5). The launcher writes your rules to Game.ini, starts the game if needed, waits for the main menu and sends the match through the console for you. Keep your hands off the keyboard for those few seconds.\n\n" +
                "4. LIVE works during a match: restart rounds, add time, respawn bots, change rules, without typing commands.\n\n" +
                "If the game never reacts, open Settings > Console key, press \"Set up F10\" and restart the game once."));
        }

        public ICommand HelpCommand { get; private set; }

        private IntPtr OwnerHandle => Application.Current?.MainWindow == null ? IntPtr.Zero : new WindowInteropHelper(Application.Current.MainWindow).Handle;

        private void RaiseSettings()
        {
            ExtraModFolders.Clear();
            foreach (var f in State.Settings.ExtraModFolders) ExtraModFolders.Add(f);
            RaiseMany(nameof(GameDir), nameof(GameStore), nameof(GameBuild), nameof(GameFound), nameof(GameDirIsManual), nameof(AutoStartGame), nameof(MinimizeOnLaunch),
                      nameof(SoloGameFlag), nameof(ApplyLiveRules), nameof(InputMethod), nameof(KeyDelayMs), nameof(RestartPolicy), nameof(LaunchArgs),
                      nameof(StartTimeoutSec), nameof(ConsoleKeyPreference), nameof(ConsoleKeyChoices), nameof(DataDir), nameof(IsPortable), nameof(ModioRoot));
        }

        // ------------------------------------------------------------------ game install

        public string GameDir => State.Install?.GameDir ?? "Not found";
        public string GameStore => State.Install?.Store ?? "";
        public string GameBuild => string.IsNullOrEmpty(State.Install?.BuildId) ? "" : "Build " + State.Install.BuildId;
        public bool GameFound => State.Install?.IsValid == true;
        public bool GameDirIsManual => !string.IsNullOrEmpty(State.Settings.GameDirOverride);
        public string DataDir => AppPaths.DataDir;
        public bool IsPortable => AppPaths.DataDir.StartsWith(AppPaths.ExeDir, StringComparison.OrdinalIgnoreCase);
        public string ModioRoot => GameInstall.ModioRoot;

        private async Task ChangeGameDir()
        {
            string dir = FolderPicker.Pick(OwnerHandle, "Pick the Insurgency: Sandstorm folder (it contains \"Insurgency\" and \"Insurgency.exe\")", State.Install?.GameDir);
            if (dir == null) return;
            if (!GameInstall.IsGameDir(dir))
            {
                string parent = Directory.GetParent(dir)?.FullName;
                if (parent != null && GameInstall.IsGameDir(parent)) dir = parent;
                else { await ShowMessage("That is not the game folder", "Pick the folder that contains the \"Insurgency\" folder, usually ...\\steamapps\\common\\sandstorm."); return; }
            }
            State.Settings.GameDirOverride = dir;
            await ReloadEverything("Reading the game...");
        }

        private async Task ReloadEverything(string message)
        {
            Loading = true;
            LoadingText = message;
            try
            {
                State.Install = GameInstall.Detect(State.Settings.GameDirOverride);
                await Task.Run(() =>
                {
                    State.Official = GameCatalog.Load(State.Install, AppPaths.CacheDir, t => ui.BeginInvoke(new Action(() => LoadingText = t)));
                    State.Mods = ModScanner.Scan(State.Install, State.Settings.ExtraModFolders, AppPaths.CacheDir, t => ui.BeginInvoke(new Action(() => LoadingText = t)));
                    State.Rebuild();
                });
                BuildMods();
                RefreshCatalogUi();
                RefreshConsoleKeyStatus();
                SaveSettingsSoon();
            }
            catch (Exception ex) { AppLog.Error("Reload failed", ex); }
            finally
            {
                Loading = false;
                RaiseSettings();
                UpdatePlan();
            }
            ShowToast(State.Install.IsValid ? "Found " + State.AllMutators.Count + " mutators and " + State.Maps.Count + " maps" : "The game was not found");
        }

        private async Task ClearCache()
        {
            try
            {
                foreach (var f in new[] { "official.json", "mods.json" })
                {
                    string p = Path.Combine(AppPaths.CacheDir, f);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch (Exception ex) { AppLog.Warn("Cache clear: " + ex.Message); }
            await ReloadEverything("Rebuilding the catalog from the game files...");
        }

        // ------------------------------------------------------------------ mod folders

        private async Task AddModFolder()
        {
            string dir = FolderPicker.Pick(OwnerHandle, "Pick a folder with extra mod .pak files", GameInstall.ModioRoot);
            if (dir == null) return;
            if (State.Settings.ExtraModFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
            State.Settings.ExtraModFolders.Add(dir);
            ExtraModFolders.Add(dir);
            SaveSettingsSoon();
            await RescanMods(false);
        }

        // ------------------------------------------------------------------ launch behaviour

        private void SetSetting(Action apply, bool replan = false, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            apply();
            Raise(name);
            SaveSettingsSoon();
            if (replan) UpdatePlan(); else UpdateLaunchButton();
        }

        public bool AutoStartGame { get => State.Settings.AutoStartGame; set => SetSetting(() => State.Settings.AutoStartGame = value); }
        public bool MinimizeOnLaunch { get => State.Settings.MinimizeOnLaunch; set => SetSetting(() => State.Settings.MinimizeOnLaunch = value); }
        public bool SoloGameFlag { get => State.Settings.SoloGameFlag; set => SetSetting(() => State.Settings.SoloGameFlag = value, true); }
        public bool ApplyLiveRules { get => State.Settings.ApplyLiveRules; set => SetSetting(() => State.Settings.ApplyLiveRules = value, true); }
        public string InputMethod { get => State.Settings.InputMethod ?? "Paste"; set => SetSetting(() => State.Settings.InputMethod = value ?? "Paste"); }
        public int KeyDelayMs { get => State.Settings.KeyDelayMs; set => SetSetting(() => State.Settings.KeyDelayMs = Math.Max(30, Math.Min(250, value))); }
        public string RestartPolicy { get => State.Settings.RestartPolicy ?? "Ask"; set => SetSetting(() => State.Settings.RestartPolicy = value ?? "Ask"); }
        public string LaunchArgs { get => State.Settings.LaunchArgs ?? ""; set => SetSetting(() => State.Settings.LaunchArgs = value ?? ""); }
        public int StartTimeoutSec { get => State.Settings.StartTimeoutSec; set => SetSetting(() => State.Settings.StartTimeoutSec = Math.Max(60, Math.Min(900, value))); }

        // ------------------------------------------------------------------ console key

        public string ConsoleKeyStatus { get => consoleKeyStatus; set => Set(ref consoleKeyStatus, value); }
        public string ConsoleKeysText { get => consoleKeysText; set => Set(ref consoleKeysText, value); }
        public bool ConsoleKeyOk { get => consoleKeyOk; set => Set(ref consoleKeyOk, value); }
        public bool HasF10 { get => hasF10; set => Set(ref hasF10, value); }
        public bool F10Pending { get => f10Pending; set => Set(ref f10Pending, value); }

        public List<string> ConsoleKeyChoices
        {
            get
            {
                var list = new List<string> { "Auto" };
                try { list.AddRange(ConsoleBridge.ConfiguredKeys(State.Official)); } catch { }
                return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public string ConsoleKeyPreference
        {
            get => State.Settings.ConsoleKey ?? "Auto";
            set { State.Settings.ConsoleKey = string.IsNullOrEmpty(value) ? "Auto" : value; Raise(); SaveSettingsSoon(); RefreshConsoleKeyStatus(); }
        }

        private static string PrettyKey(string k) => k.Equals("Tilde", StringComparison.OrdinalIgnoreCase) ? "` (the key left of 1)" : k;

        private void RefreshConsoleKeyStatus()
        {
            try
            {
                var keys = ConsoleBridge.ConfiguredKeys(State.Official);
                var input = new GameInput(Monitor?.Window ?? IntPtr.Zero);
                var usable = keys.Where(k => input.VirtualKeyFor(k) != 0).ToList();
                HasF10 = keys.Contains("F10", StringComparer.OrdinalIgnoreCase);
                F10Pending = HasF10 && Monitor?.ProcessStartUtc != null && State.Settings.ConsoleKeyAddedUtc > Monitor.ProcessStartUtc.Value;
                ConsoleKeysText = keys.Count == 0 ? "none" : string.Join(", ", keys.Select(PrettyKey));
                if (usable.Count == 0)
                {
                    ConsoleKeyOk = false;
                    ConsoleKeyStatus = "None of the game's console keys can be pressed on your keyboard layout. Click \"Set up F10\" once and the launcher can control the game.";
                }
                else if (F10Pending && usable.All(k => k.Equals("F10", StringComparison.OrdinalIgnoreCase)))
                {
                    ConsoleKeyOk = false;
                    ConsoleKeyStatus = "F10 is set up. Restart the game once so it takes effect.";
                }
                else
                {
                    ConsoleKeyOk = true;
                    string best = usable.OrderBy(k => k.StartsWith("F", StringComparison.OrdinalIgnoreCase) && k.Length <= 3 ? 0 : 1).First();
                    ConsoleKeyStatus = "Ready. The launcher opens the console with " + PrettyKey(best) + (HasF10 ? "." : ". Setting up F10 as well makes it work on any keyboard layout.");
                }
            }
            catch (Exception ex)
            {
                ConsoleKeyOk = false;
                ConsoleKeyStatus = "Could not read Input.ini: " + ex.Message;
            }
            Raise(nameof(ConsoleKeyChoices));
        }

        private void SetupF10()
        {
            try
            {
                bool added = ConsoleBridge.AddConsoleKey("F10");
                if (added)
                {
                    State.Settings.ConsoleKeyAddedUtc = DateTime.UtcNow;
                    SaveSettingsSoon();
                    ShowToast(Monitor?.IsRunning == true ? "F10 added. Restart the game once so it takes effect." : "F10 added as a console key");
                }
                else ShowToast("F10 is already a console key");
            }
            catch (Exception ex) { ShowToast("Could not update Input.ini: " + ex.Message); }
            RefreshConsoleKeyStatus();
        }

        // ------------------------------------------------------------------ legacy import

        private async Task ImportLegacyInteractive()
        {
            string folder = LegacyImport.FindOldLauncherFolder();
            if (folder == null)
                folder = FolderPicker.Pick(OwnerHandle, "Pick the old Local Play Launcher folder (the one with the \"Defaults\" file)");
            if (folder == null) return;
            if (!File.Exists(Path.Combine(folder, "Defaults")))
            {
                await ShowMessage("Nothing to import", "That folder has no \"Defaults\" file from the old launcher.");
                return;
            }
            ImportLegacy(folder);
        }

        private void ImportLegacy(string folder)
        {
            try
            {
                var res = LegacyImport.Import(folder, State);
                foreach (var p in res.Profiles)
                {
                    State.Store.Profiles.Add(p);
                    State.Store.SaveProfile(p);
                }
                State.Rebuild();
                SaveSettingsSoon();
                RebuildProfilesList();
                BuildMutatorPresets();
                RefreshCatalogUi();
                RaiseSettings();
                RefreshConsoleKeyStatus();
                ShowToast($"Imported {res.Profiles.Count} profile(s), {res.Presets} preset(s), {res.CustomMaps} custom map(s)");
                if (res.Profiles.Count > 0) SelectedProfile = res.Profiles[0].Name;
            }
            catch (Exception ex)
            {
                AppLog.Error("Legacy import failed", ex);
                ShowToast("Import failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ tools

        private void OpenKnownPath(string what)
        {
            switch (what)
            {
                case "Data": Open(AppPaths.DataDir); break;
                case "Logs": Open(Path.Combine(AppPaths.DataDir, "logs")); break;
                case "Backups": Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "backups")); Open(Path.Combine(AppPaths.DataDir, "backups")); break;
                case "Config": Open(GameInstall.ConfigDir); break;
                case "GameIni": if (File.Exists(GameInstall.GameIniPath)) Open(GameInstall.GameIniPath); else Open(GameInstall.ConfigDir); break;
                case "InputIni": if (File.Exists(GameInstall.InputIniPath)) Open(GameInstall.InputIniPath); else Open(GameInstall.ConfigDir); break;
                case "GameLogs": Open(Path.GetDirectoryName(GameInstall.LogPath)); break;
                case "Game": Open(State.Install?.GameDir); break;
                case "Modio": Open(GameInstall.ModioRoot); break;
                case "ModioSite": Open("https://mod.io/g/insurgencysandstorm"); break;
                default: Open(what); break;
            }
        }

        private void CreateShortcut()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Sandstorm Mod Launcher.lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var t = shortcut.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exe + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Local play launcher for Insurgency: Sandstorm" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                ShowToast("Desktop shortcut created");
            }
            catch (Exception ex) { ShowToast("Could not create the shortcut: " + ex.Message); }
        }

        private async Task RemoveIniRules()
        {
            if (await Ask("Remove launcher rules?", "Remove the rules this launcher wrote to Game.ini? Your own lines are kept, and a backup is made first. The game uses its default rules from the next start.", "Remove") != "Remove") return;
            try
            {
                string path = GameInstall.GameIniPath;
                string text = UeIni.ReadText(path);
                string stripped = UeIni.StripManagedBlocks(text);
                if (stripped == text) { ShowToast("Game.ini has no launcher rules"); return; }
                ConsoleBridge.BackupFile(path);
                UeIni.WriteText(path, stripped);
                State.Settings.LastWrittenRulesHash = LaunchPlanner.RestartKeyFor(new Profile(), State.Rules);
                State.Settings.LastRulesWriteUtc = DateTime.UtcNow;
                SaveSettingsSoon();
                UpdateLaunchButton();
                ShowToast("Launcher rules removed from Game.ini");
            }
            catch (Exception ex) { await ShowMessage("Could not update Game.ini", ex.Message); }
        }

        private async Task WriteDiagnostics()
        {
            string file = Path.Combine(AppPaths.DataDir, "logs", "diagnostics.txt");
            ShowToast("Writing the diagnostics report...");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                await Task.Run(() => SelfTest.Run(file));
                File.AppendAllText(file, "\r\n--- recent launcher log ---\r\n" + AppLog.Recent());
                Open(file);
            }
            catch (Exception ex) { await ShowMessage("Diagnostics failed", ex.Message); }
        }

        // ------------------------------------------------------------------ custom map entries

        private CustomMapEntry editingMap;
        private bool mapEditorOpen;
        private string editMapLabel = "", editMapLevel = "", editMapScenario = "", editMapMode = "";
        public bool MapEditorOpen { get => mapEditorOpen; set => Set(ref mapEditorOpen, value); }
        public bool MapEditorIsNew => editingMap == null;
        public string EditMapLabel { get => editMapLabel; set => Set(ref editMapLabel, value ?? ""); }
        public string EditMapLevel { get => editMapLevel; set => Set(ref editMapLevel, value ?? ""); }
        public string EditMapScenario { get => editMapScenario; set => Set(ref editMapScenario, value ?? ""); }
        public string EditMapMode { get => editMapMode; set => Set(ref editMapMode, value ?? ""); }
        public List<string> EditMapModeChoices => new[] { "" }.Concat(State.Rules?.Modes.Select(m => m.Cls) ?? Enumerable.Empty<string>()).ToList();

        private void OpenMapEditor(CustomMapEntry entry)
        {
            editingMap = entry;
            EditMapLabel = entry?.Label ?? "";
            EditMapLevel = entry?.Level ?? "";
            EditMapScenario = entry?.Scenario ?? "";
            EditMapMode = entry?.GameModeClass ?? "";
            RaiseMany(nameof(MapEditorIsNew), nameof(EditMapModeChoices));
            MapEditorOpen = true;
        }

        private void SaveCustomMap()
        {
            string level = editMapLevel.Trim(), scenario = editMapScenario.Trim();
            if (level.Length == 0 || scenario.Length == 0) return;
            var entry = editingMap ?? new CustomMapEntry();
            entry.Label = string.IsNullOrWhiteSpace(editMapLabel) ? level.Substring(level.LastIndexOf('/') + 1) : editMapLabel.Trim();
            entry.Level = level;
            entry.Scenario = scenario;
            entry.GameModeClass = string.IsNullOrWhiteSpace(editMapMode) ? GuessModeClass(scenario) : editMapMode.Trim();
            if (editingMap == null) State.Settings.CustomMaps.Add(entry);
            SaveSettingsSoon();
            MapEditorOpen = false;
            RefreshCatalogUi();
            var tile = Maps.FirstOrDefault(m => m.Custom?.Id == entry.Id);
            if (tile != null) SelectedMap = tile;
            ShowToast("Custom map \"" + entry.Label + "\" saved");
        }

        private string GuessModeClass(string scenario)
        {
            string s = scenario.ToLowerInvariant();
            foreach (var m in State.Rules.Modes.OrderByDescending(m => m.Alias.Length))
                if (!string.IsNullOrEmpty(m.Alias) && s.Contains("_" + m.Alias.ToLowerInvariant())) return m.Cls;
            if (s.Contains("checkpoint")) return "INSCheckpointGameMode";
            if (s.Contains("push")) return "INSPushGameMode";
            if (s.Contains("firefight")) return "INSFirefightGameMode";
            if (s.Contains("skirmish")) return "INSSkirmishGameMode";
            return "";
        }

        private async Task DeleteCustomMap(CustomMapEntry entry)
        {
            if (entry == null) return;
            if (await Ask("Delete custom map", "Delete the custom map entry \"" + entry.Label + "\"?", "Delete") != "Delete") return;
            State.Settings.CustomMaps.Remove(entry);
            if (Profile.CustomMapId == entry.Id) Profile.CustomMapId = null;
            SaveSettingsSoon();
            MapEditorOpen = false;
            RefreshCatalogUi();
        }
    }
}
