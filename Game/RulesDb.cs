using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    public sealed class ModeDef
    {
        public string Cls { get; set; }
        public string Alias { get; set; }
        public string Name { get; set; }
        public bool Coop { get; set; }
        public Dictionary<string, string> Defaults { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class PropDef
    {
        public string Key, Label, Category, Description, Type;
        public double? Min, Max, Step;
        public List<string> Options = new List<string>();
        public bool IsBool => Type == "bool";
        public bool IsNumber => Type == "int" || Type == "float";
    }

    public sealed class RulesetDef
    {
        public string Id, Name;
        public Dictionary<string, Dictionary<string, string>> Rules = new Dictionary<string, Dictionary<string, string>>();
        public List<string> Notes = new List<string>();
        public List<string> Mutators = new List<string>();
    }

    public sealed class PlaylistDef
    {
        public string Id, Key, Title, Description, Type, Lighting, GameAlias, Ruleset;
        public List<string> Modes = new List<string>(), Mutators = new List<string>(), Missing = new List<string>(), Features = new List<string>();
        public Dictionary<string, string> CoopRules = new Dictionary<string, string>();
        public bool IsCoop => string.Equals(Type, "Coop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Game-mode settings database measured from the live game (property names, per-mode
    /// defaults), plus the official rulesets and playlists. Embedded in the launcher.
    /// </summary>
    public sealed class RulesDb
    {
        public List<ModeDef> Modes = new List<ModeDef>();
        public List<PropDef> Properties = new List<PropDef>();
        public List<RulesetDef> Rulesets = new List<RulesetDef>();
        public List<PlaylistDef> Playlists = new List<PlaylistDef>();
        private Dictionary<string, PropDef> propIndex;
        private Dictionary<string, ModeDef> modeIndex;

        public static RulesDb LoadEmbedded()
        {
            var asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames().First(n => n.EndsWith("GameRules.json", StringComparison.OrdinalIgnoreCase));
            using (var s = asm.GetManifestResourceStream(name))
            using (var reader = new StreamReader(s))
                return FromJson(Json.Parse(reader.ReadToEnd()));
        }

        private static RulesDb FromJson(object root)
        {
            var db = new RulesDb();
            foreach (var m in root.Get("modes").Arr())
            {
                var def = new ModeDef { Cls = m.Get("cls").Str(), Alias = m.Get("alias").Str() ?? "", Name = m.Get("name").Str(), Coop = m.Get("coop") is bool b && b };
                if (m.Get("defaults") is Dictionary<string, object> d)
                    foreach (var kv in d) def.Defaults[kv.Key] = kv.Value.Str();
                db.Modes.Add(def);
            }
            foreach (var p in root.Get("properties").Arr())
            {
                var def = new PropDef
                {
                    Key = p.Get("key").Str(), Label = p.Get("label").Str(), Category = p.Get("cat").Str(),
                    Description = p.Get("desc").Str(), Type = p.Get("type").Str(),
                    Min = Num(p.Get("min")), Max = Num(p.Get("max")), Step = Num(p.Get("step"))
                };
                foreach (var o in p.Get("options").Arr()) def.Options.Add(o.Str());
                db.Properties.Add(def);
            }
            foreach (var r in root.Get("rulesets").Arr())
            {
                var def = new RulesetDef { Id = r.Get("id").Str(), Name = r.Get("name").Str() };
                if (r.Get("rules") is Dictionary<string, object> rules)
                    foreach (var kv in rules)
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (kv.Value is Dictionary<string, object> inner) foreach (var p in inner) dict[p.Key] = p.Value.Str();
                        def.Rules[kv.Key] = dict;
                    }
                foreach (var n in r.Get("notes").Arr()) def.Notes.Add(n.Str());
                foreach (var n in r.Get("mutators").Arr()) def.Mutators.Add(n.Str());
                db.Rulesets.Add(def);
            }
            foreach (var p in root.Get("playlists").Arr())
            {
                var def = new PlaylistDef
                {
                    Id = p.Get("id").Str(), Key = p.Get("key").Str(), Title = p.Get("title").Str(), Description = p.Get("desc").Str(),
                    Type = p.Get("type").Str(), Lighting = p.Get("lighting").Str(), GameAlias = p.Get("gameAlias").Str() ?? ""
                };
                foreach (var x in p.Get("modes").Arr()) def.Modes.Add(x.Str());
                foreach (var x in p.Get("mutators").Arr()) def.Mutators.Add(x.Str());
                foreach (var x in p.Get("missing").Arr()) def.Missing.Add(x.Str());
                foreach (var x in p.Get("features").Arr()) def.Features.Add(x.Str());
                var rules = p.Get("rules") as Dictionary<string, object>;
                if (rules != null)
                {
                    if (rules.TryGetValue("ruleset", out var rs)) def.Ruleset = rs.Str();
                    if (rules.TryGetValue("*coop", out var coop) && coop is Dictionary<string, object> cr)
                        foreach (var kv in cr) def.CoopRules[kv.Key] = kv.Value.Str();
                }
                db.Playlists.Add(def);
            }
            db.propIndex = db.Properties.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            db.modeIndex = db.Modes.ToDictionary(x => x.Cls, StringComparer.OrdinalIgnoreCase);
            return db;
        }

        private static double? Num(object o)
        {
            switch (o)
            {
                case long l: return l;
                case double d: return d;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v): return v;
                default: return null;
            }
        }

        public PropDef Prop(string key) => key != null && propIndex.TryGetValue(key, out var p) ? p : null;
        public ModeDef Mode(string cls) => cls != null && modeIndex.TryGetValue(cls, out var m) ? m : null;

        /// <summary>Maps a scenario's game-mode class (native or blueprint) to a mode in the database.</summary>
        public ModeDef ResolveMode(string gameModeClass, bool hardcore = false)
        {
            if (string.IsNullOrEmpty(gameModeClass)) return null;
            string cls = gameModeClass;
            int dot = cls.LastIndexOf('.');
            if (dot >= 0) cls = cls.Substring(dot + 1);
            cls = cls.Trim('\'', '"');
            if (hardcore && cls.Equals("INSCheckpointGameMode", StringComparison.OrdinalIgnoreCase)) cls = "INSCheckpointHardcoreGameMode";
            var direct = Mode(cls);
            if (direct != null) return direct;
            string lower = cls.ToLowerInvariant();
            foreach (var (needle, target) in new[]
            {
                ("hardcore", "INSCheckpointHardcoreGameMode"), ("checkpoint", "INSCheckpointGameMode"), ("outpost", "INSOutpostGameMode"),
                ("survival", "INSSurvivalGameMode"), ("push", "INSPushGameMode"), ("firefight", "INSFirefightGameMode"),
                ("frontline", "INSFrontlineGameMode"), ("domination", "DominationGameMode"), ("ambush", "INSAmbushGameMode"),
                ("defus", "INSDefuseGameMode"), ("deathmatch", "INSTeamDeathmatchGameMode"), ("freeforall", "INSFreeForAllMode"),
                ("skirmish", "INSSkirmishGameMode")
            })
                if (lower.Contains(needle)) return Mode(target);
            return null;
        }

        public string DefaultValue(string modeCls, string key)
        {
            var m = Mode(modeCls);
            return m != null && m.Defaults.TryGetValue(key, out var v) ? v : null;
        }
    }
}
