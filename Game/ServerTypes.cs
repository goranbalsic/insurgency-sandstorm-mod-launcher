using System;
using System.Collections.Generic;
using System.Linq;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    /// <summary>A kind of server from the server admin guide's examples, set up in one go.</summary>
    public sealed class ServerType
    {
        public string Id, Name, Description;
        /// <summary>The game mode of its scenarios (every official one of that mode goes into the map cycle).</summary>
        public string ModeCls;
        /// <summary>An official playlist applied as the match preset (its mutators, rules and hardcore), or null.</summary>
        public string PlaylistKey;
        /// <summary>An official ruleset applied as the match preset, or null.</summary>
        public string RulesetId;
        /// <summary>Checkpoint Hardcore: the map cycle entries get Mode="CheckpointHardcore".</summary>
        public bool Hardcore;
        /// <summary>Versus for players only (competitive): no bots.</summary>
        public bool NoBots;
        public int MaxPlayers;
    }

    public static class ServerTypes
    {
        public static readonly List<ServerType> All = new List<ServerType>
        {
            new ServerType { Id = "coop", Name = N("Co-op"), ModeCls = "INSCheckpointGameMode", MaxPlayers = 8,
                             Description = N("Players together against bots on Checkpoint, every official map in turn. Up to 8 players.") },
            new ServerType { Id = "coop-hardcore", Name = N("Co-op Hardcore"), ModeCls = "INSCheckpointGameMode", PlaylistKey = "COOP_CHECKPOINTHC", Hardcore = true, MaxPlayers = 8,
                             Description = N("Checkpoint Hardcore like the official playlist: slower movement and longer captures. Up to 8 players.") },
            new ServerType { Id = "coop-frenzy", Name = N("Co-op Frenzy"), ModeCls = "INSCheckpointGameMode", PlaylistKey = "COOP_FRENZY", MaxPlayers = 8,
                             Description = N("Checkpoint against melee enemies and their specials, like the official Frenzy playlist. Up to 8 players.") },
            new ServerType { Id = "push", Name = N("Versus: Push"), ModeCls = "INSPushGameMode", MaxPlayers = 28,
                             Description = N("Two teams attack and defend; bots fill the empty places. Every official Push scenario in turn. Up to 28 players.") },
            new ServerType { Id = "competitive", Name = N("Competitive: Firefight"), ModeCls = "INSFirefightGameMode", RulesetId = "RS_CompetitiveFirefight", NoBots = true, MaxPlayers = 10,
                             Description = N("5 against 5 with the official competitive rules, players only. Every official Firefight scenario in turn. Up to 10 players.") },
        };

        public static ServerType Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The official scenarios of the type's mode, map by map.</summary>
        public static List<(MapInfo Map, ScenarioInfo Scenario)> Scenarios(AppState s, ServerType t) =>
            s.Maps.Where(m => m.Source == ContentSource.Official)
                  .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                  .SelectMany(m => m.Scenarios.Where(sc => sc.Source == ContentSource.Official && s.ModeFor(sc, false)?.Cls == t.ModeCls)
                                              .OrderBy(sc => sc.Id, StringComparer.OrdinalIgnoreCase).Select(sc => (m, sc)))
                  .ToList();

        /// <summary>The first match: on the map picked now when it has the mode, Security side first.</summary>
        public static (MapInfo Map, ScenarioInfo Scenario) Pick(AppState s, Profile p, ServerType t)
        {
            var list = Scenarios(s, t);
            if (list.Count == 0) return (null, null);
            var here = list.Where(x => string.Equals(x.Map.Key, p.MapKey, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(p.CustomMapId)).ToList();
            var pool = here.Count > 0 ? here : list;
            var security = pool.FirstOrDefault(x => string.Equals(x.Scenario.Side, "Security", StringComparison.OrdinalIgnoreCase));
            return security.Scenario != null ? security : pool[0];
        }

        /// <summary>The map cycle: every official scenario of the mode by day, and by night too when asked.</summary>
        public static List<MapCycleEntry> Cycle(AppState s, ServerType t, bool night)
        {
            var list = new List<MapCycleEntry>();
            foreach (var (_, sc) in Scenarios(s, t))
            {
                list.Add(new MapCycleEntry { Scenario = sc.Id, Lighting = "Day", Mode = t.Hardcore ? "CheckpointHardcore" : null });
                if (night) list.Add(new MapCycleEntry { Scenario = sc.Id, Lighting = "Night", Mode = t.Hardcore ? "CheckpointHardcore" : null });
            }
            return list;
        }

        /// <summary>
        /// A new setup for the server type: its first scenario, the game defaults, then its preset (through SetupEngine,
        /// so the next preset takes it back out as usual). Returns a line for the player, or null when the game has no
        /// scenario of the mode.
        /// </summary>
        public static string Apply(Profile p, AppState s, ServerType t)
        {
            var (map, sc) = Pick(s, p, t);
            if (sc == null) return null;
            var db = s.Rules;
            var fresh = new Profile { MapKey = map.Key, ScenarioId = sc.Id, Lighting = "Day" };
            SetupEngine.CopySetup(fresh, p);
            SetupEngine.ResetAll(p);
            p.SetupName = null;
            p.SetupCheck = null;
            if (t.NoBots)
                foreach (var m in db.Modes.Where(m => !m.Coop && m.Defaults.ContainsKey("bBots")))
                    SetupEngine.SetRule(p, db, m.Cls, "bBots", "False");
            Preset preset = null;
            if (t.PlaylistKey != null) preset = SetupEngine.PlaylistPresets(s, true).FirstOrDefault(x => x.Source is PlaylistDef pl && pl.Key == t.PlaylistKey);
            else if (t.RulesetId != null) preset = SetupEngine.OfficialPresets(db).FirstOrDefault(x => x.Source is RulesetDef rs && rs.Id == t.RulesetId);
            if (preset != null) SetupEngine.Apply(p, s, preset);
            return F("{0}: {1}", T(t.Name), (map.DisplayName ?? map.Key) + " · " + T(s.ModeFor(sc, p.Hardcore)?.Name ?? sc.GameModeName));
        }
    }
}
