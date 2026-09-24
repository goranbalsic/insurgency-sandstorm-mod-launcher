using System;
using System.Collections.Generic;
using SandstormModLauncher.Core;

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
    }
}
