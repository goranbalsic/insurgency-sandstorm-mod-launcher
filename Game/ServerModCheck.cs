using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    public enum ModVerdict
    {
        /// <summary>Mods are switched off for the server (or it is not running).</summary>
        NotAsked,
        /// <summary>Started a moment ago: mod.io has not been heard from yet.</summary>
        Waiting,
        /// <summary>The server mounted mods.</summary>
        Loaded,
        /// <summary>The server is not logged in to mod.io: its mod list stays empty.</summary>
        NoLogin,
        /// <summary>mod.io refused the login (a used or wrong security code, or the terms not accepted).</summary>
        LoginFailed,
        /// <summary>The log has nothing about mods at all.</summary>
        Unknown,
        /// <summary>The server took mods from its subscriptions and is downloading them.</summary>
        Downloading,
        /// <summary>Logged in, but no mods came: the account has no subscriptions (or is the game's own account).</summary>
        NoMods,
    }

    /// <summary>What the dedicated server's own log says about its mods (see <see cref="ServerModCheck.Parse"/>).</summary>
    public sealed class ServerModStatus
    {
        public bool Initialized, LoggedIn, ManagerEnabled, ListPublishedEmpty;
        public string LoginError;
        public List<string> Added = new List<string>();
        public int Mounted;
        public ModVerdict Verdict;
        /// <summary>The server's saved mod.io login (null = not looked at).</summary>
        public ServerModioAccount Account;

        /// <summary>A plain sentence for the player.</summary>
        public string Text
        {
            get
            {
                switch (Verdict)
                {
                    case ModVerdict.Loaded: return Mounted == 1 ? T("The server loaded 1 mod.") : F("The server loaded {0} mods.", Mounted);
                    case ModVerdict.Downloading: return Added.Count == 1 ? T("The server is downloading 1 mod...") : F("The server is downloading {0} mods...", Added.Count);
                    case ModVerdict.LoginFailed: return F("mod.io did not log the server in ({0}). Send a new security code (Mods card) and start the server with it.", LoginError);
                    case ModVerdict.NoLogin:
                        return Account?.Expired == true
                            ? T("The server started without mods: its mod.io login has expired. Send a new security code (Mods card) and start the server with it.")
                            : T("The server started without mods: it is not logged in to mod.io. Send a security code to its mod.io account (Mods card) and start the server with it.");
                    case ModVerdict.NoMods:
                        if (Account?.SameAsGame == true) return T("The server started without mods: it is logged in with your game's mod.io account, and mod.io gives it nothing that way. Log it in with an account of its own.");
                        return Account != null && Account.Subscriptions.Count == 0
                            ? T("The server started without mods: its mod.io account is not subscribed to any. Subscribe it to the mods it should load (Mods card).")
                            : T("The server started without mods although it is logged in to mod.io. Check its account's subscriptions on mod.io.");
                    case ModVerdict.Waiting: return T("Waiting for the server to report its mods...");
                    case ModVerdict.Unknown: return T("The server's log says nothing about mods yet.");
                    default: return T("Mods are off for the server.");
                }
            }
        }
    }

    /// <summary>
    /// Reads the mod lines of a dedicated server's log (Insurgency.log): the messages the game's mod system writes when it logs
    /// in to mod.io, adds mods and mounts them, and the warning the server writes when the mod list it publishes is empty.
    /// Only the lines of the run in the file count (from its last "Log file open").
    /// </summary>
    public static class ServerModCheck
    {
        private static readonly Regex Added = new Regex(@"ModSubsystem: Mod added, id: (?<id>\d+)", RegexOptions.Compiled);
        private static readonly Regex Failed = new Regex(@"ModSubsystem: User authentication failed(?: \(Terms of Use\))?(?: with error: (?<code>-?\d+), message)?[: ]*(?<msg>.*)$", RegexOptions.Compiled);
        private static readonly Regex Mount = new Regex(@"INSModioGame: MountMod: \S+ in |ModSubsystem: OnModActivatedFromCloud: Mod .* is mounted and up-to-date", RegexOptions.Compiled);
        /// <summary>mod.io's own answer to a refused login (a wrong or used security code) in the SDK's log line; no ModSubsystem line comes with it.</summary>
        private static readonly Regex HttpRefused = new Regex(@"LogModio: .*Non 200-204 response received: .*""code"":401.*""message"":""(?<msg>[^""]*)""", RegexOptions.Compiled);

        public static ServerModStatus Parse(IEnumerable<string> lines, bool modsOn, bool justStarted = false, ServerModioAccount account = null)
        {
            var st = new ServerModStatus { Account = account };
            var all = (lines ?? Enumerable.Empty<string>()).ToList();
            int from = all.FindLastIndex(l => l.StartsWith("Log file open", StringComparison.Ordinal));
            foreach (var l in from >= 0 ? all.Skip(from) : all)
            {
                if (l.IndexOf("ModSubsystem: mod.io initialization complete", StringComparison.Ordinal) >= 0) st.Initialized = true;
                else if (l.IndexOf("ModSubsystem: User authentication successful", StringComparison.Ordinal) >= 0) { st.LoggedIn = true; st.LoginError = null; }
                else if (l.IndexOf("ModSubsystem: ModManager enabled", StringComparison.Ordinal) >= 0) st.ManagerEnabled = true;
                else if (l.IndexOf("Empty session setting ModList", StringComparison.Ordinal) >= 0) st.ListPublishedEmpty = true;
                else
                {
                    var f = Failed.Match(l);
                    if (f.Success)
                    {
                        string code = f.Groups["code"].Value, msg = f.Groups["msg"].Value.Trim();
                        st.LoginError = (code.Length > 0 ? "error " + code : "") + (msg.Length > 0 ? (code.Length > 0 ? ": " : "") + msg : "");
                        if (st.LoginError.Length == 0) st.LoginError = T("no reason given");
                        st.LoggedIn = false;
                        continue;
                    }
                    var h = HttpRefused.Match(l);
                    if (h.Success && !st.LoggedIn) { st.LoginError = h.Groups["msg"].Value.Trim().Length > 0 ? h.Groups["msg"].Value.Trim() : "HTTP 401"; continue; }
                    var a = Added.Match(l);
                    if (a.Success) { if (!st.Added.Contains(a.Groups["id"].Value)) st.Added.Add(a.Groups["id"].Value); }
                    else if (Mount.IsMatch(l)) st.Mounted++;
                }
            }
            bool loggedIn = st.LoggedIn || account?.LoggedIn == true;
            if (!modsOn) st.Verdict = ModVerdict.NotAsked;
            // Mods that mounted are what counts (an old code passed again is refused, but the saved login still works).
            else if (st.Mounted > 0) st.Verdict = ModVerdict.Loaded;
            else if (st.LoginError != null) st.Verdict = ModVerdict.LoginFailed;
            else if (st.Added.Count > 0) st.Verdict = ModVerdict.Downloading;
            else if (justStarted) st.Verdict = ModVerdict.Waiting;
            else if (st.ListPublishedEmpty) st.Verdict = loggedIn ? ModVerdict.NoMods : ModVerdict.NoLogin;
            else if (st.Initialized || all.Count > 0) st.Verdict = ModVerdict.Unknown;
            else st.Verdict = ModVerdict.Waiting;
            return st;
        }
    }
}
