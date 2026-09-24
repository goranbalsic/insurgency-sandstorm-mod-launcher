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
        public ICommand OpenPathCommand { get; private set; }
        public ICommand CreateShortcutCommand { get; private set; }
        public ICommand ClearCacheCommand { get; private set; }
        public ICommand RemoveIniRulesCommand { get; private set; }
        public ICommand RefreshConsoleKeyCommand { get; private set; }
        public ICommand AddCustomMapCommand { get; private set; }
        public ICommand EditCustomMapCommand { get; private set; }
        public ICommand SaveCustomMapCommand { get; private set; }
        public ICommand DeleteCustomMapCommand { get; private set; }
        public ICommand CancelCustomMapCommand { get; private set; }

        private string consoleKeyStatus = "", consoleKeysText = "";
        private bool consoleKeyOk = true;

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
            OpenPathCommand = new RelayCommand(p => OpenKnownPath(p as string));
            CreateShortcutCommand = new RelayCommand(CreateShortcut);
            ClearCacheCommand = new AsyncCommand(ClearCache);
            RemoveIniRulesCommand = new AsyncCommand(RemoveIniRules);
            RefreshConsoleKeyCommand = new RelayCommand(() => { RefreshConsoleKeyStatus(); RefreshKeyBackups(); });
            RestoreKeyBindingsCommand = new AsyncCommand(RestoreKeyBindings, () => SelectedKeyBackup != null);
            DismissKeyWarningCommand = new RelayCommand(() => { KeyBindings.ClearWarning(); KeyBindingsWarning = null; });
            SaveReportCommand = new AsyncCommand(SaveReport);
            AddCustomMapCommand = new RelayCommand(() => OpenMapEditor(null));
            EditCustomMapCommand = new RelayCommand(p => OpenMapEditor((p as MapItem)?.Custom));
            SaveCustomMapCommand = new RelayCommand(SaveCustomMap, () => !string.IsNullOrWhiteSpace(editMapLevel) && !string.IsNullOrWhiteSpace(editMapScenario));
            DeleteCustomMapCommand = new AsyncCommand(p => DeleteCustomMap((p as MapItem)?.Custom ?? editingMap));
            CancelCustomMapCommand = new RelayCommand(() => MapEditorOpen = false);
            HelpCommand = new AsyncCommand(() => ShowMessage("How it works",
                "1. PLAY > Map: pick a map and scenario, then set your squad and the enemies on the right. Every value starts at that mode's own default. Changed values turn gold.\n\n" +
                "2. PLAY > Rules has every other match setting of that mode, with play-style presets and the official rulesets. PLAY > Playlists sets up any official online playlist for offline play. PLAY > Advanced shows exactly what will be sent to the game.\n\n" +
                "3. MODS > Mutators: tick what you want, in load order. MODS > Installed mods lists what the game has downloaded.\n\n" +
                "4. Press LAUNCH (or F5). The launcher writes your rules to Game.ini, starts the game if needed, waits for the main menu and sends the match through the console. It checks on screen that the console is open before typing, and stops if it is not. Keep your hands off the keyboard for those few seconds.\n\n" +
                "5. PLAY > Live works during a match: restart rounds, set the clock, respawn bots, change rules, without typing commands.\n\n" +
                "Something wrong? Settings > Something went wrong? saves a report with everything needed to find the cause."));
        }

        public ICommand HelpCommand { get; private set; }

        private IntPtr OwnerHandle => Application.Current?.MainWindow == null ? IntPtr.Zero : new WindowInteropHelper(Application.Current.MainWindow).Handle;

        private void RaiseSettings()
        {
            ExtraModFolders.Clear();
            foreach (var f in State.Settings.ExtraModFolders) ExtraModFolders.Add(f);
            RaiseMany(nameof(GameDir), nameof(GameStore), nameof(GameBuild), nameof(GameFound), nameof(GameDirIsManual), nameof(AutoStartGame), nameof(MinimizeOnLaunch),
                      nameof(SoloGameFlag), nameof(ApplyLiveRules), nameof(InputMethod), nameof(KeyDelayMs), nameof(RestartPolicy), nameof(LaunchArgs),
                      nameof(StartTimeoutSec), nameof(AutoConsoleKey), nameof(DataDir), nameof(ModioRoot));
        }

        // ------------------------------------------------------------------ game install

        public string GameDir => State.Install?.GameDir ?? "Not found";
        public string GameStore => State.Install?.Store ?? "";
        public string GameBuild => string.IsNullOrEmpty(State.Install?.BuildId) ? "" : "Build " + State.Install.BuildId;
        public bool GameFound => State.Install?.IsValid == true;
        public bool GameDirIsManual => !string.IsNullOrEmpty(State.Settings.GameDirOverride);
        public string DataDir => AppPaths.DataDir;
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
                    LoadCommands();
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

        public bool AutoConsoleKey
        {
            get => State.Settings.AutoConsoleKey;
            set { SetSetting(() => State.Settings.AutoConsoleKey = value); RefreshConsoleKeyStatus(); }
        }

        private bool GameProcessRunning => Monitor?.IsRunning == true || System.Diagnostics.Process.GetProcessesByName(GameInstall.ClientProcess).Length > 0;

        private void RefreshConsoleKeyStatus()
        {
            try
            {
                bool running = GameProcessRunning;
                // With the game closed the extra key can be written safely (the game rewrites Input.ini when it exits).
                if (State.Settings.AutoConsoleKey && !running && State.Official != null)
                    ConsoleBridge.EnsureLayoutFreeKey(State.Official, State.Settings, false);
                var keys = ConsoleBridge.ConfiguredKeys(State.Official);
                IntPtr window = Monitor?.Window ?? IntPtr.Zero;
                var input = new GameInput(window);
                string layout = ConsoleBridge.LayoutName(window != IntPtr.Zero ? input.KeyboardLayout : Native.GetKeyboardLayout(0));
                string fn = keys.FirstOrDefault(ConsoleBridge.IsFunctionKey);
                bool pending = fn != null && running && Monitor?.ProcessStartUtc != null && State.Settings.ConsoleKeyAddedUtc > Monitor.ProcessStartUtc.Value;
                bool layoutKey = keys.Any(k => !ConsoleBridge.IsFunctionKey(k) && input.VirtualKeyFor(k) != 0);
                ConsoleKeysText = keys.Count == 0 ? "none" : string.Join(", ", keys.Select(ConsoleBridge.PrettyKey));
                if (fn != null && !pending)
                {
                    ConsoleKeyOk = true;
                    ConsoleKeyStatus = "Ready. The launcher opens the console with " + fn + ", which works on every keyboard layout.";
                }
                else if (fn != null)
                {
                    ConsoleKeyOk = layoutKey;
                    ConsoleKeyStatus = layoutKey
                        ? "Ready with ` for now. " + fn + " takes over after the game restarts, so any keyboard layout works."
                        : fn + " is set up but only works after the game restarts. Until then switch the keyboard to English (Win+Space), or close the game and launch from here.";
                }
                else if (layoutKey)
                {
                    ConsoleKeyOk = true;
                    ConsoleKeyStatus = "Ready with ` (" + layout + " keyboard). " + (State.Settings.AutoConsoleKey
                        ? "F10 is added the next time the game is closed, so other keyboard layouts work too."
                        : "Turn on the switch below so other keyboard layouts work too.");
                }
                else
                {
                    ConsoleKeyOk = false;
                    ConsoleKeyStatus = "The " + layout + " keyboard layout has no ` key, which the game uses for its console. " + (State.Settings.AutoConsoleKey
                        ? "Close the game and launch from here: the launcher adds F10 first, which works on every layout. Or switch the keyboard to English (Win+Space)."
                        : "Turn on the switch below, or switch the keyboard to English (Win+Space).");
                }
            }
            catch (Exception ex)
            {
                ConsoleKeyOk = false;
                ConsoleKeyStatus = "Could not read Input.ini: " + ex.Message;
                AppLog.Warn("Console key status: " + ex.Message);
            }
        }

        private void SetupF10()
        {
            if (GameProcessRunning)
            {
                // Writing now would be lost: the game rewrites Input.ini when it exits.
                if (!State.Settings.AutoConsoleKey) AutoConsoleKey = true;
                ShowToast("F10 is added as soon as the game is closed");
                return;
            }
            string key = ConsoleBridge.EnsureLayoutFreeKey(State.Official, State.Settings, false);
            SaveSettingsSoon();
            ShowToast(key != null ? key + " is set up as a console key" : "Could not add a console key. See the launcher log.");
            RefreshConsoleKeyStatus();
        }

        // ------------------------------------------------------------------ key bindings safety copies

        public ObservableCollection<KeyBindingsBackup> KeyBindingBackups { get; } = new ObservableCollection<KeyBindingsBackup>();
        private KeyBindingsBackup selectedKeyBackup;
        private string keyBindingsWarning;
        public KeyBindingsBackup SelectedKeyBackup { get => selectedKeyBackup; set => Set(ref selectedKeyBackup, value); }
        public string KeyBindingsWarning { get => keyBindingsWarning; set => Set(ref keyBindingsWarning, value); }
        public ICommand RestoreKeyBindingsCommand { get; private set; }
        public ICommand DismissKeyWarningCommand { get; private set; }

        private void RefreshKeyBackups()
        {
            var keep = selectedKeyBackup?.Path;
            KeyBindingBackups.Clear();
            foreach (var b in KeyBindings.List()) KeyBindingBackups.Add(b);
            SelectedKeyBackup = KeyBindingBackups.FirstOrDefault(b => b.Path == keep) ?? KeyBindingBackups.FirstOrDefault();
            KeyBindingsWarning = KeyBindings.DropWarning;
        }

        private async Task RestoreKeyBindings()
        {
            var b = SelectedKeyBackup;
            if (b == null) return;
            if (GameProcessRunning) { await ShowMessage("Close the game first", "The game rewrites its key bindings while it runs, so close it and then restore."); return; }
            if (await Ask("Restore key bindings?", "Put back your game key bindings from " + b.Label + "? The current ones are copied first, so this can be undone.", "Restore") != "Restore") return;
            string error = KeyBindings.Restore(b);
            RefreshKeyBackups();
            if (error != null) await ShowMessage("Could not restore", error);
            else ShowToast("Key bindings restored. They apply the next time the game starts.");
        }

        // ------------------------------------------------------------------ problem reports

        private string reportNote = "";
        public string ReportNote { get => reportNote; set => Set(ref reportNote, value ?? ""); }
        public ICommand SaveReportCommand { get; private set; }

        private async Task SaveReport()
        {
            string note = ReportNote;
            ShowToast("Saving a problem report...");
            string dir = await Task.Run(() => DebugReport.Write("manual", note, State, Monitor));
            if (dir == null) { ShowToast("Could not save the report. See the launcher log."); return; }
            ReportNote = "";
            ShowToast("Report saved: " + Path.GetFileName(dir));
        }

        // ------------------------------------------------------------------ tools

        private void OpenKnownPath(string what)
        {
            switch (what)
            {
                case "Data": Open(AppPaths.DataDir); break;
                case "Logs": Open(Path.Combine(AppPaths.DataDir, "logs")); break;
                case "Reports": Directory.CreateDirectory(DebugReport.ReportsDir); Open(DebugReport.ReportsDir); break;
                case "Backups": Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "backups")); Open(Path.Combine(AppPaths.DataDir, "backups")); break;
                case "Config": Open(GameInstall.ConfigDir); break;
                case "GameIni": if (File.Exists(GameInstall.GameIniPath)) Open(GameInstall.GameIniPath); else Open(GameInstall.ConfigDir); break;
                case "InputIni": if (File.Exists(GameInstall.InputIniPath)) Open(GameInstall.InputIniPath); else Open(GameInstall.ConfigDir); break;
                case "GameLogs": Open(Path.GetDirectoryName(GameInstall.LogPath)); break;
                case "Game": Open(State.Install?.GameDir); break;
                case "Modio": Open(GameInstall.ModioRoot); break;
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
            if (GameProcessRunning) { await ShowMessage("Close the game first", "The game rewrites Game.ini while it runs, so close it and then remove the rules."); return; }
            if (await Ask("Remove launcher rules?", "Remove every match rule from Game.ini? Other lines are kept, and a backup is made first. The game uses its default rules from the next start.", "Remove") != "Remove") return;
            try
            {
                string path = GameInstall.GameIniPath;
                string text = UeIni.ReadText(path);
                var earlier = new HashSet<string>(State.Settings.ManagedIniKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                string stripped = UeIni.MergeSections(text, new List<UeIni.Section>(), (s, k) => LaunchPlanner.IsManagedIniKey(State.Rules, s, k) || earlier.Contains(s + "\n" + k));
                if (stripped.Replace("\r\n", "\n").Trim() == text.Replace("\r\n", "\n").Trim()) { ShowToast("Game.ini has no launcher rules"); return; }
                State.Settings.ManagedIniKeys = new List<string>();
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
