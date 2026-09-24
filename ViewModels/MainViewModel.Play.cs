using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed partial class MainViewModel
    {
        public ObservableCollection<MapItem> Maps { get; } = new ObservableCollection<MapItem>();
        public ObservableCollection<ScenarioItem> Scenarios { get; } = new ObservableCollection<ScenarioItem>();
        public ObservableCollection<ScenarioModeGroup> ScenarioModeGroups { get; } = new ObservableCollection<ScenarioModeGroup>();
        public ObservableCollection<ScenarioItem> ScenarioSides { get; } = new ObservableCollection<ScenarioItem>();
        public ICommand SelectScenarioModeCommand { get; private set; }
        public ICollectionView MapsView { get; private set; }
        public ICommand SelectMapCommand { get; private set; }
        public ICommand SelectScenarioCommand { get; private set; }
        public ICommand RandomMissionCommand { get; private set; }
        public ICommand PlayStyleCommand { get; private set; }
        public ICommand StepCommand { get; private set; }
        private MapItem selectedMap;
        private ScenarioItem selectedScenario;
        private string mapFilter = "All", mapSearch = "";
        private static readonly Random rng = new Random();

        private void InitPlayCommands()
        {
            SelectMapCommand = new RelayCommand(p => { if (p is MapItem m) SelectedMap = m; });
            SelectScenarioCommand = new RelayCommand(p => { if (p is ScenarioItem s) SelectedScenario = s; });
            SelectScenarioModeCommand = new RelayCommand(p => SelectScenarioMode(p as ScenarioModeItem));
            RandomMissionCommand = new RelayCommand(RandomMission);
            PlayStyleCommand = new RelayCommand(p => ApplyPlayStyle(p as string));
            StepCommand = new RelayCommand(p => Step(p as string));
        }

        // ------------------------------------------------------------------ maps

        private void BuildMaps()
        {
            Maps.Clear();
            bool night = Profile.Lighting == "Night";
            foreach (var m in State.Maps) Maps.Add(new MapItem(m) { Night = night });
            foreach (var c in State.Settings.CustomMaps)
            {
                var info = new MapInfo { Key = "custom:" + c.Id, DisplayName = c.Label, Level = c.Level, LevelName = c.Level, Source = ContentSource.Custom };
                var official = State.Maps.FirstOrDefault(x => x.LevelName.Equals(c.Level, StringComparison.OrdinalIgnoreCase));
                if (official != null) { info.ThumbDay = official.ThumbDay; info.ThumbNight = official.ThumbNight; }
                Maps.Add(new MapItem(info, c) { Night = night });
            }
            MapsView = CollectionViewSource.GetDefaultView(Maps);
            MapsView.Filter = o =>
            {
                var m = (MapItem)o;
                if (mapFilter != "All" && m.Source != mapFilter) return false;
                return string.IsNullOrWhiteSpace(mapSearch) || m.Name.IndexOf(mapSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || m.Info.LevelName?.IndexOf(mapSearch, StringComparison.OrdinalIgnoreCase) >= 0
                       || (m.Info.ModName ?? "").IndexOf(mapSearch, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            RaiseMany(nameof(MapsView), nameof(OfficialMapCount), nameof(ModMapCount), nameof(CustomMapCount), nameof(AllMapCount));
        }

        public int AllMapCount => Maps.Count;
        public int OfficialMapCount => Maps.Count(m => m.Source == "Official");
        public int ModMapCount => Maps.Count(m => m.Source == "Mods");
        public int CustomMapCount => Maps.Count(m => m.Source == "Custom");

        public string MapFilter
        {
            get => mapFilter;
            set { if (Set(ref mapFilter, value)) MapsView?.Refresh(); }
        }

        public string MapSearch
        {
            get => mapSearch;
            set { if (Set(ref mapSearch, value ?? "")) MapsView?.Refresh(); }
        }

        private void SyncMapSelection()
        {
            MapItem target = null;
            if (!string.IsNullOrEmpty(Profile.CustomMapId)) target = Maps.FirstOrDefault(m => m.Custom?.Id == Profile.CustomMapId);
            if (target == null) target = Maps.FirstOrDefault(m => m.Custom == null && m.Info.Key.Equals(Profile.MapKey ?? "", StringComparison.OrdinalIgnoreCase));
            if (target == null) target = Maps.FirstOrDefault(m => m.Info.Key == "Farmhouse") ?? Maps.FirstOrDefault();
            SetSelectedMap(target, false);
        }

        public MapItem SelectedMap
        {
            get => selectedMap;
            set => SetSelectedMap(value, true);
        }

        private void SetSelectedMap(MapItem value, bool user)
        {
            if (selectedMap != null) selectedMap.IsSelected = false;
            selectedMap = value;
            if (value != null) value.IsSelected = true;
            Raise(nameof(SelectedMap));
            Scenarios.Clear();
            if (value == null) { SelectedScenario = null; return; }
            if (value.Custom != null)
            {
                Profile.CustomMapId = value.Custom.Id;
                var sc = new ScenarioInfo { Id = value.Custom.Scenario, Level = value.Custom.Level, GameModeClass = value.Custom.GameModeClass ?? "",
                                            GameModeName = "Custom scenario", Side = value.Custom.Scenario, Category = "Custom", Source = ContentSource.Custom };
                Scenarios.Add(new ScenarioItem(sc));
            }
            else
            {
                if (user) Profile.CustomMapId = null;
                Profile.MapKey = value.Info.Key;
                foreach (var s in value.Info.Scenarios) Scenarios.Add(new ScenarioItem(s));
            }
            BuildScenarioModes();
            var keep = Scenarios.FirstOrDefault(s => s.Id.Equals(Profile.ScenarioId ?? "", StringComparison.OrdinalIgnoreCase));
            if (keep == null && user && selectedScenario != null)
                keep = Scenarios.FirstOrDefault(s => s.Mode == selectedScenario.Mode && s.Side == selectedScenario.Side)
                       ?? Scenarios.FirstOrDefault(s => s.Mode == selectedScenario.Mode);
            SetSelectedScenario(keep ?? DefaultScenario(), user);
            Raise(nameof(SelectedMapTitle));
            if (user) ProfileChanged();
        }

        public string SelectedMapTitle => selectedMap == null ? "Pick a map" : selectedMap.Name;

        /// <summary>Co-op Checkpoint as Security when the map has it: the usual way to start.</summary>
        private ScenarioItem DefaultScenario() =>
            Scenarios.FirstOrDefault(s => s.Info.GameModeClass == "INSCheckpointGameMode" && string.Equals(s.Side, "Security", StringComparison.OrdinalIgnoreCase))
            ?? Scenarios.FirstOrDefault(s => s.Category == "Co-op" && string.Equals(s.Side, "Security", StringComparison.OrdinalIgnoreCase))
            ?? Scenarios.FirstOrDefault(s => s.Category == "Co-op")
            ?? Scenarios.FirstOrDefault();

        private static int CategoryRank(string c) => c == "Co-op" ? 0 : c == "Versus" ? 1 : c == "Custom" ? 2 : c == "Training" ? 4 : 3;

        /// <summary>Groups the map's scenarios into mode chips, each with its sides.</summary>
        private void BuildScenarioModes()
        {
            ScenarioModeGroups.Clear();
            var modes = new List<ScenarioModeItem>();
            foreach (var s in Scenarios)
            {
                var m = modes.FirstOrDefault(x => x.Mode == s.Mode && x.Category == s.Category);
                if (m == null) modes.Add(m = new ScenarioModeItem { Mode = s.Mode, Category = s.Category });
                m.Items.Add(s);
            }
            foreach (var m in modes)
            {
                bool unique = m.Items.Select(i => i.Side).Distinct(StringComparer.OrdinalIgnoreCase).Count() == m.Items.Count;
                foreach (var i in m.Items)
                {
                    string label = unique && !string.IsNullOrWhiteSpace(i.Side) ? i.Side : null;
                    if (label == null)
                    {
                        string id = i.Id ?? "", token = "_" + m.Mode.Replace(" ", "");
                        int at = id.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                        string rest = at >= 0 ? id.Substring(at + token.Length).Trim('_') : "";
                        label = rest.Length > 0 ? rest.Replace('_', ' ') : id.Replace("Scenario_", "").Replace('_', ' ');
                    }
                    i.SideLabel = label;
                }
            }
            foreach (var g in modes.GroupBy(m => m.Category).OrderBy(g => CategoryRank(g.Key)))
                ScenarioModeGroups.Add(new ScenarioModeGroup { Name = g.Key, Modes = g.ToList() });
        }

        private void SyncScenarioModes()
        {
            ScenarioSides.Clear();
            foreach (var g in ScenarioModeGroups)
                foreach (var m in g.Modes)
                {
                    m.IsSelected = selectedScenario != null && m.Items.Contains(selectedScenario);
                    if (m.IsSelected && m.Items.Count > 1) foreach (var i in m.Items) ScenarioSides.Add(i);
                }
            RaiseMany(nameof(HasScenarioSides), nameof(SelectedScenarioId));
        }

        public bool HasScenarioSides => ScenarioSides.Count > 1;
        public string SelectedScenarioId => selectedScenario?.Id ?? "";

        private void SelectScenarioMode(ScenarioModeItem mode)
        {
            if (mode == null || mode.Items.Count == 0) return;
            string side = selectedScenario?.Side;
            SelectedScenario = mode.Items.FirstOrDefault(i => !string.IsNullOrEmpty(side) && string.Equals(i.Side, side, StringComparison.OrdinalIgnoreCase))
                               ?? mode.Items.FirstOrDefault(i => string.Equals(i.Side, "Security", StringComparison.OrdinalIgnoreCase))
                               ?? mode.Items[0];
        }

        // ------------------------------------------------------------------ scenario

        public ScenarioItem SelectedScenario
        {
            get => selectedScenario;
            set => SetSelectedScenario(value, true);
        }

        private void SetSelectedScenario(ScenarioItem value, bool user)
        {
            if (selectedScenario != null) selectedScenario.IsSelected = false;
            selectedScenario = value;
            if (value != null) { value.IsSelected = true; Profile.ScenarioId = value.Id; }
            SyncScenarioModes();
            RaiseMany(nameof(SelectedScenario), nameof(CanHardcore), nameof(IsCoopMode), nameof(IsVersusMode), nameof(CurrentModeName));
            RaiseSquad();
            SyncRulesModeToScenario();
            if (user) ProfileChanged();
        }

        public bool CanHardcore => selectedScenario?.Info.GameModeClass == "INSCheckpointGameMode";
        public ModeDef CurrentMode => State.ModeFor(selectedScenario?.Info, Profile.Hardcore);
        public bool IsCoopMode => CurrentMode?.Coop ?? (selectedScenario?.Info.IsCoop ?? false);
        public bool IsVersusMode => CurrentMode != null && !CurrentMode.Coop;
        public string CurrentModeName => CurrentMode?.Name ?? selectedScenario?.Mode ?? "";

        // ------------------------------------------------------------------ conditions

        public bool Night
        {
            get => Profile.Lighting == "Night";
            set
            {
                Profile.Lighting = value ? "Night" : "Day";
                foreach (var m in Maps) m.Night = value;
                RaiseMany(nameof(Night), nameof(Day));
                ProfileChanged();
            }
        }
        public bool Day { get => !Night; set => Night = !value; }

        public bool Hardcore
        {
            get => Profile.Hardcore;
            set
            {
                Profile.Hardcore = value;
                RaiseMany(nameof(Hardcore), nameof(IsCoopMode), nameof(IsVersusMode), nameof(CurrentModeName));
                RaiseSquad();
                SyncRulesModeToScenario();
                ProfileChanged();
            }
        }

        public int MaxPlayers
        {
            get => Profile.MaxPlayers;
            set { Profile.MaxPlayers = Math.Max(1, Math.Min(64, value)); Raise(); ProfileChanged(); }
        }

        public bool MutatorsEnabled
        {
            get => Profile.MutatorsEnabled;
            set { Profile.MutatorsEnabled = value; Raise(); ProfileChanged(); }
        }

        private void RaiseProfileFields()
        {
            RaiseMany(nameof(Profile), nameof(Night), nameof(Day), nameof(Hardcore), nameof(MaxPlayers), nameof(MutatorsEnabled), nameof(ForceReload),
                      nameof(CustomIniMode), nameof(CustomIniText), nameof(ExtraUrlOptions), nameof(GameModeOverride), nameof(AfterLoadCommands),
                      nameof(EnableCheatsAfterLoad), nameof(LaunchRuleset), nameof(CanHardcore));
            RaiseSquad();
        }

        // ------------------------------------------------------------------ squad & enemies

        private string RuleGet(string cls, string key)
        {
            if (cls == null) return null;
            return Profile.Rules.TryGetValue(cls, out var d) && d.TryGetValue(key, out var v) ? v : null;
        }

        private void RuleSet(string cls, string key, string value)
        {
            if (cls == null) return;
            if (!Profile.Rules.TryGetValue(cls, out var d))
            {
                if (value == null) return;
                Profile.Rules[cls] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (value == null) d.Remove(key); else d[key] = value;
            if (d.Count == 0) Profile.Rules.Remove(cls);
            ProfileChanged();
        }

        private int EffectiveInt(string key)
        {
            var mode = CurrentMode;
            string v = RuleGet(mode?.Cls, key) ?? State.Rules.DefaultValue(mode?.Cls, key);
            return int.TryParse(v, out var i) ? i : 0;
        }

        private int DefaultInt(string key) => int.TryParse(State.Rules.DefaultValue(CurrentMode?.Cls, key), out var i) ? i : 0;

        /// <summary>
        /// Squad settings belong to the kind of play, not one mode: a value set while a co-op scenario is picked
        /// applies to every co-op mode that has the setting, and a versus value to every versus mode.
        /// </summary>
        private IEnumerable<ModeDef> SquadModes(string key)
        {
            var mode = CurrentMode;
            if (mode == null) return Enumerable.Empty<ModeDef>();
            return State.Rules.Modes.Where(m => m.Coop == mode.Coop && m.Defaults.ContainsKey(key));
        }

        private void SetSquadValue(string key, string value, Func<ModeDef, string, string> stored)
        {
            foreach (var m in SquadModes(key)) RuleSet(m.Cls, key, stored(m, value));
            RaiseSquad();
            RefreshRuleItems();
        }

        private void SetInt(string key, int value, int min, int max)
        {
            var mode = CurrentMode;
            if (mode == null || !mode.Defaults.ContainsKey(key)) return;
            value = Math.Max(min, Math.Min(max, value));
            string text = value.ToString(CultureInfo.InvariantCulture);
            SetSquadValue(key, text, (m, v) => State.Rules.DefaultValue(m.Cls, key) == v ? null : v);
        }

        public int Teammates { get => EffectiveInt("FriendlyBotQuota"); set => SetInt("FriendlyBotQuota", value, 0, 32); }
        public int SoloEnemies { get => EffectiveInt("SoloEnemies"); set => SetInt("SoloEnemies", value, 0, 64); }
        public int MinEnemies { get => EffectiveInt("MinimumEnemies"); set => SetInt("MinimumEnemies", value, 0, 64); }
        public int MaxEnemies { get => EffectiveInt("MaximumEnemies"); set => SetInt("MaximumEnemies", value, 0, 64); }
        public int BotQuota { get => EffectiveInt("BotQuota"); set => SetInt("BotQuota", value, 0, 32); }
        public string TeammatesHint => "FriendlyBotQuota · mode default " + DefaultInt("FriendlyBotQuota");
        public string SoloEnemiesHint => "SoloEnemies · mode default " + DefaultInt("SoloEnemies");
        public string MinEnemiesHint => "MinimumEnemies · default " + DefaultInt("MinimumEnemies");
        public string MaxEnemiesHint => "MaximumEnemies · default " + DefaultInt("MaximumEnemies");
        /// <summary>Versus: BotQuota is the size of each team. You take one slot of yours, bots fill the rest and the whole enemy team.</summary>
        public string BotQuotaHint => BotQuota <= 1
            ? "BotQuota · you vs 1 bot · mode default " + DefaultInt("BotQuota")
            : "BotQuota · you + " + (BotQuota - 1) + " AI vs " + BotQuota + " bots · mode default " + DefaultInt("BotQuota");
        public bool TeammatesChanged => RuleGet(CurrentMode?.Cls, "FriendlyBotQuota") != null;
        public bool SoloEnemiesChanged => RuleGet(CurrentMode?.Cls, "SoloEnemies") != null;
        public bool MinEnemiesChanged => RuleGet(CurrentMode?.Cls, "MinimumEnemies") != null;
        public bool MaxEnemiesChanged => RuleGet(CurrentMode?.Cls, "MaximumEnemies") != null;
        public bool BotQuotaChanged => RuleGet(CurrentMode?.Cls, "BotQuota") != null;

        public bool BotsEnabled
        {
            // Versus is played against bots unless the player turns them off (the game's own default is off).
            get
            {
                var mode = CurrentMode;
                string v = RuleGet(mode?.Cls, "bBots");
                if (v != null) return LaunchPlanner.IsTrue(v);
                return mode != null && !mode.Coop ? mode.Defaults.ContainsKey("bBots") : LaunchPlanner.IsTrue(State.Rules.DefaultValue(mode?.Cls, "bBots"));
            }
            set
            {
                var mode = CurrentMode;
                if (mode == null) return;
                SetSquadValue("bBots", value ? "True" : "False", (m, v) => !m.Coop ? (value ? null : "False")
                                                                           : (LaunchPlanner.IsTrue(State.Rules.DefaultValue(m.Cls, "bBots")) == value ? null : v));
            }
        }

        /// <summary>AI difficulty: a game-mode property in co-op, applied through the cheat manager in versus.</summary>
        public double AiDifficulty
        {
            get
            {
                var mode = CurrentMode;
                string v = mode != null && mode.Coop ? (RuleGet(mode.Cls, "AIDifficulty") ?? State.Rules.DefaultValue(mode.Cls, "AIDifficulty")) : RuleGet("*", "AIDifficulty");
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.5;
            }
            set
            {
                var mode = CurrentMode;
                double v = Math.Round(Math.Max(0, Math.Min(1, value)) * 20) / 20;
                string s = v.ToString("0.##", CultureInfo.InvariantCulture);
                // Stored only where it differs from that mode's own default.
                if (mode != null && mode.Coop) { SetSquadValue("AIDifficulty", s, (m, x) => LaunchPlanner.Same(State.Rules.DefaultValue(m.Cls, "AIDifficulty"), x, null) ? null : x); return; }
                RuleSet("*", "AIDifficulty", Math.Abs(v - 0.5) < 0.001 ? null : s);
                RaiseSquad();
                RefreshRuleItems();
            }
        }

        public string AiDifficultyLabel
        {
            get
            {
                double d = AiDifficulty;
                string name = d < 0.2 ? "Recruit" : d < 0.4 ? "Regular" : d < 0.6 ? "Normal" : d < 0.8 ? "Veteran" : d < 0.95 ? "Elite" : "Nightmare";
                return d.ToString("0.00", CultureInfo.InvariantCulture) + " · " + name;
            }
        }

        public string PlayStyle
        {
            get
            {
                var mode = CurrentMode;
                if (mode == null) return "Custom";
                if (!mode.Coop)
                {
                    if (BotsEnabled && (BotQuota == 1 || BotQuota == 5 || BotQuota == 10) && RuleGet(mode.Cls, "BotQuota") != null) return "VS" + BotQuota;
                    bool changed = RuleGet(mode.Cls, "bBots") != null || RuleGet(mode.Cls, "BotQuota") != null;
                    if (!changed) return "Defaults";
                    return "Custom";
                }
                bool any = new[] { "FriendlyBotQuota", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "AIDifficulty" }.Any(k => RuleGet(mode.Cls, k) != null);
                if (!any) return "Defaults";
                if (Teammates == 0) return "LoneWolf";
                if (Teammates > 0 && RuleGet(mode.Cls, "SoloEnemies") == null && RuleGet(mode.Cls, "AIDifficulty") == null) return "Squad";
                return "Custom";
            }
        }

        private void ApplyPlayStyle(string style)
        {
            var mode = CurrentMode;
            if (mode == null) return;
            switch (style)
            {
                case "LoneWolf":
                    if (mode.Coop) SetInt("FriendlyBotQuota", 0, 0, 32);
                    ShowToast("Lone Wolf: no AI teammates. Tune the enemies below.");
                    break;
                case "Squad":
                    if (mode.Coop) SetInt("FriendlyBotQuota", Math.Max(DefaultInt("FriendlyBotQuota"), Teammates == 0 ? DefaultInt("FriendlyBotQuota") : Teammates), 0, 32);
                    else { BotsEnabled = true; if (BotQuota == 0) BotQuota = 10; }
                    break;
                case "VS1":
                case "VS5":
                case "VS10":
                    if (mode.Coop) break;
                    int size = int.Parse(style.Substring(2), CultureInfo.InvariantCulture);
                    BotsEnabled = true;
                    BotQuota = size;
                    ShowToast(size == 1 ? "1 v 1: you against one bot" : size + " v " + size + ": you + " + (size - 1) + " AI against " + size + " bots");
                    break;
                case "Defaults":
                    foreach (var k in new[] { "FriendlyBotQuota", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "AIDifficulty", "bBots", "BotQuota" })
                        foreach (var m in SquadModes(k)) RuleSet(m.Cls, k, null);
                    if (!mode.Coop) RuleSet("*", "AIDifficulty", null);
                    ShowToast(mode.Coop ? "Bot and enemy settings reset to each co-op mode's defaults" : "Bots back to the defaults: versus is played against bots");
                    break;
            }
            RaiseSquad();
            RefreshRuleItems();
        }

        private void Step(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return;
            var parts = arg.Split(':');
            int delta = parts.Length > 1 && int.TryParse(parts[1], out var d) ? d : 1;
            switch (parts[0])
            {
                case "Teammates": Teammates += delta; break;
                case "SoloEnemies": SoloEnemies += delta; break;
                case "MinEnemies": MinEnemies += delta; break;
                case "MaxEnemies": MaxEnemies += delta; break;
                case "BotQuota": BotQuota += delta; break;
                case "MaxPlayers": MaxPlayers += delta; break;
            }
        }

        private void RaiseSquad()
        {
            RaiseMany(nameof(Teammates), nameof(SoloEnemies), nameof(MinEnemies), nameof(MaxEnemies), nameof(BotQuota), nameof(BotsEnabled),
                      nameof(TeammatesHint), nameof(SoloEnemiesHint), nameof(MinEnemiesHint), nameof(MaxEnemiesHint), nameof(BotQuotaHint),
                      nameof(TeammatesChanged), nameof(SoloEnemiesChanged), nameof(MinEnemiesChanged), nameof(MaxEnemiesChanged), nameof(BotQuotaChanged),
                      nameof(AiDifficulty), nameof(AiDifficultyLabel), nameof(PlayStyle), nameof(IsCoopMode), nameof(IsVersusMode), nameof(CurrentModeName),
                      nameof(RuleChangeCount), nameof(RulesTabLabel), nameof(RulesModeTitle));
        }

        private void RandomMission()
        {
            var pool = Maps.Where(m => m.Custom == null && (mapFilter == "All" || m.Source == mapFilter)).ToList();
            if (pool.Count == 0) return;
            string wantCategory = selectedScenario?.Category ?? "Co-op";
            var map = pool[rng.Next(pool.Count)];
            SelectedMap = map;
            var options = Scenarios.Where(s => s.Category == wantCategory).ToList();
            if (options.Count == 0) options = Scenarios.ToList();
            if (options.Count > 0) SelectedScenario = options[rng.Next(options.Count)];
            ShowToast("Random mission: " + map.Name + " · " + (SelectedScenario?.Mode ?? ""));
        }
    }
}
