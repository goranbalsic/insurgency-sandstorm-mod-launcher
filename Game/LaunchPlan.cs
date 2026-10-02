using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    public sealed class LaunchPlan
    {
        public Profile Profile;
        public ScenarioInfo Scenario;
        public MapInfo Map;
        public ModeDef Mode;
        public bool Hardcore;
        public string Level;
        public string GameAlias;
        public List<string> Mutators = new List<string>();
        public List<string> MissingMutators = new List<string>();
        public List<MutatorInfo> MutatorInfos = new List<MutatorInfo>();
        public Dictionary<string, string> Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string OpenCommand;
        public string GameIniBlock = "";
        public List<UeIni.Section> IniSections = new List<UeIni.Section>();
        public int PlayerSlots;
        public string RestartKey = "";
        /// <summary>Everything done after the map loads, for display.</summary>
        public List<string> AfterLoad = new List<string>();
        /// <summary>Game mode properties set over RCON after the map loads (when the game was already running).</summary>
        public List<KeyValuePair<string, string>> LiveProperties = new List<KeyValuePair<string, string>>();
        /// <summary>Commands only the game's own console can run (cheats such as versus AI difficulty, the player's own lines).</summary>
        public List<string> ConsoleOnly = new List<string>();

        /// <summary>Keys of the player's extra URL options (they replace the launcher's value for the same key).</summary>
        public HashSet<string> ExtraOptionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The map URL the open command loads (what RCON's travel takes).</summary>
        public string TravelUrl => OpenCommand != null && OpenCommand.StartsWith("open ", StringComparison.Ordinal) ? OpenCommand.Substring(5) : null;
        public List<string> Warnings = new List<string>();
        public string Error;

        public bool IsValid => Error == null && Scenario != null;

        public string Title => Scenario == null ? T("No scenario selected")
            : $"{Map?.DisplayName ?? Scenario.MapKey} · {T(ModeTitle)}{(string.IsNullOrEmpty(Scenario.Side) ? "" : " (" + Scenario.Side + ")")} · {T(Profile.Lighting)}";

        public string ModeTitle => Hardcore ? "Hardcore Checkpoint" : Scenario?.GameModeName ?? "";
    }

    /// <summary>Builds the travel URL, the Game.ini block and the live commands for a profile.</summary>
    public static class LaunchPlanner
    {
        /// <summary>Options the multiplayer mode reads from the travel URL on every map load.</summary>
        public static readonly HashSet<string> UrlOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "RoundTime", "RoundLimit", "WinLimit", "InitialSupply", "MinimumPlayers", "MinimumPlayersInProgress", "SwitchTeamsEveryRound",
          "AutobalanceRoundEndThreshold", "BotQuota", "bBots", "bKillFeed", "bKillFeedSpectator", "bAllowThirdPersonSpectate", "bAllowDeathCamera" };

        /// <summary>Settings the game only reads when bots are first added, so they need a fresh game start.</summary>
        public static readonly HashSet<string> RestartOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FriendlyBotQuota" };

        public static LaunchPlan Build(Profile p, AppState state)
        {
            var plan = new LaunchPlan { Profile = p };
            var db = state.Rules;
            ScenarioInfo sc = null;
            if (!string.IsNullOrEmpty(p.CustomMapId))
            {
                var custom = state.Settings.CustomMaps.FirstOrDefault(c => c.Id == p.CustomMapId);
                if (custom != null)
                {
                    sc = new ScenarioInfo
                    {
                        Id = custom.Scenario, Level = custom.Level, MapKey = custom.Level, GameModeClass = custom.GameModeClass ?? "",
                        GameModeName = string.IsNullOrEmpty(custom.GameModeClass) ? "Custom" : GameCatalog.ModeName(custom.GameModeClass),
                        Category = GameCatalog.IsCoopClass(custom.GameModeClass) ? "Co-op" : "Versus", IsCoop = GameCatalog.IsCoopClass(custom.GameModeClass),
                        Source = ContentSource.Custom
                    };
                    plan.Map = new MapInfo { Key = custom.Level, DisplayName = custom.Label, Level = custom.Level, Source = ContentSource.Custom };
                }
            }
            if (sc == null)
            {
                plan.Map = state.Maps.FirstOrDefault(m => m.Key.Equals(p.MapKey ?? "", StringComparison.OrdinalIgnoreCase));
                sc = plan.Map?.Scenarios.FirstOrDefault(s => s.Id.Equals(p.ScenarioId ?? "", StringComparison.OrdinalIgnoreCase));
                if (sc == null && !string.IsNullOrEmpty(p.ScenarioId))
                    foreach (var m in state.Maps)
                    {
                        sc = m.Scenarios.FirstOrDefault(s => s.Id.Equals(p.ScenarioId, StringComparison.OrdinalIgnoreCase));
                        if (sc != null) { plan.Map = m; break; }
                    }
            }
            if (sc == null) { plan.Error = T("Pick a map and scenario first."); return plan; }
            // The level and scenario go into the open command as they are: a space, ? or | would break it or add options
            // (a custom map entry took any text, 2026-10-02 audit).
            if (!GameCatalog.SafeId(sc.Id) || !GameCatalog.SafeId(sc.Level))
            {
                plan.Error = T("The map's level or scenario name has characters the game cannot take (only letters, digits and _ - . / are allowed).");
                return plan;
            }
            plan.Scenario = sc;
            plan.Hardcore = p.Hardcore && sc.GameModeClass == "INSCheckpointGameMode";
            plan.Mode = db.ResolveMode(sc.GameModeClass, plan.Hardcore);
            string alias = UrlSafe(p.GameModeOverride, false);
            if (alias.Length != (p.GameModeOverride ?? "").Trim().Length) plan.Warnings.Add(T("Game mode override: spaces and ? & = \" | ; were left out."));
            plan.GameAlias = alias.Length > 0 ? alias : plan.Hardcore ? "CheckpointHardcore" : null;
            plan.Level = sc.Level.StartsWith("/Game/Maps/", StringComparison.OrdinalIgnoreCase)
                ? sc.Level.Substring(sc.Level.LastIndexOf('/') + 1) : sc.Level;

            // Mutators
            if (p.MutatorsEnabled)
                foreach (var id in p.Mutators)
                {
                    var info = state.FindMutator(id);
                    if (info == null && !state.Settings.CustomMutators.Contains(id, StringComparer.OrdinalIgnoreCase)) plan.MissingMutators.Add(id);
                    if (info != null) plan.MutatorInfos.Add(info);
                    plan.Mutators.Add(info?.Id ?? id);
                }
            if (plan.MissingMutators.Count > 0)
                plan.Warnings.Add(F("Not installed: {0}. Subscribe in the game's mod browser or remove them.", string.Join(", ", plan.MissingMutators)));
            foreach (var mi in plan.MutatorInfos.Where(m => m.IsBaseClass))
                plan.Warnings.Add(F("{0} is a base class used by other mutators; it usually does nothing on its own.", mi.DisplayName));

            // Rules for the played mode
            string cls = plan.Mode?.Cls;
            if (cls != null && p.Rules.TryGetValue(cls, out var ov))
                foreach (var kv in ov)
                {
                    string def = db.DefaultValue(cls, kv.Key);
                    if (def == null || !Same(def, kv.Value, db.Prop(kv.Key))) plan.Overrides[kv.Key] = kv.Value;
                }

            // Versus is played against bots unless the profile turns them off (the game's own default is off).
            if (plan.Mode != null && !plan.Mode.Coop && plan.Mode.Defaults.ContainsKey("bBots")
                && !(p.Rules.TryGetValue(cls, out var own) && own.ContainsKey("bBots")))
                plan.Overrides["bBots"] = "True";

            // Ambush and Free For All wait for two human players (MinimumPlayers=2) before the match starts, and bots
            // only join once it has started, so offline they sat at "waiting for players" with no bots. With bots on,
            // one player is enough (unless the profile sets these itself).
            bool versusBots = plan.Mode != null && !plan.Mode.Coop && plan.Overrides.TryGetValue("bBots", out var vb) && IsTrue(vb);
            // Ambush, Defusal and Free For All also default to BotQuota=0: bots "on" but none would come.
            if (versusBots && !plan.Overrides.ContainsKey("BotQuota") && int.TryParse(db.DefaultValue(cls, "BotQuota"), out int dq) && dq <= 0)
                plan.Overrides["BotQuota"] = "5";
            // Bots on but a team size of 0 (set by hand) would also leave the match empty: one per team at least.
            if (versusBots && plan.Overrides.TryGetValue("BotQuota", out var setQuota) && int.TryParse(setQuota, out int sq) && sq <= 0)
            {
                plan.Overrides["BotQuota"] = "1";
                plan.Warnings.Add(T("Players per team was 0 with bots on; 1 is used (you against one bot)."));
            }
            // The launcher's default for the minimums (SetupEngine.LauncherDefault: 1), with bots or without: offline nobody
            // else comes; a server that should wait for a second player keeps 2 in its rules.
            foreach (var key in new[] { "MinimumPlayers", "MinimumPlayersInProgress" })
            {
                string launcherMin = SetupEngine.LauncherDefault(db, cls, key);
                if (launcherMin != null && launcherMin != db.DefaultValue(cls, key) && !(p.Rules.TryGetValue(cls, out var own3) && own3.ContainsKey(key)))
                    plan.Overrides[key] = launcherMin;
            }

            if (plan.Mode != null && plan.Mode.Coop
                && int.TryParse(plan.Overrides.TryGetValue("MinimumEnemies", out var mn) ? mn : db.DefaultValue(cls, "MinimumEnemies"), out int minE)
                && int.TryParse(plan.Overrides.TryGetValue("MaximumEnemies", out var mx) ? mx : db.DefaultValue(cls, "MaximumEnemies"), out int maxE) && minE > maxE)
                plan.Warnings.Add(F("Minimum enemies ({0}) is above maximum enemies ({1}); the game then uses the maximum.", minE, maxE));

            // Co-op AI teammates only join when the mode fills teams with bots (bBots is off by default).
            if (plan.Mode != null && plan.Mode.Coop && plan.Mode.Defaults.ContainsKey("bBots")
                && int.TryParse(plan.Overrides.TryGetValue("FriendlyBotQuota", out var fbq) ? fbq : db.DefaultValue(cls, "FriendlyBotQuota"), out int fbn) && SetupEngine.AiTeammates(fbn) > 0
                && !(p.Rules.TryGetValue(cls, out var own2) && own2.ContainsKey("bBots")))
                plan.Overrides["bBots"] = "True";

            // Travel URL
            var url = new StringBuilder();
            url.Append("open ").Append(plan.Level).Append("?Scenario=").Append(sc.Id);
            // AI teammates take player slots, so there must be room for you plus all of them.
            plan.PlayerSlots = Math.Max(1, p.MaxPlayers);
            bool wantsMates = false;
            if (plan.Mode != null && plan.Mode.Coop)
            {
                string fb = plan.Overrides.TryGetValue("FriendlyBotQuota", out var ov2) ? ov2 : db.DefaultValue(cls, "FriendlyBotQuota");
                // FriendlyBotQuota counts you: 1 is no AI teammate (setups saved by 1.10.0 and older can hold it).
                wantsMates = int.TryParse(fb, out int mates) && SetupEngine.AiTeammates(mates) > 0;
                // Offline nobody else joins, so plenty of slots costs nothing; too few and the AI teammates do not join.
                if (wantsMates) plan.PlayerSlots = Math.Max(plan.PlayerSlots, Math.Max(8, 1 + mates + 2));
            }
            else if (versusBots)
            {
                // Versus bots take player slots as well (BotQuota per team, or all the bots in Free For All): a profile
                // left at 1 slot from Lone Wolf co-op meant no bot could join.
                string bq = plan.Overrides.TryGetValue("BotQuota", out var bqo) ? bqo : db.DefaultValue(cls, "BotQuota");
                int quota = int.TryParse(bq, out int q) && q > 0 ? q : 5;
                plan.PlayerSlots = Math.Max(plan.PlayerSlots, Math.Min(64, 2 * quota + 2));
            }
            // The player's extra URL options (Advanced): each key once (the last one written), and they replace the
            // launcher's value for the same key; the scenario always stays the launcher's.
            string extraText = UrlSafe(p.ExtraUrlOptions, true);
            if (extraText.Length != (p.ExtraUrlOptions ?? "").Trim().Length) plan.Warnings.Add(T("Extra URL options: spaces, quotes, | and ; were left out (they would break the open command)."));
            var extra = new List<KeyValuePair<string, string>>();
            foreach (var part in extraText.Split(new[] { '?' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string key = eq < 0 ? part : part.Substring(0, eq);
                if (key.Length == 0) continue;
                if (key.Equals("Scenario", StringComparison.OrdinalIgnoreCase)) { plan.Warnings.Add(T("Extra URL options: Scenario is set by the map picked, so it was left out.")); continue; }
                int at = extra.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                var kv = new KeyValuePair<string, string>(key, eq < 0 ? null : part.Substring(eq + 1));
                if (at >= 0) extra[at] = kv; else extra.Add(kv);
            }
            plan.ExtraOptionKeys = new HashSet<string>(extra.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
            void Option(string key, string value)
            {
                if (plan.ExtraOptionKeys.Contains(key)) { plan.Warnings.Add(F("Extra URL options: {0} replaces the launcher's value ({1}).", key, value)); return; }
                url.Append('?').Append(key).Append('=').Append(value);
            }
            Option("MaxPlayers", plan.PlayerSlots.ToString(CultureInfo.InvariantCulture));
            Option("Lighting", p.Lighting == "Night" ? "Night" : "Day");
            if (!string.IsNullOrEmpty(plan.GameAlias)) Option("game", plan.GameAlias);
            // bSoloGame stops AI teammates from joining (seen in the game), so it is left out when co-op has teammates.
            if (state.Settings.SoloGameFlag && !wantsMates) Option("bSoloGame", "1");
            foreach (var kv in plan.Overrides)
            {
                if (!UrlOptions.Contains(kv.Key)) continue;
                var prop = db.Prop(kv.Key);
                if (prop != null && prop.IsBool)
                {
                    if (IsTrue(kv.Value)) Option(kv.Key, "1");
                }
                else Option(kv.Key, kv.Value);
            }
            if (plan.Mutators.Count > 0) Option("Mutators", string.Join(",", plan.Mutators));
            foreach (var kv in extra) url.Append('?').Append(kv.Key).Append(kv.Value == null ? "" : "=" + kv.Value);
            plan.OpenCommand = url.ToString();

            // Game.ini sections (every mode the profile customises)
            plan.IniSections = BuildIniSections(p, state);
            plan.GameIniBlock = UeIni.Render(plan.IniSections);
            plan.RestartKey = RestartKeyFor(p, db);

            // After-load: game mode properties go over RCON (gamemodeproperty); cheats and the player's own lines need the console.
            if (state.Settings.ApplyLiveRules && cls != null)
                foreach (var kv in plan.Overrides)
                {
                    // FriendlyBotQuota is also read at game start from Game.ini; setting it again does no harm.
                    plan.LiveProperties.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                    plan.AfterLoad.Add("gamemodeproperty " + kv.Key + " " + kv.Value);
                }
            if (plan.Mode != null && !plan.Mode.Coop && p.Rules.TryGetValue("*", out var global) && global.TryGetValue("AIDifficulty", out var versusDifficulty))
            {
                plan.ConsoleOnly.Add("EnableCheats");
                plan.ConsoleOnly.Add("AIDifficulty " + versusDifficulty);
            }
            else if (p.EnableCheatsAfterLoad) plan.ConsoleOnly.Add("EnableCheats");
            foreach (var line in (p.AfterLoadCommands ?? "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith(";")) plan.ConsoleOnly.Add(t);
            }
            foreach (var c in plan.ConsoleOnly) plan.AfterLoad.Add("console: " + c);
            if (plan.Overrides.Count > 0 && cls == null)
                plan.Warnings.Add(T("This scenario's game mode is not in the rules database, so rule changes are skipped."));
            return plan;
        }

        /// <summary>Game.ini sections for every mode the profile customises, plus the profile's own extra lines.</summary>
        public static List<UeIni.Section> BuildIniSections(Profile p, AppState state)
        {
            var db = state.Rules;
            var sections = new List<UeIni.Section>();
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (mode.Key == "*") continue;
                if (db.Mode(mode.Key) == null) continue;
                var values = new List<KeyValuePair<string, string>>();
                foreach (var kv in mode.Value.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string d = db.DefaultValue(mode.Key, kv.Key);
                    if (d != null && Same(d, kv.Value, db.Prop(kv.Key))) continue;
                    values.Add(new KeyValuePair<string, string>(kv.Key, IniValue(kv.Value, db.Prop(kv.Key))));
                }
                if (values.Count == 0) continue;
                sections.Add(new UeIni.Section("/Script/Insurgency." + mode.Key) { Values = values });
                // Blueprint subclasses (e.g. the Skirmish blueprint) read their own section too.
                foreach (var bpPath in ModeBlueprints(state, mode.Key))
                    sections.Add(new UeIni.Section(bpPath) { Values = new List<KeyValuePair<string, string>>(values) });
            }
            if (!string.Equals(p.CustomIniMode ?? "Off", "Off", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.CustomIniText))
                foreach (var s in UeIni.Parse(p.CustomIniText))
                {
                    var existing = sections.FirstOrDefault(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null) sections.Add(s);
                    else
                    {
                        // The typed lines replace the rules of the same keys, and all of them stay, in their order: each one
                        // took the earlier typed lines of its key out (+Array=A went, MyKey=1 became MyKey=2, 2026-10-02 audit).
                        var typed = new HashSet<string>(s.Values.Select(v => UeIni.KeyOf(v.Key + "=")), StringComparer.OrdinalIgnoreCase);
                        existing.Values.RemoveAll(x => typed.Contains(UeIni.KeyOf(x.Key + "=")));
                        existing.Values.AddRange(s.Values);
                    }
                }
            return sections;
        }

        public static IEnumerable<string> ModeBlueprints(AppState state, string modeCls) =>
            state.AllScenarios.Where(s => s.GameModePath != null && s.Category != "Training"
                                          && !s.GameModePath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)
                                          && s.GameModePath.IndexOf("/Develop", StringComparison.OrdinalIgnoreCase) < 0
                                          && state.Rules.ResolveMode(s.GameModeClass)?.Cls == modeCls)
                              .Select(s => s.GameModePath).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The new Game.ini text for a launch: the plan merged into the current file ("Replace" keeps only the plan).
        /// <paramref name="earlier"/> = the player's own extra keys written last time, removed again when no longer wanted.
        /// </summary>
        public static string MergeGameIni(string current, LaunchPlan plan, RulesDb db, IEnumerable<string> earlier, bool allowReplace = true)
        {
            if (allowReplace && string.Equals(plan.Profile?.CustomIniMode, "Replace", StringComparison.OrdinalIgnoreCase)) return UeIni.Render(plan.IniSections) + "\r\n";
            var old = new HashSet<string>(earlier ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return UeIni.MergeSections(current, plan.IniSections, (s, k) => IsManagedIniKey(db, s, k) || old.Contains(s + "\n" + k));
        }

        /// <summary>Game.ini as a launch writes it: the plan merged in, and the launcher's [Rcon] section kept (also in Replace mode).</summary>
        public static string GameIniForLaunch(string current, LaunchPlan plan, RulesDb db, IEnumerable<string> earlier, AppSettings settings) =>
            RconSetup.Apply(MergeGameIni(current, plan, db, earlier), settings);

        /// <summary>The player's own extra Game.ini keys in a plan ("Section\nKey", array operators removed), remembered for the next launch.</summary>
        public static List<string> PlayerIniKeys(LaunchPlan plan, RulesDb db) =>
            plan.IniSections.SelectMany(s => s.Values.Where(v => !IsManagedIniKey(db, s.Name, v.Key)).Select(v => s.Name + "\n" + UeIni.KeyOf(v.Key + "=")))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Keys the launcher owns in Game.ini: every match rule it knows, in any game mode section. Old copies of
        /// them (from earlier launches, or left behind when the game rewrote the file) are removed before writing.
        /// </summary>
        public static bool IsManagedIniKey(RulesDb db, string section, string key) =>
            db.Prop(key) != null && (section.IndexOf("GameMode", StringComparison.OrdinalIgnoreCase) >= 0
                                     || db.Modes.Any(m => section.EndsWith("." + m.Cls, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Fingerprint of everything that only takes effect when the game starts.</summary>
        public static string RestartKeyFor(Profile p, RulesDb db) => CombineRestartKey(IniRestartPart(p, db), p.LaunchRuleset);

        /// <summary>
        /// The Game.ini part of it: start-only rules, and the player's own Game.ini lines (mutator settings and the like are
        /// read once at game start; edits never reached a running game, 2026-10-02 audit). Without own lines it is the text
        /// older versions hashed, so a game started by one of them is not restarted for nothing.
        /// </summary>
        public static string IniRestartPart(Profile p, RulesDb db)
        {
            var sb = new StringBuilder();
            foreach (var mode in p.Rules.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var kv in mode.Value.Where(k => RestartOnly.Contains(k.Key)).OrderBy(k => k.Key))
                {
                    string d = db.DefaultValue(mode.Key, kv.Key);
                    if (d != null && Same(d, kv.Value, db.Prop(kv.Key))) continue;
                    sb.Append(mode.Key).Append('.').Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                }
            if (!string.Equals(p.CustomIniMode ?? "Off", "Off", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.CustomIniText))
                sb.Append("ini=").Append(Hash(p.CustomIniMode + ":" + UeIni.Render(UeIni.Parse(p.CustomIniText)))).Append(';');
            return sb.ToString();
        }

        /// <summary>The restart key from its Game.ini part and the official ruleset the game is started with (a start argument).</summary>
        public static string CombineRestartKey(string iniPart, string ruleset) => Hash((iniPart ?? "") + "ruleset=" + (ruleset ?? ""));

        /// <summary>"Mode|Key" of every rule the plan writes into Game.ini for a game mode (the game reads them at start).</summary>
        public static List<string> WrittenRuleKeys(LaunchPlan plan, RulesDb db)
        {
            const string prefix = "/Script/Insurgency.";
            return plan.IniSections.Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && db.Mode(s.Name.Substring(prefix.Length)) != null)
                                   .SelectMany(s => s.Values.Where(v => db.Prop(v.Key) != null).Select(v => s.Name.Substring(prefix.Length) + "|" + v.Key))
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// For a game that is already running: the rules its Game.ini set at start that this plan leaves at the default, set
        /// back to the default after the map loads (Realism, then Game defaults kept Realism's rules, 2026-10-02 audit).
        /// </summary>
        public static List<KeyValuePair<string, string>> LiveResets(LaunchPlan plan, RulesDb db, IEnumerable<string> startedWith)
        {
            var list = new List<KeyValuePair<string, string>>();
            string cls = plan.Mode?.Cls;
            if (cls == null || startedWith == null) return list;
            foreach (var id in startedWith)
            {
                int bar = id.IndexOf('|');
                if (bar <= 0 || !id.Substring(0, bar).Equals(cls, StringComparison.OrdinalIgnoreCase)) continue;
                string key = id.Substring(bar + 1);
                if (plan.Overrides.ContainsKey(key) || list.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                string def = db.DefaultValue(cls, key);
                if (def != null) list.Add(new KeyValuePair<string, string>(key, def));
            }
            return list;
        }

        public static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").Substring(0, 16);
        }

        /// <summary>Text for the travel URL: no whitespace or characters the console treats specially. Options keep ? and =.</summary>
        public static string UrlSafe(string text, bool options)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '"' || c == '|' || c == ';') continue;
                if (!options && (c == '?' || c == '&' || c == '=')) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool IsTrue(string v) => v != null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

        public static bool Same(string a, string b, PropDef prop)
        {
            if (a == null || b == null) return a == b;
            if (prop != null && prop.IsBool) return IsTrue(a) == IsTrue(b);
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return Math.Abs(x - y) < 1e-6;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static string IniValue(string v, PropDef prop)
        {
            if (prop != null && prop.IsBool) return IsTrue(v) ? "True" : "False";
            return v;
        }
    }
}
