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
    }
}
