using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher
{
    /// <summary>Headless diagnostic: scans everything and writes a readable report.</summary>
    public static class SelfTest
    {
        public static void Run(string outFile)
        {
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            sb.AppendLine($"Rules DB: {state.Rules.Modes.Count} modes, {state.Rules.Properties.Count} properties, {state.Rules.Rulesets.Count} rulesets, {state.Rules.Playlists.Count} playlists");

            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            sb.AppendLine($"Game: {state.Install.GameDir} store={state.Install.Store} build={state.Install.BuildId} steam={state.Install.SteamExe}");
            sb.AppendLine($"mod.io root: {GameInstall.ModioRoot}");

            string cache = Path.Combine(AppPaths.DataDir, "cache");
            var t0 = sw.Elapsed;
            state.Official = GameCatalog.Load(state.Install, cache, m => { });
            sb.AppendLine($"Official scan: {(sw.Elapsed - t0).TotalSeconds:0.0}s mutators={state.Official.Mutators.Count} scenarios={state.Official.Scenarios.Count} thumbs={state.Official.MapThumbDay.Count}/{state.Official.MapThumbNight.Count} aliases={state.Official.GameModeAliases.Count}");
            sb.AppendLine("Default console keys: " + string.Join(",", state.Official.DefaultConsoleKeys));
            foreach (var w in state.Official.Warnings) sb.AppendLine("  WARN " + w);

            t0 = sw.Elapsed;
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, cache, m => { });
            sb.AppendLine($"Mod scan: {(sw.Elapsed - t0).TotalSeconds:0.0}s mods={state.Mods.Count}");
            state.Rebuild();
            sb.AppendLine($"Totals: mutators={state.AllMutators.Count} scenarios={state.AllScenarios.Count} maps={state.Maps.Count}");
            sb.AppendLine();

            sb.AppendLine("== OFFICIAL MUTATORS");
            foreach (var m in state.Official.Mutators) sb.AppendLine($"  {m.Id,-26} {m.DisplayName,-26} {Trim(m.Description, 70)}");
            sb.AppendLine();
            sb.AppendLine("== MAPS");
            foreach (var m in state.Maps)
                sb.AppendLine($"  {m.DisplayName,-16} key={m.Key,-12} level={m.LevelName,-12} src={m.Source,-8} scen={m.Scenarios.Count,2} thumb={(m.ThumbDay != null ? "yes" : "no")} modes={string.Join(",", m.Scenarios.Select(s => s.GameModeName).Distinct())}");
            sb.AppendLine();
            sb.AppendLine("== MODS");
            foreach (var mod in state.Mods)
            {
                sb.AppendLine($"  [{mod.Id}] {mod.Name} by {mod.Author} v{mod.Version} state={mod.State} paks={mod.Paks.Count} root={mod.PackageRoot} ingame={mod.InGameName} tags={string.Join("/", mod.Tags)}");
                foreach (var mu in mod.Mutators)
                    sb.AppendLine($"      MUT {mu.Id,-28} {mu.DisplayName,-26} reg={mu.Registered} base={mu.IsBaseClass} author={mu.Author} desc={Trim(mu.Description, 50)} {mu.Warning}");
                foreach (var sc in mod.Scenarios) sb.AppendLine($"      SCN {sc.Id} level={sc.Level} mode={sc.GameModeName}");
                foreach (var w in mod.Warnings) sb.AppendLine("      WARN " + w);
            }
            sb.AppendLine();
            sb.AppendLine("== CONSOLE KEYS (effective): " + string.Join(", ", ConsoleBridge.ConfiguredKeys(state.Official)));
            sb.AppendLine();

            var p = new Profile
            {
                Name = "SelfTest", MapKey = state.Maps.FirstOrDefault(m => m.Key == "Farmhouse")?.Key,
                ScenarioId = "Scenario_Farmhouse_Checkpoint_Security", Lighting = "Night", MaxPlayers = 8,
                Mutators = new List<string> { "ISMC_Hardcore", "ImprovedAI", "BetterScopes", "DoesNotExist" }
            };
            p.Rules["INSCheckpointGameMode"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["FriendlyBotQuota"] = "0", ["SoloEnemies"] = "10", ["AIDifficulty"] = "0.8", ["RoundTime"] = "600", ["bAllowFriendlyFire"] = "False", ["MinimumEnemies"] = "3" };
            p.Rules["INSSkirmishGameMode"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DefaultReinforcementWaves"] = "20" };
            var plan = LaunchPlanner.Build(p, state);
            sb.AppendLine("== SAMPLE PLAN");
            sb.AppendLine("Title: " + plan.Title + "   error=" + plan.Error);
            sb.AppendLine("Open: " + plan.OpenCommand);
            sb.AppendLine("Overrides: " + string.Join(", ", plan.Overrides.Select(k => k.Key + "=" + k.Value)));
            sb.AppendLine("AfterLoad: " + string.Join(" | ", plan.AfterLoad));
            sb.AppendLine("RestartKey: " + plan.RestartKey);
            sb.AppendLine("Warnings: " + string.Join(" / ", plan.Warnings));
            sb.AppendLine("Game.ini block:\r\n" + plan.GameIniBlock);
            sb.AppendLine();
            sb.AppendLine("== RULESETS");
            foreach (var r in state.Rules.Rulesets) sb.AppendLine($"  {r.Id} '{r.Name}' modes={r.Rules.Count} mutators={string.Join(",", r.Mutators)}");
            sb.AppendLine($"Total time {sw.Elapsed.TotalSeconds:0.0}s");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>--ini-test in.ini out.ini: merges the active profile's rules into a copy of a Game.ini (nothing else is touched).</summary>
        public static void IniTest(string inFile, string outFile)
        {
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            state.Official = GameCatalog.Load(state.Install, Path.Combine(AppPaths.DataDir, "cache"), m => { });
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, Path.Combine(AppPaths.DataDir, "cache"), m => { });
            state.Rebuild();
            var plan = LaunchPlanner.Build(state.Store.Active, state);
            string text = UeIni.ReadText(inFile);
            string merged = UeIni.MergeSections(text, plan.IniSections, (s, k) => LaunchPlanner.IsManagedIniKey(state.Rules, s, k));
            File.WriteAllText(outFile, merged);
            File.WriteAllText(outFile + ".plan.txt", "Open: " + plan.OpenCommand + "\r\nPlayer slots: " + plan.PlayerSlots + "\r\n\r\n" + plan.GameIniBlock);
        }

        /// <summary>--plan-test out.txt: the open command and after-load commands for one scenario of every game mode (active profile).</summary>
        public static void PlanTest(string outFile)
        {
            var state = new AppState();
            state.Store.Load();
            state.Rules = RulesDb.LoadEmbedded();
            state.Install = GameInstall.Detect(state.Settings.GameDirOverride);
            string cache = GameInstall.DemoCacheDir ?? Path.Combine(AppPaths.DataDir, "cache");
            state.Official = GameCatalog.Load(state.Install, cache, m => { });
            state.Mods = ModScanner.Scan(state.Install, state.Settings.ExtraModFolders, cache, m => { });
            state.Rebuild();
            var sb = new StringBuilder();
            var p = state.Store.Active;
            string keepMap = p.MapKey, keepScenario = p.ScenarioId;
            foreach (var group in state.AllScenarios.Where(s => s.Category != "Training").GroupBy(s => s.GameModeClass))
            {
                var sc = group.First();
                p.ScenarioId = sc.Id;
                p.MapKey = state.Maps.FirstOrDefault(m => m.Scenarios.Any(x => x.Id == sc.Id))?.Key;
                var plan = LaunchPlanner.Build(p, state);
                sb.AppendLine("== " + group.Key + "  (" + sc.Id + ")");
                sb.AppendLine("   " + plan.OpenCommand);
                sb.AppendLine("   slots " + plan.PlayerSlots + "   after load: " + string.Join(" | ", plan.AfterLoad));
                if (plan.Error != null) sb.AppendLine("   ERROR " + plan.Error);
            }
            p.MapKey = keepMap; p.ScenarioId = keepScenario;
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        private static string Trim(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length > n ? s.Substring(0, n) + "..." : s).Replace("\n", " ");
    }
}
