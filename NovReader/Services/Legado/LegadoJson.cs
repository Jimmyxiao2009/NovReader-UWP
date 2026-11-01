using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NovReader.Services.Legado
{
    public enum JsonKind { Null, Boolean, Number, String, Array, Object }

    /// <summary>
    /// 极简 JSON 文档模型。自带解析器，不依赖 Windows.Data.Json，
    /// 以便书源引擎可以在普通桌面程序里直接编译和测试。
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind = JsonKind.Null;
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonValue> Items;                                  // Array
        public List<KeyValuePair<string, JsonValue>> Members;          // Object

        public static JsonValue Null() { return new JsonValue { Kind = JsonKind.Null }; }

        public static JsonValue NewObject()
        {
            return new JsonValue { Kind = JsonKind.Object, Members = new List<KeyValuePair<string, JsonValue>>() };
        }

        public static JsonValue NewArray()
        {
            return new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
        }

        public bool IsNull { get { return Kind == JsonKind.Null; } }

        public int Count
        {
            get
            {
                if (Kind == JsonKind.Array && Items != null) return Items.Count;
                if (Kind == JsonKind.Object && Members != null) return Members.Count;
                return 0;
            }
        }

        /// <summary>按名称取对象成员，不存在时返回 null。</summary>
        public JsonValue Get(string name)
        {
            if (Kind != JsonKind.Object || Members == null || name == null) return null;
            for (int i = 0; i < Members.Count; i++)
                if (String.Equals(Members[i].Key, name, StringComparison.Ordinal)) return Members[i].Value;
            return null;
        }

        public JsonValue At(int index)
        {
            if (Kind != JsonKind.Array || Items == null) return null;
            if (index < 0 || index >= Items.Count) return null;
            return Items[index];
        }

        public void Add(string name, JsonValue value)
        {
            if (Kind != JsonKind.Object) { Kind = JsonKind.Object; Members = new List<KeyValuePair<string, JsonValue>>(); }
            Members.Add(new KeyValuePair<string, JsonValue>(name ?? "", value ?? Null()));
        }

        /// <summary>转换为文本：字符串原样返回，数字/布尔取字面量，容器取 JSON 文本。</summary>
        public string AsString()
        {
            switch (Kind)
            {
                case JsonKind.String: return Str ?? "";
                case JsonKind.Number:
                    if (Math.Abs(Number - Math.Round(Number)) < 1e-9 && Math.Abs(Number) < 1e15)
                        return ((long)Math.Round(Number)).ToString(CultureInfo.InvariantCulture);
                    return Number.ToString(CultureInfo.InvariantCulture);
                case JsonKind.Boolean: return Bool ? "true" : "false";
                case JsonKind.Null: return "";
                default: return ToJson();
            }
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); return;
                case JsonKind.Boolean: sb.Append(Bool ? "true" : "false"); return;
                case JsonKind.Number: sb.Append(AsString()); return;
                case JsonKind.String: WriteString(sb, Str); return;
                case JsonKind.Array:
                    sb.Append('[');
                    if (Items != null)
                        for (int i = 0; i < Items.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            if (Items[i] == null) sb.Append("null"); else Items[i].Write(sb);
                        }
                    sb.Append(']');
                    return;
                default:
                    sb.Append('{');
                    if (Members != null)
                        for (int i = 0; i < Members.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            WriteString(sb, Members[i].Key);
                            sb.Append(':');
                            if (Members[i].Value == null) sb.Append("null"); else Members[i].Value.Write(sb);
                        }
                    sb.Append('}');
                    return;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            sb.Append('"');
        }
    }

    /// <summary>容错 JSON 解析器，失败时返回 null。</summary>
    public static class LegadoJson
    {
        public static JsonValue Parse(string text)
        {
            if (String.IsNullOrEmpty(text)) return null;
            try
            {
                int i = 0;
                var v = ParseValue(text, ref i);
                SkipWs(text, ref i);
                return v;
            }
            catch (Exception) { return null; }
        }

        /// <summary>从可能混有前缀文本的响应里提取第一个 JSON 对象或数组，失败返回 null。</summary>
        public static JsonValue ParseLoose(string text)
        {
            var direct = Parse(text);
            if (direct != null) return direct;
            if (String.IsNullOrEmpty(text)) return null;
            int start = -1;
            char open = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{' || text[i] == '[') { start = i; open = text[i]; break; }
            }
            if (start < 0) return null;
            char close = open == '{' ? '}' : ']';
            int end = text.LastIndexOf(close);
            if (end <= start) return null;
            return Parse(text.Substring(start, end - start + 1));
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return new JsonValue { Kind = JsonKind.String, Str = ParseString(s, ref i) };
            if (c == 't') { Expect(s, ref i, "true"); return new JsonValue { Kind = JsonKind.Boolean, Bool = true }; }
            if (c == 'f') { Expect(s, ref i, "false"); return new JsonValue { Kind = JsonKind.Boolean, Bool = false }; }
            if (c == 'n') { Expect(s, ref i, "null"); return JsonValue.Null(); }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || String.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("非法字面量");
            i += word.Length;
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            var obj = JsonValue.NewObject();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("对象未闭合");
                if (s[i] != '"') throw new FormatException("键必须为字符串");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("缺少冒号");
                i++;
                obj.Members.Add(new KeyValuePair<string, JsonValue>(key, ParseValue(s, ref i)));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return obj; }
                throw new FormatException("对象分隔符错误");
            }
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            var arr = JsonValue.NewArray();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            while (true)
            {
                arr.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return arr; }
                throw new FormatException("数组分隔符错误");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                        if (i + 4 <= s.Length)
                        {
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("字符串未闭合");
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i == start) throw new FormatException("非法值");
            double d;
            if (!Double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("非法数字");
            return new JsonValue { Kind = JsonKind.Number, Number = d };
        }
    }
}
