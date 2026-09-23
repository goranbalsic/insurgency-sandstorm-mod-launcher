using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SandstormModLauncher.Core
{
    /// <summary>
    /// Text-preserving helpers for Unreal config files (Game.ini, Input.ini). Only the launcher's
    /// own managed block is ever rewritten; the rest of the file is left byte-for-byte intact.
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

        /// <summary>Removes the launcher's managed block.</summary>
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

        public static string ExtractManagedBlock(string text)
        {
            int a = text.IndexOf(BlockStart, StringComparison.Ordinal);
            if (a < 0) return null;
            int b = text.IndexOf(BlockEnd, a, StringComparison.Ordinal);
            if (b < 0) return null;
            return text.Substring(a + BlockStart.Length, b - a - BlockStart.Length).Trim('\r', '\n');
        }

        public static string WithManagedBlock(string text, string block)
        {
            string baseText = StripManagedBlocks(text);
            if (string.IsNullOrWhiteSpace(block)) return baseText.Length > 0 ? baseText + "\r\n" : "\r\n";
            var sb = new StringBuilder();
            if (baseText.Length > 0) sb.Append(baseText).Append("\r\n\r\n");
            sb.Append(BlockStart).Append("\r\n").Append(block.Trim('\r', '\n')).Append("\r\n").Append(BlockEnd).Append("\r\n");
            return sb.ToString();
        }

        public static List<string> Split(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n').ToList();

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
