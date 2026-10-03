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
        public ObservableCollection<CategoryItem> RuleCategories { get; } = new ObservableCollection<CategoryItem>();
        public ObservableCollection<RuleItem> RuleItems { get; } = new ObservableCollection<RuleItem>();
        public ObservableCollection<PresetItem> RulePresets { get; } = new ObservableCollection<PresetItem>();
        public ICollectionView RuleItemsView { get; private set; }
        public ICommand ApplyPresetCommand { get; private set; }
        public ICommand ResetModeCommand { get; private set; }
        public ICommand ResetAllRulesCommand { get; private set; }
        public ICommand InsertSectionCommand { get; private set; }
        public ICommand PlayTabCommand { get; private set; }
        public ICommand ResetSetupCommand { get; private set; }
        private ModeDef rulesMode;
        private CategoryItem ruleCategory;
        private string ruleSearch = "", presetFilter = "Styles", playTab = "Map";

        public static readonly string[] CategoryOrder =
        {
            "Bots & AI", "Enemy forces", "Tickets & waves", "Rounds & time", "Objectives", "Defense & counter-attacks",
            "Respawning", "Supply & loadout", "Friendly fire", "Teams & players", "HUD & spectating", "Voice & chat"
        };

        /// <summary>Set in the squad card on Play, so the rules list leaves them out (nothing is shown twice).</summary>
        public static readonly HashSet<string> SquadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "FriendlyBotQuota", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "AIDifficulty", "bBots", "BotQuota" };

        private void InitRulesCommands()
        {
            ApplyPresetCommand = new RelayCommand(p => ApplyPreset(p as PresetItem));
            ResetModeCommand = new AsyncCommand(ResetMode, () => rulesMode != null && Profile.Rules.ContainsKey(rulesMode.Cls));
            ResetAllRulesCommand = new AsyncCommand(ResetAllRules, () => Profile.Rules.Count > 0);
            InsertSectionCommand = new RelayCommand(p => InsertMutatorSection(p as string));
            PlayTabCommand = new RelayCommand(p => { Page = "Play"; PlayTab = p as string ?? "Map"; });
            ResetSetupCommand = new AsyncCommand(ResetSetup);
        }

        /// <summary>Map, Rules, Mods, Live or Advanced: the views of the Play page.</summary>
        public string PlayTab
        {
            get => playTab;
            set
            {
                // Playlists are presets in the Rules tab since 1.4.0 (older links and settings still say "Playlists").
                if (value == "Playlists") { PresetFilter = "Playlists"; value = "Rules"; }
                // Live is its own page since 1.8.0.
                if (value == "Live") { Page = "Live"; return; }
                if (!Set(ref playTab, string.IsNullOrEmpty(value) ? "Map" : value)) return;
                SyncRulesModeToScenario();
            }
        }

        private void BuildRules()
        {
            // No known mode for the scenario: no rules (a rescan put the first mode's rules back, 2026-10-02 audit).
            rulesMode = CurrentMode;
            BuildRuleItems();
            UpdateCategoryCounts();
            BuildPresets();
        }

        /// <summary>The rules always belong to the game mode of the scenario picked on the map.</summary>
        private void SyncRulesModeToScenario()
        {
            // The preset lists follow co-op or versus even for a scenario without known rules (a custom map entry).
            PresetsFollowScenario();
            var m = CurrentMode;
            if (m == rulesMode) return;
            // A scenario whose game mode has no known rules (a custom map entry, a mod's own mode): no rules to edit; the
            // tab kept the mode picked before, whose changes this launch would not send (2026-10-02 audit).
            rulesMode = m;
            BuildRuleItems();
            UpdateCategoryCounts();
            Raise(nameof(RulesModeTitle));
        }

        public string RulesModeTitle => rulesMode == null ? "" : T(rulesMode.Name);

        private void BuildRuleItems()
        {
            RuleItems.Clear();
            if (rulesMode == null)
            {
                // No rules to list: no categories either (they stayed, in the language picked before, 2026-10-02 audit).
                RuleCategories.Clear();
                ruleCategory = null;
                Raise(nameof(RuleCategory));
                UpdateCategoryCounts();
                return;
            }
            foreach (var p in State.Rules.Properties)
            {
                if (SquadKeys.Contains(p.Key) || !rulesMode.Defaults.TryGetValue(p.Key, out var def)) continue;
                // The default the launch uses (the game's, or the launcher's where it differs: what the screen shows is sent).
                RuleItems.Add(new RuleItem(p, rulesMode.Cls, SetupEngine.LauncherDefault(State.Rules, rulesMode.Cls, p.Key) ?? def, RuleGet, (cls, key, v) => { RuleSet(cls, key, v); OnRuleEdited(); }));
            }
            // Settings other modes have: listed read-only so every mode shows the full picture.
            foreach (var p in State.Rules.Properties)
            {
                if (SquadKeys.Contains(p.Key) || rulesMode.Defaults.ContainsKey(p.Key)) continue;
                var owners = State.Rules.Modes.Where(m => m.Defaults.ContainsKey(p.Key)).ToList();
                if (owners.Count == 0) continue;
                string who = owners.All(m => m.Coop) ? T("co-op modes") : owners.All(m => !m.Coop) ? T("versus modes") : string.Join(", ", owners.Select(m => T(m.Name)));
                RuleItems.Add(new RuleItem(p, rulesMode.Cls, owners[0].Defaults[p.Key], (c, k) => null, (c, k, v) => { })
                {
                    IsAvailable = false,
                    AvailabilityNote = F("Only in {0}. {1} has no such setting, so the game would ignore it.", who, T(rulesMode.Name))
                });
            }
            string keep = ruleCategory?.Name ?? "All settings";
            RuleCategories.Clear();
            RuleCategories.Add(new CategoryItem { Name = N("All settings"), Count = RuleItems.Count(r => r.IsAvailable) });
            RuleCategories.Add(new CategoryItem { Name = N("Changed"), Count = 0 });
            foreach (var name in CategoryOrder)
            {
                int count = RuleItems.Count(r => r.Category == name && r.IsAvailable);
                if (count > 0) RuleCategories.Add(new CategoryItem { Name = name, Count = count });
            }
            int other = RuleItems.Count(r => !r.IsAvailable);
            if (other > 0) RuleCategories.Add(new CategoryItem { Name = RuleItem.UnavailableCategory, Count = other });
            ruleCategory = RuleCategories.FirstOrDefault(c => c.Name == keep) ?? RuleCategories.First();
            RuleItemsView = CollectionViewSource.GetDefaultView(RuleItems);
            RuleItemsView.Filter = o =>
            {
                var r = (RuleItem)o;
                if (!r.IsAvailable) return string.IsNullOrWhiteSpace(ruleSearch) && ruleCategory?.Name == RuleItem.UnavailableCategory;
                if (!string.IsNullOrWhiteSpace(ruleSearch))
                    return (r.Label ?? "").IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0 || r.Key.IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0
                           || (r.Description ?? "").IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0;
                if (ruleCategory == null || ruleCategory.Name == "All settings") return true;
                if (ruleCategory.Name == "Changed") return r.Changed;
                return r.Category == ruleCategory.Name;
            };
            UpdateCategoryCounts();
            RaiseMany(nameof(RuleItemsView), nameof(RuleCategory), nameof(RulesModeTitle));
        }

        private void RefreshRuleItems()
        {
            foreach (var r in RuleItems) r.Refresh();
            UpdateCategoryCounts();
        }

        private void OnRuleEdited()
        {
            UpdateCategoryCounts();
            RaiseSquad();
        }

        private void UpdateCategoryCounts()
        {
            foreach (var c in RuleCategories)
                c.Changes = c.Name == "Changed" || c.Name == "All settings" ? RuleItems.Count(r => r.Changed) : RuleItems.Count(r => r.Category == c.Name && r.Changed);
            Raise(nameof(RuleChangeCount));
            Raise(nameof(RulesTabLabel));
            Raise(nameof(RulesSummary));
        }

        /// <summary>Changed settings in the rules list (the squad card shows its own).</summary>
        public int RuleChangeCount => RuleItems.Count(r => r.Changed);
        public string RulesTabLabel => RuleChangeCount == 0 ? T("Rules") : F("Rules · {0}", RuleChangeCount);

        public CategoryItem RuleCategory
        {
            get => ruleCategory;
            set { if (value != null && Set(ref ruleCategory, value)) RuleItemsView?.Refresh(); }
        }

        public string RuleSearch
        {
            get => ruleSearch;
            set { if (Set(ref ruleSearch, value ?? "")) RuleItemsView?.Refresh(); }
        }

        // ------------------------------------------------------------------ presets (the logic is in Game/SetupEngine)

        public ObservableCollection<PresetItem> SquadPresets { get; } = new ObservableCollection<PresetItem>();
        private string presetKind;

        /// <summary>Styles, Official or Playlists: the preset lists of the Rules tab (saved setups are in the bar at the top).</summary>
        public string PresetFilter
        {
            get => presetFilter;
            set
            {
                string v = value == "Official" || value == "Playlists" ? value : "Styles";
                if (Set(ref presetFilter, v)) BuildPresets();
            }
        }

        private string presetSearch = "";

        /// <summary>Filters the preset list by name, description, mutators or mode.</summary>
        public string PresetSearch
        {
            get => presetSearch;
            set { if (Set(ref presetSearch, value ?? "")) BuildPresets(); }
        }

        public string PresetCountText { get; private set; } = "";

        private bool CurrentIsCoop => CurrentMode == null || CurrentMode.Coop;
        public string SquadKindLabel => CurrentIsCoop ? T("For every co-op mode. Applying one replaces the bot values it is about; fine-tune them on the right.")
                                                     : T("For every versus mode. Applying one replaces the bot values it is about; fine-tune them on the right.");
        /// <summary>The last preset applied or saved setup loaded (shown in the match summary).</summary>
        public string ActivePresetName => SetupEngine.PresetLabel(Profile);

        private static PresetItem Item(Preset p) =>
            new PresetItem { Name = T(p.Name), Group = p.Group, Description = T(p.Description), Tag = p.Tag, Note = T(p.Note), Source = p };

        private void BuildPresets()
        {
            bool coop = CurrentIsCoop;
            RulePresets.Clear();
            IEnumerable<Preset> list = presetFilter == "Official" ? SetupEngine.OfficialPresets(State.Rules)
                                     : presetFilter == "Playlists" ? SetupEngine.PlaylistPresets(State, coop)
                                     : SetupEngine.StylePresets(State.Rules);
            var all = list.Select(Item).ToList();
            string q = (presetSearch ?? "").Trim();
            foreach (var item in all)
                if (q.Length == 0 || (item.Name + " " + item.Description + " " + item.Note + " " + ((Preset)item.Source).Name).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    RulePresets.Add(item);
            PresetCountText = q.Length == 0 ? (all.Count == 1 ? T("1 preset") : F("{0} presets", all.Count)) : F("{0} of {1} presets", RulePresets.Count, all.Count);
            Raise(nameof(PresetCountText));
            MarkActivePresets();
            SquadPresets.Clear();
            foreach (var p in SetupEngine.SquadPresets(State.Rules, coop)) SquadPresets.Add(Item(p));
            MarkActivePresets();
            presetKind = coop ? "Co-op" : "Versus";
            Raise(nameof(HasNoPresets));
        }

        public bool HasNoPresets => RulePresets.Count == 0;

        /// <summary>Called when the picked scenario changes: squad presets and the playlist order follow co-op or versus.</summary>
        private void PresetsFollowScenario()
        {
            if (presetKind != (CurrentIsCoop ? "Co-op" : "Versus")) BuildPresets();
        }

        /// <summary>The preset in use stands out in the lists.</summary>
        private void MarkActivePresets()
        {
            foreach (var item in RulePresets.Concat(SquadPresets))
                item.IsActive = item.Source is Preset pr && SetupEngine.InUse(Profile, pr);
        }

        private void ApplyPreset(PresetItem item)
        {
            if (!(item?.Source is Preset preset)) return;
            string message = SetupEngine.Apply(Profile, State, preset);
            RefreshFromProfile(preset.Kind == PresetKind.Saved);
            ShowToast(message);
        }

        /// <summary>
        /// After the setup changed in the engine: everything on screen is read again from the profile (one way only,
        /// so the screen can never disagree with what gets launched), then it is saved.
        /// </summary>
        private void RefreshFromProfile(bool mapChanged)
        {
            suppressProfileSave = true;
            try
            {
                foreach (var m in Maps) m.Night = Profile.Lighting == "Night";
                if (mapChanged) SyncMapSelection();
                BuildMutatorList();
                BuildActiveMutators();
                rulesMode = null;
                SyncRulesModeToScenario();
                RefreshRuleItems();
                RaiseProfileFields();
                MarkActivePresets();
                RaiseMany(nameof(IsCoopMode), nameof(IsVersusMode), nameof(CurrentModeName), nameof(CanHardcore));
            }
            finally { suppressProfileSave = false; }
            ProfileChanged();
        }

        private async Task ResetMode()
        {
            if (rulesMode == null) return;
            if (await Ask(F("Reset {0}", T(rulesMode.Name)), F("Put every {0} setting back to the game default?", T(rulesMode.Name)), "Reset") != "Reset") return;
            string cls = rulesMode.Cls;
            SetupEngine.ClearRules(Profile, (c, k) => c == cls);
            RefreshFromProfile(false);
        }

        /// <summary>Bots, enemies, every rule and the mutators back to the defaults; the map, scenario and conditions stay.</summary>
        private async Task ResetSetup()
        {
            if (await Ask("Reset the setup", "Put bots and enemies, every rule and the mutators back to the game defaults? The map and scenario stay.", "Reset") != "Reset") return;
            SetupEngine.ResetAll(Profile);
            RefreshFromProfile(false);
            ShowToast("Setup reset to the game defaults");
        }

        private async Task ResetAllRules()
        {
            if (await Ask("Reset all rules", "Put every setting in every mode (bots and enemies too) back to the game defaults? Map and mutators stay.", "Reset all") != "Reset all") return;
            SetupEngine.ClearRules(Profile, (c, k) => true);
            Profile.RulesPresetName = null;
            Profile.PresetCheck = null;
            Profile.PresetKeys = new List<string>();
            Profile.PresetChanges = null;
            RefreshFromProfile(false);
        }

        /// <summary>The player's own rule changes, as the Rules tab counts them (bots are in their own line; the launcher's
        /// automatic adjustments for offline play are in the command line, not counted as the player's changes).</summary>
        public string RulesSummary => State.Settings.OwnRules ? T("Rules from your Game.ini") : RuleChangesSummary;

        /// <summary>The rule changes of the Play match (what the server gets, whatever your game uses).</summary>
        public string RuleChangesSummary
        {
            get
            {
                int n = RuleChangeCount;
                return n == 0 ? T("Game default rules") : n == 1 ? T("1 rule change") : F("{0} rule changes", n);
            }
        }

        // ------------------------------------------------------------------ advanced profile fields

        public List<string> OfficialRulesetChoices => new[] { "" }.Concat(State.Rules?.Rulesets.Select(r => r.Id) ?? Enumerable.Empty<string>()).ToList();

        public string LaunchRuleset
        {
            get => Profile.LaunchRuleset ?? "";
            set { Profile.LaunchRuleset = string.IsNullOrWhiteSpace(value) ? null : value; Raise(); ProfileChanged(); }
        }

        public string CustomIniMode
        {
            get => Profile.CustomIniMode ?? "Off";
            set { Profile.CustomIniMode = value; Raise(); ProfileChanged(); }
        }

        public string CustomIniText
        {
            get => Profile.CustomIniText ?? "";
            set { Profile.CustomIniText = value; Raise(); ProfileChanged(); }
        }

        public string ExtraUrlOptions
        {
            get => Profile.ExtraUrlOptions ?? "";
            set { Profile.ExtraUrlOptions = value; Raise(); ProfileChanged(); }
        }

        public string GameModeOverride
        {
            get => Profile.GameModeOverride ?? "";
            set { Profile.GameModeOverride = value; Raise(); ProfileChanged(); }
        }

        public string AfterLoadCommands
        {
            get => Profile.AfterLoadCommands ?? "";
            set { Profile.AfterLoadCommands = value; Raise(); ProfileChanged(); }
        }

        public bool EnableCheatsAfterLoad
        {
            get => Profile.EnableCheatsAfterLoad;
            set { Profile.EnableCheatsAfterLoad = value; Raise(); ProfileChanged(); }
        }

        public bool ForceReload
        {
            get => Profile.ForceReload;
            set { Profile.ForceReload = value; Raise(); ProfileChanged(); }
        }

        public List<string> ActiveMutatorSections =>
            Profile.Mutators.Select(id => State.FindMutator(id)).Where(m => m?.ConfigSection != null).Select(m => m.ConfigSection).ToList();

        private void InsertMutatorSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return;
            string text = CustomIniText ?? "";
            if (text.IndexOf(section, StringComparison.OrdinalIgnoreCase) >= 0) { ShowToast("That section is already in the editor"); return; }
            CustomIniText = (text.TrimEnd() + (text.Trim().Length > 0 ? "\r\n\r\n" : "") + section + "\r\n; add this mutator's settings here, e.g. SomeSetting=Value\r\n").TrimStart();
            if (CustomIniMode == "Off") CustomIniMode = "Append";
        }
    }
}
