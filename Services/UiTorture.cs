using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using SandstormModLauncher.ViewModels;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// --data dir --ui-torture out.txt [steps] [seed]: the real main window (never shown) driven like a player would,
    /// through the view model's commands and properties, with its dialogs answered. After every step it checks that
    /// what the screen shows, what is saved and what would be launched all agree, that no binding broke and nothing
    /// was logged as an error. Use a copy of a data folder: profiles are created and deleted.
    /// </summary>
    public static class UiTorture
    {
        private sealed class BindingErrors : TraceListener
        {
            public readonly List<string> Lines = new List<string>();
            public override void Write(string message) { }
            public override void WriteLine(string message)
            {
                // A known, harmless WPF message: a recycled ListBoxItem looks for its list while the list is being rebuilt.
                if (message.Contains("Path=HorizontalContentAlignment") || message.Contains("Path=VerticalContentAlignment")) return;
                if (Lines.Count < 200) Lines.Add(message);
            }
        }

        /// <summary>
        /// What the screen shows: property values as of the last change notification. A value that changed without a
        /// notification would stay stale on screen; Stale() lists those.
        /// </summary>
        private sealed class Shadow
        {
            private readonly Dictionary<object, Dictionary<string, string>> seen = new Dictionary<object, Dictionary<string, string>>();
            private readonly Dictionary<object, string[]> props = new Dictionary<object, string[]>();

            private static string Read(object o, string prop)
            {
                var pi = o.GetType().GetProperty(prop);
                object v = pi?.GetValue(o);
                return v is double d ? d.ToString("0.####", CultureInfo.InvariantCulture) : v?.ToString() ?? "";
            }

            public void Watch(System.ComponentModel.INotifyPropertyChanged o, params string[] names)
            {
                if (props.ContainsKey(o)) return;
                props[o] = names;
                var map = seen[o] = new Dictionary<string, string>();
                foreach (var n in names) map[n] = Read(o, n);
                o.PropertyChanged += (sender, e) =>
                {
                    foreach (var n in names)
                        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == n) map[n] = Read(sender, n);
                };
            }

            public void Forget(object o) { props.Remove(o); seen.Remove(o); }

            public IEnumerable<string> Stale(object o, string label)
            {
                if (!props.TryGetValue(o, out var names)) yield break;
                foreach (var n in names)
                {
                    string now = Read(o, n);
                    if (seen[o][n] != now) yield return label + "." + n + " shows '" + seen[o][n] + "' but is '" + now + "'";
                }
            }
        }

        private static readonly string[] VmProps =
        {
            "Teammates", "SoloEnemies", "MinEnemies", "MaxEnemies", "BotQuota", "BotsEnabled", "AiDifficulty", "AiDifficultyLabel",
            "TeammatesChanged", "SoloEnemiesChanged", "MinEnemiesChanged", "MaxEnemiesChanged", "BotQuotaChanged", "BotQuotaHint", "TeammatesHint",
            "Night", "Day", "Hardcore", "CanHardcore", "MaxPlayers", "MutatorsEnabled", "ActivePresetName", "SquadKindLabel", "IsCoopMode", "IsVersusMode",
            "CurrentModeName", "RulesModeTitle", "SelectedScenarioId", "SelectedMapTitle", "SquadSummary", "MutatorSummary", "RulesSummary", "MapSummary",
            "ModeSummary", "PlanCommand", "PlanIni", "PlanAfterLoad", "PlanError", "CanLaunch", "RulesTabLabel", "RuleChangeCount", "HasNoPresets",
            "ActiveMutatorCount", "LaunchRuleset", "CustomIniMode", "CustomIniText", "ExtraUrlOptions", "GameModeOverride", "AfterLoadCommands",
            "EnableCheatsAfterLoad", "SelectedProfile", "PresetFilter", "PlayTab", "Page", "SelectedMutatorPreset"
        };

        private static readonly string[] Names = { "My setup", "my setup", "a/b", "a?b", "CON", "x.", "  ", "Lone Wolf", "ž č", "Very long " + new string('x', 120) };
        private static readonly string[] Texts = { "", "5", "-5", "1e99", "abc", "a b", "?x=1", "0.5", "True", "999999", "NaN", " 7 " };

        public static async Task<int> Run(int steps, int seed, Action<string> print)
        {
            var rnd = new Random(seed);
            var failures = new List<string>();
            var ops = new Dictionary<string, int>();
            string step = "start";
            void Fail(string msg) { failures.Add(step + ": " + msg); if (failures.Count <= 80) print("FAIL " + step + ": " + msg); }
            T Pick<T>(IList<T> list) => list[rnd.Next(list.Count)];

            var bindings = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            var win = new Views.MainWindow();
            var vm = (MainViewModel)win.DataContext;
            var root = (FrameworkElement)win.Content;
            win.Content = null;
            root.DataContext = vm;
            var host = new Border { Child = root, Background = win.Background };
            double w = 1536, h = 864;
            void Layout() { host.Width = w; host.Height = h; host.Measure(new Size(w, h)); host.Arrange(new Rect(0, 0, w, h)); host.UpdateLayout(); }
            async Task Pump()
            {
                for (int k = 0; k < 3; k++) await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
            // Runs a command and answers the dialogs it opens (answer = the button pressed, input = typed text).
            async Task Do(ICommand cmd, object param, string answer = null, string input = null)
            {
                if (cmd == null || !cmd.CanExecute(param)) return;
                cmd.Execute(param);
                for (int k = 0; k < 6; k++)
                {
                    await Pump();
                    if (!vm.DialogOpen) break;
                    if (input != null && vm.DialogHasInput) vm.DialogInput = input;
                    vm.DialogCommand.Execute(answer ?? vm.DialogPrimary);
                }
            }

            Layout();
            await vm.InitializeAsync();
            for (int k = 0; k < 600 && vm.Loading; k++) await Task.Delay(100);
            if (vm.DialogOpen) vm.DialogCommand.Execute(vm.DialogPrimary);
            await Pump();
            Layout();
            int errorsAtStart = AppLog.ErrorCount;
            var shadow = new Shadow();
            shadow.Watch(vm, VmProps);
            void WatchItems()
            {
                foreach (var r in vm.RuleItems) shadow.Watch(r, "Effective", "Changed", "Text", "Bool", "Number", "DisplayValue", "EnumValue");
                foreach (var m in vm.MutatorItems) shadow.Watch(m, "IsActive");
            }
            WatchItems();
            var state = vm.State;
            var db = state.Rules;
            bindings.Lines.Clear();

            for (int i = 1; i <= steps; i++)
            {
                int op = rnd.Next(26);
                string name = "op" + op;
                try
                {
                    switch (op)
                    {
                        case 0:
                        {
                            var m = Pick(vm.Maps);
                            name = "map " + m.Name;
                            await Do(vm.SelectMapCommand, m);
                            break;
                        }
                        case 1:
                        {
                            var modes = vm.ScenarioModeGroups.SelectMany(g => g.Modes).ToList();
                            if (modes.Count == 0) break;
                            var m = Pick(modes);
                            name = "mode chip " + m.Mode;
                            await Do(vm.SelectScenarioModeCommand, m);
                            break;
                        }
                        case 2:
                        {
                            if (vm.Scenarios.Count == 0) break;
                            var s = Pick(vm.Scenarios);
                            name = "scenario " + s.Id;
                            await Do(vm.SelectScenarioCommand, s);
                            break;
                        }
                        case 3: name = "night toggle"; vm.Night = !vm.Night; break;
                        case 4: name = "hardcore toggle"; vm.Hardcore = !vm.Hardcore; break;
                        case 5:
                        {
                            int d = rnd.Next(-70, 70);
                            name = "slots " + d;
                            if (rnd.Next(2) == 0) vm.MaxPlayers = d; else await Do(vm.StepCommand, "MaxPlayers:" + d);
                            break;
                        }
                        case 6:
                        {
                            string what = Pick(new[] { "Teammates", "SoloEnemies", "MinEnemies", "MaxEnemies", "BotQuota" });
                            int d = rnd.Next(-40, 41);
                            name = "stepper " + what + " " + d;
                            await Do(vm.StepCommand, what + ":" + d);
                            break;
                        }
                        case 7: name = "bots switch"; vm.BotsEnabled = !vm.BotsEnabled; break;
                        case 8:
                        {
                            double v = rnd.NextDouble() * 2 - 0.5;
                            name = "AI difficulty " + v.ToString("0.00", CultureInfo.InvariantCulture);
                            vm.AiDifficulty = v;
                            break;
                        }
                        case 9:
                        {
                            if (vm.SquadPresets.Count == 0) break;
                            var pr = Pick(vm.SquadPresets);
                            name = "squad preset " + pr.Name;
                            await Do(vm.ApplyPresetCommand, pr);
                            break;
                        }
                        case 10:
                        {
                            vm.PresetFilter = Pick(new[] { "Styles", "Official", "Playlists", "Saved", "Mine" });
                            await Pump();
                            if (vm.RulePresets.Count == 0) { name = "rules presets " + vm.PresetFilter + " (none)"; break; }
                            var pr = Pick(vm.RulePresets);
                            name = "rules preset " + vm.PresetFilter + " " + pr.Name;
                            await Do(vm.ApplyPresetCommand, pr);
                            break;
                        }
                        case 11:
                        {
                            var items = vm.RuleItems.Where(r => r.IsAvailable).ToList();
                            if (items.Count == 0) break;
                            var r = Pick(items);
                            if (r.IsBool) { name = "rule " + r.Key + " bool"; r.Bool = !r.Bool; }
                            else if (r.IsEnum && r.Options.Count > 0) { name = "rule " + r.Key + " enum"; r.EnumValue = Pick(r.Options); }
                            else if (rnd.Next(2) == 0) { string t = Pick(Texts); name = "rule " + r.Key + " text '" + t + "'"; r.Text = t; }
                            else { double v = r.Min + rnd.NextDouble() * (r.Max - r.Min) * 1.5; name = "rule " + r.Key + " slider " + v.ToString("0.##", CultureInfo.InvariantCulture); r.Number = v; }
                            break;
                        }
                        case 12:
                        {
                            var changed = vm.RuleItems.Where(r => r.Changed).ToList();
                            if (changed.Count == 0) break;
                            var r = Pick(changed);
                            name = "rule reset " + r.Key;
                            await Do(r.ResetCommand, null);
                            break;
                        }
                        case 13:
                        {
                            var m = Pick(vm.MutatorItems);
                            name = "mutator tick " + m.Id;
                            m.IsActive = !m.IsActive;
                            break;
                        }
                        case 14:
                        {
                            if (vm.ActiveMutators.Count == 0) break;
                            var a = Pick(vm.ActiveMutators);
                            int k = rnd.Next(4);
                            name = new[] { "mutator up ", "mutator down ", "mutator remove ", "mutators clear " }[k] + a.Id;
                            await Do(new[] { vm.MoveMutatorUpCommand, vm.MoveMutatorDownCommand, vm.RemoveMutatorCommand, vm.ClearMutatorsCommand }[k], a);
                            break;
                        }
                        case 15: name = "mutators switch"; vm.MutatorsEnabled = !vm.MutatorsEnabled; break;
                        case 16:
                        {
                            string n = Pick(Names);
                            name = "save setup '" + n + "'";
                            await Do(vm.SaveRulesPresetCommand, null, rnd.Next(5) == 0 ? "Cancel" : null, n);
                            break;
                        }
                        case 17:
                        {
                            vm.PresetFilter = "Saved";
                            await Pump();
                            if (vm.RulePresets.Count == 0) break;
                            var pr = Pick(vm.RulePresets);
                            name = "delete saved " + pr.Name;
                            await Do(vm.DeleteRulesPresetCommand, pr);
                            break;
                        }
                        case 18:
                        {
                            int k = rnd.Next(3);
                            name = new[] { "reset setup", "reset all rules", "reset mode" }[k];
                            await Do(new[] { vm.ResetSetupCommand, vm.ResetAllRulesCommand, vm.ResetModeCommand }[k], null);
                            break;
                        }
                        case 19:
                        {
                            name = "navigate";
                            vm.Page = Pick(new[] { "Play", "Settings", "Mods", "Playlists" });
                            vm.PlayTab = Pick(new[] { "Map", "Squad", "Rules", "Mods", "Live", "Advanced", "Playlists", "" });
                            if (vm.RuleCategories.Count > 0) vm.RuleCategory = Pick(vm.RuleCategories);
                            vm.RuleSearch = Pick(new[] { "", "", "bot", "zzz", "?" });
                            vm.MapFilter = Pick(new[] { "All", "Official", "Mods", "Custom" });
                            vm.MapSearch = Pick(new[] { "", "", "far", "zzz" });
                            vm.MutatorFilter = Pick(new[] { "All", "Official", "Mods", "Custom", "Active" });
                            break;
                        }
                        case 20:
                        {
                            int k = rnd.Next(5);
                            string n = Pick(Names);
                            name = new[] { "new profile ", "duplicate profile ", "rename profile ", "delete profile", "switch profile" }[k] + (k < 3 ? n : "");
                            if (k == 0) await Do(vm.NewProfileCommand, null, null, n);
                            else if (k == 1) await Do(vm.DuplicateProfileCommand, null, null, n);
                            else if (k == 2) await Do(vm.RenameProfileCommand, null, null, n);
                            else if (k == 3) { if (vm.ProfileNames.Count > 3) await Do(vm.DeleteProfileCommand, null); }
                            else vm.SelectedProfile = Pick(vm.ProfileNames);
                            break;
                        }
                        case 21:
                        {
                            name = "advanced fields";
                            vm.ExtraUrlOptions = Pick(new[] { "", "", "?x=1", "a b", "RoundTime=60" });
                            vm.GameModeOverride = Pick(new[] { "", "", "", "Checkpoint", "x y" });
                            vm.CustomIniMode = Pick(new[] { "Off", "Append", "Replace" });
                            vm.CustomIniText = Pick(new[] { "", "[/Script/Foo.Bar]\r\n+Arr=1\r\n", "garbage" });
                            vm.AfterLoadCommands = Pick(new[] { "", "slomo 1" });
                            vm.EnableCheatsAfterLoad = rnd.Next(2) == 0;
                            vm.LaunchRuleset = Pick(vm.OfficialRulesetChoices);
                            break;
                        }
                        case 22:
                        {
                            w = rnd.Next(700, 2000); h = rnd.Next(450, 1200);
                            double scale = Pick(new[] { 0.9, 1.0, 1.1, 1.25, 1.4, 1.5 });
                            name = "layout " + w + "x" + h + " at " + scale;
                            vm.UiScale = scale;
                            vm.SoloGameFlag = rnd.Next(2) == 0;
                            vm.ApplyLiveRules = rnd.Next(4) > 0;
                            break;
                        }
                        case 23: name = "random mission"; await Do(vm.RandomMissionCommand, null); break;
                        case 24:
                        {
                            if (rnd.Next(2) == 0) { name = "save mutator preset"; await Do(vm.SaveMutatorPresetCommand, null, null, Pick(Names)); }
                            else if (vm.MutatorPresetNames.Count > 0) { name = "load mutator preset"; vm.SelectedMutatorPreset = Pick(vm.MutatorPresetNames); }
                            break;
                        }
                        default:
                        {
                            // A saved setup is loaded back exactly.
                            vm.PresetFilter = "Saved";
                            await Pump();
                            var saved = vm.RulePresets.ToList();
                            if (saved.Count == 0) break;
                            var pr = Pick(saved);
                            name = "load saved " + pr.Name;
                            var rp = ((Preset)pr.Source).Saved;
                            await Do(vm.ApplyPresetCommand, pr);
                            var p = vm.Profile;
                            if (Canon(p.Rules) != Canon(Cleaned(rp.Rules, db))) Fail(name + ": rules differ from the saved setup\n  saved " + Canon(rp.Rules) + "\n  now   " + Canon(p.Rules));
                            if (rp.ScenarioId != null && !string.Equals(p.ScenarioId, rp.ScenarioId, StringComparison.OrdinalIgnoreCase) && state.AllScenarios.Any(s => s.Id == rp.ScenarioId))
                                Fail(name + ": scenario " + p.ScenarioId + " instead of " + rp.ScenarioId);
                            if (rp.Lighting != null && p.Lighting != rp.Lighting) Fail(name + ": lighting " + p.Lighting + " instead of " + rp.Lighting);
                            break;
                        }
                    }
                    step = i + " (" + name + ")";
                    await Pump();
                    Layout();
                    await Pump();
                    Check(vm, state, Fail);
                    WatchItems();
                    foreach (var x in shadow.Stale(vm, "screen").Take(4)) Fail(x);
                    foreach (var r in vm.RuleItems) foreach (var x in shadow.Stale(r, "rule " + r.Key).Take(2)) Fail(x);
                    foreach (var m in vm.MutatorItems) foreach (var x in shadow.Stale(m, "mutator " + m.Id)) Fail(x);
                    if (AppLog.ErrorCount != errorsAtStart) { Fail("error logged: " + AppLog.LastError); errorsAtStart = AppLog.ErrorCount; }
                    if (bindings.Lines.Count > 0) { foreach (var l in bindings.Lines.Distinct().Take(3)) Fail("binding: " + l); bindings.Lines.Clear(); }
                }
                catch (Exception ex)
                {
                    step = i + " (" + name + ")";
                    Fail("EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                }
                string opName = name.Split(' ')[0];
                ops[opName] = ops.TryGetValue(opName, out var c) ? c + 1 : 1;
            }
            vm.SaveNow();
            print("UI torture: " + steps + " steps (seed " + seed + "): " + string.Join(", ", ops.OrderBy(o => o.Key).Select(o => o.Key + " " + o.Value)));
            print(failures.Count == 0 ? "ALL CHECKS PASSED" : failures.Count + " FAILURES");
            return failures.Count == 0 ? 0 : 1;
        }

        /// <summary>What the screen shows, what is stored and what would be launched agree.</summary>
        private static void Check(MainViewModel vm, AppState state, Action<string> fail)
        {
            var p = vm.Profile;
            var db = state.Rules;
            foreach (var x in SetupEngine.Problems(p, state)) fail("stored setup: " + x);

            if (vm.SelectedScenario != null && !vm.SelectedScenario.Id.Equals(p.ScenarioId ?? "", StringComparison.OrdinalIgnoreCase))
                fail("screen shows scenario " + vm.SelectedScenario.Id + " but the profile has " + p.ScenarioId);

            // The plan on screen is the plan of the stored setup (never stale).
            var fresh = LaunchPlanner.Build(p, state);
            var shown = vm.CurrentPlan;
            if (shown == null) fail("no plan on screen");
            else
            {
                if (shown.OpenCommand != fresh.OpenCommand) fail("screen plan is stale:\n  shown " + shown.OpenCommand + "\n  real  " + fresh.OpenCommand);
                if (shown.GameIniBlock != fresh.GameIniBlock) fail("screen Game.ini block is stale");
                if (string.Join("|", shown.AfterLoad) != string.Join("|", fresh.AfterLoad)) fail("screen after-load commands are stale");
            }

            var mode = SetupEngine.CurrentMode(p, state);
            if ((vm.CurrentMode?.Cls) != (mode?.Cls)) fail("screen mode " + vm.CurrentMode?.Cls + " but the profile's scenario is " + mode?.Cls);
            if (mode != null)
            {
                int Eff(string key) => int.TryParse(SetupEngine.Effective(p, db, mode.Cls, key), out var v) ? v : 0;
                if (mode.Coop)
                {
                    if (mode.Defaults.ContainsKey("FriendlyBotQuota") && vm.Teammates != Eff("FriendlyBotQuota")) fail("teammates shown " + vm.Teammates + ", stored " + Eff("FriendlyBotQuota"));
                    if (mode.Defaults.ContainsKey("SoloEnemies") && vm.SoloEnemies != Eff("SoloEnemies")) fail("solo enemies shown " + vm.SoloEnemies + ", stored " + Eff("SoloEnemies"));
                    string ai = SetupEngine.Effective(p, db, mode.Cls, "AIDifficulty");
                    if (ai != null && Math.Abs(vm.AiDifficulty - double.Parse(ai, CultureInfo.InvariantCulture)) > 0.001) fail("AI difficulty shown " + vm.AiDifficulty + ", stored " + ai);
                }
                else
                {
                    if (mode.Defaults.ContainsKey("bBots") && vm.BotsEnabled != LaunchPlanner.IsTrue(SetupEngine.Effective(p, db, mode.Cls, "bBots"))) fail("bots switch shows " + vm.BotsEnabled + ", stored " + SetupEngine.Effective(p, db, mode.Cls, "bBots"));
                    if (mode.Defaults.ContainsKey("BotQuota") && vm.BotQuota != Eff("BotQuota")) fail("team size shown " + vm.BotQuota + ", stored " + Eff("BotQuota"));
                    string ai = SetupEngine.GetRule(p, "*", "AIDifficulty") ?? "0.5";
                    if (Math.Abs(vm.AiDifficulty - double.Parse(ai, CultureInfo.InvariantCulture)) > 0.001) fail("versus AI difficulty shown " + vm.AiDifficulty + ", stored " + ai);
                }
                foreach (var r in vm.RuleItems)
                {
                    if (r.ModeCls != mode.Cls) { fail("rules list is for " + r.ModeCls + " but the scenario is " + mode.Cls); break; }
                    if (!r.IsAvailable) continue;
                    string want = SetupEngine.Effective(p, db, mode.Cls, r.Key);
                    if (!LaunchPlanner.Same(r.Effective ?? "", want ?? "", r.Prop)) fail("rule " + r.Key + " shows " + r.Effective + ", stored " + want);
                }
                string kind = mode.Coop ? "Co-op" : "Versus";
                if (vm.SquadPresets.Any(x => x.Tag != kind)) fail("squad presets are not the " + kind + " ones");
            }

            var active = vm.ActiveMutators.Select(a => a.Id).ToList();
            if (string.Join(",", active) != string.Join(",", p.Mutators)) fail("mutator list shows " + string.Join(",", active) + ", stored " + string.Join(",", p.Mutators));
            var ticked = vm.MutatorItems.Where(m => m.IsActive).Select(m => m.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var known = p.Mutators.Where(id => vm.MutatorItems.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            if (!ticked.SequenceEqual(known, StringComparer.OrdinalIgnoreCase)) fail("ticked mutators " + string.Join(",", ticked) + " differ from stored " + string.Join(",", known));
            if (vm.ActivePresetName != SetupEngine.PresetLabel(p)) fail("preset name shown " + vm.ActivePresetName + ", stored " + p.RulesPresetName);
            if (p.RulesPresetName != null && p.PresetCheck != null && !vm.ActivePresetName.EndsWith("(changed)") && SetupEngine.PresetCheck(p, p.PresetCheck.StartsWith("S|")) != p.PresetCheck)
                fail("setup changed after " + p.RulesPresetName + " but the summary does not say so");
            if (vm.PresetFilter == "Saved" && vm.RulePresets.Count != state.Settings.RulesPresets.Count) fail("Saved tab shows " + vm.RulePresets.Count + " of " + state.Settings.RulesPresets.Count + " saved setups");
            if (vm.MutatorsEnabled != p.MutatorsEnabled || vm.Night != (p.Lighting == "Night") || vm.MaxPlayers != p.MaxPlayers) fail("conditions on screen differ from the profile");

            // Profiles: the list matches the store, every profile has its own file, and the files hold what is in memory.
            if (!vm.ProfileNames.OrderBy(x => x).SequenceEqual(state.Store.Profiles.Select(x => x.Name).OrderBy(x => x))) fail("profile list differs from the stored profiles");
            if (vm.SelectedProfile != p.Name) fail("selected profile " + vm.SelectedProfile + " but the active one is " + p.Name);
            var files = state.Store.Profiles.Select(x => Store.ProfilePath(x.Name)).ToList();
            if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) fail("two profiles share a file");
            vm.SaveNow();
            foreach (var x in state.Store.Profiles)
            {
                string path = Store.ProfilePath(x.Name);
                if (!File.Exists(path)) { fail("profile " + x.Name + " has no file"); continue; }
                var back = Json.Deserialize<Profile>(File.ReadAllText(path));
                if (back == null || back.Name != x.Name || Canon(back.Rules) != Canon(x.Rules) || string.Join(",", back.Mutators) != string.Join(",", x.Mutators))
                    fail("profile file of " + x.Name + " differs from memory");
            }
            var onDisk = Directory.GetFiles(AppPaths.ProfilesDir, "*.json").Length;
            if (onDisk != state.Store.Profiles.Count) fail(onDisk + " profile files for " + state.Store.Profiles.Count + " profiles");
        }

        private static Dictionary<string, Dictionary<string, string>> Cleaned(Dictionary<string, Dictionary<string, string>> rules, RulesDb db)
        {
            var copy = rules.ToDictionary(k => k.Key, k => new Dictionary<string, string>(k.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            SetupEngine.CleanRules(copy, db);
            return copy;
        }

        private static string Canon(Dictionary<string, Dictionary<string, string>> rules) =>
            string.Join(";", rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                  .Select(m => m.Key + ":" + string.Join(",", m.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value))));
    }
}
