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
using static SandstormModLauncher.Core.Loc;

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
            "EnableCheatsAfterLoad", "SelectedSetup", "SetupState", "SetupChanged", "PresetFilter", "PresetSearch", "PresetCountText", "PlayTab", "Page",
            "ServerName", "ServerJoinPassword", "ServerMaxPlayersText", "ServerPortText", "ServerQueryPortText", "ServerRconPortText",
            "ServerRconFromNetwork", "ServerShowLog", "ServerCheats", "ServerGslt", "ServerGameStats", "ServerExtraArgs", "ServerAdmins",
            "ServerModsEnabled", "ServerModIds", "ServerUseMapCycle", "ServerRemote", "ServerLocal", "ServerRemoteHost", "ServerRemotePortText",
            "ServerCommandLine", "ServerPlanError", "ServerMatchSummary", "ServerMatchMods", "MapCycleFile", "MapCycleIsDefault", "MapCycleCount",
            "ServerRconAddress", "CanStartServer", "PlayTarget", "PlaysOnServer", "MainActionTitle", "MainActionTip", "CanMainAction",
            "ServerUseOwnArgs", "ServerUsesLauncherArgs", "ServerOwnArgs", "ServerModsOn", "ServerModioEmail", "ServerModioCode", "ModioCodeHint",
            "ModioAccountText", "ModioAccountKind", "ServerModsListText", "HasOtherSubs", "ModSyncText", "ModioText", "OwnArgsNote"
        };

        private static readonly string[] Names = { "My setup", "my setup", "a/b", "a?b", "CON", "x.", "  ", "Lone Wolf", "ž č", "Very long " + new string('x', 120), "Settings" };   // (a name that is also a text of the launcher)
        private static readonly string[] Texts = { "", "5", "-5", "1e99", "abc", "a b", "?x=1", "0.5", "True", "999999", "NaN", " 7 " };

        /// <summary>
        /// The texts written in the pages themselves (not bound values): text blocks, their pieces, and captions.
        /// With <paramref name="all"/>, every text shown, bound values from the view model too.
        /// </summary>
        private static IEnumerable<(string text, string where)> PageLiterals(DependencyObject root, bool all = false)
        {
            bool Literal(DependencyObject d, DependencyProperty p)
            {
                if (all) return true;
                var src = DependencyPropertyHelper.GetValueSource(d, p);
                return !src.IsExpression && (src.BaseValueSource == BaseValueSource.Local || src.BaseValueSource == BaseValueSource.ParentTemplate);
            }
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is UIElement ui && ui.Visibility != Visibility.Visible) continue;   // hidden: not shown now
                if (child is TextBlock tb && (all || !(tb.TemplatedParent is ContentPresenter cp && cp.Content is string && cp.ContentTemplate == null && cp.ContentTemplateSelector == null)))
                {
                    if (tb.Inlines.Count > 1) { foreach (var run in tb.Inlines.OfType<System.Windows.Documents.Run>()) if (Literal(run, System.Windows.Documents.Run.TextProperty) && !string.IsNullOrWhiteSpace(run.Text)) yield return (run.Text, Where(tb)); }
                    else if (Literal(tb, TextBlock.TextProperty) && !string.IsNullOrWhiteSpace(tb.Text)) yield return (tb.Text, Where(tb));
                }
                else if (child is ContentControl cc && cc.Content is string s && Literal(cc, ContentControl.ContentProperty) && s.Trim().Length > 0) yield return (s, Where(cc));
                foreach (var t in PageLiterals(child, all)) yield return t;
            }
        }

        /// <summary>Where an element is: its type, the template it comes from and the item it shows.</summary>
        private static string Where(FrameworkElement e) =>
            e.GetType().Name + (e.TemplatedParent != null ? " in " + e.TemplatedParent.GetType().Name : "") + (e.DataContext != null ? " for " + e.DataContext.GetType().Name : "");

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
                await Answer(answer, input);
            }
            // Answers the dialogs a step opened (answer = the button pressed, input = typed text); the first dialog gets
            // the answer, a dialog after it (a name to save under) its primary button.
            async Task Answer(string answer, string input)
            {
                for (int k = 0; k < 6; k++)
                {
                    await Pump();
                    if (!vm.DialogOpen) break;
                    if (input != null && vm.DialogHasInput) vm.DialogInput = input;
                    // The caption as shown (translated) when the answer is one of this dialog's buttons.
                    string shown = answer == null ? vm.DialogPrimary : Loc.T(answer);
                    if (answer != null && shown != vm.DialogPrimary && shown != vm.DialogSecondary && shown != vm.DialogTertiary) shown = vm.DialogPrimary;
                    vm.DialogCommand.Execute(shown);
                    answer = null;
                }
            }

            Layout();
            await vm.InitializeAsync();
            // The map cycle of this test lives in its data folder, never in the real server's.
            TortureCycle = Path.Combine(AppPaths.DataDir, "torture-MapCycle.txt");
            if (File.Exists(TortureCycle)) File.Delete(TortureCycle);
            vm.UseMapCycleFile(TortureCycle);
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

            // Fixed first: the custom map in use is deleted after the setup was saved on it. The screen falls back to
            // another map, and the bar has to say the setup changed (it did not, 2026-09-29).
            {
                step = "custom map in use deleted";
                // A scenario on another map than the one picked now: the entry keeps that map underneath.
                var sc = state.AllScenarios.First(s => s.GameModeClass == "INSCheckpointGameMode" && !string.Equals(s.MapKey, vm.Profile.MapKey, StringComparison.OrdinalIgnoreCase)
                                                        && state.Maps.Any(m => m.Scenarios.Contains(s)));
                vm.AddCustomMapCommand.Execute(null);
                vm.EditMapLabel = "UI check map"; vm.EditMapLevel = sc.Level; vm.EditMapScenario = sc.Id; vm.EditMapMode = "";
                vm.SaveCustomMapCommand.Execute(null);
                await Pump();
                if (vm.SelectedMap?.Custom == null) Fail("the new custom map is not picked");
                await Do(vm.SaveSetupAsCommand, null, null, "UI check");
                if (vm.SetupChanged) Fail("just saved, but shown as changed");
                await Do(vm.DeleteCustomMapCommand, vm.SelectedMap);
                await Pump();
                if (vm.Profile.CustomMapId != null) Fail("the deleted custom map is still in the setup");
                // The setup stays on the entry's scenario, now on the map that scenario is on.
                var owner = state.Maps.First(m => m.Scenarios.Contains(sc));
                if (vm.Profile.ScenarioId != sc.Id || vm.SelectedMap?.Info.Key != owner.Key)
                    Fail("after deleting the custom map in use the setup is on " + vm.SelectedMap?.Info.Key + " / " + vm.Profile.ScenarioId + " (should be " + owner.Key + " / " + sc.Id + ")");
                Check(vm, state, Fail);
                // Cleaned up: the saved setup of this check goes again.
                await Do(vm.DeleteSetupCommand, null);

                // Two playlists called LMGOnly (co-op and versus): only the one applied is marked in use.
                step = "same-name presets";
                vm.PresetFilter = "Playlists";
                vm.PresetSearch = "LMGOnly";
                await Pump();
                var lmg = vm.RulePresets.Where(x => ((Preset)x.Source).Name == "LMGOnly").ToList();
                if (lmg.Count < 2) Fail("the two LMGOnly playlists are not both listed");
                else
                {
                    await Do(vm.ApplyPresetCommand, lmg[0]);
                    await Pump();
                    Check(vm, state, Fail);
                }
                vm.PresetSearch = "";

                // AI teammates: 2 on the screen is FriendlyBotQuota=3 in the launch (the game counts you; 2 gave one AI, 2026-10-01).
                step = "AI teammates";
                var cpMap = vm.Maps.FirstOrDefault(m => m.Custom == null && m.Info.Scenarios.Any(s => s.GameModeClass == "INSCheckpointGameMode"));
                if (cpMap == null) Fail("no map with a checkpoint scenario");
                else
                {
                    await Do(vm.SelectMapCommand, cpMap);
                    var cp = vm.Scenarios.First(s => s.Info.GameModeClass == "INSCheckpointGameMode");
                    await Do(vm.SelectScenarioCommand, cp);
                    foreach (int n in new[] { 2, 0, 1, 32 })
                    {
                        vm.Teammates = n;
                        await Pump();
                        string sent = LaunchPlanner.Build(vm.Profile, state).Overrides.TryGetValue("FriendlyBotQuota", out var fq) ? fq : db.DefaultValue("INSCheckpointGameMode", "FriendlyBotQuota");
                        string want = n == 0 ? "0" : (n + 1).ToString(CultureInfo.InvariantCulture);
                        if (vm.Teammates != n || sent != want) Fail(n + " AI teammates set: the screen shows " + vm.Teammates + ", the launch sends FriendlyBotQuota=" + sent + " (want " + want + ")");
                    }
                    await Do(vm.StepCommand, "Teammates:-1");
                    if (vm.Teammates != 31) Fail("one less than 32 AI teammates shows " + vm.Teammates);
                    Check(vm, state, Fail);
                }
                step = "start";
            }

            for (int i = 1; i <= steps; i++)
            {
                int op = rnd.Next(32);
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
                            vm.PresetFilter = Pick(new[] { "Styles", "Official", "Playlists", "Playlists", "Saved", "Mine" });
                            vm.PresetSearch = Pick(new[] { "", "", "", "", "chad", "hard", "frenzy", "zzz", "night" });
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
                            bool asNew = rnd.Next(2) == 0;
                            name = (asNew ? "save setup as '" : "save setup '") + n + "'";
                            string saveAnswer = rnd.Next(5) == 0 ? "Cancel" : null;
                            string before = vm.SelectedSetup;
                            int countBefore = state.Settings.RulesPresets.Count;
                            await Do(asNew ? vm.SaveSetupAsCommand : vm.SaveSetupCommand, null, saveAnswer, n);
                            bool saved = saveAnswer == null && (before != null && !asNew || n.Trim().Length > 0);
                            if (saved)
                            {
                                string expect = !asNew && before != null ? before : state.Settings.RulesPresets.FirstOrDefault(r => r.Name.Equals(n.Trim().Length > 60 ? n.Trim().Substring(0, 60).TrimEnd() : n.Trim(), StringComparison.OrdinalIgnoreCase))?.Name;
                                if (expect == null) Fail(name + ": nothing was saved");
                                else if (vm.SelectedSetup != expect) Fail(name + ": saved, but the bar shows " + (vm.SelectedSetup ?? "nothing") + " instead of " + expect);
                                else if (vm.SetupChanged) Fail(name + ": just saved, but shown as changed");
                            }
                            break;
                        }
                        case 17:
                        {
                            name = "delete setup " + (vm.SelectedSetup ?? "(none)");
                            await Do(vm.DeleteSetupCommand, null, rnd.Next(4) == 0 ? "Cancel" : null);
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
                            vm.Page = Pick(new[] { "Play", "Settings", "Mods", "Playlists", "Server", "Live" });
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
                            int k = rnd.Next(4);
                            string n = Pick(Names);
                            name = new[] { "rename setup ", "new setup", "save setup as ", "undo setup changes" }[k] + (k == 0 || k == 2 ? n : "");
                            if (k == 0) await Do(vm.RenameSetupCommand, null, null, n);
                            else if (k == 1) await Do(vm.NewSetupCommand, null, rnd.Next(4) == 0 ? "Cancel" : null);
                            else if (k == 2) await Do(vm.SaveSetupAsCommand, null, null, n);
                            else
                            {
                                var loadedSetup = state.Settings.RulesPresets.FirstOrDefault(r => r.Name == vm.SelectedSetup);
                                bool could = vm.RevertSetupCommand.CanExecute(null);
                                string undoAnswer = rnd.Next(4) == 0 ? "Cancel" : null;
                                string beforeUndo = SetupEngine.Fingerprint(vm.Profile);
                                await Do(vm.RevertSetupCommand, null, undoAnswer);
                                if (could && undoAnswer == null && loadedSetup?.Setup != null)
                                {
                                    var want = new Models.Profile();
                                    SetupEngine.CopySetup(loadedSetup.Setup, want);
                                    want.Mutators = want.Mutators.Where(id => state.FindMutator(id) != null).ToList();
                                    SetupEngine.CleanRules(want.Rules, db);
                                    SetupEngine.AlignMap(want, state);   // a custom map entry deleted since: the map of its scenario
                                    if (SetupEngine.Fingerprint(vm.Profile) != SetupEngine.Fingerprint(want)) Fail(name + ": the changes were not undone\n" + FirstDifference(SetupEngine.Fingerprint(vm.Profile), SetupEngine.Fingerprint(want)));
                                    if (vm.SetupChanged) Fail(name + ": undone, but still shown as changed");
                                }
                                else if (SetupEngine.Fingerprint(vm.Profile) != beforeUndo) Fail(name + ": nothing to undo or cancelled, but the setup changed");
                            }
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
                        case 31:
                        {
                            // Server types (never the install, update or firewall buttons: those change the PC).
                            int k = rnd.Next(4);
                            if (k == 0) { name = "server vote kick"; vm.ServerVoteKick = !vm.ServerVoteKick; break; }
                            if (k == 1) { name = "server official rules"; vm.ServerOfficialRules = !vm.ServerOfficialRules; break; }
                            var item = Pick(vm.ServerTypeItems.ToList());
                            vm.ServerTypeNight = rnd.Next(2) == 0;
                            string answer = Pick(new[] { "Set it up", "Replace", "Keep mine", "Cancel" });
                            name = "server type " + item.Type.Id + (vm.ServerTypeNight ? " night" : "") + " (" + answer + ")";
                            if (!vm.ApplyServerTypeCommand.CanExecute(item)) break;
                            var beforeType = SetupEngine.Fingerprint(vm.Profile);
                            int cycleBefore = vm.MapCycleCount;
                            bool asked = vm.SetupChanged;
                            vm.ApplyServerTypeCommand.Execute(item);
                            // Every question gets the same answer: the setup question and the map cycle one.
                            for (int q = 0; q < 6; q++)
                            {
                                await Pump();
                                if (!vm.DialogOpen) break;
                                string shownAnswer = Loc.T(answer);
                                if (shownAnswer != vm.DialogPrimary && shownAnswer != vm.DialogSecondary && shownAnswer != vm.DialogTertiary) shownAnswer = answer == "Cancel" ? vm.DialogSecondary : vm.DialogPrimary;
                                vm.DialogCommand.Execute(shownAnswer);
                            }
                            await Pump();
                            var sc = SetupEngine.Scenario(vm.Profile, state);
                            bool applied = sc != null && state.ModeFor(sc, false)?.Cls == item.Type.ModeCls && SetupEngine.Fingerprint(vm.Profile) != beforeType;
                            if (answer == "Cancel" && (asked || cycleBefore > 0)) { if (SetupEngine.Fingerprint(vm.Profile) != beforeType) Fail(name + ": cancelled, but the setup changed"); break; }
                            if (!applied && SetupEngine.Fingerprint(vm.Profile) != beforeType) Fail(name + ": the setup changed but is not a " + item.Type.ModeCls + " match");
                            if (applied)
                            {
                                if (vm.PlayTarget != "Server") Fail(name + ": the big button is not on the server");
                                if (state.Settings.ServerMaxPlayers != item.Type.MaxPlayers) Fail(name + ": max players " + state.Settings.ServerMaxPlayers);
                                if (vm.Profile.Hardcore != item.Type.Hardcore) Fail(name + ": hardcore is " + vm.Profile.Hardcore);
                                if (vm.SelectedSetup != null) Fail(name + ": the new setup is shown as the saved setup " + vm.SelectedSetup);
                                bool replaced = cycleBefore == 0 || answer == "Replace" || answer == "Set it up";
                                int want = ServerTypes.Cycle(state, item.Type, vm.ServerTypeNight).Count;
                                if (replaced && vm.MapCycleCount != want) Fail(name + ": " + vm.MapCycleCount + " scenarios in the map cycle instead of " + want);
                                if (!replaced && vm.MapCycleCount != cycleBefore) Fail(name + ": the map cycle was replaced after Keep mine");
                                if (vm.MapCycleCount > 0 && !state.Settings.ServerUseMapCycle) Fail(name + ": the map cycle is not switched on");
                            }
                            break;
                        }
                        case 30:
                        {
                            // Custom map entries come and go (the one in use too), and mods are read again: the setup on
                            // screen, the bar and what is saved must follow.
                            int k = rnd.Next(4);
                            var customs = vm.Maps.Where(m => m.Custom != null).ToList();
                            if (k == 0 || (k == 1 && customs.Count == 0))
                            {
                                var sc = Pick(state.AllScenarios.ToList());
                                name = "custom map add " + sc.Id;
                                vm.AddCustomMapCommand.Execute(null);
                                vm.EditMapLabel = Pick(Names);
                                vm.EditMapLevel = sc.Level;
                                vm.EditMapScenario = sc.Id;
                                vm.EditMapMode = rnd.Next(2) == 0 ? "" : sc.GameModeClass;
                                await Do(vm.SaveCustomMapCommand, null);
                                vm.MapEditorOpen = false;
                            }
                            else if (k == 1)
                            {
                                var m = rnd.Next(2) == 0 && vm.SelectedMap?.Custom != null ? vm.SelectedMap : Pick(customs);
                                name = "custom map delete " + m.Custom.Id + (m == vm.SelectedMap ? " (in use)" : "");
                                await Do(vm.DeleteCustomMapCommand, m);
                            }
                            else if (k == 2 && customs.Count > 0)
                            {
                                var m = Pick(customs);
                                name = "custom map pick " + m.Custom.Id;
                                await Do(vm.SelectMapCommand, m);
                            }
                            else { name = "rescan mods"; await vm.RescanMods(true); }
                            break;
                        }
                        case 29:
                        {
                            // The big button: this PC or the server. Only "set up the server" is pressed here (it opens a page);
                            // starting the game or the real server is never done by this test.
                            vm.SetPlayTargetCommand.Execute(Pick(new[] { "Local", "Server", "Server", "bogus" }));
                            await Pump();
                            name = "play target " + vm.PlayTarget + " (" + vm.MainActionTitle + ")";
                            if (vm.PlayTarget == "Local")
                            {
                                if (vm.MainActionTitle != vm.LaunchTitle) Fail(name + ": the button says " + vm.MainActionTitle + " instead of " + vm.LaunchTitle);
                                if (vm.MainActionCommand.CanExecute(null) != vm.CanLaunch) Fail(name + ": the button can" + (vm.CanLaunch ? "not" : "") + " be pressed but launching can" + (vm.CanLaunch ? "" : "not"));
                                break;
                            }
                            var plan = vm.CurrentPlan == null && !(state.Settings.ServerUseOwnArgs && !state.Settings.ServerRemote) ? null : ServerPlanner.Build(vm.CurrentPlan, state.Settings, vm.ServerInstall, state.Rules, "");
                            bool planOk = plan?.IsValid == true;
                            bool setUp = vm.ServerRemote ? string.IsNullOrWhiteSpace(state.Settings.ServerRemoteHost) || !planOk || plan.Match == null || plan.Url == null : !vm.ServerFound || !planOk;
                            string want = setUp ? Loc.T("Set up the server") : vm.ServerRemote ? Loc.T("Load on the server") : Loc.T("Start the server");
                            if (vm.MainActionTitle != want) Fail(name + ": the button says " + vm.MainActionTitle + " instead of " + want);
                            if (setUp && vm.MainActionCommand.CanExecute(null))
                            {
                                vm.Page = "Play";
                                vm.MainActionCommand.Execute(null);
                                await Pump();
                                string page = planOk && (plan.Match == null || plan.Url == null) && !(vm.ServerRemote && string.IsNullOrWhiteSpace(state.Settings.ServerRemoteHost)) ? "Play" : "Server";
                                if (vm.Page != page) Fail(name + ": pressed, but the " + page + " page did not open");
                            }
                            break;
                        }
                        case 28:
                        {
                            // Another language while everything else goes on: every text written in the pages follows it.
                            var lang = Pick(vm.Languages.ToList());
                            name = "language " + (lang.Id.Length == 0 ? "English" : lang.Id);
                            // Every page is shown before and after the switch: lists made before it must follow too.
                            string[] tour = { "Play Map", "Play Squad", "Play Rules", "Play Live", "Play Mods Mutators", "Play Mods Installed", "Play Advanced", "Server", "Settings" };
                            string keepPage = vm.Page, keepTab = vm.PlayTab, keepMods = vm.ModsTab;
                            async Task Show(string where)
                            {
                                var parts = where.Split(' ');
                                vm.Page = parts[0];
                                if (parts.Length > 1) vm.PlayTab = parts[1];
                                if (parts.Length > 2) vm.ModsTab = parts[2];
                                await Pump();
                                Layout();
                                Views.LocHook.ApplyTree(host);   // off screen nothing is "loaded"; a window does this as the page appears
                                Layout();
                            }
                            foreach (var t in tour) await Show(t);
                            vm.SelectedLanguage = lang;
                            if (!string.Equals(Loc.CurrentId, lang.Id, StringComparison.OrdinalIgnoreCase)) Fail(name + ": language in use is " + Loc.CurrentId);
                            foreach (var t in tour)
                            {
                                await Show(t);
                                foreach (var (text, where) in PageLiterals(host, Loc.IsEnglish))
                                {
                                    string key = Loc.Key(text);
                                    // (back in English, no text at all, bound ones too, may keep the » « marks of the test language made from the template)
                                    if (Loc.IsEnglish ? text.IndexOf('»') >= 0 : Loc.T(key) != key && text.Trim() == key)
                                        Fail(name + ", " + t + ": \"" + text.Trim() + "\" is not in the language picked (" + where + ")");
                                }
                            }
                            vm.Page = keepPage; vm.PlayTab = keepTab; vm.ModsTab = keepMods;
                            // A value shown as it is (a profile called "Settings") is not translated as if it were a text of the page.
                            var probe = new ContentControl();
                            probe.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding { Source = "Settings" });
                            var probeHost = new Border { Child = probe };
                            probeHost.Measure(new Size(400, 100)); probeHost.Arrange(new Rect(0, 0, 400, 100)); probeHost.UpdateLayout();
                            Views.LocHook.ApplyTree(probeHost);
                            probeHost.UpdateLayout();
                            CheckLanguage(vm, probeHost, m => Fail(name + ": " + m));
                            break;
                        }
                        case 26:
                        case 27:
                        {
                            // The Server page: settings typed in (good and bad), switches, and the map cycle (its file stays in the
                            // test data folder; nothing is ever started and the real server's files are not touched).
                            if (rnd.Next(3) == 0) vm.Page = "Server";
                            int what = rnd.Next(15);
                            string[] ids = { "76561198000000001", "76561198000000002\n76561198000000003", "notanid", "", "7656119\n123", "76561198000000004, 76561198000000005" };
                            switch (what)
                            {
                                case 0: name = "server name"; vm.ServerName = Pick(Names); break;
                                case 1: name = "server numbers"; vm.ServerMaxPlayersText = Pick(Texts); vm.ServerPortText = Pick(Texts); vm.ServerQueryPortText = Pick(new[] { "27131", "27102", "abc", "70000" }); vm.ServerRconPortText = Pick(new[] { "27015", "80", "65535", "x" }); break;
                                case 2: name = "server password"; vm.ServerJoinPassword = Pick(new[] { "", "secret1", "a b", "p?w", "\u017e" }); break;
                                case 3: name = "server switches"; vm.ServerShowLog = !vm.ServerShowLog; vm.ServerCheats = rnd.Next(2) == 0; vm.ServerGameStats = rnd.Next(2) == 0; vm.ServerRconFromNetwork = rnd.Next(2) == 0; break;
                                case 4: name = "server token"; vm.ServerGslt = Pick(new[] { "", "ABCDEF0123456789", "bad token!" }); vm.ServerExtraArgs = Pick(new[] { "", "-nosteam", "-MOTD=Hi" }); break;
                                case 5: name = "server admins"; vm.ServerAdmins = Pick(ids); break;
                                case 6: name = "server mods"; vm.ServerModsEnabled = rnd.Next(2) == 0; vm.ServerModIds = Pick(new[] { "", "1457355", "12\nabc", "98685, 4858240" }); if (rnd.Next(2) == 0) await Do(vm.AddMatchModsCommand, null); break;
                                case 7: name = "server remote"; vm.ServerRemote = !vm.ServerRemote; vm.ServerRemoteHost = Pick(new[] { "", "203.0.113.5", "server.example" }); vm.ServerRemotePortText = Pick(new[] { "27015", "0", "abc" }); break;
                                case 8: name = "map cycle switch"; vm.ServerUseMapCycle = !vm.ServerUseMapCycle; break;
                                case 9: name = "map cycle add"; await Do(vm.AddMatchToCycleCommand, null); break;
                                case 12: name = "check server mods"; vm.ServerModsEnabled = rnd.Next(2) == 0; await Do(vm.CheckServerModsCommand, null); break;
                                case 13:
                                    // The player's own options (as pasted or read from a .bat file; the file picker is never opened here).
                                    name = "own options";
                                    vm.ServerUseOwnArgs = rnd.Next(3) > 0;
                                    vm.ServerOwnArgs = Pick(new[]
                                    {
                                        "", "Farmhouse?Scenario=Scenario_Farmhouse_Checkpoint_Security?MaxPlayers=8 -Port=27102 -QueryPort=27131 -log",
                                        "InsurgencyServer.exe Oilfield?Scenario=Scenario_Refinery_Push_Security -Mods -SecurityCode=none -hostname=\"x y\"",
                                        "-Mods -Rcon -RconPassword=abc -RconListenPort=27999", "start \"\" InsurgencyServer.exe Ministry -Port=27200 ^\n -QueryPort=27231 -mods > log.txt",
                                    });
                                    break;
                                case 14:
                                    // The server's mod.io login: typed in only (sending a code and subscribing go to mod.io and are never pressed here).
                                    name = "server mod.io login";
                                    vm.ServerModioEmail = Pick(new[] { "", "server@example.com", "bad@" });
                                    vm.ServerModioCode = Pick(new[] { "", "12345", "12a", " 54321 ", "123456" });
                                    if (vm.SendModioCodeCommand.CanExecute(null) != ServerModio.IsEmail(vm.ServerModioEmail)) Fail(name + ": Send code can" + (ServerModio.IsEmail(vm.ServerModioEmail) ? "not" : "") + " be pressed with " + vm.ServerModioEmail);
                                    break;
                                default:
                                {
                                    if (vm.MapCycleItems.Count == 0) { name = "map cycle add"; await Do(vm.AddMatchToCycleCommand, null); break; }
                                    var item = Pick(vm.MapCycleItems.ToList());
                                    int how = rnd.Next(4);
                                    name = "map cycle " + new[] { "remove", "up", "down", "lighting" }[how];
                                    await Do(new[] { vm.RemoveCycleItemCommand, vm.MoveCycleItemUpCommand, vm.MoveCycleItemDownCommand, vm.ToggleCycleLightingCommand }[how], item);
                                    break;
                                }
                            }
                            break;
                        }
                        case 24:
                        {
                            // Playlists one after another: nothing a playlist added may stay once the next one is applied.
                            vm.PresetFilter = "Playlists";
                            vm.PresetSearch = "";
                            await Pump();
                            if (vm.RulePresets.Count == 0) break;
                            var before = vm.Profile.Clone("before");
                            var a = Pick(vm.RulePresets);
                            var b = Pick(vm.RulePresets);
                            name = "playlists " + a.Name + " then " + b.Name;
                            await Do(vm.ApplyPresetCommand, a);
                            await Do(vm.ApplyPresetCommand, b);
                            var direct = before.Clone("direct");
                            SetupEngine.Apply(direct, state, (Preset)b.Source);
                            string got = SetupEngine.Fingerprint(vm.Profile), want = SetupEngine.Fingerprint(direct);
                            if (got != want) Fail(name + ": the setup is not the same as with " + b.Name + " alone\n" + FirstDifference(got, want));
                            break;
                        }
                        default:
                        {
                            // A saved setup is loaded back exactly (picked in the bar at the top).
                            if (vm.SavedSetupNames.Count == 0) break;
                            string pick = Pick(vm.SavedSetupNames);
                            var rp = state.Settings.RulesPresets.First(r => r.Name == pick);
                            string answer = Pick(new[] { "Load", "Load", "Load", "Cancel", "Save first" });
                            name = "load setup " + pick + " (" + answer + ")";
                            var beforeLoad = SetupEngine.Fingerprint(vm.Profile);
                            string shownBefore = vm.SelectedSetup;
                            bool wasChanged = vm.SetupChanged && shownBefore != pick;
                            vm.SelectedSetup = pick;
                            await Answer(answer, Pick(Names));
                            var p = vm.Profile;
                            if (pick == shownBefore)
                            {
                                // The one already selected: picking it again changes nothing (the list does not reload it).
                                if (SetupEngine.Fingerprint(p) != beforeLoad) Fail(name + ": the setup already selected was picked again and the setup on screen changed");
                                break;
                            }
                            if (wasChanged && answer == "Cancel")
                            {
                                // Unsaved changes and Cancel: nothing is loaded.
                                if (SetupEngine.Fingerprint(p) != beforeLoad) Fail(name + ": cancelled, but the setup on screen changed");
                                if (vm.SelectedSetup != shownBefore) Fail(name + ": cancelled, but the bar shows " + (vm.SelectedSetup ?? "nothing") + " instead of " + (shownBefore ?? "nothing"));
                                break;
                            }
                            if (vm.SelectedSetup != pick) { if (!(wasChanged && answer != "Load")) Fail(name + ": the bar shows " + (vm.SelectedSetup ?? "nothing")); break; }
                            if (rp.Setup != null)
                            {
                                var want = new Models.Profile();
                                SetupEngine.CopySetup(rp.Setup, want);
                                want.Mutators = want.Mutators.Where(id => state.FindMutator(id) != null).ToList();
                                SetupEngine.CleanRules(want.Rules, db);
                                SetupEngine.AlignMap(want, state);   // a custom map entry deleted since: the map of its scenario
                                if (SetupEngine.Fingerprint(p) != SetupEngine.Fingerprint(want)) Fail(name + ": not loaded exactly\n" + FirstDifference(SetupEngine.Fingerprint(p), SetupEngine.Fingerprint(want)));
                                if (vm.SetupChanged) Fail(name + ": just loaded, but shown as changed");
                            }
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
                    CheckServer(vm, state, Fail);
                    CheckLanguage(vm, host, Fail);
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

        /// <summary>
        /// Texts from the view model are in the language in use, not left over from the one before (a leftover of the
        /// test language keeps its » « marks), and text the window shows for a plain value (a profile name) is that value.
        /// </summary>
        private static void CheckLanguage(MainViewModel vm, DependencyObject root, Action<string> fail)
        {
            bool Stale(string s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                if (Loc.IsEnglish) return s.IndexOf('»') >= 0;
                string key = Loc.Key(s);
                return Loc.T(key) != key && s.Trim() == key;
            }
            void Test(string what, string s) { if (Stale(s)) fail("\"" + s + "\" (" + what + ") is not in the language in use"); }
            Test("toast", vm.Toast);
            foreach (var m in vm.ModItems) { Test("mod counts", m.Counts); Test("mod author", m.Author); Test("mod date", m.Updated); Test("mod state", m.StateLabel); Test("mod warnings", m.WarningText); }
            foreach (var r in vm.LiveRules) Test("live rule", r.Label);
            foreach (var b in vm.KeyBindingBackups) Test("key backup", b.Label);
            foreach (var r in vm.RuleItems) { Test("rule", r.Label); Test("rule value", r.DisplayValue); }
            void Walk(DependencyObject d)
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                    if (child is UIElement ui && ui.Visibility != Visibility.Visible) continue;
                    if (child is TextBlock tb && tb.ActualWidth > 0 && tb.TemplatedParent is ContentPresenter cp && cp.Content is string s && cp.ContentTemplate == null && cp.ContentTemplateSelector == null && tb.Text != s)
                        fail("the window shows \"" + tb.Text + "\" for the value \"" + s + "\"");
                    Walk(child);
                }
            }
            Walk(root);
        }

        /// <summary>The Server page: what it shows is what a start would run, and the map cycle file is what the list shows.</summary>
        private static void CheckServer(MainViewModel vm, AppState state, Action<string> fail)
        {
            var s = state.Settings;
            if (!string.Equals(s.ServerMapCycleFile, TortureCycle, StringComparison.OrdinalIgnoreCase)) fail("the map cycle file moved to " + s.ServerMapCycleFile);
            var fresh = vm.CurrentPlan == null && !(s.ServerUseOwnArgs && !s.ServerRemote) ? null : ServerPlanner.Build(vm.CurrentPlan, s, vm.ServerInstall, state.Rules, "");
            string want = fresh?.IsValid == true ? "InsurgencyServer.exe " + fresh.ShownCommandLine : "";
            if (vm.ServerCommandLine != want) fail("server command line on screen is stale:\n  shown " + vm.ServerCommandLine + "\n  fresh " + want);
            string code = ServerModio.CleanCode(s.ServerModioCode);
            if (code != null && vm.ServerCommandLine.Contains(code)) fail("the command line on screen shows the security code: " + vm.ServerCommandLine);
            bool modsOn = s.ServerUseOwnArgs ? ServerArgs.Has(ServerArgs.Clean(s.ServerOwnArgs), "Mods") : s.ServerModsEnabled;
            if (vm.ServerModsOn != modsOn) fail("mods on is shown as " + vm.ServerModsOn + " but the options say " + modsOn);
            if (fresh?.IsValid == true && fresh.OwnArgs != null)
            {
                // The player's own options go through unchanged, but for a waiting security code.
                string own = ServerArgs.Clean(s.ServerOwnArgs);
                bool withCode = code != null && modsOn;
                if (fresh.UsesCode != withCode) fail("own options: the security code is " + (fresh.UsesCode ? "" : "not ") + "passed");
                if (fresh.CommandLine != (withCode ? ServerModio.WithSecurityCode(own, code) : own)) fail("own options changed:\n  " + fresh.CommandLine + "\n  " + own);
                if (!ServerPlanner.RconFor(s, "").Own && (!fresh.GameIni.Contains("Password=" + s.ServerRconPassword) || !fresh.GameIni.Contains("ListenPort=" + s.ServerRconPort)))
                    fail("own options without RCON: the launcher's RCON is not in Game.ini");
            }
            else if (fresh?.IsValid == true)
            {
                if (fresh.Url.Contains("bSoloGame")) fail("the server URL has bSoloGame: " + fresh.Url);
                if (!fresh.Url.Contains("?MaxPlayers=" + Math.Max(1, Math.Min(100, s.ServerMaxPlayers)))) fail("the server URL does not have the server's player count: " + fresh.Url);
                if ((s.ServerPassword ?? "").Length > 0 != fresh.Url.Contains("?Password=")) fail("the join password and the URL disagree: " + fresh.Url);
                if (fresh.ShownCommandLine.IndexOf("?Password=" + (s.ServerPassword ?? "").Trim(), StringComparison.Ordinal) >= 0 && (s.ServerPassword ?? "").Trim().Length > 0)
                    fail("the command line on screen shows the join password");
                if (fresh.Args.Contains("-log") != s.ServerShowLog) fail("-log does not follow the switch");
                if (fresh.Args.Contains("-EnableCheats") != s.ServerCheats) fail("-EnableCheats does not follow the switch");
                bool admins = ServerPlanner.SteamIds(s.ServerAdmins, null).Count > 0;
                if (fresh.Args.Contains("-AdminList=Admins") != admins) fail("-AdminList does not follow the admins list");
                if (fresh.Args.Contains("-Mods") != s.ServerModsEnabled) fail("-Mods does not follow the switch");
                if (s.ServerModsEnabled && !fresh.Args.Contains("-SecurityCode=" + (code ?? "none"))) fail("-SecurityCode is not the waiting code (or none): " + fresh.ShownCommandLine);
                if (fresh.UsesCode != (s.ServerModsEnabled && code != null)) fail("the plan says it uses a code: " + fresh.UsesCode);
                if (fresh.Args.Any(a => a.StartsWith("-MapCycle=", StringComparison.Ordinal)) != s.ServerUseMapCycle) fail("-MapCycle does not follow the switch");
                if (!fresh.GameIni.Contains("Password=" + s.ServerRconPassword) || !fresh.GameIni.Contains("ListenPort=" + s.ServerRconPort)) fail("the server's Game.ini has no RCON section with its port and password");
                if (ServerPlanner.VoteKickOn(fresh.GameIni) != s.ServerVoteKick) fail("vote kick in the server's Game.ini does not follow the switch");
                if (fresh.Args.Contains("-ruleset=OfficialRules") != s.ServerOfficialRules) fail("official rules do not follow the switch: " + fresh.ShownCommandLine);
                if (fresh.Args.Count(a => a.StartsWith("-ruleset=", StringComparison.Ordinal)) > 1) fail("two rulesets on the server's command line");
            }
            // The list on screen is the file.
            var onDisk = File.Exists(TortureCycle) ? MapCycle.Parse(File.ReadAllText(TortureCycle)) : new List<MapCycleEntry>();
            string disk = string.Join(" | ", onDisk.Select(e => e.Line)), shown = string.Join(" | ", vm.MapCycleItems.Select(i => i.Entry.Line));
            if (disk != shown) fail("map cycle on screen differs from the file:\n  file   " + disk + "\n  screen " + shown);
            if (vm.MapCycleCount != onDisk.Count(e => e.IsEntry)) fail("map cycle count " + vm.MapCycleCount + " but the file has " + onDisk.Count(e => e.IsEntry));
            // The mod status of a server that is not running says so (or nothing), never a verdict from an old log.
            if (!vm.ServerRunningHere && vm.ServerModKind != "Off") fail("the mod status says " + vm.ServerModKind + " but the server is not running: " + vm.ServerModText);
            // How players join follows the ports.
            bool ownPorts = fresh?.IsValid == true && fresh.OwnArgs != null;
            int port = ownPorts ? fresh.GamePort : s.ServerPort, query = ownPorts ? fresh.QueryPort : s.ServerQueryPort;
            if (!vm.ServerPortsText.Contains(port.ToString(CultureInfo.InvariantCulture)) || !vm.ServerPortsText.Contains(query.ToString(CultureInfo.InvariantCulture))) fail("the ports to forward are shown as " + vm.ServerPortsText);
            if (vm.ServerJoinCommand.Length > 0 && !vm.ServerJoinCommand.EndsWith(":" + port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) fail("the join command is " + vm.ServerJoinCommand);
        }

        private static string TortureCycle;

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
                    if (mode.Defaults.ContainsKey("FriendlyBotQuota") && vm.Teammates != SetupEngine.AiTeammates(Eff("FriendlyBotQuota")))
                        fail("AI teammates shown " + vm.Teammates + ", stored FriendlyBotQuota " + Eff("FriendlyBotQuota") + " (counts you)");
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
            if (p.RulesPresetName != null && p.PresetCheck != null && vm.ActivePresetName != Core.Loc.F("{0} (changed)", Core.Loc.T(p.RulesPresetName)) && SetupEngine.PresetCheck(p, p.PresetCheck.StartsWith("S|")) != p.PresetCheck)
                fail("setup changed after " + p.RulesPresetName + " but the summary does not say so");
            // Saved setups: the bar lists them all, shows the loaded one, and says "Changed" exactly when it differs from it.
            if (!vm.SavedSetupNames.OrderBy(x => x).SequenceEqual(state.Settings.RulesPresets.Select(r => r.Name).OrderBy(x => x))) fail("setup list differs from the saved setups");
            var loaded = state.Settings.RulesPresets.FirstOrDefault(r => string.Equals(r.Name, p.SetupName, StringComparison.OrdinalIgnoreCase));
            if (vm.SelectedSetup != loaded?.Name) fail("the bar shows " + (vm.SelectedSetup ?? "nothing") + " but the setup came from " + (loaded?.Name ?? "nothing"));
            if (loaded != null && (vm.SetupState == "") != (SetupEngine.Fingerprint(p) == p.SetupCheck)) fail("the bar says \"" + vm.SetupState + "\" but the setup " + (SetupEngine.Fingerprint(p) == p.SetupCheck ? "is" : "is not") + " as saved");
            if (state.Settings.RulesPresets.Select(r => r.Name.ToLowerInvariant()).Distinct().Count() != state.Settings.RulesPresets.Count) fail("two saved setups have the same name");
            if (state.Settings.MutatorPresets.Count > 0) fail("mutator presets came back");
            if (p.SetupName != null && loaded == null) fail("the setup still points at the deleted saved setup " + p.SetupName);
            // The preset list is exactly the presets of its tab that match the search.
            {
                bool coop = SetupEngine.CurrentMode(p, state)?.Coop ?? true;
                var list = vm.PresetFilter == "Official" ? SetupEngine.OfficialPresets(state.Rules) : vm.PresetFilter == "Playlists" ? SetupEngine.PlaylistPresets(state, coop) : SetupEngine.StylePresets(state.Rules);
                string q = (vm.PresetSearch ?? "").Trim();
                var wantNames = list.Select(x => Loc.T(x.Name)).Where((x, i) => q.Length == 0 || (x + " " + Loc.T(list[i].Description) + " " + Loc.T(list[i].Note) + " " + list[i].Name).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                var shownNames = vm.RulePresets.Select(x => x.Name).ToList();
                if (!wantNames.SequenceEqual(shownNames))
                {
                    int at = 0; while (at < Math.Min(wantNames.Count, shownNames.Count) && wantNames[at] == shownNames[at]) at++;
                    fail("preset list (" + vm.PresetFilter + ", search '" + q + "', " + (coop ? "co-op" : "versus") + ") shows " + shownNames.Count + " of " + wantNames.Count
                         + "; first difference at " + at + ": " + (at < shownNames.Count ? shownNames[at] : "-") + " / " + (at < wantNames.Count ? wantNames[at] : "-"));
                }
            }
            // The rule count in the summary is the count the Rules tab shows (not the launcher's own bot adjustments).
            string wantRules = vm.RuleChangeCount == 0 ? Loc.T("Game default rules") : vm.RuleChangeCount == 1 ? Loc.T("1 rule change") : Loc.F("{0} rule changes", vm.RuleChangeCount);
            if (vm.RulesSummary != wantRules) fail("the summary says \"" + vm.RulesSummary + "\" but the Rules tab has " + vm.RuleChangeCount + " changed");
            // "In use" marks exactly the match preset last applied (not another one of the same name).
            foreach (var item in vm.RulePresets.Concat(vm.SquadPresets))
                if (item.IsActive != (item.Source is Preset pr && SetupEngine.InUse(p, pr))) { fail("preset " + item.Name + " is " + (item.IsActive ? "" : "not ") + "marked in use"); break; }
            if (vm.RulePresets.Count(x => x.IsActive) > 1) fail(vm.RulePresets.Count(x => x.IsActive) + " presets are marked in use");
            if (state.Store.Profiles.Count != 1) fail(state.Store.Profiles.Count + " profiles; there is only the setup on screen now");
            if (vm.PresetFilter != "Styles" && vm.PresetFilter != "Official" && vm.PresetFilter != "Playlists") fail("preset list " + vm.PresetFilter);
            if (vm.MutatorsEnabled != p.MutatorsEnabled || vm.Night != (p.Lighting == "Night") || vm.MaxPlayers != p.MaxPlayers) fail("conditions on screen differ from the profile");

            // The setup on screen has its file, and the file holds what is in memory.
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

        /// <summary>The first line where two fingerprints differ, for a readable failure.</summary>
        private static string FirstDifference(string got, string want)
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

        private static string Canon(Dictionary<string, Dictionary<string, string>> rules) =>
            string.Join(";", rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                                  .Select(m => m.Key + ":" + string.Join(",", m.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + kv.Value))));
    }
}
