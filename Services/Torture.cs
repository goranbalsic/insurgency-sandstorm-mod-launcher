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
                string json = "{\"Mods\":[" + string.Join(",", Mod(1, 1, true, true), Mod(2, 0, false, false), Mod(3, 1, true, false), Mod(4, 5, true, true)) + "],\"version\":1}";
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "metadata", "state.json"), json);
                Environment.SetEnvironmentVariable("PUBLIC", temp);
                var found = ModScanner.Scan(new GameInstall(), new string[0], System.IO.Path.Combine(temp, "cache"), t => { });
                string names = string.Join(",", found.Select(m => m.Id));
                if (names != "1") fail("installed mods are " + names + " (only mod 1 is on the PC and kept)");
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
                // Bots off: the team size means nothing and is not sent (the screen still shows what bots would get).
                if (key == "BotQuota" && !mode.Coop && !versusBots && !stored) continue;
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
