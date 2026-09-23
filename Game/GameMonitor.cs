using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SandstormModLauncher.Core;

namespace SandstormModLauncher.Game
{
    public enum GamePhase { NotRunning, Starting, Menu, Loading, InMatch }

    /// <summary>
    /// Watches the game process and tails Insurgency.log. Lines from the online services
    /// (tokens, account ids) are dropped before anything else sees them.
    /// </summary>
    public sealed class GameMonitor : IDisposable
    {
        private readonly object sync = new object();
        private readonly Timer timer;
        private long position;
        private DateTime logCreated;
        private string partial = "";
        private bool firstPoll = true;
        private bool mapLoaded;
        private readonly List<(Func<string, bool> Match, TaskCompletionSource<string> Tcs)> waiters = new List<(Func<string, bool>, TaskCompletionSource<string>)>();
        private static readonly Regex Sensitive = new Regex(@"LogPros|LogVOIP|VivoxCore|LogOnline|token|signature|Steam(ID|NWI)|userIdentity|payload|LogEOS|LogHydra|LogAnalytics", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Transition = new Regex(@"State transition: '(\w+)' -> '(\w+)'", RegexOptions.Compiled);
        private static readonly Regex LoadMap = new Regex(@"LogLoad: LoadMap: (\S+)", RegexOptions.Compiled);
        private static readonly Regex LoadDone = new Regex(@"Took [\d\.]+ seconds to LoadMap\(([^\)]+)\)", RegexOptions.Compiled);
        private static readonly Regex MatchState = new Regex(@"LogGameMode: Display: State: (\w+) -> (\w+)", RegexOptions.Compiled);

        public event Action StateChanged;
        public event Action<string> LineReceived;

        public GamePhase Phase { get; private set; } = GamePhase.NotRunning;
        public string CurrentLevel { get; private set; }
        public string CurrentUrl { get; private set; }
        public string RoundState { get; private set; }
        public int ModsMounted { get; private set; }
        public int ProcessId { get; private set; }
        public DateTime? ProcessStartUtc { get; private set; }
        public IntPtr Window { get; private set; }
        public bool IsRunning => ProcessId != 0;
        public bool AtMenu => Phase == GamePhase.Menu;

        public GameMonitor()
        {
            timer = new Timer(_ => Poll(), null, 0, 400);
        }

        public void Dispose() => timer.Dispose();

        public void Poll()
        {
            if (!Monitor.TryEnter(sync)) return;
            bool changed = false;
            try
            {
                changed |= CheckProcess();
                changed |= TailLog();
                // An old log (e.g. after a crash) must never make a closed game look ready.
                if (ProcessId == 0 && Phase != GamePhase.NotRunning) { Phase = GamePhase.NotRunning; CurrentLevel = null; RoundState = null; changed = true; }
                firstPoll = false;
            }
            catch (Exception ex) { AppLog.Warn("Monitor: " + ex.Message); }
            finally { Monitor.Exit(sync); }
            if (changed) { try { StateChanged?.Invoke(); } catch { } }
        }

        private bool CheckProcess()
        {
            Process p = null;
            try { p = Process.GetProcessesByName(GameInstall.ClientProcess).FirstOrDefault(); } catch { }
            if (p == null)
            {
                if (ProcessId == 0) return false;
                ProcessId = 0; ProcessStartUtc = null; Window = IntPtr.Zero;
                Phase = GamePhase.NotRunning; CurrentLevel = null; RoundState = null; mapLoaded = false;
                return true;
            }
            bool changed = false;
            if (p.Id != ProcessId)
            {
                ProcessId = p.Id;
                try { ProcessStartUtc = p.StartTime.ToUniversalTime(); } catch { ProcessStartUtc = DateTime.UtcNow; }
                // A game that starts while we watch begins at "starting", whatever the previous log said.
                // One that was already running when the launcher opened gets its phase from the log instead.
                if (!firstPoll || Phase == GamePhase.NotRunning) { Phase = GamePhase.Starting; CurrentLevel = null; RoundState = null; ModsMounted = 0; mapLoaded = false; }
                changed = true;
            }
            IntPtr w = IntPtr.Zero;
            try { w = p.MainWindowHandle; } catch { }
            if (w == IntPtr.Zero) w = FindWindow(p.Id);
            if (w != Window) { Window = w; changed = true; }
            return changed;
        }

        private static IntPtr FindWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                Native.GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == pid && Native.IsWindowVisible(h))
                {
                    var sb = new StringBuilder(64);
                    Native.GetClassName(h, sb, 64);
                    if (sb.ToString().IndexOf("Unreal", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private bool TailLog()
        {
            string path = GameInstall.LogPath;
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            bool changed = false;
            if (fi.CreationTimeUtc != logCreated || fi.Length < position)
            {
                logCreated = fi.CreationTimeUtc;
                position = 0;
                partial = "";
                ModsMounted = 0;
                if (ProcessId == 0) Phase = GamePhase.NotRunning;
                changed = true;
            }
            if (fi.Length == position) return changed;
            string chunk;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Position = position;
                var buf = new byte[Math.Min(fi.Length - position, 8 * 1024 * 1024)];
                int read = fs.Read(buf, 0, buf.Length);
                position += read;
                chunk = Encoding.UTF8.GetString(buf, 0, read);
            }
            string text = partial + chunk;
            int last = text.LastIndexOf('\n');
            if (last < 0) { partial = text; return changed; }
            partial = text.Substring(last + 1);
            foreach (var raw in text.Substring(0, last).Split('\n'))
            {
                string line = raw.TrimEnd('\r').TrimStart('﻿');
                if (line.Length == 0 || Sensitive.IsMatch(line)) continue;
                changed |= Parse(line);
                try { LineReceived?.Invoke(line); } catch { }
                CheckWaiters(line);
            }
            return changed;
        }

        private bool Parse(string line)
        {
            if (line.StartsWith("Log file open")) { ModsMounted = 0; mapLoaded = false; Phase = ProcessId != 0 ? GamePhase.Starting : GamePhase.NotRunning; return true; }
            // The game logs "-> Playing" just after a map finishes loading, so it only means
            // "loading" while the map is still on its way.
            var t = Transition.Match(line);
            if (t.Success)
            {
                switch (t.Groups[2].Value)
                {
                    case "MainMenu": Phase = GamePhase.Menu; mapLoaded = false; CurrentLevel = null; RoundState = null; break;
                    case "WelcomeScreen": Phase = GamePhase.Starting; break;
                    case "MatchTransition": Phase = GamePhase.Loading; break;
                    case "Playing": Phase = mapLoaded ? GamePhase.InMatch : GamePhase.Loading; break;
                }
                return true;
            }
            var lm = LoadMap.Match(line);
            if (lm.Success)
            {
                CurrentUrl = lm.Groups[1].Value;
                if (CurrentUrl.IndexOf("/Utility/", StringComparison.OrdinalIgnoreCase) < 0) { Phase = GamePhase.Loading; mapLoaded = false; RoundState = null; }
                return true;
            }
            var ld = LoadDone.Match(line);
            if (ld.Success)
            {
                string level = ld.Groups[1].Value;
                if (level.IndexOf("/Utility/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (Phase == GamePhase.Loading && CurrentLevel != null) Phase = GamePhase.Menu;
                    CurrentLevel = null;
                    mapLoaded = false;
                }
                else { CurrentLevel = level; mapLoaded = true; Phase = GamePhase.InMatch; }
                return true;
            }
            var ms = MatchState.Match(line);
            if (ms.Success)
            {
                RoundState = ms.Groups[2].Value;
                if (mapLoaded && Phase == GamePhase.Loading) Phase = GamePhase.InMatch;
                return true;
            }
            if (line.Contains("LogINSModioGame: MountPak:") && line.Contains("was successful")) { ModsMounted++; return true; }
            if (line.Contains("LogExit: Exiting") || line.Contains("LogExit: Game engine shut down")) { Phase = GamePhase.NotRunning; return true; }
            return false;
        }

        private void CheckWaiters(string line)
        {
            lock (waiters)
            {
                for (int i = waiters.Count - 1; i >= 0; i--)
                {
                    bool hit;
                    try { hit = waiters[i].Match(line); } catch { hit = false; }
                    if (!hit) continue;
                    waiters[i].Tcs.TrySetResult(line);
                    waiters.RemoveAt(i);
                }
            }
        }

        /// <summary>Waits for a future log line matching the predicate. Returns null on timeout.</summary>
        public async Task<string> WaitForLine(Func<string, bool> match, TimeSpan timeout, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waiters) waiters.Add((match, tcs));
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
                lock (waiters) waiters.RemoveAll(w => w.Tcs == tcs);
                if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }

        /// <summary>Collects every non-sensitive log line written while the action runs plus a settle time.</summary>
        public async Task<List<string>> Capture(Func<Task> action, TimeSpan settle, CancellationToken ct)
        {
            var lines = new List<string>();
            void Handler(string l) { lock (lines) lines.Add(l); }
            LineReceived += Handler;
            try
            {
                await action().ConfigureAwait(false);
                await Task.Delay(settle, ct).ConfigureAwait(false);
                Poll();
            }
            finally { LineReceived -= Handler; }
            lock (lines) return new List<string>(lines);
        }

        public string Describe()
        {
            switch (Phase)
            {
                case GamePhase.NotRunning: return "Game not running";
                case GamePhase.Starting: return "Game starting...";
                case GamePhase.Menu: return ModsMounted > 0 ? $"At main menu · {ModsMounted} mods mounted" : "At main menu";
                case GamePhase.Loading: return "Loading a map...";
                case GamePhase.InMatch:
                    string lvl = CurrentLevel == null ? "" : CurrentLevel.Substring(CurrentLevel.LastIndexOf('/') + 1);
                    return "In match" + (lvl.Length > 0 ? " · " + lvl : "") + (RoundState != null ? " · " + PrettyRound(RoundState) : "");
            }
            return "";
        }

        private static string PrettyRound(string s)
        {
            switch (s)
            {
                case "WaitingToStart": return "choosing class";
                case "PreRound": return "pre-round";
                case "RoundActive": return "round active";
                case "PostRound": return "round over";
                case "GameOver": return "match over";
                default: return UnrealText.Humanize(s).ToLowerInvariant();
            }
        }
    }
}
