using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    /// <summary>One line of a server map cycle: a scenario, optionally with its lighting and a game mode that replaces the scenario's.</summary>
    public sealed class MapCycleEntry
    {
        public string Scenario;
        public string Lighting;
        public string Mode;
        /// <summary>Options="?Mutators=..?.." of the entry (only shown; the entry is then kept as written).</summary>
        public string Options;
        /// <summary>Text kept exactly as it was: a comment, or an entry with settings the launcher does not know (one or more lines).</summary>
        public string Raw;

        public bool IsEntry => !string.IsNullOrEmpty(Scenario);

        public string Line
        {
            get
            {
                if (Raw != null) return Raw;
                if (string.IsNullOrEmpty(Lighting) && string.IsNullOrEmpty(Mode)) return Scenario;
                var sb = new StringBuilder("(Scenario=\"").Append(Scenario).Append('"');
                if (!string.IsNullOrEmpty(Lighting)) sb.Append(",Lighting=\"").Append(Lighting).Append('"');
                if (!string.IsNullOrEmpty(Mode)) sb.Append(",Mode=\"").Append(Mode).Append('"');
                return sb.Append(')').ToString();
            }
        }
    }

    /// <summary>
    /// MapCycle.txt of a dedicated server: one scenario per line, or (Scenario="...",Lighting="Night",Mode="...",Options="..."),
    /// which may run over several lines. Comments and lines the launcher does not understand are kept as they are.
    /// </summary>
    public static class MapCycle
    {
        private static readonly Regex Field = new Regex(@"^\s*(?<k>\w+)\s*=\s*[""“”]?(?<v>[^""“”]*?)[""“”]?\s*$", RegexOptions.Compiled);
        private static readonly Regex Plain = new Regex(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

        public static List<MapCycleEntry> Parse(string text)
        {
            var list = new List<MapCycleEntry>();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart('﻿'), t = line.Trim();
                if (t.Length == 0) continue;
                if (Plain.IsMatch(t)) { list.Add(new MapCycleEntry { Scenario = t }); continue; }
                if (t[0] == '(')
                {
                    // An entry ends where its bracket closes, which can be lines later (Options="..." written in parts,
                    // mod.io #1865932). Never closed: only this line is kept as written.
                    string whole = line.TrimStart();
                    int last = i;
                    while (Close(whole) < 0 && last + 1 < lines.Length) whole += "\n" + lines[++last];
                    int close = Close(whole);
                    if (close >= 0 && whole.Substring(close + 1).Trim().Length == 0)
                    {
                        var e = new MapCycleEntry();
                        bool known = true;
                        foreach (var part in Fields(whole.Substring(1, close - 1)))
                        {
                            var f = Field.Match(part);
                            if (!f.Success) { known = false; continue; }
                            string k = f.Groups["k"].Value, v = f.Groups["v"].Value.Trim();
                            if (k.Equals("Scenario", StringComparison.OrdinalIgnoreCase)) e.Scenario = v;
                            else if (k.Equals("Lighting", StringComparison.OrdinalIgnoreCase)) e.Lighting = v;
                            else if (k.Equals("Mode", StringComparison.OrdinalIgnoreCase)) e.Mode = v;
                            else { known = false; if (k.Equals("Options", StringComparison.OrdinalIgnoreCase)) e.Options = v; }
                        }
                        // Settings the launcher does not know stay exactly as they were written, line breaks too.
                        if (!known || !e.IsEntry) e.Raw = whole.TrimEnd().Replace("\n", "\r\n");
                        list.Add(e);
                        i = last;
                        continue;
                    }
                }
                list.Add(new MapCycleEntry { Raw = t });
            }
            return list;
        }

        private static bool IsQuote(char c) => c == '"' || c == '“' || c == '”';

        /// <summary>Where the bracket that opens <paramref name="s"/> closes; -1 while it is open. Brackets in quotes do not count.</summary>
        private static int Close(string s)
        {
            int depth = 0;
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (IsQuote(c)) quoted = !quoted;
                else if (quoted) continue;
                else if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The fields of an entry: split at the commas outside quotes (Options="?Mutators=A,B" is one field).</summary>
        private static List<string> Fields(string body)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in body)
            {
                if (IsQuote(c)) quoted = !quoted;
                if (c == ',' && !quoted) { parts.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>The mutators an entry sets for its map (Options="?Mutators=A,B"); they replace the start URL's on that map.</summary>
        public static List<string> MutatorsOf(MapCycleEntry e)
        {
            var m = Regex.Match(e?.Options ?? "", @"(?:^|\?)\s*Mutators\s*=(?<v>[^?]*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["v"].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();
        }

        public static string Render(IEnumerable<MapCycleEntry> entries) =>
            string.Concat(entries.Select(e => e.Line + "\r\n"));

        /// <summary>The entry for a match from Play.</summary>
        public static MapCycleEntry For(LaunchPlan plan) => new MapCycleEntry
        {
            Scenario = plan.Scenario.Id,
            Lighting = plan.Profile.Lighting == "Night" ? "Night" : "Day",
            Mode = string.IsNullOrEmpty(plan.GameAlias) ? null : plan.GameAlias,
        };
    }

    /// <summary>What starting the dedicated server does: its command line and the files written first.</summary>
    public sealed class ServerPlan
    {
        public LaunchPlan Match;
        /// <summary>The first map: map?Scenario=...?options.</summary>
        public string Url;
        /// <summary>Everything after the URL, one switch each.</summary>
        public List<string> Args = new List<string>();
        /// <summary>The whole server Game.ini as it will be written (the match rules and the RCON section merged in).</summary>
        public string GameIni;
        public List<string> ManagedIniKeys = new List<string>();
        /// <summary>Admins.txt (null = the file is left alone and no admin list is used).</summary>
        public string AdminsText;
        /// <summary>Mods the match needs (its map or mutators come from them).</summary>
        public List<long> MatchModIds = new List<long>();
        public string MapCycleName;
        /// <summary>A map cycle file outside the server's Config\Server folder, copied there before the start.</summary>
        public string MapCycleCopyFrom;
        public List<string> Warnings = new List<string>();
        public string Error;
        /// <summary>The player's own options (Server settings > start with my own options): used as they are instead of Url and Args.</summary>
        public string OwnArgs;
        /// <summary>The server loads mods (-Mods).</summary>
        public bool ModsOn;
        /// <summary>The start passes a security code: it logs the server in to mod.io once and is used up.</summary>
        public bool UsesCode;
        /// <summary>Ports the server takes (for the check that nothing else has them).</summary>
        public int GamePort, QueryPort, RconPort;
        /// <summary>The first map, for the progress text.</summary>
        public string StartMap;

        public bool IsValid => Error == null;
        public string CommandLine => OwnArgs ?? (Url == null ? "" : Url + (Args.Count > 0 ? " " + string.Join(" ", Args) : ""));

        /// <summary>The command line to show and log: the Steam server token, the join password and a security code hidden.</summary>
        public string ShownCommandLine => ServerModio.HideCode(Regex.Replace(Regex.Replace(Regex.Replace(CommandLine, @"(-GSLTToken=)\S+", "$1<your token>", RegexOptions.IgnoreCase),
                                                        @"(\?Password=)[^?\s]+", "$1<password>", RegexOptions.IgnoreCase),
                                                        @"(-(?:RconPassword|GameStatsToken)=)\S+", "$1<hidden>", RegexOptions.IgnoreCase));
    }

    public static class ServerPlanner
    {
        public const string AdminsName = "Admins";
        private static readonly Regex SafePassword = new Regex(@"^[A-Za-z0-9_\-\.!@#\$%\^\*\+~]*$", RegexOptions.Compiled);
        private static readonly Regex SafeCycleName = new Regex(@"^[A-Za-z0-9_\-\.]+$", RegexOptions.Compiled);

        public const string TeamInfoSection = "/Script/Insurgency.TeamInfo";
        public const string VoteKickIssue = "/Script/Insurgency.VoteIssueKick";

        /// <summary>The ruleset the admin guide calls official rules (the game takes OfficialRules or RS_OfficialRules).</summary>
        public static bool IsOfficialRules(string ruleset) =>
            string.Equals(ruleset, "OfficialRules", StringComparison.OrdinalIgnoreCase) || string.Equals(ruleset, "RS_OfficialRules", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Player voting with the vote kick (the admin guide's [/Script/Insurgency.TeamInfo] lines). Switched off, only the
        /// launcher's own lines go again: voting set up by hand with other vote issues stays as it is.
        /// </summary>
        public static string ApplyVoteKick(string gameIni, bool on)
        {
            bool Ours(string section, string key) => section.Equals(TeamInfoSection, StringComparison.OrdinalIgnoreCase)
                                                     && (key.Equals("bVotingEnabled", StringComparison.OrdinalIgnoreCase) || key.Equals("TeamVoteIssues", StringComparison.OrdinalIgnoreCase));
            if (on)
                return UeIni.MergeSections(gameIni ?? "", new[]
                {
                    new UeIni.Section(TeamInfoSection) { Values = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("bVotingEnabled", "True"),
                        new KeyValuePair<string, string>("TeamVoteIssues", VoteKickIssue),
                    } }
                }, (sec, key) => false);
            var sec = UeIni.Parse(gameIni ?? "").LastOrDefault(x => x.Name.Equals(TeamInfoSection, StringComparison.OrdinalIgnoreCase));
            if (sec == null) return gameIni ?? "";
            var issues = UeIni.ReadArray(gameIni ?? "", TeamInfoSection, "TeamVoteIssues");
            bool onlyOurs = issues.All(i => i.Equals(VoteKickIssue, StringComparison.OrdinalIgnoreCase));
            return onlyOurs ? UeIni.MergeSections(gameIni ?? "", new UeIni.Section[0], Ours) : gameIni ?? "";
        }

        /// <summary>True when the server's Game.ini has the vote kick switched on.</summary>
        public static bool VoteKickOn(string gameIni)
        {
            var sec = UeIni.Parse(gameIni ?? "").LastOrDefault(x => x.Name.Equals(TeamInfoSection, StringComparison.OrdinalIgnoreCase));
            return sec != null && sec.Values.Any(v => UeIni.KeyOf(v.Key + "=").Equals("bVotingEnabled", StringComparison.OrdinalIgnoreCase) && LaunchPlanner.IsTrue(v.Value.Trim()))
                   && UeIni.ReadArray(gameIni, TeamInfoSection, "TeamVoteIssues").Any(i => i.Equals(VoteKickIssue, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Steam IDs (64-bit) from free text, one per line; anything else is reported.</summary>
        public static List<string> SteamIds(string text, List<string> bad)
        {
            var ids = new List<string>();
            foreach (var raw in (text ?? "").Split('\n', ',', ';'))
            {
                string t = raw.Trim();
                int comment = t.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) t = t.Substring(0, comment).Trim();
                if (t.Length == 0) continue;
                if (Regex.IsMatch(t, @"^7656119\d{10}$")) { if (!ids.Contains(t)) ids.Add(t); }
                else bad?.Add(t);
            }
            return ids;
        }

        /// <summary>mod.io mod ids from free text, one per line.</summary>
        public static List<long> ModIds(string text, List<string> bad)
        {
            var ids = new List<long>();
            foreach (var raw in (text ?? "").Split('\n', ',', ';'))
            {
                string t = raw.Trim();
                int comment = t.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) t = t.Substring(0, comment).Trim();
                if (t.Length == 0) continue;
                if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0) { if (!ids.Contains(id)) ids.Add(id); }
                else bad?.Add(t);
            }
            return ids;
        }

        /// <summary>The map cycle file in use: the chosen one, or MapCycle.txt in the server's Config\Server folder.</summary>
        public static string MapCyclePath(AppSettings s, ServerInstall inst) =>
            !string.IsNullOrWhiteSpace(s.ServerMapCycleFile) ? s.ServerMapCycleFile.Trim()
            : inst?.ServerConfigDir == null ? null : Path.Combine(inst.ServerConfigDir, "MapCycle.txt");

        /// <summary>The Play match as the server's first map: its own player count and join password instead of the offline ones.</summary>
        private static string ServerUrl(LaunchPlan match, int maxPlayers, string password)
        {
            var parts = match.TravelUrl.Split('?');
            var url = new StringBuilder(parts[0]);
            foreach (var opt in parts.Skip(1))
            {
                string key = opt.Split('=')[0];
                if (key.Equals("bSoloGame", StringComparison.OrdinalIgnoreCase) || key.Equals("MaxPlayers", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Password", StringComparison.OrdinalIgnoreCase)) continue;
                url.Append('?').Append(opt);
            }
            url.Append("?MaxPlayers=").Append(maxPlayers.ToString(CultureInfo.InvariantCulture));
            if (password.Length > 0) url.Append("?Password=").Append(password);
            return url.ToString();
        }

        /// <summary>
        /// Where the launcher reaches the server's RCON: the player's own -RconPassword/-RconListenPort, or the [Rcon] section
        /// already in the server's Game.ini when the player starts it with their own options; otherwise the launcher's.
        /// <paramref name="own"/> = the values are the player's (the launcher leaves Game.ini alone).
        /// </summary>
        public static (int Port, string Password, bool Own) RconFor(AppSettings s, string gameIni)
        {
            if (s.ServerUseOwnArgs)
            {
                string args = ServerArgs.Clean(s.ServerOwnArgs);
                string pw = ServerArgs.Value(args, "RconPassword");
                int port = int.TryParse(ServerArgs.Value(args, "RconListenPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p > 0 && p < 65536 ? p : 0;
                if (!string.IsNullOrEmpty(pw)) return (port > 0 ? port : s.ServerRconPort, pw, true);
                var sec = UeIni.Parse(gameIni ?? "").LastOrDefault(x => x.Name.Equals(RconSetup.Section, StringComparison.OrdinalIgnoreCase));
                string Val(string key) => sec?.Values.LastOrDefault(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
                if (sec != null && LaunchPlanner.IsTrue(Val("bEnabled") ?? "") && !string.IsNullOrEmpty(Val("Password")))
                    return (int.TryParse(Val("ListenPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int ip) && ip > 0 && ip < 65536 ? ip : 27015, Val("Password"), true);
            }
            return (s.ServerRconPort, s.ServerRconPassword, false);
        }

        /// <summary>What the server's mod.io login means for this start (the account is null when it was not looked at).</summary>
        private static void ModWarnings(ServerPlan plan, ServerModioAccount account, List<long> wanted, bool code, bool codeInArgs)
        {
            if (account == null) return;
            if (!code && !codeInArgs && !account.LoggedIn)
                plan.Warnings.Add(account.Expired ? T("The server's mod.io login has expired, so it loads no mods: send a new security code (Mods card).")
                                                  : T("The server is not logged in to mod.io, so it loads no mods: send a security code to its mod.io account (Mods card)."));
            if (account.SameAsGame)
                plan.Warnings.Add(T("The server is logged in with the same mod.io account as your game. mod.io gives it nothing that way: log the server in with an account of its own."));
            if (!account.LoggedIn) return;
            var missing = wanted.Where(id => !account.Subscriptions.Contains(id)).ToList();
            if (missing.Count > 0)
                plan.Warnings.Add(F("The server's mod.io account is not subscribed to {0} (press Subscribe on the Mods card): the server loads only its subscriptions.", string.Join(", ", missing)));
            else if (account.Subscriptions.Count == 0)
                plan.Warnings.Add(T("The server's mod.io account is not subscribed to any mods, so it loads none."));
        }

        public static ServerPlan Build(LaunchPlan match, AppSettings s, ServerInstall inst, RulesDb db, string currentGameIni, ServerModioAccount account = null)
        {
            var plan = new ServerPlan { Match = match };
            // Own options start the server on this PC; a server on another PC is only reached over RCON.
            if (s.ServerUseOwnArgs && !s.ServerRemote) return BuildOwn(plan, match, s, inst, currentGameIni, account);
            if (match == null || !match.IsValid) { plan.Error = match?.Error ?? T("Pick a map and scenario in Play first."); return plan; }
            if (inst == null || !inst.Found) { plan.Error = T("The dedicated server is not installed yet: install it on the Server page, or pick its folder there."); return plan; }
            if (inst.Unfinished) { plan.Error = T("The server's install did not finish: press Update the server to finish it."); return plan; }
            plan.Warnings.AddRange(match.Warnings);

            // The first map: the Play match, with the server's own player count and password instead of the offline ones.
            int maxPlayers = Math.Max(1, Math.Min(100, s.ServerMaxPlayers));
            string password = (s.ServerPassword ?? "").Trim();
            if (!SafePassword.IsMatch(password)) { plan.Error = T("The join password can only use letters, digits and - _ . ! @ # $ % ^ * + ~ (no spaces)."); return plan; }
            plan.Url = ServerUrl(match, maxPlayers, password);
            plan.StartMap = match.Map?.DisplayName ?? match.Level;

            // Mods the match needs: its map's and its mutators'.
            if (match.Scenario.Source == ContentSource.Mod && match.Scenario.ModId > 0) plan.MatchModIds.Add(match.Scenario.ModId);
            foreach (var m in match.MutatorInfos.Where(m => m.Source == ContentSource.Mod && m.ModId > 0))
                if (!plan.MatchModIds.Contains(m.ModId)) plan.MatchModIds.Add(m.ModId);

            if (s.ServerPort < 1 || s.ServerPort > 65535 || s.ServerQueryPort < 1 || s.ServerQueryPort > 65535) { plan.Error = T("The game port and the query port must be numbers from 1 to 65535."); return plan; }
            if (s.ServerPort == s.ServerQueryPort) { plan.Error = T("The game port and the query port must be different."); return plan; }
            plan.GamePort = s.ServerPort;
            plan.QueryPort = s.ServerQueryPort;
            plan.RconPort = s.ServerRconPort;
            plan.Args.Add("-Port=" + s.ServerPort.ToString(CultureInfo.InvariantCulture));
            plan.Args.Add("-QueryPort=" + s.ServerQueryPort.ToString(CultureInfo.InvariantCulture));
            if (s.ServerShowLog) plan.Args.Add("-log");
            string name = (s.ServerName ?? "").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
            if (name.Length > 63) { plan.Warnings.Add(T("The server name is cut to 63 characters (the server browser shows no more).")); name = name.Substring(0, 63).TrimEnd(); }
            if (name.Length > 0) plan.Args.Add("-hostname=\"" + name + "\"");

            // Map cycle
            if (s.ServerUseMapCycle)
            {
                string file = MapCyclePath(s, inst);
                if (file == null || !File.Exists(file)) { plan.Error = file == null ? T("The map cycle file was not found.") : F("The map cycle file was not found: {0}", file); return plan; }
                string cycle = Path.GetFileNameWithoutExtension(file);
                string inConfig = inst.ServerConfigDir;
                bool isThere = string.Equals(Path.GetFullPath(Path.GetDirectoryName(file)).TrimEnd('\\'), Path.GetFullPath(inConfig).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                               && string.Equals(Path.GetExtension(file), ".txt", StringComparison.OrdinalIgnoreCase);
                if (!SafeCycleName.IsMatch(cycle))
                {
                    // The server takes a bare name on its command line: a copy under a plain name is used instead.
                    cycle = Regex.Replace(cycle, @"[^A-Za-z0-9_\-\.]", "_");
                    isThere = false;
                }
                if (!isThere) plan.MapCycleCopyFrom = file;
                plan.MapCycleName = cycle;
                var entries = MapCycle.Parse(File.ReadAllText(file));
                if (!entries.Any(e => e.IsEntry)) plan.Warnings.Add(T("The map cycle has no scenarios, so the server cycles through the game's versus scenarios."));
                // Mutators in the start URL are per map: an entry with its own ?Mutators= has only those (mod.io #1865932).
                if (match.Mutators.Count > 0 && entries.Any(e => e.IsEntry && MapCycle.MutatorsOf(e).Count > 0))
                    plan.Warnings.Add(F("Map cycle entries with their own mutators use only those; the match's mutators ({0}) are not on those maps.", string.Join(", ", match.Mutators)));
                plan.Args.Add("-MapCycle=" + cycle);
            }

            // Admins
            var badIds = new List<string>();
            var admins = SteamIds(s.ServerAdmins, badIds);
            foreach (var b in badIds) plan.Warnings.Add(F("Not a Steam ID (the 17-digit number starting with 7656119), left out of the admins: {0}", b));
            if (admins.Count > 0)
            {
                plan.AdminsText = string.Concat(admins.Select(a => a + "\r\n"));
                plan.Args.Add("-AdminList=" + AdminsName);
            }

            // Mods
            var badMods = new List<string>();
            var mods = ModIds(s.ServerMods, badMods);
            foreach (var b in badMods) plan.Warnings.Add(F("Not a mod.io mod id (a number), left out of the mods: {0}", b));
            if (s.ServerModsEnabled)
            {
                foreach (var id in plan.MatchModIds) if (!mods.Contains(id)) mods.Add(id);
                plan.ModsOn = true;
                plan.Args.Add("-Mods");
                // Since game update 1.20 the server logs in to mod.io with a code e-mailed to its own account, once; later starts
                // use the saved login (the official guide's -SecurityCode=none).
                string code = ServerModio.CleanCode(s.ServerModioCode);
                plan.UsesCode = code != null;
                plan.Args.Add("-SecurityCode=" + (code ?? "none"));
                ModWarnings(plan, account, mods, code != null, false);
                // The mods are downloaded after the server has started: then it loads the match again, with its mod content.
                plan.Args.Add("-ModDownloadTravelTo=" + plan.Url);
                if (match.Scenario.Source == ContentSource.Mod)
                {
                    // A map from a mod does not exist until the download is done: start on an official map first.
                    plan.Url = "Farmhouse?Scenario=Scenario_Farmhouse_Checkpoint_Security?MaxPlayers=" + maxPlayers.ToString(CultureInfo.InvariantCulture)
                               + (password.Length > 0 ? "?Password=" + password : "");
                }
            }
            else if (match.Scenario.Source == ContentSource.Mod)
            {
                // Without its mod the map does not exist on the server.
                plan.Error = F("The map comes from a mod ({0}): turn on mods for the server.", string.Join(", ", plan.MatchModIds));
                return plan;
            }
            else if (plan.MatchModIds.Count > 0)
                plan.Warnings.Add(F("This match uses mods ({0}). Turn on mods for the server, or players and the server will not have them.", string.Join(", ", plan.MatchModIds)));

            string ruleset = (match.Profile.LaunchRuleset ?? "").Trim();
            if (s.ServerOfficialRules)
            {
                // One ruleset per server: official rules win over the match's own (the admin guide's opt-in).
                if (ruleset.Length > 0 && !IsOfficialRules(ruleset)) plan.Warnings.Add(F("Official rules replace the match's ruleset ({0}): a server runs one ruleset.", ruleset));
                if (match.Overrides.Keys.Any(k => !SetupEngine.IsSquadKey(k) && match.Profile.Rules.TryGetValue(match.Mode?.Cls ?? "", out var own) && own.ContainsKey(k)))
                    plan.Warnings.Add(T("Official rules keep the official values: some of your rule changes from Play may not apply on this server."));
                ruleset = "OfficialRules";
            }
            if (ruleset.Length > 0) plan.Args.Add("-ruleset=" + ruleset);
            string gslt = (s.ServerGslt ?? "").Trim();
            if (gslt.Length > 0)
            {
                if (!Regex.IsMatch(gslt, @"^[A-Za-z0-9]+$")) { plan.Error = T("The Steam server token can only have letters and digits."); return plan; }
                plan.Args.Add("-GSLTToken=" + gslt);
            }
            if (s.ServerGameStats)
            {
                if (gslt.Length == 0) plan.Warnings.Add(T("Game stats need a Steam server token (GSLT)."));
                if (password.Length > 0) plan.Warnings.Add(T("Game stats only work on servers without a join password."));
                plan.Args.Add("-GameStats");
            }
            if (s.ServerCheats) plan.Args.Add("-EnableCheats");
            string extra = (s.ServerExtraArgs ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (extra.Length > 0) plan.Args.Add(extra);

            // Game.ini: the match rules as for local play, and the server's own RCON section.
            RconSetup.EnsureServerSettings(s);
            string merged = LaunchPlanner.MergeGameIni(currentGameIni ?? "", match, db, s.ServerManagedIniKeys);
            plan.GameIni = ApplyVoteKick(RconSetup.Apply(merged, RconSetup.IniSection(s.ServerRconPort, s.ServerRconPassword, s.ServerRconFromNetwork)), s.ServerVoteKick);
            plan.ManagedIniKeys = LaunchPlanner.PlayerIniKeys(match, db);
            if (s.ServerRconPort == s.ServerPort || s.ServerRconPort == s.ServerQueryPort)
                plan.Warnings.Add(T("The RCON port is the same as a game port; pick another one."));
            return plan;
        }

        /// <summary>
        /// The player's own options, used as they are (from their .bat file or pasted). The launcher only adds a security code
        /// that is waiting, and its [Rcon] section to Game.ini when the options and the file have no RCON of their own.
        /// </summary>
        private static ServerPlan BuildOwn(ServerPlan plan, LaunchPlan match, AppSettings s, ServerInstall inst, string currentGameIni, ServerModioAccount account)
        {
            if (inst == null || !inst.Found) { plan.Error = T("The dedicated server is not installed yet: install it on the Server page, or pick its folder there."); return plan; }
            if (inst.Unfinished) { plan.Error = T("The server's install did not finish: press Update the server to finish it."); return plan; }
            string own = ServerArgs.Clean(s.ServerOwnArgs);
            if (own.Length == 0) { plan.Error = T("Paste your server's options, or import them from your .bat file (Server settings)."); return plan; }
            plan.ModsOn = ServerArgs.Has(own, "Mods");
            string code = ServerModio.CleanCode(s.ServerModioCode);
            string theirs = ServerModio.SecurityCodeIn(own);
            if (code != null && plan.ModsOn) { own = ServerModio.WithSecurityCode(own, code); plan.UsesCode = true; }
            else if (code != null) plan.Warnings.Add(T("A security code is waiting, but your options have no -Mods: add it to log the server in to mod.io."));
            if (!plan.UsesCode && theirs != null && !theirs.Equals("none", StringComparison.OrdinalIgnoreCase))
                plan.Warnings.Add(T("Your options pass a security code. A code works only once: after the first start, change it to -SecurityCode=none."));
            plan.OwnArgs = own;
            if (plan.ModsOn)
                ModWarnings(plan, account, ModIds(s.ServerMods, null), plan.UsesCode,
                            theirs != null && !theirs.Equals("none", StringComparison.OrdinalIgnoreCase));
            plan.StartMap = ServerArgs.FirstMap(own);
            plan.GamePort = int.TryParse(ServerArgs.Value(own, "Port"), NumberStyles.None, CultureInfo.InvariantCulture, out int gp) ? gp : 27102;
            plan.QueryPort = int.TryParse(ServerArgs.Value(own, "QueryPort"), NumberStyles.None, CultureInfo.InvariantCulture, out int qp) ? qp : 27131;

            // RCON for the Players card: theirs when they have it, else the launcher's section in Game.ini.
            RconSetup.EnsureServerSettings(s);
            var rcon = RconFor(s, currentGameIni);
            plan.RconPort = rcon.Port;
            plan.GameIni = rcon.Own ? currentGameIni ?? "" : RconSetup.Apply(currentGameIni ?? "", RconSetup.IniSection(s.ServerRconPort, s.ServerRconPassword, s.ServerRconFromNetwork));
            plan.ManagedIniKeys = s.ServerManagedIniKeys ?? new List<string>();

            // "Load the Play match now" on the running server still loads the match from Play.
            if (match != null && match.IsValid)
            {
                string password = (s.ServerPassword ?? "").Trim();
                plan.Url = ServerUrl(match, Math.Max(1, Math.Min(100, s.ServerMaxPlayers)), SafePassword.IsMatch(password) ? password : "");
            }
            else plan.Match = null;
            return plan;
        }
    }
}
