using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    public sealed class LaunchPlan
    {
        public Profile Profile;
        public ScenarioInfo Scenario;
        public MapInfo Map;
        public ModeDef Mode;
        public bool Hardcore;
        public string Level;
        public string GameAlias;
        public List<string> Mutators = new List<string>();
        public List<string> MissingMutators = new List<string>();
        public List<MutatorInfo> MutatorInfos = new List<MutatorInfo>();
        public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string OpenCommand;
        public string GameIniBlock = "";
        public List<UeIni.Section> IniSections = new List<UeIni.Section>();
        public int PlayerSlots;
        public string RestartKey = "";
        public List<string> AfterLoad = new List<string>();
        public List<string> Warnings = new List<string>();
        public string Error;

        public bool IsValid => Error == null && Scenario != null;

        public string Title => Scenario == null ? "No scenario selected"
            : $"{Map?.DisplayName ?? Scenario.MapKey} · {ModeTitle}{(string.IsNullOrEmpty(Scenario.Side) ? "" : " (" + Scenario.Side + ")")} · {Profile.Lighting}";

        public string ModeTitle => Hardcore ? "Hardcore Checkpoint" : Scenario?.GameModeName ?? "";
    }

    /// <summary>Builds the travel URL, the Game.ini block and the live commands for a profile.</summary>
    public static class LaunchPlanner
    {
        /// <summary>Options the multiplayer mode reads from the travel URL on every map load.</summary>
        public static readonly HashSet<string> UrlOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "RoundTime", "RoundLimit", "WinLimit", "InitialSupply", "MinimumPlayers", "MinimumPlayersInProgress", "SwitchTeamsEveryRound",
          "AutobalanceRoundEndThreshold", "BotQuota", "bBots", "bKillFeed", "bKillFeedSpectator", "bAllowThirdPersonSpectate", "bAllowDeathCamera" };

        /// <summary>Settings the game only reads when bots are first added, so they need a fresh game start.</summary>
        public static readonly HashSet<string> RestartOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FriendlyBotQuota" };

        public static LaunchPlan Build(Profile p, AppState state)
        {
            var plan = new LaunchPlan { Profile = p };
            var db = state.Rules;
            ScenarioInfo sc = null;
            if (!string.IsNullOrEmpty(p.CustomMapId))
            {
                var custom = state.Settings.CustomMaps.FirstOrDefault(c => c.Id == p.CustomMapId);
                if (custom != null)
                {
                    sc = new ScenarioInfo
                    {
                        Id = custom.Scenario, Level = custom.Level, MapKey = custom.Level, GameModeClass = custom.GameModeClass ?? "",
                        GameModeName = string.IsNullOrEmpty(custom.GameModeClass) ? "Custom" : GameCatalog.ModeName(custom.GameModeClass),
                        Category = GameCatalog.IsCoopClass(custom.GameModeClass) ? "Co-op" : "Versus", IsCoop = GameCatalog.IsCoopClass(custom.GameModeClass),
                        Source = ContentSource.Custom
                    };
                    plan.Map = new MapInfo { Key = custom.Level, DisplayName = custom.Label, Level = custom.Level, Source = ContentSource.Custom };
                }
            }
            if (sc == null)
            {
                plan.Map = state.Maps.FirstOrDefault(m => m.Key.Equals(p.MapKey ?? "", StringComparison.OrdinalIgnoreCase));
                sc = plan.Map?.Scenarios.FirstOrDefault(s => s.Id.Equals(p.ScenarioId ?? "", StringComparison.OrdinalIgnoreCase));
                if (sc == null && !string.IsNullOrEmpty(p.ScenarioId))
                    foreach (var m in state.Maps)
                    {
                        sc = m.Scenarios.FirstOrDefault(s => s.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase));
                        if (sc != null) { plan.Map = m; break; }
                    }
            }
            if (sc == null) { plan.Error = "Pick a map and scenario first."; return plan; }
            plan.Scenario = sc;
            plan.Hardcore = p.Hardcore && sc.GameModeClass == "INSCheckpointGameMode";
            plan.Mode = db.ResolveMode(sc.GameModeClass, plan.Hardcore);
            plan.GameAlias = !string.IsNullOrWhiteSpace(p.GameModeOverride) ? p.GameModeOverride.Trim() : plan.Hardcore ? "CheckpointHardcore" : null;
            plan.Level = sc.Level.StartsWith("/Game/Maps/", StringComparison.OrdinalIgnoreCase)
                ? sc.Level.Substring(sc.Level.LastIndexOf('/') + 1) : sc.Level;

            // Mutators
            if (p.MutatorsEnabled)
                foreach (var id in p.Mutators)
                {
                    var info = state.FindMutator(id);
                    if (info == null && !state.Settings.CustomMutators.Contains(id, StringComparer.OrdinalIgnoreCase)) plan.MissingMutators.Add(id);
                    if (info != null) plan.MutatorInfos.Add(info);
                    plan.Mutators.Add(info?.Id ?? id);
                }
            if (plan.MissingMutators.Count > 0)
                plan.Warnings.Add("Not installed: " + string.Join(", ", plan.MissingMutators) + ". Subscribe in the game's mod browser or remove them.");
            foreach (var mi in plan.MutatorInfos.Where(m => m.IsBaseClass))
                plan.Warnings.Add(mi.DisplayName + " is a base class used by other mutators; it usually does nothing on its own.");

            // Rules for the played mode
            string cls = plan.Mode?.Cls;
            if (cls != null && p.Rules.TryGetValue(cls, out var ov))
                foreach (var kv in ov)
                {
                    string def = db.DefaultValue(cls, kv.Key);
                    if (def == null || !Same(def, kv.Value, db.Prop(kv.Key))) plan.Overrides[kv.Key] = kv.Value;
                }

            // Versus is played against bots unless the profile turns them off (the game's own default is off).
            if (plan.Mode != null && !plan.Mode.Coop && plan.Mode.Defaults.ContainsKey("bBots")
                && !(p.Rules.TryGetValue(cls, out var own) && own.ContainsKey("bBots")))
                plan.Overrides["bBots"] = "True";

            // Ambush and Free For All wait for two human players (MinimumPlayers=2) before the match starts, and bots
            // only join once it has started, so offline they sat at "waiting for players" with no bots. With bots on,
            // one player is enough (unless the profile sets these itself).
            bool versusBots = plan.Mode != null && !plan.Mode.Coop && plan.Overrides.TryGetValue("bBots", out var vb) && IsTrue(vb);
            // Ambush, Defusal and Free For All also default to BotQuota=0: bots "on" but none would come.
            if (versusBots && !plan.Overrides.ContainsKey("BotQuota") && int.TryParse(db.DefaultValue(cls, "BotQuota"), out int dq) && dq <= 0)
                plan.Overrides["BotQuota"] = "5";
            // Bots on but a team size of 0 (set by hand) would also leave the match empty: one per team at least.
            if (versusBots && plan.Overrides.TryGetValue("BotQuota", out var setQuota) && int.TryParse(setQuota, out int sq) && sq <= 0)
            {
                plan.Overrides["BotQuota"] = "1";
                plan.Warnings.Add("Players per team was 0 with bots on; 1 is used (you against one bot).");
            }
            if (versusBots)
                foreach (var key in new[] { "MinimumPlayers", "MinimumPlayersInProgress" })
                    if (int.TryParse(db.DefaultValue(cls, key), out int min) && min > 1 && !(p.Rules.TryGetValue(cls, out var own3) && own3.ContainsKey(key)))
                        plan.Overrides[key] = "1";

            // Co-op AI teammates only join when the mode fills teams with bots (bBots is off by default).
            if (plan.Mode != null && plan.Mode.Coop && plan.Mode.Defaults.ContainsKey("bBots")
                && int.TryParse(plan.Overrides.TryGetValue("FriendlyBotQuota", out var fbq) ? fbq : db.DefaultValue(cls, "FriendlyBotQuota"), out int fbn) && fbn > 0
                && !(p.Rules.TryGetValue(cls, out var own2) && own2.ContainsKey("bBots")))
                plan.Overrides["bBots"] = "True";

            // Travel URL
            var url = new StringBuilder();
            url.Append("open ").Append(plan.Level).Append("?Scenario=").Append(sc.Id);
            // AI teammates take player slots, so there must be room for you plus all of them.
            plan.PlayerSlots = Math.Max(1, p.MaxPlayers);
            bool wantsMates = false;
            if (plan.Mode != null && plan.Mode.Coop)
            {
                string fb = plan.Overrides.TryGetValue("FriendlyBotQuota", out var ov2) ? ov2 : db.DefaultValue(cls, "FriendlyBotQuota");
                wantsMates = int.TryParse(fb, out int mates) && mates > 0;
                // Offline nobody else joins, so plenty of slots costs nothing; too few and the AI teammates do not join.
                if (wantsMates) plan.PlayerSlots = Math.Max(plan.PlayerSlots, Math.Max(8, 1 + mates + 2));
            }
            else if (versusBots)
            {
                // Versus bots take player slots as well (BotQuota per team, or all the bots in Free For All): a profile
                // left at 1 slot from Lone Wolf co-op meant no bot could join.
                string bq = plan.Overrides.TryGetValue("BotQuota", out var bqo) ? bqo : db.DefaultValue(cls, "BotQuota");
                int quota = int.TryParse(bq, out int q) && q > 0 ? q : 5;
                plan.PlayerSlots = Math.Max(plan.PlayerSlots, Math.Min(64, 2 * quota + 2));
            }
            url.Append("?MaxPlayers=").Append(plan.PlayerSlots);
            url.Append("?Lighting=").Append(p.Lighting == "Night" ? "Night" : "Day");
            if (!string.IsNullOrEmpty(plan.GameAlias)) url.Append("?game=").Append(plan.GameAlias);
            // bSoloGame stops AI teammates from joining (seen in the game), so it is left out when co-op has teammates.
            if (state.Settings.SoloGameFlag && !wantsMates) url.Append("?bSoloGame=1");
            foreach (var kv in plan.Overrides)
            {
                if (!UrlOptions.Contains(kv.Key)) continue;
                var prop = db.Prop(kv.Key);
                if (prop != null && prop.IsBool)
                {
                    if (IsTrue(kv.Value)) url.Append('?').Append(kv.Key).Append("=1");
                }
                else url.Append('?').Append(kv.Key).Append('=').Append(kv.Value);
            }
            if (plan.Mutators.Count > 0) url.Append("?Mutators=").Append(string.Join(",", plan.Mutators));
            string extra = (p.ExtraUrlOptions ?? "").Trim();
            if (extra.Length > 0) url.Append(extra.StartsWith("?") ? extra : "?" + extra);
            plan.OpenCommand = url.ToString();

            // Game.ini sections (every mode the profile customises)
            plan.IniSections = BuildIniSections(p, state);
            plan.GameIniBlock = UeIni.Render(plan.IniSections);
            plan.RestartKey = RestartKeyFor(p, db);

            // After-load commands
            if (state.Settings.ApplyLiveRules && cls != null)
                foreach (var kv in plan.Overrides)
                {
                    // FriendlyBotQuota is also read at game start from Game.ini; sending it again does no harm.
                    plan.AfterLoad.Add("AdminSetGamemodeProperty " + kv.Key + " " + kv.Value);
                }
            if (plan.Mode != null && !plan.Mode.Coop && p.Rules.TryGetValue("*", out var global) && global.TryGetValue("AIDifficulty", out var versusDifficulty))
            {
                plan.AfterLoad.Add("EnableCheats");
                plan.AfterLoad.Add("AIDifficulty " + versusDifficulty);
            }
            else if (p.EnableCheatsAfterLoad) plan.AfterLoad.Add("EnableCheats");
            foreach (var line in (p.AfterLoadCommands ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith(";")) plan.AfterLoad.Add(t);
            }
            if (plan.Overrides.Count > 0 && cls == null)
                plan.Warnings.Add("This scenario's game mode is not in the rules database, so rule changes are skipped.");
            return plan;
        }

        /// <summary>Game.ini sections for every mode the profile customises, plus the profile's own extra lines.</summary>
        public static List<UeIni.Section> BuildIniSections(Profile p, AppState state)
        {
            var db = state.Rules;
            var sections = new List<UeIni.Section>();
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (mode.Key == "*") continue;
                if (db.Mode(mode.Key) == null) continue;
                var values = new List<KeyValuePair<string, string>>();
                foreach (var kv in mode.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string d = db.DefaultValue(mode.Key, kv.Key);
                    if (d != null && Same(d, kv.Value, db.Prop(kv.Key))) continue;
                    values.Add(new KeyValuePair<string, string>(kv.Key, IniValue(kv.Value, db.Prop(kv.Key))));
                }
                if (values.Count == 0) continue;
                sections.Add(new UeIni.Section("/Script/Insurgency." + mode.Key) { Values = values });
                // Blueprint subclasses (e.g. the Skirmish blueprint) read their own section too.
                foreach (var bpPath in ModeBlueprints(state, mode.Key))
                    sections.Add(new UeIni.Section(bpPath) { Values = new List<KeyValuePair<string, string>>(values) });
            }
            if (!string.Equals(p.CustomIniMode ?? "Off", "Off", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.CustomIniText))
                foreach (var s in UeIni.Parse(p.CustomIniText))
                {
                    var existing = sections.FirstOrDefault(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null) sections.Add(s);
                    else foreach (var v in s.Values) { existing.Values.RemoveAll(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)); existing.Values.Add(v); }
                }
            return sections;
        }

        public static IEnumerable<string> ModeBlueprints(AppState state, string modeCls) =>
            state.AllScenarios.Where(s => s.GameModePath != null && s.Category != "Training"
                                          && !s.GameModePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)
                                          && s.GameModePath.IndexOf("/Develop", StringComparison.OrdinalIgnoreCase) < 0
                                          && state.Rules.ResolveMode(s.GameModeClass)?.Cls == modeCls)
                              .Select(s => s.GameModePath).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Keys the launcher owns in Game.ini: every match rule it knows, in any game mode section. Old copies of
        /// them (from earlier launches, or left behind when the game rewrote the file) are removed before writing.
        /// </summary>
        public static bool IsManagedIniKey(RulesDb db, string section, string key) =>
            section.IndexOf("GameMode", StringComparison.OrdinalIgnoreCase) >= 0 && db.Prop(key) != null;

        /// <summary>Fingerprint of everything that only takes effect when the game starts.</summary>
        public static string RestartKeyFor(Profile p, RulesDb db)
        {
            var sb = new StringBuilder();
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var kv in mode.Value.Where(k => RestartOnly.Contains(k.Key)).OrderBy(k => k.Key))
                {
                    string d = db.DefaultValue(mode.Key, kv.Key);
                    if (d != null && Same(d, kv.Value, db.Prop(kv.Key))) continue;
                    sb.Append(mode.Key).Append('.').Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                }
            sb.Append("ruleset=").Append(p.LaunchRuleset ?? "");
            return Hash(sb.ToString());
        }

        public static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").Substring(0, 16);
        }

        public static bool IsTrue(string v) => v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

        public static bool Same(string a, string b, PropDef prop)
        {
            if (a == null || b == null) return a == b;
            if (prop != null && prop.IsBool) return IsTrue(a) == IsTrue(b);
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return Math.Abs(x - y) < 1e-6;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string IniValue(string v, PropDef prop)
        {
            if (prop != null && prop.IsBool) return IsTrue(v) ? "True" : "False";
            return v;
        }
    }
}
