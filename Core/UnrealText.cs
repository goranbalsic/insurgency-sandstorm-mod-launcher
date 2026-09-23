using System;
using System.Collections.Generic;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>Turns exported FText strings (NSLOCTEXT / INVTEXT / LOCTABLE) into display text.</summary>
    public static class UnrealText
    {
        public static string Resolve(string raw, Func<string, string, string> tableLookup = null)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.StartsWith("NSLOCTEXT(", StringComparison.Ordinal))
            {
                var a = Args(raw, 10);
                return a.Count >= 3 ? a[2] : raw;
            }
            if (raw.StartsWith("LOCTEXT(", StringComparison.Ordinal))
            {
                var a = Args(raw, 8);
                return a.Count >= 2 ? a[1] : raw;
            }
            if (raw.StartsWith("INVTEXT(", StringComparison.Ordinal))
            {
                var a = Args(raw, 8);
                return a.Count >= 1 ? a[0] : raw;
            }
            if (raw.StartsWith("LOCTABLE(", StringComparison.Ordinal))
            {
                var a = Args(raw, 9);
                if (a.Count >= 2)
                {
                    string v = tableLookup?.Invoke(a[0], a[1]);
                    return string.IsNullOrEmpty(v) ? Humanize(a[1]) : v;
                }
            }
            return raw;
        }

        private static List<string> Args(string s, int start)
        {
            var list = new List<string>();
            int i = start;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] != '"' && s[i] != ')') i++;
                if (i >= s.Length || s[i] == ')') break;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; }
                    sb.Append(s[i]);
                    i++;
                }
                list.Add(sb.ToString());
                i++;
            }
            return list;
        }

        /// <summary>"SlowCaptureTimesTitle" -> "Slow Capture Times", "ISMC_Hardcore" -> "ISMC Hardcore".</summary>
        public static string Humanize(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            string s = id;
            foreach (var suffix in new[] { "Title", "_C" })
                if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length) s = s.Substring(0, s.Length - suffix.Length);
            if (s.StartsWith("BP_Mutator_", StringComparison.Ordinal)) s = s.Substring(11);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '_') { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c))
                {
                    char p = s[i - 1];
                    bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                    if (char.IsLower(p) || (char.IsUpper(p) && nextLower) || char.IsDigit(p))
                        if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else if (i > 0 && char.IsDigit(c) && char.IsLetter(s[i - 1]))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
