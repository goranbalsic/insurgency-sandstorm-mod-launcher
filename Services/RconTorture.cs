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

        public static int Run(int steps, int seed, Action<string> print)
        {
            var rnd = new Random(seed);
            var failures = new List<string>();
            var ops = new Dictionary<string, int>();
            void Fail(string m) { failures.Add(m); if (failures.Count <= 40) print("FAIL " + m); }
            LogChecks(Fail);
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
