using System;
using System.Collections.Generic;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Models
{
    public enum ContentSource { Official, Mod, Custom }

    public sealed class MutatorInfo
    {
        public string Id { get; set; }                 // name used in ?Mutators= (PrimaryAssetName)
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public ContentSource Source { get; set; }
        public string ModName { get; set; }
        public long ModId { get; set; }
        public string AssetPath { get; set; }           // /Pkg/Mutators/Asset.Asset
        public string ConfigSection { get; set; }       // [/Pkg/Mutators/Asset.Asset_C]
        public bool IsBaseClass { get; set; }
        public bool Registered { get; set; } = true;    // inside a folder ModData registers for mutators
        public string Warning { get; set; }
    }

    public sealed class ScenarioInfo
    {
        public string Id { get; set; }                  // Scenario_Farmhouse_Checkpoint_Security
        public string Level { get; set; }               // /Game/Maps/Farmhouse/Farmhouse
        public string MapKey { get; set; }              // Farmhouse
        public string GameModeClass { get; set; }       // INSCheckpointGameMode
        public string GameModePath { get; set; }        // /Script/Insurgency.INSCheckpointGameMode or a blueprint class path
        public string GameModeName { get; set; }        // Checkpoint
        public string Side { get; set; }                // Security / Insurgents / East / West
        public string Category { get; set; }            // Co-op / Versus / Training
        public bool IsCoop { get; set; }
        public ContentSource Source { get; set; }
        public string ModName { get; set; }
        public long ModId { get; set; }
        public string Location { get; set; }            // Tideway (from the scenario id)
    }

    public sealed class MapInfo
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string LevelName { get; set; }
        public string Level { get; set; }
        public ContentSource Source { get; set; }
        public string ModName { get; set; }
        public string ThumbDay { get; set; }
        public string ThumbNight { get; set; }
        public List<ScenarioInfo> Scenarios { get; set; } = new List<ScenarioInfo>();
    }

    public sealed class ModInfo
    {
        public long Id { get; set; }
        public long StateCode { get; set; } = -1;   // mod.io state (5 = uninstall pending)
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string LogoFile { get; set; }                    // local image only (the game's own mod cache or the mod folder)
        public string Version { get; set; }
        public DateTime? Updated { get; set; }
        public long SizeOnDisk { get; set; }
        public string Folder { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Paks { get; set; } = new List<string>();
        public string InGameName { get; set; }
        public string WebsiteUrl { get; set; }
        public bool RequiredByClients { get; set; }
        public string PackageRoot { get; set; }
        public string State { get; set; }
        public List<MutatorInfo> Mutators { get; set; } = new List<MutatorInfo>();
        public List<ScenarioInfo> Scenarios { get; set; } = new List<ScenarioInfo>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class MutatorPreset
    {
        public string Name { get; set; }
        public List<string> Mutators { get; set; } = new List<string>();
    }

    public sealed class CustomMapEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Label { get; set; }
        public string Level { get; set; }
        public string Scenario { get; set; }
        public string GameModeClass { get; set; }
    }

    /// <summary>A saved set of rule overrides: game-mode class to property to value.</summary>
    public sealed class RulesPreset
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, Dictionary<string, string>> Rules { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public List<string> Mutators { get; set; } = new List<string>();

        // A saved setup (1.5.0+) also keeps the map part; older saved presets only had rules and mutators.
        public bool FullSetup { get; set; }
        public string MapKey { get; set; }
        public string ScenarioId { get; set; }
        public string CustomMapId { get; set; }
        public string Lighting { get; set; }
        public bool Hardcore { get; set; }
        public int MaxPlayers { get; set; }
        public bool MutatorsEnabled { get; set; } = true;

        /// <summary>Since 1.8.0: the whole setup (Advanced options too). Loading it replaces everything.</summary>
        public Profile Setup { get; set; }
    }

    /// <summary>
    /// Changes a match preset made outside its own rules: mutators it added, day or night, hardcore, and squad values.
    /// The next preset puts back each one that is still as the preset left it (a value changed by hand since stays).
    /// </summary>
    public sealed class PresetChanges
    {
        /// <summary>Which preset it was (two can share a name, e.g. a co-op and a versus playlist).</summary>
        public string PresetId { get; set; }
        public List<string> AddedMutators { get; set; } = new List<string>();
        public bool? MutatorsOnBefore { get; set; }
        public bool? MutatorsOnAfter { get; set; }
        public string LightingBefore { get; set; }
        public string LightingAfter { get; set; }
        public bool? HardcoreBefore { get; set; }
        public bool? HardcoreAfter { get; set; }
        /// <summary>"mode|key" to the value before (null = none) and after the preset.</summary>
        public Dictionary<string, string> RulesBefore { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> RulesAfter { get; set; } = new Dictionary<string, string>();
    }

    public sealed class Profile
    {
        public string Name { get; set; } = "Default";
        public string MapKey { get; set; }
        public string ScenarioId { get; set; }
        public string CustomMapId { get; set; }
        public bool Hardcore { get; set; }
        public string Lighting { get; set; } = "Day";
        public int MaxPlayers { get; set; } = 8;
        public bool MutatorsEnabled { get; set; } = true;
        public List<string> Mutators { get; set; } = new List<string>();
        public string MutatorPreset { get; set; }
        public Dictionary<string, Dictionary<string, string>> Rules { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public string RulesPresetName { get; set; }
        /// <summary>"mode|key" of every rule the last applied match preset set, so the next one can take them back out.</summary>
        public List<string> PresetKeys { get; set; } = new List<string>();
        /// <summary>The setup as the last preset left it (SetupEngine.PresetCheck), to show when it was changed since.</summary>
        public string PresetCheck { get; set; }
        /// <summary>What the last match preset changed besides its rules, so the next preset can take it back out.</summary>
        public PresetChanges PresetChanges { get; set; }
        /// <summary>The saved setup this one was loaded from or saved as (null = not saved), and how it was then.</summary>
        public string SetupName { get; set; }
        public string SetupCheck { get; set; }
        public string LaunchRuleset { get; set; }            // official ruleset applied with -ruleset= at game start
        public string CustomIniMode { get; set; } = "Off";     // Off / Append / Replace
        public string CustomIniText { get; set; } = "";
        public bool ForceReload { get; set; }
        public string ExtraUrlOptions { get; set; } = "";
        public string GameModeOverride { get; set; } = "";
        public string AfterLoadCommands { get; set; } = "";
        public bool EnableCheatsAfterLoad { get; set; }

        public Profile Clone(string newName)
        {
            var copy = Json.Deserialize<Profile>(Json.Serialize(this));
            copy.Name = newName;
            return copy;
        }
    }

    public sealed class AppSettings
    {
        public string ActiveProfile { get; set; } = "Default";
        public string GameDirOverride { get; set; } = "";
        public List<string> ExtraModFolders { get; set; } = new List<string>();
        public List<string> CustomMutators { get; set; } = new List<string>();
        public List<MutatorPreset> MutatorPresets { get; set; } = new List<MutatorPreset>();
        public List<CustomMapEntry> CustomMaps { get; set; } = new List<CustomMapEntry>();
        public List<RulesPreset> RulesPresets { get; set; } = new List<RulesPreset>();
        public bool AutoConsoleKey { get; set; } = true;         // add F10 as a console key while the game is closed
        public DateTime ConsoleKeyAddedUtc { get; set; }
        public List<string> ManagedIniKeys { get; set; } = new List<string>();   // "section\nkey" of extra Game.ini lines written last time
        public int RconPort { get; set; }                         // the game's RCON server on 127.0.0.1 (chosen once)
        public string RconPassword { get; set; }                  // random, set up by the launcher
        public bool AllowConsoleTyping { get; set; } = true;      // type into the game console when RCON is not available
        public string InputMethod { get; set; } = "Paste";      // Paste / Type
        public int KeyDelayMs { get; set; } = 60;
        public bool AutoStartGame { get; set; } = true;
        public int StartTimeoutSec { get; set; } = 300;
        public bool MinimizeOnLaunch { get; set; } = true;
        public string RestartPolicy { get; set; } = "Ask";      // Ask / Always / Never
        public bool SoloGameFlag { get; set; } = true;
        public bool ApplyLiveRules { get; set; } = true;
        public string LaunchArgs { get; set; } = "";
        public string LastWrittenRulesHash { get; set; } = "";
        public string GameStartedWithRulesHash { get; set; }
        public DateTime LastRulesWriteUtc { get; set; }
        public double WindowWidth { get; set; } = 1360;
        public double WindowHeight { get; set; } = 860;
        public bool WindowMaximized { get; set; } = true;
        public string LastPage { get; set; } = "Play";
        public double UiScale { get; set; } = 1.0;               // size of the whole interface (text and controls), 0.9 - 1.5
        public bool AutoUpdate { get; set; } = true;             // check GitHub releases and install new versions quietly
        public DateTime LastUpdateCheckUtc { get; set; }
        public string SkippedUpdateTag { get; set; }             // a release that cannot be installed (its exe is not newer)
        public string StartWith { get; set; } = "Store";        // how the game is started: Store (Steam or Epic), Exe (its own exe), Command
        public string StartCommand { get; set; } = "";            // the player's own start command ({options} = where the launcher's options go)
        public string PlayTarget { get; set; } = "Local";        // the big button: Local (the game on this PC) or Server (the dedicated server)
        public bool SetupsMigrated { get; set; }                  // profiles and mutator presets became saved setups (1.8.0, once)
        public string Language { get; set; } = "";               // translation in use ("" = English)

        // Dedicated server (Server page)
        public string ServerDirOverride { get; set; } = "";
        public bool ServerRemote { get; set; }                    // control a server on another PC (RCON only) instead of one on this PC
        public string ServerName { get; set; } = "";
        public int ServerPort { get; set; } = 27102;
        public int ServerQueryPort { get; set; } = 27131;
        public int ServerMaxPlayers { get; set; } = 28;
        public string ServerPassword { get; set; } = "";          // players need it to join
        public int ServerRconPort { get; set; } = 27015;
        public string ServerRconPassword { get; set; }            // random, chosen once
        public bool ServerRconFromNetwork { get; set; }           // RCON on every network card, not only this PC
        public string ServerRemoteHost { get; set; } = "";
        public int ServerRemoteRconPort { get; set; } = 27015;
        public string ServerRemoteRconPassword { get; set; } = "";
        public bool ServerUseMapCycle { get; set; }
        public string ServerMapCycleFile { get; set; } = "";      // empty = MapCycle.txt in the server's Insurgency\Config\Server
        public string ServerAdmins { get; set; } = "";            // SteamID64s, one per line
        public bool ServerModsEnabled { get; set; }
        public string ServerMods { get; set; } = "";              // mod.io mod ids the server's account should be subscribed to, one per line
        public string ServerModioEmail { get; set; } = "";        // the server's own mod.io account, for the security code
        public string ServerModioCode { get; set; } = "";         // a security code from mod.io, used at the next start and then dropped
        public bool ServerUseOwnArgs { get; set; }                // start with the player's own options (from their .bat) instead of the launcher's
        public string ServerOwnArgs { get; set; } = "";
        public string ServerGslt { get; set; } = "";              // Steam game server login token
        public bool ServerGameStats { get; set; }
        public bool ServerCheats { get; set; }
        public bool ServerShowLog { get; set; } = true;           // -log: the server's own log window
        public string ServerExtraArgs { get; set; } = "";
        public bool ServerVoteKick { get; set; }                  // players can vote to kick someone ([/Script/Insurgency.TeamInfo])
        public bool ServerOfficialRules { get; set; }             // -ruleset=OfficialRules: listed under the official rules filter
        public string ServerInstallDir { get; set; } = "";        // where SteamCMD installs the server (empty = C:\SandstormServer)
        public List<string> ServerManagedIniKeys { get; set; } = new List<string>();
    }
}
