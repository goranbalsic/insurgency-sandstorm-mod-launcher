using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>
    /// Text-preserving helpers for Unreal config files (Game.ini, Input.ini). Values are written by key;
    /// every line the launcher does not manage is kept as it was.
    /// </summary>
    public static class UeIni
    {
        public const string BlockStart = "; >>> Sandstorm Mod Launcher (managed block, edited automatically) >>>";
        public const string BlockEnd = "; <<< Sandstorm Mod Launcher <<<";

        public static string ReadText(string path)
        {
            if (!File.Exists(path)) return "";
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bool ascii = text.All(c => c < 128);
            string tmp = path + ".sml-tmp";
            if (ascii) File.WriteAllText(tmp, text, new UTF8Encoding(false));
            else File.WriteAllText(tmp, text, Encoding.Unicode); // Unreal reads UTF-16 LE with BOM
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>Removes the marked block older launcher versions wrote.</summary>
        public static string StripManagedBlocks(string text)
        {
            var lines = Split(text);
            var result = new List<string>();
            bool skipping = false;
            foreach (var line in lines)
            {
                string t = line.Trim();
                if (!skipping && t == BlockStart) { skipping = true; continue; }
                if (skipping)
                {
                    if (t == BlockEnd) skipping = false;
                    continue;
                }
                result.Add(line);
            }
            while (result.Count > 0 && result[result.Count - 1].Trim().Length == 0) result.RemoveAt(result.Count - 1);
            return string.Join("\r\n", result);
        }

        public static List<string> Split(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n').ToList();

        /// <summary>One [Section] with its Key=Value lines.</summary>
        public sealed class Section
        {
            public string Name;
            public List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
            public Section(string name) { Name = name; }
        }

        /// <summary>Key of a "Key=Value" line (array operators + - . ! removed), or null for comments, blanks and headers.</summary>
        public static string KeyOf(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#") || t.StartsWith("[")) return null;
            if (t[0] == '+' || t[0] == '-' || t[0] == '.' || t[0] == '!') t = t.Substring(1);
            int eq = t.IndexOf('=');
            return (eq >= 0 ? t.Substring(0, eq) : t).Trim();
        }

        /// <summary>Sections and values of config text (for merging lines typed by the player).</summary>
        public static List<Section> Parse(string text)
        {
            var list = new List<Section>();
            Section current = null;
            foreach (var raw in Split(text))
            {
                string t = raw.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { current = new Section(t.Substring(1, t.Length - 2)); list.Add(current); continue; }
                string key = KeyOf(raw);
                if (key == null || current == null) continue;
                int eq = t.IndexOf('=');
                current.Values.Add(new KeyValuePair<string, string>(t.Substring(0, eq >= 0 ? eq : t.Length).Trim(), eq >= 0 ? t.Substring(eq + 1).Trim() : ""));
            }
            return list;
        }

        /// <summary>
        /// Writes the given values into the file by key, not by marker comments: the game rewrites its own
        /// config files and drops comments, so markers cannot be relied on. Every existing line for a key the
        /// launcher manages (isManaged) or writes now is removed from its section first, so each key ends up
        /// exactly once (the game uses the first value it finds). Everything else is kept as it was.
        /// </summary>
        public static string MergeSections(string text, IList<Section> ours, Func<string, string, bool> isManaged)
        {
            var writing = new HashSet<string>(ours.SelectMany(s => s.Values.Select(v => s.Name + "\n" + v.Key)), StringComparer.OrdinalIgnoreCase);
            bool Drop(string section, string key) =>
                section != null && key != null && (writing.Contains(section + "\n" + key) || isManaged(section, key));

            // Parse into blocks: preamble (section null) and sections, keeping every other line untouched.
            var blocks = new List<(string Name, List<string> Lines)> { (null, new List<string>()) };
            foreach (var line in Split(StripManagedBlocks(text)))
            {
                string t = line.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { blocks.Add((t.Substring(1, t.Length - 2), new List<string> { line })); continue; }
                var b = blocks[blocks.Count - 1];
                if (Drop(b.Name, KeyOf(line))) continue;
                b.Lines.Add(line);
            }
            foreach (var s in ours)
            {
                if (s.Values.Count == 0) continue;
                var target = blocks.FirstOrDefault(b => string.Equals(b.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                if (target.Lines == null) { target = (s.Name, new List<string> { "[" + s.Name + "]" }); blocks.Add(target); }
                int at = target.Lines.Count;
                while (at > 1 && target.Lines[at - 1].Trim().Length == 0) at--;
                target.Lines.InsertRange(at, s.Values.Select(v => v.Key + "=" + v.Value));
            }
            var sb = new StringBuilder();
            foreach (var b in blocks)
            {
                var lines = b.Lines;
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                // A section left with nothing but its header is dropped.
                if (b.Name != null && lines.Count(l => l.Trim().Length > 0) <= 1) continue;
                if (lines.Count == 0) continue;
                foreach (var l in lines) sb.Append(l).Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString().TrimEnd('\r', '\n') + "\r\n";
        }

        public static string Render(IEnumerable<Section> sections)
        {
            var sb = new StringBuilder();
            foreach (var s in sections.Where(s => s.Values.Count > 0))
            {
                sb.Append('[').Append(s.Name).Append("]\r\n");
                foreach (var v in s.Values) sb.Append(v.Key).Append('=').Append(v.Value).Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>Values of a key inside a section, following Unreal array operators (+ - . !).</summary>
        public static List<string> ReadArray(string text, string section, string key, List<string> initial = null)
        {
            var values = initial != null ? new List<string>(initial) : new List<string>();
            string current = null;
            foreach (var raw in Split(text))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { current = line.Substring(1, line.Length - 2); continue; }
                if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
                char op = line[0];
                string body = (op == '+' || op == '-' || op == '.' || op == '!') ? line.Substring(1) : line;
                int eq = body.IndexOf('=');
                string k = (eq >= 0 ? body.Substring(0, eq) : body).Trim();
                if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = eq >= 0 ? body.Substring(eq + 1).Trim() : "";
                switch (op)
                {
                    case '!': values.Clear(); break;
                    case '-': values.RemoveAll(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)); break;
                    case '+': if (!values.Contains(v, StringComparer.OrdinalIgnoreCase)) values.Add(v); break;
                    case '.': values.Add(v); break;
                    default: values.Clear(); values.Add(v); break;
                }
            }
            return values;
        }

        /// <summary>Adds "Line" under [Section], creating the section when missing. No-op when present.</summary>
        public static string EnsureLine(string text, string section, string line)
        {
            var lines = Split(text);
            int sectionIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) sectionIdx = i;
                else if (sectionIdx >= 0 && t.StartsWith("[")) break;
                if (sectionIdx >= 0 && i > sectionIdx && t.Equals(line, StringComparison.OrdinalIgnoreCase)) return text;
            }
            if (sectionIdx < 0)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.Add("[" + section + "]");
                lines.Add(line);
                lines.Add("");
            }
            else
            {
                int insertAt = sectionIdx + 1;
                while (insertAt < lines.Count && !lines[insertAt].Trim().StartsWith("[") && lines[insertAt].Trim().Length > 0) insertAt++;
                lines.Insert(insertAt, line);
            }
            return string.Join("\r\n", lines);
        }
    }
}
