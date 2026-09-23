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
    public sealed partial class MainViewModel
    {
        public ObservableCollection<ModeDef> RuleModes { get; } = new ObservableCollection<ModeDef>();
        public ObservableCollection<CategoryItem> RuleCategories { get; } = new ObservableCollection<CategoryItem>();
        public ObservableCollection<RuleItem> RuleItems { get; } = new ObservableCollection<RuleItem>();
        public ObservableCollection<PresetItem> RulePresets { get; } = new ObservableCollection<PresetItem>();
        public ICollectionView RuleItemsView { get; private set; }
        public ICommand ApplyPresetCommand { get; private set; }
        public ICommand SaveRulesPresetCommand { get; private set; }
        public ICommand DeleteRulesPresetCommand { get; private set; }
        public ICommand ResetModeCommand { get; private set; }
        public ICommand ResetAllRulesCommand { get; private set; }
        public ICommand SelectRuleModeCommand { get; private set; }
        public ICommand SelectCategoryCommand { get; private set; }
        public ICommand InsertSectionCommand { get; private set; }
        private ModeDef rulesMode;
        private CategoryItem ruleCategory;
        private string ruleSearch = "", presetFilter = "Styles";

        public static readonly string[] CategoryOrder =
        {
            "Bots & AI", "Enemy forces", "Tickets & waves", "Rounds & time", "Objectives", "Defense & counter-attacks",
            "Respawning", "Supply & loadout", "Friendly fire", "Teams & players", "HUD & spectating", "Voice & chat"
        };

        private void InitRulesCommands()
        {
            ApplyPresetCommand = new AsyncCommand(p => ApplyPreset(p as PresetItem));
            SaveRulesPresetCommand = new AsyncCommand(SaveRulesPreset);
            DeleteRulesPresetCommand = new AsyncCommand(p => DeleteRulesPreset(p as PresetItem));
            ResetModeCommand = new AsyncCommand(ResetMode, () => rulesMode != null && Profile.Rules.ContainsKey(rulesMode.Cls));
            ResetAllRulesCommand = new AsyncCommand(ResetAllRules, () => Profile.Rules.Count > 0);
            SelectRuleModeCommand = new RelayCommand(p => { if (p is ModeDef m) RulesMode = m; });
            SelectCategoryCommand = new RelayCommand(p => { if (p is CategoryItem c) RuleCategory = c; });
            InsertSectionCommand = new RelayCommand(p => InsertMutatorSection(p as string));
        }

        private void BuildRules()
        {
            if (RuleModes.Count == 0) foreach (var m in State.Rules.Modes) RuleModes.Add(m);
            rulesMode = CurrentMode ?? State.Rules.Modes.First();
            BuildRuleItems();
            BuildPresets();
            Raise(nameof(RulesMode));
        }

        private void SyncRulesModeToScenario()
        {
            var m = CurrentMode;
            if (m != null && m != rulesMode) RulesMode = m;
        }

        public ModeDef RulesMode
        {
            get => rulesMode;
            set
            {
                if (value == null || !Set(ref rulesMode, value)) return;
                BuildRuleItems();
                RaiseMany(nameof(RulesModeTitle), nameof(RulesModeIsPlayed));
            }
        }

        public string RulesModeTitle => rulesMode == null ? "" : rulesMode.Name;
        public bool RulesModeIsPlayed => rulesMode != null && rulesMode == CurrentMode;

        private void BuildRuleItems()
        {
            RuleItems.Clear();
            if (rulesMode == null) return;
            foreach (var p in State.Rules.Properties)
            {
                if (!rulesMode.Defaults.TryGetValue(p.Key, out var def)) continue;
                RuleItems.Add(new RuleItem(p, rulesMode.Cls, def, RuleGet, (cls, key, v) => { RuleSet(cls, key, v); OnRuleEdited(); }));
            }
            string keep = ruleCategory?.Name ?? "Bots & AI";
            RuleCategories.Clear();
            foreach (var name in CategoryOrder)
            {
                int count = RuleItems.Count(r => r.Category == name);
                if (count > 0) RuleCategories.Add(new CategoryItem { Name = name, Count = count });
            }
            RuleCategories.Add(new CategoryItem { Name = "Changed", Count = 0 });
            RuleCategories.Add(new CategoryItem { Name = "All settings", Count = RuleItems.Count });
            RuleCategories.Add(new CategoryItem { Name = AdvancedCategory, Count = 0 });
            ruleCategory = RuleCategories.FirstOrDefault(c => c.Name == keep) ?? RuleCategories.First();
            RuleItemsView = CollectionViewSource.GetDefaultView(RuleItems);
            RuleItemsView.Filter = o =>
            {
                var r = (RuleItem)o;
                if (!string.IsNullOrWhiteSpace(ruleSearch))
                    return (r.Label ?? "").IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0 || r.Key.IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0
                           || (r.Description ?? "").IndexOf(ruleSearch, StringComparison.OrdinalIgnoreCase) >= 0;
                if (ruleCategory == null || ruleCategory.Name == "All settings") return true;
                if (ruleCategory.Name == AdvancedCategory) return false;
                if (ruleCategory.Name == "Changed") return r.Changed;
                return r.Category == ruleCategory.Name;
            };
            UpdateCategoryCounts();
            RaiseMany(nameof(RuleItemsView), nameof(RuleCategory), nameof(RulesModeTitle), nameof(RulesModeIsPlayed), nameof(ShowAdvancedRules));
        }

        public const string AdvancedCategory = "Advanced";
        public bool ShowAdvancedRules => ruleCategory?.Name == AdvancedCategory && string.IsNullOrWhiteSpace(ruleSearch);

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
            Raise(nameof(RulesSummary));
        }

        public int RuleChangeCount => RuleItems.Count(r => r.Changed);

        public CategoryItem RuleCategory
        {
            get => ruleCategory;
            set { if (Set(ref ruleCategory, value)) { RuleItemsView?.Refresh(); Raise(nameof(ShowAdvancedRules)); } }
        }

        public string RuleSearch
        {
            get => ruleSearch;
            set { if (Set(ref ruleSearch, value ?? "")) { RuleItemsView?.Refresh(); Raise(nameof(ShowAdvancedRules)); } }
        }

        // ------------------------------------------------------------------ presets

        public string PresetFilter
        {
            get => presetFilter;
            set { if (Set(ref presetFilter, value)) BuildPresets(); }
        }

        private static IEnumerable<string> CoopModes(RulesDb db) => db.Modes.Where(m => m.Coop).Select(m => m.Cls);
        private static IEnumerable<string> VersusModes(RulesDb db) => db.Modes.Where(m => !m.Coop).Select(m => m.Cls);

        /// <summary>Play styles tuned for solo and small co-op groups.</summary>
        private IEnumerable<RulesPreset> BuiltInStyles()
        {
            var db = State.Rules;
            RulesPreset Coop(string name, string desc, params (string k, string v)[] kv)
            {
                var p = new RulesPreset { Name = name, Description = desc };
                foreach (var cls in CoopModes(db)) p.Rules[cls] = kv.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);
                return p;
            }
            yield return Coop("Lone Wolf", "Just you against the insurgency. No AI teammates, default enemy numbers.", ("FriendlyBotQuota", "0"));
            yield return Coop("Lone Wolf: Hardened", "No teammates, more and sharper enemies, one extra solo wave to lean on.",
                ("FriendlyBotQuota", "0"), ("SoloEnemies", "10"), ("AIDifficulty", "0.75"), ("SoloWaves", "2"));
            yield return Coop("Fireteam", "You plus two AI riflemen against a slightly larger force.",
                ("FriendlyBotQuota", "2"), ("SoloEnemies", "8"));
            yield return Coop("Squad Leader", "Lead six AI teammates into heavier resistance.",
                ("FriendlyBotQuota", "6"), ("SoloEnemies", "12"), ("MinimumEnemies", "6"), ("MaximumEnemies", "16"));
            yield return Coop("Full Platoon", "Ten teammates, big enemy waves, a war-sized fight.",
                ("FriendlyBotQuota", "10"), ("SoloEnemies", "18"), ("MinimumEnemies", "10"), ("MaximumEnemies", "24"), ("AIDifficulty", "0.6"));
            yield return Coop("Relaxed", "Fewer, slower-reacting enemies and quicker respawns. Good for learning maps.",
                ("AIDifficulty", "0.25"), ("SoloEnemies", "4"), ("RespawnDelay", "10"));
            yield return Coop("Realism", "No death camera, no floating markers, no kill feed, full friendly fire damage.",
                ("bAllowDeathCamera", "False"), ("FloatingObjectiveVisibility", "HideAll"), ("bKillFeed", "False"), ("bKillerInfo", "False"), ("FriendlyFireModifier", "1"));
            yield return Coop("Sandbox", "Practice without pressure: rounds never end, huge supply, long clock.",
                ("bIgnoreRoundOver", "True"), ("RoundTime", "7200"), ("SoloRoundTime", "7200"), ("InitialSupply", "100"), ("MaximumSupply", "100"), ("SoloWaves", "50"));
            var vs = new RulesPreset { Name = "Versus vs bots", Description = "Fill both teams with bots in any versus mode." };
            foreach (var cls in VersusModes(db)) vs.Rules[cls] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bBots"] = "True", ["BotQuota"] = "16" };
            yield return vs;
            var quick = new RulesPreset { Name = "Quick rounds", Description = "Shorter rounds and pre-round time in versus modes." };
            foreach (var cls in VersusModes(db)) quick.Rules[cls] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PreRoundTime"] = "5", ["RoundTime"] = "300" };
            yield return quick;
        }

        private void BuildPresets()
        {
            RulePresets.Clear();
            if (presetFilter == "Styles")
                foreach (var p in BuiltInStyles()) RulePresets.Add(new PresetItem { Name = p.Name, Group = "Play style", Description = p.Description, Source = p });
            else if (presetFilter == "Official")
                foreach (var r in State.Rules.Rulesets.Where(r => r.Rules.Count > 0))
                {
                    int changes = r.Rules.Values.Sum(v => v.Count);
                    string desc = r.Id + " · " + string.Join(", ", r.Rules.Keys.Select(k => State.Rules.Mode(k)?.Name ?? k).Distinct().Take(4))
                                  + (r.Rules.Count > 4 ? " and more" : "") + (r.Notes.Count > 0 ? ". " + string.Join(" ", r.Notes) : "");
                    RulePresets.Add(new PresetItem { Name = r.Name, Group = "Official ruleset", Description = desc, Source = r });
                }
            else
                foreach (var p in State.Settings.RulesPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                    RulePresets.Add(new PresetItem { Name = p.Name, Group = "My preset", Description = string.IsNullOrEmpty(p.Description) ? RulesPresetSummary(p) : p.Description, Source = p });
            Raise(nameof(HasNoPresets));
        }

        public bool HasNoPresets => RulePresets.Count == 0;

        private string RulesPresetSummary(RulesPreset p)
        {
            int n = p.Rules.Values.Sum(v => v.Count);
            return n + " setting" + (n == 1 ? "" : "s") + " across " + p.Rules.Count + " mode" + (p.Rules.Count == 1 ? "" : "s") + (p.Mutators.Count > 0 ? ", " + p.Mutators.Count + " mutators" : "");
        }

        private async Task ApplyPreset(PresetItem item)
        {
            if (item == null) return;
            Dictionary<string, Dictionary<string, string>> rules = null;
            List<string> mutators = null;
            switch (item.Source)
            {
                case RulesPreset rp: rules = rp.Rules; mutators = rp.Mutators; break;
                case RulesetDef rd: rules = rd.Rules; mutators = rd.Mutators; break;
            }
            if (rules == null) return;
            foreach (var mode in rules)
                foreach (var kv in mode.Value)
                {
                    string def = State.Rules.DefaultValue(mode.Key, kv.Key);
                    RuleSet(mode.Key, kv.Key, def != null && LaunchPlanner.Same(def, kv.Value, State.Rules.Prop(kv.Key)) ? null : kv.Value);
                }
            if (mutators != null) foreach (var m in mutators) if (State.FindMutator(m) != null) SetMutatorActive(State.FindMutator(m).Id, true);
            Profile.RulesPresetName = item.Name;
            RefreshRuleItems();
            RaiseSquad();
            ProfileChanged();
            ShowToast(item.Name + " applied" + (mutators != null && mutators.Count > 0 ? " (+ " + string.Join(", ", mutators) + ")" : ""));
            if (item.Source is RulesetDef def2 && def2.Notes.Count > 0)
                await ShowMessage(item.Name, string.Join("\n\n", def2.Notes) + "\n\nTo get every part of the official ruleset, including player speed and health changes, pick it under \"Official ruleset at game start\" on this page. It takes effect the next time the launcher starts the game.");
        }

        private async Task SaveRulesPreset()
        {
            if (Profile.Rules.Count == 0 && Profile.Mutators.Count == 0) { await ShowMessage("Nothing to save", "Change some settings first, then save them as a preset."); return; }
            string name = await Prompt("Save rules preset", "Name for these rule changes (all modes) and mutators:", Profile.RulesPresetName ?? "My rules", "Save");
            if (string.IsNullOrWhiteSpace(name)) return;
            var copy = new RulesPreset { Name = name, Mutators = new List<string>(Profile.Mutators) };
            foreach (var kv in Profile.Rules) copy.Rules[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            State.Settings.RulesPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            State.Settings.RulesPresets.Add(copy);
            SaveSettingsSoon();
            PresetFilter = "Mine";
            BuildPresets();
            ShowToast("Preset \"" + name + "\" saved");
        }

        private async Task DeleteRulesPreset(PresetItem item)
        {
            if (!(item?.Source is RulesPreset rp) || !State.Settings.RulesPresets.Contains(rp)) return;
            if (await Ask("Delete preset", "Delete \"" + rp.Name + "\"?", "Delete") != "Delete") return;
            State.Settings.RulesPresets.Remove(rp);
            SaveSettingsSoon();
            BuildPresets();
        }

        private async Task ResetMode()
        {
            if (rulesMode == null) return;
            if (await Ask("Reset " + rulesMode.Name, "Put every " + rulesMode.Name + " setting back to the game default?", "Reset") != "Reset") return;
            Profile.Rules.Remove(rulesMode.Cls);
            RefreshRuleItems();
            RaiseSquad();
            ProfileChanged();
        }

        private async Task ResetAllRules()
        {
            if (await Ask("Reset all rules", "Put every setting in every mode back to the game defaults for this profile?", "Reset all") != "Reset all") return;
            Profile.Rules.Clear();
            Profile.RulesPresetName = null;
            RefreshRuleItems();
            RaiseSquad();
            ProfileChanged();
        }

        public string RulesSummary
        {
            get
            {
                var plan = CurrentPlan;
                int n = plan?.Overrides.Count ?? 0;
                return n == 0 ? "Game default rules" : n + " rule change" + (n == 1 ? "" : "s");
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
