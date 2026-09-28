using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.Services
{
    /// <summary>
    /// --cli translation-template [repo folder] [out.csv]: every English text of the launcher, read from its source:
    /// the texts written in the XAML pages, every T("...") and F("...") in the code, and the names and descriptions in
    /// the rules database. The table goes to Resources\Languages\template.csv, which the launcher carries and turns into
    /// a translation file (Settings > Language). Run it again whenever texts change.
    /// </summary>
    public static class TranslationTemplate
    {
        private static readonly Regex XamlAttr = new Regex(@"\b(?:Text|Content|ToolTip|Tag|Header|Title|Label|Hint)=""(?<v>[^""]*)""", RegexOptions.Compiled);
        private static readonly Regex XamlTr = new Regex(@"\{v:Tr\s+'(?<v>[^']*)'\s*\}", RegexOptions.Compiled);
        private static readonly Regex DialogCall = new Regex(@"(?<![\w.])(?:Ask|Prompt|ShowMessage|ShowToast)\(", RegexOptions.Compiled);
        private static readonly Regex CsCall = new Regex(@"(?<![\w.])(?:Loc\.)?[TFN]\(\s*(?="")", RegexOptions.Compiled);

        public static List<Loc.Row> Scan(string repo)
        {
            var rows = new List<Loc.Row>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string text, string where)
            {
                string k = Loc.Key(text);
                if (k.Length == 0 || !k.Any(char.IsLetter) || !seen.Add(k)) return;
                rows.Add(new Loc.Row { English = k, Translation = "", Where = where });
            }

            foreach (var f in Directory.GetFiles(Path.Combine(repo, "Views"), "*.xaml", SearchOption.AllDirectories).OrderBy(x => Order(x)))
            {
                string text = File.ReadAllText(f), where = Where(f);
                foreach (Match m in XamlTr.Matches(text)) Add(WebUtility.HtmlDecode(m.Groups["v"].Value), where);
                foreach (Match m in XamlAttr.Matches(text))
                {
                    string v = m.Groups["v"].Value;
                    if (v.StartsWith("{", StringComparison.Ordinal) || v.StartsWith("[/Script/", StringComparison.Ordinal)) continue;
                    Add(WebUtility.HtmlDecode(v), where);
                }
            }
            foreach (var f in Directory.GetFiles(repo, "*.cs", SearchOption.AllDirectories)
                                       .Where(x => !x.Contains("\\obj\\") && !x.Contains("\\bin\\") && !x.Contains("\\tools\\"))
                                       .Where(x => !Regex.IsMatch(Path.GetFileName(x), "(Torture|Test)\\.cs$"))   // the tests' own sample texts
                                       .OrderBy(x => Order(x)))
            {
                string text = File.ReadAllText(f), where = Where(f);
                foreach (Match m in CsCall.Matches(text)) Add(Unescape(LiteralChain(text, m.Index + m.Length, out _)), where);
                // Dialogs and toasts translate their own texts, so the call sites keep plain English literals.
                foreach (Match m in DialogCall.Matches(text))
                    foreach (var lit in Literals(text, m.Index + m.Length)) Add(Unescape(lit), where);
            }
            string rules = Path.Combine(repo, "Resources", "GameRules.json");
            if (File.Exists(rules))
            {
                var db = Json.Parse(File.ReadAllText(rules));
                foreach (var mode in Items(db.Get("modes"))) Add(mode.Get("name").Str(), "Game modes");
                foreach (var prop in Items(db.Get("properties")))
                {
                    Add(prop.Get("cat").Str(), "Match rules");
                    Add(prop.Get("label").Str(), "Match rules");
                    Add(prop.Get("desc").Str(), "Match rules");
                }
                foreach (var pl in Items(db.Get("playlists")))
                {
                    Add(pl.Get("title").Str(), "Official playlists");
                    Add(pl.Get("desc").Str(), "Official playlists");
                }
                foreach (var rs in Items(db.Get("rulesets")))
                {
                    Add(rs.Get("name").Str(), "Official rulesets");
                    foreach (var note in Items(rs.Get("notes"))) Add(note as string, "Official rulesets");
                }
            }
            return rows;
        }

        /// <summary>The entries of a JSON list, or the values of a JSON object.</summary>
        private static IEnumerable<object> Items(object node)
        {
            if (node is System.Collections.IDictionary d) return d.Values.Cast<object>();
            if (node is System.Collections.IEnumerable e && !(node is string)) return e.Cast<object>();
            return Enumerable.Empty<object>();
        }

        /// <summary>Pages first (in the order they appear), then the messages.</summary>
        private static string Order(string file)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            string[] first = { "MainWindow", "PlayPage", "LivePage", "MutatorsPage", "ModsPage", "ServerPage", "SettingsPage" };
            int i = Array.IndexOf(first, n);
            return (i >= 0 ? i.ToString("00") : "50") + n;
        }

        private static string Where(string file)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            switch (n)
            {
                case "MainWindow": return "Main window";
                case "PlayPage": return "Play";
                case "LivePage": return "Play > Live";
                case "MutatorsPage": return "Play > Mods (mutators)";
                case "ModsPage": return "Play > Mods (installed)";
                case "ServerPage": return "Server";
                case "SettingsPage": return "Settings";
            }
            if (n.StartsWith("MainViewModel.", StringComparison.Ordinal)) return n.Substring(14) + " (messages)";
            if (n == "MainViewModel") return "Messages";
            return Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
        }

        /// <summary>The plain string literals inside a call, from just after its "(" to the matching ")".</summary>
        private static IEnumerable<string> Literals(string text, int start)
        {
            int depth = 1;
            for (int i = start; i < text.Length && depth > 0; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '\'') { i++; if (i < text.Length && text[i] == '\\') i++; i++; }
                else if (c == '"')
                {
                    bool interpolated = i > 0 && (text[i - 1] == '$' || (i > 1 && text[i - 1] == '@' && text[i - 2] == '$'));
                    string s = LiteralChain(text, i, out i);
                    if (!interpolated) yield return s;
                }
            }
        }

        /// <summary>
        /// The string literal starting at <paramref name="quote"/>, joined with the literals added to it ("a " + "b"), as
        /// it is written in the code (escapes kept). <paramref name="end"/> is the closing quote of the last one.
        /// </summary>
        private static string LiteralChain(string text, int quote, out int end)
        {
            var sb = new StringBuilder();
            int i = quote;
            while (true)
            {
                bool verbatim = i > 0 && text[i - 1] == '@';
                for (i++; i < text.Length; i++)
                {
                    if (!verbatim && text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i]).Append(text[i + 1]); i++; continue; }
                    if (text[i] == '"')
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { sb.Append("\\\""); i++; continue; }
                        break;
                    }
                    if (verbatim && text[i] == '\\') { sb.Append("\\\\"); continue; }   // a plain character in a verbatim text
                    sb.Append(text[i]);
                }
                end = i;
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j >= text.Length || text[j] != '+') return sb.ToString();
                j++;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j < text.Length && text[j] == '@' && j + 1 < text.Length && text[j + 1] == '"') j++;
                if (j >= text.Length || text[j] != '"' || text[j - 1] == '$') return sb.ToString();
                i = j;
            }
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u' when i + 4 < s.Length: sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Writes the table (UTF-8 with the byte order mark, so Excel shows every script).</summary>
        public static int Write(string repo, string outFile, Action<string> print)
        {
            var rows = Scan(repo);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllText(outFile, Loc.WriteCsv(rows), new UTF8Encoding(true));
            print(rows.Count + " texts written to " + outFile);
            foreach (var g in rows.GroupBy(r => r.Where)) print("  " + g.Key + ": " + g.Count());
            return 0;
        }
    }
}
