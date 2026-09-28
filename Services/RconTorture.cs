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

        /// <summary>The player's own start command: program and arguments, and where the launcher's options go.</summary>
        private static void StartCommandChecks(Action<string> fail)
        {
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
