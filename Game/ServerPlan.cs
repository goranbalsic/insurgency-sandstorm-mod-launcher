using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        /// <summary>A line kept exactly as it was: a comment, or an entry with settings the launcher does not know.</summary>
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
    /// MapCycle.txt of a dedicated server: one scenario per line, or (Scenario="...",Lighting="Night",Mode="...").
    /// Comments and lines the launcher does not understand are kept as they are.
    /// </summary>
    public static class MapCycle
    {
        private static readonly Regex Struct = new Regex(@"^\((?<body>.*)\)$", RegexOptions.Compiled);
        private static readonly Regex Field = new Regex(@"^\s*(?<k>\w+)\s*=\s*[""“”]?(?<v>[^""“”]*?)[""“”]?\s*$", RegexOptions.Compiled);
        private static readonly Regex Plain = new Regex(@"^[A-Za-z0-9_\-]+$", RegexOptions.Compiled);

        public static List<MapCycleEntry> Parse(string text)
        {
            var list = new List<MapCycleEntry>();
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string t = raw.Trim().TrimStart('﻿');
                if (t.Length == 0) continue;
                if (Plain.IsMatch(t)) { list.Add(new MapCycleEntry { Scenario = t }); continue; }
                var m = Struct.Match(t);
                if (m.Success)
                {
                    var e = new MapCycleEntry();
                    bool known = true;
                    foreach (var part in m.Groups["body"].Value.Split(','))
                    {
                        var f = Field.Match(part);
                        if (!f.Success) { known = false; continue; }
                        string k = f.Groups["k"].Value, v = f.Groups["v"].Value.Trim();
                        if (k.Equals("Scenario", StringComparison.OrdinalIgnoreCase)) e.Scenario = v;
                        else if (k.Equals("Lighting", StringComparison.OrdinalIgnoreCase)) e.Lighting = v;
                        else if (k.Equals("Mode", StringComparison.OrdinalIgnoreCase)) e.Mode = v;
                        else known = false;
                    }
                    // Settings the launcher does not know stay exactly as they were written.
                    if (!known || !e.IsEntry) e.Raw = t;
                    list.Add(e);
                    continue;
                }
                list.Add(new MapCycleEntry { Raw = t });
            }
            return list;
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
        /// <summary>Mods.txt (null = left alone, mods off).</summary>
        public string ModsText;
        /// <summary>Mods the match needs (its map or mutators come from them).</summary>
        public List<long> MatchModIds = new List<long>();
        public string MapCycleName;
        /// <summary>A map cycle file outside the server's Config\Server folder, copied there before the start.</summary>
        public string MapCycleCopyFrom;
        public List<string> Warnings = new List<string>();
        public string Error;

        public bool IsValid => Error == null;
        public string CommandLine => Url == null ? "" : Url + (Args.Count > 0 ? " " + string.Join(" ", Args) : "");

        /// <summary>The command line to show and log: the Steam server token and the join password hidden.</summary>
        public string ShownCommandLine => Regex.Replace(Regex.Replace(CommandLine, @"(-GSLTToken=)\S+", "$1<your token>", RegexOptions.IgnoreCase),
                                                        @"(\?Password=)[^?\s]+", "$1<password>", RegexOptions.IgnoreCase);
    }

    public static class ServerPlanner
    {
        public const string AdminsName = "Admins";
        private static readonly Regex SafePassword = new Regex(@"^[A-Za-z0-9_\-\.!@#\$%\^\*\+~]*$", RegexOptions.Compiled);
        private static readonly Regex SafeCycleName = new Regex(@"^[A-Za-z0-9_\-\.]+$", RegexOptions.Compiled);

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

        public static ServerPlan Build(LaunchPlan match, AppSettings s, ServerInstall inst, RulesDb db, string currentGameIni)
        {
            var plan = new ServerPlan { Match = match };
            if (match == null || !match.IsValid) { plan.Error = match?.Error ?? T("Pick a map and scenario in Play first."); return plan; }
            if (inst == null || !inst.Found) { plan.Error = T("The dedicated server was not found. Set its folder on this page."); return plan; }
            plan.Warnings.AddRange(match.Warnings);

            // The first map: the Play match, with the server's own player count and password instead of the offline ones.
            int maxPlayers = Math.Max(1, Math.Min(100, s.ServerMaxPlayers));
            string password = (s.ServerPassword ?? "").Trim();
            if (!SafePassword.IsMatch(password)) { plan.Error = T("The join password can only use letters, digits and - _ . ! @ # $ % ^ * + ~ (no spaces)."); return plan; }
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
            plan.Url = url.ToString();

            // Mods the match needs: its map's and its mutators'.
            if (match.Scenario.Source == ContentSource.Mod && match.Scenario.ModId > 0) plan.MatchModIds.Add(match.Scenario.ModId);
            foreach (var m in match.MutatorInfos.Where(m => m.Source == ContentSource.Mod && m.ModId > 0))
                if (!plan.MatchModIds.Contains(m.ModId)) plan.MatchModIds.Add(m.ModId);

            if (s.ServerPort < 1 || s.ServerPort > 65535 || s.ServerQueryPort < 1 || s.ServerQueryPort > 65535) { plan.Error = T("The game port and the query port must be numbers from 1 to 65535."); return plan; }
            if (s.ServerPort == s.ServerQueryPort) { plan.Error = T("The game port and the query port must be different."); return plan; }
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
                if (!MapCycle.Parse(File.ReadAllText(file)).Any(e => e.IsEntry)) plan.Warnings.Add(T("The map cycle has no scenarios, so the server cycles through the game's versus scenarios."));
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
                if (mods.Count == 0) plan.Warnings.Add(T("Mods are on but no mod ids are listed."));
                plan.ModsText = string.Concat(mods.Select(m => m.ToString(CultureInfo.InvariantCulture) + "\r\n"));
                plan.Args.Add("-Mods");
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

            string ruleset = match.Profile.LaunchRuleset;
            if (!string.IsNullOrWhiteSpace(ruleset)) plan.Args.Add("-ruleset=" + ruleset.Trim());
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
            plan.GameIni = RconSetup.Apply(merged, RconSetup.IniSection(s.ServerRconPort, s.ServerRconPassword, s.ServerRconFromNetwork));
            plan.ManagedIniKeys = LaunchPlanner.PlayerIniKeys(match, db);
            if (s.ServerRconPort == s.ServerPort || s.ServerRconPort == s.ServerQueryPort)
                plan.Warnings.Add(T("The RCON port is the same as a game port; pick another one."));
            return plan;
        }
    }
}
