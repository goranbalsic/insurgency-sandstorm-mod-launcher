using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

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
            step = "regression checks";
            RegressionChecks(state, Fail);

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
                            // Sometimes a squad preset in between: its values are the player's, the second preset must not
                            // take them back to what was there before the first (2026-10-02 audit).
                            var squad = rnd.Next(2) == 0 ? Pick(SetupEngine.SquadPresets(db, coop)) : null;
                            name = "match " + a.Name + (squad != null ? ", squad " + squad.Name : "") + " then " + b.Name;
                            var copy = Json.Deserialize<Profile>(Json.Serialize(p));
                            SetupEngine.Apply(p, state, a);
                            if (squad != null) SetupEngine.Apply(p, state, squad);
                            SetupEngine.Apply(p, state, b);
                            if (squad != null) SetupEngine.Apply(copy, state, squad);
                            SetupEngine.Apply(copy, state, b);
                            if (Full(p) != Full(copy)) Fail(name + ": the setup differs from applying only " + (squad != null ? "the squad preset and " : "") + "the second\n" + Diff(Full(p), Full(copy)));
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
                // The preset's mutators are all there (next to the ones picked by hand).
                var want = pr.Mutators.Select(id => state.FindMutator(id)?.Id).Where(id => id != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var lost = want.Where(id => !p.Mutators.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                if (lost.Count > 0) fail(pr.Name + ": its mutators " + string.Join(",", lost) + " are missing (mutators: " + string.Join(",", p.Mutators) + ")");
            }
        }

        /// <summary>
        /// Fixed checks for what players reported, run before the random steps (each one failed before its fix):
        /// presets taking the last preset's mutators, day/night and hardcore back out, every pair of presets giving the
        /// same setup as the second one alone, saved setups loading back exactly, old profiles and mutator presets
        /// becoming saved setups, and mods that are not on the PC staying out of the launcher.
        /// </summary>
        public static void RegressionChecks(AppState state, Action<string> fail)
        {
            var db = state.Rules;
            var styles = SetupEngine.StylePresets(db);
            var all = styles.Concat(SetupEngine.OfficialPresets(db)).Concat(SetupEngine.PlaylistPresets(state, true)).ToList();
            var checkpoint = state.AllScenarios.FirstOrDefault(s => s.GameModeClass == "INSCheckpointGameMode" && s.Id.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0)
                             ?? state.AllScenarios.First(s => s.GameModeClass == "INSCheckpointGameMode");
            var versus = state.AllScenarios.FirstOrDefault(s => state.ModeFor(s, false) != null && !state.ModeFor(s, false).Coop);
            Profile Fresh(ScenarioInfo s) { var x = new Profile { Name = "check" }; Cli.PickScenario(state, x, s); return x; }
            var realism = styles.First(x => x.Name == "Realism");
            bool Installed(Preset x) => x.Mutators != null && x.Mutators.Any(id => state.FindMutator(id) != null);
            string Muts(Profile x) => string.Join(",", x.Mutators);

            // 1. A preset without mutators takes the last preset's mutators back out ("Hardcore stays", 2026-09-28).
            var withMutators = all.Where(x => Installed(x) && (x.ForModes.Count == 0 || x.ForModes.Contains("INSCheckpointGameMode"))).ToList();
            if (withMutators.Count == 0) fail("no preset with installed mutators to check with");
            foreach (var pl in withMutators)
            {
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, pl);
                SetupEngine.Apply(p, state, realism);
                if (p.Mutators.Count > 0) fail("after " + pl.Name + " then Realism the mutators are " + Muts(p) + " (should be none)");
            }
            // 2. Mutators picked by hand stay; only the preset's go.
            var own = state.AllMutators.Select(m => m.Id).FirstOrDefault(id => withMutators.All(pl => !pl.Mutators.Contains(id, StringComparer.OrdinalIgnoreCase)));
            if (own != null && withMutators.Count > 0)
            {
                var p = Fresh(checkpoint);
                p.Mutators.Add(own);
                SetupEngine.Apply(p, state, withMutators[0]);
                SetupEngine.Apply(p, state, realism);
                if (Muts(p) != own) fail("the mutator picked by hand was not kept alone: " + Muts(p) + " (should be " + own + ")");
            }
            // 3. Night and hardcore a playlist turned on go back off with the next preset, unless changed by hand since.
            var night = all.FirstOrDefault(x => x.Night && x.ForModes.Contains("INSCheckpointGameMode"));
            if (night != null)
            {
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, night);
                if (p.Lighting != "Night") fail(night.Name + " did not turn night on");
                SetupEngine.Apply(p, state, realism);
                if (p.Lighting != "Day") fail("after " + night.Name + " then Realism it is still " + p.Lighting);
                // Night picked by the player before the playlist is the player's: it stays.
                var q = Fresh(checkpoint);
                q.Lighting = "Night";
                SetupEngine.Apply(q, state, night);
                SetupEngine.Apply(q, state, realism);
                if (q.Lighting != "Night") fail("night picked by hand was turned off by the preset after " + night.Name);
                // Day picked by the player after the playlist stays too.
                var r = Fresh(checkpoint);
                SetupEngine.Apply(r, state, night);
                r.Lighting = "Day";
                SetupEngine.Apply(r, state, night);
                SetupEngine.Apply(r, state, realism);
                if (r.Lighting != "Day") fail("lighting after " + night.Name + " twice and Realism is " + r.Lighting);
            }
            else fail("no night playlist for checkpoint to check with");
            var hardcore = all.FirstOrDefault(x => x.HardcoreCheckpoint);
            if (hardcore != null)
            {
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, hardcore);
                if (!p.Hardcore) fail(hardcore.Name + " did not turn hardcore on");
                SetupEngine.Apply(p, state, realism);
                if (p.Hardcore) fail("after " + hardcore.Name + " then Realism hardcore is still on");
            }
            else fail("no hardcore playlist to check with");

            // 3b. A bot value a playlist set but the player changed afterwards stays the player's.
            var squadSetter = all.FirstOrDefault(x => x.Rules.TryGetValue("INSCheckpointGameMode", out var r) && r.ContainsKey("SoloEnemies"));
            if (squadSetter != null)
            {
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, squadSetter);
                SetupEngine.SetRule(p, db, "INSCheckpointGameMode", "SoloEnemies", "5");
                SetupEngine.Apply(p, state, realism);
                if (SetupEngine.GetRule(p, "INSCheckpointGameMode", "SoloEnemies") != "5")
                    fail("enemies set by hand after " + squadSetter.Name + " became " + SetupEngine.Effective(p, db, "INSCheckpointGameMode", "SoloEnemies") + " with the next preset");
            }
            else fail("no playlist with enemy numbers to check with");

            // 3c. Every playlist that changes something is in the list (night-only ones too); map rotations are not.
            var listed = new HashSet<string>(SetupEngine.PlaylistPresets(state, true).Select(x => ((PlaylistDef)x.Source).Key));
            foreach (var pl in db.Playlists)
            {
                bool effect = pl.Mutators.Count > 0 || pl.CoopRules.Count > 0 || !string.IsNullOrEmpty(pl.Ruleset) || pl.Lighting == "Night" || pl.GameAlias == "CheckpointHardcore";
                if (effect != listed.Contains(pl.Key)) fail("playlist " + pl.Title + (effect ? " changes the match but is not listed" : " is listed but changes nothing"));
            }
            if (!listed.Contains("COOP_CHADTEAM6")) fail("Chad Team 6 is not in the playlists");

            // 4. Any preset after any other gives exactly the setup of the second one alone (from the same start).
            var starts = new List<Profile> { Fresh(checkpoint) };
            if (versus != null)
            {
                var v = Fresh(versus);
                v.Lighting = "Night";
                if (own != null) v.Mutators.Add(own);
                SetupEngine.SetRule(v, db, versus.GameModeClass == null ? "*" : state.ModeFor(versus, false).Cls, "RoundTime", "600");
                starts.Add(v);
            }
            var c = Fresh(checkpoint);
            if (own != null) c.Mutators.Add(own);
            SetupEngine.SetRule(c, db, "INSCheckpointGameMode", "SoloEnemies", "12");
            SetupEngine.SetRule(c, db, "INSCheckpointGameMode", "FriendlyBotQuota", "3");
            starts.Add(c);
            int pairs = 0, bad = 0;
            foreach (var start in starts)
            {
                var alone = new Dictionary<Preset, string>();
                foreach (var b in all) { var q = start.Clone("q"); SetupEngine.Apply(q, state, b); alone[b] = SetupEngine.Fingerprint(q); }
                foreach (var a in all)
                    foreach (var b in all)
                    {
                        pairs++;
                        var p = start.Clone("p");
                        SetupEngine.Apply(p, state, a);
                        SetupEngine.Apply(p, state, b);
                        if (SetupEngine.Fingerprint(p) != alone[b] && ++bad <= 5)
                            fail(a.Name + " then " + b.Name + " is not the same as " + b.Name + " alone:\n" + Diff(SetupEngine.Fingerprint(p), alone[b]));
                    }
            }
            if (bad > 5) fail(bad + " preset pairs in all differ");
            if (pairs < 100) fail("only " + pairs + " preset pairs were checked");

            // 5. A saved setup loads back exactly, whatever was on screen before (Advanced options too).
            {
                var p = Fresh(checkpoint);
                p.Lighting = "Night"; p.Hardcore = true; p.MaxPlayers = 12; p.MutatorsEnabled = false;
                if (own != null) p.Mutators.Add(own);
                SetupEngine.SetRule(p, db, "INSCheckpointGameMode", "RoundTime", "900");
                p.CustomIniMode = "Append"; p.CustomIniText = "[/Script/X.Y]\r\nA=1"; p.ExtraUrlOptions = "?Foo=1"; p.AfterLoadCommands = "slomo 1";
                p.EnableCheatsAfterLoad = true; p.GameModeOverride = "Checkpoint"; p.LaunchRuleset = "RS_Test"; p.ForceReload = true;
                var saved = SetupEngine.Capture(p, "check");
                string want = SetupEngine.Fingerprint(p);
                var q = Fresh(versus ?? checkpoint);
                SetupEngine.Apply(q, state, realism);
                SetupEngine.Apply(q, state, new Preset { Name = "check", Kind = PresetKind.Saved, Saved = Json.Deserialize<RulesPreset>(Json.Serialize(saved)) });
                if (SetupEngine.Fingerprint(q) != want) fail("a saved setup did not load back exactly:\n" + Diff(SetupEngine.Fingerprint(q), want));
                // Every Advanced option one by one (not only through the fingerprint).
                foreach (var (label, got, exp) in new[]
                {
                    ("custom Game.ini mode", q.CustomIniMode, p.CustomIniMode), ("custom Game.ini lines", q.CustomIniText, p.CustomIniText),
                    ("extra URL options", q.ExtraUrlOptions, p.ExtraUrlOptions), ("after-load commands", q.AfterLoadCommands, p.AfterLoadCommands),
                    ("mode override", q.GameModeOverride, p.GameModeOverride), ("launch ruleset", q.LaunchRuleset, p.LaunchRuleset),
                    ("cheats after load", q.EnableCheatsAfterLoad.ToString(), p.EnableCheatsAfterLoad.ToString()), ("force reload", q.ForceReload.ToString(), p.ForceReload.ToString()),
                    ("player slots", q.MaxPlayers.ToString(), p.MaxPlayers.ToString()), ("mutators on", q.MutatorsEnabled.ToString(), p.MutatorsEnabled.ToString()),
                    ("hardcore", q.Hardcore.ToString(), p.Hardcore.ToString()), ("lighting", q.Lighting, p.Lighting), ("scenario", q.ScenarioId, p.ScenarioId),
                })
                    if (got != exp) fail("a saved setup loaded " + label + " as " + got + " instead of " + exp);
                if (SetupEngine.SetupChanged(q)) fail("a saved setup just loaded counts as changed");
                q.MaxPlayers = 13;
                if (!SetupEngine.SetupChanged(q)) fail("a change after loading a saved setup is not noticed");
            }

            // 6. Profiles and mutator presets of older versions become saved setups; only the setup on screen stays.
            {
                var store = state.Store;
                var extra = new Profile { Name = "Check old profile" };
                Cli.PickScenario(state, extra, checkpoint);
                SetupEngine.SetRule(extra, db, "INSCheckpointGameMode", "RoundTime", "700");
                store.Profiles.Add(extra);
                store.SaveProfile(extra);
                var mp = new MutatorPreset { Name = "Check mutator preset", Mutators = own != null ? new List<string> { own } : new List<string>() };
                state.Settings.MutatorPresets.Add(mp);
                string extraPrint = SetupEngine.Fingerprint(extra);
                SetupEngine.MigrateToSetups(state, true);
                var fromProfile = state.Settings.RulesPresets.FirstOrDefault(r => r.Name == "Check old profile");
                var fromMutators = state.Settings.RulesPresets.FirstOrDefault(r => r.Name == "Check mutator preset");
                if (fromProfile?.Setup == null) fail("an old profile did not become a saved setup");
                else if (SetupEngine.Fingerprint(fromProfile.Setup) != extraPrint) fail("an old profile changed on its way to a saved setup:\n" + Diff(SetupEngine.Fingerprint(fromProfile.Setup), extraPrint));
                if (fromMutators?.Setup == null) fail("a mutator preset did not become a saved setup");
                else if (string.Join(",", fromMutators.Setup.Mutators) != string.Join(",", mp.Mutators)) fail("a mutator preset lost its mutators on the way");
                if (store.Profiles.Count != 1) fail(store.Profiles.Count + " profiles after the change to saved setups");
                if (state.Settings.MutatorPresets.Count != 0) fail("mutator presets are left");
                // The next start converts nothing again (the setup on screen keeps an old profile name).
                int count = state.Settings.RulesPresets.Count;
                store.Active.Name = "Check named profile";
                SetupEngine.MigrateToSetups(state);
                SetupEngine.MigrateToSetups(state);
                if (state.Settings.RulesPresets.Count != count) fail("starting again converted " + (state.Settings.RulesPresets.Count - count) + " more saved setups");
                store.Active.Name = state.Settings.ActiveProfile;
                state.Settings.RulesPresets.RemoveAll(r => r == fromProfile || r == fromMutators);
                store.SaveSettings();
            }

            // 8. Saving and loading a setup does not change what the next preset does: the preset the setup was made with
            //    still comes back out ("Hardcore stays" after loading a saved setup, 2026-09-29).
            {
                var hc = all.FirstOrDefault(x => x.HardcoreCheckpoint && x.Night) ?? hardcore;
                var nexts = new List<Preset> { realism };
                if (withMutators.Count > 1) nexts.Add(withMutators[1]);
                int checkedSaves = 0, badSaves = 0;
                foreach (var a in withMutators.Take(12).Concat(new[] { night, hc, squadSetter }).Where(x => x != null).Distinct())
                {
                    var p = Fresh(checkpoint);
                    if (own != null) p.Mutators.Add(own);
                    SetupEngine.Apply(p, state, a);
                    var saved = Json.Deserialize<RulesPreset>(Json.Serialize(SetupEngine.Capture(p, "check save")));
                    var q = Fresh(versus ?? checkpoint);
                    SetupEngine.Apply(q, state, new Preset { Name = saved.Name, Kind = PresetKind.Saved, Saved = saved });
                    if (SetupEngine.PresetLabel(q) != SetupEngine.PresetLabel(p) && ++badSaves <= 3)
                        fail("a saved setup made with " + a.Name + " shows the preset as '" + SetupEngine.PresetLabel(q) + "' after loading (should be '" + SetupEngine.PresetLabel(p) + "')");
                    foreach (var b in nexts)
                    {
                        checkedSaves++;
                        var p2 = p.Clone("p2"); var q2 = q.Clone("q2");
                        SetupEngine.Apply(p2, state, b);
                        SetupEngine.Apply(q2, state, b);
                        if (SetupEngine.Fingerprint(q2) != SetupEngine.Fingerprint(p2) && ++badSaves <= 3)
                            fail("saved with " + a.Name + ", loaded, then " + b.Name + " is not the same as without saving:\n" + Diff(SetupEngine.Fingerprint(q2), SetupEngine.Fingerprint(p2)));
                    }
                }
                if (badSaves > 3) fail(badSaves + " save-and-load checks in all differ");
                if (checkedSaves < 10) fail("only " + checkedSaves + " save-and-load checks");
            }

            // 9. A setup from 1.7.0 (a preset applied then, before the launcher kept what presets add): the next preset
            //    still takes that preset's mutators back out; mutators picked by hand stay.
            if (withMutators.Count > 0)
            {
                var old = withMutators.FirstOrDefault(x => x.Group == "Playlist") ?? withMutators[0];
                var p = Fresh(checkpoint);
                // What 1.7.0 left: the preset's mutators in place of the list, its name and check, nothing else.
                SetupEngine.SetMutators(p, state, old.Mutators);
                SetupEngine.MarkPreset(p, old.Name, false);
                if (own != null) p.Mutators.Add(own);
                p.PresetKeys = new List<string>();
                p.PresetChanges = null;
                SetupEngine.Apply(p, state, realism);
                string want = own ?? "";
                if (Muts(p) != want) fail("a 1.7.0 setup with " + old.Name + " kept " + Muts(p) + " after Realism (should be " + (want.Length == 0 ? "none" : want) + ")");
            }

            // 10. Hardcore playlists fit Hardcore Checkpoint too (hardcore already on): night comes on, no "made for" note.
            {
                var nightHc = all.FirstOrDefault(x => x.HardcoreCheckpoint && x.Night);
                if (nightHc == null) fail("no night hardcore playlist to check with");
                else
                {
                    var p = Fresh(checkpoint);
                    p.Hardcore = true;
                    string text = SetupEngine.Apply(p, state, nightHc);
                    if (p.Lighting != "Night") fail(nightHc.Name + " with hardcore already on did not turn night on");
                    if (!p.Hardcore) fail(nightHc.Name + " turned hardcore off");
                    if (text.Contains("pick one of those")) fail(nightHc.Name + " with hardcore on says: " + text);
                    SetupEngine.Apply(p, state, realism);
                    if (p.Lighting != "Day" || !p.Hardcore) fail("after " + nightHc.Name + " (hardcore picked by hand) then Realism: " + p.Lighting + ", hardcore " + p.Hardcore);
                }
                // A Checkpoint playlist that is not hardcore is played as normal Checkpoint: hardcore goes off while it is
                // in use (its rules are Checkpoint's) and comes back with the next preset.
                var nightNormal = all.FirstOrDefault(x => x.Night && !x.HardcoreCheckpoint && x.ForModes.Contains("INSCheckpointGameMode"));
                if (nightNormal == null) fail("no night Checkpoint playlist to check with");
                else
                {
                    var p = Fresh(checkpoint);
                    p.Hardcore = true;
                    string text = SetupEngine.Apply(p, state, nightNormal);
                    if (p.Hardcore || p.Lighting != "Night") fail(nightNormal.Name + " with hardcore on: hardcore " + p.Hardcore + ", " + p.Lighting + " (should be normal Checkpoint at night)");
                    if (text.Contains("pick one of those")) fail(nightNormal.Name + " with hardcore on says: " + text);
                    SetupEngine.Apply(p, state, realism);
                    if (!p.Hardcore || p.Lighting != "Day") fail("after " + nightNormal.Name + " then Realism: hardcore " + p.Hardcore + ", " + p.Lighting + " (should be hardcore by day again)");
                }
            }

            // 11. A preset whose point is its mutators turns the mutators switch on (off, the match would have none of them);
            //     the next preset puts the switch back.
            if (withMutators.Count > 0)
            {
                var p = Fresh(checkpoint);
                p.MutatorsEnabled = false;
                SetupEngine.Apply(p, state, withMutators[0]);
                if (!p.MutatorsEnabled) fail(withMutators[0].Name + " left the mutators switched off");
                SetupEngine.Apply(p, state, realism);
                if (p.MutatorsEnabled) fail("after " + withMutators[0].Name + " then Realism the mutators are still switched on (they were off before)");
                // Switched off by hand after the preset: stays off.
                var q = Fresh(checkpoint);
                SetupEngine.Apply(q, state, withMutators[0]);
                q.MutatorsEnabled = false;
                SetupEngine.Apply(q, state, realism);
                if (q.MutatorsEnabled) fail("mutators switched off by hand came back on with the next preset");
            }

            // 12. Two presets with the same name (LMGOnly for co-op and for versus, Competitive Firefight as a ruleset and a
            //     playlist): only the one applied counts as in use.
            {
                var lists = all.GroupBy(SetupEngine.PresetId).Select(g => g.First()).ToList();
                var twins = lists.GroupBy(x => x.Name).Where(g => g.Select(SetupEngine.PresetId).Distinct().Count() > 1).ToList();
                if (twins.Count == 0) fail("no two presets with the same name to check with");
                foreach (var g in twins)
                {
                    var first = g.First();
                    var p = Fresh(checkpoint);
                    SetupEngine.Apply(p, state, first);
                    var inUse = g.Where(x => SetupEngine.InUse(p, x)).ToList();
                    if (inUse.Count != 1 || inUse[0] != first) fail(first.Name + ": " + inUse.Count + " presets of that name count as in use");
                }
            }

            // 13. A saved setup on a map that is not installed any more says so when it is loaded.
            {
                var p = Fresh(checkpoint);
                p.ScenarioId = "Scenario_NotInstalled_Checkpoint_Security";
                p.MapKey = "NotInstalled";
                var saved = SetupEngine.Capture(p, "check missing map");
                var q = Fresh(checkpoint);
                string text = SetupEngine.Apply(q, state, new Preset { Name = saved.Name, Kind = PresetKind.Saved, Saved = saved });
                if (text.IndexOf("Scenario_NotInstalled_Checkpoint_Security", StringComparison.OrdinalIgnoreCase) < 0) fail("loading a setup on a map that is not installed says only: " + text);
            }

            // 14. A setup saved on a custom map entry that was deleted since loads on the map of its scenario (not on the
            //     official map picked before the entry), unchanged.
            {
                var other = state.Maps.FirstOrDefault(m => !m.Scenarios.Any(x => x.Id == checkpoint.Id) && m.Scenarios.Count > 0);
                var entry = new CustomMapEntry { Label = "check", Level = checkpoint.Level, Scenario = checkpoint.Id, GameModeClass = checkpoint.GameModeClass };
                state.Settings.CustomMaps.Add(entry);
                var p = Fresh(checkpoint);
                if (other != null) p.MapKey = other.Key;
                p.CustomMapId = entry.Id;
                var saved = SetupEngine.Capture(p, "check custom");
                state.Settings.CustomMaps.Remove(entry);
                var q = Fresh(versus ?? checkpoint);
                SetupEngine.Apply(q, state, new Preset { Name = saved.Name, Kind = PresetKind.Saved, Saved = saved });
                var owner = state.Maps.First(m => m.Scenarios.Any(x => x.Id == checkpoint.Id));
                if (q.CustomMapId != null || q.ScenarioId != checkpoint.Id || q.MapKey != owner.Key)
                    fail("a setup of a deleted custom map loaded as " + q.MapKey + " / " + q.ScenarioId + " / custom " + q.CustomMapId + " (should be " + owner.Key + " / " + checkpoint.Id + ")");
                if (SetupEngine.SetupChanged(q)) fail("a setup of a deleted custom map counts as changed right after loading");
            }

            // 15. What the screen shows as the bot count is what the launch sends (Free For All, Ambush, Defusal: 0 by
            //     default, 5 in the launch).
            foreach (var m in db.Modes.Where(x => !x.Coop && x.Defaults.ContainsKey("BotQuota") && x.Defaults.ContainsKey("bBots")))
            {
                var sc = state.AllScenarios.FirstOrDefault(x => state.ModeFor(x, false)?.Cls == m.Cls);
                if (sc == null) continue;
                var p = Fresh(sc);
                var plan = LaunchPlanner.Build(p, state);
                string sent = plan.Overrides.TryGetValue("BotQuota", out var bq) ? bq : db.DefaultValue(m.Cls, "BotQuota");
                string shown = SetupEngine.Effective(p, db, m.Cls, "BotQuota");
                if (!LaunchPlanner.Same(sent, shown, db.Prop("BotQuota"))) fail(m.Name + ": the bot count shown is " + shown + " but the launch sends " + sent);
            }

            // 17. AI teammates on screen are the AI the game brings: FriendlyBotQuota is the team size, you included (owner,
            //     2026-10-01: 2 on the screen gave one AI). The co-op squad presets bring the teammates their text promises.
            for (int n = 0; n <= 32; n++)
            {
                int quota = SetupEngine.FriendlyBotQuotaFor(n);
                if (quota != (n == 0 ? 0 : n + 1) || SetupEngine.AiTeammates(quota) != n)
                    fail(n + " AI teammates are sent as FriendlyBotQuota=" + quota + " and shown as " + SetupEngine.AiTeammates(quota) + " (the game counts you too)");
            }
            foreach (var (name, ai) in new[] { ("Lone Wolf", 0), ("Fireteam", 2), ("Squad Leader", 6), ("Full Platoon", 10) })
            {
                var pr = SetupEngine.SquadPresets(db, true).FirstOrDefault(x => x.Name == name);
                if (pr == null) { fail("co-op squad preset " + name + " is missing"); continue; }
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, pr);
                var plan = LaunchPlanner.Build(p, state);
                string sent = plan.Overrides.TryGetValue("FriendlyBotQuota", out var fq) ? fq : db.DefaultValue("INSCheckpointGameMode", "FriendlyBotQuota");
                int got = int.TryParse(sent, out var sq) ? SetupEngine.AiTeammates(sq) : -1;
                if (got != ai) fail(name + ": promises " + ai + " AI teammates, the game gets FriendlyBotQuota=" + sent + " (" + got + " AI)");
                if (ai > 0 && !(plan.Overrides.TryGetValue("bBots", out var bb) && LaunchPlanner.IsTrue(bb))) fail(name + ": AI teammates without bBots");
                if (plan.PlayerSlots < 1 + ai) fail(name + ": " + ai + " AI teammates but " + plan.PlayerSlots + " player slots");
            }

            // 19. (2026-10-02 audit) A squad value the player picked after a match preset stays when the next match preset
            //     comes: Frenzy, then Fireteam, then Realism put Frenzy's "before" values back (SoloEnemies 6 came back), and
            //     Squad Leader, Official Rules, Mode defaults, Realism brought 6 AI teammates back.
            //     And FriendlyBotQuota=1 (no AI: it counts you) launches like 0: solo, no bots forced on.
            {
                var squads = SetupEngine.SquadPresets(db, true);
                var matches = all.Where(x => x.ForModes.Count == 0 || x.ForModes.Contains("INSCheckpointGameMode")).ToList();
                foreach (var first in matches.Where(x => x.Rules != null && x.Rules.Values.Any(r => r.Keys.Any(SetupEngine.IsSquadKey))).Concat(matches.Take(3)))
                    foreach (var sq in squads)
                    {
                        var p = Fresh(checkpoint);
                        SetupEngine.Apply(p, state, first);
                        SetupEngine.Apply(p, state, sq);
                        SetupEngine.Apply(p, state, realism);
                        var q = Fresh(checkpoint);
                        SetupEngine.Apply(q, state, sq);
                        SetupEngine.Apply(q, state, realism);
                        if (Canon(p.Rules) != Canon(q.Rules))
                        {
                            fail(first.Name + ", " + sq.Name + ", Realism is not " + sq.Name + ", Realism:\n" + Diff(Canon(p.Rules), Canon(q.Rules)));
                            break;
                        }
                    }
                // The same for a value set on the squad card that happens to be the one the match preset left.
                var leader = squads.FirstOrDefault(x => x.Name == "Squad Leader");
                foreach (var first in matches.Where(x => x.Rules != null && x.Rules.Values.Any(r => r.Keys.Any(SetupEngine.IsSquadKey))))
                {
                    if (leader == null) { fail("no Squad Leader squad preset"); break; }
                    var p = Fresh(checkpoint);
                    SetupEngine.Apply(p, state, leader);
                    SetupEngine.Apply(p, state, first);
                    var kept = new List<(string cls, string key, string value)>();
                    foreach (var mode in db.Modes.Where(m => m.Coop))
                        foreach (var key in mode.Defaults.Keys.Where(SetupEngine.IsSquadKey))
                        {
                            string v = SetupEngine.GetRule(p, mode.Cls, key);
                            SetupEngine.SetSquadRule(p, db, mode.Cls, key, v);
                            kept.Add((mode.Cls, key, SetupEngine.GetRule(p, mode.Cls, key)));
                        }
                    SetupEngine.Apply(p, state, realism);
                    // The same squad values set on the card with no match preset before: Realism must leave both alike.
                    var q = Fresh(checkpoint);
                    SetupEngine.Apply(q, state, leader);
                    foreach (var k in kept) SetupEngine.SetSquadRule(q, db, k.cls, k.key, k.value);
                    SetupEngine.Apply(q, state, realism);
                    var lost = kept.FirstOrDefault(k => SetupEngine.GetRule(p, k.cls, k.key) != SetupEngine.GetRule(q, k.cls, k.key));
                    if (lost.key != null) { fail("Squad Leader, " + first.Name + ", " + lost.key + " set on the squad card to " + (lost.value ?? "default") + ", Realism: it is " + (SetupEngine.GetRule(p, lost.cls, lost.key) ?? "default") + " (should be " + (SetupEngine.GetRule(q, lost.cls, lost.key) ?? "default") + ")"); break; }
                }
                var solo = new[] { "0", "1" }.Select(v =>
                {
                    var p = Fresh(checkpoint);
                    SetupEngine.SetRule(p, db, "INSCheckpointGameMode", "FriendlyBotQuota", v);
                    var plan = LaunchPlanner.Build(p, state);
                    return (plan.OpenCommand ?? "").Replace("?FriendlyBotQuota=" + v, "") + " slots " + plan.PlayerSlots + " bots " + (plan.Overrides.TryGetValue("bBots", out var bb) ? bb : "-");
                }).ToList();
                if (solo[0] != solo[1]) fail("FriendlyBotQuota=1 (no AI teammate) launches unlike 0:\n  0: " + solo[0] + "\n  1: " + solo[1]);
            }

            // 20. (2026-10-02 audit) A running game read its rules from Game.ini at start: a match that leaves one of them at
            //     the default sets it back after the map loads (Realism, then Game defaults kept Realism's rules). Own
            //     Game.ini lines and the ruleset are read at start too: a change asks for a restart, and a game started some
            //     other way (Steam) has no ruleset.
            {
                var named = new List<(string name, Profile p)> { ("Game defaults", Fresh(checkpoint)) };
                foreach (var m in all.Where(x => x.ForModes.Count == 0 || x.ForModes.Contains("INSCheckpointGameMode")))
                {
                    var p = Fresh(checkpoint);
                    SetupEngine.Apply(p, state, m);
                    named.Add((m.Name, p));
                }
                var plans = named.Select(x => (x.name, x.p, plan: LaunchPlanner.Build(x.p, state))).Where(x => x.plan.Mode != null).ToList();
                // What a plan writes into the mode sections of Game.ini is what it remembers as read at game start.
                const string script = "/Script/Insurgency.";
                foreach (var a in plans)
                {
                    var keys = LaunchPlanner.WrittenRuleKeys(a.plan, db);
                    foreach (var sec in a.plan.IniSections.Where(x => x.Name.StartsWith(script, StringComparison.OrdinalIgnoreCase) && db.Mode(x.Name.Substring(script.Length)) != null))
                        foreach (var v in sec.Values.Where(v => db.Prop(v.Key) != null))
                            if (!keys.Contains(sec.Name.Substring(script.Length) + "|" + v.Key, StringComparer.OrdinalIgnoreCase)) { fail(a.name + ": " + v.Key + " goes into Game.ini but is not remembered as read at game start"); break; }
                }
                if (!plans.Any(a => LaunchPlanner.WrittenRuleKeys(a.plan, db).Count > 0)) fail("no preset writes a rule into Game.ini: check 20 has nothing to check");
                bool reported = false;
                foreach (var a in plans)
                {
                    var keys = LaunchPlanner.WrittenRuleKeys(a.plan, db);
                    foreach (var b in plans)
                    {
                        if (reported) break;
                        var live = b.plan.LiveProperties.Concat(LaunchPlanner.LiveResets(b.plan, db, keys)).ToList();
                        foreach (var id in keys.Where(k => k.StartsWith(b.plan.Mode.Cls + "|", StringComparison.OrdinalIgnoreCase)))
                        {
                            string key = id.Substring(id.IndexOf('|') + 1);
                            string want = b.plan.Overrides.TryGetValue(key, out var ov) ? ov : db.DefaultValue(b.plan.Mode.Cls, key);
                            var got = live.Where(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList();
                            if (got.Count == 1 && LaunchPlanner.Same(want, got[0], db.Prop(key))) continue;
                            fail(a.name + " at game start, then " + b.name + ": " + key + " is sent as " + (got.Count == 0 ? "nothing" : string.Join("/", got)) + " (the match wants " + want + ")");
                            reported = true;
                            break;
                        }
                    }
                }
                Profile WithIni(string text) { var x = Fresh(checkpoint); x.CustomIniMode = "Append"; x.CustomIniText = text; return x; }
                string none = LaunchPlanner.RestartKeyFor(Fresh(checkpoint), db);
                string one = LaunchPlanner.RestartKeyFor(WithIni("[/Script/Insurgency.Mutator_X]\nA=1"), db), two = LaunchPlanner.RestartKeyFor(WithIni("[/Script/Insurgency.Mutator_X]\nA=2"), db);
                if (one == two || one == none) fail("own Game.ini lines are read at game start, but changing them asks for no restart");
                if (LaunchPlanner.RestartKeyFor(new Profile(), db) != LaunchPlanner.Hash("ruleset=")) fail("the restart key of a plain setup changed: games started by older versions would be restarted for nothing");

                var withRuleset = Fresh(checkpoint);
                withRuleset.LaunchRuleset = "RS_CompetitiveFirefight";
                var planR = LaunchPlanner.Build(withRuleset, state);
                var planPlain = LaunchPlanner.Build(Fresh(checkpoint), state);
                var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                var set = new AppSettings
                {
                    LastRulesWriteUtc = t0, LastWrittenRulesHash = planR.RestartKey, LastWrittenIniPart = LaunchPlanner.IniRestartPart(withRuleset, db),
                    LastWrittenRuleKeys = new List<string> { "INSCheckpointGameMode|RoundTime" },
                    GameStartedWithRulesHash = planR.RestartKey, GameStartedWithRuleKeys = new List<string> { "INSCheckpointGameMode|RoundTime" }, GameStartedAtUtc = t0.AddSeconds(1)
                };
                if (LaunchService.ActiveRestartKey(set, t0.AddSeconds(30), db) != planR.RestartKey) fail("the game the launcher started is not taken as started with its official ruleset");
                string fromSteam = LaunchService.ActiveRestartKey(set, t0.AddHours(1), db);
                if (fromSteam == planR.RestartKey || fromSteam != planPlain.RestartKey) fail("a game started outside the launcher is taken as started with the launcher's official ruleset");
                if (LaunchService.ActiveRuleKeys(set, t0.AddHours(1)).Count != 1) fail("a game started outside the launcher after the last write did not read Game.ini's rules");
                set.LastWrittenIniPart = null;
                if (LaunchService.ActiveRestartKey(set, t0.AddHours(1), db) != set.LastWrittenRulesHash) fail("settings from 1.10.0: a game started outside the launcher is restarted for nothing");
            }

            // 21. (2026-10-02 audit) Secrets never leave the PC or show on screen: the public problem report kept the server's
            //     Steam token, a waiting mod.io code and the remote server's name; the shown command line kept the end of
            //     a quoted password.
            {
                var s = state.Settings;
                var keep = (s.ServerGslt, s.ServerModioCode, s.ServerRemoteHost, s.ServerGameStatsToken, s.ServerRemoteRconPassword, s.ServerPassword);
                s.ServerGslt = "GSLTSECRET0123"; s.ServerModioCode = "54321"; s.ServerRemoteHost = "secret-host.example.org";
                s.ServerGameStatsToken = "STATSSECRET99"; s.ServerRemoteRconPassword = "RCONSECRET77"; s.ServerPassword = "JOINSECRET55";
                try
                {
                    DebugReport.BuildPublic("check", state, null, out var shortText, out var fullText);
                    if (!fullText.Contains("== settings")) fail("the public report has no settings part to check");
                    foreach (var secret in new[] { "GSLTSECRET0123", "54321", "secret-host", "STATSSECRET99", "RCONSECRET77", "JOINSECRET55" })
                        if ((shortText + fullText).Contains(secret)) fail("the public problem report contains " + secret);
                }
                finally { (s.ServerGslt, s.ServerModioCode, s.ServerRemoteHost, s.ServerGameStatsToken, s.ServerRemoteRconPassword, s.ServerPassword) = keep; }
                var shown = new ServerPlan { OwnArgs = "Town?Scenario=Scenario_Hideout_Checkpoint_Security?Password=\"join me\" -RconPassword=\"open sesame\" -GSLTToken=\"TOK EN\" -GameStatsToken=\"ST ATS\" -SecurityCode=12345" }.ShownCommandLine;
                foreach (var secret in new[] { "join", "me\"", "open", "sesame", "TOK", "EN\"", "ST ", "ATS", "12345" })
                    if (shown.Contains(secret)) fail("the shown command line contains " + secret + ": " + shown);
            }

            // 22. (2026-10-02 audit) Names from a mod's .pak go into the open command, the server's command line, RCON and Game.ini
            //     section names: one with a space, | or a line break could add server switches, RCON commands or ini sections.
            //     The game's own names all pass; a mod's odd ones are left out, also when they come from an old scan cache.
            {
                foreach (var odd in new[] { "Scen_X -RconPassword=p", "M|exit", "A?B=1", "X\r\n[Rcon]", "a\"b", "", "a;b" })
                    if (GameCatalog.SafeId(odd)) fail("a name from a mod passes the check: " + odd.Replace("\r\n", "\\r\\n"));
                if (GameCatalog.SafeModePathText("/X/BP.BP_C'\r\n[/Script/Insurgency.TeamInfo]") || !GameCatalog.SafeModePathText("/Game/Game/GameModes/BP_Skirmish.BP_Skirmish_C"))
                    fail("the game mode path check is wrong");
                foreach (var s in state.Official.Scenarios)
                    if (!GameCatalog.SafeId(s.Id) || !GameCatalog.SafeId(s.Level)) { fail("an official scenario fails the name check: " + s.Id + " " + s.Level); break; }
                foreach (var m in state.Official.Mutators)
                    if (!GameCatalog.SafeId(m.Id)) { fail("an official mutator fails the name check: " + m.Id); break; }
                if (UeIni.Render(new[] { new UeIni.Section("A]\r\n[Rcon") { Values = { new KeyValuePair<string, string>("bEnabled", "False") } } }).Contains("Rcon"))
                    fail("a section name with a line break is written into Game.ini");
                var evil = new ModInfo { Id = 999, Name = "Evil" };
                evil.Scenarios.Add(new ScenarioInfo { Id = "Scen_X -EnableCheats", Level = "/Game/Mods/Evil/Maps/X", MapKey = "X", GameModeClass = "INSCheckpointGameMode", Source = ContentSource.Mod });
                evil.Scenarios.Add(new ScenarioInfo { Id = "Scen_Fine", Level = "/Game/Mods/Evil/Maps/Y", MapKey = "Y", GameModeClass = "INSCheckpointGameMode", GameModePath = "/X/BP.BP_C'\r\n[Rcon]", Source = ContentSource.Mod });
                evil.Mutators.Add(new MutatorInfo { Id = "M|exit", DisplayName = "M", Source = ContentSource.Mod });
                state.Mods.Add(evil);
                try
                {
                    state.Rebuild();
                    if (state.AllScenarios.Any(s => s.Id.Contains(" ")) || state.FindMutator("M|exit") != null) fail("a mod's unsafe name reaches the scenario or mutator list");
                    if (state.AllScenarios.FirstOrDefault(s => s.Id == "Scen_Fine")?.GameModePath != null) fail("a mod's unsafe game mode path reaches the Game.ini sections");
                }
                finally { state.Mods.Remove(evil); state.Rebuild(); }
                // A file nested very deep is a clean error, not a stack overflow that ends the launcher at every start.
                try { Json.Parse(new string('[', 100000)); fail("JSON nested 100000 deep was read"); }
                catch (FormatException) { }
                if (Json.Parse("[[[[1]]]]") == null) fail("plain nested JSON is not read");
                // SteamCMD: Valve's own certificate name only.
                if (!SteamCmd.ValveNames.Contains("Valve Corp.") || SteamCmd.ValveNames.Contains("Valve Tools Ltd")) fail("the SteamCMD signer check accepts the wrong names");
            }

            // 23. (2026-10-02 audit) A setup saved before 1.8.0 (rules and mutators, maybe the match) loads the same whatever was on
            //     screen before: the Advanced options of the setup before stayed (extra URL options, cheats, after-load lines).
            {
                var legacy = new RulesPreset
                {
                    Name = "Old", FullSetup = true, MapKey = checkpoint.MapKey, ScenarioId = checkpoint.Id, Lighting = "Night", MaxPlayers = 8,
                    Rules = new Dictionary<string, Dictionary<string, string>> { ["INSCheckpointGameMode"] = new Dictionary<string, string> { ["RoundTime"] = "900" } },
                };
                var old = new Preset { Name = "Old", Kind = PresetKind.Saved, Saved = legacy };
                var busy = Fresh(versus ?? checkpoint);
                busy.ExtraUrlOptions = "?Foo=1"; busy.EnableCheatsAfterLoad = true; busy.AfterLoadCommands = "slomo 2"; busy.GameModeOverride = "Push";
                busy.CustomIniMode = "Append"; busy.CustomIniText = "[/Script/Insurgency.Mutator_X]\nA=1"; busy.LaunchRuleset = "RS_Hardcore";
                SetupEngine.Apply(busy, state, old);
                var clean = Fresh(versus ?? checkpoint);
                SetupEngine.Apply(clean, state, old);
                if (SetupEngine.Fingerprint(busy) != SetupEngine.Fingerprint(clean)) fail("a setup saved before 1.8.0 keeps what the setup before had:\n" + Diff(SetupEngine.Fingerprint(busy), SetupEngine.Fingerprint(clean)));
            }

            // 24. (2026-10-02 audit) A playlist's own values win over its ruleset's (Chad Team666 sent the Frenzy ruleset's enemy
            //     counts), and a hardcore playlist's rules reach the Hardcore Checkpoint match it is played as.
            foreach (var pr in SetupEngine.PlaylistPresets(state, true).Where(x => x.Source is PlaylistDef pd && pd.CoopRules.Count > 0))
            {
                var pl = (PlaylistDef)pr.Source;
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, pr);
                var plan = LaunchPlanner.Build(p, state);
                if (plan.Mode == null || !(pl.Modes.Count == 0 || pl.Modes.Contains("INSCheckpointGameMode"))) continue;
                foreach (var kv in pl.CoopRules)
                {
                    if (!SetupEngine.MatchPresetSets(db, plan.Mode.Cls, kv.Key, kv.Value) || db.DefaultValue(plan.Mode.Cls, kv.Key) == null) continue;
                    string got = SetupEngine.Effective(p, db, plan.Mode.Cls, kv.Key);
                    if (!LaunchPlanner.Same(kv.Value, got, db.Prop(kv.Key))) { fail(pr.Name + " on " + plan.Mode.Cls + ": " + kv.Key + " is " + got + " (the playlist says " + kv.Value + ")"); break; }
                }
            }
            // Every Checkpoint rule a playlist sets (its ruleset's too) is in the match it is played as, Hardcore Checkpoint
            // for the hardcore ones (Task Force 666's Frenzy rules sat on Checkpoint while Hardcore was played).
            foreach (var pr in SetupEngine.PlaylistPresets(state, true).Where(x => x.Rules.ContainsKey("INSCheckpointGameMode") && (x.ForModes.Count == 0 || x.ForModes.Contains("INSCheckpointGameMode"))))
            {
                var p = Fresh(checkpoint);
                SetupEngine.Apply(p, state, pr);
                var plan = LaunchPlanner.Build(p, state);
                if (plan.Mode == null) continue;
                foreach (var kv in pr.Rules["INSCheckpointGameMode"])
                {
                    if (!SetupEngine.MatchPresetSets(db, plan.Mode.Cls, kv.Key, kv.Value) || db.DefaultValue(plan.Mode.Cls, kv.Key) == null) continue;
                    string got = SetupEngine.Effective(p, db, plan.Mode.Cls, kv.Key);
                    if (!LaunchPlanner.Same(kv.Value, got, db.Prop(kv.Key))) { fail(pr.Name + " played as " + plan.Mode.Cls + ": " + kv.Key + " is " + got + " (the playlist sets " + kv.Value + ")"); break; }
                }
            }

            // 25. (2026-10-02 audit) Stored setups edited by hand load and clean up: a mode or rule written twice in another case
            //     threw and the setup was set aside as broken; rules of a mode the game does not have stayed forever.
            {
                var p = new Profile { Name = "hand" };
                p.Rules = new Dictionary<string, Dictionary<string, string>>
                {
                    ["INSCheckpointGameMode"] = new Dictionary<string, string> { ["RoundTime"] = "600", ["roundtime"] = "700" },
                    ["inscheckpointgamemode"] = new Dictionary<string, string> { ["WinLimit"] = "3" },
                    ["INSNoSuchMode"] = new Dictionary<string, string> { ["RoundTime"] = "600" },
                    ["*"] = new Dictionary<string, string> { ["Foo"] = "1", ["AIDifficulty"] = "0.8" },
                };
                try
                {
                    Store.Normalize(p);
                    if (!p.Rules.TryGetValue("INSCheckpointGameMode", out var cp) || !cp.ContainsKey("WinLimit") || cp.Count != 2) fail("rules written twice in another case were not merged: " + Canon(p.Rules));
                    SetupEngine.CleanRules(p.Rules, db);
                    var left = SetupEngine.Problems(p, state);
                    if (left.Count > 0) fail("a setup edited by hand is not clean after loading: " + string.Join("; ", left));
                    if (p.Rules.ContainsKey("INSNoSuchMode") || (p.Rules.TryGetValue("*", out var anyMode) && anyMode.ContainsKey("Foo"))) fail("rules nothing uses stayed: " + Canon(p.Rules));
                    if (!(p.Rules.TryGetValue("*", out var all2) && all2.ContainsKey("AIDifficulty"))) fail("the versus AI difficulty went with the unused rules");
                }
                catch (Exception ex) { fail("a setup edited by hand: " + ex.GetType().Name + " " + ex.Message); }
            }

            // 26. (2026-10-02 audit) Own Game.ini lines in a section that also has rules are all written, in order (+Array=A went,
            //     MyKey=1 became MyKey=2); [Rcon] is read as the game reads it (first value); and a custom map entry with ?, | or
            //     a space in its scenario does not launch (it went into the open command as it was).
            {
                var p = Fresh(checkpoint);
                SetupEngine.SetRule(p, db, "INSCheckpointGameMode", "RoundTime", "600");
                p.CustomIniMode = "Append";
                p.CustomIniText = "[/Script/Insurgency.INSCheckpointGameMode]\n+MyArray=A\n+MyArray=B\nMyKey=1\nMyKey=2";
                var sec = LaunchPlanner.Build(p, state).IniSections.FirstOrDefault(x => x.Name == "/Script/Insurgency.INSCheckpointGameMode");
                string lines = sec == null ? "" : string.Join("|", sec.Values.Select(v => v.Key + "=" + v.Value));
                if (!lines.Contains("RoundTime=600") || !lines.Contains("+MyArray=A|+MyArray=B|MyKey=1|MyKey=2")) fail("own Game.ini lines next to rules: " + lines);

                string twice = "[Rcon]\r\nbEnabled=True\r\nPassword=first\r\nPassword=second\r\n\r\n[Rcon]\r\nPassword=third\r\n";
                var ownOptions = new AppSettings { ServerUseOwnArgs = true, ServerOwnArgs = "Town?Scenario=Scenario_Hideout_Checkpoint_Security -log" };
                if (ServerPlanner.RconFor(ownOptions, twice).Password != "first") fail("[Rcon] read with a password the game does not use: " + ServerPlanner.RconFor(ownOptions, twice).Password);
                // Own options with a password but no port: the server's default port, not the launcher's setting.
                var portless = new AppSettings { ServerUseOwnArgs = true, ServerRconPort = 27999, ServerOwnArgs = "Town?Scenario=Scenario_Hideout_Checkpoint_Security -Rcon -RconPassword=abc" };
                if (ServerPlanner.RconFor(portless, "").Port != 27015) fail("own options without an RCON port: the launcher tries port " + ServerPlanner.RconFor(portless, "").Port);
                // Own options naming their map cycle: that file is the one shown and edited.
                string srv = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-cyc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    System.IO.Directory.CreateDirectory(srv);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(srv, "InsurgencyServer.exe"), "");
                    portless.ServerOwnArgs += " -MapCycle=DynCycle";
                    string cyc = ServerPlanner.MapCyclePath(portless, ServerInstall.At(srv));
                    if (cyc == null || !cyc.EndsWith("DynCycle.txt", StringComparison.OrdinalIgnoreCase)) fail("own options with -MapCycle=DynCycle: the page edits " + cyc);
                }
                finally { try { System.IO.Directory.Delete(srv, true); } catch { } }

                var odd = new CustomMapEntry { Label = "Odd", Level = "Farmhouse", Scenario = "Scenario_Farmhouse_Checkpoint_Security?MaxPlayers=1" };
                state.Settings.CustomMaps.Add(odd);
                try
                {
                    var q = Fresh(checkpoint);
                    q.CustomMapId = odd.Id;
                    if (LaunchPlanner.Build(q, state).IsValid) fail("a custom map entry with ? in its scenario launches: " + LaunchPlanner.Build(q, state).OpenCommand);
                }
                finally { state.Settings.CustomMaps.Remove(odd); }
            }

            // 27. (2026-10-02 audit) Ambush and Free For All: the screen shows the minimum players the launch sends (1), with bots
            //     or without, and 2 can be kept to wait for a second player (it showed 2, sent 1, and 2 could not be kept).
            foreach (var m in db.Modes.Where(x => !x.Coop && x.Defaults.ContainsKey("bBots") && int.TryParse(db.DefaultValue(x.Cls, "MinimumPlayers"), out var mp) && mp > 1))
            {
                var sc = state.AllScenarios.FirstOrDefault(x => state.ModeFor(x, false)?.Cls == m.Cls);
                if (sc == null) continue;
                foreach (bool bots in new[] { true, false })
                {
                    var p = Fresh(sc);
                    if (!bots) SetupEngine.SetRule(p, db, m.Cls, "bBots", "False");
                    var plan = LaunchPlanner.Build(p, state);
                    string shown = SetupEngine.Effective(p, db, m.Cls, "MinimumPlayers");
                    string sent = plan.Overrides.TryGetValue("MinimumPlayers", out var o) ? o : db.DefaultValue(m.Cls, "MinimumPlayers");
                    if (shown != "1" || sent != "1") fail(m.Name + (bots ? "" : " without bots") + ": minimum players shown " + shown + ", sent " + sent + " (want 1 and 1)");
                    SetupEngine.SetRule(p, db, m.Cls, "MinimumPlayers", "2");
                    plan = LaunchPlanner.Build(p, state);
                    sent = plan.Overrides.TryGetValue("MinimumPlayers", out var o2) ? o2 : db.DefaultValue(m.Cls, "MinimumPlayers");
                    if (SetupEngine.GetRule(p, m.Cls, "MinimumPlayers") != "2" || sent != "2") fail(m.Name + ": minimum players 2 is not kept or not sent (stored " + SetupEngine.GetRule(p, m.Cls, "MinimumPlayers") + ", sent " + sent + ")");
                }
            }

            // 28. (mod.io comment, 2026-10-03: "have it read all the game rules from my Game.ini") Your own Game.ini rules: a launch
            //     writes no rule or line of Play's into Game.ini (only the RCON section) and sends none (open command, map loads,
            //     after the load, ruleset); AI teammates and bots set in Game.ini get their player slots and no bSoloGame; the
            //     restart key is the same whatever Play has; the server's match keeps Play's rules.
            {
                string cls = state.ModeFor(checkpoint, false)?.Cls ?? "INSCheckpointGameMode";
                var p = Fresh(checkpoint);
                SetupEngine.SetRule(p, db, cls, "RoundTime", "777");
                SetupEngine.SetRule(p, db, cls, "FriendlyBotQuota", "6");
                p.LaunchRuleset = "RS_CompetitiveFirefight";
                p.CustomIniMode = "Append";
                p.CustomIniText = "[/Script/Insurgency.Mutator_X]\nA=1";
                p.ExtraUrlOptions = "?WinLimit=3";
                string mine = "[/Script/Insurgency." + cls + "]\r\nRoundTime=1200\r\nFriendlyBotQuota=1\r\nSomeOwnKey=5\r\n\r\n[/Script/Insurgency.Mutator_Y]\r\nB=2\r\n";
                var mineLines = UeIni.Split(mine).Where(l => l.Trim().Length > 0).ToList();
                bool keepSolo = state.Settings.SoloGameFlag, keepOwn = state.Settings.OwnRules;
                state.Settings.SoloGameFlag = true;
                try
                {
                    var ownPlan = LaunchPlanner.Build(p, state, true, mine);
                    var playPlan = LaunchPlanner.Build(p, state, false);
                    bool SendsRule(string url, LaunchPlan x) => LaunchPlanner.UrlOptions.Any(k => !x.ExtraOptionKeys.Contains(k) && (url ?? "").IndexOf("?" + k + "=", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!ownPlan.IsValid || !ownPlan.OwnRules || playPlan.OwnRules) fail("own Game.ini rules: the plans are wrong: " + ownPlan.Error + " own " + ownPlan.OwnRules + ", Play " + playPlan.OwnRules);
                    else
                    {
                        if (SendsRule(ownPlan.OpenCommand, ownPlan)) fail("own Game.ini rules: the open command sends rules: " + ownPlan.OpenCommand);
                        if (!ownPlan.OpenCommand.Contains("?WinLimit=3")) fail("own Game.ini rules: the player's extra URL option is gone: " + ownPlan.OpenCommand);
                        string resets = LaunchService.TravelResets(ownPlan);
                        if (SendsRule(resets, ownPlan)) fail("own Game.ini rules: a map load sets rules back: " + resets);
                        if (ownPlan.IniSections.Count > 0 || ownPlan.GameIniBlock.Length > 0 || ownPlan.LiveProperties.Count > 0 || ownPlan.Overrides.Count > 0 || ownPlan.StartRuleset != null
                            || ownPlan.AfterLoad.Any(a => a.StartsWith("gamemodeproperty", StringComparison.Ordinal)))
                            fail("own Game.ini rules: the plan still writes or sends rules");
                        if (LaunchPlanner.LiveResets(ownPlan, db, new[] { cls + "|RoundTime" }).Count > 0) fail("own Game.ini rules: a running game's rules are set back after the load");
                        if (playPlan.StartRuleset != "RS_CompetitiveFirefight" || playPlan.IniSections.Count == 0 || playPlan.Overrides.Count == 0) fail("Play's rules: the plan lost the rules, lines or ruleset");

                        // Game.ini: every line stays, nothing of Play's goes in, the RCON section does.
                        var settings = new AppSettings { RconPort = 27015, RconPassword = "abcdefghijklmnopqrstuvwx" };
                        string written = LaunchPlanner.GameIniForLaunch(mine, ownPlan, db, new List<string> { "/Script/Insurgency.Mutator_Y\nB" }, settings);
                        var lines = UeIni.Split(written);
                        foreach (var l in mineLines) if (!lines.Contains(l)) fail("own Game.ini rules: the line \"" + l + "\" was changed or removed");
                        if (written.Contains("RoundTime=777") || written.Contains("Mutator_X")) fail("own Game.ini rules: Play's rules or lines went into Game.ini:\n" + written);
                        if (!written.Contains("Password=" + settings.RconPassword)) fail("own Game.ini rules: no RCON section in Game.ini:\n" + written);
                        if (LaunchPlanner.MergeGameIni(mine, ownPlan, db, new[] { "/Script/Insurgency.Mutator_Y\nB" }) != mine) fail("own Game.ini rules: the merge changed Game.ini");
                        var keys = LaunchPlanner.RuleKeysIn(UeIni.Parse(mine), db);
                        if (!keys.Contains(cls + "|RoundTime") || keys.Any(k => k.EndsWith("|SomeOwnKey", StringComparison.Ordinal))) fail("own Game.ini rules: the rules the game reads at start are " + string.Join(", ", keys));

                        // Play's 5 AI teammates do not count; Game.ini's do (with its bots on), and they need room.
                        if (!ownPlan.OpenCommand.Contains("?bSoloGame=1")) fail("own Game.ini rules: Play's AI teammates kept bSoloGame out: " + ownPlan.OpenCommand);
                        if (playPlan.OpenCommand.Contains("bSoloGame")) fail("Play's rules: bSoloGame with AI teammates: " + playPlan.OpenCommand);
                        var withMates = LaunchPlanner.Build(p, state, true, mine.Replace("FriendlyBotQuota=1", "FriendlyBotQuota=6\r\nbBots=True"));
                        if (withMates.OpenCommand.Contains("bSoloGame") || withMates.PlayerSlots < 9 || !withMates.OpenCommand.Contains("?MaxPlayers=" + withMates.PlayerSlots.ToString(CultureInfo.InvariantCulture)))
                            fail("own Game.ini rules: Game.ini's AI teammates get no room, or bSoloGame keeps them out: " + withMates.OpenCommand);
                        if (versus != null)
                        {
                            string vcls = state.ModeFor(versus, false)?.Cls;
                            var vp = Fresh(versus);
                            var noBots = LaunchPlanner.Build(vp, state, true, "");
                            if (SendsRule(noBots.OpenCommand, noBots)) fail("own Game.ini rules: versus still sends the launcher's bots: " + noBots.OpenCommand);
                            var bots = LaunchPlanner.Build(vp, state, true, "[/Script/Insurgency." + vcls + "]\r\nbBots=True\r\nBotQuota=10\r\n");
                            if (bots.PlayerSlots < 22) fail("own Game.ini rules: Game.ini's 10 bots per team got " + bots.PlayerSlots + " player slots");
                        }

                        // Restarts: the same key whatever Play has, another one than Play's; a game started outside the launcher after
                        // a write with your own rules is not restarted for nothing.
                        var plainOwn = LaunchPlanner.Build(Fresh(checkpoint), state, true, mine);
                        if (ownPlan.RestartKey != plainOwn.RestartKey || ownPlan.RestartKey == playPlan.RestartKey || ownPlan.RestartKey == LaunchPlanner.Build(Fresh(checkpoint), state, false).RestartKey)
                            fail("own Game.ini rules: the restart key follows Play or equals Play's");
                        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                        var written0 = new AppSettings { LastRulesWriteUtc = t0, LastWrittenRulesHash = ownPlan.RestartKey, LastWrittenIniPart = LaunchPlanner.OwnRulesPart };
                        if (LaunchService.ActiveRestartKey(written0, t0.AddHours(1), db) != ownPlan.RestartKey) fail("own Game.ini rules: a game started outside the launcher would be restarted for nothing");

                        // Warnings: Play's rules are not used (said once), and nothing is said when Play has none.
                        if (ownPlan.Warnings.Count != plainOwn.Warnings.Count + 1) fail("own Game.ini rules: no word that Play's rules are not used (" + string.Join(" | ", ownPlan.Warnings) + ")");

                        // The setting picks the plan.
                        state.Settings.OwnRules = true;
                        if (!LaunchPlanner.Build(p, state).OwnRules) fail("own Game.ini rules: the setting is on but the plan has Play's rules");
                        state.Settings.OwnRules = false;
                        if (LaunchPlanner.Build(p, state).OwnRules || LaunchPlanner.Build(p, state).OpenCommand != playPlan.OpenCommand) fail("own Game.ini rules: the setting is off but the plan is not Play's");
                    }
                }
                finally { state.Settings.SoloGameFlag = keepSolo; state.Settings.OwnRules = keepOwn; }
            }

            // 16. Server types (the admin guide's example servers): each one sets up a valid match of its mode with its
            //     preset, a map cycle of installed official scenarios of that mode, and a server plan that starts.
            string fakeServer = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-fake-server-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(fakeServer);
                System.IO.File.WriteAllText(System.IO.Path.Combine(fakeServer, "InsurgencyServer.exe"), "");
                var inst = ServerInstall.At(fakeServer);
                string after = null;
                foreach (var t in ServerTypes.All)
                {
                    var p = Fresh(versus ?? checkpoint);
                    SetupEngine.Apply(p, state, night ?? realism);   // a preset from before: its name and what it changed must go too
                    p.Mutators.Add(own ?? "Hardcore");
                    SetupEngine.SetRule(p, db, "INSCheckpointGameMode", "RoundTime", "900");
                    string text = ServerTypes.Apply(p, state, t);
                    bool ownPreset = t.PlaylistKey != null || t.RulesetId != null;
                    if (!ownPreset && (p.RulesPresetName != null || p.PresetChanges != null)) fail(t.Name + ": the preset from before is still in use (" + p.RulesPresetName + ")");
                    if (ownPreset && (p.RulesPresetName == null || p.RulesPresetName == (night ?? realism).Name)) fail(t.Name + ": the preset in use is " + p.RulesPresetName);
                    if (text == null) { fail(t.Name + ": no scenario of its mode"); continue; }
                    var sc = SetupEngine.Scenario(p, state);
                    if (sc == null || state.ModeFor(sc, false)?.Cls != t.ModeCls) fail(t.Name + ": the match is " + sc?.Id + " (should be a " + t.ModeCls + " scenario)");
                    if (p.Hardcore != t.Hardcore) fail(t.Name + ": hardcore is " + p.Hardcore);
                    if (SetupEngine.GetRule(p, "INSCheckpointGameMode", "RoundTime") != null) fail(t.Name + ": a rule of the setup before stayed");
                    if (own != null && p.Mutators.Contains(own)) fail(t.Name + ": a mutator of the setup before stayed");
                    var pl = t.PlaylistKey == null ? null : db.Playlists.First(x => x.Key == t.PlaylistKey);
                    if (pl != null && pl.Mutators.Any(id => state.FindMutator(id) != null && !p.Mutators.Contains(state.FindMutator(id).Id, StringComparer.OrdinalIgnoreCase)))
                        fail(t.Name + ": its playlist's mutators are missing (" + string.Join(",", p.Mutators) + ")");
                    if (t.RulesetId != null && p.RulesPresetName == null) fail(t.Name + ": its official ruleset was not applied");
                    if (t.NoBots && LaunchPlanner.IsTrue(SetupEngine.Effective(p, db, t.ModeCls, "bBots"))) fail(t.Name + ": bots are on");
                    if (p.SetupName != null) fail(t.Name + ": the new setup still counts as a saved one");
                    var plan = LaunchPlanner.Build(p, state);
                    if (!plan.IsValid) fail(t.Name + ": the match does not launch: " + plan.Error);
                    foreach (bool atNight in new[] { false, true })
                    {
                        var cycle = ServerTypes.Cycle(state, t, atNight);
                        if (cycle.Count < (atNight ? 4 : 2)) fail(t.Name + ": only " + cycle.Count + " scenarios in the map cycle");
                        foreach (var e in cycle)
                        {
                            var s2 = state.AllScenarios.FirstOrDefault(x => x.Id == e.Scenario);
                            if (s2 == null || state.ModeFor(s2, false)?.Cls != t.ModeCls || s2.Source != ContentSource.Official) { fail(t.Name + ": map cycle entry " + e.Scenario); break; }
                            if ((e.Mode == "CheckpointHardcore") != t.Hardcore) { fail(t.Name + ": map cycle mode " + e.Mode); break; }
                        }
                        if (atNight != cycle.Any(e => e.Lighting == "Night")) fail(t.Name + ": night in the map cycle is " + !atNight);
                        var back = MapCycle.Parse(MapCycle.Render(cycle));
                        if (back.Count != cycle.Count || back.Any(e => e.Raw != null)) fail(t.Name + ": the map cycle does not read back");
                    }
                    var settings = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerVoteKick = true, ServerOfficialRules = t.Id == "coop" };
                    var sp = ServerPlanner.Build(plan, settings, inst, db, "");
                    if (!sp.IsValid) fail(t.Name + ": the server plan is not valid: " + sp.Error);
                    else
                    {
                        if (!ServerPlanner.VoteKickOn(sp.GameIni)) fail(t.Name + ": vote kick is not in the server's Game.ini");
                        if ((t.Id == "coop") != sp.Args.Contains("-ruleset=OfficialRules")) fail(t.Name + ": official rules on the command line: " + sp.CommandLine);
                        if (sp.Args.Count(a => a.StartsWith("-ruleset=")) > 1) fail(t.Name + ": two rulesets on the command line");
                    }
                    // Another type after this one gives that type alone (nothing of this one stays).
                    string print = SetupEngine.Fingerprint(p);
                    if (after != null && print == after && t != ServerTypes.All[0]) fail(t.Name + ": the same setup as the type before");
                    after = print;
                    var q = p.Clone("q");
                    var other = ServerTypes.All[(ServerTypes.All.IndexOf(t) + 1) % ServerTypes.All.Count];
                    ServerTypes.Apply(q, state, other);
                    var r = Fresh(checkpoint);
                    r.MapKey = q.MapKey;
                    ServerTypes.Apply(r, state, other);
                    if (SetupEngine.Fingerprint(q) != SetupEngine.Fingerprint(r)) fail(t.Name + " then " + other.Name + " is not " + other.Name + " alone:\n" + Diff(SetupEngine.Fingerprint(q), SetupEngine.Fingerprint(r)));
                }
                // A SteamCMD install stopped half way (its manifest not "fully installed") does not start; a finished one does.
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(fakeServer, "steamapps"));
                    string manifest = System.IO.Path.Combine(fakeServer, "steamapps", "appmanifest_581330.acf");
                    var match = LaunchPlanner.Build(Fresh(checkpoint), state);
                    var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx" };
                    System.IO.File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"581330\"\n\t\"StateFlags\"\t\t\"1026\"\n\t\"buildid\"\t\t\"0\"\n}\n");
                    var half = ServerInstall.At(fakeServer);
                    if (!half.Unfinished || !half.ManagedBySteamCmd) fail("a half-installed server is not seen as unfinished");
                    if (ServerPlanner.Build(match, set, half, db, "").IsValid) fail("a half-installed server would start");
                    System.IO.File.WriteAllText(manifest, "\"AppState\"\n{\n\t\"appid\"\t\t\"581330\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"buildid\"\t\t\"24065441\"\n}\n");
                    var done = ServerInstall.At(fakeServer);
                    if (done.Unfinished || done.BuildId != "24065441") fail("a finished SteamCMD install reads as unfinished or without its build: " + done.BuildId);
                    if (!ServerPlanner.Build(match, set, done, db, "").IsValid) fail("a finished SteamCMD install does not start");
                    System.IO.Directory.Delete(System.IO.Path.Combine(fakeServer, "steamapps"), true);
                }
                // 18. The server's own rules (mod.io #1865863, #1866898): its Game.ini keeps every line the admin wrote, the
                //     first URL and later map loads carry no rule option of the match (URL options beat Game.ini), and only
                //     RCON and vote kick are written. Own options mode works the same way. Game stats send their token.
                {
                    var p = Fresh(versus ?? checkpoint);
                    string cls = state.ModeFor(SetupEngine.Scenario(p, state), false)?.Cls ?? "INSCheckpointGameMode";
                    SetupEngine.SetRule(p, db, cls, "RoundTime", "777");
                    SetupEngine.SetRule(p, db, cls, "WinLimit", "9");
                    var match = LaunchPlanner.Build(p, state);
                    string admin = "[/Script/Insurgency." + cls + "]\r\nRoundTime=1200\r\nWinLimit=7\r\nbBots=False\r\n\r\n[/Script/Insurgency.INSCheckpointGameMode]\r\nFriendlyBotQuota=4\r\n";
                    var adminLines = UeIni.Split(admin).Where(l => l.Trim().Length > 0).ToList();
                    var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerVoteKick = true, ServerOwnRules = true };
                    bool HasRuleOption(string url) => LaunchPlanner.UrlOptions.Any(k => url.IndexOf("?" + k + "=", StringComparison.OrdinalIgnoreCase) >= 0);
                    foreach (bool ownArgs in new[] { false, true })
                    {
                        set.ServerUseOwnArgs = ownArgs;
                        set.ServerOwnArgs = ownArgs ? "Hideout?Scenario=Scenario_Hideout_Checkpoint_Security?MaxPlayers=8 -Port=27102 -QueryPort=27131 -log" : "";
                        string what = ownArgs ? "own options" : "own rules";
                        var sp = ServerPlanner.Build(match, set, inst, db, admin);
                        if (!sp.IsValid) { fail(what + ": " + sp.Error); continue; }
                        if (!sp.OwnRules) fail(what + ": the plan does not keep the server's rules");
                        var lines = UeIni.Split(sp.GameIni);
                        foreach (var l in adminLines) if (!lines.Contains(l)) fail(what + ": the admin's Game.ini line \"" + l + "\" was changed or removed");
                        if (sp.GameIni.Contains("777") || sp.GameIni.Contains("WinLimit=9")) fail(what + ": the match's rules went into the server's Game.ini");
                        if (!sp.GameIni.Contains("Password=abcdefghijklmnopqrstuvwx")) fail(what + ": no RCON section in the server's Game.ini");
                        if (!ownArgs && !ServerPlanner.VoteKickOn(sp.GameIni)) fail(what + ": vote kick is not in the server's Game.ini");
                        if (sp.Url == null || HasRuleOption(sp.Url)) fail(what + ": the server URL has the match's rule options: " + sp.Url);
                        else if (HasRuleOption(ServerService.TravelUrl(sp))) fail(what + ": a map load resets the server's rules: " + ServerService.TravelUrl(sp));
                    }
                    set.ServerUseOwnArgs = false;
                    set.ServerOwnRules = false;
                    var ours = ServerPlanner.Build(match, set, inst, db, admin);
                    if (ours.OwnRules || !ours.GameIni.Contains("RoundTime=777") || ours.GameIni.Contains("RoundTime=1200")) fail("launcher rules: the match's rules are not the server's:\n" + ours.GameIni);
                    if (!ours.Url.Contains("?RoundTime=777")) fail("launcher rules: the server URL lost the match's round time: " + ours.Url);

                    // B4: game stats need the token from gamestats.sandstorm.game ("-GameStatsToken= required for statistics collection").
                    set.ServerGameStats = true;
                    set.ServerGslt = "ABCDEF0123456789";
                    set.ServerGameStatsToken = " 1417264D1C6549CC95E10CA1E9BE8F09 ";
                    var stats = ServerPlanner.Build(match, set, inst, db, "");
                    if (!stats.Args.Contains("-GameStats") || !stats.Args.Contains("-GameStatsToken=1417264D1C6549CC95E10CA1E9BE8F09")) fail("game stats: " + stats.CommandLine);
                    if (stats.ShownCommandLine.Contains("1417264D1C6549CC95E10CA1E9BE8F09")) fail("the shown command line has the game stats token: " + stats.ShownCommandLine);
                    set.ServerGameStatsToken = "";
                    var noToken = ServerPlanner.Build(match, set, inst, db, "");
                    if (noToken.Args.Any(a => a.StartsWith("-GameStatsToken", StringComparison.Ordinal)) || !noToken.Warnings.Any(w => w.Contains("gamestats.sandstorm.game")))
                        fail("game stats without a token: no warning, or an empty token on the command line: " + noToken.CommandLine);
                    set.ServerGameStatsToken = "bad token";
                    if (ServerPlanner.Build(match, set, inst, db, "").IsValid) fail("a game stats token with a space starts the server");
                    set.ServerGameStats = false;
                    set.ServerGameStatsToken = "1417264D1C6549CC95E10CA1E9BE8F09";
                    if (ServerPlanner.Build(match, set, inst, db, "").Args.Any(a => a.StartsWith("-GameStatsToken", StringComparison.Ordinal))) fail("game stats off but the token is passed");
                }
                // Mods since game update 1.20: -Mods with -SecurityCode (the waiting code once, else none), no Mods.txt, and the
                // server's mod.io login decides the warnings.
                {
                    var match = LaunchPlanner.Build(Fresh(checkpoint), state);
                    var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerModsEnabled = true, ServerMods = "1457355" };
                    var plain = ServerPlanner.Build(match, set, inst, db, "");
                    if (!plain.Args.Contains("-Mods") || !plain.Args.Contains("-SecurityCode=none") || plain.UsesCode || !plain.ModsOn) fail("mods on without a code: " + plain.CommandLine);
                    set.ServerModioCode = " 12345 ";
                    var coded = ServerPlanner.Build(match, set, inst, db, "");
                    if (!coded.Args.Contains("-SecurityCode=12345") || !coded.UsesCode) fail("mods on with a code: " + coded.CommandLine);
                    if (coded.ShownCommandLine.Contains("12345")) fail("the shown command line has the security code: " + coded.ShownCommandLine);
                    set.ServerModioCode = "1234";
                    if (!ServerPlanner.Build(match, set, inst, db, "").Args.Contains("-SecurityCode=none")) fail("a code that is not 5 digits was passed");
                    set.ServerModioCode = "";
                    var nobody = new ServerModioAccount();
                    if (!ServerPlanner.Build(match, set, inst, db, "", nobody).Warnings.Any(w => w.Contains("not logged in"))) fail("mods on, server not logged in: no warning");
                    set.ServerModioCode = "12345";
                    if (ServerPlanner.Build(match, set, inst, db, "", nobody).Warnings.Any(w => w.Contains("not logged in"))) fail("a waiting code still warns that the server is not logged in");
                    set.ServerModioCode = "";
                    var logged = new ServerModioAccount { LoggedIn = true, UserId = 7, Subscriptions = new List<long> { 1457355 } };
                    var ok = ServerPlanner.Build(match, set, inst, db, "", logged);
                    if (ok.Warnings.Any(w => w.Contains("mod.io"))) fail("mods on, logged in and subscribed: warned: " + string.Join(" | ", ok.Warnings));
                    set.ServerMods = "1457355\n98685";
                    if (!ServerPlanner.Build(match, set, inst, db, "", logged).Warnings.Any(w => w.Contains("98685") && !w.Contains("1457355"))) fail("a listed mod the account lacks is not named (or a subscribed one is)");
                    logged.SameAsGame = true;
                    if (!ServerPlanner.Build(match, set, inst, db, "", logged).Warnings.Any(w => w.Contains("same mod.io account"))) fail("the game's own account on the server: no warning");
                    if (ServerPlanner.Build(match, new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx" }, inst, db, "", nobody).Warnings.Any(w => w.Contains("mod.io")))
                        fail("mods off: warned about mod.io");
                    // Starting leaves Mods.txt alone and takes the old token section out (the guides for 1.20 say so).
                    var cfg = System.IO.Path.Combine(fakeServer, "Insurgency", "Saved", "Config", "WindowsServer");
                    var lists = System.IO.Path.Combine(fakeServer, "Insurgency", "Config", "Server");
                    System.IO.Directory.CreateDirectory(cfg);
                    System.IO.Directory.CreateDirectory(lists);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(lists, "Mods.txt"), "150867\r\n");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(cfg, "GameUserSettings.ini"), "[/Script/ModKit.ModIOClient]\r\nbHasUserAcceptedTerms=True\r\nAccessToken=tokentokentokentoken1234\r\n\r\n[/Script/Engine.GameUserSettings]\r\nbUseVSync=False\r\n");
                    new ServerService(() => set, () => inst).WriteFiles(ServerPlanner.Build(match, set, inst, db, ""));
                    if (System.IO.File.ReadAllText(System.IO.Path.Combine(lists, "Mods.txt")) != "150867\r\n") fail("starting the server changed Mods.txt");
                    string gus = System.IO.File.ReadAllText(System.IO.Path.Combine(cfg, "GameUserSettings.ini"));
                    if (gus.Contains("ModKit") || gus.Contains("AccessToken") || !gus.Contains("bUseVSync=False")) fail("the old token section was not taken out cleanly:\n" + gus);
                    foreach (var f in System.IO.Directory.GetFiles(cfg)) System.IO.File.Delete(f);
                    System.IO.File.Delete(System.IO.Path.Combine(lists, "Mods.txt"));
                }
                // The player's own options: used as they are, with a waiting code put in and the launcher's RCON only when they have none.
                {
                    var set = new AppSettings
                    {
                        ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerUseOwnArgs = true, ServerModioCode = "54321",
                        ServerOwnArgs = "InsurgencyServer.exe Oilfield?Scenario=Scenario_Refinery_Push_Security?MaxPlayers=20 -Port=27200 -QueryPort=27231\n-log -Mods -SecurityCode=none -hostname=\"Brett's\"",
                    };
                    var ownPlan = ServerPlanner.Build(null, set, inst, db, "");
                    if (!ownPlan.IsValid) fail("own options: " + ownPlan.Error);
                    else
                    {
                        if (ownPlan.CommandLine != "Oilfield?Scenario=Scenario_Refinery_Push_Security?MaxPlayers=20 -Port=27200 -QueryPort=27231 -log -Mods -SecurityCode=54321 -hostname=\"Brett's\"")
                            fail("own options changed: " + ownPlan.CommandLine);
                        if (!ownPlan.UsesCode || !ownPlan.ModsOn || ownPlan.GamePort != 27200 || ownPlan.QueryPort != 27231 || ownPlan.StartMap != "Oilfield") fail("own options read wrong: " + ownPlan.GamePort + "/" + ownPlan.QueryPort + "/" + ownPlan.StartMap);
                        if (!ownPlan.GameIni.Contains("[Rcon]") || !ownPlan.GameIni.Contains("Password=abcdefghijklmnopqrstuvwx")) fail("own options without RCON: the launcher's RCON is not in Game.ini");
                        if (ownPlan.AdminsText != null || ownPlan.MapCycleCopyFrom != null) fail("own options: the launcher's files are still written");
                    }
                    string theirs = "[Rcon]\r\nbEnabled=True\r\nPassword=theirs123\r\nListenPort=27020\r\n";
                    var keep = ServerPlanner.Build(null, set, inst, db, theirs);
                    if (keep.GameIni != theirs || keep.RconPort != 27020 || ServerPlanner.RconFor(set, theirs).Password != "theirs123") fail("own options: their own RCON in Game.ini was changed or not used");
                    set.ServerOwnArgs = "Farmhouse -Rcon -RconPassword=cmdpass -RconListenPort=28000";
                    var cmd = ServerPlanner.RconFor(set, theirs);
                    if (cmd.Port != 28000 || cmd.Password != "cmdpass" || !cmd.Own) fail("own options: RCON from the command line not used");
                    if (!ServerPlanner.Build(null, set, inst, db, "").ShownCommandLine.Contains("-RconPassword=<hidden>")) fail("own options: the RCON password is shown");
                    if (!ServerPlanner.Build(null, set, inst, db, "").Warnings.Any(w => w.Contains("-Mods"))) fail("a waiting code without -Mods: no note");
                    set.ServerModioCode = "";
                    set.ServerOwnArgs = "Farmhouse -Mods -SecurityCode=12345";
                    if (!ServerPlanner.Build(null, set, inst, db, "").Warnings.Any(w => w.Contains("only once"))) fail("a code kept in the player's options: no note that it works once");
                    set.ServerOwnArgs = "Farmhouse -Mods -SecurityCode=none";
                    if (ServerPlanner.Build(null, set, inst, db, "").Warnings.Any(w => w.Contains("only once"))) fail("-SecurityCode=none: noted as a used code");
                    set.ServerOwnArgs = "  ";
                    if (ServerPlanner.Build(null, set, inst, db, "").IsValid) fail("own options switched on but empty: would start");
                    set.ServerOwnArgs = "Farmhouse -Port=27200";
                    set.ServerRemote = true;
                    if (ServerPlanner.Build(null, set, inst, db, "").OwnArgs != null) fail("own options used for a server on another PC");
                    set.ServerRemote = false;
                    set.ServerUseOwnArgs = false;
                    if (ServerPlanner.RconFor(set, theirs).Own) fail("the launcher's own start used the player's RCON");
                }
                // B3 (mod.io #1865932, verified on the server 2026-10-02): the match's mutators go on the command line, where they
                // stay for every map and a map cycle entry's own ?Mutators= adds to them (in the start URL, an entry's list
                // replaced them); never in the URL too (loaded twice). A map load on the running server names only mutators
                // the server was not started with, and says which ones stay until a restart.
                {
                    var p = Fresh(checkpoint);
                    p.Mutators.Add(own ?? "Hardcore");
                    var match = LaunchPlanner.Build(p, state);
                    string cycleFile = System.IO.Path.Combine(fakeServer, "dyn-cycle.txt");
                    var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerUseMapCycle = true, ServerMapCycleFile = cycleFile };
                    System.IO.File.WriteAllText(cycleFile, "(Scenario=\"Scenario_Farmhouse_Checkpoint_Security\",\r\nLighting=\"Night\",Options=\"?Mutators=AllYouCanEat,\r\nHardcore\")\r\n");
                    var dyn = ServerPlanner.Build(match, set, inst, db, "");
                    if (!dyn.IsValid) fail("map cycle with a multi-line entry: " + dyn.Error);
                    else if (match.Mutators.Count == 0) fail("map cycle mutators: the match has no mutators to check with");
                    else
                    {
                        if (!dyn.Args.Contains("-mutators=" + string.Join(",", match.Mutators))) fail("the match's mutators are not on the server's command line: " + dyn.CommandLine);
                        if (dyn.Url.IndexOf("?Mutators=", StringComparison.OrdinalIgnoreCase) >= 0) fail("the match's mutators are in the start URL too (loaded twice): " + dyn.Url);
                        if (dyn.Warnings.Contains(T("The map cycle has no scenarios, so the server cycles through the game's versus scenarios."))) fail("a map cycle of one multi-line entry counts as having no scenarios");
                        string again = ServerService.TravelUrl(dyn);
                        if (System.Text.RegularExpressions.Regex.IsMatch(again, @"\?Mutators=[^?]")) fail("a map load names a mutator the server was started with: " + again);
                        string another = state.AllMutators.Select(m => m.Id).FirstOrDefault(id => !match.Mutators.Contains(id, StringComparer.OrdinalIgnoreCase));
                        if (another != null)
                        {
                            var q = Fresh(checkpoint);
                            q.Mutators.Add(another);
                            var plan2 = ServerPlanner.Build(LaunchPlanner.Build(q, state), set, inst, db, "");
                            string load = ServerService.TravelUrl(plan2, dyn.StartMutators);
                            if (!load.Contains("?Mutators=" + another)) fail("a map load does not add the match's new mutator: " + load);
                            if (!ServerService.StuckMutators(plan2, dyn.StartMutators).SequenceEqual(match.Mutators)) fail("the mutators that stay until a restart are not named");
                        }
                    }
                    // A chosen MapCycle.txt from another folder is copied under the launcher's own name, never over the server's.
                    string otherDir = System.IO.Path.Combine(fakeServer, "elsewhere");
                    System.IO.Directory.CreateDirectory(otherDir);
                    string chosen = System.IO.Path.Combine(otherDir, "MapCycle.txt");
                    System.IO.File.WriteAllText(chosen, "Scenario_Farmhouse_Checkpoint_Security\r\n");
                    set.ServerMapCycleFile = chosen;
                    var copied = ServerPlanner.Build(match, set, inst, db, "");
                    if (copied.MapCycleCopyFrom == null || copied.MapCycleName.Equals("MapCycle", StringComparison.OrdinalIgnoreCase)) fail("a chosen MapCycle.txt is copied over the server's own: " + copied.MapCycleName);
                }
                // The server's own Admins.txt: used as it is with no admins typed in the launcher, and admins only in the file
                // are named before the launcher's list replaces it.
                {
                    var m = LaunchPlanner.Build(Fresh(checkpoint), state);
                    System.IO.Directory.CreateDirectory(inst.ServerConfigDir);
                    string adminsFile = System.IO.Path.Combine(inst.ServerConfigDir, "Admins.txt");
                    System.IO.File.WriteAllText(adminsFile, "76561198000000001\r\n76561198000000002\r\n");
                    try
                    {
                        var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx" };
                        var theirs = ServerPlanner.Build(m, set, inst, db, "");
                        if (!theirs.Args.Contains("-AdminList=Admins") || theirs.AdminsText != null) fail("the server's own Admins.txt is not used, or is written over: " + theirs.CommandLine);
                        set.ServerAdmins = "76561198000000003";
                        var mine = ServerPlanner.Build(m, set, inst, db, "");
                        if (mine.AdminsText == null || !mine.Warnings.Any(w => w.Contains("Admins.txt"))) fail("the launcher's admins replace Admins.txt without naming the admins only in the file");
                    }
                    finally { System.IO.File.Delete(adminsFile); }
                }
                // The server's own rules leave the match's ruleset out too; official rules stay a choice of their own.
                {
                    var p = Fresh(checkpoint);
                    p.LaunchRuleset = "RS_Hardcore";
                    var m = LaunchPlanner.Build(p, state);
                    var set = new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerOwnRules = true };
                    if (ServerPlanner.Build(m, set, inst, db, "").Args.Any(a => a.StartsWith("-ruleset=", StringComparison.Ordinal))) fail("own rules, but the match's ruleset is on the command line");
                    set.ServerOfficialRules = true;
                    if (!ServerPlanner.Build(m, set, inst, db, "").Args.Contains("-ruleset=OfficialRules")) fail("own rules took official rules off");
                    // "Replace Game.ini" is for the player's game: the server's Game.ini keeps its other sections.
                    p.LaunchRuleset = null;
                    p.CustomIniMode = "Replace";
                    p.CustomIniText = "[/Script/Insurgency.Mutator_X]\nA=1";
                    var rep = ServerPlanner.Build(LaunchPlanner.Build(p, state), new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx" }, inst, db, "[/Script/Engine.GameSession]\r\nMaxPlayers=8\r\n");
                    if (!rep.GameIni.Contains("[/Script/Engine.GameSession]") || !rep.GameIni.Contains("A=1")) fail("Replace Game.ini cut the server's Game.ini down:\n" + rep.GameIni);
                    // A server on another PC: the match only, over RCON, without a server installed here; its own player count stays.
                    var remote = ServerPlanner.Build(LaunchPlanner.Build(Fresh(checkpoint), state), new AppSettings { ServerRemote = true, ServerMaxPlayers = 12 }, new ServerInstall(), db, "");
                    if (!remote.IsValid || remote.Url == null || remote.Url.Contains("MaxPlayers=") || remote.Args.Count > 0) fail("a server on another PC: " + (remote.Error ?? remote.Url));
                }
                // A map from a mod: the server starts on an official map until the mod is there, but loading the match on the
                // running server loads the mod's map (it loaded Farmhouse).
                {
                    var modMap = new ModInfo { Id = 4242, Name = "Map mod" };
                    modMap.Scenarios.Add(new ScenarioInfo { Id = "Scenario_ModTown_Checkpoint_Security", Level = "/Game/Mods/ModTown/Maps/ModTown", MapKey = "ModTown", GameModeClass = "INSCheckpointGameMode",
                                                           GameModePath = "/Script/Insurgency.INSCheckpointGameMode", Category = "Co-op", IsCoop = true, Source = ContentSource.Mod, ModId = 4242 });
                    state.Mods.Add(modMap);
                    try
                    {
                        state.Rebuild();
                        var p = Fresh(state.AllScenarios.First(s => s.Id == "Scenario_ModTown_Checkpoint_Security"));
                        var mp = LaunchPlanner.Build(p, state);
                        var sp = ServerPlanner.Build(mp, new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerModsEnabled = true }, inst, db, "");
                        if (!sp.IsValid) fail("a mod's map on the server: " + sp.Error);
                        else
                        {
                            if (!sp.Url.StartsWith("Farmhouse?", StringComparison.Ordinal)) fail("a mod's map: the server does not start on an official map first: " + sp.Url);
                            if (!ServerService.TravelUrl(sp).StartsWith(mp.Level + "?", StringComparison.Ordinal)) fail("a mod's map: loading the match on the running server loads " + ServerService.TravelUrl(sp));
                        }
                    }
                    finally { state.Mods.Remove(modMap); state.Rebuild(); }
                }
                // Official rules and the match's own ruleset: one ruleset, official, with a note.
                {
                    var p = Fresh(checkpoint);
                    p.LaunchRuleset = "RS_Hardcore";
                    var sp = ServerPlanner.Build(LaunchPlanner.Build(p, state), new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx", ServerOfficialRules = true }, inst, db, "");
                    if (sp.Args.Count(a => a.StartsWith("-ruleset=")) != 1 || !sp.Args.Contains("-ruleset=OfficialRules")) fail("official rules with a match ruleset: " + sp.CommandLine);
                    if (!sp.Warnings.Any(w => w.Contains("RS_Hardcore"))) fail("official rules with a match ruleset: no note that the match's ruleset was left out");
                    var sp2 = ServerPlanner.Build(LaunchPlanner.Build(p, state), new AppSettings { ServerRconPassword = "abcdefghijklmnopqrstuvwx" }, inst, db, "");
                    if (!sp2.Args.Contains("-ruleset=RS_Hardcore") || ServerPlanner.VoteKickOn(sp2.GameIni)) fail("without the server options: " + sp2.CommandLine);
                }
            }
            catch (Exception ex) { fail("server types: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
            finally { try { System.IO.Directory.Delete(fakeServer, true); } catch { } }

            // 7. Mods that are not on this PC (deleted, unsubscribed, still downloading) are not listed.
            string pub = Environment.GetEnvironmentVariable("PUBLIC");
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-mods-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string root = System.IO.Path.Combine(temp, "mod.io", GameInstall.ModioGameId);
                string mods = System.IO.Path.Combine(root, "mods");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "metadata"));
                string Mod(long id, int st, bool folder, bool pak)
                {
                    string dir = System.IO.Path.Combine(mods, id.ToString());
                    if (folder) System.IO.Directory.CreateDirectory(dir);
                    if (pak) System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "x.pak"), new byte[64]);
                    return "{\"ID\":" + id + ",\"PathOnDisk\":" + Json.Serialize(dir) + ",\"State\":" + st + ",\"Profile\":{\"name\":\"Mod " + id + "\"}}";
                }
                string json = "{\"Mods\":[" + string.Join(",", "{\"ID\":9,\"PathOnDisk\":\"C:\\\\bad|path\",\"State\":1,\"Profile\":{}}",
                    Mod(1, 1, true, true), Mod(2, 0, false, false), Mod(3, 1, true, false), Mod(4, 5, true, true)) + "],\"version\":1}";
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "metadata", "state.json"), json);
                Environment.SetEnvironmentVariable("PUBLIC", temp);
                var found = ModScanner.Scan(new GameInstall(), new string[0], System.IO.Path.Combine(temp, "cache"), t => { });
                string names = string.Join(",", found.Select(m => m.Id));
                if (names != "1") fail("installed mods are " + names + " (only mod 1 is on the PC and kept)");
                // A mod with an unusable folder in state.json (mod 9) must not cost the others their names.
                else if (found[0].Name != "Mod 1") fail("mod 1 is named \"" + found[0].Name + "\" (a bad state.json entry dropped the mod.io names)");
            }
            catch (Exception ex) { fail("mod list check: " + ex.Message); }
            finally
            {
                Environment.SetEnvironmentVariable("PUBLIC", pub);
                try { System.IO.Directory.Delete(temp, true); } catch { }
            }
        }

        private static string Diff(string got, string want)
        {
            var a = got.Split('\n');
            var b = want.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string x = i < a.Length ? a[i] : "", y = i < b.Length ? b[i] : "";
                if (x != y) return "  got  " + x + "\n  want " + y;
            }
            return "  (same)";
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
                // A map load over RCON (relative: old options stay) brings every URL rule to what the match wants: its own
                // value, or the mode's default (a switch turned off came back on, 2026-10-02 audit).
                if (mode != null)
                {
                    var opts = (url + LaunchService.TravelResets(plan)).Split('?').Skip(1).Select(o => o.Split(new[] { '=' }, 2))
                                                                    .GroupBy(o => o[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var key in LaunchPlanner.UrlOptions.Where(k => mode.Defaults.ContainsKey(k) && !plan.ExtraOptionKeys.Contains(k)))
                    {
                        string want = plan.Overrides.TryGetValue(key, out var ov) ? ov : mode.Defaults[key];
                        if (want == null) continue;
                        want = LaunchPlanner.IsTrue(want) ? "1" : want.Equals("False", StringComparison.OrdinalIgnoreCase) ? "0" : want;
                        string got = opts.TryGetValue(key, out var kv) && kv.Length > 1 ? kv[1] : null;
                        if (got != want) fail("a map load sends " + key + "=" + got + " but the match wants " + want + ": " + url + LaunchService.TravelResets(plan));
                    }
                }
                if (mode != null && mode.Coop)
                {
                    int mates = int.TryParse(SetupEngine.Effective(p, db, mode.Cls, "FriendlyBotQuota"), out var mt) ? SetupEngine.AiTeammates(mt) : 0;
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
                // Bots off: the team size means nothing and is not sent (the screen still shows what bots would get).
                if (key == "BotQuota" && !mode.Coop && !versusBots && !stored) continue;
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
