using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Game
{
    /// <summary>
    /// Sets up the game's own RCON server so the launcher can talk to the game directly: on 127.0.0.1 only (never
    /// reachable from the network), with a random password. The game reads the [Rcon] section of Game.ini at start
    /// (also when started from Steam), and the launcher passes the same values on the command line when it starts it.
    /// </summary>
    public static class RconSetup
    {
        public const string Section = "Rcon";
        public const string Address = "127.0.0.1";

        /// <summary>Picks the port and password once (kept in the launcher settings).</summary>
        public static bool EnsureSettings(AppSettings s)
        {
            bool changed = false;
            if (s.RconPort < 1024 || s.RconPort > 65535) { s.RconPort = FreePort(); changed = true; }
            if (string.IsNullOrEmpty(s.RconPassword) || s.RconPassword.Length < 16 || !s.RconPassword.All(char.IsLetterOrDigit))
            {
                s.RconPassword = RandomPassword(24);
                changed = true;
            }
            return changed;
        }

        private static int FreePort()
        {
            var rnd = new Random();
            for (int i = 0; i < 40; i++)
            {
                int port = 27700 + rnd.Next(300);
                if (PortFree(port)) return port;
            }
            return 27777;
        }

        public static bool PortFree(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch { return false; }
        }

        private static string RandomPassword(int length)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        /// <summary>The [Rcon] section the launcher keeps in Game.ini.</summary>
        public static UeIni.Section IniSection(AppSettings s) => new UeIni.Section(Section)
        {
            Values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("bEnabled", "True"),
                new KeyValuePair<string, string>("Password", s.RconPassword ?? ""),
                new KeyValuePair<string, string>("ListenPort", s.RconPort.ToString()),
                new KeyValuePair<string, string>("bUseBroadcastAddress", "False"),
                new KeyValuePair<string, string>("ListenAddressOverride", Address),
                new KeyValuePair<string, string>("bAllowConsoleCommands", "True"),
                new KeyValuePair<string, string>("bAllowOnListenServer", "True"),
                new KeyValuePair<string, string>("MaxPasswordAttempts", "10"),
                new KeyValuePair<string, string>("IncorrectPasswordBanTime", "0"),
            }
        };

        /// <summary>Game.ini text with the launcher's [Rcon] values (other lines of the file, and of the section, stay).</summary>
        public static string Apply(string gameIni, AppSettings s) =>
            UeIni.MergeSections(gameIni ?? "", new[] { IniSection(s) }, (sec, key) => false);

        /// <summary>Command line switches for a game the launcher starts (the same values as in Game.ini).</summary>
        public static string CommandLine(AppSettings s) =>
            "-Rcon -RconPassword=" + s.RconPassword + " -RconListenPort=" + s.RconPort + " -RconListenAddress=" + Address;

        /// <summary>Writes the section into Game.ini now (only while the game is closed: a running game overwrites the file).</summary>
        public static bool EnsureGameIni(AppSettings s)
        {
            try
            {
                string path = GameInstall.GameIniPath;
                if (string.IsNullOrEmpty(path) || !Directory.Exists(Path.GetDirectoryName(path))) return false;
                string current = File.Exists(path) ? UeIni.ReadText(path) : "";
                string updated = Apply(current, s);
                if (Normalize(updated) == Normalize(current)) return false;
                ConsoleBridge.BackupFile(path);
                UeIni.WriteText(path, updated);
                AppLog.Info("Game.ini: RCON set up on " + Address + ":" + s.RconPort);
                return true;
            }
            catch (Exception ex) { AppLog.Warn("Could not set up RCON in Game.ini: " + ex.Message); return false; }
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        /// <summary>True when Game.ini holds the launcher's current RCON values.</summary>
        public static bool GameIniHasIt(AppSettings s)
        {
            try
            {
                string text = UeIni.ReadText(GameInstall.GameIniPath);
                var sec = UeIni.Parse(text).LastOrDefault(x => x.Name.Equals(Section, StringComparison.OrdinalIgnoreCase));
                return sec != null && IniSection(s).Values.All(v => sec.Values.Any(x => x.Key.Equals(v.Key, StringComparison.OrdinalIgnoreCase) && x.Value == v.Value));
            }
            catch { return false; }
        }
    }

    public sealed class RconReply
    {
        public bool Ok;
        public string Text = "";
        public string Error;
    }

    /// <summary>
    /// Talks to the running game over its RCON server: loading maps (travel), game mode properties, round restarts,
    /// reading values and closing the game. No keys are pressed and the game does not need to be in front.
    /// One connection per call keeps it simple and survives map changes and game restarts.
    /// </summary>
    public sealed class GameRcon
    {
        private readonly Func<AppSettings> settings;
        private readonly object gate = new object();
        public int ConnectTimeoutMs = 1500;
        public int ReplyTimeoutMs = 8000;
        public string Host = RconSetup.Address;
        public int? PortOverride;
        public string PasswordOverride;

        public GameRcon(Func<AppSettings> settings) { this.settings = settings; }

        private int Port => PortOverride ?? settings().RconPort;
        private string Password => PasswordOverride ?? settings().RconPassword;

        /// <summary>Runs commands on one connection, in order. Throws RconException when the game cannot be reached.</summary>
        public List<string> Run(params string[] commands)
        {
            lock (gate)
            {
                using (var c = new RconClient(Host, Port, Password))
                {
                    c.Connect(ConnectTimeoutMs, ReplyTimeoutMs);
                    var replies = new List<string>();
                    foreach (var cmd in commands) replies.Add(c.Send(cmd, ReplyTimeoutMs));
                    return replies;
                }
            }
        }

        /// <summary>Connects and logs in; null when fine, else the reason.</summary>
        public string Probe() => Probe(out _);

        /// <summary>
        /// Connects and logs in; null when fine, else the reason. <paramref name="busy"/> is true when the game took the
        /// connection but did not answer in time: it is running RCON but busy (a map loading freezes it for seconds).
        /// </summary>
        public string Probe(out bool busy)
        {
            busy = false;
            try { Run(); return null; }
            catch (RconException ex)
            {
                busy = ex.Kind == RconError.Timeout;
                return ex.Kind == RconError.AuthFailed ? "the game did not accept the launcher's RCON password"
                     : busy ? "the game is busy and did not answer yet" : ex.Message;
            }
        }

        public bool Available => Probe() == null;

        /// <summary>A console command line for the game. The whole line is quoted: the game's RCON server passes only the first word to the console otherwise.</summary>
        public static string Quote(string commandLine) => "\"" + (commandLine ?? "").Replace("\"", "'").Trim() + "\"";

        /// <summary>Loads a map: the part after "open " (map?Scenario=...?options).</summary>
        public RconReply Travel(string url)
        {
            var r = new RconReply();
            try
            {
                r.Text = Run("travel " + url)[0].Trim();
                r.Ok = r.Text.StartsWith("Travelling to", StringComparison.OrdinalIgnoreCase);
                if (!r.Ok) r.Error = r.Text.Length > 0 ? r.Text : "the game gave no answer to travel";
            }
            catch (RconException ex) { r.Error = ex.Message; }
            return r;
        }

        private static readonly Regex SetReply = new Regex(@"^(?<k>\w+) = ""(?<v>[^""]*)""(\s*\(was ""(?<was>[^""]*)""\))?\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Sets game mode properties; returns the ones the game did not take, with its answer. Throws RconException when
        /// the game did not answer (a busy game may still carry the commands out later).
        /// </summary>
        public Dictionary<string, string> SetProperties(IEnumerable<KeyValuePair<string, string>> values)
        {
            var list = values.ToList();
            var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (list.Count == 0) return failed;
            var replies = Run(list.Select(kv => "gamemodeproperty " + kv.Key + " " + kv.Value).ToArray());
            for (int i = 0; i < list.Count; i++)
            {
                string reply = replies[i].Trim();
                var m = SetReply.Match(reply);
                if (!m.Success || !m.Groups["k"].Value.Equals(list[i].Key, StringComparison.OrdinalIgnoreCase)) failed[list[i].Key] = reply.Length > 0 ? reply : "no answer";
                else if (!SameValue(m.Groups["v"].Value, list[i].Value)) failed[list[i].Key] = "the game kept " + m.Groups["v"].Value;
            }
            return failed;
        }

        private static bool SameValue(string game, string wanted)
        {
            if (string.Equals(game, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (double.TryParse(game, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(wanted, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)) return Math.Abs(a - b) < 1e-4;
            bool gt = game.Equals("true", StringComparison.OrdinalIgnoreCase) || game == "1", wt = wanted.Equals("true", StringComparison.OrdinalIgnoreCase) || wanted == "1";
            bool gf = game.Equals("false", StringComparison.OrdinalIgnoreCase) || game == "0", wf = wanted.Equals("false", StringComparison.OrdinalIgnoreCase) || wanted == "0";
            return (gt && wt) || (gf && wf);
        }

        private static readonly Regex ListLine = new Regex(@"^(?<k>\w+) = (?<v>.*?) \((?<t>[^,()]+), [^()]*\)\s*$", RegexOptions.Compiled);

        /// <summary>Current game mode property values (all, or those containing the filter), and the game mode's class.</summary>
        public Dictionary<string, string> ReadProperties(string filter, out string modeClass)
        {
            modeClass = null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string reply = Run("listgamemodeproperties" + (string.IsNullOrEmpty(filter) ? "" : " " + filter))[0];
            foreach (var raw in reply.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Listing properties for gamemode ", StringComparison.Ordinal)) { modeClass = line.Substring(32).Trim(); continue; }
                var m = ListLine.Match(line);
                if (m.Success) values[m.Groups["k"].Value] = TrimNumber(m.Groups["v"].Value.Trim());
            }
            return values;
        }

        private static string TrimNumber(string v) =>
            Regex.IsMatch(v, @"^-?\d+\.\d+$") && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) : v;

        public RconReply RestartRound(bool swapTeams)
        {
            var r = new RconReply();
            try { r.Text = Run("restartround " + (swapTeams ? "1" : "0"))[0].Trim(); r.Ok = !r.Text.StartsWith("No ", StringComparison.Ordinal) && !r.Text.StartsWith("Invalid", StringComparison.Ordinal); if (!r.Ok) r.Error = r.Text; }
            catch (RconException ex) { r.Error = ex.Message; }
            return r;
        }

        /// <summary>Humans and bots per team (team number, from the game's player states).</summary>
        public SortedDictionary<int, (int humans, int bots)> CountPlayers()
        {
            var replies = Run(Quote("getall INSPlayerState TeamId"), Quote("getall INSPlayerState bIsABot"));
            var team = new Dictionary<string, int>();
            var bot = new Dictionary<string, bool>();
            foreach (Match m in Regex.Matches(replies[0], @"(INSPlayerState_\w+)\.TeamId = (\d+)")) team[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            foreach (Match m in Regex.Matches(replies[1], @"(INSPlayerState_\w+)\.bIsABot = (True|False)")) bot[m.Groups[1].Value] = m.Groups[2].Value == "True";
            var result = new SortedDictionary<int, (int humans, int bots)>();
            foreach (var kv in team)
            {
                result.TryGetValue(kv.Value, out var c);
                bool isBot = bot.TryGetValue(kv.Key, out var b) && b;
                result[kv.Value] = isBot ? (c.humans, c.bots + 1) : (c.humans + 1, c.bots);
            }
            return result;
        }

        /// <summary>Asks the game to close itself. True when it was asked (the connection closing is part of that).</summary>
        public bool Exit()
        {
            try { Run(Quote("exit")); return true; }
            catch (RconException ex) { return ex.Kind == RconError.Closed || ex.Kind == RconError.Timeout; }
        }
    }
}
