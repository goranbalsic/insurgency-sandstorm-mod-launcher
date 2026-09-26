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

        public static int Run(int steps, int seed, Action<string> print)
        {
            var rnd = new Random(seed);
            var failures = new List<string>();
            var ops = new Dictionary<string, int>();
            void Fail(string m) { failures.Add(m); if (failures.Count <= 40) print("FAIL " + m); }
            var settings = new AppSettings();
            RconSetup.EnsureSettings(settings);
            using (var server = new FakeRconServer(settings.RconPassword, seed) { Chaos = true, SlowMs = 1400 })
            {
                server.Start();
                var rcon = new GameRcon(() => settings) { PortOverride = server.Port, ReplyTimeoutMs = 1000, ConnectTimeoutMs = 1000 };
                int transient = 0, lastTransient = 0;
                for (int i = 1; i <= steps; i++)
                {
                    int op = rnd.Next(8);
                    string name = new[] { "travel", "set", "read", "count", "restart", "wrong password", "probe", "raw" }[op];
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
