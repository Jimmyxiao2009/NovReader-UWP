using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace NovReader.Services.Legado
{
    /// <summary>轻量 HTML 节点。元素节点 Name 为小写标签名，文本节点 Name 为 "#text"。</summary>
    public sealed class HtmlNode
    {
        public const string TextName = "#text";

        private readonly Dictionary<string, string> _attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<HtmlNode> _children = new List<HtmlNode>();

        public HtmlNode(string name) { Name = name ?? ""; }

        public string Name { get; private set; }
        public HtmlNode Parent { get; internal set; }
        /// <summary>文本节点内容。</summary>
        public string TextValue { get; set; }

        public bool IsText { get { return Name == TextName; } }
        public bool IsElement { get { return Name != TextName; } }
        public IList<HtmlNode> Children { get { return _children; } }
        public IDictionary<string, string> Attributes { get { return _attrs; } }

        public void Add(HtmlNode child)
        {
            if (child == null) return;
            child.Parent = this;
            _children.Add(child);
        }

        public void SetAttribute(string name, string value)
        {
            if (String.IsNullOrEmpty(name)) return;
            _attrs[name.Trim()] = value ?? "";
        }

        public string GetAttribute(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;
            string v;
            return _attrs.TryGetValue(name.Trim(), out v) ? v : null;
        }

        public bool HasAttribute(string name)
        {
            return !String.IsNullOrEmpty(name) && _attrs.ContainsKey(name.Trim());
        }

        public string ClassName { get { return GetAttribute("class") ?? ""; } }
        public string Id { get { return GetAttribute("id") ?? ""; } }

        /// <summary>直接子元素。</summary>
        public IEnumerable<HtmlNode> Elements
        {
            get
            {
                for (int i = 0; i < _children.Count; i++)
                    if (_children[i].IsElement) yield return _children[i];
            }
        }

        /// <summary>自身（可选）及全部后代元素，按文档顺序。</summary>
        public IEnumerable<HtmlNode> AllElements(bool includeSelf)
        {
            if (includeSelf && IsElement) yield return this;
            for (int i = 0; i < _children.Count; i++)
            {
                var c = _children[i];
                if (!c.IsElement) continue;
                yield return c;
                foreach (var d in c.AllElements(false)) yield return d;
            }
        }

        public int ElementIndexInParent()
        {
            if (Parent == null) return 0;
            int n = 0;
            foreach (var e in Parent.Elements)
            {
                if (ReferenceEquals(e, this)) return n;
                n++;
            }
            return n;
        }

        public int ElementCountInParent()
        {
            if (Parent == null) return 1;
            int n = 0;
            foreach (var e in Parent.Elements) n++;
            return n;
        }
    }

    /// <summary>文本、属性与序列化相关的辅助方法。</summary>
    public static class Html
    {
        /// <summary>空白折叠并去首尾空格（含 &amp;nbsp;）。</summary>
        public static string Normalize(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool pendingSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (IsSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\u00A0';
        }

        private static void CollectText(HtmlNode node, StringBuilder sb)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var c = node.Children[i];
                if (c.IsText) sb.Append(c.TextValue);
                else if (c.Name == "script" || c.Name == "style") continue;
                else CollectText(c, sb);
            }
        }

        /// <summary>全部后代文本，空白折叠。</summary>
        public static string InnerText(HtmlNode node)
        {
            if (node == null) return "";
            if (node.IsText) return Normalize(node.TextValue);
            var sb = new StringBuilder();
            CollectText(node, sb);
            return Normalize(sb.ToString());
        }

        /// <summary>仅本节点直属文本，不含子元素文本。</summary>
        public static string OwnText(HtmlNode node)
        {
            if (node == null) return "";
            if (node.IsText) return Normalize(node.TextValue);
            var sb = new StringBuilder();
            for (int i = 0; i < node.Children.Count; i++)
            {
                var c = node.Children[i];
                if (c.IsText) { sb.Append(c.TextValue); sb.Append(' '); }
            }
            return Normalize(sb.ToString());
        }

        /// <summary>直属文本节点列表（未经折叠）。</summary>
        public static List<string> TextNodes(HtmlNode node)
        {
            var list = new List<string>();
            if (node == null) return list;
            if (node.IsText) { list.Add(node.TextValue ?? ""); return list; }
            for (int i = 0; i < node.Children.Count; i++)
                if (node.Children[i].IsText) list.Add(node.Children[i].TextValue ?? "");
            return list;
        }

        public static string InnerHtml(HtmlNode node)
        {
            if (node == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < node.Children.Count; i++) WriteNode(sb, node.Children[i]);
            return sb.ToString();
        }

        public static string OuterHtml(HtmlNode node)
        {
            if (node == null) return "";
            var sb = new StringBuilder();
            WriteNode(sb, node);
            return sb.ToString();
        }

        private static readonly HashSet<string> VoidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

        private static void WriteNode(StringBuilder sb, HtmlNode node)
        {
            if (node.IsText) { sb.Append(EscapeText(node.TextValue)); return; }
            sb.Append('<').Append(node.Name);
            foreach (var a in node.Attributes)
            {
                sb.Append(' ').Append(a.Key).Append("=\"").Append(EscapeAttribute(a.Value)).Append('"');
            }
            if (VoidTags.Contains(node.Name) && node.Children.Count == 0) { sb.Append(" />"); return; }
            sb.Append('>');
            for (int i = 0; i < node.Children.Count; i++) WriteNode(sb, node.Children[i]);
            sb.Append("</").Append(node.Name).Append('>');
        }

        public static string EscapeText(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public static string EscapeAttribute(string s)
        {
            if (String.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static readonly HashSet<string> LineTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "section",
          "article", "dd", "dt", "td", "table", "ul", "ol", "dl", "form" };

        /// <summary>按 DOM 结构提取纯文本：块级标签换行，跳过 script/style。</summary>
        public static string ToPlainText(HtmlNode node)
        {
            if (node == null) return "";
            var sb = new StringBuilder();
            AppendText(node, sb);
            return CleanLines(sb.ToString());
        }

        private static void AppendText(HtmlNode node, StringBuilder sb)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                var c = node.Children[i];
                if (c.IsText) { sb.Append(c.TextValue); continue; }
                if (c.Name == "script" || c.Name == "style") continue;
                if (c.Name == "br") { sb.Append('\n'); continue; }
                bool line = LineTags.Contains(c.Name);
                if (line) sb.Append('\n');
                AppendText(c, sb);
                if (line) sb.Append('\n');
            }
        }

        private static string CleanLines(string text)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string t = Normalize(lines[i]);
                if (t.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>把 HTML 片段转成纯文本：块级标签换行，其余标签剔除，实体解码。</summary>
        public static string ToPlainText(string html)
        {
            if (String.IsNullOrEmpty(html)) return "";
            if (html.IndexOf('<') < 0) return DecodeEntities(html);

            html = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);

            var sb = new StringBuilder(html.Length);
            int i = 0;
            while (i < html.Length)
            {
                char c = html[i];
                if (c == '<')
                {
                    int end = html.IndexOf('>', i);
                    if (end < 0) break;
                    string tag = html.Substring(i + 1, end - i - 1).Trim().ToLowerInvariant();
                    bool closing = tag.StartsWith("/", StringComparison.Ordinal);
                    string bare = closing ? tag.Substring(1) : tag;
                    int sp = bare.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '/' });
                    if (sp > 0) bare = bare.Substring(0, sp);
                    if (bare == "br" || bare == "p" || bare == "div" || bare == "li" ||
                        bare == "tr" || bare == "h1" || bare == "h2" || bare == "h3")
                        sb.Append('\n');
                    i = end + 1;
                    continue;
                }
                int next = html.IndexOf('<', i);
                if (next < 0) next = html.Length;
                sb.Append(DecodeEntities(html.Substring(i, next - i)));
                i = next;
            }
            // 逐行折叠空白，保留换行结构
            return CleanLines(sb.ToString());
        }

        public static string DecodeEntities(string s)
        {
            if (String.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '&') { sb.Append(c); i++; continue; }
                int semi = s.IndexOf(';', i + 1);
                if (semi < 0 || semi - i > 12) { sb.Append(c); i++; continue; }
                string name = s.Substring(i + 1, semi - i - 1);
                string rep = DecodeOne(name);
                if (rep == null) { sb.Append(c); i++; continue; }
                sb.Append(rep);
                i = semi + 1;
            }
            return sb.ToString();
        }

        private static string DecodeOne(string name)
        {
            if (name.Length == 0) return null;
            if (name[0] == '#')
            {
                int code;
                try
                {
                    if (name.Length > 1 && (name[1] == 'x' || name[1] == 'X'))
                        code = Convert.ToInt32(name.Substring(2), 16);
                    else
                        code = Int32.Parse(name.Substring(1));
                }
                catch (Exception) { return null; }
                if (code <= 0) return null;
                try { return Char.ConvertFromUtf32(code); }
                catch (Exception) { return null; }
            }
            switch (name.ToLowerInvariant())
            {
                case "amp": return "&";
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
                case "nbsp": return "\u00A0";
                case "ensp": return " ";
                case "emsp": return " ";
                case "copy": return "©";
                case "reg": return "®";
                case "trade": return "™";
                case "hellip": return "…";
                case "mdash": return "—";
                case "ndash": return "–";
                case "lsquo": return "‘";
                case "rsquo": return "’";
                case "ldquo": return "“";
                case "rdquo": return "”";
                case "middot": return "·";
                case "bull": return "·";
                case "times": return "×";
                case "divide": return "÷";
                case "sect": return "§";
                case "para": return "¶";
                case "deg": return "°";
                case "laquo": return "«";
                case "raquo": return "»";
                default: return null;
            }
        }
    }

    /// <summary>容错 HTML 解析器：自动补闭合、识别空元素与 raw text 元素。</summary>
    public static class HtmlParser
    {
        private static readonly HashSet<string> VoidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

        private static readonly HashSet<string> RawTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "textarea", "title" };

        private static readonly HashSet<string> AutoClose = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "li", "p", "dt", "dd", "tr", "td", "th", "option", "thead", "tbody", "tfoot", "optgroup", "colgroup" };

        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "div", "p", "ul", "ol", "table", "tr", "td", "th", "li", "dl", "dt", "dd", "h1", "h2", "h3",
          "h4", "h5", "h6", "blockquote", "pre", "section", "article", "header", "footer", "nav", "form" };

        public static HtmlNode Parse(string html)
        {
            var root = new HtmlNode("#root");
            if (String.IsNullOrEmpty(html)) return root;

            var open = new List<HtmlNode>();
            open.Add(root);
            HtmlNode current = root;
            int i = 0, n = html.Length;

            while (i < n)
            {
                char c = html[i];
                if (c != '<')
                {
                    int next = html.IndexOf('<', i);
                    if (next < 0) next = n;
                    AddText(current, html.Substring(i, next - i));
                    i = next;
                    continue;
                }

                if (i + 1 >= n) { AddText(current, "<"); break; }
                char c1 = html[i + 1];

                if (c1 == '!')
                {
                    if (i + 3 < n && html[i + 2] == '-' && html[i + 3] == '-')
                    {
                        int e = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        i = e < 0 ? n : e + 3;
                        continue;
                    }
                    int e2 = html.IndexOf('>', i);
                    i = e2 < 0 ? n : e2 + 1;
                    continue;
                }

                if (c1 == '?')
                {
                    int e = html.IndexOf('>', i);
                    i = e < 0 ? n : e + 1;
                    continue;
                }

                if (c1 == '/')
                {
                    int e = html.IndexOf('>', i);
                    if (e < 0) { i = n; break; }
                    string closeName = ReadName(html, i + 2, e);
                    for (int k = open.Count - 1; k >= 1; k--)
                    {
                        if (open[k].Name == closeName) { open.RemoveRange(k, open.Count - k); break; }
                    }
                    current = open[open.Count - 1];
                    i = e + 1;
                    continue;
                }

                if (!Char.IsLetter(c1))
                {
                    AddText(current, "<");
                    i++;
                    continue;
                }

                int tagEnd = FindTagEnd(html, i + 1);
                if (tagEnd < 0)
                {
                    AddText(current, html.Substring(i));
                    break;
                }

                string raw = html.Substring(i + 1, tagEnd - i - 1);
                bool selfClose = raw.EndsWith("/", StringComparison.Ordinal);
                if (selfClose) raw = raw.Substring(0, raw.Length - 1);

                string name = ReadTagName(raw);
                var node = new HtmlNode(name);
                ParseAttributes(raw, name.Length, node);

                if (AutoClose.Contains(name) && open.Count > 1 && open[open.Count - 1].Name == name)
                {
                    open.RemoveAt(open.Count - 1);
                    current = open[open.Count - 1];
                }
                else if (BlockTags.Contains(name) && open.Count > 1 && open[open.Count - 1].Name == "p")
                {
                    open.RemoveAt(open.Count - 1);
                    current = open[open.Count - 1];
                }

                current.Add(node);
                i = tagEnd + 1;

                if (selfClose || VoidTags.Contains(name)) continue;

                if (RawTags.Contains(name))
                {
                    string closeTag = "</" + name;
                    int e = IndexOfIgnoreCase(html, closeTag, i);
                    if (e < 0)
                    {
                        AddText(node, html.Substring(i));
                        i = n;
                    }
                    else
                    {
                        AddText(node, html.Substring(i, e - i));
                        int e2 = html.IndexOf('>', e);
                        i = e2 < 0 ? n : e2 + 1;
                    }
                    continue;
                }

                open.Add(node);
                current = node;
            }

            return root;
        }

        private static void AddText(HtmlNode parent, string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            parent.Add(new HtmlNode(HtmlNode.TextName) { TextValue = Html.DecodeEntities(text) });
        }

        private static int FindTagEnd(string s, int start)
        {
            char quote = '\0';
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '>') return i;
            }
            return -1;
        }

        private static int IndexOfIgnoreCase(string s, string value, int start)
        {
            return s.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadName(string s, int start, int end)
        {
            int i = start;
            while (i < end && Char.IsWhiteSpace(s[i])) i++;
            int nameStart = i;
            while (i < end && !Char.IsWhiteSpace(s[i])) i++;
            return s.Substring(nameStart, i - nameStart).ToLowerInvariant();
        }

        private static string ReadTagName(string raw)
        {
            int i = 0;
            while (i < raw.Length && !Char.IsWhiteSpace(raw[i]) && raw[i] != '/') i++;
            return raw.Substring(0, i).ToLowerInvariant();
        }

        private static void ParseAttributes(string raw, int start, HtmlNode node)
        {
            int i = start;
            while (i < raw.Length)
            {
                while (i < raw.Length && (Char.IsWhiteSpace(raw[i]) || raw[i] == '/')) i++;
                if (i >= raw.Length) break;

                int nameStart = i;
                while (i < raw.Length && !Char.IsWhiteSpace(raw[i]) && raw[i] != '=' && raw[i] != '/') i++;
                string name = raw.Substring(nameStart, i - nameStart);
                if (name.Length == 0) { i++; continue; }

                while (i < raw.Length && Char.IsWhiteSpace(raw[i])) i++;
                string value = "";
                if (i < raw.Length && raw[i] == '=')
                {
                    i++;
                    while (i < raw.Length && Char.IsWhiteSpace(raw[i])) i++;
                    if (i < raw.Length && (raw[i] == '"' || raw[i] == '\''))
                    {
                        char q = raw[i++];
                        int vs = i;
                        while (i < raw.Length && raw[i] != q) i++;
                        value = raw.Substring(vs, i - vs);
                        if (i < raw.Length) i++;
                    }
                    else
                    {
                        int vs = i;
                        while (i < raw.Length && !Char.IsWhiteSpace(raw[i])) i++;
                        value = raw.Substring(vs, i - vs);
                    }
                }
                node.SetAttribute(name, Html.DecodeEntities(value));
            }
        }
    }
}
