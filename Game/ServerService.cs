using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;
using SandstormModLauncher.Models;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    public sealed class ServerPlayer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string NetId { get; set; }
        public string Ip { get; set; }
        public string Score { get; set; }
    }

    /// <summary>
    /// Runs the dedicated server on this PC (writes its files, starts and stops it) and controls a running one over its
    /// RCON, here or on another PC. Player names, ids and addresses from the server are shown, never logged.
    /// </summary>
    public sealed class ServerService
    {
        private readonly Func<AppSettings> settings;
        private readonly Func<ServerInstall> install;
        public GameRcon Rcon { get; }
        public GameMonitor Monitor { get; private set; }

        public ServerService(Func<AppSettings> settings, Func<ServerInstall> install)
        {
            this.settings = settings;
            this.install = install;
            Rcon = new GameRcon(
                () => settings().ServerRemote ? (settings().ServerRemoteHost ?? "").Trim() : RconSetup.Address,
                () => settings().ServerRemote ? settings().ServerRemoteRconPort : LocalRcon().Port,
                () => settings().ServerRemote ? settings().ServerRemoteRconPassword ?? "" : LocalRcon().Password ?? "");
        }

        /// <summary>The RCON of the server on this PC: the launcher's, or the player's own when they start it with their own options.</summary>
        private (int Port, string Password, bool Own) LocalRcon()
        {
            var s = settings();
            if (!s.ServerUseOwnArgs) return (s.ServerRconPort, s.ServerRconPassword, false);
            string ini = "";
            try { var path = install().GameIniPath; if (path != null && File.Exists(path)) ini = UeIni.ReadText(path); }
            catch (Exception ex) { AppLog.Warn("Server Game.ini for RCON: " + ex.Message); }
            return ServerPlanner.RconFor(s, ini);
        }

        /// <summary>Watches the server of the current install (again after the folder changes).</summary>
        public GameMonitor Watch()
        {
            Monitor?.Dispose();
            var inst = install();
            Monitor = inst.Found ? new GameMonitor(ServerInstall.ProcessName, () => install().LogPath, inst.Root) : null;
            return Monitor;
        }

        public bool IsRunning
        {
            get { using (var p = install().FindProcess()) return p != null; }
        }

        /// <summary>
        /// Writes the server's files for a plan: Game.ini (backed up), Admins.txt and the map cycle. Mods.txt is left alone:
        /// since game update 1.20 the server loads the mods its mod.io account is subscribed to, and no longer reads it.
        /// </summary>
        public void WriteFiles(ServerPlan plan)
        {
            var inst = install();
            Directory.CreateDirectory(inst.SavedConfigDir);
            string current = File.Exists(inst.GameIniPath) ? UeIni.ReadText(inst.GameIniPath) : "";
            if (Normalize(current) != Normalize(plan.GameIni))
            {
                if (File.Exists(inst.GameIniPath)) ConsoleBridge.BackupFile(inst.GameIniPath, "server-");
                UeIni.WriteText(inst.GameIniPath, plan.GameIni);
                AppLog.Info("Server Game.ini updated (" + UeIni.Split(plan.GameIni).Count + " lines)");
            }
            settings().ServerManagedIniKeys = plan.ManagedIniKeys;
            settings().ServerStartedMutators = new List<string>(plan.StartMutators);
            if (plan.VoteKickOurs.HasValue) settings().ServerVoteKickOurs = plan.VoteKickOurs;
            if (plan.ModsOn) ServerModio.RemoveObsoleteToken(inst);
            Directory.CreateDirectory(inst.ServerConfigDir);
            if (plan.AdminsText != null) WriteIfChanged(Path.Combine(inst.ServerConfigDir, ServerPlanner.AdminsName + ".txt"), plan.AdminsText);
            if (plan.MapCycleCopyFrom != null)
                WriteIfChanged(Path.Combine(inst.ServerConfigDir, plan.MapCycleName + ".txt"), File.ReadAllText(plan.MapCycleCopyFrom));
        }

        /// <summary>What the running server's own log says about its mods.</summary>
        public ServerModStatus ModStatus(bool modsOn, bool justStarted, ServerModioAccount account)
        {
            var inst = install();
            var lines = new List<string>();
            try
            {
                if (inst.LogPath != null && File.Exists(inst.LogPath))
                    using (var fs = new FileStream(inst.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs))
                    {
                        string l;
                        while ((l = r.ReadLine()) != null) if (l.IndexOf("Mod", StringComparison.Ordinal) >= 0 || l.StartsWith("Log file open", StringComparison.Ordinal)) lines.Add(l);
                    }
            }
            catch (Exception ex) { AppLog.Warn("Server log for the mod check: " + ex.Message); }
            return ServerModCheck.Parse(lines, modsOn, justStarted, account);
        }

        private static void WriteIfChanged(string path, string text)
        {
            if (File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(text)) return;
            if (File.Exists(path)) ConsoleBridge.BackupFile(path, "server-");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            AppLog.Info("Server file written: " + Path.GetFileName(path));
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        /// <summary>Why the ports cannot be used (another program has them), or null.</summary>
        public string PortProblem(ServerPlan plan)
        {
            var s = settings();
            int game = plan.GamePort > 0 ? plan.GamePort : s.ServerPort, query = plan.QueryPort > 0 ? plan.QueryPort : s.ServerQueryPort, rcon = plan.RconPort > 0 ? plan.RconPort : s.ServerRconPort;
            foreach (var (port, what) in new[] { (game, N("The game port {0} is used by another program (another server?). Pick another one.")), (query, N("The query port {0} is used by another program (another server?). Pick another one.")) })
                if (!UdpFree(port)) return F(what, port);
            if (!TcpFree(rcon, s.ServerRconFromNetwork)) return F("The RCON port {0} is used by another program. Pick another one.", rcon);
            return null;
        }

        private static bool UdpFree(int port)
        {
            try { using (var u = new UdpClient(new IPEndPoint(IPAddress.Any, port))) return true; }
            catch { return false; }
        }

        private static bool TcpFree(int port, bool network)
        {
            try
            {
                var l = new TcpListener(network ? IPAddress.Any : IPAddress.Loopback, port);
                l.Start();
                l.Stop();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Writes the files and starts the server, then waits until it has loaded its first map and answers on RCON.</summary>
        public async Task<string> Start(ServerPlan plan, IProgress<string> progress, CancellationToken ct)
        {
            var inst = install();
            if (!plan.IsValid) return plan.Error;
            if (IsRunning) return T("The server is already running. Stop it first, or use Restart.");
            string ports = PortProblem(plan);
            if (ports != null) return ports;
            progress?.Report(T("Writing the server's settings"));
            try { WriteFiles(plan); }
            catch (Exception ex) { return F("The server's files could not be written: {0}", ex.Message); }

            string exe = File.Exists(inst.StartExe) ? inst.StartExe : inst.ShippingExe;
            AppLog.Info("Starting the server: " + plan.ShownCommandLine);
            progress?.Report(T("Starting the server"));
            try
            {
                Process.Start(new ProcessStartInfo(exe, plan.CommandLine)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    // Its own log window comes from -log; the small start program never needs one.
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex) { return F("The server did not start: {0}", ex.Message); }
            if (plan.UsesCode)
            {
                // A security code logs the server in once: it is used up by this start.
                settings().ServerModioCode = "";
                AppLog.Info("Server: started with a mod.io security code");
            }

            var sw = Stopwatch.StartNew();
            TimeSpan? loadedAt = null;
            var limit = TimeSpan.FromSeconds(Math.Max(120, settings().StartTimeoutSec));
            while (sw.Elapsed < limit)
            {
                await Task.Delay(1000, ct);
                Monitor?.Poll();
                bool running = IsRunning;
                if (!running && sw.Elapsed > TimeSpan.FromSeconds(20)) return F("The server closed while starting. Its log is in {0}.", Path.GetDirectoryName(inst.LogPath));
                bool loaded = Monitor != null && Monitor.Phase == GamePhase.InMatch;
                if (loaded && await Task.Run(() => Rcon.Probe(), ct) == null)
                {
                    AppLog.Info("Server running on port " + (Monitor.ListeningPort > 0 ? Monitor.ListeningPort : plan.GamePort) + " after " + (int)sw.Elapsed.TotalSeconds + " s");
                    return null;
                }
                if (loaded && plan.OwnArgs != null)
                {
                    // The player's own options may keep RCON off or elsewhere: the server runs, only the Players card cannot reach it.
                    loadedAt = loadedAt ?? sw.Elapsed;
                    if (sw.Elapsed - loadedAt.Value > TimeSpan.FromSeconds(30)) { AppLog.Warn("Server running, but its RCON does not answer"); return null; }
                }
                progress?.Report(running ? F("Loading {0} ({1} s)", plan.StartMap ?? T("the first map"), (int)sw.Elapsed.TotalSeconds) : T("Starting the server"));
            }
            return F("The server did not finish starting in {0} s. Look at its log window.", (int)limit.TotalSeconds);
        }

        /// <summary>Asks the server to close (over RCON, then its window). False when it is still running.</summary>
        public async Task<bool> Stop(CancellationToken ct)
        {
            using (var p = install().FindProcess())
            {
                if (p == null) return true;
                if (await Task.Run(() => Rcon.Exit(), ct))
                    for (int i = 0; i < 40 && !p.HasExited; i++) await Task.Delay(500, ct);
                if (!p.HasExited)
                {
                    try { p.CloseMainWindow(); } catch { }
                    for (int i = 0; i < 40 && !p.HasExited; i++) await Task.Delay(500, ct);
                }
                Monitor?.Poll();
                return p.HasExited;
            }
        }

        /// <summary>Ends the server process at once (when it does not close by itself).</summary>
        public bool Kill()
        {
            using (var p = install().FindProcess())
            {
                if (p == null) return true;
                try { p.Kill(); p.WaitForExit(10000); } catch (Exception ex) { AppLog.Warn("Server kill: " + ex.Message); }
                return p.HasExited;
            }
        }

        // ------------------------------------------------------------------ control (RCON)

        /// <summary>Players on the server (from listplayers).</summary>
        public List<ServerPlayer> Players() => ParsePlayers(Rcon.Run("listplayers")[0]);

        public static List<ServerPlayer> ParsePlayers(string reply)
        {
            var list = new List<ServerPlayer>();
            foreach (var raw in (reply ?? "").Replace("\r", "").Split('\n'))
            {
                if (!raw.Contains("|") || raw.TrimStart().StartsWith("=", StringComparison.Ordinal)) continue;
                var cells = raw.Split('|').Select(c => c.Trim()).ToList();
                if (cells.Count > 0 && cells[cells.Count - 1].Length == 0) cells.RemoveAt(cells.Count - 1);   // the line ends with " |"
                if (cells.Count < 5 || cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;
                // The id is first and net id, address and score are last: a | in a player's name stays in the name.
                int n = cells.Count;
                list.Add(new ServerPlayer { Id = cells[0], Name = string.Join("|", cells.Skip(1).Take(n - 4)), NetId = cells[n - 3], Ip = cells[n - 2], Score = cells[n - 1] });
            }
            return list;
        }

        /// <summary>A chat line or reason as the server takes it: one line, no quotes, and no | (the game would run what follows it as a second command).</summary>
        public static string Reason(string text) => (text ?? "").Replace("\"", "'").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();

        public string Kick(ServerPlayer p, string reason) => Rcon.Run(("kick " + p.Id + " " + Reason(reason)).Trim())[0].Trim();

        public string Ban(ServerPlayer p, int minutes, string reason) =>
            Rcon.Run(("ban " + p.Id + " " + (minutes > 0 ? minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0") + " " + Reason(reason)).Trim())[0].Trim();

        public string PermBan(ServerPlayer p, string reason) => Rcon.Run(("permban " + p.Id + " " + Reason(reason)).Trim())[0].Trim();

        public string Say(string message) => Rcon.Run("say " + Reason(message))[0].Trim();

        /// <summary>Loads a match on the running server (RCON's travel, with every option of the map before reset).</summary>
        public RconReply Travel(ServerPlan plan) => Rcon.Travel(TravelUrl(plan, RunningMutators(plan)));

        /// <summary>Mutators the running server was started with by the launcher (none known for own options or a server elsewhere).</summary>
        public List<string> RunningMutators(ServerPlan plan) => plan.OwnArgs != null || settings().ServerRemote ? new List<string>() : settings().ServerStartedMutators ?? new List<string>();

        /// <summary>
        /// The URL a map load on the running server sends: the match, the mutators it needs on top of the ones the server
        /// was started with (those stay; naming one again loads it twice), and resets of what the last map left behind.
        /// <paramref name="running"/> = the mutators the server was started with (null = the plan's own).
        /// </summary>
        public static string TravelUrl(ServerPlan plan, IEnumerable<string> running = null)
        {
            string url = plan.MatchUrl ?? plan.Url;
            var started = new HashSet<string>(running ?? plan.StartMutators, StringComparer.OrdinalIgnoreCase);
            var more = plan.Match.Mutators.Where(m => !started.Contains(m)).ToList();
            if (more.Count > 0 && url.IndexOf("?Mutators=", StringComparison.OrdinalIgnoreCase) < 0) url += "?Mutators=" + string.Join(",", more);
            return url + LaunchService.TravelResets(plan.Match, url, !plan.OwnRules);
        }

        /// <summary>Mutators the running server keeps although the match does not have them (only a restart takes them off).</summary>
        public static List<string> StuckMutators(ServerPlan plan, IEnumerable<string> running) =>
            (running ?? Enumerable.Empty<string>()).Where(m => plan.Match != null && !plan.Match.Mutators.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
