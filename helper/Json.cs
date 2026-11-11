// 极简 JSON 编解码：只覆盖本协议用到的类型，不引第三方库。
// .NET Framework 4.8 自带的 JavaScriptSerializer 对 Dictionary 支持松散、且大字符串性能差，
// 这里手写一份，行为完全可预测。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DshComputerUse
{
    internal static class Json
    {
        // ── 解析 ───────────────────────────────────────────────

        public static object Parse(string text)
        {
            int at = 0;
            object value = ParseValue(text, ref at);
            SkipWhitespace(text, ref at);
            if (at != text.Length)
            {
                throw new FormatException("JSON 尾部有多余内容（位置 " + at + "）");
            }
            return value;
        }

        private static object ParseValue(string s, ref int at)
        {
            SkipWhitespace(s, ref at);
            if (at >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[at];
            switch (c)
            {
                case '{': return ParseObject(s, ref at);
                case '[': return ParseArray(s, ref at);
                case '"': return ParseString(s, ref at);
                case 't': Expect(s, ref at, "true"); return true;
                case 'f': Expect(s, ref at, "false"); return false;
                case 'n': Expect(s, ref at, "null"); return null;
                default: return ParseNumber(s, ref at);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int at)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            at++; // {
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == '}') { at++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != '"') throw new FormatException("JSON 对象缺少键名");
                string key = ParseString(s, ref at);
                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != ':') throw new FormatException("JSON 对象缺少冒号");
                at++;
                result[key] = ParseValue(s, ref at);
                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw new FormatException("JSON 对象意外结束");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == '}') { at++; return result; }
                throw new FormatException("JSON 对象缺少逗号");
            }
        }

        private static List<object> ParseArray(string s, ref int at)
        {
            var result = new List<object>();
            at++; // [
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == ']') { at++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref at));
                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw new FormatException("JSON 数组意外结束");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == ']') { at++; return result; }
                throw new FormatException("JSON 数组缺少逗号");
            }
        }

        private static string ParseString(string s, ref int at)
        {
            var sb = new StringBuilder();
            at++; // "
            while (true)
            {
                if (at >= s.Length) throw new FormatException("JSON 字符串未闭合");
                char c = s[at++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (at >= s.Length) throw new FormatException("JSON 转义未完成");
                char e = s[at++];
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
                        if (at + 4 > s.Length) throw new FormatException("JSON \\u 转义不完整");
                        sb.Append((char)Convert.ToInt32(s.Substring(at, 4), 16));
                        at += 4;
                        break;
                    default: throw new FormatException("JSON 未知转义 \\" + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int at)
        {
            int start = at;
            while (at < s.Length && "+-0123456789.eE".IndexOf(s[at]) >= 0) at++;
            string raw = s.Substring(start, at - start);
            long integer;
            if (raw.IndexOf('.') < 0 && raw.IndexOf('e') < 0 && raw.IndexOf('E') < 0
                && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer))
            {
                return integer;
            }
            double real;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out real)) return real;
            throw new FormatException("JSON 数字无法解析：" + raw);
        }

        private static void Expect(string s, ref int at, string literal)
        {
            if (at + literal.Length > s.Length || string.CompareOrdinal(s, at, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("JSON 期望字面量 " + literal);
            }
            at += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int at)
        {
            while (at < s.Length && (s[at] == ' ' || s[at] == '\t' || s[at] == '\r' || s[at] == '\n')) at++;
        }

        // ── 序列化 ─────────────────────────────────────────────

        public static string Write(object value)
        {
            var sb = new StringBuilder(1024);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (value is int || value is long || value is short || value is byte) { sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is float || value is double || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }
            var sequence = value as IEnumerable;
            if (sequence != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in sequence)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string text)
        {
            sb.Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // 控制字符走 \u，其余（含中文）直接输出 UTF-8 原文
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── 取值辅助 ───────────────────────────────────────────

        public static Dictionary<string, object> Object(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static string String(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null) return null;
            return raw as string;
        }

        public static bool Has(Dictionary<string, object> source, string key)
        {
            return source != null && source.ContainsKey(key) && source[key] != null;
        }

        public static int Int(Dictionary<string, object> source, string key, int fallback)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null) return fallback;
            if (raw is long) return (int)(long)raw;
            if (raw is double) return (int)(double)raw;
            return fallback;
        }

        public static int? NullableInt(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null) return null;
            if (raw is long) return (int)(long)raw;
            if (raw is double) return (int)(double)raw;
            return null;
        }

        public static long? NullableLong(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null) return null;
            if (raw is long) return (long)raw;
            if (raw is double) return (long)(double)raw;
            return null;
        }

        public static bool Bool(Dictionary<string, object> source, string key, bool fallback)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null) return fallback;
            return raw is bool ? (bool)raw : fallback;
        }
    }
}
