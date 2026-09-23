using System;
using System.Collections.Generic;
using System.Linq;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;
using SandstormModLauncher.Services;

namespace SandstormModLauncher
{
    /// <summary>Shared, UI-independent state: install, catalogs, mods and user data.</summary>
    public sealed class AppState
    {
        public Store Store { get; } = new Store();
        public AppSettings Settings => Store.Settings;
        public RulesDb Rules { get; set; }
        public GameInstall Install { get; set; }
        public OfficialData Official { get; set; } = new OfficialData();
        public List<ModInfo> Mods { get; set; } = new List<ModInfo>();
        public List<ExecCommand> Commands { get; set; } = new List<ExecCommand>();
        public List<MutatorInfo> AllMutators { get; private set; } = new List<MutatorInfo>();
        public List<ScenarioInfo> AllScenarios { get; private set; } = new List<ScenarioInfo>();
        public List<MapInfo> Maps { get; private set; } = new List<MapInfo>();
        private Dictionary<string, MutatorInfo> mutatorIndex = new Dictionary<string, MutatorInfo>(StringComparer.OrdinalIgnoreCase);

        public void Rebuild()
        {
            var mutators = new List<MutatorInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Official.Mutators) if (seen.Add(m.Id)) mutators.Add(m);
            foreach (var mod in Mods)
                foreach (var m in mod.Mutators.OrderBy(x => x.IsBaseClass).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
                    if (seen.Add(m.Id)) mutators.Add(m);
            foreach (var c in Settings.CustomMutators)
                if (!string.IsNullOrWhiteSpace(c) && seen.Add(c.Trim()))
                    mutators.Add(new MutatorInfo { Id = c.Trim(), DisplayName = c.Trim(), Source = ContentSource.Custom, ModName = "Added by you", Description = "Added by name. The launcher could not find it in your installed mods." });
            AllMutators = mutators;
            mutatorIndex = mutators.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var scenarios = new List<ScenarioInfo>(Official.Scenarios);
            foreach (var mod in Mods) scenarios.AddRange(mod.Scenarios);
            AllScenarios = scenarios;
            Maps = GameCatalog.BuildMaps(scenarios, Official);
        }

        public MutatorInfo FindMutator(string id) => id != null && mutatorIndex.TryGetValue(id, out var m) ? m : null;

        public ModeDef ModeFor(ScenarioInfo s, bool hardcore) => s == null ? null : Rules.ResolveMode(s.GameModeClass, hardcore && s.GameModeClass == "INSCheckpointGameMode");
    }
}
