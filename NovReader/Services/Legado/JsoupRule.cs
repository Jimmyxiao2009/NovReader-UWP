using System;
using System.Collections.Generic;

namespace NovReader.Services.Legado
{
    /// <summary>
    /// Legado 默认（不带 @css: 前缀）规则的单段求值，对应 AnalyzeByJSoup.ElementsSingle：
    ///   class.xxx / tag.xxx / id.xxx / text.xxx / children，其余按 CSS 选择器；
    ///   段尾索引两种写法：旧式 `.0` `.-1` `!0` `.0:2:4`，新式 `[0]` `[-1]` `[1:5:2]` `[!0,3]`。
    /// 和 Jsoup 一样，按类型查找时包含元素自身。索引按每个父元素各自计算。
    /// </summary>
    public static class JsoupRule
    {
        private sealed class Range
        {
            public bool IsRange;
            public int Single;
            public int? Start;
            public int? End;
            public int Step = 1;
        }

        public static List<HtmlNode> SelectSingle(HtmlNode temp, string rule)
        {
            var result = new List<HtmlNode>();
            if (temp == null) return result;

            string before;
            char split;
            var indexes = ParseIndexes((rule ?? "").Trim(), out before, out split);

            List<HtmlNode> elements;
            if (before.Length == 0)
            {
                elements = Children(temp);
            }
            else
            {
                string[] parts = before.Split('.');
                string arg = parts.Length > 1 ? parts[1] : "";
                switch (parts[0])
                {
                    case "children": elements = Children(temp); break;
                    case "class": elements = Collect(temp, delegate (HtmlNode n) { return HasClass(n, arg); }); break;
                    case "tag": elements = Collect(temp, delegate (HtmlNode n) { return String.Equals(n.Name, arg, StringComparison.OrdinalIgnoreCase); }); break;
                    case "id": elements = Collect(temp, delegate (HtmlNode n) { return String.Equals(n.Id, arg, StringComparison.Ordinal); }); break;
                    case "text": elements = Collect(temp, delegate (HtmlNode n) { return Html.OwnText(n).IndexOf(arg, StringComparison.Ordinal) >= 0; }); break;
                    default:
                        elements = new List<HtmlNode>();
                        if (CssSelector.Matches(temp, before)) elements.Add(temp);
                        elements.AddRange(CssSelector.Select(temp, before));
                        break;
                }
            }

            if (indexes == null || split == ' ') return elements;
            return ApplyIndexes(elements, indexes, split);
        }

        private static List<HtmlNode> Children(HtmlNode node)
        {
            var list = new List<HtmlNode>();
            foreach (var child in node.Elements) list.Add(child);
            return list;
        }

        private static List<HtmlNode> Collect(HtmlNode root, Func<HtmlNode, bool> match)
        {
            var list = new List<HtmlNode>();
            foreach (var node in root.AllElements(true))
                if (match(node)) list.Add(node);
            return list;
        }

        private static bool HasClass(HtmlNode node, string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            var classes = node.ClassName.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < classes.Length; i++)
                if (String.Equals(classes[i], name, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---------------------------------------------------------------- 索引

        /// <summary>
        /// 拆出段尾索引。split：'.' 选取、'!' 排除、' ' 无索引。不是索引结构时 before 为原规则。
        /// </summary>
        private static List<Range> ParseIndexes(string rule, out string before, out char split)
        {
            before = rule;
            split = ' ';
            if (rule.Length == 0) return null;

            if (rule[rule.Length - 1] == ']')
            {
                int open = rule.LastIndexOf('[');
                if (open < 0) return null;
                string body = rule.Substring(open + 1, rule.Length - open - 2).Trim();
                char mode = '.';
                if (body.StartsWith("!", StringComparison.Ordinal)) { mode = '!'; body = body.Substring(1).Trim(); }
                var list = new List<Range>();
                foreach (var raw in body.Split(','))
                {
                    var item = ParseBracketItem(raw.Trim());
                    if (item == null) return null; // 是 CSS 属性选择器（如 a[href]），不是索引
                    list.Add(item);
                }
                if (list.Count == 0) return null;
                before = rule.Substring(0, open).Trim();
                split = mode;
                return list;
            }

            // 旧式：从尾部取 [-0-9:]+，前面必须紧跟 '.' 或 '!'
            int i = rule.Length - 1;
            while (i >= 0 && (Char.IsDigit(rule[i]) || rule[i] == '-' || rule[i] == ':')) i--;
            if (i < 0 || i == rule.Length - 1) return null;
            char sep = rule[i];
            if (sep != '.' && sep != '!') return null;

            var singles = new List<Range>();
            foreach (var raw in rule.Substring(i + 1).Split(':'))
            {
                int value;
                if (!Int32.TryParse(raw, out value)) return null;
                singles.Add(new Range { Single = value });
            }
            before = rule.Substring(0, i).Trim();
            split = sep;
            return singles;
        }

        private static Range ParseBracketItem(string text)
        {
            if (text.Length == 0) return null;
            if (text.IndexOf(':') < 0)
            {
                int value;
                return Int32.TryParse(text, out value) ? new Range { Single = value } : null;
            }

            var parts = text.Split(':');
            if (parts.Length > 3) return null;
            var range = new Range { IsRange = true };
            int n;
            if (parts[0].Trim().Length > 0) { if (!Int32.TryParse(parts[0].Trim(), out n)) return null; range.Start = n; }
            if (parts[1].Trim().Length > 0) { if (!Int32.TryParse(parts[1].Trim(), out n)) return null; range.End = n; }
            if (parts.Length == 3 && parts[2].Trim().Length > 0) { if (!Int32.TryParse(parts[2].Trim(), out n)) return null; range.Step = n; }
            return range;
        }

        private static List<HtmlNode> ApplyIndexes(List<HtmlNode> elements, List<Range> indexes, char split)
        {
            int len = elements.Count;
            var picked = new List<int>();
            var seen = new HashSet<int>();
            Action<int> add = delegate (int k) { if (seen.Add(k)) picked.Add(k); };

            foreach (var item in indexes)
            {
                if (!item.IsRange)
                {
                    int k = item.Single;
                    if (k >= 0 && k < len) add(k);
                    else if (k < 0 && len >= -k) add(k + len);
                    continue;
                }

                int start = item.Start ?? 0;
                if (start < 0) start += len;
                int end = item.End ?? (len - 1);
                if (end < 0) end += len;
                if ((start < 0 && end < 0) || (start >= len && end >= len)) continue;
                if (start >= len) start = len - 1; else if (start < 0) start = 0;
                if (end >= len) end = len - 1; else if (end < 0) end = 0;
                if (start == end || item.Step >= len) { add(start); continue; }

                int step = item.Step > 0 ? item.Step : (-item.Step < len ? item.Step + len : 1);
                if (end > start) for (int k = start; k <= end; k += step) add(k);
                else for (int k = start; k >= end; k -= step) add(k);
            }

            var output = new List<HtmlNode>();
            if (split == '!')
            {
                for (int k = 0; k < len; k++) if (!seen.Contains(k)) output.Add(elements[k]);
            }
            else
            {
                for (int k = 0; k < picked.Count; k++) output.Add(elements[picked[k]]);
            }
            return output;
        }
    }
}
