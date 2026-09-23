using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Services
{
    /// <summary>Imports settings from the old "Local Play Launcher" (Defaults + Profile* INI files).</summary>
    public static class LegacyImport
    {
        public sealed class Result
        {
            public List<Profile> Profiles = new List<Profile>();
            public int Presets, CustomMaps, CustomMutators;
            public string Folder;
        }

        public static IEnumerable<string> CandidateFolders()
        {
            var list = new List<string> { AppPaths.ExeDir };
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            list.Add(downloads);
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static string FindOldLauncherFolder()
        {
            foreach (var dir in CandidateFolders())
            {
                string f = Path.Combine(dir, "Defaults");
                if (File.Exists(f) && File.ReadAllText(f).Contains("[DefaultConfiguration]")) return dir;
            }
            return null;
        }

        private static Dictionary<string, string> ReadSection(string file)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = null;
            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                if (section != "DefaultConfiguration" && section != "PreviousProfile") continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
            return d;
        }

        private static string ScenarioLocation(string map)
        {
            switch (map)
            {
                case "Sinjar": return "Hillside";
                case "Mountain": return "Summit";
                case "Buhriz": return "Tideway";
                case "Oilfield": return "Refinery";
                case "Compound": return "Outskirts";
                case "Town": return "Hideout";
                case "Canyon": return "Crossing";
                default: return map;
            }
        }

        public static Result Import(string folder, AppState state)
        {
            var res = new Result { Folder = folder };
            string defaultsFile = Path.Combine(folder, "Defaults");
            if (!File.Exists(defaultsFile)) return res;
            var defaults = ReadSection(defaultsFile);
            var s = state.Settings;

            foreach (var m in Split(defaults, "DefaultAvailableMutators"))
                if (state.FindMutator(m) == null && !s.CustomMutators.Contains(m, StringComparer.OrdinalIgnoreCase)) { s.CustomMutators.Add(m); res.CustomMutators++; }

            foreach (var preset in Split(defaults, "DefaultMutatorPresets"))
            {
                var parts = preset.Split(new[] { " - " }, StringSplitOptions.None).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (parts.Count < 2) continue;
                string name = "Imported: " + parts[0];
                if (s.MutatorPresets.Any(p => p.Name == name)) continue;
                s.MutatorPresets.Add(new MutatorPreset { Name = name, Mutators = parts.Skip(1).ToList() });
                res.Presets++;
            }

            foreach (var mp in Split(defaults, "DefaultCustomMapPresets"))
            {
                var parts = mp.Split(new[] { " - " }, StringSplitOptions.None).Select(x => x.Trim()).ToList();
                if (parts.Count < 3 || parts[0] == "None") continue;
                string scenario = parts[2].StartsWith("Scenario_") ? parts[2] : "Scenario_" + parts[1] + "_" + parts[2];
                if (s.CustomMaps.Any(c => c.Level == parts[0] && c.Scenario == scenario)) continue;
                s.CustomMaps.Add(new CustomMapEntry { Label = parts[0] + " (" + parts[2].Replace('_', ' ') + ")", Level = parts[0], Scenario = scenario });
                res.CustomMaps++;
            }

            if (defaults.TryGetValue("DefaultConsoleKey", out var key) && key.Trim().Equals("F10", StringComparison.OrdinalIgnoreCase)) s.ConsoleKey = "F10";
            if (defaults.TryGetValue("DefaultModDirectory", out var modDir) && !string.IsNullOrWhiteSpace(modDir) && Directory.Exists(modDir)
                && !IsScannedAnyway(modDir, state) && !s.ExtraModFolders.Contains(modDir, StringComparer.OrdinalIgnoreCase)) s.ExtraModFolders.Add(modDir);

            var files = new List<(string name, string path)> { ("Imported defaults", defaultsFile) };
            foreach (var f in Directory.GetFiles(folder, "Profile*"))
            {
                string n = Path.GetFileName(f);
                if (n.Length > 7 && !n.Contains(".")) files.Add(("Imported " + n.Substring(7), f));
            }
            foreach (var (name, path) in files)
            {
                var d = ReadSection(path);
                var p = new Profile { Name = UniqueName(state, name) };
                string map = Get(d, "DefaultMap"), scen = Get(d, "DefaultScenario");
                if (!string.IsNullOrEmpty(map) && !string.IsNullOrEmpty(scen))
                {
                    string id = "Scenario_" + ScenarioLocation(map) + "_" + scen.Replace(' ', '_');
                    var mapInfo = state.Maps.FirstOrDefault(m => m.Scenarios.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                                  ?? state.Maps.FirstOrDefault(m => m.Key.Equals(map, StringComparison.OrdinalIgnoreCase));
                    if (mapInfo != null)
                    {
                        p.MapKey = mapInfo.Key;
                        p.ScenarioId = mapInfo.Scenarios.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id ?? mapInfo.Scenarios.FirstOrDefault()?.Id;
                    }
                }
                string customMap = Get(d, "DefaultCustomMapName");
                if (!string.IsNullOrWhiteSpace(customMap))
                {
                    string cs = Get(d, "DefaultCustomScenario");
                    string csName = Get(d, "DefaultCustomScenarioName");
                    var entry = new CustomMapEntry
                    {
                        Label = customMap, Level = customMap,
                        Scenario = !string.IsNullOrEmpty(cs) && cs.StartsWith("Scenario_") ? cs
                                 : "Scenario_" + (string.IsNullOrEmpty(csName) ? customMap : csName) + "_" + (string.IsNullOrEmpty(cs) ? (scen ?? "Checkpoint_Security") : cs)
                    };
                    var existing = s.CustomMaps.FirstOrDefault(c => c.Level == entry.Level && c.Scenario == entry.Scenario);
                    if (existing == null) { s.CustomMaps.Add(entry); existing = entry; res.CustomMaps++; }
                    p.CustomMapId = existing.Id;
                }
                p.Lighting = Get(d, "DefaultTimeOfDay") == "Night" ? "Night" : "Day";
                if (int.TryParse(Get(d, "DefaultNumOfPlayers"), out int players) && players > 0) p.MaxPlayers = players;
                p.Mutators = Split(d, "DefaultMutators").ToList();
                p.MutatorsEnabled = Get(d, "DefaultLoadMutators") != "0";
                p.ForceReload = Get(d, "DefaultRunTwice") == "1";
                if (int.TryParse(Get(d, "DefaultAiDifficulty"), out int ai) && ai != 5)
                {
                    string v = (Math.Max(0, Math.Min(10, ai)) / 10.0).ToString("0.0#", CultureInfo.InvariantCulture);
                    foreach (var mode in state.Rules.Modes.Where(m => m.Coop)) SetRule(p, mode.Cls, "AIDifficulty", v);
                    SetRule(p, "*", "AIDifficulty", v);
                }
                string ini = (Get(d, "DefaultGameConfig") ?? "").Replace("|", "\r\n");
                string method = Get(d, "DefaultGameConfigMethod");
                if (!string.IsNullOrWhiteSpace(ini) && !ini.StartsWith(";Example Game.ini entry"))
                {
                    p.CustomIniText = ini;
                    p.CustomIniMode = method == "Replace" ? "Replace" : method == "Append" ? "Append" : "Off";
                }
                res.Profiles.Add(p);
            }
            return res;
        }

        /// <summary>The mod.io folder and the game's own legacy mod folder are scanned without being listed.</summary>
        public static bool IsScannedAnyway(string folder, AppState state)
        {
            try
            {
                string f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                foreach (var root in new[] { Game.GameInstall.ModioRoot, state.Install?.LegacyModioDir })
                    if (!string.IsNullOrEmpty(root) && f.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static void SetRule(Profile p, string cls, string key, string value)
        {
            if (!p.Rules.TryGetValue(cls, out var d)) p.Rules[cls] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d[key] = value;
        }

        private static string UniqueName(AppState state, string name)
        {
            string n = name; int i = 2;
            while (state.Store.Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n = name + " " + i++;
            return n;
        }

        private static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v.Trim() : null;

        private static IEnumerable<string> Split(Dictionary<string, string> d, string key) =>
            (Get(d, key) ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0 && x != "None" && x != "Default Preset");
    }
}
