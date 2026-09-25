using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SandstormModLauncher.Core
{
    /// <summary>Removes account details from game log lines before the launcher keeps or shows them.</summary>
    public static class LogSanitizer
    {
        private static readonly Regex Sensitive = new Regex(
            @"LogPros|LogVOIP|VivoxCore|LogOnline|token|signature|Steam(ID|NWI)|userIdentity|payload|LogEOS|LogHydra|LogAnalytics|auth|ticket|password|session ?id|LogSteamShared|LogHttp",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Ip = new Regex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b", RegexOptions.Compiled);
        private static readonly Regex Name = new Regex(@"(Name=)[^?&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SteamId = new Regex(@"\b7656119\d{10}\b", RegexOptions.Compiled);
        private static readonly Regex Email = new Regex(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);
        private static readonly Regex UserPath = new Regex(@"(?i)(C:\\Users\\)[^\\]+", RegexOptions.Compiled);

        /// <summary>True for lines from online services that may hold tokens or account ids.</summary>
        public static bool IsSensitive(string line) => Sensitive.IsMatch(line);

        /// <summary>The line with addresses, names and ids masked, or null when the whole line should be dropped.</summary>
        public static string Clean(string line)
        {
            if (line == null || IsSensitive(line)) return null;
            line = Ip.Replace(line, "x.x.x.x");
            line = Name.Replace(line, "$1<hidden>");
            line = SteamId.Replace(line, "<id>");
            line = Email.Replace(line, "<email>");
            line = UserPath.Replace(line, "$1<user>");
            return line;
        }

        private static readonly Regex AnyUserPath = new Regex(@"(?i)([A-Z]:\\(Users|Documents and Settings)\\)[^\\""\r\n]+", RegexOptions.Compiled);
        private static Regex ownNames;

        /// <summary>
        /// Clean, plus everything that could name this PC or person, for text that leaves the PC (a GitHub issue):
        /// the Windows user name and PC name wherever they appear, and user folders on any drive.
        /// </summary>
        public static string Public(string line)
        {
            line = Clean(line);
            if (line == null) return null;
            line = AnyUserPath.Replace(line, "$1<user>");
            if (ownNames == null)
            {
                var names = new[] { Environment.UserName, Environment.MachineName }
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3).Select(n => Regex.Escape(n.Trim())).ToList();
                ownNames = names.Count == 0 ? new Regex("(?!)") : new Regex(@"(?i)(?<![A-Za-z0-9])(" + string.Join("|", names) + @")(?![A-Za-z0-9])", RegexOptions.Compiled);
            }
            return ownNames.Replace(line, "<name>");
        }
    }
}
