using System;
using System.Collections.Generic;
using System.Text;

namespace NovReader.Services.Legado
{
    /// <summary>
    /// 覆盖常见书源用法的 XPath 子集求值器，直接作用在自建 HTML 树上，
    /// 因此不依赖 System.Xml（UWP 下不可用）。支持：
    /// //tag、/child、*、[@attr]、[@attr='v']、[@attr!='v']、[contains(@attr,'v')]、[n]、
    /// 以及末级的 /text() 与 /@attr。
    /// </summary>
    public static class XPathQuery
    {
        private sealed class Step
        {
            public char Axis;              // 'c' 子节点，'d' 后代
            public string Tag;             // "*" 表示任意标签
            public bool IsText;
            public bool IsAttribute;
            public string Attribute;
            public readonly List<string> Predicates = new List<string>();
        }

        public static List<object> Select(HtmlNode context, string xpath)
        {
            var result = new List<object>();
            if (context == null || String.IsNullOrEmpty(xpath)) return result;

            var steps = Parse(xpath);
            if (steps == null || steps.Count == 0) return result;

            var current = new List<HtmlNode>();
            current.Add(context);

            for (int s = 0; s < steps.Count; s++)
            {
                var step = steps[s];

                if (step.IsText || step.IsAttribute)
                {
                    foreach (var node in current)
                    {
                        if (step.IsAttribute)
                        {
                            string value = node.GetAttribute(step.Attribute);
                            if (value != null) result.Add(value);
                        }
                        else
                        {
                            foreach (var text in Html.TextNodes(node))
                            {
                                string normalized = Html.Normalize(text);
                                if (normalized.Length > 0) result.Add(normalized);
                            }
                        }
                    }
                    return result;
                }

                var next = new List<HtmlNode>();
                foreach (var node in current) Collect(node, step, next);
                current = next;
                if (current.Count == 0) return result;
            }

            foreach (var node in current) result.Add(node);
            return result;
        }

        public static string SelectFirstString(HtmlNode context, string xpath)
        {
            var list = Select(context, xpath);
            for (int i = 0; i < list.Count; i++)
            {
                string value = RuleEngine.ToStringValue(list[i]);
                if (!String.IsNullOrEmpty(value)) return value;
            }
            return "";
        }

        private static void Collect(HtmlNode context, Step step, List<HtmlNode> output)
        {
            var candidates = new List<HtmlNode>();
            if (step.Axis == 'c')
            {
                foreach (var child in context.Elements) candidates.Add(child);
            }
            else
            {
                foreach (var descendant in context.AllElements(false)) candidates.Add(descendant);
            }

            var matched = new List<HtmlNode>();
            for (int i = 0; i < candidates.Count; i++)
                if (MatchesTag(candidates[i], step.Tag)) matched.Add(candidates[i]);

            for (int i = 0; i < step.Predicates.Count; i++)
                matched = ApplyPredicate(matched, step.Predicates[i]);

            output.AddRange(matched);
        }

        private static bool MatchesTag(HtmlNode node, string tag)
        {
            if (tag == "*") return true;
            return String.Equals(node.Name, tag, StringComparison.OrdinalIgnoreCase);
        }

        private static string TextOrAttribute(HtmlNode node, string target)
        {
            if (target == "text()") return Html.OwnText(node);
            if (target == "." || target == "string()" || target == "string(.)") return Html.InnerText(node);
            return node.GetAttribute(target.TrimStart('@'));
        }

        private static List<HtmlNode> ApplyPositional(List<HtmlNode> nodes, string expression)
        {
            int count = nodes.Count;
            var kept = new List<HtmlNode>();

            // 纯数值（不含 position()）：last() / last()-1 表示取那一个
            if (expression.IndexOf("position()", StringComparison.Ordinal) < 0)
            {
                int index;
                if (TryArithmetic(expression, 0, count, out index) && index >= 1 && index <= count)
                    kept.Add(nodes[index - 1]);
                return kept;
            }

            for (int i = 0; i < count; i++)
                if (EvalCondition(expression, i + 1, count)) kept.Add(nodes[i]);
            return kept;
        }

        private static bool EvalCondition(string expression, int position, int last)
        {
            foreach (var orPart in expression.Split(new[] { " or " }, StringSplitOptions.None))
            {
                bool all = true;
                foreach (var andPart in orPart.Split(new[] { " and " }, StringSplitOptions.None))
                {
                    if (!EvalComparison(andPart.Trim(), position, last)) { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        private static readonly string[] ComparisonOps = { "<=", ">=", "!=", "<", ">", "=" };

        private static bool EvalComparison(string text, int position, int last)
        {
            string t = text.Trim();
            if (t.StartsWith("(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal))
                return EvalCondition(t.Substring(1, t.Length - 2), position, last);

            foreach (var op in ComparisonOps)
            {
                int at = t.IndexOf(op, StringComparison.Ordinal);
                if (at <= 0) continue;
                int left, right;
                if (!TryArithmetic(t.Substring(0, at), position, last, out left)) return false;
                if (!TryArithmetic(t.Substring(at + op.Length), position, last, out right)) return false;
                switch (op)
                {
                    case "<=": return left <= right;
                    case ">=": return left >= right;
                    case "!=": return left != right;
                    case "<": return left < right;
                    case ">": return left > right;
                    default: return left == right;
                }
            }

            int value;
            return TryArithmetic(t, position, last, out value) && value == position;
        }

        /// <summary>求值 position() / last() / 整数 之间的加减。</summary>
        private static bool TryArithmetic(string text, int position, int last, out int value)
        {
            value = 0;
            string t = text.Replace(" ", "");
            if (t.Length == 0) return false;
            int i = 0;
            int sign = 1;
            bool any = false;
            while (i < t.Length)
            {
                if (t[i] == '+') { sign = 1; i++; continue; }
                if (t[i] == '-') { sign = -sign; i++; continue; }

                int term;
                if (String.CompareOrdinal(t, i, "position()", 0, 10) == 0) { term = position; i += 10; }
                else if (String.CompareOrdinal(t, i, "last()", 0, 6) == 0) { term = last; i += 6; }
                else if (Char.IsDigit(t[i]))
                {
                    int start = i;
                    while (i < t.Length && Char.IsDigit(t[i])) i++;
                    term = Int32.Parse(t.Substring(start, i - start));
                }
                else return false;

                value += sign * term;
                sign = 1;
                any = true;
            }
            return any;
        }

        private static List<HtmlNode> ApplyPredicate(List<HtmlNode> nodes, string predicate)
        {
            string p = predicate.Trim();
            if (p.Length == 0) return nodes;

            // 位置谓词 [n]
            int position;
            if (Int32.TryParse(p, out position))
            {
                var single = new List<HtmlNode>();
                if (position >= 1 && position <= nodes.Count) single.Add(nodes[position - 1]);
                return single;
            }

            // 位置表达式：[last()] [last()-1] [position()>1 and position()<last()]
            if (p.IndexOf("position()", StringComparison.Ordinal) >= 0 || p.IndexOf("last()", StringComparison.Ordinal) >= 0)
                return ApplyPositional(nodes, p);

            // contains(@attr,'value') / contains(text(),'value') / contains(.,'value')
            if (p.StartsWith("contains(", StringComparison.OrdinalIgnoreCase))
            {
                string inner = TrimParens(p, "contains");
                var args = SplitArguments(inner);
                if (args.Count == 2)
                {
                    string target = args[0].Trim();
                    string want = Unquote(args[1].Trim());
                    var kept = new List<HtmlNode>();
                    foreach (var node in nodes)
                    {
                        string value = TextOrAttribute(node, target);
                        if (value != null && value.IndexOf(want, StringComparison.Ordinal) >= 0) kept.Add(node);
                    }
                    return kept;
                }
                return nodes;
            }

            // text()='值' / .='值'
            if (p.StartsWith("text()", StringComparison.Ordinal) || p.StartsWith(".", StringComparison.Ordinal))
            {
                int eq = p.IndexOf('=');
                if (eq > 0)
                {
                    bool negate = p[eq - 1] == '!';
                    string target = p.Substring(0, negate ? eq - 1 : eq).Trim();
                    string want = Unquote(p.Substring(eq + 1).Trim());
                    var kept = new List<HtmlNode>();
                    foreach (var node in nodes)
                    {
                        bool same = String.Equals((TextOrAttribute(node, target) ?? "").Trim(), want, StringComparison.Ordinal);
                        if (same != negate) kept.Add(node);
                    }
                    return kept;
                }
            }

            // @attr / @attr='v' / @attr!='v'
            if (p.StartsWith("@", StringComparison.Ordinal))
            {
                int neq = p.IndexOf("!=", StringComparison.Ordinal);
                if (neq > 0)
                {
                    string attr = p.Substring(1, neq - 1).Trim();
                    string want = Unquote(p.Substring(neq + 2).Trim());
                    var kept = new List<HtmlNode>();
                    foreach (var node in nodes)
                    {
                        string value = node.GetAttribute(attr);
                        if (value != null && !String.Equals(value, want, StringComparison.Ordinal)) kept.Add(node);
                    }
                    return kept;
                }

                int eq = p.IndexOf('=');
                if (eq > 0)
                {
                    string attr = p.Substring(1, eq - 1).Trim();
                    string want = Unquote(p.Substring(eq + 1).Trim());
                    var kept = new List<HtmlNode>();
                    foreach (var node in nodes)
                    {
                        string value = node.GetAttribute(attr);
                        if (value != null && String.Equals(value, want, StringComparison.Ordinal)) kept.Add(node);
                    }
                    return kept;
                }

                string only = p.Substring(1).Trim();
                var withAttr = new List<HtmlNode>();
                foreach (var node in nodes)
                    if (node.HasAttribute(only)) withAttr.Add(node);
                return withAttr;
            }

            return nodes;
        }

        private static string TrimParens(string text, string name)
        {
            int start = text.IndexOf('(');
            int end = text.LastIndexOf(')');
            if (start < 0 || end <= start) return "";
            return text.Substring(start + 1, end - start - 1);
        }

        private static List<string> SplitArguments(string text)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
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
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == ',') { args.Add(sb.ToString()); sb.Length = 0; continue; }
                sb.Append(c);
            }
            args.Add(sb.ToString());
            return args;
        }

        private static string Unquote(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0])
                return value.Substring(1, value.Length - 2);
            return value;
        }

        // ------------------------------------------------------------ 路径解析

        private static List<Step> Parse(string xpath)
        {
            var steps = new List<Step>();
            string path = xpath.Trim();
            if (path.Length == 0) return steps;

            int i = 0;
            char axis;
            if (path.StartsWith("//", StringComparison.Ordinal)) { axis = 'd'; i = 2; }
            else if (path.StartsWith("/", StringComparison.Ordinal)) { axis = 'c'; i = 1; }
            else axis = 'd';   // 裸表达式按 // 处理

            while (i < path.Length)
            {
                int start = i;
                int depth = 0;
                char quote = '\0';
                while (i < path.Length)
                {
                    char c = path[i];
                    if (quote != '\0') { if (c == quote) quote = '\0'; i++; continue; }
                    if (c == '\'' || c == '"') { quote = c; i++; continue; }
                    if (c == '[') { depth++; i++; continue; }
                    if (c == ']') { if (depth > 0) depth--; i++; continue; }
                    if (c == '/' && depth == 0) break;
                    i++;
                }

                string stepText = path.Substring(start, i - start).Trim();
                if (stepText.Length > 0)
                {
                    var step = ParseStep(stepText);
                    if (step != null) { step.Axis = axis; steps.Add(step); }
                }

                if (i < path.Length && path[i] == '/')
                {
                    if (i + 1 < path.Length && path[i + 1] == '/') { axis = 'd'; i += 2; }
                    else { axis = 'c'; i += 1; }
                }
            }
            return steps;
        }

        private static Step ParseStep(string text)
        {
            var step = new Step();

            if (String.Equals(text, "text()", StringComparison.OrdinalIgnoreCase))
            {
                step.IsText = true;
                return step;
            }
            if (String.Equals(text, "node()", StringComparison.OrdinalIgnoreCase))
            {
                step.Tag = "*";
                return step;
            }
            if (text.StartsWith("@", StringComparison.Ordinal))
            {
                step.IsAttribute = true;
                step.Attribute = text.Substring(1).Trim();
                return step;
            }

            int bracket = text.IndexOf('[');
            string namePart = bracket < 0 ? text : text.Substring(0, bracket);
            step.Tag = namePart.Trim();
            if (step.Tag.Length == 0) step.Tag = "*";

            if (bracket >= 0)
            {
                int i = bracket;
                while (i < text.Length)
                {
                    if (text[i] == '[')
                    {
                        int end = FindClose(text, i);
                        if (end < 0) break;
                        step.Predicates.Add(text.Substring(i + 1, end - i - 1));
                        i = end + 1;
                    }
                    else i++;
                }
            }
            return step;
        }

        private static int FindClose(string text, int open)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
