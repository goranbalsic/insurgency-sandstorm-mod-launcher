using System;
using System.Collections.Generic;
using System.Linq;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    /// <summary>What a preset is for, which decides what applying it replaces (see <see cref="SetupEngine.Apply"/>).</summary>
    public enum PresetKind
    {
        /// <summary>Bots and enemies of one kind of play (co-op or versus): Lone Wolf, 5 v 5, elite bots...</summary>
        Squad,
        /// <summary>Match rules and mutators: rule styles (Realism...), official rulesets, official playlists.</summary>
        Match,
        /// <summary>A setup the player saved: map, scenario, conditions, every rule and the mutators.</summary>
        Saved,
    }

    public sealed class Preset
    {
        public string Name, Description = "", Tag = "", Note = "", Group = "";
        public PresetKind Kind;
        /// <summary>Game-mode class (or "*" for launcher-wide values such as versus AI difficulty) to key to value.</summary>
        public Dictionary<string, Dictionary<string, string>> Rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Mutators the preset sets. Null leaves the current mutators alone.</summary>
        public List<string> Mutators;
        /// <summary>Squad presets: the squad keys they are in charge of (cleared before the preset's values go in).</summary>
        public HashSet<string> Owns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Coop;                 // squad presets: which kind of play
        public List<string> ForModes = new List<string>(); // playlists: the modes they were made for
        public bool Night, HardcoreCheckpoint; // playlists: applied when the picked scenario is one of ForModes
        public RulesPreset Saved;         // Saved presets
        public object Source;
    }

    /// <summary>
    /// Every change to a match setup goes through here, on the profile data only (no UI). The rules:
    ///   Squad preset  - replaces the squad keys it owns in every mode of its kind; nothing else changes.
    ///   Match preset  - takes out every non-squad rule (all modes) and every rule the previous match preset set,
    ///                   then puts in exactly its own rules; its mutators replace the current ones (when it has a list).
    ///   Saved setup   - replaces everything: all rules, the mutators and (full setups) map, scenario and conditions.
    /// Applying presets one after another therefore never piles values up: the result is always the last preset
    /// on top of the squad (or, for a saved setup, exactly the saved setup). --cli torture checks these rules.
    /// </summary>
    public static class SetupEngine
    {
        private const string HardcoreCheckpointCls = "INSCheckpointHardcoreGameMode";

        public static readonly HashSet<string> SquadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "FriendlyBotQuota", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "AIDifficulty", "bBots", "BotQuota" };
        private static readonly string[] CoopSquad = { "FriendlyBotQuota", "SoloEnemies", "MinimumEnemies", "MaximumEnemies", "AIDifficulty" };

        public static bool IsSquadKey(string key) => SquadKeys.Contains(key);

        // ------------------------------------------------------------------ reading the setup

        public static ScenarioInfo Scenario(Profile p, AppState s)
        {
            if (!string.IsNullOrEmpty(p.CustomMapId))
            {
                var c = s.Settings.CustomMaps.FirstOrDefault(x => x.Id == p.CustomMapId);
                if (c != null) return new ScenarioInfo { Id = c.Scenario, Level = c.Level, GameModeClass = c.GameModeClass ?? "", Category = "Custom", Source = ContentSource.Custom };
            }
            return s.AllScenarios.FirstOrDefault(x => x.Id.Equals(p.ScenarioId ?? "", StringComparison.OrdinalIgnoreCase));
        }

        public static ModeDef CurrentMode(Profile p, AppState s) => s.ModeFor(Scenario(p, s), p.Hardcore);

        public static string GetRule(Profile p, string cls, string key) =>
            cls != null && p.Rules.TryGetValue(cls, out var d) && d.TryGetValue(key, out var v) ? v : null;

        /// <summary>The value the match uses: the profile's own, else the launcher's default for the mode.</summary>
        public static string Effective(Profile p, RulesDb db, string cls, string key) => GetRule(p, cls, key) ?? LauncherDefault(db, cls, key);

        /// <summary>
        /// The value used when the profile stores nothing. Same as the game's default, except that versus is played
        /// against bots offline (bBots on; the game's own default is off), so "bots off" has to be stored.
        /// </summary>
        public static string LauncherDefault(RulesDb db, string cls, string key)
        {
            if (cls == null || cls == "*") return null;
            if (key.Equals("bBots", StringComparison.OrdinalIgnoreCase))
            {
                var m = db.Mode(cls);
                if (m != null && !m.Coop && m.Defaults.ContainsKey("bBots")) return "True";
            }
            // Ambush, Defusal and Free For All have no bots by default (0): the launch brings 5 (LaunchPlanner), so the
            // screen shows 5 too.
            if (key.Equals("BotQuota", StringComparison.OrdinalIgnoreCase))
            {
                var m = db.Mode(cls);
                if (m != null && !m.Coop && m.Defaults.ContainsKey("bBots") && int.TryParse(db.DefaultValue(cls, key), out int q) && q <= 0) return "5";
            }
            // Ambush and Free For All wait for 2 players: the launch starts them with 1 (LaunchPlanner), so the screen shows 1,
            // and 2 can be kept to wait for a second player (it showed 2, sent 1, and 2 could not be kept, 2026-10-02 audit).
            if (key.Equals("MinimumPlayers", StringComparison.OrdinalIgnoreCase) || key.Equals("MinimumPlayersInProgress", StringComparison.OrdinalIgnoreCase))
            {
                var m = db.Mode(cls);
                if (m != null && !m.Coop && m.Defaults.ContainsKey("bBots") && int.TryParse(db.DefaultValue(cls, key), out int min) && min > 1) return "1";
            }
            return db.DefaultValue(cls, key);
        }

        /// <summary>
        /// FriendlyBotQuota is the size of your team, you included (like BotQuota in versus): 3 gives you + 2 AI
        /// (owner, 2026-10-01: 2 gave one AI). The screen counts AI teammates; these two convert, nothing else may.
        /// </summary>
        public static int AiTeammates(int friendlyBotQuota) => Math.Max(0, friendlyBotQuota - 1);
        public static int FriendlyBotQuotaFor(int aiTeammates) => aiTeammates <= 0 ? 0 : aiTeammates + 1;

        /// <summary>
        /// Match presets (styles, official rulesets, playlists) never turn versus bots off: the official online rulesets
        /// say bBots=False, which offline would leave an empty match. Bots are set on the Squad tab.
        /// </summary>
        public static bool MatchPresetSets(RulesDb db, string cls, string key, string value)
        {
            if (!key.Equals("bBots", StringComparison.OrdinalIgnoreCase)) return true;
            var m = db.Mode(cls);
            return m == null || m.Coop || LaunchPlanner.IsTrue(value);
        }

        // ------------------------------------------------------------------ changing it

        /// <summary>
        /// A value in the form the game and the travel URL expect, or null when it is not valid for the setting:
        /// numbers in invariant form (clamped to the known range, whole numbers for int settings), bools as True/False,
        /// enum values from the known list, and no spaces or URL characters anywhere (they would break the open command).
        /// </summary>
        public static string NormalizeValue(PropDef prop, string value)
        {
            if (value == null) return null;
            value = value.Trim();
            if (value.Length == 0 || value.IndexOfAny(new[] { ' ', '\t', '?', '&', '=', '"', '\r', '\n', '|', ';' }) >= 0) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (prop == null) return double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var any) && (Math.Abs(any) > 1e9 || double.IsNaN(any)) ? null : value;
            if (prop.IsBool)
            {
                if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return "True";
                if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return "False";
                return null;
            }
            if (prop.IsNumber)
            {
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float, inv, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return null;
                if (prop.Min.HasValue && d < prop.Min.Value) d = prop.Min.Value;
                if (prop.Max.HasValue && d > prop.Max.Value) d = prop.Max.Value;
                if (Math.Abs(d) > 1e9) return null;
                return prop.Type == "int" ? ((long)Math.Round(d)).ToString(inv) : d.ToString("0.#####", inv);
            }
            if (prop.Options.Count > 0) return prop.Options.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase));
            return value;
        }

        /// <summary>Sets one rule. A value equal to the mode's default (or null) removes the rule, so it never counts as a change. Returns false for an invalid value (nothing changes then).</summary>
        public static bool SetRule(Profile p, RulesDb db, string cls, string key, string value)
        {
            if (string.IsNullOrEmpty(cls) || string.IsNullOrEmpty(key) || key.IndexOfAny(new[] { ' ', '?', '=', '&' }) >= 0) return false;
            if (value != null)
            {
                string clean = NormalizeValue(db.Prop(key), value);
                if (clean == null) return false;
                value = clean;
            }
            if (value != null && cls != "*")
            {
                string def = LauncherDefault(db, cls, key);
                if (def != null && LaunchPlanner.Same(def, value, db.Prop(key))) value = null;
            }
            if (cls == "*" && key == "AIDifficulty" && value != null && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && Math.Abs(d - 0.5) < 0.001)
                value = null;
            if (!p.Rules.TryGetValue(cls, out var map))
            {
                if (value == null) return true;
                p.Rules[cls] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (value == null) map.Remove(key); else map[key] = value;
            if (map.Count == 0) p.Rules.Remove(cls);
            return true;
        }

        /// <summary>Removes every rule the filter picks (mode class, key).</summary>
        public static void ClearRules(Profile p, Func<string, string, bool> which)
        {
            foreach (var cls in p.Rules.Keys.ToList())
            {
                var map = p.Rules[cls];
                foreach (var key in map.Keys.ToList()) if (which(cls, key)) map.Remove(key);
                if (map.Count == 0) p.Rules.Remove(cls);
            }
        }

        /// <summary>A mutator ID the travel URL can carry: letters, digits, _ . and -.</summary>
        public static bool IsValidMutatorId(string id) =>
            !string.IsNullOrEmpty(id) && id.Length <= 200 && id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-');

        public static void SetMutators(Profile p, AppState s, IEnumerable<string> ids, List<string> missing = null)
        {
            var list = new List<string>();
            foreach (var id in ids ?? Enumerable.Empty<string>())
            {
                var info = s.FindMutator(id);
                if (info == null) { missing?.Add(id); continue; }
                if (!list.Contains(info.Id, StringComparer.OrdinalIgnoreCase)) list.Add(info.Id);
            }
            p.Mutators = list;
            p.MutatorPreset = null;
        }

        /// <summary>Everything back to the game defaults (map and conditions stay).</summary>
        public static void ResetAll(Profile p)
        {
            p.Rules.Clear();
            p.Mutators = new List<string>();
            p.MutatorPreset = null;
            p.RulesPresetName = null;
            p.PresetKeys = new List<string>();
            p.PresetCheck = null;
            p.PresetChanges = null;
        }

        /// <summary>
        /// What a preset decides, as text: a match preset its rules (bots aside) and mutators, a saved setup everything.
        /// Compared with the stored check to tell whether the setup was changed after the preset.
        /// </summary>
        public static string PresetCheck(Profile p, bool full)
        {
            var sb = new System.Text.StringBuilder(full ? "S|" : "M|");
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var kv in mode.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    if (full || (mode.Key != "*" && !IsSquadKey(kv.Key))) sb.Append(mode.Key).Append('.').Append(kv.Key).Append('=').Append(kv.Value).Append(';');
            sb.Append('|').Append(string.Join(",", p.Mutators));
            if (full) sb.Append('|').Append(p.ScenarioId).Append('|').Append(p.CustomMapId).Append('|').Append(p.Lighting).Append('|').Append(p.Hardcore).Append('|').Append(p.MutatorsEnabled);
            return sb.ToString();
        }

        /// <summary>The preset name for the summary: "Realism", or "Realism (changed)" once the setup differs from what it set.</summary>
        public static string PresetLabel(Profile p)
        {
            if (string.IsNullOrEmpty(p.RulesPresetName)) return "";
            bool full = p.PresetCheck != null && p.PresetCheck.StartsWith("S|");
            return p.PresetCheck == null || PresetCheck(p, full) == p.PresetCheck ? T(p.RulesPresetName) : F("{0} (changed)", T(p.RulesPresetName));
        }

        /// <summary>Remembers the setup as the named preset left it.</summary>
        public static void MarkPreset(Profile p, string name, bool full)
        {
            p.RulesPresetName = name;
            p.PresetCheck = PresetCheck(p, full);
        }

        /// <summary>
        /// Applies a preset (rules in the class comment). Returns a short line for the player, and the mutators it
        /// wanted that are not installed in <paramref name="missing"/>.
        /// </summary>
        public static string Apply(Profile p, AppState s, Preset preset, List<string> missing = null)
        {
            var db = s.Rules;
            missing = missing ?? new List<string>();
            switch (preset.Kind)
            {
                case PresetKind.Squad:
                {
                    var modes = db.Modes.Where(m => m.Coop == preset.Coop).Select(m => m.Cls).ToList();
                    ClearRules(p, (cls, key) => preset.Owns.Contains(key) && (modes.Contains(cls) || (cls == "*" && !preset.Coop)));
                    foreach (var key in preset.Owns)
                        foreach (var cls in preset.Coop ? modes : modes.Concat(new[] { "*" })) OwnSquadValue(p, cls, key);
                    foreach (var mode in preset.Rules) foreach (var kv in mode.Value) SetRule(p, db, mode.Key, kv.Key, kv.Value);
                    return preset.Coop ? F("{0} applied to co-op bots and enemies", T(preset.Name)) : F("{0} applied to versus bots and enemies", T(preset.Name));
                }
                case PresetKind.Match:
                {
                    // First the previous preset comes back out: its rules, its mutators, and the day/night, hardcore and
                    // squad values it changed (each only while it is still as the preset left it).
                    UndoPreset(p, s);
                    ClearRules(p, (cls, key) => cls != "*" && !IsSquadKey(key));
                    var changes = new PresetChanges();
                    var set = new List<string>();
                    foreach (var mode in preset.Rules)
                        foreach (var kv in mode.Value)
                        {
                            if (!MatchPresetSets(db, mode.Key, kv.Key, kv.Value)) continue;
                            string before = GetRule(p, mode.Key, kv.Key);
                            SetRule(p, db, mode.Key, kv.Key, kv.Value);
                            string after = GetRule(p, mode.Key, kv.Key);
                            if (after != null) set.Add(mode.Key + "|" + kv.Key);
                            // Squad values survive a match preset's clean-up, so the preset remembers the value it replaced.
                            if (IsSquadKey(kv.Key) && before != after && !changes.RulesBefore.ContainsKey(mode.Key + "|" + kv.Key))
                            {
                                changes.RulesBefore[mode.Key + "|" + kv.Key] = before;
                                changes.RulesAfter[mode.Key + "|" + kv.Key] = after;
                            }
                        }
                    p.PresetKeys = set;
                    changes.PresetId = PresetId(preset);
                    if (preset.Mutators != null)
                    {
                        // The preset's mutators join the ones picked by hand; the next preset takes out only the preset's.
                        var list = new List<string>(p.Mutators);
                        bool any = false;
                        foreach (var id in preset.Mutators)
                        {
                            var info = s.FindMutator(id);
                            if (info == null) { if (!missing.Contains(id, StringComparer.OrdinalIgnoreCase)) missing.Add(id); continue; }
                            any = true;
                            if (list.Contains(info.Id, StringComparer.OrdinalIgnoreCase)) continue;
                            list.Add(info.Id);
                            changes.AddedMutators.Add(info.Id);
                        }
                        p.Mutators = list;
                        p.MutatorPreset = null;
                        // Its mutators are the point of the preset: with the switch off the match would have none of them.
                        if (any && !p.MutatorsEnabled)
                        {
                            changes.MutatorsOnBefore = false;
                            p.MutatorsEnabled = true;
                            changes.MutatorsOnAfter = true;
                        }
                    }
                    // A Checkpoint playlist says whether it is played hardcore: the hardcore ones turn it on, the others off
                    // (their rules are Checkpoint's). The next preset puts it back.
                    bool hardcoreChanged = false;
                    if (preset.ForModes.Contains("INSCheckpointGameMode", StringComparer.OrdinalIgnoreCase) && Scenario(p, s)?.GameModeClass == "INSCheckpointGameMode"
                        && p.Hardcore != preset.HardcoreCheckpoint)
                    {
                        changes.HardcoreBefore = p.Hardcore;
                        p.Hardcore = preset.HardcoreCheckpoint;
                        changes.HardcoreAfter = p.Hardcore;
                        hardcoreChanged = true;
                    }
                    var current = CurrentMode(p, s);
                    bool fits = current == null || preset.ForModes.Count == 0 || preset.ForModes.Contains(current.Cls, StringComparer.OrdinalIgnoreCase)
                                || (preset.HardcoreCheckpoint && current.Cls == "INSCheckpointHardcoreGameMode");
                    if (fits && preset.Night && p.Lighting != "Night")
                    {
                        changes.LightingBefore = p.Lighting;
                        p.Lighting = "Night";
                        changes.LightingAfter = p.Lighting;
                    }
                    p.PresetChanges = changes;
                    MarkPreset(p, preset.Name, false);
                    string text = preset.Mutators == null ? F("{0} applied", T(preset.Name))
                                 : p.Mutators.Count == 0 ? F("{0} applied with no mutators", T(preset.Name))
                                 : p.Mutators.Count == 1 ? F("{0} applied with 1 mutator", T(preset.Name))
                                 : F("{0} applied with {1} mutators", T(preset.Name), p.Mutators.Count);
                    if (hardcoreChanged) text += ". " + (p.Hardcore ? T("Hardcore on") : T("Hardcore off: this playlist is played as normal Checkpoint"));
                    if (!fits && preset.ForModes.Count > 0)
                        text += ". " + F("Made for {0}: pick one of those on the Map tab for the full effect", string.Join(", ", preset.ForModes.Select(m => T(db.Mode(m)?.Name ?? m)).Distinct()));
                    return text + (missing.Count > 0 ? ". " + F("Not installed: {0}", string.Join(", ", missing)) : "");
                }
                default:
                {
                    var saved = preset.Saved;
                    ResetAll(p);
                    if (saved.Setup != null)
                    {
                        // 1.8.0+: the whole setup, Advanced options included.
                        CopySetup(saved.Setup, p);
                        p.Rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var mode in saved.Setup.Rules ?? new Dictionary<string, Dictionary<string, string>>())
                            foreach (var kv in mode.Value) SetRule(p, db, mode.Key, kv.Key, kv.Value);
                        SetMutators(p, s, saved.Setup.Mutators, missing);
                        if (p.CustomMapId != null && !s.Settings.CustomMaps.Any(c => c.Id == p.CustomMapId)) p.CustomMapId = null;
                        if (p.MaxPlayers < 1 || p.MaxPlayers > 64) p.MaxPlayers = 8;
                        p.Lighting = p.Lighting == "Night" ? "Night" : "Day";
                        // The preset the setup was made with, so the next preset still takes it back out.
                        CopyPresetTracking(saved.Setup, p);
                    }
                    else
                    {
                        // A setup saved before 1.8.0 holds rules and mutators (and the match, when FullSetup): everything else
                        // is as in a new setup, not left over from the setup on screen before (its Advanced options stayed,
                        // and the next Save wrote them into this one, 2026-10-02 audit).
                        var match = (p.MapKey, p.ScenarioId, p.CustomMapId, p.Lighting, p.Hardcore, p.MaxPlayers, p.MutatorsEnabled);
                        CopySetup(new Profile(), p);
                        (p.MapKey, p.ScenarioId, p.CustomMapId, p.Lighting, p.Hardcore, p.MaxPlayers, p.MutatorsEnabled) = match;
                        foreach (var mode in saved.Rules) foreach (var kv in mode.Value) SetRule(p, db, mode.Key, kv.Key, kv.Value);
                        SetMutators(p, s, saved.Mutators, missing);
                        if (saved.FullSetup)
                        {
                            p.MapKey = saved.MapKey;
                            p.ScenarioId = saved.ScenarioId;
                            p.CustomMapId = saved.CustomMapId;
                            p.Lighting = saved.Lighting == "Night" ? "Night" : "Day";
                            p.Hardcore = saved.Hardcore;
                            if (saved.MaxPlayers > 0 && saved.MaxPlayers <= 64) p.MaxPlayers = saved.MaxPlayers;
                            p.MutatorsEnabled = saved.MutatorsEnabled;
                        }
                    }
                    // A map that is not installed any more (a mod removed since): the screen falls back to another one.
                    if (Scenario(p, s) == null && !string.IsNullOrEmpty(p.ScenarioId) && string.IsNullOrEmpty(p.CustomMapId)) missing.Insert(0, p.ScenarioId);
                    AlignMap(p, s);
                    MarkSetup(p, saved.Name);
                    return F("{0} loaded", saved.Name) + (missing.Count > 0 ? ". " + F("Not installed: {0}", string.Join(", ", missing)) : "");
                }
            }
        }

        /// <summary>
        /// The player set a squad value (squad card or squad preset): the last match preset no longer takes it back, even
        /// when the new value happens to be the one that preset left (it put the old value back, 2026-10-02 audit).
        /// </summary>
        public static void SetSquadRule(Profile p, RulesDb db, string cls, string key, string value)
        {
            OwnSquadValue(p, cls, key);
            SetRule(p, db, cls, key, value);
        }

        public static void OwnSquadValue(Profile p, string cls, string key)
        {
            string id = cls + "|" + key;
            p.PresetChanges?.RulesBefore?.Remove(id);
            p.PresetChanges?.RulesAfter?.Remove(id);
            p.PresetKeys?.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Takes the last match preset back out of the setup: the mutators it added, and the day/night, hardcore and
        /// squad values it changed, each only while it is still as the preset left it (the player's own changes stay).
        /// </summary>
        public static void UndoPreset(Profile p, AppState s)
        {
            var c = p.PresetChanges;
            if (c == null)
            {
                // A setup from before 1.8.0: the preset's squad values simply go (as they did then), and so do its mutators
                // (a preset then replaced the whole list with its own, so they are the preset's).
                var old = new HashSet<string>(p.PresetKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (old.Count > 0) ClearRules(p, (cls, key) => IsSquadKey(key) && old.Contains(cls + "|" + key));
                if (!string.IsNullOrEmpty(p.RulesPresetName) && p.PresetCheck != null && p.PresetCheck.StartsWith("M|", StringComparison.Ordinal))
                {
                    var theirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pr in OfficialPresets(s.Rules).Concat(PlaylistPresets(s, true)).Where(x => x.Mutators != null && x.Name == p.RulesPresetName))
                        foreach (var id in pr.Mutators) { theirs.Add(id); var info = s.FindMutator(id); if (info != null) theirs.Add(info.Id); }
                    if (theirs.Count > 0) p.Mutators = p.Mutators.Where(m => !theirs.Contains(m)).ToList();
                }
                p.PresetKeys = new List<string>();
                return;
            }
            foreach (var kv in c.RulesAfter ?? new Dictionary<string, string>())
            {
                int bar = kv.Key.IndexOf('|');
                if (bar <= 0) continue;
                string cls = kv.Key.Substring(0, bar), key = kv.Key.Substring(bar + 1);
                if (GetRule(p, cls, key) != kv.Value) continue;   // changed by hand since
                c.RulesBefore.TryGetValue(kv.Key, out var before);
                SetRule(p, s.Rules, cls, key, before);
            }
            if (c.AddedMutators != null && c.AddedMutators.Count > 0)
                p.Mutators = p.Mutators.Where(m => !c.AddedMutators.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            if (c.MutatorsOnAfter.HasValue && p.MutatorsEnabled == c.MutatorsOnAfter.Value) p.MutatorsEnabled = c.MutatorsOnBefore ?? true;
            if (c.LightingAfter != null && p.Lighting == c.LightingAfter) p.Lighting = c.LightingBefore == "Night" ? "Night" : "Day";
            if (c.HardcoreAfter.HasValue && p.Hardcore == c.HardcoreAfter.Value) p.Hardcore = c.HardcoreBefore ?? false;
            p.PresetChanges = null;
            p.PresetKeys = new List<string>();
        }

        private static readonly HashSet<string> TrackingFields = new HashSet<string>
            { nameof(Profile.Name), nameof(Profile.RulesPresetName), nameof(Profile.PresetKeys), nameof(Profile.PresetCheck),
              nameof(Profile.PresetChanges), nameof(Profile.SetupName), nameof(Profile.SetupCheck), nameof(Profile.MutatorPreset) };

        /// <summary>Every setting of the setup from one profile to another (not its name or what tracks presets and saves).</summary>
        public static void CopySetup(Profile from, Profile to)
        {
            var copy = Json.Deserialize<Profile>(Json.Serialize(from));
            foreach (var prop in typeof(Profile).GetProperties())
                if (prop.CanRead && prop.CanWrite && !TrackingFields.Contains(prop.Name)) prop.SetValue(to, prop.GetValue(copy));
            to.Mutators = to.Mutators ?? new List<string>();
            to.Rules = to.Rules ?? new Dictionary<string, Dictionary<string, string>>();
        }

        /// <summary>
        /// The map of the setup is the map its scenario is on. They can part: a setup saved on a custom map entry keeps the
        /// official map picked before it, and the entry may be deleted since.
        /// </summary>
        public static void AlignMap(Profile p, AppState s)
        {
            if (!string.IsNullOrEmpty(p.CustomMapId))
            {
                if (s.Settings.CustomMaps.Any(c => c.Id == p.CustomMapId)) return;
                p.CustomMapId = null;
            }
            if (string.IsNullOrEmpty(p.ScenarioId)) return;
            var map = s.Maps.FirstOrDefault(m => m.Key.Equals(p.MapKey ?? "", StringComparison.OrdinalIgnoreCase));
            if (map != null && map.Scenarios.Any(x => x.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase))) return;
            var owner = s.Maps.FirstOrDefault(m => m.Scenarios.Any(x => x.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase)));
            if (owner != null) p.MapKey = owner.Key;
        }

        /// <summary>Which preset this is, also when another one has the same name: its list and the game's own id for it.</summary>
        public static string PresetId(Preset pr) =>
            pr.Group + ":" + (pr.Source is PlaylistDef pl ? pl.Key : pr.Source is RulesetDef rs ? rs.Id : pr.Name);

        /// <summary>True for the match preset last applied to the setup (shown as in use in the lists).</summary>
        public static bool InUse(Profile p, Preset pr) =>
            pr.Kind == PresetKind.Match && (p.PresetChanges?.PresetId != null ? p.PresetChanges.PresetId == PresetId(pr)
                                                                                : string.Equals(pr.Name, p.RulesPresetName, StringComparison.Ordinal));

        /// <summary>What remembers the last match preset (its name, the rules it set and what else it changed), as a copy.</summary>
        public static void CopyPresetTracking(Profile from, Profile to)
        {
            to.RulesPresetName = from.RulesPresetName;
            to.PresetCheck = from.PresetCheck;
            to.PresetKeys = new List<string>(from.PresetKeys ?? new List<string>());
            to.PresetChanges = from.PresetChanges == null ? null : Json.Deserialize<PresetChanges>(Json.Serialize(from.PresetChanges));
        }

        /// <summary>The setup as text, without what tracks presets and saves: equal text = the same match.</summary>
        public static string Fingerprint(Profile p)
        {
            var copy = new Profile();
            CopySetup(p, copy);
            copy.Name = "";
            var sb = new System.Text.StringBuilder();
            foreach (var prop in typeof(Profile).GetProperties().Where(x => x.CanRead && !TrackingFields.Contains(x.Name)).OrderBy(x => x.Name))
            {
                object v = prop.GetValue(copy);
                if (v is Dictionary<string, Dictionary<string, string>> rules)
                    v = string.Join(";", rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).SelectMany(m => m.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => m.Key + "." + kv.Key + "=" + kv.Value)));
                else if (v is List<string> list) v = string.Join(",", list);
                sb.Append(prop.Name).Append('=').Append(v).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Remembers that the setup is (now) the saved setup of that name.</summary>
        public static void MarkSetup(Profile p, string name)
        {
            p.SetupName = name;
            p.SetupCheck = Fingerprint(p);
        }

        /// <summary>True when the setup differs from the saved setup it was loaded from or saved as.</summary>
        public static bool SetupChanged(Profile p) => p.SetupName != null && p.SetupCheck != Fingerprint(p);

        /// <summary>The whole setup as a saved setup: map, scenario, conditions, bots, every rule, mutators and the Advanced options.</summary>
        public static RulesPreset Capture(Profile p, string name)
        {
            var setup = new Profile();
            CopySetup(p, setup);
            CopyPresetTracking(p, setup);
            setup.Name = name;
            var r = new RulesPreset
            {
                Name = name, FullSetup = true, MapKey = p.MapKey, ScenarioId = p.ScenarioId, CustomMapId = p.CustomMapId,
                Lighting = p.Lighting, Hardcore = p.Hardcore, MaxPlayers = p.MaxPlayers, MutatorsEnabled = p.MutatorsEnabled,
                Mutators = new List<string>(p.Mutators), Setup = setup,
            };
            foreach (var kv in p.Rules) r.Rules[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            return r;
        }

        // ------------------------------------------------------------------ the presets

        private static Preset Squad(RulesDb db, bool coop, string name, string desc, IEnumerable<string> owns, params (string k, string v)[] kv)
        {
            var p = new Preset { Name = name, Description = desc, Kind = PresetKind.Squad, Coop = coop, Tag = coop ? "Co-op" : "Versus", Group = "Squad" };
            foreach (var k in owns) p.Owns.Add(k);
            foreach (var m in db.Modes.Where(m => m.Coop == coop))
            {
                var values = kv.Where(x => x.k != "*AIDifficulty" && m.Defaults.ContainsKey(x.k)).ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);
                if (values.Count > 0) p.Rules[m.Cls] = values;
            }
            var global = kv.FirstOrDefault(x => x.k == "*AIDifficulty");
            if (global.k != null) p.Rules["*"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AIDifficulty"] = global.v };
            return p;
        }

        /// <summary>Bots-and-enemies presets for co-op or versus. Each owns the keys it is about, so switching between them never piles up.</summary>
        public static List<Preset> SquadPresets(RulesDb db, bool coop)
        {
            var list = new List<Preset>();
            if (coop)
            {
                list.Add(Squad(db, true, N("Lone Wolf"), N("Just you against the insurgency, default enemy numbers."), CoopSquad, ("FriendlyBotQuota", "0")));
                list.Add(Squad(db, true, N("Lone Wolf: Hardened"), N("No teammates, more and sharper enemies."), CoopSquad, ("FriendlyBotQuota", "0"), ("SoloEnemies", "10"), ("AIDifficulty", "0.75")));
                list.Add(Squad(db, true, N("Fireteam"), N("You plus two AI riflemen against a slightly larger force."), CoopSquad, ("FriendlyBotQuota", "3"), ("SoloEnemies", "8")));
                list.Add(Squad(db, true, N("Squad Leader"), N("Lead six AI teammates into heavier resistance."), CoopSquad,
                    ("FriendlyBotQuota", "7"), ("SoloEnemies", "12"), ("MinimumEnemies", "6"), ("MaximumEnemies", "16")));
                list.Add(Squad(db, true, N("Full Platoon"), N("Ten teammates, big enemy waves, a war-sized fight."), CoopSquad,
                    ("FriendlyBotQuota", "11"), ("SoloEnemies", "18"), ("MinimumEnemies", "10"), ("MaximumEnemies", "24"), ("AIDifficulty", "0.6")));
                list.Add(Squad(db, true, N("Relaxed"), N("Fewer, slower-reacting enemies. Good for learning maps."), CoopSquad, ("AIDifficulty", "0.25"), ("SoloEnemies", "4")));
                list.Add(Squad(db, true, N("Mode defaults"), N("Bots and enemies as the game mode has them."), CoopSquad));
            }
            else
            {
                var size = new[] { "bBots", "BotQuota" };
                list.Add(Squad(db, false, N("Duel: 1 v 1"), N("You against a single bot."), size, ("bBots", "True"), ("BotQuota", "1")));
                list.Add(Squad(db, false, N("Small teams: 5 v 5"), N("You and four AI against five bots."), size, ("bBots", "True"), ("BotQuota", "5")));
                list.Add(Squad(db, false, N("Battle: 10 v 10"), N("You and nine AI against ten bots."), size, ("bBots", "True"), ("BotQuota", "10")));
                list.Add(Squad(db, false, N("Big battle: 16 v 16"), N("Full teams: you and fifteen AI against sixteen bots."), size, ("bBots", "True"), ("BotQuota", "16")));
                list.Add(Squad(db, false, N("Relaxed bots"), N("Slower, less accurate bots (team size stays)."), new[] { "AIDifficulty" }, ("*AIDifficulty", "0.25")));
                list.Add(Squad(db, false, N("Elite bots"), N("Fast, accurate bots (team size stays)."), new[] { "AIDifficulty" }, ("*AIDifficulty", "0.9")));
                list.Add(Squad(db, false, N("Mode defaults"), N("Bot teams and difficulty back to normal: versus is still played against bots."), SquadKeys));
            }
            return list;
        }

        private static Preset Match(RulesDb db, string name, string desc, string group, IEnumerable<string> modes, params (string k, string v)[] kv)
        {
            var p = new Preset { Name = name, Description = desc, Kind = PresetKind.Match, Group = group };
            foreach (var cls in modes)
            {
                var m = db.Mode(cls);
                var values = kv.Where(x => m != null && m.Defaults.ContainsKey(x.k)).ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);
                if (values.Count > 0) p.Rules[cls] = values;
            }
            return p;
        }

        /// <summary>Rule styles for every mode (they leave bots and mutators alone).</summary>
        public static List<Preset> StylePresets(RulesDb db)
        {
            var all = db.Modes.Select(m => m.Cls).ToList();
            return new List<Preset>
            {
                Match(db, N("Game defaults"), N("Every match rule back to the game's own values (bots and mutators stay)."), "Style", all),
                Match(db, N("Realism"), N("No death camera, no floating markers, no kill feed, full friendly fire damage."), "Style", all,
                    ("bAllowDeathCamera", "False"), ("FloatingObjectiveVisibility", "HideAll"), ("bKillFeed", "False"), ("bKillerInfo", "False"), ("FriendlyFireModifier", "1")),
                Match(db, N("Sandbox"), N("Practice without pressure: rounds never end, huge supply, long clock."), "Style", all,
                    ("bIgnoreRoundOver", "True"), ("RoundTime", "7200"), ("SoloRoundTime", "7200"), ("InitialSupply", "100"), ("MaximumSupply", "100"), ("SoloWaves", "50")),
                Match(db, N("Quick rounds"), N("Five-minute rounds and a short wait before each one."), "Style", all, ("PreRoundTime", "5"), ("RoundTime", "300")),
                Match(db, N("Long rounds"), N("Thirty-minute rounds for slow, careful play."), "Style", all, ("RoundTime", "1800"), ("SoloRoundTime", "1800")),
            };
        }

        /// <summary>True when rules or mutators would change anything compared with the game defaults.</summary>
        public static bool HasEffect(RulesDb db, Dictionary<string, Dictionary<string, string>> rules, ICollection<string> mutators) =>
            (mutators?.Count ?? 0) > 0 || rules.Any(mode => mode.Value.Any(kv =>
            {
                if (!MatchPresetSets(db, mode.Key, kv.Key, kv.Value)) return false;
                string d = LauncherDefault(db, mode.Key, kv.Key);
                return d == null || !LaunchPlanner.Same(d, kv.Value, db.Prop(kv.Key));
            }));

        private static string Kind(RulesDb db, IEnumerable<string> modes)
        {
            var list = modes.Select(db.Mode).Where(m => m != null).ToList();
            return list.Count == 0 ? "" : list.All(m => m.Coop) ? "Co-op" : list.All(m => !m.Coop) ? "Versus" : "";
        }

        public static List<Preset> OfficialPresets(RulesDb db) =>
            db.Rulesets.Where(r => r.Rules.Count > 0 && HasEffect(db, r.Rules, r.Mutators)).Select(r =>
            {
                var p = new Preset { Name = r.Name, Kind = PresetKind.Match, Group = "Official", Source = r, Tag = Kind(db, r.Rules.Keys),
                                     Mutators = r.Mutators.Count > 0 ? new List<string>(r.Mutators) : null,
                                     Description = r.Id + (r.Notes.Count > 0 ? ". " + string.Join(" ", r.Notes.Select(T)) : "") };
                foreach (var kv in r.Rules) p.Rules[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                return p;
            }).ToList();

        /// <summary>Official playlists that change something (mutators, rules or a ruleset). Map-rotation-only ones are left out.</summary>
        public static List<Preset> PlaylistPresets(AppState s, bool coopFirst)
        {
            var db = s.Rules;
            return db.Playlists.Where(pl => pl.Mutators.Count > 0 || pl.CoopRules.Count > 0 || !string.IsNullOrEmpty(pl.Ruleset) || pl.Lighting == "Night"
                                            || pl.GameAlias == "CheckpointHardcore")
                .OrderBy(pl => pl.IsCoop == coopFirst ? 0 : 1).ThenBy(pl => pl.Title, StringComparer.OrdinalIgnoreCase)
                .Select(pl =>
                {
                    var p = new Preset { Name = pl.Title, Kind = PresetKind.Match, Group = "Playlist", Source = pl, Tag = pl.IsCoop ? "Co-op" : "Versus",
                                         Mutators = new List<string>(pl.Mutators), ForModes = new List<string>(pl.Modes),
                                         Night = pl.Lighting == "Night", HardcoreCheckpoint = pl.GameAlias == "CheckpointHardcore" };
                    void Put(string cls, string key, string value)
                    {
                        if (!p.Rules.TryGetValue(cls, out var map)) p.Rules[cls] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        map[key] = value;
                    }
                    // The ruleset first, then the playlist's own values: those are what makes the playlist (the ruleset's
                    // went over them, e.g. Chad Team666's enemy counts, 2026-10-02 audit).
                    if (!string.IsNullOrEmpty(pl.Ruleset))
                    {
                        var rs = db.Rulesets.FirstOrDefault(r => r.Id == pl.Ruleset);
                        if (rs != null)
                            foreach (var mode in rs.Rules)
                                foreach (var kv in mode.Value) Put(mode.Key, kv.Key, kv.Value);
                    }
                    var targets = pl.Modes.Count > 0 ? pl.Modes : db.Modes.Where(m => m.Coop == pl.IsCoop).Select(m => m.Cls).ToList();
                    foreach (var cls in targets)
                        foreach (var kv in pl.CoopRules) Put(cls, kv.Key, kv.Value);
                    // A hardcore playlist is played as Hardcore Checkpoint: its Checkpoint rules are that mode's too (they were
                    // only on Checkpoint, and the squad card and the launch showed none of them, 2026-10-02 audit).
                    if (p.HardcoreCheckpoint && db.Mode(HardcoreCheckpointCls) != null && p.Rules.TryGetValue("INSCheckpointGameMode", out var checkpointRules))
                        foreach (var kv in checkpointRules.ToList())
                            if (!(p.Rules.TryGetValue(HardcoreCheckpointCls, out var hc) && hc.ContainsKey(kv.Key))) Put(HardcoreCheckpointCls, kv.Key, kv.Value);
                    string modes = string.Join(", ", pl.Modes.Select(m => T(db.Mode(m)?.Name ?? m)).Distinct());
                    string what = pl.Mutators.Count > 0 ? string.Join(", ", pl.Mutators.Select(id => s.FindMutator(id)?.DisplayName ?? id)) : T("rules only");
                    p.Description = (string.IsNullOrWhiteSpace(pl.Description) ? "" : T(pl.Description.Trim()) + " ") + F("Mutators: {0}.", what);
                    p.Note = ((modes.Length > 0 ? F("Made for {0}.", modes) : "") + (pl.Lighting == "Night" ? " " + T("Night.") : "")
                              + (pl.Missing.Count > 0 ? " " + F("Not in the current game: {0}.", string.Join(", ", pl.Missing)) : "")).Trim();
                    return p;
                }).ToList();
        }

        public static List<Preset> SavedPresets(AppState s) =>
            s.Settings.RulesPresets.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new Preset
            {
                Name = r.Name, Kind = PresetKind.Saved, Group = "Saved", Saved = r, Source = r,
                Description = !string.IsNullOrEmpty(r.Description) ? r.Description : Summary(s, r),
            }).ToList();

        public static string Summary(AppState s, RulesPreset r)
        {
            int n = r.Rules.Values.Sum(v => v.Count);
            string where = r.FullSetup ? (s.AllScenarios.FirstOrDefault(x => x.Id == r.ScenarioId)?.Id ?? r.ScenarioId ?? "") + (r.Lighting == "Night" ? ", " + T("night") : "") + (r.Hardcore ? ", " + T("hardcore") : "") + ". " : "";
            return where + (n == 1 ? T("1 rule") : F("{0} rules", n)) + ", " + (r.Mutators.Count == 1 ? T("1 mutator") : F("{0} mutators", r.Mutators.Count));
        }

        /// <summary>
        /// Brings stored rules into the clean form after loading (hand-edited or older files): invalid values
        /// (spaces, URL characters, text in a number setting) are dropped, numbers clamped, defaults removed.
        /// Returns how many values changed.
        /// </summary>
        public static int CleanRules(Dictionary<string, Dictionary<string, string>> rules, RulesDb db)
        {
            if (rules == null) return 0;
            int changed = 0;
            foreach (var cls in rules.Keys.ToList())
            {
                var map = rules[cls];
                // Rules for a mode the game does not have (a hand-edited file, a mode gone in a game update) are never sent.
                if (map == null || (cls != "*" && db.Mode(cls) == null)) { rules.Remove(cls); changed++; continue; }
                foreach (var key in map.Keys.ToList())
                {
                    string clean = string.IsNullOrEmpty(key) || key.IndexOfAny(new[] { ' ', '?', '=', '&' }) >= 0 ? null : NormalizeValue(db.Prop(key), map[key]);
                    if (clean != null && cls != "*")
                    {
                        string def = LauncherDefault(db, cls, key);
                        if (def != null && LaunchPlanner.Same(def, clean, db.Prop(key))) clean = null;
                    }
                    if (cls == "*" && !string.Equals(key, "AIDifficulty", StringComparison.OrdinalIgnoreCase)) clean = null;
                    if (clean != null && cls == "*" && key == "AIDifficulty" && double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ai) && Math.Abs(ai - 0.5) < 0.001)
                        clean = null;
                    if (clean == null) { map.Remove(key); changed++; }
                    else if (clean != map[key]) { map[key] = clean; changed++; }
                }
                if (map.Count == 0) rules.Remove(cls);
            }
            return changed;
        }

        /// <summary>A name no saved setup has yet ("Name", "Name 2", ...).</summary>
        public static string UniqueSetupName(AppState s, string name)
        {
            string baseName = string.IsNullOrWhiteSpace(name) ? "My setup" : name.Trim();
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60).TrimEnd();
            string n = baseName;
            for (int i = 2; s.Settings.RulesPresets.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)); i++) n = baseName + " " + i;
            return n;
        }

        /// <summary>
        /// 1.8.0 has one way to keep a setup: saved setups. Profiles other than the one in use (and every profile with a
        /// name of its own) and mutator presets from older versions become saved setups, so nothing is lost.
        /// Returns how many were converted.
        /// </summary>
        public static int MigrateToSetups(AppState s, bool force = false)
        {
            // Once only: afterwards the setup on screen keeps its old profile name, which must not be converted again.
            if (s.Settings.SetupsMigrated && !force) return 0;
            s.Settings.SetupsMigrated = true;
            var store = s.Store;
            var active = store.Active;
            int converted = 0;
            if (store.Profiles.Count > 1 || s.Settings.MutatorPresets.Count > 0 || !string.Equals(active.Name, "Default", StringComparison.OrdinalIgnoreCase))
            {
                // A copy of the old profile files first, just in case.
                try
                {
                    string dir = System.IO.Path.Combine(AppPaths.DataDir, "backups", "profiles-before-1.8");
                    System.IO.Directory.CreateDirectory(dir);
                    foreach (var f in System.IO.Directory.GetFiles(AppPaths.ProfilesDir, "*.json"))
                        System.IO.File.Copy(f, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f)), true);
                    if (System.IO.File.Exists(AppPaths.SettingsFile)) System.IO.File.Copy(AppPaths.SettingsFile, System.IO.Path.Combine(dir, "settings.json"), true);
                }
                catch (Exception ex) { AppLog.Warn("Profile backup before converting: " + ex.Message); }
            }
            foreach (var p in store.Profiles.ToList())
            {
                bool named = !string.Equals(p.Name, "Default", StringComparison.OrdinalIgnoreCase);
                if (p == active && !named) continue;
                if (named || p != active)
                {
                    string name = UniqueSetupName(s, p.Name);
                    s.Settings.RulesPresets.Add(Capture(p, name));
                    if (p == active) MarkSetup(p, name);
                    converted++;
                    AppLog.Info("Profile " + p.Name + " kept as the saved setup " + name);
                }
                if (p != active) store.DeleteProfile(p);
            }
            foreach (var mp in s.Settings.MutatorPresets.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name)).ToList())
            {
                var setup = active.Clone(active.Name);
                // The mutator preset's own list, not the preset tracking of the setup it was cloned from (a later preset
                // took the player's mutators out as its own, 2026-10-02 audit).
                setup.RulesPresetName = null;
                setup.PresetCheck = null;
                setup.PresetKeys = new List<string>();
                setup.PresetChanges = null;
                setup.MutatorPreset = null;
                setup.Mutators = new List<string>(mp.Mutators ?? new List<string>());
                setup.MutatorsEnabled = true;
                string name = UniqueSetupName(s, mp.Name);
                s.Settings.RulesPresets.Add(Capture(setup, name));
                converted++;
                AppLog.Info("Mutator preset " + mp.Name + " kept as the saved setup " + name);
            }
            if (s.Settings.MutatorPresets.Count > 0) { s.Settings.MutatorPresets.Clear(); converted = Math.Max(converted, 1); }
            if (active.MutatorPreset != null) active.MutatorPreset = null;
            s.Settings.ActiveProfile = active.Name;
            store.SaveProfile(active);
            store.SaveSettings();
            return converted;
        }

        /// <summary>Cleans every profile and saved setup of the store (run once after loading).</summary>
        public static void CleanStored(AppState s)
        {
            foreach (var p in s.Store.Profiles)
            {
                int n = CleanRules(p.Rules, s.Rules);
                var muts = (p.Mutators ?? new List<string>()).Where(IsValidMutatorId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (p.Mutators == null || muts.Count != p.Mutators.Count) { p.Mutators = muts; n++; }
                if (n > 0) { AppLog.Warn("Profile " + p.Name + ": " + n + " stored values were not valid and were fixed"); s.Store.SaveProfile(p); }
            }
            int saved = 0;
            foreach (var r in s.Settings.RulesPresets)
            {
                saved += CleanRules(r.Rules, s.Rules);
                // The whole setup a 1.8.0+ save keeps was never cleaned.
                if (r.Setup != null) saved += CleanRules(r.Setup.Rules, s.Rules);
            }
            saved += s.Settings.CustomMutators.RemoveAll(m => !IsValidMutatorId(m));
            foreach (var mp in s.Settings.MutatorPresets.Where(x => x != null))
            {
                var list = (mp.Mutators ?? new List<string>()).Where(IsValidMutatorId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (mp.Mutators == null || list.Count != mp.Mutators.Count) { mp.Mutators = list; saved++; }
            }
            saved += s.Settings.MutatorPresets.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Name));
            if (saved > 0) { AppLog.Warn("Saved setups: " + saved + " values fixed"); s.Store.SaveSettings(); }
        }

        // ------------------------------------------------------------------ checks (used by --cli torture)

        /// <summary>Things that must never be true of a stored setup.</summary>
        public static List<string> Problems(Profile p, AppState s)
        {
            var db = s.Rules;
            var list = new List<string>();
            foreach (var mode in p.Rules)
            {
                if (mode.Key != "*" && db.Mode(mode.Key) == null) list.Add("rules for an unknown mode " + mode.Key);
                if (mode.Value.Count == 0) list.Add("empty rule section " + mode.Key);
                foreach (var kv in mode.Value)
                {
                    if (kv.Value == null) list.Add("null value " + mode.Key + "." + kv.Key);
                    else if (NormalizeValue(db.Prop(kv.Key), kv.Value) != kv.Value) list.Add("value not in clean form " + mode.Key + "." + kv.Key + "=" + kv.Value);
                    string d = LauncherDefault(db, mode.Key, kv.Key);
                    if (d != null && LaunchPlanner.Same(d, kv.Value, db.Prop(kv.Key))) list.Add("stored value equal to the default " + mode.Key + "." + kv.Key + "=" + kv.Value);
                }
            }
            if (p.Mutators.Count != p.Mutators.Distinct(StringComparer.OrdinalIgnoreCase).Count()) list.Add("duplicate mutators");
            if (p.MaxPlayers < 1 || p.MaxPlayers > 64) list.Add("player slots out of range: " + p.MaxPlayers);
            if (p.Lighting != "Day" && p.Lighting != "Night") list.Add("lighting is " + p.Lighting);
            return list;
        }
    }
}
