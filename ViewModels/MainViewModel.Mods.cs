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
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<ModItem> ModItems { get; } = new ObservableCollection<ModItem>();
        public ICollectionView ModsView { get; private set; }
        public ICommand RescanModsCommand { get; private set; }
        public ICommand OpenModFolderCommand { get; private set; }
        public ICommand UseModMutatorsCommand { get; private set; }
        public ICommand AddMutatorByIdCommand { get; private set; }
        public ICommand ModsTabCommand { get; private set; }
        private string modSearch = "", modsTab = "Mutators";
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
            AddMutatorByIdCommand = new RelayCommand(p => { if (p is MutatorInfo m) { SetMutatorActive(m.Id, true); ShowToast(F("{0} added", m.DisplayName)); } });
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
            RaiseMany(nameof(ModsView), nameof(ModCount), nameof(ModsSummary));
        }

        public int ModCount => ModItems.Count;
        public string ModsSummary => F("{0} mods installed · {1} mod mutators", ModItems.Count, State.AllMutators.Count(m => m.Source == ContentSource.Mod && m.Registered && !m.IsBaseClass));
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
            else { Page = "Play"; PlayTab = "Mods"; ModsTab = "Mutators"; MutatorSearch = item.Name; ShowToast(F("Pick which of the {0} mutators to use", usable.Count)); return; }
            ShowToast(F("{0} added to your mutators", usable[0].DisplayName));
        }
    }
}
