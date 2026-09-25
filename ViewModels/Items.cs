using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.ViewModels
{
    public sealed class MapItem : ObservableObject
    {
        public MapInfo Info { get; }
        public CustomMapEntry Custom { get; }
        private bool night, selected;

        public MapItem(MapInfo info, CustomMapEntry custom = null) { Info = info; Custom = custom; }

        public string Key => Custom != null ? "custom:" + Custom.Id : Info.Key;
        public string Name => Custom?.Label ?? Info.DisplayName;
        public string Sub => Custom != null ? "Custom entry · " + Custom.Level
            : Info.Source == ContentSource.Mod ? Info.ModName
            : Info.LevelName.Equals(Info.DisplayName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ? "Official map" : "Level " + Info.LevelName;
        public string Tag => Custom != null ? "CUSTOM" : Info.Source == ContentSource.Mod ? "MOD" : "";
        public string Source => Custom != null ? "Custom" : Info.Source == ContentSource.Mod ? "Mods" : "Official";
        public string Thumb => night ? (Info.ThumbNight ?? Info.ThumbDay) : Info.ThumbDay;
        public string Initials => string.IsNullOrEmpty(Name) ? "?" : new string(Name.Split(' ').Where(w => w.Length > 0).Take(2).Select(w => char.ToUpperInvariant(w[0])).ToArray());
        public int CoopCount => Info.Scenarios.Count(s => s.Category == "Co-op");
        public int VersusCount => Info.Scenarios.Count(s => s.Category == "Versus");
        public string Counts => Custom != null ? Custom.Scenario : $"{CoopCount} co-op · {VersusCount} versus";
        public bool Night { get => night; set { if (Set(ref night, value)) Raise(nameof(Thumb)); } }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
    }

    public sealed class ScenarioItem : ObservableObject
    {
        public ScenarioInfo Info { get; }
        private bool selected;
        public ScenarioItem(ScenarioInfo info) { Info = info; }
        public string Mode => Info.GameModeName;
        public string Side => string.IsNullOrWhiteSpace(Info.Side) ? "" : Info.Side;
        public string Category => Info.Category;
        public string Id => Info.Id;
        public string SideLabel { get; set; }
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
    }

    /// <summary>One game mode on the selected map, with its scenarios (usually one per side).</summary>
    public sealed class ScenarioModeItem : ObservableObject
    {
        private bool selected;
        public string Mode { get; set; }
        public string Category { get; set; }
        public List<ScenarioItem> Items { get; set; } = new List<ScenarioItem>();
        public string Tip => string.Join("\n", Items.Select(i => i.Id));
        public bool IsSelected { get => selected; set => Set(ref selected, value); }
    }

    public sealed class ScenarioModeGroup
    {
        public string Name { get; set; }
        public List<ScenarioModeItem> Modes { get; set; } = new List<ScenarioModeItem>();
    }

    public sealed class MutatorItem : ObservableObject
    {
        public MutatorInfo Info { get; }
        private readonly Action<MutatorItem, bool> toggled;
        private bool active;

        public MutatorItem(MutatorInfo info, bool isActive, Action<MutatorItem, bool> onToggle)
        {
            Info = info; active = isActive; toggled = onToggle;
        }

        public string Id => Info.Id;
        public string Name => Info.DisplayName;
        public string Description => string.IsNullOrWhiteSpace(Info.Description) ? "No description in the mod files." : Info.Description;
        /// <summary>Set for official mutators: which official playlists (co-op, versus) use it.</summary>
        public string OfficialGroup { get; set; }
        public int GroupRank { get; set; }
        public string Group => Info.Source == ContentSource.Official ? OfficialGroup ?? "Official"
                             : Info.Source == ContentSource.Custom ? "Added by name" : Info.ModName;
        public string SourceKey => Info.Source.ToString();
        public bool IsBase => Info.IsBaseClass;
        public bool NotRegistered => !Info.Registered;
        public string Warning => Info.Warning;
        public bool ShowId => !string.Equals(Info.Id, Info.DisplayName?.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        public bool IsActive
        {
            get => active;
            set { if (Set(ref active, value)) toggled?.Invoke(this, value); }
        }

        public void SetActiveSilently(bool value) { if (active != value) { active = value; Raise(nameof(IsActive)); } }
    }

    public sealed class ActiveMutatorItem : ObservableObject
    {
        private int index;
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public bool Missing { get; set; }
        public int Index { get => index; set => Set(ref index, value); }
    }

    public sealed class ModItem : ObservableObject
    {
        public ModInfo Info { get; }
        public ModItem(ModInfo info) { Info = info; }
        public string Name => Info.Name;
        public string Author => string.IsNullOrEmpty(Info.Author) ? "" : "by " + Info.Author;
        public string Summary => Info.Summary;
        public string Logo => Info.LogoFile;
        public string Version => string.IsNullOrEmpty(Info.Version) ? "" : "v" + Info.Version;
        public string Updated => Info.Updated.HasValue ? "Updated " + Info.Updated.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "";
        public int MutatorCount => Info.Mutators.Count(m => m.Registered && !m.IsBaseClass);
        public string Counts
        {
            get
            {
                var parts = new List<string>();
                if (MutatorCount > 0) parts.Add(MutatorCount + " mutator" + (MutatorCount == 1 ? "" : "s"));
                if (Info.Scenarios.Count > 0) parts.Add(Info.Scenarios.Count + " scenario" + (Info.Scenarios.Count == 1 ? "" : "s"));
                if (parts.Count == 0) parts.Add(Info.Tags.Count > 0 ? string.Join(" · ", Info.Tags) : "Content pack");
                return string.Join(" · ", parts);
            }
        }
        public string Tags => string.Join(" · ", Info.Tags);
        public bool HasWarnings => Info.Warnings.Count > 0;
        public string WarningText => string.Join("\n", Info.Warnings);
        public string Initials => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
    }

    public sealed class RuleItem : ObservableObject
    {
        private readonly Func<string, string, string> getValue;
        private readonly Action<string, string, string> setValue;
        public PropDef Prop { get; }
        public string ModeCls { get; }
        public string Default { get; }

        public RuleItem(PropDef prop, string modeCls, string def, Func<string, string, string> get, Action<string, string, string> set)
        {
            Prop = prop; ModeCls = modeCls; Default = def; getValue = get; setValue = set;
            ResetCommand = new RelayCommand(() => Value = null, () => Changed);
        }

        public string Key => Prop.Key;
        public string Label => Prop.Label;
        public string Description => Prop.Description;
        public const string UnavailableCategory = "Not in this mode";
        public bool IsAvailable { get; set; } = true;
        public string AvailabilityNote { get; set; }
        public string Category => IsAvailable ? Prop.Category : UnavailableCategory;
        public bool IsBool => Prop.IsBool;
        public bool IsNumber => Prop.IsNumber;
        public bool IsEnum => Prop.Type == "enum";
        public List<string> Options => Prop.Options;
        public double Min => Prop.Min ?? 0;
        public double Max => Math.Max(Prop.Max ?? 100, NumericDefault);
        public double Step => Prop.Step ?? (Prop.Type == "int" ? 1 : 0.1);
        public ICommand ResetCommand { get; }

        private double NumericDefault => double.TryParse(Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        /// <summary>Stored override, or null when the mode default is used.</summary>
        public string Value
        {
            get => getValue(ModeCls, Key);
            set
            {
                string v = value;
                if (v != null && LaunchPlanner.Same(v, Default, Prop)) v = null;
                setValue(ModeCls, Key, v);
                RaiseMany(nameof(Value), nameof(Effective), nameof(Changed), nameof(Number), nameof(Bool), nameof(Text), nameof(DisplayValue), nameof(EnumValue));
            }
        }

        public string Effective => Value ?? Default;
        public bool Changed => Value != null;
        public string DisplayDefault => IsBool ? (LaunchPlanner.IsTrue(Default) ? "On" : "Off") : Default;
        public string DisplayValue => IsBool ? (LaunchPlanner.IsTrue(Effective) ? "On" : "Off") : Effective;

        public double Number
        {
            get => double.TryParse(Effective, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            set
            {
                double v = Math.Round(value / Step) * Step;
                Value = Prop.Type == "int" ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
                                           : Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
            }
        }

        public string Text
        {
            get => Effective;
            set
            {
                if (IsNumber)
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) Number = d;
                    else Raise(nameof(Text));
                }
                else Value = value;
            }
        }

        public bool Bool
        {
            get => LaunchPlanner.IsTrue(Effective);
            set => Value = value ? "True" : "False";
        }

        public string EnumValue
        {
            get => Effective;
            set { if (value != null) Value = value; }
        }

        public void Refresh() => RaiseMany(nameof(Value), nameof(Effective), nameof(Changed), nameof(Number), nameof(Bool), nameof(Text), nameof(DisplayValue), nameof(EnumValue));
    }

    public sealed class CategoryItem : ObservableObject
    {
        private int changes;
        public string Name { get; set; }
        public int Count { get; set; }
        public int Changes { get => changes; set => Set(ref changes, value); }
    }

    public sealed class PresetItem
    {
        public string Name { get; set; }
        public string Group { get; set; }
        public string Description { get; set; }
        public object Source { get; set; }
        /// <summary>"Co-op" (PvE: solo or with AI teammates) or "Versus" (PvP, played against bots offline); empty for none.</summary>
        public string Tag { get; set; } = "";
        public bool HasTag => Tag.Length > 0;
        public bool IsVersus => Tag == "Versus";
        /// <summary>Extra line, e.g. which mode a playlist was made for.</summary>
        public string Note { get; set; } = "";
        public bool HasNote => Note.Length > 0;
    }

    public sealed class LaunchStepItem : ObservableObject
    {
        private StepState state;
        private string detail;
        public string Title { get; set; }
        public StepState State { get => state; set => Set(ref state, value); }
        public string Detail { get => detail; set => Set(ref detail, value); }
    }

    public sealed class LiveAction
    {
        public string Label { get; set; }
        public string Command { get; set; }
        public string Tip { get; set; }
        public bool CoopOnly { get; set; }
    }

    public sealed class LiveGroup
    {
        public string Title { get; set; }
        public string Badge { get; set; }
        public bool Cheat { get; set; }
        public List<LiveAction> Actions { get; set; } = new List<LiveAction>();
    }
}
