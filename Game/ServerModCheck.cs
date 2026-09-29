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
        /// <summary>The server never logged in to mod.io: its mod list stays empty.</summary>
        NoLogin,
        /// <summary>mod.io refused the login (a bad token, or the terms not accepted).</summary>
        LoginFailed,
        /// <summary>The log has nothing about mods at all.</summary>
        Unknown,
    }

    /// <summary>What the dedicated server's own log says about its mods (see <see cref="ServerModCheck.Parse"/>).</summary>
    public sealed class ServerModStatus
    {
        public bool Initialized, LoggedIn, ManagerEnabled, ListPublishedEmpty;
        public string LoginError;
        public List<string> Added = new List<string>();
        public int Mounted;
        public ModVerdict Verdict;

        /// <summary>A plain sentence for the player.</summary>
        public string Text
        {
            get
            {
                switch (Verdict)
                {
                    case ModVerdict.Loaded: return Mounted == 1 ? T("The server loaded 1 mod.") : F("The server loaded {0} mods.", Mounted);
                    case ModVerdict.LoginFailed: return F("mod.io refused the server's login ({0}). Check the token: it must be a read access token from your own mod.io account.", LoginError);
                    case ModVerdict.NoLogin:
                        return T("The server started, but it did not load any mods: it never logged in to mod.io, so its mod list is empty. Check that the token is saved, that the mod ids are on Mods.txt, and that Mods is switched on. Players cannot join a modded match until this works.");
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
        private static readonly Regex Mount = new Regex(@"INSModioGame: MountMod: \S+ in ", RegexOptions.Compiled);

        public static ServerModStatus Parse(IEnumerable<string> lines, bool modsOn, bool justStarted = false)
        {
            var st = new ServerModStatus();
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
                    var a = Added.Match(l);
                    if (a.Success) { if (!st.Added.Contains(a.Groups["id"].Value)) st.Added.Add(a.Groups["id"].Value); }
                    else if (Mount.IsMatch(l)) st.Mounted++;
                }
            }
            if (!modsOn) st.Verdict = ModVerdict.NotAsked;
            else if (st.LoginError != null) st.Verdict = ModVerdict.LoginFailed;
            else if (st.Mounted > 0) st.Verdict = ModVerdict.Loaded;
            else if (st.ListPublishedEmpty && !st.LoggedIn) st.Verdict = justStarted ? ModVerdict.Waiting : ModVerdict.NoLogin;
            else if (st.Initialized || all.Count > 0) st.Verdict = justStarted ? ModVerdict.Waiting : ModVerdict.Unknown;
            else st.Verdict = ModVerdict.Waiting;
            return st;
        }
    }
}
