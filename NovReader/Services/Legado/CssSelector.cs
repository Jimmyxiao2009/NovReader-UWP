using System;
using System.Collections.Generic;

namespace NovReader.Services.Legado
{
    /// <summary>
    /// 精简 CSS 选择器引擎。支持 tag / #id / .class / [attr] / [attr=val] / [attr^=] / [attr$=] / [attr*=]，
    /// 后代与子代组合符（空格、&gt;），逗号分组，以及 :first-child :last-child :nth-child :contains :eq :lt :gt。
    /// </summary>
    public static class CssSelector
    {
        private sealed class Compound
        {
            public string Tag;                                  // null 表示任意标签
            public string Id;
            public readonly List<string> Classes = new List<string>();
            public readonly List<string[]> Attrs = new List<string[]>();    // {name, op, value}
            public readonly List<string[]> Pseudos = new List<string[]>();  // {name, arg}
        }

        private sealed class Part
        {
            public char Combinator;         // ' ' 后代，'>' 子代
            public Compound Node;
        }

        public static List<HtmlNode> Select(HtmlNode context, string selector)
        {
            var result = new List<HtmlNode>();
            if (context == null || String.IsNullOrEmpty(selector)) return result;

            foreach (var groupText in SplitTopLevel(selector, ','))
            {
                string g = groupText.Trim();
                if (g.Length == 0) continue;
                var parts = ParseGroup(g);
                if (parts == null || parts.Count == 0) continue;

                var matched = new List<HtmlNode>();
                foreach (var node in context.AllElements(false))
                {
                    if (MatchFrom(node, parts, parts.Count - 1)) matched.Add(node);
                }

                matched = ApplyListPseudos(matched, parts[parts.Count - 1].Node.Pseudos);
                for (int i = 0; i < matched.Count; i++)
                    if (!ReferenceIn(result, matched[i])) result.Add(matched[i]);
            }
            return result;
        }

        /// <summary>节点自身是否匹配选择器（不含 :eq 等列表级伪类）。</summary>
        public static bool Matches(HtmlNode node, string selector)
        {
            if (node == null || !node.IsElement || String.IsNullOrEmpty(selector)) return false;
            foreach (var groupText in SplitTopLevel(selector, ','))
            {
                string g = groupText.Trim();
                if (g.Length == 0) continue;
                var parts = ParseGroup(g);
                if (parts == null || parts.Count == 0) continue;
                if (MatchFrom(node, parts, parts.Count - 1)) return true;
            }
            return false;
        }

        public static HtmlNode SelectFirst(HtmlNode context, string selector)
        {
            var list = Select(context, selector);
            return list.Count == 0 ? null : list[0];
        }

        private static bool ReferenceIn(List<HtmlNode> list, HtmlNode node)
        {
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], node)) return true;
            return false;
        }

        private static bool MatchFrom(HtmlNode node, List<Part> parts, int index)
        {
            if (!MatchCompound(node, parts[index].Node)) return false;
            if (index == 0) return true;

            char combinator = parts[index].Combinator;
            var parent = node.Parent;

            if (combinator == '>')
                return parent != null && parent.IsElement && MatchFrom(parent, parts, index - 1);

            while (parent != null && parent.IsElement)
            {
                if (MatchFrom(parent, parts, index - 1)) return true;
                parent = parent.Parent;
            }
            return false;
        }

        private static bool MatchCompound(HtmlNode node, Compound c)
        {
            if (node == null || !node.IsElement) return false;
            if (c.Tag != null && !String.Equals(node.Name, c.Tag, StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.IsNullOrEmpty(c.Id) && !String.Equals(node.Id, c.Id, StringComparison.Ordinal)) return false;

            if (c.Classes.Count > 0)
            {
                var classes = node.ClassName.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < c.Classes.Count; i++)
                {
                    bool found = false;
                    for (int j = 0; j < classes.Length; j++)
                        if (String.Equals(classes[j], c.Classes[i], StringComparison.Ordinal)) { found = true; break; }
                    if (!found) return false;
                }
            }

            for (int i = 0; i < c.Attrs.Count; i++)
            {
                var spec = c.Attrs[i];
                string actual = node.GetAttribute(spec[0]);
                if (actual == null) return false;
                string op = spec[1];
                string expected = spec[2];
                if (op == "=" && !String.Equals(actual, expected, StringComparison.Ordinal)) return false;
                if (op == "^=" && !actual.StartsWith(expected, StringComparison.Ordinal)) return false;
                if (op == "$=" && !actual.EndsWith(expected, StringComparison.Ordinal)) return false;
                if (op == "*=" && actual.IndexOf(expected, StringComparison.Ordinal) < 0) return false;
                if (op == "~=")
                {
                    var words = actual.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool found = false;
                    for (int j = 0; j < words.Length; j++)
                        if (String.Equals(words[j], expected, StringComparison.Ordinal)) { found = true; break; }
                    if (!found) return false;
                }
            }

            for (int i = 0; i < c.Pseudos.Count; i++)
            {
                var p = c.Pseudos[i];
                switch (p[0])
                {
                    case "first-child":
                        if (node.ElementIndexInParent() != 0) return false;
                        break;
                    case "last-child":
                        if (node.ElementIndexInParent() != node.ElementCountInParent() - 1) return false;
                        break;
                    case "nth-child":
                        {
                            int want;
                            if (!Int32.TryParse(p[1], out want)) return false;
                            if (node.ElementIndexInParent() + 1 != want) return false;
                        }
                        break;
                    case "contains":
                        if (Html.InnerText(node).IndexOf(p[1], StringComparison.Ordinal) < 0) return false;
                        break;
                    case "empty":
                        if (node.Children.Count != 0) return false;
                        break;
                    // eq/lt/gt/first/last 是列表级伪类，在 ApplyListPseudos 中处理
                }
            }
            return true;
        }

        private static List<HtmlNode> ApplyListPseudos(List<HtmlNode> list, List<string[]> pseudos)
        {
            for (int i = 0; i < pseudos.Count; i++)
            {
                string name = pseudos[i][0];
                string arg = pseudos[i][1];
                if (name == "first") { name = "eq"; arg = "0"; }
                else if (name == "last") { name = "eq"; arg = "-1"; }

                if (name == "eq")
                {
                    int n;
                    if (!Int32.TryParse(arg, out n)) continue;
                    if (n < 0) n += list.Count;
                    var single = new List<HtmlNode>();
                    if (n >= 0 && n < list.Count) single.Add(list[n]);
                    list = single;
                }
                else if (name == "lt" || name == "gt")
                {
                    int n;
                    if (!Int32.TryParse(arg, out n)) continue;
                    var kept = new List<HtmlNode>();
                    for (int k = 0; k < list.Count; k++)
                    {
                        bool keep = name == "lt" ? k < n : k > n;
                        if (keep) kept.Add(list[k]);
                    }
                    list = kept;
                }
            }
            return list;
        }

        private static List<Part> ParseGroup(string text)
        {
            var parts = new List<Part>();
            char pending = '\0';
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (Html.IsSpace(c))
                {
                    while (i < text.Length && Html.IsSpace(text[i])) i++;
                    if (i < text.Length && (text[i] == '>' || text[i] == '+' || text[i] == '~')) continue;
                    if (i < text.Length && pending == '\0') pending = ' ';
                    continue;
                }

                if (c == '>' || c == '+' || c == '~')
                {
                    pending = c == '>' ? '>' : ' ';
                    i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && !Html.IsSpace(text[i]) && text[i] != '>' && text[i] != '+' && text[i] != '~') i++;
                var compound = ParseCompound(text.Substring(start, i - start));
                if (compound == null) return null;

                parts.Add(new Part
                {
                    Combinator = parts.Count == 0 ? ' ' : (pending == '\0' ? ' ' : pending),
                    Node = compound
                });
                pending = '\0';
            }
            return parts;
        }

        private static Compound ParseCompound(string text)
        {
            var c = new Compound();
            int i = 0;

            if (i < text.Length && text[i] == '*') i++;
            else
            {
                int s = i;
                while (i < text.Length && IsNameChar(text[i])) i++;
                if (i > s) c.Tag = text.Substring(s, i - s).ToLowerInvariant();
            }

            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '#')
                {
                    i++;
                    int s = i;
                    while (i < text.Length && IsNameChar(text[i])) i++;
                    c.Id = text.Substring(s, i - s);
                }
                else if (ch == '.')
                {
                    i++;
                    int s = i;
                    while (i < text.Length && IsNameChar(text[i])) i++;
                    c.Classes.Add(text.Substring(s, i - s));
                }
                else if (ch == '[')
                {
                    int e = text.IndexOf(']', i);
                    if (e < 0) return null;
                    var spec = ParseAttrSpec(text.Substring(i + 1, e - i - 1).Trim());
                    if (spec != null) c.Attrs.Add(spec);
                    i = e + 1;
                }
                else if (ch == ':')
                {
                    i++;
                    int s = i;
                    while (i < text.Length && IsNameChar(text[i])) i++;
                    string name = text.Substring(s, i - s).ToLowerInvariant();
                    string arg = "";
                    if (i < text.Length && text[i] == '(')
                    {
                        int e = text.IndexOf(')', i);
                        if (e < 0) return null;
                        arg = text.Substring(i + 1, e - i - 1).Trim().Trim('"', '\'');
                        i = e + 1;
                    }
                    c.Pseudos.Add(new[] { name, arg });
                }
                else i++;
            }
            return c;
        }

        private static string[] ParseAttrSpec(string body)
        {
            if (String.IsNullOrEmpty(body)) return null;
            string[] ops = { "^=", "$=", "*=", "~=", "=" };
            foreach (var op in ops)
            {
                int idx = body.IndexOf(op, StringComparison.Ordinal);
                if (idx < 0) continue;
                string name = body.Substring(0, idx).Trim();
                string value = body.Substring(idx + op.Length).Trim().Trim('"', '\'');
                if (name.Length == 0) return null;
                return new[] { name, op, value };
            }
            return new[] { body, "exists", "" };
        }

        private static bool IsNameChar(char c)
        {
            return Char.IsLetterOrDigit(c) || c == '-' || c == '_';
        }

        /// <summary>按顶层分隔符切分，忽略引号与方括号内部的分隔符。</summary>
        private static List<string> SplitTopLevel(string text, char separator)
        {
            var list = new List<string>();
            var sb = new System.Text.StringBuilder();
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); continue; }
                if (c == '[' || c == '(') depth++;
                else if (c == ']' || c == ')') depth--;
                if (c == separator && depth <= 0)
                {
                    list.Add(sb.ToString());
                    sb.Length = 0;
                    continue;
                }
                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }
    }
}
