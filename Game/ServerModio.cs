using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Game
{
    /// <summary>The dedicated server's own mod.io login, as the game keeps it on this PC (never the token itself).</summary>
    public sealed class ServerModioAccount
    {
        /// <summary>A login that has not expired.</summary>
        public bool LoggedIn;
        public bool Expired;
        public string UserName;
        public long UserId;
        public DateTime? ExpiresUtc;
        /// <summary>The mods the account is subscribed to, as the server saw them at its last start.</summary>
        public List<long> Subscriptions = new List<long>();
        /// <summary>The game on this PC is logged in with the same account (mod.io then gives the server nothing).</summary>
        public bool SameAsGame;
        public string Problem;
    }

    /// <summary>
    /// Mods on the dedicated server since game update 1.20: the server logs in to mod.io as a user of its own, once, with a
    /// security code mod.io e-mails to that account (-SecurityCode=12345; later starts use -SecurityCode=none), and loads the
    /// mods that account is subscribed to. Mods.txt, -ModList and the access token in the ini files are no longer read.
    /// The game keeps the login in %LOCALAPPDATA%\mod.io\254\ModServer\user.json of the Windows user that runs the server.
    /// Network access happens only from the buttons that call mod.io (send a code, subscribe).
    /// </summary>
    public static class ServerModio
    {
        public const string GameId = "254";
        /// <summary>The game's public mod.io API key, the one the official server guide's authorization script uses.</summary>
        public const string ApiKey = "bbf3af200848aef28418c032a601e7a2";
        public const string ApiRoot = "https://g-254.modapi.io/v1";
        public const string ProfileName = "ModServer";
        /// <summary>Where the token went before update 1.20; the guides say to take it out.</summary>
        public const string ObsoleteSection = "/Script/ModKit.ModIOClient";
        public const string SubscriptionsUrl = "https://mod.io/g/insurgencysandstorm/library";

        private static readonly Regex Email = new Regex(@"^[^@\s""]+@[^@\s""]+\.[^@\s""]+$", RegexOptions.Compiled);
        private static readonly Regex Code = new Regex(@"^\d{5}$", RegexOptions.Compiled);

        public static string UserDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mod.io", GameId);

        public static bool IsEmail(string text) => Email.IsMatch((text ?? "").Trim());

        /// <summary>A security code as mod.io sends it (5 digits), or null.</summary>
        public static string CleanCode(string text)
        {
            string t = Regex.Replace(text ?? "", @"\s", "");
            return Code.IsMatch(t) ? t : null;
        }

        /// <summary>The server's login from its user.json under <paramref name="root"/> (default: this Windows user's mod.io folder).</summary>
        public static ServerModioAccount ReadAccount(string root = null, DateTime? nowUtc = null)
        {
            root = root ?? UserDataDir;
            var acc = new ServerModioAccount();
            string file = Path.Combine(root, ProfileName, "user.json");
            try
            {
                if (!File.Exists(file)) return acc;
                var d = Json.Parse(ReadShared(file));
                acc.UserId = d.Path("Profile", "id").Long();
                acc.UserName = d.Path("Profile", "username").Str();
                foreach (var s in d.Get("subscriptions").Arr())
                {
                    long id = s.Long();
                    if (id > 0 && !acc.Subscriptions.Contains(id)) acc.Subscriptions.Add(id);
                }
                if (!string.IsNullOrEmpty(d.Path("OAuth", "token").Str()))
                {
                    long expiry = d.Path("OAuth", "expiry").Long();
                    if (expiry > 0) acc.ExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiry);
                    acc.Expired = acc.ExpiresUtc.HasValue && acc.ExpiresUtc.Value <= (nowUtc ?? DateTime.UtcNow);
                    acc.LoggedIn = !acc.Expired;
                }
                if (acc.UserId > 0)
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        if (Path.GetFileName(dir).Equals(ProfileName, StringComparison.OrdinalIgnoreCase)) continue;
                        string other = Path.Combine(dir, "user.json");
                        try { if (File.Exists(other) && Json.Parse(ReadShared(other)).Path("Profile", "id").Long() == acc.UserId) acc.SameAsGame = true; }
                        catch { /* another profile that cannot be read says nothing about this one */ }
                    }
            }
            catch (Exception ex) { acc.Problem = ex.Message; }
            return acc;
        }

        /// <summary>The server account's access token, for the subscription calls. Never logged or shown.</summary>
        private static string Token(string root = null)
        {
            string file = Path.Combine(root ?? UserDataDir, ProfileName, "user.json");
            return File.Exists(file) ? Json.Parse(ReadShared(file)).Path("OAuth", "token").Str() : null;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
        }

        // ------------------------------------------------------------------ the command line

        private static readonly Regex CodeArg = new Regex(@"(?<=^|\s)-SecurityCode=(""[^""]*""|\S*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The command line with -SecurityCode set to <paramref name="code"/> (replaced where it already is).</summary>
        public static string WithSecurityCode(string args, string code)
        {
            args = (args ?? "").Trim();
            if (CodeArg.IsMatch(args)) return CodeArg.Replace(args, "-SecurityCode=" + code, 1);
            return (args + " -SecurityCode=" + code).Trim();
        }

        /// <summary>The -SecurityCode value on a command line (null = none given).</summary>
        public static string SecurityCodeIn(string args)
        {
            var m = CodeArg.Match(args ?? "");
            return m.Success ? m.Groups[1].Value.Trim('"') : null;
        }

        /// <summary>A command line to show or log: a real security code hidden (it can log the server in until it is used).</summary>
        public static string HideCode(string args) =>
            Regex.Replace(args ?? "", @"(?<=^|\s)(-SecurityCode=)(?!none(\s|$))\S+", "$1<code>", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------------ files from before update 1.20

        /// <summary>
        /// Takes the old token section out of the server's Engine.ini and GameUserSettings.ini (a copy of each is kept first):
        /// the server no longer reads it, and the guides for update 1.20 say to remove it. Returns the files changed.
        /// </summary>
        public static List<string> RemoveObsoleteToken(ServerInstall inst)
        {
            var changed = new List<string>();
            if (inst?.Found != true) return changed;
            foreach (string path in new[] { inst.EngineIniPath, inst.GameUserSettingsPath })
            {
                if (!File.Exists(path)) continue;
                string text = UeIni.ReadText(path);
                if (!UeIni.Parse(text).Any(s => s.Name.Equals(ObsoleteSection, StringComparison.OrdinalIgnoreCase))) continue;
                ConsoleBridge.BackupFile(path, "server-");
                UeIni.WriteText(path, UeIni.MergeSections(text, new UeIni.Section[0], (sec, key) => sec.Equals(ObsoleteSection, StringComparison.OrdinalIgnoreCase)));
                changed.Add(Path.GetFileName(path));
            }
            if (changed.Count > 0) AppLog.Info("Server: the old mod.io token section was taken out of " + string.Join(", ", changed));
            return changed;
        }

        /// <summary>Mod ids in a Mods.txt (one per line, // comments), null when the file is not there.</summary>
        public static List<long> ReadModsTxt(string path, List<string> bad = null) =>
            path != null && File.Exists(path) ? ServerPlanner.ModIds(File.ReadAllText(path), bad) : null;

        // ------------------------------------------------------------------ mod.io (only from the buttons)

        /// <summary>Asks mod.io to e-mail a security code to the server account's address. Null when sent, else why not.</summary>
        public static string RequestCode(string email)
        {
            if (!IsEmail(email)) return T("Enter the e-mail address of the server's mod.io account.");
            var (status, body) = Call("POST", ApiRoot + "/oauth/emailrequest?api_key=" + ApiKey, "email=" + Uri.EscapeDataString(email.Trim()), null);
            if (status >= 200 && status < 300) { AppLog.Info("mod.io: security code requested for the server account"); return null; }
            return ErrorText(status, body);
        }

        /// <summary>The account's subscriptions for this game, from mod.io (null + <paramref name="error"/> when it failed).</summary>
        public static List<long> Subscribed(out string error, string root = null)
        {
            error = null;
            string token = Token(root);
            if (string.IsNullOrEmpty(token)) { error = T("The server is not logged in to mod.io yet."); return null; }
            var ids = new List<long>();
            for (int offset = 0, page = 0; page < 50; page++)
            {
                var (status, body) = Call("GET", ApiRoot + "/me/subscribed?game_id=" + GameId + "&_limit=100&_offset=" + offset.ToString(CultureInfo.InvariantCulture), null, token);
                if (status != 200) { error = ErrorText(status, body); return null; }
                var d = Json.Parse(body);
                var data = d.Get("data").Arr();
                foreach (var m in data)
                {
                    long id = m.Get("id").Long();
                    if (id > 0 && !ids.Contains(id)) ids.Add(id);
                }
                offset += data.Count;
                if (data.Count == 0 || offset >= d.Get("result_total").Long()) break;
            }
            return ids;
        }

        /// <summary>Subscribes the server's account to a mod. Null when done, else why not.</summary>
        public static string Subscribe(long id, string root = null)
        {
            string token = Token(root);
            if (string.IsNullOrEmpty(token)) return T("The server is not logged in to mod.io yet.");
            var (status, body) = Call("POST", ApiRoot + "/games/" + GameId + "/mods/" + id.ToString(CultureInfo.InvariantCulture) + "/subscribe", "", token);
            return status >= 200 && status < 300 || IsAlready(body) ? null : ErrorText(status, body);
        }

        public static string Unsubscribe(long id, string root = null)
        {
            string token = Token(root);
            if (string.IsNullOrEmpty(token)) return T("The server is not logged in to mod.io yet.");
            var (status, body) = Call("DELETE", ApiRoot + "/games/" + GameId + "/mods/" + id.ToString(CultureInfo.InvariantCulture) + "/subscribe", null, token);
            return status >= 200 && status < 300 || IsAlready(body) ? null : ErrorText(status, body);
        }

        /// <summary>mod.io's "already subscribed" / "not subscribed" answers: the account is as asked.</summary>
        private static bool IsAlready(string body)
        {
            long reference = SafeParse(body).Path("error", "error_ref").Long();
            return reference == 15004 || reference == 15005;
        }

        private static object SafeParse(string body)
        {
            try { return Json.Parse(body ?? ""); } catch { return null; }
        }

        /// <summary>mod.io's own message for a failed call, with the field errors it lists.</summary>
        public static string ErrorText(int status, string body)
        {
            if (status == 0) return body ?? T("mod.io could not be reached.");
            var err = SafeParse(body).Get("error");
            string msg = err.Get("message").Str();
            var fields = err.Get("errors") as Dictionary<string, object>;
            if (fields != null && fields.Count > 0) msg = string.Join(" ", fields.Values.Select(v => v.Str()).Where(v => !string.IsNullOrEmpty(v)));
            if (string.IsNullOrWhiteSpace(msg)) msg = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
            return status == 401 ? F("mod.io did not take the server's login ({0}). Send a new security code and start the server with it.", msg.Trim()) : msg.Trim();
        }

        /// <summary>One call to mod.io: (status, body). Status 0 = no answer (the body says why).</summary>
        private static (int, string) Call(string method, string url, string form, string token)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Accept = "application/json";
                req.UserAgent = "SandstormModLauncher/" + typeof(ServerModio).Assembly.GetName().Version.ToString(3);
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                if (token != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                if (form != null)
                {
                    req.ContentType = "application/x-www-form-urlencoded";
                    byte[] bytes = Encoding.UTF8.GetBytes(form);
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return ((int)resp.StatusCode, r.ReadToEnd());
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse bad)
            {
                using (bad)
                using (var r = new StreamReader(bad.GetResponseStream(), Encoding.UTF8))
                    return ((int)bad.StatusCode, r.ReadToEnd());
            }
            catch (Exception ex)
            {
                AppLog.Warn("mod.io: " + ex.Message);
                return (0, F("mod.io could not be reached: {0}", ex.Message));
            }
        }
    }

    /// <summary>A server command line the player already has, from a .bat file or pasted.</summary>
    public static class ServerArgs
    {
        private static readonly Regex Exe = new Regex(@"(?i)(""[^""]*InsurgencyServer(-Win64-Shipping)?(\.exe)?""|[^\s""]*InsurgencyServer(-Win64-Shipping)?(\.exe)?)(?=\s|$)", RegexOptions.Compiled);
        private static readonly Regex SetLine = new Regex(@"^\s*set\s+(?!/[ap]\s)(""(?<k>[^=""]+)=(?<v>[^""]*)""|(?<k>[^=\s]+)=(?<v>.*))\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Var = new Regex(@"%(?<n>[A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);

        /// <summary>
        /// The server's options in a .bat or .cmd file: the line that starts InsurgencyServer (^ line breaks joined, the file's
        /// own set variables filled in), without the program itself and without redirections after it. Null when there is none.
        /// </summary>
        public static string FromBatch(string text, List<string> notes = null)
        {
            var lines = new List<string>();
            var sb = new StringBuilder();
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.TrimEnd();
                if (l.EndsWith("^")) { sb.Append(l, 0, l.Length - 1).Append(' '); continue; }
                sb.Append(l);
                lines.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) lines.Add(sb.ToString());

            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in lines)
            {
                string t = l.Trim().TrimStart('@');
                if (Skip.IsMatch(t)) continue;
                var set = SetLine.Match(t);
                if (set.Success) { vars[set.Groups["k"].Value.Trim()] = Expand(set.Groups["v"].Value, vars, null); continue; }
                // Variables first: "%SERVER%" can be the program (set SERVER=...\InsurgencyServer.exe, 2026-10-02 audit).
                string line = Expand(t, vars, null);
                var m = ExeAt(line);
                if (m == null) continue;
                Expand(t, vars, notes);
                return Cut(line.Substring(m.Index + m.Length)).Trim();
            }
            return null;
        }

        /// <summary>Lines that name the server without starting it (comments, messages, labels, stopping it).</summary>
        private static readonly Regex Skip = new Regex(@"^(rem(\s|$)|::|:\w|echo[\s.]|title\s|taskkill\s|tasklist|find(str)?\s|timeout\s|goto\s|cd(\s|/|\\|$)|chdir\s|pushd\s|popd)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The program in a line, when it comes before the options (not a hostname or path inside them).</summary>
        private static Match ExeAt(string line)
        {
            var m = Exe.Match(line);
            if (!m.Success) return null;
            string before = line.Substring(0, m.Index);
            return before.Contains("?") || Regex.IsMatch(before, @"(^|\s)-") || m.Value.StartsWith("-", StringComparison.Ordinal) ? null : m;
        }

        /// <summary>A pasted line: the options only (a leading "start ... InsurgencyServer.exe" is taken off).</summary>
        public static string Clean(string text)
        {
            string t = Regex.Replace(text ?? "", @"\s*\^?\s*[\r\n]+\s*", " ").Trim();
            var m = ExeAt(t);
            if (m != null) t = t.Substring(m.Index + m.Length);
            return Cut(t).Trim();
        }

        private static string Expand(string s, Dictionary<string, string> vars, List<string> notes) =>
            Var.Replace(s.Replace("%%", "\u0001"), m =>
            {
                if (vars.TryGetValue(m.Groups["n"].Value, out string v)) return v;
                notes?.Add(F("%{0}% is not set in the file: it was left as it is.", m.Groups["n"].Value));
                return m.Value;
            }).Replace("\u0001", "%");

        /// <summary>The line up to an unquoted redirection or second command (&gt;, |, &amp;), with single spaces between options.</summary>
        private static string Cut(string s)
        {
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in s)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && (c == '>' || c == '|' || c == '&' || c == '<')) break;
                if (!quoted && char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>True when a switch (-Mods) is on the command line, in any letter case.</summary>
        public static bool Has(string args, string name) =>
            Regex.IsMatch(args ?? "", @"(?<=^|\s)-" + Regex.Escape(name) + @"(?=\s|$)", RegexOptions.IgnoreCase);

        /// <summary>The value of -Key=value on the command line (quotes taken off), or null.</summary>
        public static string Value(string args, string key)
        {
            var m = Regex.Match(args ?? "", @"(?<=^|\s)-" + Regex.Escape(key) + @"=(""(?<v>[^""]*)""|(?<v>\S*))", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["v"].Value : null;
        }

        /// <summary>The first map of a command line (the part before the first ? when it does not start with -), or null.</summary>
        public static string FirstMap(string args)
        {
            string t = (args ?? "").Trim();
            if (t.Length == 0 || t[0] == '-') return null;
            int end = t.IndexOfAny(new[] { '?', ' ' });
            return end < 0 ? t : t.Substring(0, end);
        }
    }
}
