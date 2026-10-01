using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SandstormModLauncher.Core;
using SandstormModLauncher.Game;
using SandstormModLauncher.Models;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// A stand-in for the game's RCON server (same protocol and answers as Insurgency: Sandstorm's), for testing
    /// the launcher's RCON code without the game. With chaos on it splits replies into small pieces, sends replies
    /// over several packets, answers late and drops connections, like a busy or closing game can.
    /// </summary>
    public sealed class FakeRconServer : IDisposable
    {
        public readonly string Password;
        public int Port { get; private set; }
        public bool Chaos;
        public double DropChance = 0.03, SlowChance = 0.03;
        public int SlowMs = 3000;
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<(int team, bool bot)> Players = new List<(int, bool)>();
        public string LastTravel;
        public int RoundRestarts, Exits;
        /// <summary>Commands run as the player ("defer ..."), in the order the game got them.</summary>
        public readonly List<string> Deferred = new List<string>();
        /// <summary>How long a map load freezes the game after a deferred open (the next answer comes after it).</summary>
        public int LoadMs;
        private readonly Random rnd;
        private readonly object sync = new object();
        private TcpListener listener;
        private volatile bool running;

        public FakeRconServer(string password, int seed)
        {
            Password = password;
            rnd = new Random(seed);
            for (int i = 0; i < 180; i++) Props["Property" + i] = (i * 7 % 50).ToString();
            Props["RoundTime"] = "300";
            Props["AIDifficulty"] = "0.500000";
            Props["bBots"] = "False";
            Props["BotQuota"] = "10";
        }

        private int Next(int max) { lock (rnd) return rnd.Next(max); }
        private double NextDouble() { lock (rnd) return rnd.NextDouble(); }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true;
            new Thread(() =>
            {
                while (running)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { break; }
                    new Thread(() => Serve(c)) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
        }

        private void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    var s = c.GetStream();
                    bool authed = false;
                    while (running)
                    {
                        var head = ReadExact(s, 4);
                        if (head == null) return;
                        int size = BitConverter.ToInt32(head, 0);
                        var rest = ReadExact(s, size);
                        if (rest == null) return;
                        int id = BitConverter.ToInt32(rest, 0), type = BitConverter.ToInt32(rest, 4);
                        string body = Encoding.UTF8.GetString(rest, 8, Math.Max(0, size - 10));
                        if (type == 3)
                        {
                            authed = body == Password;
                            if (Chaos && Next(2) == 0) Send(s, id, 0, "");   // some servers send an empty value first
                            Send(s, authed ? id : -1, 2, "");
                            if (!authed) return;
                            continue;
                        }
                        if (!authed) return;
                        if (Chaos && NextDouble() < DropChance) return;              // the game closes (map change, exit)
                        if (Chaos && NextDouble() < SlowChance) Thread.Sleep(SlowMs); // the game thread is busy (loading)
                        if (body == "\"exit\"") { lock (sync) Exits++; return; }
                        string reply = Answer(body);
                        // Long replies go in several packets (all with the command's id), like a Source server may send them.
                        int chunk = Chaos ? 1 + Next(700) : 4096;
                        for (int at = 0; at == 0 || at < reply.Length; at += chunk)
                            Send(s, id, 0, reply.Substring(at, Math.Min(chunk, reply.Length - at)));
                        // A deferred open loads the map on the next frame: the game answers nothing more until it is done.
                        if (body.StartsWith("\"defer open ", StringComparison.Ordinal) && LoadMs > 0) Thread.Sleep(LoadMs);
                    }
                }
            }
            catch { }
        }

        private string Answer(string cmd)
        {
            lock (sync)
            {
                if (cmd.StartsWith("help ", StringComparison.Ordinal)) return "Listing commands containing \"" + cmd.Substring(5) + "\"\n";
                // The quoted console line "defer X": the engine queues X for the local player and answers nothing.
                if (cmd.StartsWith("\"defer ", StringComparison.Ordinal) && cmd.EndsWith("\"", StringComparison.Ordinal) && cmd.Length > 8)
                {
                    lock (Deferred) Deferred.Add(cmd.Substring(7, cmd.Length - 8));
                    return "";
                }
                if (cmd.StartsWith("travel ", StringComparison.Ordinal)) { LastTravel = cmd.Substring(7); return "Travelling to \"" + LastTravel + "\"..."; }
                if (cmd.StartsWith("restartround", StringComparison.Ordinal)) { RoundRestarts++; return ""; }
                if (cmd.StartsWith("gamemodeproperty ", StringComparison.Ordinal))
                {
                    var w = cmd.Substring(17).Split(new[] { ' ' }, 2);
                    if (!Props.TryGetValue(w[0], out var old)) return "Could not find property \"" + w[0] + "\"";
                    if (w.Length == 1) return w[0] + " = \"" + old + "\"";
                    Props[w[0]] = w[1];
                    return w[0] + " = \"" + w[1] + "\" (was \"" + old + "\")";
                }
                if (cmd.StartsWith("listgamemodeproperties", StringComparison.Ordinal))
                {
                    string filter = cmd.Length > 23 ? cmd.Substring(23) : "";
                    var sb = new StringBuilder("Listing properties for gamemode INSTestGameMode\n");
                    foreach (var kv in Props.Where(p => filter.Length == 0 || p.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                        sb.Append(kv.Key).Append(" = ").Append(kv.Value).Append(" (int32, Config)\n");
                    return sb.ToString();
                }
                if (cmd == "\"getall INSPlayerState TeamId\"" || cmd == "\"getall INSPlayerState bIsABot\"")
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < Players.Count; i++)
                        sb.Append(i).Append(") INSPlayerState /Game/Maps/Test/Test.Test:PersistentLevel.INSPlayerState_").Append(2147470000 + i)
                          .Append(cmd.Contains("TeamId") ? ".TeamId = " + Players[i].team : ".bIsABot = " + (Players[i].bot ? "True" : "False")).Append('\n');
                    return sb.ToString();
                }
                return "";
            }
        }

        private static byte[] ReadExact(NetworkStream s, int n)
        {
            if (n < 0 || n > 1 << 20) return null;
            var buf = new byte[n];
            int got = 0;
            while (got < n) { int r = s.Read(buf, got, n - got); if (r <= 0) return null; got += r; }
            return buf;
        }

        private void Send(NetworkStream s, int id, int type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            var p = new byte[14 + b.Length];
            BitConverter.GetBytes(10 + b.Length).CopyTo(p, 0);
            BitConverter.GetBytes(id).CopyTo(p, 4);
            BitConverter.GetBytes(type).CopyTo(p, 8);
            b.CopyTo(p, 12);
            if (!Chaos) { s.Write(p, 0, p.Length); return; }
            // Bytes arrive in pieces, with small gaps.
            for (int at = 0; at < p.Length;)
            {
                int n = Math.Min(p.Length - at, 1 + Next(40));
                s.Write(p, at, n);
                at += n;
                if (Next(8) == 0) Thread.Sleep(Next(5));
            }
        }
    }

    /// <summary>--cli rcon-torture [steps] [seed]: the launcher's RCON code against a misbehaving fake game.</summary>
    public static class RconTorture
    {
        private static bool SameNumberOrText(string a, string b) =>
            a == b || (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                       double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-9);

        /// <summary>
        /// Fixed checks of what the launch reads from the game log (lines from real sessions): the game's own state,
        /// which tells a match the game really plays from one it loaded while staying in its menu state, and the line
        /// that confirms the AI difficulty.
        /// </summary>
        private static void LogChecks(Action<string> fail)
        {
            using (var m = GameMonitor.Detached())
            {
                void Feed(params string[] lines) { foreach (var l in lines) m.Feed("[2026.09.26-20.41.51:410][  1]" + l); }
                Feed("LogINSGameInstance: State transition: 'None' -> 'WelcomeScreen'", "LogINSGameInstance: State transition: 'WelcomeScreen' -> 'MainMenu'");
                if (m.Phase != GamePhase.Menu || m.InstanceState != "MainMenu") fail("log: at the main menu: " + m.Phase + " / " + m.InstanceState);
                // A map opened over RCON without "defer" (1.6.0): loaded, but the game is still in its menu state.
                Feed("LogLoad: LoadMap: /Game/Maps/Citadel/Citadel?Scenario=Scenario_Citadel_Checkpoint_Security?MaxPlayers=8?Lighting=Day",
                     "LogLoad: Took 3.804450 seconds to LoadMap(/Game/Maps/Citadel/Citadel)");
                if (m.Phase != GamePhase.InMatch || m.InstanceState != "MainMenu") fail("log: map loaded without the switch into play: " + m.Phase + " / " + m.InstanceState);
                Feed("LogINSGameInstance: State transition: 'MainMenu' -> 'Playing'");
                if (m.Phase != GamePhase.InMatch || m.InstanceState != "Playing") fail("log: after the switch into play: " + m.Phase + " / " + m.InstanceState);
                // "disconnect" goes back to the menu map without a state line.
                Feed("LogLoad: LoadMap: /Game/Maps/Utility/Entry?closed", "LogLoad: Took 0.153096 seconds to LoadMap(/Game/Maps/Utility/Entry)");
                if (m.Phase != GamePhase.Menu || m.CurrentLevel != null) fail("log: back on the menu map: " + m.Phase + " / " + m.CurrentLevel);
                m.Feed("Log file open, 09/26/26 22:41:50");
                if (m.InstanceState != null) fail("log: a new log kept the old state " + m.InstanceState);
                // The game on its way to the menu travels to a menu map the way a server changes maps: no match yet.
                Feed("LogLoad: LoadMap: /Game/Maps/Utility/Entry?Name=Player", "LogLoad: Took 0.151852 seconds to LoadMap(/Game/Maps/Utility/Entry)",
                     "LogWorld: SeamlessTravel to: /Game/Maps/Utility/MainMenu_Hold", "LogWorld: ----SeamlessTravel finished in 2.09 seconds ------");
                if (m.Phase == GamePhase.InMatch || m.CurrentLevel != null) fail("log: the menu map was taken for a match: " + m.Phase + " / " + m.CurrentLevel);
                // A server changing maps: the match is there once the travel has finished.
                Feed("LogGameMode: ProcessServerTravel: Farmhouse?Scenario=Scenario_Farmhouse_Push_Security", "LogWorld: SeamlessTravel to: /Game/Maps/Farmhouse/Farmhouse");
                if (m.Phase != GamePhase.Loading) fail("log: a server travel is not loading: " + m.Phase);
                Feed("LogWorld: ----SeamlessTravel finished in 4.10 seconds ------");
                if (m.Phase != GamePhase.InMatch || m.CurrentLevel != "/Game/Maps/Farmhouse/Farmhouse") fail("log: after a server travel: " + m.Phase + " / " + m.CurrentLevel);
            }
            // The privacy filter keeps the game lines the launch follows, even with mod names like "Authentic..." or
            // "...Tickets", and still drops account lines and URL secrets.
            foreach (var (line, secret) in new[]
            {
                ("[2026.09.26-21.53.12:865][415]LogNet: Browse: /Game/Maps/Farmhouse/Farmhouse?Scenario=Scenario_Farmhouse_Push_Security?Mutators=AuthenticRecoil,NoTickets,SessionFix", false),
                ("[2026.09.26-21.53.12:865][415]LogLoad: LoadMap: /Game/Maps/Farmhouse/Farmhouse?Scenario=S?Mutators=TokenMod", false),
                ("[2026.09.26-21.50.45:738][382]LogGameState: Round reset. Authority:3", false),
                ("[2026.09.26-21.50.45:735][382]LogGameMode: Display: State: RoundActive -> PreRound", false),
                ("[2026.09.26-21.08.33:085][703]LogINSGameInstance: State transition: 'MainMenu' -> 'Playing'", false),
                ("[2026.09.26-21.00.22:427][969]LogAI: Display: AI difficulty set to 0.9", false),
                ("[2026.09.26-21.53.12:865][415]LogNet: Browse: 203.0.113.5:27102?Password=secret123", true),
                ("[2026.09.26-21.53.12:865][415]LogNet: Browse: /Game/Maps/X/X?AuthTicket=abc", true),
                ("[2026.09.26-20.37.54:000][  0]LogInit: Command Line: -Rcon -RconPassword=abc -RconListenPort=27700", true),
                ("[2026.09.26-20.37.54:000][  0]LogPros: Verbose: token eyJhbGciOi", true),
                ("[2026.09.26-20.37.54:000][  0]LogOnline: Display: STEAM: logged in", true),
                ("[2026.09.26-20.37.54:000][  0]LogINSGameInstance: OnAuthTokenReceived invalid auth token. Service: x error: y", true),
                ("[2026.09.26-20.37.54:000][  0]LogRcon: 127.0.0.1:50000 << \"defer open X?Mutators=AuthenticRecoil\"", true),
            })
                if (LogSanitizer.IsSensitive(line) != secret) fail("privacy filter: " + (secret ? "kept " : "dropped ") + line);
            using (var m = GameMonitor.Detached())
            {
                var got = new List<string>();
                m.LineReceived += got.Add;
                m.Feed("[2026.09.26-21.53.12:865][415]LogNet: Browse: /Game/Maps/Farmhouse/Farmhouse?Scenario=Scenario_Farmhouse_Push_Security?Mutators=AuthenticRecoil");
                m.Feed("[2026.09.26-20.37.54:000][  0]LogPros: Verbose: token eyJhbGciOi");
                if (got.Count != 1 || !got[0].Contains("LogNet: Browse:")) fail("log: lines passed on: " + got.Count + " (the Browse line with an 'Authentic' mod must pass, the account line must not)");
            }
            foreach (var (line, wanted, ok) in new[]
            {
                ("[2026.09.26-21.00.22:427][969]LogAI: Display: AI difficulty set to 0.9", "0.9", true),
                ("LogAI: Display: AI difficulty set to 0.9", "0.90", true),
                ("LogAI: Display: AI difficulty set to 1", "1.0", true),
                ("LogAI: Display: AI difficulty set to 0.5", "0.8", false),
                ("LogRcon: 127.0.0.1:50000 << \"defer AIDifficulty 0.8\"", "0.8", false),
                (null, "0.8", false),
            })
                if (LaunchService.DifficultyIs(line, wanted) != ok) fail("difficulty line '" + line + "' against " + wanted + " gave " + !ok);
        }

        /// <summary>The dedicated server's text formats: MapCycle.txt (written and read back) and listplayers.</summary>
        private static void ServerFormatChecks(Random rnd, Action<string> fail)
        {
            var parsed = MapCycle.Parse("Scenario_Crossing_Skirmish\r\n\r\n(Scenario=“Scenario_Town_Checkpoint_Security”, Mode=“CheckpointHardcore”)\n"
                                        + "// my comment\n(Scenario=\"Scenario_Refinery_Push_Security\",Lighting=\"Night\",Options=\"x\")\nnot a scenario line\n");
            if (parsed.Count != 5) fail("map cycle: " + parsed.Count + " lines instead of 5");
            else
            {
                if (parsed[0].Scenario != "Scenario_Crossing_Skirmish" || parsed[0].Raw != null) fail("map cycle: a plain scenario line was not read");
                if (parsed[1].Scenario != "Scenario_Town_Checkpoint_Security" || parsed[1].Mode != "CheckpointHardcore" || parsed[1].Raw != null) fail("map cycle: curly quotes and spaces were not read");
                if (parsed[2].Raw != "// my comment" || parsed[2].IsEntry) fail("map cycle: a comment was not kept");
                if (parsed[3].Raw == null || parsed[3].Scenario != "Scenario_Refinery_Push_Security") fail("map cycle: an entry with unknown settings was not kept as written");
                if (parsed[4].Raw != "not a scenario line") fail("map cycle: an unknown line was not kept");
            }
            // An entry written over several lines (mod.io #1865932, exactly as posted) is one entry, kept as written, its
            // mutators read; commas and brackets inside quotes split nothing; a bracket never closed stays a line of its own.
            string dyn = "(Scenario=\"Scenario_Crossing_ZY_Checkpoint_Security\",\r\nLighting=\"Night\",Options=\"?Mutators=Fullkit,RandomSeason,RandomTime?\r\n Mapname=Crossing ZY?Label=CP Dyn Fullkit\")";
            string cycleText = "Scenario_Farmhouse_Checkpoint_Security\r\n" + dyn + "\r\n(Scenario=\"Scenario_Town_Push_Insurgents\",Options=\"?a=(1,2),Scenario=Wrong?Mutators=Hardcore\")\r\n"
                             + "(Scenario=\"Scenario_Bab_Survival\"\r\nScenario_Crossing_Skirmish\r\n";
            var multi = MapCycle.Parse(cycleText);
            if (multi.Count != 5 || multi.Count(e => e.IsEntry) != 4)
                fail("map cycle with a multi-line entry: " + multi.Count + " items, " + multi.Count(e => e.IsEntry) + " scenarios (want 5 and 4): " + string.Join(" | ", multi.Select(e => e.Line)));
            else
            {
                var d = multi[1];
                if (d.Scenario != "Scenario_Crossing_ZY_Checkpoint_Security" || d.Lighting != "Night" || d.Raw != dyn)
                    fail("map cycle: the multi-line entry was read as " + d.Scenario + " / " + d.Lighting + " / " + d.Raw);
                if (string.Join(",", MapCycle.MutatorsOf(d)) != "Fullkit,RandomSeason,RandomTime") fail("map cycle: the entry's mutators read as " + string.Join(",", MapCycle.MutatorsOf(d)));
                if (multi[2].Scenario != "Scenario_Town_Push_Insurgents" || string.Join(",", MapCycle.MutatorsOf(multi[2])) != "Hardcore")
                    fail("map cycle: a comma or bracket in quotes split the entry (" + multi[2].Scenario + ", mutators " + string.Join(",", MapCycle.MutatorsOf(multi[2])) + ")");
                if (multi[3].IsEntry || multi[3].Raw != "(Scenario=\"Scenario_Bab_Survival\"") fail("map cycle: a bracket never closed took other lines: " + multi[3].Line);
                if (multi[4].Scenario != "Scenario_Crossing_Skirmish") fail("map cycle: the line after an unclosed bracket was not read");
                if (MapCycle.Render(multi) != cycleText) fail("map cycle: written back differently:\n" + MapCycle.Render(multi));
                // Moving the entry moves all of its lines.
                var moved = new List<MapCycleEntry>(multi);
                moved.RemoveAt(1); moved.Insert(0, d);
                var again = MapCycle.Parse(MapCycle.Render(moved));
                if (again.Count != 5 || again[0].Raw != dyn) fail("map cycle: a moved multi-line entry did not stay whole");
            }
            string[] scenarios = { "Scenario_Farmhouse_Checkpoint_Security", "Scenario_Town_Push_Insurgents", "Scenario_Bab_Survival", "Scenario_Crossing_Skirmish" };
            for (int i = 0; i < 200; i++)
            {
                var list = Enumerable.Range(0, rnd.Next(8)).Select(k => new MapCycleEntry
                {
                    Scenario = scenarios[rnd.Next(scenarios.Length)],
                    Lighting = new[] { null, "Day", "Night" }[rnd.Next(3)],
                    Mode = new[] { null, null, "CheckpointHardcore" }[rnd.Next(3)],
                }).ToList();
                var back = MapCycle.Parse(MapCycle.Render(list));
                string a = string.Join("|", list.Select(e => e.Scenario + "," + e.Lighting + "," + e.Mode));
                string b = string.Join("|", back.Select(e => e.Scenario + "," + e.Lighting + "," + e.Mode));
                if (a != b || back.Any(e => e.Raw != null)) { fail("map cycle round trip: " + a + " came back as " + b); break; }
            }
            var players = ServerService.ParsePlayers("ID\t | Name\t\t\t\t | NetID\t\t\t | IP\t\t\t | Score\t\t |\n" + new string('=', 79) + "\n"
                                                     + "0\t | Some Player\t | SteamNWI:76561198000000001\t | 203.0.113.9\t | 120\t\t |\n1\t | a|b\t | x\t | y\t | 5 |\n");
            if (players.Count != 2 || players[0].Id != "0" || players[0].Name != "Some Player" || players[0].Score != "120") fail("listplayers: " + players.Count + " players read wrongly");
            else if (players[1].Name != "a|b" || players[1].Score != "5" || players[1].Id != "1") fail("listplayers: a name with | was read as " + players[1].Name + " / score " + players[1].Score);
            if (ServerService.ParsePlayers("ID\t | Name\t | NetID\t | IP\t | Score\t |\n" + new string('=', 79) + "\n").Count != 0) fail("listplayers: an empty server has players");
        }

        /// <summary>
        /// Installing the server with SteamCMD: its output lines (real ones) read as the right step, percentage and outcome,
        /// the command it gets, which folders it may use; and the vote kick lines in the server's Game.ini.
        /// </summary>
        private static void ServerSetupChecks(Action<string> fail)
        {
            void Line(string line, string stage, double? pct, bool success = false, bool upToDate = false, string errorPart = null, long done = 0, long total = 0)
            {
                var p = SteamCmd.ParseLine(line);
                if (stage == null && errorPart == null && !success) { if (p != null) fail("steamcmd: '" + line + "' was read as " + p.Stage); return; }
                if (p == null) { fail("steamcmd: '" + line + "' was not read"); return; }
                if (stage != null && p.Stage != stage) fail("steamcmd: '" + line + "' is " + p.Stage + " instead of " + stage);
                if (pct.HasValue != p.Percent.HasValue || (pct.HasValue && Math.Abs(pct.Value - p.Percent.Value) > 0.001)) fail("steamcmd: '" + line + "' percent " + p.Percent + " instead of " + pct);
                if (p.Success != success || p.UpToDate != upToDate) fail("steamcmd: '" + line + "' success " + p.Success + "/" + p.UpToDate);
                if (errorPart == null ? p.Error != null : p.Error == null || p.Error.IndexOf(errorPart, StringComparison.OrdinalIgnoreCase) < 0) fail("steamcmd: '" + line + "' error " + (p.Error ?? "none"));
                if (done != p.Done || total != p.Total) fail("steamcmd: '" + line + "' bytes " + p.Done + "/" + p.Total);
            }
            Line("Redirecting stderr to 'C:\\steamcmd\\logs\\stderr.txt'", null, null);
            Line("[  0%] Checking for available updates...", "Updating SteamCMD", 0);
            Line("[----] Downloading update (0 of 42,431 KB)...", "Updating SteamCMD", null);
            Line("[ 12%] Downloading update (5,120 of 42,431 KB)...", "Updating SteamCMD", 12);
            Line("[----] Update complete, launching Steamcmd...", "Updating SteamCMD", null);
            Line("Loading Steam API...OK", null, null);
            Line("Connecting anonymously to Steam Public...OK", "Connecting to Steam", null);
            Line("Waiting for user info...OK", "Connecting to Steam", null);
            Line(" Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)", "Preparing", 0);
            Line(" Update state (0x61) downloading, progress: 3.52 (269853491 / 7667458210)", "Downloading", 3.52, done: 269853491, total: 7667458210);
            Line(" Update state (0x81) verifying update, progress: 42.10 (1234 / 5678)", "Checking files", 42.10, done: 1234, total: 5678);
            Line(" Update state (0x5) verifying install, progress: 7.5 (10 / 100)", "Checking files", 7.5, done: 10, total: 100);
            Line(" Update state (0x101) committing, progress: 99.12 (7000 / 7100)", "Installing", 99.12, done: 7000, total: 7100);
            Line(" Update state (0x11) preallocating, progress: 50.00 (1 / 2)", "Making room on the disk", 50, done: 1, total: 2);
            Line("Success! App '581330' fully installed.", "Done", 100, success: true);
            Line("Success! App '581330' already up to date.", "Done", 100, success: true, upToDate: true);
            Line("Error! App '581330' state is 0x202 after update job.", null, null, errorPart: "disk");
            Line("Error! App '581330' state is 0x602 after update job.", null, null, errorPart: "Steam's servers");
            Line("Error! App '581330' state is 0x1234 after update job.", null, null, errorPart: "0x1234");
            Line("ERROR! Failed to install app '581330' (Disk write failure)", null, null, errorPart: "read-only");
            Line("ERROR! Failed to install app '581330' (No Connection)", null, null, errorPart: "internet");
            // Seen live on the first SteamCMD run of a PC; the launcher tries once more by itself.
            Line("ERROR! Failed to install app '581330' (Missing configuration)", null, null, errorPart: "again");
            if (SteamCmd.ParseLine("ERROR! Failed to install app '581330' (Missing configuration)")?.Retryable != true) fail("steamcmd: Missing configuration is not tried again");
            if (SteamCmd.ParseLine("Error! App '581330' state is 0x202 after update job.")?.Retryable != false) fail("steamcmd: a full disk is tried again");
            if (SteamCmd.ParseLine("ERROR! Failed to install app '581330' (Disk write failure)")?.Retryable != false) fail("steamcmd: a folder it cannot write is tried again");
            Line("FAILED (No Connection)", null, null, errorPart: "internet");
            Line("", null, null);
            string args = SteamCmd.ServerArgs(@"C:\My Servers\Sandstorm\", false);
            if (args != "+force_install_dir \"C:\\My Servers\\Sandstorm\" +login anonymous +app_update 581330 +quit") fail("steamcmd arguments: " + args);
            if (!SteamCmd.ServerArgs(@"D:\S", true).EndsWith(" +app_update 581330 validate +quit", StringComparison.Ordinal)) fail("steamcmd arguments with validate: " + SteamCmd.ServerArgs(@"D:\S", true));

            // Folders: not empty text, not a drive, not Windows, not a folder of other things; a new or empty one is fine.
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-srv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                foreach (var bad in new[] { "", "  ", "C:\\", "relative\\folder", Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\Server", "C:\\a\"b" })
                    if (SteamCmd.CheckFolder(bad) == null) fail("steamcmd: the folder '" + bad + "' was taken");
                if (SteamCmd.CheckFolder(System.IO.Path.Combine(temp, "new")) != null) fail("steamcmd: a new folder was refused: " + SteamCmd.CheckFolder(System.IO.Path.Combine(temp, "new")));
                string other = System.IO.Path.Combine(temp, "other");
                System.IO.Directory.CreateDirectory(other);
                System.IO.File.WriteAllText(System.IO.Path.Combine(other, "photo.jpg"), "x");
                if (SteamCmd.CheckFolder(other) == null) fail("steamcmd: a folder with other files in it was taken");
                string resumed = System.IO.Path.Combine(temp, "resumed");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(resumed, "steamapps"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(resumed, "steamapps", "appmanifest_581330.acf"), "x");
                if (SteamCmd.CheckFolder(resumed) != null) fail("steamcmd: a half-installed server folder was refused (it must go on)");
                // A first try that stopped at once leaves only an empty steamapps folder (seen live): the next try goes on there.
                string stopped = System.IO.Path.Combine(temp, "stopped");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(stopped, "steamapps"));
                if (SteamCmd.CheckFolder(stopped) != null) fail("steamcmd: a folder where an install stopped early was refused: " + SteamCmd.CheckFolder(stopped));
            }
            finally { try { System.IO.Directory.Delete(temp, true); } catch { } }

            // Mods on the server, from the server's own log (real lines: the dedicated server of build 24065441 that never logs
            // in to mod.io, and the game client's log of a working login).
            const string ts = "[2026.09.29-17.57.";
            var never = new[]
            {
                "Log file open, 09/29/26 19:57:08",
                ts + "17:087][  0]LogModioGame: ModSubsystem: mod.io initialization complete with result: The operation completed successfully.",
                ts + "25:616][ 80]LogOnlineSession: Warning: STEAM (NWI): Empty session setting ModList  : OnlineService of type String",
            };
            void Verdict(string what, string[] lines, bool modsOn, bool just, ModVerdict want, string mustSay = null, ServerModioAccount acc = null)
            {
                var st = ServerModCheck.Parse(lines, modsOn, just, acc);
                if (st.Verdict != want) fail("server mods, " + what + ": " + st.Verdict + " instead of " + want);
                if (mustSay != null && st.Text.IndexOf(mustSay, StringComparison.OrdinalIgnoreCase) < 0) fail("server mods, " + what + ": the text says '" + st.Text + "'");
                if (string.IsNullOrWhiteSpace(st.Text)) fail("server mods, " + what + ": no text");
            }
            var noSubs = new ServerModioAccount { LoggedIn = true, UserId = 3 };
            Verdict("not logged in", never, true, false, ModVerdict.NoLogin, "not logged in");
            Verdict("login expired", never, true, false, ModVerdict.NoLogin, "expired", new ServerModioAccount { Expired = true });
            Verdict("logged in, no subscriptions", never, true, false, ModVerdict.NoMods, "not subscribed", noSubs);
            Verdict("logged in with the game's account", never, true, false, ModVerdict.NoMods, "game's mod.io account", new ServerModioAccount { LoggedIn = true, SameAsGame = true, Subscriptions = new List<long> { 5 } });
            Verdict("just started", never, true, true, ModVerdict.Waiting);
            Verdict("mods off", never, false, false, ModVerdict.NotAsked);
            var working = new[]
            {
                "Log file open, 09/27/26 03:42:06",
                ts + "17:087][  0]LogModioGame: ModSubsystem: mod.io initialization complete with result: The operation completed successfully.",
                ts + "18:179][132]LogModioGame: ModSubsystem: User authentication successful",
                ts + "18:523][139]LogModioGame: ModSubsystem: ModManager enabled",
                ts + "18:523][139]LogModioGame: ModSubsystem: Mod added, id: 1457355 to PendingChanges with state Added",
                ts + "18:523][139]LogModioGame: ModSubsystem: Mod added, id: 150867 to PendingChanges with state Added",
                ts + "18:747][139]LogINSModioGame: MountMod: CountFlex in C:\\Users\\Public\\mod.io\\254\\mods\\1457355 (id: 1457355, package: /CountFlex/, content: ../../../Insurgency/Plugins/CountFlex/Content)",
                ts + "18:677][139]LogINSModioGame: MountMod: ISMCm in C:\\Users\\Public\\mod.io\\254\\mods\\150867 (id: 150867, package: /ISMCm/, content: ../../../Insurgency/Mods/ISMCm/Content)",
            };
            Verdict("mods mounted", working, true, false, ModVerdict.Loaded, "2 mods");
            if (ServerModCheck.Parse(working, true).Added.Count != 2) fail("server mods: the added mods were not counted");
            Verdict("added, not mounted yet", working.Take(6).ToArray(), true, false, ModVerdict.Downloading, "downloading 2 mods");
            Verdict("already mounted", new[] { working[0], ts + "19:000][140]LogModioGame: ModSubsystem: OnModActivatedFromCloud: Mod ISMCmod[150867] is mounted and up-to-date. Not remounting." }, true, false, ModVerdict.Loaded, "1 mod");
            Verdict("logged in by the log, nothing came", never.Concat(new[] { ts + "30:000][  0]LogModioGame: ModSubsystem: User authentication successful" }).ToArray(), true, false, ModVerdict.NoMods);
            Verdict("refused, with a code", never.Concat(new[] { ts + "30:000][  0]LogModioGame: ModSubsystem: User authentication failed with error: 11007, message Invalid token" }).ToArray(), true, false, ModVerdict.LoginFailed, "11007");
            Verdict("terms not accepted", never.Concat(new[] { ts + "30:000][  0]LogModioGame: ModSubsystem: User authentication failed (Terms of Use): Terms not accepted" }).ToArray(), true, false, ModVerdict.LoginFailed, "Terms not accepted");
            // A wrong or used security code: mod.io's answer in the SDK's own line (seen live with -SecurityCode=00000).
            string refused = ts + "55:328][  0]LogModio: [06:15:54:794ms][Error][Http] Non 200-204 response received: {\"error\":{\"code\":401,\"error_ref\":11014,\"message\":\"Authorization failed. Invalid security code.\"}}";
            Verdict("security code refused", never.Take(2).Concat(new[] { refused }).Concat(never.Skip(2)).ToArray(), true, false, ModVerdict.LoginFailed, "Invalid security code");
            Verdict("old code refused, saved login loaded the mods", working.Concat(new[] { refused }).ToArray(), true, false, ModVerdict.Loaded);
            Verdict("old code refused first, then the mods mounted", never.Take(2).Concat(new[] { refused }).Concat(working.Skip(4)).ToArray(), true, false, ModVerdict.Loaded, "2 mods");
            Verdict("another mod.io error is not a refused login", never.Concat(new[] { ts + "56:000][  0]LogModio: [06:15:55:000ms][Error][Http] Non 200-204 response received: {\"error\":{\"code\":404,\"error_ref\":15022,\"message\":\"Mod not found.\"}}" }).ToArray(), true, false, ModVerdict.NoLogin);
            // Lines of an earlier run in the same file do not count.
            Verdict("old run loaded, this one did not", working.Concat(never).ToArray(), true, false, ModVerdict.NoLogin);
            Verdict("empty log", new string[0], true, false, ModVerdict.Waiting);

            // The server's saved login (user.json of the ModServer profile): read without the token, expiry and the game's own account seen.
            string modio = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-modio-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string User(long id, long expiry, string subs) => "{\"OAuth\":{\"expiry\":" + expiry + ",\"status\":0,\"token\":\"secret-token\"},\"Profile\":{\"id\":" + id
                                                                 + ",\"username\":\"srv\"},\"subscriptions\":[" + subs + "],\"version\":1}";
                if (ServerModio.ReadAccount(modio).LoggedIn) fail("modio login: no folder, but logged in");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(modio, "ModServer"));
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(modio, "Player"));
                var now = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
                long future = 1830000000, past = 1700000000;
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "ModServer", "user.json"), User(11, future, "150867,1457355,150867"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "Player", "user.json"), User(22, future, "1"));
                var a = ServerModio.ReadAccount(modio, now);
                if (!a.LoggedIn || a.Expired || a.UserName != "srv" || a.UserId != 11 || a.SameAsGame || !a.Subscriptions.SequenceEqual(new long[] { 150867, 1457355 }) || a.ExpiresUtc?.Year != 2027)
                    fail("modio login read wrong: " + a.LoggedIn + " " + a.UserName + " " + a.UserId + " " + a.SameAsGame + " " + string.Join(",", a.Subscriptions) + " " + a.ExpiresUtc);
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "Player", "user.json"), User(11, future, "1"));
                if (!ServerModio.ReadAccount(modio, now).SameAsGame) fail("modio login: the game's own account on the server not seen");
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "ModServer", "user.json"), User(11, past, ""));
                var old = ServerModio.ReadAccount(modio, now);
                if (old.LoggedIn || !old.Expired) fail("modio login: an expired login counts");
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "ModServer", "user.json"), "{\"Profile\":{\"id\":11},\"subscriptions\":[]}");
                if (ServerModio.ReadAccount(modio, now).LoggedIn) fail("modio login: a profile without a token counts as logged in");
                System.IO.File.WriteAllText(System.IO.Path.Combine(modio, "ModServer", "user.json"), "{broken");
                var broken = ServerModio.ReadAccount(modio, now);
                if (broken.LoggedIn || broken.Problem == null) fail("modio login: a broken file was not reported");
            }
            finally { try { System.IO.Directory.Delete(modio, true); } catch { } }

            // Security codes on the command line.
            if (ServerModio.CleanCode(" 12 345 ") != "12345" || ServerModio.CleanCode("1234") != null || ServerModio.CleanCode("abcde") != null || ServerModio.CleanCode("123456") != null)
                fail("security code: 5 digits are not taken as they should");
            if (ServerModio.WithSecurityCode("Map -SecurityCode=none -log", "12345") != "Map -SecurityCode=12345 -log") fail("security code: not replaced: " + ServerModio.WithSecurityCode("Map -SecurityCode=none -log", "12345"));
            if (ServerModio.WithSecurityCode("Map -log", "12345") != "Map -log -SecurityCode=12345") fail("security code: not added");
            if (ServerModio.WithSecurityCode("Map -mysecuritycode=1 -securitycode=none", "12345") != "Map -mysecuritycode=1 -SecurityCode=12345") fail("security code: another option was taken for it");
            if (ServerModio.SecurityCodeIn("a -securitycode=\"777\" b") != "777" || ServerModio.SecurityCodeIn("a b") != null) fail("security code: not read");
            if (ServerModio.HideCode("a -SecurityCode=12345 b") != "a -SecurityCode=<code> b" || ServerModio.HideCode("a -SecurityCode=none") != "a -SecurityCode=none") fail("security code: not hidden right");
            if (!ServerModio.IsEmail("server@example.com") || ServerModio.IsEmail("server@") || ServerModio.IsEmail("a b@c.d")) fail("e-mail check wrong");
            if (ServerModio.ErrorText(422, "{\"error\":{\"code\":422,\"error_ref\":13009,\"message\":\"Validation Failed.\",\"errors\":{\"email\":\"The \\\"email\\\" must be a valid email address.\"}}}").IndexOf("valid email", StringComparison.Ordinal) < 0)
                fail("mod.io errors: the field message is not shown");
            if (ServerModio.ErrorText(500, "<html>").Length == 0) fail("mod.io errors: nothing for an answer that is not JSON");

            // The player's own .bat file.
            string bat = "@echo off\r\nrem start InsurgencyServer.exe Old -Port=1\r\ntitle InsurgencyServer\r\nset MAP=Farmhouse\r\nset \"PORT=27102\"\r\ncd /d \"C:\\Servers\\sandstorm_server\"\r\n"
                       + "start \"Sandstorm\" /wait \"C:\\Servers\\sandstorm_server\\InsurgencyServer.exe\" %MAP%?Scenario=Scenario_Farmhouse_Checkpoint_Security?MaxPlayers=8 -Port=%PORT% ^\r\n"
                       + "  -QueryPort=27131 -hostname=\"Brett & Co\" -log -Mods -SecurityCode=none -mutators=MapIcons,MoreAmmo -Motd=%%DAY%% > server.log\r\npause\r\n";
            var notes = new List<string>();
            string batArgs = ServerArgs.FromBatch(bat, notes);
            string wantArgs = "Farmhouse?Scenario=Scenario_Farmhouse_Checkpoint_Security?MaxPlayers=8 -Port=27102 -QueryPort=27131 -hostname=\"Brett & Co\" -log -Mods -SecurityCode=none -mutators=MapIcons,MoreAmmo -Motd=%DAY%";
            if (batArgs != wantArgs) fail(".bat: read as\n" + batArgs + "\ninstead of\n" + wantArgs);
            if (notes.Count != 0) fail(".bat: notes for a file with every variable set: " + string.Join(" ", notes));
            if (!ServerArgs.Has(batArgs, "mods") || ServerArgs.Has(batArgs, "Mod") || ServerArgs.Value(batArgs, "hostname") != "Brett & Co" || ServerArgs.Value(batArgs, "Port") != "27102" || ServerArgs.FirstMap(batArgs) != "Farmhouse")
                fail(".bat: options read wrong from " + batArgs);
            ServerArgs.FromBatch("InsurgencyServer-Win64-Shipping.exe Map -Port=%GAMEPORT%", notes);
            if (notes.Count != 1 || !notes[0].Contains("GAMEPORT")) fail(".bat: an unset variable is not noted");
            if (ServerArgs.FromBatch("@echo off\r\necho Starting InsurgencyServer\r\ntaskkill /im InsurgencyServer.exe\r\npause") != null) fail(".bat: a file that does not start the server gave options");
            if (ServerArgs.Clean("start InsurgencyServer.exe Oilfield -log") != "Oilfield -log" || ServerArgs.Clean("Oilfield\r\n-log ^\r\n-Mods") != "Oilfield -log -Mods") fail(".bat: a pasted line not cleaned");
            if (ServerArgs.Clean("Oilfield -hostname=MyInsurgencyServer -log") != "Oilfield -hostname=MyInsurgencyServer -log") fail("a server name with InsurgencyServer in it was cut: " + ServerArgs.Clean("Oilfield -hostname=MyInsurgencyServer -log"));
            if (ServerArgs.FirstMap("-Mods -log") != null) fail("a line without a map has a first map");

            // The old token section goes, the rest of the files stays; Mods.txt is read for the list.
            string fake = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sml-tok-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(fake);
                System.IO.File.WriteAllText(System.IO.Path.Combine(fake, "InsurgencyServer.exe"), "");
                var inst = ServerInstall.At(fake);
                Directory.CreateDirectory(inst.SavedConfigDir);
                System.IO.File.WriteAllText(inst.GameUserSettingsPath, "[/Script/ModKit.ModIOClient]\r\nbHasUserAcceptedTerms=True\r\nAccessToken=tokentokentokentoken1234\r\n\r\n[/Script/Engine.GameUserSettings]\r\nbUseVSync=False\r\n");
                System.IO.File.WriteAllText(inst.EngineIniPath, "[Core.Log]\r\nLogSkinnedMeshComp=Error\r\n\r\n[/script/modkit.modioclient]\r\nAccessToken=tokentokentokentoken1234\r\n");
                var changed = ServerModio.RemoveObsoleteToken(inst);
                if (changed.Count != 2) fail("old token: taken out of " + changed.Count + " files instead of 2");
                foreach (var path in new[] { inst.EngineIniPath, inst.GameUserSettingsPath })
                {
                    string text = System.IO.File.ReadAllText(path);
                    if (text.IndexOf("modkit", StringComparison.OrdinalIgnoreCase) >= 0 || text.Contains("AccessToken")) fail("old token: still in " + System.IO.Path.GetFileName(path) + ":\n" + text);
                }
                if (!System.IO.File.ReadAllText(inst.GameUserSettingsPath).Contains("bUseVSync=False") || !System.IO.File.ReadAllText(inst.EngineIniPath).Contains("LogSkinnedMeshComp=Error"))
                    fail("old token: the rest of the files was lost");
                if (ServerModio.RemoveObsoleteToken(inst).Count != 0) fail("old token: files changed again with nothing to take out");
                Directory.CreateDirectory(inst.ServerConfigDir);
                string modsTxt = System.IO.Path.Combine(inst.ServerConfigDir, "Mods.txt");
                System.IO.File.WriteAllText(modsTxt, "\uFEFF150867\r\n// ISMC\r\n1457355 // bots\r\n\r\nnot-a-mod\r\n150867\r\n");
                var bad = new List<string>();
                var ids = ServerModio.ReadModsTxt(modsTxt, bad);
                if (ids == null || !ids.SequenceEqual(new long[] { 150867, 1457355 }) || bad.Count != 1) fail("Mods.txt: read as " + (ids == null ? "nothing" : string.Join(",", ids)) + ", bad " + bad.Count);
                if (ServerModio.ReadModsTxt(System.IO.Path.Combine(fake, "none.txt")) != null) fail("Mods.txt: a missing file read as a list");
            }
            finally { try { System.IO.Directory.Delete(fake, true); } catch { } }

            // Vote kick: on writes the guide's two lines once; off takes only the launcher's own lines out again.
            string on = ServerPlanner.ApplyVoteKick("[Rcon]\r\nbEnabled=True\r\n", true);
            if (!ServerPlanner.VoteKickOn(on)) fail("vote kick: switched on but not in Game.ini:\n" + on);
            if (ServerPlanner.ApplyVoteKick(on, true) != on) fail("vote kick: switching it on twice changed Game.ini");
            if (on.Split('\n').Count(l => l.Trim().StartsWith("bVotingEnabled")) != 1) fail("vote kick: bVotingEnabled is not there exactly once");
            string off = ServerPlanner.ApplyVoteKick(on, false);
            if (ServerPlanner.VoteKickOn(off) || off.Contains("TeamInfo") || !off.Contains("[Rcon]")) fail("vote kick: switched off but Game.ini is:\n" + off);
            string was = "[/Script/Insurgency.TeamInfo]\r\nbVotingEnabled=False\r\n";
            if (!ServerPlanner.VoteKickOn(ServerPlanner.ApplyVoteKick(was, true))) fail("vote kick: an old bVotingEnabled=False was not replaced");
            string own = "[/Script/Insurgency.TeamInfo]\r\nbVotingEnabled=True\r\n+TeamVoteIssues=/Script/Insurgency.VoteIssueKick\r\n+TeamVoteIssues=/Script/Insurgency.VoteIssueOther\r\n";
            if (ServerPlanner.ApplyVoteKick(own, false) != own) fail("vote kick: switching it off removed voting set up by hand with another issue");
        }

        /// <summary>The player's own start command: program and arguments, and where the launcher's options go.</summary>
        private static void StartCommandChecks(Action<string> fail)
        {
            // The updater takes release files from this project under its current and its new name (a renamed repository
            // hands out the new address), and nothing from anywhere else.
            foreach (var (owner, ok) in new[] { (Updater.Repo, true), (Updater.NewRepo, true), ("someone/else", false), ("goranbalsic/insurgency-sandstorm-mod-launcher-evil", false) })
            {
                string json = "{\"tag_name\":\"v9.0.0\",\"html_url\":\"x\",\"assets\":[{\"name\":\"SandstormModLauncher-v9.0.0.zip\",\"browser_download_url\":\"https://github.com/" + owner
                              + "/releases/download/v9.0.0/SandstormModLauncher-v9.0.0.zip\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/" + owner + "/releases/download/v9.0.0/SHA256SUMS.txt\"}]}";
                var info = Updater.Parse(json);
                if ((info.ZipUrl != null) != ok || (info.SumsUrl != null) != ok) fail("updater: release files from " + owner + (ok ? " were not taken" : " were taken"));
            }
            // Chat text and reasons stay one command: a | in them would make the game run what follows it.
            string said = ServerService.Reason("hi | exit\n\"x\"");
            if (said.Contains("|") || said.Contains("\n") || said.Contains("\"")) fail("chat text reaches the server as " + said);
            string dir = Path.Combine(Path.GetTempPath(), "sml start " + Guid.NewGuid().ToString("N").Substring(0, 6));
            string bat = Path.Combine(dir, "my start.bat");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(bat, "@echo off");
                var cases = new[]
                {
                    ("\"" + bat + "\" -a {options} -b", "-x 1", bat, "-a -x 1 -b"),
                    ("\"" + bat + "\"", "-x 1", bat, "-x 1"),
                    (bat + " -a", "-x", bat, "-a -x"),                      // a path with spaces, no quotes
                    (bat, "", bat, ""),
                    ("tool.exe {options}", "-x  -y", "tool.exe", "-x -y"),
                    ("  tool.exe   -a  ", "", "tool.exe", "-a"),
                    ("\"C:\\no such\\x.exe\" -a", "-o", "C:\\no such\\x.exe", "-a -o"),
                };
                foreach (var (cmd, options, file, args) in cases)
                {
                    var (f, a) = LaunchService.SplitCommand(cmd, options);
                    if (f != file || a != args) fail("start command " + cmd + " with " + options + ": " + f + " | " + a + " instead of " + file + " | " + args);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Translation tables: reading what Excel, Sheets and LibreOffice save, writing, and using them.</summary>
        private static void TranslationChecks(Random rnd, Action<string> fail)
        {
            string[] bits = { "a", "Launch", "中文", "ñ", " ", ",", ";", "\"", "\"\"", "\n", "{0}", "·", "x y", "'", "\t", "=" };
            string Random(int max, bool trim)
            {
                var sb = new StringBuilder();
                int n = 1 + rnd.Next(max);
                for (int k = 0; k < n; k++) sb.Append(bits[rnd.Next(bits.Length)]);
                string s = sb.ToString();
                return trim ? Loc.Key(s) : s;
            }
            for (int i = 0; i < 300; i++)
            {
                var rows = new List<Loc.Row>();
                int count = rnd.Next(6);
                for (int k = 0; k < count; k++)
                {
                    string en = Random(6, true);
                    if (en.Length == 0) en = "text " + k;
                    rows.Add(new Loc.Row { English = en, Translation = Random(6, false), Where = Random(3, false) });
                }
                var back = Loc.ReadCsv(Loc.WriteCsv(rows));
                string a = string.Join("¦", rows.Select(r => r.English + "→" + r.Translation + "→" + r.Where));
                string b = string.Join("¦", back.Select(r => r.English + "→" + r.Translation + "→" + r.Where));
                if (a != b) { fail("translation table round trip: " + a + " came back as " + b); break; }
            }
            // As spreadsheet programs save it: semicolons, other column orders, no header, the byte order mark, CRLF.
            var semi = Loc.ReadCsv("﻿English;Translation;Where\r\n\"Hello; there\";\"Hola\";\"Main window\"\r\nPlay;Jugar;\r\n");
            if (semi.Count != 2 || semi[0].English != "Hello; there" || semi[0].Translation != "Hola" || semi[1].Translation != "Jugar") fail("translation table: a semicolon file was read wrongly");
            var order = Loc.ReadCsv("Where,Note,Translation,English\nMain window,x,Einstellungen,Settings\n");
            if (order.Count != 1 || order[0].English != "Settings" || order[0].Translation != "Einstellungen" || order[0].Where != "Main window")
                fail("translation table: columns in another order were read wrongly");
            var bare = Loc.ReadCsv("Launch,启动\n\"Two\nlines\",\"两\n行\"\n");
            if (bare.Count != 2 || bare[0].Translation != "启动" || bare[1].English != "Two\nlines" || bare[1].Translation != "两\n行") fail("translation table: a file without a header was read wrongly");
            if (Loc.ReadCsv("English,Translation\n,x\n  ,y\n").Count != 0) fail("translation table: rows without English text were kept");
            // Files: UTF-8 with or without the mark, and a file saved in the PC's old code page does not stop anything.
            string tmp = Path.Combine(Path.GetTempPath(), "sml-loc-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllText(tmp, "English,Translation\nPlay,玩\n", new UTF8Encoding(false));
                if (Loc.ReadCsv(Loc.ReadFile(tmp)).FirstOrDefault()?.Translation != "玩") fail("translation file: UTF-8 without the byte order mark was read wrongly");
                File.WriteAllBytes(tmp, new byte[] { (byte)'P', (byte)'l', (byte)'a', (byte)'y', (byte)',', 0xE9, 0xFF, (byte)'\n' });
                if (Loc.ReadCsv(Loc.ReadFile(tmp)).FirstOrDefault()?.Translation != Encoding.Default.GetString(new byte[] { 0xE9, 0xFF }))
                    fail("translation file: a file in the old code page was not read with it");
                Loc.MakeTranslationFile(null, tmp);
                var made = Loc.ReadCsv(Loc.ReadFile(tmp));
                var template = Loc.Template();
                if (made.Count != template.Count + 2 || made[0].English != Loc.LanguageKey || made[1].English != Loc.TranslatorKey || made.Any(r => r.Translation.Length > 0))
                    fail("translation file: a new file has " + made.Count + " rows for " + template.Count + " texts, or translations filled in");
                if (!File.ReadAllBytes(tmp).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) fail("translation file: saved without the byte order mark Excel needs");
            }
            finally { try { File.Delete(tmp); } catch { } }
            // The table the launcher carries: every text once, and every {n} usable.
            var all = Loc.Template();
            if (all.Count < 500 || !all.Any(r => r.English == "Launch") || !all.Any(r => r.English == "Settings")) fail("translation template: " + all.Count + " texts, some missing");
            foreach (var dup in all.GroupBy(r => r.English).Where(g => g.Count() > 1).Take(3)) fail("translation template: \"" + dup.Key + "\" is there twice");
            foreach (var r in all.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.English, @"\{\d")))
                try { string.Format(r.English, "a", "b", "c", "d", "e"); }
                catch (FormatException) { fail("translation template: bad placeholder in \"" + r.English + "\""); }
            // Using a table: spaces around a piece stay, a translation that breaks the placeholders falls back to English.
            try
            {
                Loc.UseTable(new[]
                {
                    new Loc.Row { English = "Loading", Translation = "Cargando" },
                    new Loc.Row { English = "Found {0} maps", Translation = "{0} mapas" },
                    new Loc.Row { English = "{0} of {1}", Translation = "{0} de {2}" },
                    new Loc.Row { English = "Two\nlines", Translation = "Dos\r\nlíneas " },
                    new Loc.Row { English = Loc.LanguageKey, Translation = "Español" },
                }, "test");
                if (Loc.T(" Loading ") != " Cargando ") fail("translation: spaces around a text were lost (" + Loc.T(" Loading ") + ")");
                if (Loc.F("Found {0} maps", 3) != "3 mapas") fail("translation: a text with a value was " + Loc.F("Found {0} maps", 3));
                if (Loc.F("{0} of {1}", 1, 2) != "1 of 2") fail("translation: a broken translation was used: " + Loc.F("{0} of {1}", 1, 2));
                if (Loc.T("Two\r\nlines") != "Dos\nlíneas") fail("translation: a text over two lines was " + Loc.T("Two\r\nlines"));
                if (Loc.T("Not there") != "Not there" || Loc.T(null) != null || Loc.T("") != "") fail("translation: a text without translation changed");
                if (Loc.T(Loc.LanguageKey) != Loc.LanguageKey) fail("translation: the language name row is used as a text");
                if (Loc.CurrentId != "test" || Loc.IsEnglish) fail("translation: the table in use is not the test one");
            }
            finally { Loc.UseTable(null, ""); }
            if (!Loc.IsEnglish || Loc.CurrentId != "" || Loc.T("Loading") != "Loading") fail("translation: English is not back after the test");
        }

        public static int Run(int steps, int seed, Action<string> print)
        {
            var rnd = new Random(seed);
            var failures = new List<string>();
            var ops = new Dictionary<string, int>();
            void Fail(string m) { failures.Add(m); if (failures.Count <= 40) print("FAIL " + m); }
            LogChecks(Fail);
            ServerFormatChecks(new Random(seed), Fail);
            TranslationChecks(new Random(seed), Fail);
            StartCommandChecks(Fail);
            ServerSetupChecks(Fail);
            var settings = new AppSettings();
            RconSetup.EnsureSettings(settings);
            using (var server = new FakeRconServer(settings.RconPassword, seed) { Chaos = true, SlowMs = 1400 })
            {
                server.Start();
                var rcon = new GameRcon(() => settings) { PortOverride = server.Port, ReplyTimeoutMs = 1000, ConnectTimeoutMs = 1000 };
                int transient = 0, lastTransient = 0;
                for (int i = 1; i <= steps; i++)
                {
                    int op = rnd.Next(10);
                    string name = new[] { "travel", "set", "read", "count", "restart", "wrong password", "probe", "raw", "as player", "open while loading" }[op];
                    ops[name] = ops.TryGetValue(name, out var c) ? c + 1 : 1;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        switch (op)
                        {
                            case 0:
                            {
                                string url = "Map" + rnd.Next(100) + "?Scenario=Scenario_" + rnd.Next(1000) + "?Mutators=" + string.Join(",", Enumerable.Range(0, rnd.Next(12)).Select(k => "Mut" + k));
                                var r = rcon.Travel(url);
                                if (r.Ok && server.LastTravel != url) Fail(i + " travel: the game got " + server.LastTravel + " instead of " + url);
                                if (r.Ok && r.Text != "Travelling to \"" + url + "\"...") Fail(i + " travel: reply mixed up: " + r.Text);
                                if (!r.Ok) transient++;
                                break;
                            }
                            case 1:
                            {
                                var props = Enumerable.Range(0, 1 + rnd.Next(6)).Select(k => new KeyValuePair<string, string>(rnd.Next(5) == 0 ? "NoSuchProp" + k : "Property" + rnd.Next(180), rnd.Next(1000).ToString())).GroupBy(kv => kv.Key).Select(g => g.Last()).ToList();
                                Dictionary<string, string> failed;
                                try { failed = rcon.SetProperties(props); }
                                catch (RconException) { transient++; break; }
                                foreach (var kv in props)
                                {
                                    bool exists = !kv.Key.StartsWith("NoSuchProp", StringComparison.Ordinal);
                                    if (exists && failed.ContainsKey(kv.Key)) Fail(i + " set " + kv.Key + ": reported as not taken (" + failed[kv.Key] + ")");
                                    if (!exists && !failed.ContainsKey(kv.Key)) Fail(i + " set " + kv.Key + ": a property that does not exist was reported as set");
                                    if (exists && server.Props[kv.Key] != kv.Value) Fail(i + " set " + kv.Key + ": the game has " + server.Props[kv.Key]);
                                }
                                break;
                            }
                            case 2:
                            {
                                string filter = rnd.Next(3) == 0 ? "" : "Property" + rnd.Next(18);
                                Dictionary<string, string> read;
                                try { read = rcon.ReadProperties(filter, out var mode); if (mode != "INSTestGameMode") Fail(i + " read: mode " + mode); }
                                catch (RconException) { transient++; break; }
                                var want = server.Props.Where(p => filter.Length == 0 || p.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                                if (read.Count != want.Count) Fail(i + " read '" + filter + "': " + read.Count + " values instead of " + want.Count + " (a reply was cut or mixed)");
                                foreach (var kv in want) if (!read.TryGetValue(kv.Key, out var v) || !SameNumberOrText(v, kv.Value)) { Fail(i + " read " + kv.Key + ": " + v + " instead of " + kv.Value); break; }
                                break;
                            }
                            case 3:
                            {
                                lock (server.Players)
                                {
                                    server.Players.Clear();
                                    for (int k = rnd.Next(30); k > 0; k--) server.Players.Add((rnd.Next(2), rnd.Next(4) > 0));
                                }
                                SortedDictionary<int, (int humans, int bots)> counted;
                                try { counted = rcon.CountPlayers(); }
                                catch (RconException) { transient++; break; }
                                foreach (int team in new[] { 0, 1 })
                                {
                                    int h = server.Players.Count(p => p.team == team && !p.bot), b = server.Players.Count(p => p.team == team && p.bot);
                                    counted.TryGetValue(team, out var got);
                                    if (got.humans != h || got.bots != b) Fail(i + " count team " + team + ": " + got.humans + "/" + got.bots + " instead of " + h + "/" + b);
                                }
                                break;
                            }
                            case 4:
                            {
                                int before = server.RoundRestarts;
                                var r = rcon.RestartRound(rnd.Next(2) == 0);
                                if (r.Ok && server.RoundRestarts != before + 1) Fail(i + " restart: counted " + (server.RoundRestarts - before));
                                if (!r.Ok) transient++;
                                break;
                            }
                            case 5:
                            {
                                var wrong = new GameRcon(() => settings) { PortOverride = server.Port, PasswordOverride = "wrong" + rnd.Next(), ReplyTimeoutMs = 1000, ConnectTimeoutMs = 1000 };
                                string p = wrong.Probe();
                                if (p == null) Fail(i + " a wrong password was accepted");
                                break;
                            }
                            case 6:
                                if (rcon.Probe() != null) transient++;
                                break;
                            case 8:
                            {
                                // Console commands run as the player: every single command, in order, "|" split, quotes made safe.
                                var pool = new[] { "EnableCheats", "AIDifficulty 0." + rnd.Next(10), "Slomo 1", "say \"hi\" | GodMode", " | ", "SetRoundTimer " + rnd.Next(2000) + " |AIToggle" };
                                var lines = Enumerable.Range(0, 1 + rnd.Next(4)).Select(k => pool[rnd.Next(pool.Length)]).ToList();
                                var want = lines.SelectMany(l => l.Split('|')).Select(c => c.Trim().Replace("\"", "'")).Where(c => c.Length > 0).ToList();
                                int before;
                                lock (server.Deferred) before = server.Deferred.Count;
                                try { rcon.RunAsPlayer(lines); }
                                catch (RconException) { transient++; break; }
                                List<string> got;
                                lock (server.Deferred) got = server.Deferred.Skip(before).ToList();
                                if (!got.SequenceEqual(want)) Fail(i + " as player: the game got [" + string.Join(" / ", got) + "] instead of [" + string.Join(" / ", want) + "]");
                                break;
                            }
                            case 9:
                            {
                                // A deferred open freezes the game while the map loads: no answer in time, but the command is known to be sent.
                                string open = "open Map" + rnd.Next(100) + "?Scenario=Scenario_" + rnd.Next(1000);
                                bool slow = rnd.Next(2) == 0;
                                server.LoadMs = slow ? rcon.ReplyTimeoutMs + 500 : 0;
                                int before;
                                lock (server.Deferred) before = server.Deferred.Count;
                                try
                                {
                                    rcon.Run(GameRcon.AsPlayer(open));
                                    if (slow) Fail(i + " open while loading: an answer came although the game was still loading");
                                }
                                catch (RconException ex)
                                {
                                    transient++;
                                    if (!slow && ex.Kind != RconError.Timeout && ex.Kind != RconError.Closed) Fail(i + " open: " + ex.Kind + " " + ex.Message);
                                    if (slow && ex.Kind == RconError.Timeout && !ex.Delivered) Fail(i + " open while loading: the timeout does not say the command was sent");
                                    if (ex.Kind == RconError.Timeout && !ex.Delivered) break;
                                    if (ex.Kind != RconError.Timeout) break;
                                }
                                finally { server.LoadMs = 0; }
                                // A busy game gets to the command late, but it gets it.
                                bool Arrived() { lock (server.Deferred) return server.Deferred.Skip(before).Contains(open); }
                                var until = DateTime.UtcNow.AddMilliseconds(server.SlowMs + 800);
                                while (!Arrived() && DateTime.UtcNow < until) Thread.Sleep(20);
                                if (!Arrived()) Fail(i + " open: the game did not get " + open);
                                break;
                            }
                            default:
                            {
                                // Unknown and odd commands: an answer (maybe empty) or a clean error, never a hang.
                                try { rcon.Run(new[] { "", "sml " + new string('x', rnd.Next(3000)), GameRcon.Quote("stat fps \"quoted\"") }.Take(1 + rnd.Next(3)).ToArray()); }
                                catch (RconException) { transient++; }
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { Fail(i + " " + name + ": " + ex.GetType().Name + " " + ex.Message); }
                    // Nothing may hang: one call waits at most for its timeouts (connect + auth + a few replies).
                    if (sw.ElapsedMilliseconds > 9000) Fail(i + " " + name + " took " + sw.ElapsedMilliseconds + " ms");
                    // A command the client gave up on is still carried out when the busy game gets to it (like the real game):
                    // let it land before the next step compares values.
                    if (transient > lastTransient) { Thread.Sleep(server.SlowMs + 300); lastTransient = transient; }
                }
                // A server that is gone: a quick, clean "unreachable".
                server.Dispose();
                var sw2 = Stopwatch.StartNew();
                string gone = rcon.Probe();
                if (gone == null) Fail("a stopped server still answered");
                if (sw2.ElapsedMilliseconds > 3000) Fail("finding out that nothing listens took " + sw2.ElapsedMilliseconds + " ms");
                print("RCON torture: " + steps + " steps (seed " + seed + "): " + string.Join(", ", ops.OrderBy(o => o.Key).Select(o => o.Key + " " + o.Value)) +
                      "; " + transient + " clean errors from the misbehaving server");
            }
            print(failures.Count == 0 ? "ALL CHECKS PASSED" : failures.Count + " FAILURES");
            return failures.Count == 0 ? 0 : 1;
        }
    }
}
