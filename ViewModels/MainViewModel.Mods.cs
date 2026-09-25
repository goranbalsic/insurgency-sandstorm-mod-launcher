using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed class PlaylistItem
    {
        public PlaylistDef Def { get; set; }
        public string Title => Def.Title;
        public string Description => string.IsNullOrWhiteSpace(Def.Description) ? string.Join(" · ", Def.Features) : Def.Description;
        public string Type => Def.IsCoop ? "CO-OP" : "VERSUS";
        public string Lighting => Def.Lighting == "Night" ? "Night" : Def.Lighting == "DayAndNight" ? "Day or night" : "Day";
        public string Modes { get; set; }
        public string MutatorText => Def.Mutators.Count == 0 ? "No mutators" : string.Join(", ", Def.Mutators);
        public List<string> Features => Def.Features;
        public bool HasMissing => Def.Missing.Count > 0;
        public string MissingText => "Not in the current game: " + string.Join(", ", Def.Missing);
        public bool Hardcore => Def.GameAlias == "CheckpointHardcore";
    }

    public sealed partial class MainViewModel
    {
        public ObservableCollection<ModItem> ModItems { get; } = new ObservableCollection<ModItem>();
        public ObservableCollection<PlaylistItem> PlaylistItems { get; } = new ObservableCollection<PlaylistItem>();
        public ICollectionView ModsView { get; private set; }
        public ICollectionView PlaylistsView { get; private set; }
        public ICommand RescanModsCommand { get; private set; }
        public ICommand OpenModFolderCommand { get; private set; }
        public ICommand UseModMutatorsCommand { get; private set; }
        public ICommand SetupPlaylistCommand { get; private set; }
        public ICommand PlayPlaylistCommand { get; private set; }
        public ICommand AddMutatorByIdCommand { get; private set; }
        public ICommand ModsTabCommand { get; private set; }
        private string modSearch = "", playlistFilter = "All", playlistSearch = "", modsTab = "Mutators";
        private ModItem selectedMod;

        /// <summary>Mutators or Installed: the views of the Mods page.</summary>
        public string ModsTab { get => modsTab; set => Set(ref modsTab, string.IsNullOrEmpty(value) ? "Mutators" : value); }

        private void InitModCommands()
        {
            RescanModsCommand = new AsyncCommand(() => RescanMods(false));
            ModsTabCommand = new RelayCommand(p => { Page = "Play"; PlayTab = "Mods"; ModsTab = p as string ?? "Mutators"; });
            OpenModFolderCommand = new RelayCommand(p =>
            {
                string folder = (p as ModItem)?.Info.Folder;
                if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder)) { Open(folder); return; }
                string parent = string.IsNullOrEmpty(folder) ? GameInstall.ModioRoot : System.IO.Path.GetDirectoryName(folder);
                Open(System.IO.Directory.Exists(parent) ? parent : GameInstall.ModioRoot);
                if (!string.IsNullOrEmpty(folder)) ShowToast("This mod's folder is not on disk, so its parent folder was opened");
            });
            UseModMutatorsCommand = new RelayCommand(p => UseModMutators(p as ModItem));
            SetupPlaylistCommand = new RelayCommand(p => SetupPlaylist(p as PlaylistItem, false));
            PlayPlaylistCommand = new RelayCommand(p => SetupPlaylist(p as PlaylistItem, true));
            AddMutatorByIdCommand = new RelayCommand(p => { if (p is MutatorInfo m) { SetMutatorActive(m.Id, true); ShowToast(m.DisplayName + " added"); } });
        }

        private void BuildMods()
        {
            string keep = selectedMod?.Info.Folder;
            ModItems.Clear();
            foreach (var m in State.Mods) ModItems.Add(new ModItem(m));
            ModsView = CollectionViewSource.GetDefaultView(ModItems);
            ModsView.Filter = o =>
            {
                var m = (ModItem)o;
                return string.IsNullOrWhiteSpace(modSearch) || (m.Name ?? "").IndexOf(modSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || (m.Info.Author ?? "").IndexOf(modSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || m.Info.Mutators.Any(x => x.Id.IndexOf(modSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            };
            SelectedMod = ModItems.FirstOrDefault(m => m.Info.Folder == keep) ?? ModItems.FirstOrDefault();
            BuildPlaylists();
            RaiseMany(nameof(ModsView), nameof(ModCount), nameof(ModsSummary));
        }

        public int ModCount => ModItems.Count;
        public string ModsSummary => ModItems.Count + " mods installed · " + State.AllMutators.Count(m => m.Source == ContentSource.Mod && m.Registered && !m.IsBaseClass) + " mod mutators";
        public string ModSearch { get => modSearch; set { if (Set(ref modSearch, value ?? "")) ModsView?.Refresh(); } }

        public ModItem SelectedMod
        {
            get => selectedMod;
            set => Set(ref selectedMod, value);
        }

        private void UseModMutators(ModItem item)
        {
            if (item == null) return;
            var usable = item.Info.Mutators.Where(m => m.Registered && !m.IsBaseClass).ToList();
            if (usable.Count == 0) { ShowToast("This mod has no mutators to add"); return; }
            if (usable.Count == 1) SetMutatorActive(usable[0].Id, true);
            else { Page = "Play"; PlayTab = "Mods"; ModsTab = "Mutators"; MutatorSearch = item.Name; ShowToast("Pick which of the " + usable.Count + " mutators to use"); return; }
            ShowToast(usable[0].DisplayName + " added to your mutators");
        }

        // ------------------------------------------------------------------ official playlists

        private void BuildPlaylists()
        {
            PlaylistItems.Clear();
            foreach (var p in State.Rules.Playlists.OrderBy(p => p.IsCoop ? 0 : 1).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
                PlaylistItems.Add(new PlaylistItem { Def = p, Modes = string.Join(", ", p.Modes.Select(m => State.Rules.Mode(m)?.Name ?? m).Distinct()) });
            PlaylistsView = CollectionViewSource.GetDefaultView(PlaylistItems);
            PlaylistsView.Filter = o =>
            {
                var p = (PlaylistItem)o;
                if (playlistFilter == "Coop" && !p.Def.IsCoop) return false;
                if (playlistFilter == "Versus" && p.Def.IsCoop) return false;
                return string.IsNullOrWhiteSpace(playlistSearch) || p.Title.IndexOf(playlistSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || p.MutatorText.IndexOf(playlistSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || string.Join(" ", p.Features).IndexOf(playlistSearch, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            Raise(nameof(PlaylistsView));
        }
        public string PlaylistFilter { get => playlistFilter; set { if (Set(ref playlistFilter, value)) PlaylistsView?.Refresh(); } }
        public string PlaylistSearch { get => playlistSearch; set { if (Set(ref playlistSearch, value ?? "")) PlaylistsView?.Refresh(); } }

        private void SetupPlaylist(PlaylistItem item, bool launch)
        {
            if (item == null) return;
            var def = item.Def;
            var modes = new HashSet<string>(def.Modes, StringComparer.OrdinalIgnoreCase);
            bool Fits(ScenarioInfo s) => modes.Count == 0 || modes.Contains(State.Rules.ResolveMode(s.GameModeClass)?.Cls ?? "");

            // Keep the current map when it offers a fitting scenario, otherwise pick one that does.
            var map = selectedMap != null && selectedMap.Custom == null && selectedMap.Info.Scenarios.Any(Fits) ? selectedMap
                    : Maps.Where(m => m.Custom == null && m.Info.Source == ContentSource.Official && m.Info.Scenarios.Any(Fits)).OrderBy(_ => rng.Next()).FirstOrDefault();
            if (map == null) { ShowToast("No installed map has a scenario for " + item.Modes); return; }
            SelectedMap = map;
            var scenario = Scenarios.Where(s => Fits(s.Info)).OrderBy(s => s.Side == "Security" ? 0 : 1).FirstOrDefault();
            if (scenario != null) SelectedScenario = scenario;
            Night = def.Lighting == "Night";
            Hardcore = item.Hardcore;

            Profile.Mutators = new List<string>();
            foreach (var m in def.Mutators)
            {
                var info = State.FindMutator(m);
                if (info != null) Profile.Mutators.Add(info.Id);
            }
            foreach (var m in MutatorItems) m.SetActiveSilently(Profile.Mutators.Contains(m.Id, StringComparer.OrdinalIgnoreCase));
            BuildActiveMutators();
            Profile.MutatorsEnabled = true;
            Profile.MutatorPreset = null;
            BuildMutatorPresets();

            if (def.CoopRules.Count > 0)
                foreach (var cls in State.Rules.Modes.Where(m => m.Coop).Select(m => m.Cls))
                    foreach (var kv in def.CoopRules) SetRuleOverride(cls, kv.Key, kv.Value);
            if (!string.IsNullOrEmpty(def.Ruleset))
            {
                var rs = State.Rules.Rulesets.FirstOrDefault(r => r.Id == def.Ruleset);
                if (rs != null) foreach (var mode in rs.Rules) foreach (var kv in mode.Value) SetRuleOverride(mode.Key, kv.Key, kv.Value);
            }
            RefreshRuleItems();
            RaiseProfileFields();
            ProfileChanged();
            string note = def.Missing.Count > 0 ? " (" + string.Join(", ", def.Missing) + " left out: not in the current game)" : "";
            ShowToast(def.Title + " is set up on " + map.Name + note);
            if (!launch) PlayTab = "Map";
            else if (LaunchCommand.CanExecute(null)) LaunchCommand.Execute(null);
            else ShowToast(def.Title + " is set up. " + (PlanError ?? "Launch is not possible right now."));
        }
    }
}
