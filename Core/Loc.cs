using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>
    /// Translations. Every text of the launcher is written in English in the code; a language file maps each English
    /// text to its translation. The file is a plain table (CSV: English, Translation, Where) that anyone can fill in with
    /// Excel, Google Sheets or LibreOffice. A text without a translation stays English.
    /// </summary>
    public static class Loc
    {
        public sealed class Language
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Translator { get; set; }
            public string Path { get; set; }
            public bool BuiltIn { get; set; }
            public int Count { get; set; }
        }

        public sealed class Row
        {
            public string English;
            public string Translation;
            public string Where;
        }

        public const string LanguageKey = "@language";
        public const string TranslatorKey = "@translator";
        private const string ResourcePrefix = "SandstormModLauncher.Resources.Languages.";
        private static Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The language in use: its id, or "" for English.</summary>
        public static string CurrentId { get; private set; } = "";
        public static bool IsEnglish => map.Count == 0;
        public static event Action Changed;

        /// <summary>Language files the player adds (a translation being made, or one somebody sent).</summary>
        public static string UserFolder => Path.Combine(AppPaths.DataDir, "languages");

        public static string T(string english)
        {
            if (string.IsNullOrEmpty(english) || map.Count == 0) return english;
            if (!map.TryGetValue(Key(english), out var t)) return english;
            // A piece of a longer text keeps its spaces at both ends ("Loading " + map).
            int lead = english.Length - english.TrimStart().Length, trail = english.Length - english.TrimEnd().Length;
            return english.Substring(0, lead) + t + english.Substring(english.Length - trail);
        }

        /// <summary>A text with values in it: "Loading {0}". A translation that breaks the placeholders falls back to English.</summary>
        public static string F(string english, params object[] args)
        {
            string f = T(english);
            try { return string.Format(CultureInfo.InvariantCulture, f, args); }
            catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, english, args); }
        }

        /// <summary>Marks a text for the translation table where it is kept in English and translated when shown (a fixed list).</summary>
        public static string N(string english) => english;

        public static string Key(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        /// <summary>The languages there are: those that come with the launcher and those in the languages folder (a file there wins).</summary>
        public static List<Language> Available()
        {
            var list = new List<Language>();
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                string id = res.Substring(ResourcePrefix.Length, res.Length - ResourcePrefix.Length - 4);
                if (id.Equals("template", StringComparison.OrdinalIgnoreCase)) continue;
                try { list.Add(Describe(id, ReadCsv(ReadResource(res)), res, true)); } catch (Exception ex) { AppLog.Warn("Language " + id + ": " + ex.Message); }
            }
            try
            {
                if (Directory.Exists(UserFolder))
                    foreach (var f in Directory.GetFiles(UserFolder, "*.csv"))
                    {
                        string id = Path.GetFileNameWithoutExtension(f);
                        if (id.Equals("template", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var lang = Describe(id, ReadCsv(ReadFile(f)), f, false);
                            list.RemoveAll(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                            list.Add(lang);
                        }
                        catch (Exception ex) { AppLog.Warn("Language file " + Path.GetFileName(f) + ": " + ex.Message); }
                    }
            }
            catch (Exception ex) { AppLog.Warn("Languages folder: " + ex.Message); }
            return list.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static Language Describe(string id, List<Row> rows, string path, bool builtIn)
        {
            string name = rows.FirstOrDefault(r => r.English == LanguageKey)?.Translation;
            return new Language
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                Translator = rows.FirstOrDefault(r => r.English == TranslatorKey)?.Translation?.Trim(),
                Path = path,
                BuiltIn = builtIn,
                Count = rows.Count(r => !r.English.StartsWith("@", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(r.Translation)),
            };
        }

        /// <summary>Switches the language ("" or null = English). False when the language was not found (English is used then).</summary>
        public static bool Use(string id)
        {
            List<Row> rows = null;
            bool found = string.IsNullOrEmpty(id);
            if (!found)
            {
                var lang = Available().FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (lang != null)
                {
                    found = true;
                    rows = ReadCsv(lang.BuiltIn ? ReadResource(lang.Path) : ReadFile(lang.Path));
                    AppLog.Info("Language: " + lang.Name);
                }
                else AppLog.Warn("Language " + id + " not found, English is used");
            }
            UseTable(found ? rows : null, found ? id : "");
            return found;
        }

        /// <summary>Uses a translation table read already (null = English). The tests use it directly.</summary>
        public static void UseTable(IEnumerable<Row> rows, string id)
        {
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rows ?? Enumerable.Empty<Row>())
                if (!r.English.StartsWith("@", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(r.Translation))
                    next[Key(r.English)] = r.Translation.Replace("\r\n", "\n").Trim();
            map = next;
            CurrentId = rows != null ? id ?? "" : "";
            try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Error("Language change", ex); }
        }

        // ------------------------------------------------------------------ files

        public static string ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8, true)) return r.ReadToEnd();
            }
        }

        /// <summary>UTF-8 (with or without the byte order mark); a file saved in the PC's old code page is read with that.</summary>
        public static string ReadFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (DecoderFallbackException) { return Encoding.Default.GetString(bytes); }
        }

        /// <summary>
        /// Reads a translation table. The columns are found by their names in the first line (English, Translation, Where);
        /// without them the first column is the English text and the second its translation. Commas and semicolons
        /// (Excel in many countries) both work as separators.
        /// </summary>
        public static List<Row> ReadCsv(string text)
        {
            var rows = new List<Row>();
            var records = ParseCsv(text ?? "");
            if (records.Count == 0) return rows;
            int en = 0, tr = 1, wh = 2;
            var head = records[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
            int hEn = head.IndexOf("english"), hTr = head.IndexOf("translation");
            bool header = hEn >= 0 && hTr >= 0;
            if (header) { en = hEn; tr = hTr; wh = head.IndexOf("where"); }
            foreach (var rec in records.Skip(header ? 1 : 0))
            {
                string e = rec.Count > en ? rec[en] : "";
                if (Key(e).Length == 0) continue;
                rows.Add(new Row { English = Key(e), Translation = rec.Count > tr ? rec[tr] : "", Where = wh >= 0 && rec.Count > wh ? rec[wh] : "" });
            }
            return rows;
        }

        private static List<List<string>> ParseCsv(string text)
        {
            text = text.TrimStart('﻿');
            int firstLineEnd = text.IndexOf('\n');
            string first = firstLineEnd < 0 ? text : text.Substring(0, firstLineEnd);
            char sep = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
            var records = new List<List<string>>();
            var rec = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == sep) { rec.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n') { rec.Add(cell.ToString()); cell.Clear(); records.Add(rec); rec = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || rec.Count > 0) { rec.Add(cell.ToString()); records.Add(rec); }
            return records.Where(r => r.Any(x => x.Trim().Length > 0)).ToList();
        }

        public static string WriteCsv(IEnumerable<Row> rows)
        {
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.Append("English,Translation,Where\r\n");
            foreach (var r in rows) sb.Append(Q(r.English)).Append(',').Append(Q(r.Translation)).Append(',').Append(Q(r.Where)).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>Every text of the launcher, in English (the table that comes with it).</summary>
        public static List<Row> Template() => ReadCsv(ReadResource(ResourcePrefix + "template.csv"));

        /// <summary>
        /// A table to translate: every text of the launcher, with the translations a language already has filled in
        /// (so a translation can be brought up to date). Saved as UTF-8 with the byte order mark Excel needs.
        /// </summary>
        public static void MakeTranslationFile(string languageId, string path)
        {
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            string name = "", translator = "";
            var lang = string.IsNullOrEmpty(languageId) ? null : Available().FirstOrDefault(l => l.Id.Equals(languageId, StringComparison.OrdinalIgnoreCase));
            if (lang != null)
            {
                foreach (var r in ReadCsv(lang.BuiltIn ? ReadResource(lang.Path) : ReadFile(lang.Path)))
                    if (!string.IsNullOrWhiteSpace(r.Translation)) existing[r.English] = r.Translation;
                name = lang.Name;
                translator = lang.Translator ?? "";
            }
            var rows = new List<Row>
            {
                new Row { English = LanguageKey, Translation = name, Where = "The name of the language, written in that language (e.g. 简体中文, Español)" },
                new Row { English = TranslatorKey, Translation = translator, Where = "Your name or nickname, shown in Settings (optional)" },
            };
            foreach (var t in Template().Where(r => !r.English.StartsWith("@", StringComparison.Ordinal)))
                rows.Add(new Row { English = t.English, Translation = existing.TryGetValue(t.English, out var tr) ? tr : "", Where = t.Where });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, WriteCsv(rows), new UTF8Encoding(true));
        }
    }
}
