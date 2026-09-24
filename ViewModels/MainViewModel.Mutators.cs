using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<MutatorItem> MutatorItems { get; } = new ObservableCollection<MutatorItem>();
        public ObservableCollection<ActiveMutatorItem> ActiveMutators { get; } = new ObservableCollection<ActiveMutatorItem>();
        public ObservableCollection<string> MutatorPresetNames { get; } = new ObservableCollection<string>();
        public ICollectionView MutatorsView { get; private set; }
        public ICommand MoveMutatorUpCommand { get; private set; }
        public ICommand MoveMutatorDownCommand { get; private set; }
        public ICommand RemoveMutatorCommand { get; private set; }
        public ICommand ClearMutatorsCommand { get; private set; }
        public ICommand AddCustomMutatorCommand { get; private set; }
        public ICommand DeleteCustomMutatorCommand { get; private set; }
        public ICommand SaveMutatorPresetCommand { get; private set; }
        public ICommand DeleteMutatorPresetCommand { get; private set; }
        public ICommand CopyTextCommand { get; private set; }
        public ICommand OpenUrlCommand { get; private set; }
        private string mutatorFilter = "All", mutatorSearch = "", customMutatorText = "", selectedMutatorPreset;
        private bool showBaseClasses;
        private MutatorItem selectedMutator;

        private void InitMutatorCommands()
        {
            MoveMutatorUpCommand = new RelayCommand(p => MoveMutator(p as ActiveMutatorItem, -1));
            MoveMutatorDownCommand = new RelayCommand(p => MoveMutator(p as ActiveMutatorItem, 1));
            RemoveMutatorCommand = new RelayCommand(p => { if (p is ActiveMutatorItem a) SetMutatorActive(a.Id, false); });
            ClearMutatorsCommand = new RelayCommand(() => { foreach (var id in Profile.Mutators.ToList()) SetMutatorActive(id, false); }, () => Profile.Mutators.Count > 0);
            AddCustomMutatorCommand = new RelayCommand(AddCustomMutator, () => !string.IsNullOrWhiteSpace(customMutatorText));
            DeleteCustomMutatorCommand = new RelayCommand(p => DeleteCustomMutator(p as MutatorItem));
            SaveMutatorPresetCommand = new AsyncCommand(SaveMutatorPreset, () => Profile.Mutators.Count > 0);
            DeleteMutatorPresetCommand = new AsyncCommand(DeleteMutatorPreset, () => selectedMutatorPreset != null);
            CopyTextCommand = new RelayCommand(p => { if (p is string s && s.Length > 0) { try { Clipboard.SetDataObject(s, true); ShowToast("Copied to the clipboard"); } catch { } } });
            OpenUrlCommand = new RelayCommand(p => Open(p as string));
        }

        private void BuildMutatorList()
        {
            MutatorItems.Clear();
            var active = new HashSet<string>(Profile.Mutators, StringComparer.OrdinalIgnoreCase);
            // Official mutators are grouped by the official playlists that use them.
            var coop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var versus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pl in State.Rules?.Playlists ?? new List<PlaylistDef>())
                foreach (var name in pl.Mutators)
                {
                    var info = State.FindMutator(name);
                    if (info != null) (pl.IsCoop ? coop : versus).Add(info.Id);
                }
            var items = new List<MutatorItem>();
            foreach (var m in State.AllMutators)
            {
                var item = new MutatorItem(m, active.Contains(m.Id), OnMutatorToggled);
                if (m.Source == ContentSource.Official)
                {
                    bool c = coop.Contains(m.Id), v = versus.Contains(m.Id);
                    item.OfficialGroup = c && v ? "Official · co-op and versus playlists" : c ? "Official · co-op playlists" : v ? "Official · versus playlists" : "Official · other";
                    item.GroupRank = c && !v ? 0 : v && !c ? 1 : c ? 2 : 3;
                }
                else item.GroupRank = m.Source == ContentSource.Mod ? 4 : 5;
                items.Add(item);
            }
            foreach (var item in items.OrderBy(i => i.GroupRank).ThenBy(i => i.Group, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                MutatorItems.Add(item);
            MutatorsView = CollectionViewSource.GetDefaultView(MutatorItems);
            MutatorsView.GroupDescriptions.Clear();
            MutatorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MutatorItem.Group)));
            MutatorsView.Filter = o =>
            {
                var m = (MutatorItem)o;
                if (!showBaseClasses && (m.IsBase || m.NotRegistered) && !m.IsActive) return false;
                switch (mutatorFilter)
                {
                    case "Official": if (m.SourceKey != "Official") return false; break;
                    case "Mods": if (m.SourceKey != "Mod") return false; break;
                    case "Custom": if (m.SourceKey != "Custom") return false; break;
                    case "Active": if (!m.IsActive) return false; break;
                }
                if (string.IsNullOrWhiteSpace(mutatorSearch)) return true;
                return m.Name.IndexOf(mutatorSearch, StringComparison.OrdinalIgnoreCase) >= 0 || m.Id.IndexOf(mutatorSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || m.Group.IndexOf(mutatorSearch, StringComparison.OrdinalIgnoreCase) >= 0 || (m.Info.Description ?? "").IndexOf(mutatorSearch, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            if (selectedMutator != null) selectedMutator = MutatorItems.FirstOrDefault(x => x.Id == selectedMutator.Id);
            if (selectedMutator == null) selectedMutator = MutatorItems.FirstOrDefault(x => x.IsActive) ?? MutatorItems.FirstOrDefault();
            RaiseMany(nameof(MutatorsView), nameof(SelectedMutator), nameof(OfficialMutatorCount), nameof(ModMutatorCount), nameof(CustomMutatorCount), nameof(TotalMutatorCount));
        }

        public int TotalMutatorCount => MutatorItems.Count(m => !m.IsBase && !m.NotRegistered);
        public int OfficialMutatorCount => MutatorItems.Count(m => m.SourceKey == "Official");
        public int ModMutatorCount => MutatorItems.Count(m => m.SourceKey == "Mod" && !m.IsBase && !m.NotRegistered);
        public int CustomMutatorCount => MutatorItems.Count(m => m.SourceKey == "Custom");

        public string MutatorFilter { get => mutatorFilter; set { if (Set(ref mutatorFilter, value)) MutatorsView?.Refresh(); } }
        public string MutatorSearch { get => mutatorSearch; set { if (Set(ref mutatorSearch, value ?? "")) MutatorsView?.Refresh(); } }
        public bool ShowBaseClasses { get => showBaseClasses; set { if (Set(ref showBaseClasses, value)) MutatorsView?.Refresh(); } }
        public string CustomMutatorText { get => customMutatorText; set => Set(ref customMutatorText, value ?? ""); }

        public MutatorItem SelectedMutator
        {
            get => selectedMutator;
            set { if (Set(ref selectedMutator, value)) RaiseMany(nameof(SelectedMutatorMod)); }
        }

        public ModInfo SelectedMutatorMod => selectedMutator == null ? null : State.Mods.FirstOrDefault(m => m.Id != 0 && m.Id == selectedMutator.Info.ModId);

        private void OnMutatorToggled(MutatorItem item, bool active) => SetMutatorActive(item.Id, active);

        public void SetMutatorActive(string id, bool active)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            bool has = Profile.Mutators.Any(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (active && !has) Profile.Mutators.Add(id);
            else if (!active && has) Profile.Mutators.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            else return;
            MutatorItems.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.SetActiveSilently(active);
            BuildActiveMutators();
            ProfileChanged();
        }

        private void BuildActiveMutators()
        {
            ActiveMutators.Clear();
            int i = 1;
            foreach (var id in Profile.Mutators)
            {
                var info = State.FindMutator(id);
                ActiveMutators.Add(new ActiveMutatorItem
                {
                    Id = id, Name = info?.DisplayName ?? id, Index = i++, Missing = info == null,
                    Source = info == null ? "not installed" : info.Source == ContentSource.Official ? "Official" : info.ModName
                });
            }
            RaiseMany(nameof(ActiveMutatorCount), nameof(MutatorSummary), nameof(ActiveMutatorSections));
        }

        public int ActiveMutatorCount => ActiveMutators.Count;

        private void MoveMutator(ActiveMutatorItem item, int delta)
        {
            if (item == null) return;
            int i = Profile.Mutators.FindIndex(x => x.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            int j = i + delta;
            if (i < 0 || j < 0 || j >= Profile.Mutators.Count) return;
            var t = Profile.Mutators[i]; Profile.Mutators[i] = Profile.Mutators[j]; Profile.Mutators[j] = t;
            BuildActiveMutators();
            ProfileChanged();
        }

        private void AddCustomMutator()
        {
            string id = (customMutatorText ?? "").Trim();
            if (id.Length == 0) return;
            if (State.FindMutator(id) == null && !State.Settings.CustomMutators.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                State.Settings.CustomMutators.Add(id);
                State.Rebuild();
                BuildMutatorList();
                SaveSettingsSoon();
            }
            SetMutatorActive(State.FindMutator(id)?.Id ?? id, true);
            CustomMutatorText = "";
            ShowToast("Added " + id);
        }

        private void DeleteCustomMutator(MutatorItem item)
        {
            if (item == null || item.SourceKey != "Custom") return;
            SetMutatorActive(item.Id, false);
            State.Settings.CustomMutators.RemoveAll(x => x.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            State.Rebuild();
            BuildMutatorList();
            SaveSettingsSoon();
        }

        // ------------------------------------------------------------------ presets

        private void BuildMutatorPresets()
        {
            MutatorPresetNames.Clear();
            foreach (var p in State.Settings.MutatorPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) MutatorPresetNames.Add(p.Name);
            selectedMutatorPreset = Profile.MutatorPreset != null && MutatorPresetNames.Contains(Profile.MutatorPreset) ? Profile.MutatorPreset : null;
            Raise(nameof(SelectedMutatorPreset));
        }

        public string SelectedMutatorPreset
        {
            get => selectedMutatorPreset;
            set
            {
                if (!Set(ref selectedMutatorPreset, value) || value == null) return;
                var preset = State.Settings.MutatorPresets.FirstOrDefault(p => p.Name == value);
                if (preset == null) return;
                Profile.Mutators = new List<string>(preset.Mutators);
                Profile.MutatorPreset = value;
                foreach (var m in MutatorItems) m.SetActiveSilently(Profile.Mutators.Contains(m.Id, StringComparer.OrdinalIgnoreCase));
                BuildActiveMutators();
                MutatorsView?.Refresh();
                ProfileChanged();
                ShowToast("Loaded preset \"" + value + "\"");
            }
        }

        private async Task SaveMutatorPreset()
        {
            string name = await Prompt("Save mutator preset", "Name for this set of " + Profile.Mutators.Count + " mutators:", selectedMutatorPreset ?? "My mutators", "Save");
            if (string.IsNullOrWhiteSpace(name)) return;
            var existing = State.Settings.MutatorPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (await Ask("Replace preset?", "A preset called \"" + existing.Name + "\" already exists. Replace it?", "Replace") != "Replace") return;
                existing.Mutators = new List<string>(Profile.Mutators);
                name = existing.Name;
            }
            else State.Settings.MutatorPresets.Add(new MutatorPreset { Name = name, Mutators = new List<string>(Profile.Mutators) });
            Profile.MutatorPreset = name;
            SaveSettingsSoon();
            BuildMutatorPresets();
            ShowToast("Preset \"" + name + "\" saved");
        }

        private async Task DeleteMutatorPreset()
        {
            if (selectedMutatorPreset == null) return;
            if (await Ask("Delete preset", "Delete the mutator preset \"" + selectedMutatorPreset + "\"?", "Delete") != "Delete") return;
            State.Settings.MutatorPresets.RemoveAll(p => p.Name == selectedMutatorPreset);
            Profile.MutatorPreset = null;
            SaveSettingsSoon();
            BuildMutatorPresets();
        }
    }
}
