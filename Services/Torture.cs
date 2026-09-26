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
                int op = rnd.Next(18);
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
                        case 14:
                        {
                            // The versus "Fill teams with bots" switch (the Squad tab stores it for every versus mode).
                            bool on = rnd.Next(2) == 0;
                            name = "versus bots " + (on ? "on" : "off");
                            var versus = db.Modes.Where(x => !x.Coop && x.Defaults.ContainsKey("bBots")).ToList();
                            foreach (var m in versus) SetupEngine.SetRule(p, db, m.Cls, "bBots", on ? "True" : "False");
                            foreach (var m in versus)
                                if (LaunchPlanner.IsTrue(SetupEngine.Effective(p, db, m.Cls, "bBots")) != on) Fail(name + ": " + m.Cls + " bots are " + SetupEngine.Effective(p, db, m.Cls, "bBots"));
                            break;
                        }
                        case 15:
                            name = "mutators switch";
                            p.MutatorsEnabled = !p.MutatorsEnabled;
                            break;
                        case 16:
                        {
                            // Advanced tab: extra URL options, mode override, extra Game.ini lines, after-load commands.
                            string[] urls = { "", "", "?bFoo=1", "RoundTime=99", "a b?c", "??x=1??", " ?Game=Foo ", "x\"y|z;w", "\t?Tab=1" };
                            string[] modes = { "", "", "", "Checkpoint", "Push Hardcore", "?x=1", "  " };
                            p.ExtraUrlOptions = Pick(urls);
                            p.GameModeOverride = Pick(modes);
                            p.AfterLoadCommands = Pick(new[] { "", "slomo 1", "// comment\nstat fps\n;x\n\n", "ghost" });
                            p.EnableCheatsAfterLoad = rnd.Next(2) == 0;
                            p.CustomIniMode = Pick(new[] { "Off", "Append", "Replace" });
                            var m = Pick(db.Modes);
                            string key = Pick(m.Defaults.Keys.ToList());
                            p.CustomIniText = Pick(new[] { "", "[/Script/Insurgency." + m.Cls + "]\n" + key + "=" + RandomValue(rnd, db.Prop(key), m.Defaults[key]) + "\n; note\n",
                                                           "garbage without a section\n=\n[", "[/Script/Foo.Bar]\nX=1\n+Arr=a\n+Arr=b\n", "[]\n=1\n" });
                            name = "advanced url=" + p.ExtraUrlOptions + " mode=" + p.GameModeOverride + " ini=" + p.CustomIniMode;
                            break;
                        }
                        case 17:
                        {
                            // Profile names: every profile gets its own file, whatever the player types.
                            name = "profile names";
                            var store = new Store();
                            string[] names = { "a/b", "a?b", "a:b", "CON", "con.json", "nul", "x.", "x", "X", " x ", "COM1", "", "   ", new string('n', 300), "Lone Wolf", "lone wolf", "ž č ć", "a|b", "a*b" };
                            for (int k = 0; k < 12; k++)
                            {
                                string n = store.UniqueName(Pick(names));
                                store.Profiles.Add(new Profile { Name = n });
                            }
                            var files = store.Profiles.Select(x => Store.ProfileFileName(x.Name)).ToList();
                            if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) Fail(name + ": two profiles share a file: " + string.Join(" | ", files));
                            foreach (var fn in files)
                                if (fn.Length == 0 || fn.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || fn.EndsWith(".") || fn.EndsWith(" ") || fn.Length > 80) Fail(name + ": bad file name " + fn);
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
                string opName = new[] { "scenario", "conditions", "squad preset", "style", "official", "playlist", "set rule", "set rule", "unset rule", "mutators", "save+load", "reset", "match then match", "squad keeps match", "versus bots", "mutators switch", "advanced", "profile names" }[op];
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
                    string want = pr.Rules.TryGetValue(m.Cls, out var map) && map.TryGetValue(key, out var v) ? v : SetupEngine.LauncherDefault(db, m.Cls, key);
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
                    if (!SetupEngine.MatchPresetSets(db, mode.Key, kv.Key, kv.Value)) continue;   // never turns versus bots off
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
                Honoured(p, state, plan, fail);
            }

            // Game.ini: the block renders and parses back the same; merging it into a messy Game.ini twice gives one result.
            string block = UeIni.Render(plan.IniSections);
            var parsed = UeIni.Parse(block);
            foreach (var s in plan.IniSections.Where(s => s.Values.Count > 0))
            {
                var ps = parsed.FirstOrDefault(x => x.Name == s.Name);
                if (ps == null) { fail("Game.ini section lost when parsed: " + s.Name); continue; }
                foreach (var dup in s.Values.Where(v => !v.Key.StartsWith("+") && !v.Key.StartsWith(".")).GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) fail("duplicate Game.ini key " + s.Name + " " + dup.Key);
            }
            string messy = "[/Script/Engine.Engine]\r\nbSmoothFrameRate=True\r\n\r\n[/Script/Insurgency.INSCheckpointGameMode]\r\nFriendlyBotQuota=0\r\nFriendlyBotQuota=7\r\nSomethingElse=1\r\n; comment\r\n";
            Func<string, string, bool> managed = (sec, key) => LaunchPlanner.IsManagedIniKey(db, sec, key);
            string once = UeIni.MergeSections(messy, plan.IniSections, managed);
            string twice = UeIni.MergeSections(once, plan.IniSections, managed);
            if (once != twice) fail("merging Game.ini twice changed it again");
            if (!once.Contains("bSmoothFrameRate=True") || !once.Contains("SomethingElse=1")) fail("merging dropped lines the launcher does not own");
            foreach (var s in plan.IniSections)
                foreach (var v in s.Values.Where(x => !x.Key.StartsWith("+") && !x.Key.StartsWith(".")))
                {
                    var sec = UeIni.Parse(once).FirstOrDefault(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                    int count = sec?.Values.Count(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)) ?? 0;
                    if (count != 1) fail("after merging, " + s.Name + " " + v.Key + " appears " + count + " times");
                    else if (sec.Values.First(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)).Value != v.Value) fail("after merging, " + s.Name + " " + v.Key + " has the wrong value");
                }
            // The launcher's RCON section: exactly once, with the launcher's values, in every mode (Replace too), nothing else lost.
            string launch = LaunchPlanner.GameIniForLaunch(messy, plan, db, null, state.Settings);
            string again = LaunchPlanner.GameIniForLaunch(launch, plan, db, null, state.Settings);
            if (launch != again) fail("writing Game.ini for a launch twice changed it again");
            var rconSecs = UeIni.Parse(launch).Where(x => x.Name.Equals(RconSetup.Section, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rconSecs.Count != 1) fail("Game.ini has " + rconSecs.Count + " [Rcon] sections");
            else
            {
                foreach (var v in RconSetup.IniSection(state.Settings).Values)
                    if (rconSecs[0].Values.Count(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)) != 1 || rconSecs[0].Values.First(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase)).Value != v.Value)
                        fail("[Rcon] " + v.Key + " is not the launcher's value");
                if (rconSecs[0].Values.Any(x => x.Key == "ListenAddressOverride" && x.Value != "127.0.0.1") || rconSecs[0].Values.Any(x => x.Key == "bUseBroadcastAddress" && x.Value != "False"))
                    fail("RCON would listen beyond this PC");
            }
            if (!string.Equals(p.CustomIniMode ?? "Off", "Replace", StringComparison.OrdinalIgnoreCase) && (!launch.Contains("bSmoothFrameRate=True") || !launch.Contains("SomethingElse=1")))
                fail("the RCON section cost lines the launcher does not own");

            // Next launch with the extra lines switched off: none of the player's keys may stay behind.
            var mine = LaunchPlanner.PlayerIniKeys(plan, db);
            var without = Json.Deserialize<Profile>(Json.Serialize(p));
            without.CustomIniMode = "Off";
            var plan2 = LaunchPlanner.Build(without, state);
            string launch1 = LaunchPlanner.MergeGameIni(messy, plan, db, null);
            string launch2 = LaunchPlanner.MergeGameIni(launch1, plan2, db, mine);
            var still = new HashSet<string>(plan2.IniSections.SelectMany(s => s.Values.Select(v => s.Name + "\n" + UeIni.KeyOf(v.Key + "="))), StringComparer.OrdinalIgnoreCase);
            foreach (var sk in mine.Where(k => !still.Contains(k)))
            {
                int nl = sk.IndexOf('\n');
                var sec2 = UeIni.Parse(launch2).FirstOrDefault(x => x.Name.Equals(sk.Substring(0, nl), StringComparison.OrdinalIgnoreCase));
                if (sec2 != null && sec2.Values.Any(v => UeIni.KeyOf(v.Key + "=").Equals(sk.Substring(nl + 1), StringComparison.OrdinalIgnoreCase)))
                    fail("the player's own Game.ini line " + sk.Replace("\n", " ") + " stayed after it was removed");
            }
            if (!plan.IniSections.Any(s => s.Name.EndsWith("INSCheckpointGameMode") && s.Values.Any(v => v.Key == "FriendlyBotQuota")))
            {
                var cp = UeIni.Parse(once).FirstOrDefault(x => x.Name.EndsWith("INSCheckpointGameMode"));
                if (cp != null && cp.Values.Any(v => v.Key == "FriendlyBotQuota")) fail("an old FriendlyBotQuota the launcher owns was left in Game.ini");
            }
        }

        /// <summary>
        /// What the player set is what the game gets: every setting of the played mode reaches the game with the value the
        /// launcher shows (travel URL, Game.ini or after-load commands), apart from the documented adjustments that keep
        /// an offline match playable.
        /// </summary>
        private static void Honoured(Profile p, AppState state, LaunchPlan plan, Action<string> fail)
        {
            var db = state.Rules;
            var mode = plan.Mode;
            if (mode == null) return;
            string url = plan.OpenCommand ?? "";
            bool versusBots = !mode.Coop && LaunchPlanner.IsTrue(SetupEngine.Effective(p, db, mode.Cls, "bBots"));
            var custom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(p.CustomIniMode ?? "Off", "Off", StringComparison.OrdinalIgnoreCase))
                foreach (var sct in UeIni.Parse(p.CustomIniText ?? "")) foreach (var v in sct.Values) custom.Add(v.Key);
            // The player's own extra URL options replace the launcher's value for their keys.
            foreach (var k in plan.ExtraOptionKeys) custom.Add(k);
            foreach (var key in mode.Defaults.Keys)
            {
                if (custom.Contains(key)) continue;
                string want = SetupEngine.Effective(p, db, mode.Cls, key);
                string got = plan.Overrides.TryGetValue(key, out var o) ? o : db.DefaultValue(mode.Cls, key);
                var prop = db.Prop(key);
                if (LaunchPlanner.Same(want ?? "", got ?? "", prop)) continue;
                bool stored = SetupEngine.GetRule(p, mode.Cls, key) != null;
                if (key == "BotQuota" && versusBots && int.TryParse(want, out var wq) && wq <= 0 && (got == "1" || got == "5")) continue;
                if ((key == "MinimumPlayers" || key == "MinimumPlayersInProgress") && versusBots && !stored && got == "1") continue;
                if (key == "bBots" && mode.Coop && !stored && LaunchPlanner.IsTrue(got)
                    && int.TryParse(SetupEngine.Effective(p, db, mode.Cls, "FriendlyBotQuota"), out var fb) && fb > 0) continue;
                fail("the game would get " + mode.Cls + "." + key + "=" + got + " but the setup says " + want);
            }
            // Each change reaches the game.
            var section = plan.IniSections.FirstOrDefault(sct => sct.Name == "/Script/Insurgency." + mode.Cls);
            foreach (var kv in plan.Overrides)
            {
                if (custom.Contains(kv.Key)) continue;
                var prop = db.Prop(kv.Key);
                bool isBool = prop != null && prop.IsBool;
                bool inUrl = LaunchPlanner.UrlOptions.Contains(kv.Key) && url.Contains("?" + kv.Key + "=" + (isBool ? "1" : kv.Value)) && (!isBool || LaunchPlanner.IsTrue(kv.Value));
                bool inIni = section != null && section.Values.Any(v => v.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && LaunchPlanner.Same(v.Value, kv.Value, prop));
                if (!inUrl && !inIni) fail("change " + kv.Key + "=" + kv.Value + " is neither in the travel URL nor in Game.ini");
                if (state.Settings.ApplyLiveRules && !plan.LiveProperties.Any(x => x.Key == kv.Key && x.Value == kv.Value)) fail("change " + kv.Key + " missing from the after-load properties");
            }
            if (!mode.Coop && mode.Defaults.ContainsKey("bBots") && !versusBots && url.Contains("?bBots=1")) fail("versus bots are off but the travel URL turns them on");
            string vd = SetupEngine.GetRule(p, "*", "AIDifficulty");
            if (!mode.Coop && vd != null && !plan.ConsoleOnly.Contains("AIDifficulty " + vd)) fail("versus AI difficulty " + vd + " is not sent");
            // RCON travel keeps the options of the map before: every option the launcher may set is spelled out, once.
            string full = plan.TravelUrl + LaunchService.TravelResets(plan);
            var keys = full.Split('?').Skip(1).Select(o => o.Split('=')[0]).ToList();
            foreach (var dup in keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) fail("travel URL has " + dup.Key + " twice");
            foreach (var must in new[] { "game", "Mutators", "bSoloGame", "Scenario", "MaxPlayers", "Lighting" })
                if (!keys.Contains(must, StringComparer.OrdinalIgnoreCase)) fail("travel URL does not set " + must);
            if (full.IndexOfAny(new[] { ' ', '"', '|', ';' }) >= 0) fail("travel URL has a space or a console character: " + full);
            string muts = p.MutatorsEnabled && p.Mutators.Count > 0 ? "?Mutators=" + string.Join(",", p.Mutators) : null;
            if (!plan.ExtraOptionKeys.Contains("Mutators"))
            {
                if (muts != null && !url.Contains(muts)) fail("travel URL misses the mutators " + muts);
                if (muts == null && url.Contains("?Mutators=")) fail("mutators are off or empty but the travel URL has some");
            }
            if (!plan.ExtraOptionKeys.Contains("Lighting") && !url.Contains("?Lighting=" + p.Lighting)) fail("travel URL has the wrong lighting");
            bool hc = p.Hardcore && plan.Scenario?.GameModeClass == "INSCheckpointGameMode" && LaunchPlanner.UrlSafe(p.GameModeOverride, false).Length == 0;
            if (!plan.ExtraOptionKeys.Contains("game") && hc != url.Contains("?game=CheckpointHardcore")) fail("hardcore is " + hc + " but the travel URL says otherwise");
            var urlKeys = url.Split('?').Skip(1).Select(o => o.Split('=')[0]).ToList();
            foreach (var dup in urlKeys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) fail("open command has " + dup.Key + " twice");
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
