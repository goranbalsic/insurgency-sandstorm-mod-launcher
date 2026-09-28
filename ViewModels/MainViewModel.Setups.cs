using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    /// <summary>
    /// Saved setups: the one way to keep a match. A saved setup holds everything (map, scenario, day or night, bots,
    /// every rule, the mutators and the Advanced options). The bar at the top picks, saves, renames and deletes them;
    /// what is on screen is always the setup being worked on (kept on its own between runs).
    /// </summary>
    public sealed partial class MainViewModel
    {
        public ObservableCollection<string> SavedSetupNames { get; } = new ObservableCollection<string>();
        public ICommand SaveSetupCommand { get; private set; }
        public ICommand SaveSetupAsCommand { get; private set; }
        public ICommand RenameSetupCommand { get; private set; }
        public ICommand DeleteSetupCommand { get; private set; }
        public ICommand NewSetupCommand { get; private set; }
        public ICommand RevertSetupCommand { get; private set; }

        private void InitSetupCommands()
        {
            SaveSetupCommand = new AsyncCommand(SaveSetup, () => CanChangeSetup);
            SaveSetupAsCommand = new AsyncCommand(SaveSetupAs, () => CanChangeSetup);
            RenameSetupCommand = new AsyncCommand(RenameSetup, () => CanChangeSetup && LoadedSetup != null);
            DeleteSetupCommand = new AsyncCommand(DeleteSetup, () => CanChangeSetup && LoadedSetup != null);
            NewSetupCommand = new AsyncCommand(NewSetup, () => CanChangeSetup);
            RevertSetupCommand = new AsyncCommand(RevertSetup, () => CanChangeSetup && LoadedSetup != null && setupChangedSinceSave);
        }

        /// <summary>A setup cannot be switched while the catalog is being read or a launch is running.</summary>
        public bool CanChangeSetup => !loading && !launchRunning;

        /// <summary>The saved setup the one on screen came from (or was saved as), when it still exists.</summary>
        private RulesPreset LoadedSetup =>
            Profile.SetupName == null ? null : State.Settings.RulesPresets.FirstOrDefault(r => string.Equals(r.Name, Profile.SetupName, StringComparison.OrdinalIgnoreCase));

        private void RebuildSetupList()
        {
            SavedSetupNames.Clear();
            foreach (var r in State.Settings.RulesPresets.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)) SavedSetupNames.Add(r.Name);
            RaiseSetup();
        }

        // Whether the setup differs from its saved setup, worked out once per change (the comparison is not free).
        private bool setupChangedSinceSave;

        private void RaiseSetup()
        {
            setupChangedSinceSave = SetupEngine.SetupChanged(Profile);
            RaiseMany(nameof(SelectedSetup), nameof(SetupState), nameof(SetupChanged));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>After a refused pick: the list shows the setup in use again (outside the list's own update, which ignores it).</summary>
        private void RaiseSetupLater() => ui.BeginInvoke(new Action(RaiseSetup));

        /// <summary>The saved setup shown in the bar; picking another one loads it (the one on screen is replaced).</summary>
        public string SelectedSetup
        {
            get => LoadedSetup?.Name;
            set
            {
                if (value == null || string.Equals(value, LoadedSetup?.Name, StringComparison.Ordinal)) return;
                _ = LoadSetupAsync(value);
            }
        }

        /// <summary>True when the setup on screen has changes that are not in its saved setup (or it was never saved).</summary>
        public bool SetupChanged => LoadedSetup == null ? !IsFreshSetup() : setupChangedSinceSave;

        /// <summary>"Not saved", "Changed" or nothing, next to the setup name.</summary>
        public string SetupState => LoadedSetup == null ? T("Not saved") : setupChangedSinceSave ? T("Changed") : "";

        /// <summary>A setup nobody has changed yet (game defaults, no mutators): nothing to lose when another one is loaded.</summary>
        private bool IsFreshSetup() => Profile.Rules.Count == 0 && Profile.Mutators.Count == 0 && string.IsNullOrWhiteSpace(Profile.CustomIniText)
                                       && string.IsNullOrWhiteSpace(Profile.ExtraUrlOptions) && string.IsNullOrWhiteSpace(Profile.AfterLoadCommands)
                                       && string.IsNullOrWhiteSpace(Profile.GameModeOverride);

        public async Task LoadSetupAsync(string name)
        {
            var saved = State.Settings.RulesPresets.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (saved == null || !CanChangeSetup) { RaiseSetupLater(); return; }
            if (SetupChanged)
            {
                string answer = await Ask("Load another setup?",
                    LoadedSetup == null ? "The setup on screen was never saved. Load the other one anyway (the one on screen is replaced)?"
                                        : F("\"{0}\" has changes that are not saved. Load the other one anyway (the changes are lost)?", LoadedSetup.Name),
                    "Load", "Cancel", "Save first");
                if (answer == "Save first") { if (!await SaveSetup()) { RaiseSetup(); return; } }
                else if (answer != "Load") { RaiseSetup(); return; }
            }
            string message = SetupEngine.Apply(Profile, State, new Preset { Name = saved.Name, Kind = PresetKind.Saved, Saved = saved });
            RefreshFromProfile(true);
            RaiseSetup();
            ShowToast(message);
        }

        /// <summary>Drops the changes: the saved setup the one on screen came from is loaded again.</summary>
        private async Task RevertSetup()
        {
            var loaded = LoadedSetup;
            if (loaded == null) return;
            if (await Ask("Undo changes", F("Load \"{0}\" again as it was saved? The changes since are lost.", loaded.Name), "Undo changes") != "Undo changes") return;
            string message = SetupEngine.Apply(Profile, State, new Preset { Name = loaded.Name, Kind = PresetKind.Saved, Saved = loaded });
            RefreshFromProfile(true);
            RaiseSetup();
            ShowToast(message);
        }

        /// <summary>Saves the setup on screen: into the saved setup it came from, or under a new name.</summary>
        private async Task<bool> SaveSetup()
        {
            var loaded = LoadedSetup;
            if (loaded == null) return await SaveSetupAs();
            StoreSetup(loaded.Name);
            ShowToast(F("Saved \"{0}\"", loaded.Name));
            return true;
        }

        private async Task<bool> SaveSetupAs()
        {
            string suggestion = LoadedSetup?.Name ?? (SelectedMapTitle + " " + CurrentModeName).Trim();
            string name = await Prompt("Save setup", "Keeps everything: map, scenario, day or night, bots and enemies, every rule, the mutators and the Advanced options. Name:", suggestion, "Save");
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (name.Length > 60) name = name.Substring(0, 60).TrimEnd();
            var existing = State.Settings.RulesPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing != LoadedSetup)
            {
                if (await Ask("Replace setup?", F("A setup called \"{0}\" already exists. Replace it?", existing.Name), "Replace") != "Replace") return false;
            }
            StoreSetup(existing?.Name ?? name);
            ShowToast(F("Setup saved as \"{0}\"", existing?.Name ?? name));
            return true;
        }

        /// <summary>Writes the setup on screen as the saved setup of that name (replacing one with the same name).</summary>
        private void StoreSetup(string name)
        {
            State.Settings.RulesPresets.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            State.Settings.RulesPresets.Add(SetupEngine.Capture(Profile, name));
            SetupEngine.MarkSetup(Profile, name);
            SaveNow();
            RebuildSetupList();
        }

        private async Task RenameSetup()
        {
            var loaded = LoadedSetup;
            if (loaded == null) return;
            string name = await Prompt("Rename setup", "New name:", loaded.Name, "Rename");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (name.Length > 60) name = name.Substring(0, 60).TrimEnd();
            if (name == loaded.Name) return;
            if (State.Settings.RulesPresets.Any(r => r != loaded && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                await ShowMessage("Rename setup", F("A setup called \"{0}\" already exists.", name));
                return;
            }
            bool wasChanged = setupChangedSinceSave;
            loaded.Name = name;
            if (loaded.Setup != null) loaded.Setup.Name = name;
            Profile.SetupName = name;
            if (!wasChanged) SetupEngine.MarkSetup(Profile, name);
            SaveNow();
            RebuildSetupList();
        }

        private async Task DeleteSetup()
        {
            var loaded = LoadedSetup;
            if (loaded == null) return;
            if (await Ask("Delete setup", F("Delete the saved setup \"{0}\"? The setup on screen stays as it is.", loaded.Name), "Delete") != "Delete") return;
            State.Settings.RulesPresets.Remove(loaded);
            Profile.SetupName = null;
            Profile.SetupCheck = null;
            SaveNow();
            RebuildSetupList();
        }

        /// <summary>A new setup: bots, rules, mutators and the Advanced options back to the defaults (the map stays), not saved yet.</summary>
        private async Task NewSetup()
        {
            if (SetupChanged && await Ask("New setup", "Start a new setup from the game defaults? The setup on screen is replaced (the map and scenario stay).", "New setup") != "New setup") return;
            var fresh = new Profile { MapKey = Profile.MapKey, ScenarioId = Profile.ScenarioId, CustomMapId = Profile.CustomMapId, Lighting = Profile.Lighting };
            SetupEngine.CopySetup(fresh, Profile);
            SetupEngine.ResetAll(Profile);
            Profile.SetupName = null;
            Profile.SetupCheck = null;
            RefreshFromProfile(true);
            RaiseSetup();
            ShowToast("New setup");
        }
    }
}
