using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

            // Travel URL
            var url = new StringBuilder();
            url.Append("open ").Append(plan.Level).Append("?Scenario=").Append(sc.Id);
            url.Append("?MaxPlayers=").Append(Math.Max(1, p.MaxPlayers));
            url.Append("?Lighting=").Append(p.Lighting == "Night" ? "Night" : "Day");
            if (!string.IsNullOrEmpty(plan.GameAlias)) url.Append("?game=").Append(plan.GameAlias);
            if (state.Settings.SoloGameFlag) url.Append("?bSoloGame=1");
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

            // Game.ini block (every mode the profile customises)
            plan.GameIniBlock = BuildGameIniBlock(p, state, sc);
            plan.RestartKey = RestartKeyFor(p, db);

            // After-load commands
            if (state.Settings.ApplyLiveRules && cls != null)
                foreach (var kv in plan.Overrides)
                {
                    if (RestartOnly.Contains(kv.Key)) continue;
                    plan.AfterLoad.Add("AdminSetGamemodeProperty " + kv.Key + " " + kv.Value);
                }
            bool versusDifficulty = plan.Mode != null && !plan.Mode.Coop && p.Rules.TryGetValue("*", out var global) && global.TryGetValue("AIDifficulty", out var vd);
            if (versusDifficulty)
            {
                plan.AfterLoad.Add("EnableCheats");
                plan.AfterLoad.Add("AIDifficulty " + p.Rules["*"]["AIDifficulty"]);
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

        public static string BuildGameIniBlock(Profile p, AppState state, ScenarioInfo played)
        {
            var db = state.Rules;
            var sb = new StringBuilder();
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (mode.Key == "*") continue;
                var def = db.Mode(mode.Key);
                if (def == null) continue;
                var lines = new List<string>();
                foreach (var kv in mode.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string d = db.DefaultValue(mode.Key, kv.Key);
                    if (d != null && Same(d, kv.Value, db.Prop(kv.Key))) continue;
                    lines.Add(kv.Key + "=" + IniValue(kv.Value, db.Prop(kv.Key)));
                }
                if (lines.Count == 0) continue;
                sb.Append("[/Script/Insurgency.").Append(mode.Key).Append("]\r\n");
                foreach (var l in lines) sb.Append(l).Append("\r\n");
                sb.Append("\r\n");
                // Blueprint subclasses (e.g. the Skirmish blueprint) read their own section too.
                foreach (var bpPath in state.AllScenarios.Where(s => s.GameModePath != null && s.Category != "Training"
                                                                     && !s.GameModePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)
                                                                     && s.GameModePath.IndexOf("/Develop", StringComparison.OrdinalIgnoreCase) < 0
                                                                     && db.ResolveMode(s.GameModeClass)?.Cls == mode.Key)
                                                          .Select(s => s.GameModePath).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('[').Append(bpPath).Append("]\r\n");
                    foreach (var l in lines) sb.Append(l).Append("\r\n");
                    sb.Append("\r\n");
                }
            }
            if (string.Equals(p.CustomIniMode, "Append", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.CustomIniText))
                sb.Append("; Custom Game.ini lines from profile \"").Append(p.Name).Append("\"\r\n").Append(p.CustomIniText.Trim()).Append("\r\n");
            return sb.ToString().TrimEnd('\r', '\n');
        }

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
