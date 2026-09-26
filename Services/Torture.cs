using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// --cli torture [steps] [seed]: random operations on a scratch profile (never saved), each followed by checks of
    /// everything that must always hold: preset semantics (exact values, nothing left from the previous preset,
    /// applying twice = once), stored values (never equal to defaults, never invalid), the launch plan (a clean open
    /// command, enough player slots for the bots) and Game.ini (every rule once, merging twice = once).
    /// </summary>
    public static class Torture
    {
        private static readonly string[] Nasty = { "", " ", "a b", "1 2", "?x=1", "x&y", "=", "\"", "1e99", "-1", "NaN", "abc", "ž", "999999999999", "0.0000001", "True", "false", "1", "0" };

        public static int Run(AppState state, int steps, int seed, Action<string> print)
        {
            var rnd = new Random(seed);
            var db = state.Rules;
            var failures = new List<string>();
            var ops = new Dictionary<string, int>();
            var scenarios = state.AllScenarios.Where(s => state.ModeFor(s, false) != null).ToList();
            var mutatorIds = state.AllMutators.Select(m => m.Id).Concat(new[] { "NotARealMutator", "BeyondMeat" }).ToList();
            var p = new Profile { Name = "torture" };
            Cli.PickScenario(state, p, scenarios[0]);
            string step = "";

            void Fail(string msg)
            {
                failures.Add("step " + step + ": " + msg);
                if (failures.Count <= 60) print("FAIL step " + step + ": " + msg);
            }
            T Pick<T>(IList<T> list) => list[rnd.Next(list.Count)];

            for (int i = 1; i <= steps; i++)
            {
                int op = rnd.Next(14);
                string name = "";
                try
                {
                    var mode = SetupEngine.CurrentMode(p, state);
                    bool coop = mode?.Coop ?? true;
                    switch (op)
                    {
                        case 0:
                        {
                            var s = Pick(scenarios);
                            name = "scenario " + s.Id;
                            Cli.PickScenario(state, p, s);
                            break;
                        }
                        case 1:
                            name = "conditions";
                            p.Lighting = rnd.Next(2) == 0 ? "Day" : "Night";
                            p.Hardcore = rnd.Next(3) == 0 && SetupEngine.Scenario(p, state)?.GameModeClass == "INSCheckpointGameMode";
                            p.MaxPlayers = Math.Max(1, Math.Min(64, rnd.Next(-3, 80)));
                            break;
                        case 2:
                        {
                            var pr = Pick(SetupEngine.SquadPresets(db, coop));
                            name = "squad " + pr.Name;
                            var before = Canon(p.Rules);
                            SetupEngine.Apply(p, state, pr);
                            CheckSquad(p, db, pr, before, Fail);
                            break;
                        }
                        case 3: case 4: case 5:
                        {
                            var list = op == 3 ? SetupEngine.StylePresets(db) : op == 4 ? SetupEngine.OfficialPresets(db) : SetupEngine.PlaylistPresets(state, coop);
                            if (list.Count == 0) break;
                            var pr = Pick(list);
                            name = pr.Group + " " + pr.Name;
                            SetupEngine.Apply(p, state, pr);
                            CheckMatch(p, state, pr, Fail);
                            string once = Canon(p.Rules) + "|" + string.Join(",", p.Mutators);
                            SetupEngine.Apply(p, state, pr);
                            if (Canon(p.Rules) + "|" + string.Join(",", p.Mutators) != once) Fail(name + ": applying it twice gave a different result than once");
                            break;
                        }
                        case 6: case 7:
                        {
                            var m = rnd.Next(4) == 0 ? mode : Pick(db.Modes);
                            if (m == null) break;
                            string key = Pick(m.Defaults.Keys.ToList());
                            var prop = db.Prop(key);
                            string value = RandomValue(rnd, prop, m.Defaults[key]);
                            name = "set " + m.Cls + "." + key + "=" + value;
                            bool ok = SetupEngine.SetRule(p, db, m.Cls, key, value);
                            string clean = SetupEngine.NormalizeValue(prop, value);
                            if (ok != (clean != null)) Fail(name + ": SetRule said " + ok + " but the value is " + (clean == null ? "invalid" : "valid"));
                            if (ok && !LaunchPlanner.Same(SetupEngine.Effective(p, db, m.Cls, key) ?? "", clean, prop)) Fail(name + ": effective value is " + SetupEngine.Effective(p, db, m.Cls, key));
                            break;
                        }
                        case 8:
                        {
                            var stored = p.Rules.SelectMany(kv => kv.Value.Keys.Select(k => (kv.Key, k))).ToList();
                            if (stored.Count == 0) break;
                            var (cls, key) = Pick(stored);
                            name = "unset " + cls + "." + key;
                            SetupEngine.SetRule(p, db, cls, key, null);
                            if (SetupEngine.GetRule(p, cls, key) != null) Fail(name + ": still set");
                            break;
                        }
                        case 9:
                        {
                            var list = new List<string>(p.Mutators);
                            int n = rnd.Next(4);
                            for (int k = 0; k < n; k++) { if (rnd.Next(2) == 0) list.Add(Pick(mutatorIds)); else if (list.Count > 0) list.RemoveAt(rnd.Next(list.Count)); }
                            name = "mutators " + string.Join(",", list);
                            SetupEngine.SetMutators(p, state, list);
                            if (p.Mutators.Any(id => state.FindMutator(id) == null)) Fail(name + ": a mutator that is not installed was kept");
                            break;
                        }
                        case 10:
                        {
                            name = "save, change, load";
                            var saved = SetupEngine.Capture(p, "t" + i);
                            string want = Full(p);
                            SetupEngine.Apply(p, state, Pick(SetupEngine.StylePresets(db)));
                            SetupEngine.Apply(p, state, Pick(SetupEngine.SquadPresets(db, coop)));
                            Cli.PickScenario(state, p, Pick(scenarios));
                            p.Lighting = p.Lighting == "Day" ? "Night" : "Day";
                            SetupEngine.SetMutators(p, state, new[] { Pick(mutatorIds) });
                            SetupEngine.Apply(p, state, new Preset { Name = saved.Name, Kind = PresetKind.Saved, Saved = Json.Deserialize<RulesPreset>(Json.Serialize(saved)) });
                            if (Full(p) != want) Fail(name + ": the loaded setup differs from the saved one\n  want " + want + "\n  got  " + Full(p));
                            break;
                        }
                        case 11:
                            name = "reset";
                            SetupEngine.ResetAll(p);
                            if (p.Rules.Count > 0 || p.Mutators.Count > 0) Fail("reset left rules or mutators");
                            break;
                        case 12:
                        {
                            // Two different match presets in a row: the result must be the second one alone (no leftovers of the first).
                            var list = SetupEngine.StylePresets(db).Concat(SetupEngine.OfficialPresets(db)).Concat(SetupEngine.PlaylistPresets(state, coop)).ToList();
                            var a = Pick(list); var b = Pick(list);
                            name = "match " + a.Name + " then " + b.Name;
                            var copy = Json.Deserialize<Profile>(Json.Serialize(p));
                            SetupEngine.Apply(p, state, a);
                            SetupEngine.Apply(p, state, b);
                            SetupEngine.Apply(copy, state, b);
                            if (NonSquad(p.Rules) != NonSquad(copy.Rules)) Fail(name + ": rules differ from applying only the second\n  " + NonSquad(p.Rules) + "\n  " + NonSquad(copy.Rules));
                            break;
                        }
                        default:
                        {
                            // Squad presets never touch match rules or mutators.
                            var pr = Pick(SetupEngine.SquadPresets(db, coop));
                            name = "squad keeps match rules " + pr.Name;
                            string before = NonSquad(p.Rules) + "|" + string.Join(",", p.Mutators);
                            SetupEngine.Apply(p, state, pr);
                            if (NonSquad(p.Rules) + "|" + string.Join(",", p.Mutators) != before) Fail(name + ": changed match rules or mutators");
                            break;
                        }
                    }
                    step = i + " (" + name + ")";
                    Invariants(p, state, Fail);
                }
                catch (Exception ex)
                {
                    step = i + " (" + name + ")";
                    Fail("EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                }
                string opName = new[] { "scenario", "conditions", "squad preset", "style", "official", "playlist", "set rule", "set rule", "unset rule", "mutators", "save+load", "reset", "match then match", "squad keeps match" }[op];
                ops[opName] = ops.TryGetValue(opName, out var c) ? c + 1 : 1;
            }
            print("Torture: " + steps + " steps (seed " + seed + "): " + string.Join(", ", ops.OrderBy(o => o.Key).Select(o => o.Key + " " + o.Value)));
            print(failures.Count == 0 ? "ALL CHECKS PASSED" : failures.Count + " FAILURES");
            return failures.Count == 0 ? 0 : 1;
        }

        private static string RandomValue(Random rnd, PropDef prop, string def)
        {
            if (rnd.Next(6) == 0) return Nasty[rnd.Next(Nasty.Length)];
            if (prop == null) return def;
            if (prop.IsBool) return rnd.Next(2) == 0 ? "True" : "False";
            if (prop.Options.Count > 0) return prop.Options[rnd.Next(prop.Options.Count)];
            if (prop.IsNumber)
            {
                double lo = prop.Min ?? 0, hi = prop.Max ?? Math.Max(10, (double.TryParse(def, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 10) * 3);
                double v = lo + rnd.NextDouble() * (hi - lo);
                return prop.Type == "int" ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
            }
            return def;
        }

        private static void CheckSquad(Profile p, RulesDb db, Preset pr, string before, Action<string> fail)
        {
            var modes = db.Modes.Where(m => m.Coop == pr.Coop).ToList();
            foreach (var m in modes)
                foreach (var key in pr.Owns.Where(k => m.Defaults.ContainsKey(k)))
                {
                    string want = pr.Rules.TryGetValue(m.Cls, out var map) && map.TryGetValue(key, out var v) ? v : db.DefaultValue(m.Cls, key);
                    string got = SetupEngine.Effective(p, db, m.Cls, key);
                    if (!LaunchPlanner.Same(got ?? "", want ?? "", db.Prop(key))) fail("squad " + pr.Name + ": " + m.Cls + "." + key + " is " + got + ", preset says " + want);
                }
            if (!pr.Coop && pr.Owns.Contains("AIDifficulty"))
            {
                string want = pr.Rules.TryGetValue("*", out var g) && g.TryGetValue("AIDifficulty", out var gv) ? gv : null;
                string got = SetupEngine.GetRule(p, "*", "AIDifficulty");
                if (want != null && got != want) fail("squad " + pr.Name + ": versus AI difficulty is " + got + ", preset says " + want);
                if (want == null && got != null) fail("squad " + pr.Name + ": versus AI difficulty left at " + got);
            }
            // Keys the preset does not own, and the other kind of play, are untouched.
            string after = Canon(Filter(p.Rules, (cls, key) => !(pr.Owns.Contains(key) && (modes.Any(m => m.Cls == cls) || (cls == "*" && !pr.Coop)))));
            string was = Canon(Filter(Parse(before), (cls, key) => !(pr.Owns.Contains(key) && (modes.Any(m => m.Cls == cls) || (cls == "*" && !pr.Coop)))));
            if (after != was) fail("squad " + pr.Name + ": changed keys it does not own\n  was " + was + "\n  now " + after);
        }

        private static void CheckMatch(Profile p, AppState state, Preset pr, Action<string> fail)
        {
            var db = state.Rules;
            foreach (var mode in pr.Rules)
                foreach (var kv in mode.Value)
                {
                    var prop = db.Prop(kv.Key);
                    string want = SetupEngine.NormalizeValue(prop, kv.Value);
                    if (want == null) { fail(pr.Name + ": its own value " + mode.Key + "." + kv.Key + "=" + kv.Value + " is not valid"); continue; }
                    string got = SetupEngine.Effective(p, db, mode.Key, kv.Key);
                    if (!LaunchPlanner.Same(got ?? "", want, prop)) fail(pr.Name + ": " + mode.Key + "." + kv.Key + " is " + got + ", preset says " + want);
                }
            foreach (var mode in p.Rules.Where(m => m.Key != "*"))
                foreach (var key in mode.Value.Keys.Where(k => !SetupEngine.IsSquadKey(k)))
                    if (!(pr.Rules.TryGetValue(mode.Key, out var map) && map.ContainsKey(key))) fail(pr.Name + ": left over " + mode.Key + "." + key + "=" + mode.Value[key]);
            if (pr.Mutators != null)
            {
                var want = pr.Mutators.Select(id => state.FindMutator(id)?.Id).Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (string.Join(",", want) != string.Join(",", p.Mutators)) fail(pr.Name + ": mutators are " + string.Join(",", p.Mutators) + ", preset says " + string.Join(",", want));
            }
        }

        /// <summary>Checks that hold after every step.</summary>
        private static void Invariants(Profile p, AppState state, Action<string> fail)
        {
            var db = state.Rules;
            foreach (var x in SetupEngine.Problems(p, state)) fail("stored setup: " + x);
            foreach (var mode in p.Rules)
                foreach (var kv in mode.Value)
                    if (SetupEngine.NormalizeValue(db.Prop(kv.Key), kv.Value) != kv.Value) fail("stored value not in clean form: " + mode.Key + "." + kv.Key + "=" + kv.Value);

            // The profile survives saving and loading.
            var back = Json.Deserialize<Profile>(Json.Serialize(p));
            if (Full(back) != Full(p)) fail("profile JSON round trip differs");

            var plan = LaunchPlanner.Build(p, state);
            if (plan.Scenario != null)
            {
                if (plan.Error != null) fail("plan error: " + plan.Error);
                string cmd = plan.OpenCommand ?? "";
                if (!cmd.StartsWith("open ")) fail("open command does not start with open: " + cmd);
                string url = cmd.Length > 5 ? cmd.Substring(5) : "";
                if (url.IndexOfAny(new[] { ' ', '\t', '"', '|', ';' }) >= 0) fail("open command has a space or a character the console treats specially: " + cmd);
                if (url.Contains("??") || url.Contains("?=") || url.EndsWith("?") || url.Contains("=?")) fail("malformed travel URL: " + cmd);
                if (!url.Contains("?Scenario=" + p.ScenarioId)) fail("travel URL misses the scenario: " + cmd);
                if (plan.PlayerSlots < 1 || plan.PlayerSlots > 64) fail("player slots " + plan.PlayerSlots);
                var mode = plan.Mode;
                if (mode != null && mode.Coop)
                {
                    int mates = int.TryParse(SetupEngine.Effective(p, db, mode.Cls, "FriendlyBotQuota"), out var mt) ? mt : 0;
                    if (mates > 0 && plan.PlayerSlots < 1 + mates) fail("co-op: " + mates + " AI teammates but only " + plan.PlayerSlots + " slots");
                }
                if (mode != null && !mode.Coop && plan.Overrides.TryGetValue("bBots", out var bb) && LaunchPlanner.IsTrue(bb))
                {
                    int quota = int.TryParse(plan.Overrides.TryGetValue("BotQuota", out var bq) ? bq : db.DefaultValue(mode.Cls, "BotQuota"), out var qq) ? qq : 0;
                    if (quota <= 0) fail("versus with bots but no bot count");
                    if (plan.PlayerSlots < Math.Min(64, 2 * quota + 2)) fail("versus: " + quota + " per team but only " + plan.PlayerSlots + " slots");
                    if (int.TryParse(db.DefaultValue(mode.Cls, "MinimumPlayers"), out var mp) && mp > 1 && !plan.Overrides.ContainsKey("MinimumPlayers") && !(p.Rules.TryGetValue(mode.Cls, out var own) && own.ContainsKey("MinimumPlayers")))
                        fail("versus with bots would wait for " + mp + " players");
                }
                LaunchPlanner.RestartKeyFor(p, db);
            }

            // Game.ini: the block renders and parses back the same; merging it into a messy Game.ini twice gives one result.
            string block = UeIni.Render(plan.IniSections);
            var parsed = UeIni.Parse(block);
            foreach (var s in plan.IniSections.Where(s => s.Values.Count > 0))
            {
                var ps = parsed.FirstOrDefault(x => x.Name == s.Name);
                if (ps == null) { fail("Game.ini section lost when parsed: " + s.Name); continue; }
                foreach (var dup in s.Values.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) fail("duplicate Game.ini key " + s.Name + " " + dup.Key);
            }
            string messy = "[/Script/Engine.Engine]\r\nbSmoothFrameRate=True\r\n\r\n[/Script/Insurgency.INSCheckpointGameMode]\r\nFriendlyBotQuota=0\r\nFriendlyBotQuota=7\r\nSomethingElse=1\r\n; comment\r\n";
            Func<string, string, bool> managed = (sec, key) => LaunchPlanner.IsManagedIniKey(db, sec, key);
            string once = UeIni.MergeSections(messy, plan.IniSections, managed);
            string twice = UeIni.MergeSections(once, plan.IniSections, managed);
            if (once != twice) fail("merging Game.ini twice changed it again");
            if (!once.Contains("bSmoothFrameRate=True") || !once.Contains("SomethingElse=1")) fail("merging dropped lines the launcher does not own");
            foreach (var s in plan.IniSections)
                foreach (var v in s.Values)
                {
                    var sec = UeIni.Parse(once).FirstOrDefault(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                    int count = sec?.Values.Count(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)) ?? 0;
                    if (count != 1) fail("after merging, " + s.Name + " " + v.Key + " appears " + count + " times");
                    else if (sec.Values.First(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)).Value != v.Value) fail("after merging, " + s.Name + " " + v.Key + " has the wrong value");
                }
            if (!plan.IniSections.Any(s => s.Name.EndsWith("INSCheckpointGameMode") && s.Values.Any(v => v.Key == "FriendlyBotQuota")))
            {
                var cp = UeIni.Parse(once).FirstOrDefault(x => x.Name.EndsWith("INSCheckpointGameMode"));
                if (cp != null && cp.Values.Any(v => v.Key == "FriendlyBotQuota")) fail("an old FriendlyBotQuota the launcher owns was left in Game.ini");
            }
        }

        // ------------------------------------------------------------------ canonical text for comparing setups

        private static string Canon(Dictionary<string, Dictionary<string, string>> rules) =>
            string.Join(";", rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                  .Select(m => m.Key + ":" + string.Join(",", m.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value))));

        private static Dictionary<string, Dictionary<string, string>> Parse(string canon)
        {
            var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in canon.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = part.IndexOf(':');
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in part.Substring(c + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) { int e = kv.IndexOf('='); map[kv.Substring(0, e)] = kv.Substring(e + 1); }
                d[part.Substring(0, c)] = map;
            }
            return d;
        }

        private static Dictionary<string, Dictionary<string, string>> Filter(Dictionary<string, Dictionary<string, string>> rules, Func<string, string, bool> keep) =>
            rules.ToDictionary(m => m.Key, m => m.Value.Where(kv => keep(m.Key, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                 .Where(m => m.Value.Count > 0).ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase);

        private static string NonSquad(Dictionary<string, Dictionary<string, string>> rules) => Canon(Filter(rules, (cls, key) => cls != "*" && !SetupEngine.IsSquadKey(key)));

        private static string Full(Profile p) =>
            Canon(p.Rules) + "|" + string.Join(",", p.Mutators) + "|" + p.ScenarioId + "|" + p.MapKey + "|" + p.CustomMapId + "|" + p.Lighting + "|" + p.Hardcore + "|" + p.MaxPlayers + "|" + p.MutatorsEnabled;
    }
}
