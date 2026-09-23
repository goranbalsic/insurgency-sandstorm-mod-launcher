using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SandstormModLauncher.Core
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class JsonIgnoreAttribute : Attribute { }

    /// <summary>
    /// Small dependency-free JSON reader/writer. Parsed values are
    /// Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, long, bool or null.
    /// POCOs are (de)serialized through their public read/write properties.
    /// </summary>
    public static class Json
    {
        // ------------------------------------------------------------------ parse

        public static object Parse(string text)
        {
            if (text == null) return null;
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue();
            p.SkipWs();
            return v;
        }

        private sealed class Parser
        {
            private readonly string s;
            private int i;
            public Parser(string s) { this.s = s; if (s.Length > 0 && s[0] == '﻿') i = 1; }

            public void SkipWs()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                    {
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    break;
                }
            }

            private Exception Error(string msg) => new FormatException($"JSON: {msg} at position {i}");

            public object ReadValue()
            {
                if (i >= s.Length) throw Error("unexpected end");
                char c = s[i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"unexpected character '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error("expected " + word);
                i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length || s[i] != '"') throw Error("expected property name");
                    string key = ReadString();
                    SkipWs();
                    if (i >= s.Length || s[i] != ':') throw Error("expected ':'");
                    i++;
                    SkipWs();
                    d[key] = ReadValue();
                    SkipWs();
                    if (i >= s.Length) throw Error("unterminated object");
                    if (s[i] == ',') { i++; SkipWs(); if (i < s.Length && s[i] == '}') { i++; return d; } continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                var list = new List<object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    SkipWs();
                    list.Add(ReadValue());
                    SkipWs();
                    if (i >= s.Length) throw Error("unterminated array");
                    if (s[i] == ',') { i++; SkipWs(); if (i < s.Length && s[i] == ']') { i++; return list; } continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Error("unterminated string");
                    char c = s[i++];
                    if (c == '"') break;
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw Error("bad escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Error("bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                return sb.ToString();
            }

            private object ReadNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                bool isFloat = false;
                if (i < s.Length && s[i] == '.') { isFloat = true; i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    isFloat = true; i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                string num = s.Substring(start, i - start);
                if (!isFloat && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
                return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // -------------------------------------------------------------- serialize

        public static string Serialize(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        private static void Write(StringBuilder sb, object v, bool indent, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string str: WriteString(sb, str); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char ch: WriteString(sb, ch.ToString()); return;
                case Enum en: WriteString(sb, en.ToString()); return;
                case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
                case int _: case long _: case short _: case byte _: case uint _: case ulong _: case ushort _: case sbyte _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append('0');
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
            }

            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }

            if (v is IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }

            // POCO
            sb.Append('{');
            bool any = false;
            foreach (var p in GetProps(v.GetType()))
            {
                if (any) sb.Append(',');
                any = true;
                NewLine(sb, indent, level + 1);
                WriteString(sb, p.Name);
                sb.Append(indent ? ": " : ":");
                Write(sb, p.GetValue(v, null), indent, level + 1);
            }
            if (any) NewLine(sb, indent, level);
            sb.Append('}');
        }

        private static readonly Dictionary<Type, PropertyInfo[]> propCache = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo[] GetProps(Type t)
        {
            lock (propCache)
            {
                if (propCache.TryGetValue(t, out var cached)) return cached;
                var list = new List<PropertyInfo>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                    if (p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0) continue;
                    list.Add(p);
                }
                var arr = list.ToArray();
                propCache[t] = arr;
                return arr;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ deserialize

        public static T Deserialize<T>(string text) => (T)ConvertTo(Parse(text), typeof(T));

        public static object ConvertTo(object v, Type t)
        {
            if (v == null)
                return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;

            var nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null) t = nullable;

            if (t == typeof(object)) return v;
            if (t == typeof(string)) return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
            if (t == typeof(bool))
            {
                if (v is bool b) return b;
                if (v is string bs) return bs.Equals("true", StringComparison.OrdinalIgnoreCase) || bs == "1";
                return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0;
            }
            if (t.IsEnum)
            {
                if (v is string es)
                {
                    try { return Enum.Parse(t, es, true); } catch { return Activator.CreateInstance(t); }
                }
                return Enum.ToObject(t, Convert.ToInt64(v, CultureInfo.InvariantCulture));
            }
            if (t == typeof(DateTime))
            {
                if (v is string ds && DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) return dt;
                return default(DateTime);
            }
            if (t.IsPrimitive || t == typeof(decimal))
            {
                try
                {
                    if (v is string ns) return Convert.ChangeType(double.Parse(ns, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
                    return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
                }
                catch { return Activator.CreateInstance(t); }
            }

            if (t.IsArray)
            {
                var et = t.GetElementType();
                var src = v as List<object> ?? new List<object>();
                var arr = Array.CreateInstance(et, src.Count);
                for (int k = 0; k < src.Count; k++) arr.SetValue(ConvertTo(src[k], et), k);
                return arr;
            }

            if (t.IsGenericType)
            {
                var gd = t.GetGenericTypeDefinition();
                if (gd == typeof(List<>) || gd == typeof(IList<>) || gd == typeof(IEnumerable<>))
                {
                    var et = t.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(et));
                    if (v is List<object> src) foreach (var item in src) list.Add(ConvertTo(item, et));
                    return list;
                }
                if (gd == typeof(Dictionary<,>) || gd == typeof(IDictionary<,>))
                {
                    var args = t.GetGenericArguments();
                    var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args));
                    if (v is Dictionary<string, object> src)
                        foreach (var kv in src) dict[ConvertTo(kv.Key, args[0])] = ConvertTo(kv.Value, args[1]);
                    return dict;
                }
            }

            if (v is Dictionary<string, object> obj)
            {
                var inst = Activator.CreateInstance(t);
                foreach (var p in GetProps(t))
                {
                    if (!obj.TryGetValue(p.Name, out var pv)) continue;
                    try { p.SetValue(inst, ConvertTo(pv, p.PropertyType), null); }
                    catch { /* keep default on malformed values */ }
                }
                return inst;
            }
            return null;
        }

        // ------------------------------------------------------ dynamic helpers

        public static object Get(this object node, string key)
            => node is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static object Path(this object node, params string[] keys)
        {
            foreach (var k in keys) node = node.Get(k);
            return node;
        }

        public static string Str(this object node) => node switch
        {
            null => null,
            string s => s,
            _ => Convert.ToString(node, CultureInfo.InvariantCulture),
        };

        public static long Long(this object node, long fallback = 0)
        {
            switch (node)
            {
                case long l: return l;
                case double d: return (long)d;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r): return r;
                case bool b: return b ? 1 : 0;
                default: return fallback;
            }
        }

        public static List<object> Arr(this object node) => node as List<object> ?? new List<object>();
    }
}
