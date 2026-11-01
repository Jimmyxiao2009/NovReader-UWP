using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NovReader.Services.Legado.Js;

namespace NovReader.Services.Legado
{
    /// <summary>
    /// Legado（阅读）风格的书源规则求值器。
    /// 支持：@css: / @XPath: / @json: 前缀；裸 JSONPath（$）与裸 XPath（//）；
    /// 内容规则 text / ownText / textNodes / html / all / href / src / 属性名 / 子选择器；
    /// 正则 ##pattern##replacement；列表运算符 || 、&amp;&amp; 、%%；列表反转前缀 -。
    /// 不支持：@js: 与 &lt;js&gt; 脚本块（会记录到 Warnings）。
    /// </summary>
    public static class RuleEngine
    {
        private static readonly List<string> _warnings = new List<string>();
        private static readonly object _warnLock = new object();

        public static List<string> Warnings
        {
            get { lock (_warnLock) return new List<string>(_warnings); }
        }

        public static void ResetWarnings()
        {
            lock (_warnLock) _warnings.Clear();
        }

        /// <summary>供引擎记录非规则来源的警告（例如无法求值的 JS 表达式）。</summary>
        public static void AddWarning(string message)
        {
            Warn(message);
        }

        private static void Warn(string message)
        {
            lock (_warnLock)
            {
                if (_warnings.Count >= 50) return;
                if (_warnings.Contains(message)) return;
                _warnings.Add(message);
            }
        }

        // ---------------------------------------------------------------- 公开入口

        public static List<object> Eval(string rule, object context)
        {
            var result = new List<object>();
            try
            {
                EvalInto(rule, context, result);
            }
            catch (Exception ex)
            {
                Warn("规则求值异常：" + rule + " -> " + ex.Message);
            }
            return result;
        }

        public static List<HtmlNode> SelectNodes(string rule, object context)
        {
            var nodes = new List<HtmlNode>();
            foreach (var item in Eval(rule, context))
            {
                var node = item as HtmlNode;
                if (node != null) nodes.Add(node);
            }
            return nodes;
        }

        public static List<string> SelectStrings(string rule, object context)
        {
            var strings = new List<string>();
            foreach (var item in Eval(rule, context)) strings.Add(ToStringValue(item));
            return strings;
        }

        public static string SelectString(string rule, object context)
        {
            var list = Eval(rule, context);
            for (int i = 0; i < list.Count; i++)
            {
                string s = ToStringValue(list[i]);
                if (!String.IsNullOrEmpty(s)) return s;
            }
            return "";
        }

        public static string ToStringValue(object item)
        {
            if (item == null) return "";
            var node = item as HtmlNode;
            if (node != null) return Html.InnerText(node);
            var json = item as JsonValue;
            if (json != null) return json.AsString();
            return item.ToString();
        }

        public static HtmlNode ParseHtml(string html)
        {
            return HtmlParser.Parse(html ?? "");
        }

        // ---------------------------------------------------------------- 求值主体

        private static void EvalInto(string rule, object context, List<object> output)
        {
            // Legado 的 getString/getElements 对空规则一律返回空：
            //   if (TextUtils.isEmpty(ruleStr)) return ""
            // 所以 ruleBookInfo 里没写的字段必须保持搜索页取到的值，
            // 不能回落成“整个文档的文本”，否则书名/作者后面会粘上一整页正文。
            if (String.IsNullOrEmpty(rule)) return;

            string text = rule.Trim();
            if (text.Length == 0) return;

            // JS 规则（<js>…</js> / @js:…）：交给 JS 运行环境，未启用时按“规则为空”处理并记警告
            if (text.StartsWith("@js:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("<js>", StringComparison.OrdinalIgnoreCase))
            {
                JsEvalInto(text, context, output);
                return;
            }

            // 模板规则：/book/{{$.id}}/ 、/book/{$..id} —— 字面文字里嵌入子规则。
            // 必须最先处理：子规则里常有 ||（{{$.novelid||$.novelId}}），不能先被顶层切分。
            if (IsTemplate(text))
            {
                string filled = FillTemplate(text, context);
                if (filled != null) output.Add(filled);
                return;
            }

            // 前导 ":" 表示 AllInOne：把结果合并成一个字符串（Legado 的 : 前缀）
            bool joinAll = false;
            if (text.Length > 1 && text[0] == ':' && text[1] != ':')
            {
                joinAll = true;
                text = text.Substring(1).Trim();
            }

            // 并联：取第一个非空分支
            var orParts = SplitTopLevel(text, "||");
            if (orParts.Count > 1)
            {
                foreach (var part in orParts)
                {
                    var branch = Eval(part, context);
                    if (!IsEmptyResult(branch)) { output.AddRange(branch); return; }
                }
                return;
            }

            // 串联：合并所有分支
            var andParts = SplitTopLevel(text, "&&");
            if (andParts.Count > 1)
            {
                foreach (var part in andParts) output.AddRange(Eval(part, context));
                return;
            }

            // %%：合并为单个字符串
            var mergeParts = SplitTopLevel(text, "%%");
            if (mergeParts.Count > 1)
            {
                var pieces = new List<string>();
                foreach (var part in mergeParts)
                    foreach (var item in Eval(part, context)) pieces.Add(ToStringValue(item));
                output.Add(String.Join("\n", pieces.ToArray()));
                return;
            }

            // 正则替换 / 提取
            // Legado 语义：##正则 → 删除匹配；##正则##替换 → 全部替换；##正则##替换### → 只取第一个匹配并替换
            string regexPattern = null;
            string regexReplacement = null;
            bool replaceFirst = false;
            int hashIndex = IndexOfTopLevel(text, "##");
            if (hashIndex >= 0)
            {
                string tail = text.Substring(hashIndex + 2);
                text = text.Substring(0, hashIndex);
                // 正则里常有括号和引号，不能再按“顶层”切分
                var pieces = tail.Split(new[] { "##" }, StringSplitOptions.None);
                regexPattern = pieces[0];
                regexReplacement = pieces.Length > 1 ? pieces[1] : "";
                replaceFirst = pieces.Length > 2 && pieces[2] == "#";
            }

            // 列表反转
            bool reverse = false;
            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                reverse = true;
                text = text.Substring(1);
            }

            var segments = SplitSegments(text);
            if (segments.Count == 0)
            {
                if (context == null) return;
                if (regexPattern == null) { output.Add(context); return; }
                // 只有 ##正则 的规则（如 ##/book/(\d+)##...###）作用在元素的整段 HTML 上，链接才在里面
                var node = context as HtmlNode;
                var whole = new List<object> { node != null && node.IsElement ? Html.OuterHtml(node) : ToStringValue(context) };
                output.AddRange(ApplyRegex(whole, regexPattern, regexReplacement, replaceFirst));
                return;
            }

            // 不带 @css: 前缀的 HTML 规则是 Legado 的默认（JSoup）语法：class./tag./id./text. 与 .0 / !0 索引
            bool jsoup = !segments[0].StartsWith("@css:", StringComparison.OrdinalIgnoreCase);
            var current = EvalBase(segments[0], context);
            for (int i = 1; i < segments.Count; i++) current = ApplySpec(current, segments[i], jsoup);

            if (reverse) current.Reverse();
            if (regexPattern != null) current = ApplyRegex(current, regexPattern, regexReplacement, replaceFirst);

            if (joinAll)
            {
                var pieces = new List<string>();
                foreach (var item in current) pieces.Add(ToStringValue(item));
                output.Add(String.Join("\n", pieces.ToArray()));
                return;
            }

            output.AddRange(current);
        }

        // ---------------------------------------------------------------- 模板规则

        /// <summary>JS 规则求值，结果可能还带 {{子规则}} 模板（例如 G社漫画的章节地址），接着填充。</summary>
        private static void JsEvalInto(string rule, object context, List<object> output)
        {
            var js = LegadoJs.Current;
            if (js == null)
            {
                Warn("书源依赖 JS 引擎，当前未启用 JS 支持");
                return;
            }
            try
            {
                string script = ExtractScript(rule);
                if (script == null) return;
                string result = js.Evaluate(script, context);
                if (result == null)
                {
                    if (!String.IsNullOrEmpty(js.LastError)) Warn("JS 求值失败：" + js.LastError);
                    return;
                }
                if (result.IndexOf("{{", StringComparison.Ordinal) >= 0 && context != null)
                {
                    string filled = FillTemplate(result, context);
                    if (filled != null) result = filled;
                }
                if (result.Length > 0) output.Add(result);
            }
            catch (Exception ex)
            {
                Warn("JS 规则求值异常：" + ex.Message);
            }
        }

        /// <summary>@js:xxx 取 xxx；&lt;js&gt;…&lt;/js&gt; 取块内脚本。公开给引擎处理 header / URL 的 JS。</summary>
        public static string ExtractScript(string rule)
        {
            string text = rule.Trim();
            if (text.StartsWith("@js:", StringComparison.OrdinalIgnoreCase)) return text.Substring(4);
            if (!text.StartsWith("<js>", StringComparison.OrdinalIgnoreCase)) return null;
            int end = text.LastIndexOf("</js>", StringComparison.OrdinalIgnoreCase);
            return end > 4 ? text.Substring(4, end - 4) : text.Substring(4);
        }

        private static bool IsTemplate(string text)
        {
            if (text.StartsWith("@js:", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("<js>", StringComparison.OrdinalIgnoreCase)) return false;
            return text.IndexOf("{{", StringComparison.Ordinal) >= 0 || text.IndexOf("{$.", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 填充模板：{{子规则}} 与 {$.json路径} 用当前上下文求值后替换。
        /// 子规则是 JS 表达式（无法求值）时整条返回 null——拼出半个地址比没有地址更糟。
        /// 模板后面可以跟 ##正则。
        /// </summary>
        private static string FillTemplate(string text, object context)
        {
            string regexTail = null;
            int close = text.LastIndexOf("}}", StringComparison.Ordinal);
            int singleClose = text.LastIndexOf('}');
            int tailStart = Math.Max(close < 0 ? -1 : close + 2, singleClose + 1);
            int hash = text.IndexOf("##", Math.Max(0, tailStart), StringComparison.Ordinal);
            if (hash >= 0) { regexTail = text.Substring(hash); text = text.Substring(0, hash); }

            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                if (String.CompareOrdinal(text, i, "{{", 0, 2) == 0)
                {
                    int end = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (end < 0) { sb.Append(text, i, text.Length - i); break; }
                    string inner = text.Substring(i + 2, end - i - 2).Trim();
                    string value;
                    if (!TryInnerRule(inner, context, out value))
                    {
                        Warn("模板里含无法求值的 JS 表达式：{{" + inner + "}}");
                        return null;
                    }
                    sb.Append(value);
                    i = end + 2;
                    continue;
                }
                if (String.CompareOrdinal(text, i, "{$.", 0, 3) == 0)
                {
                    int end = FindJsonTemplateEnd(text, i + 1);
                    if (end < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(SelectString(text.Substring(i + 1, end - i - 1), AsJson(context) ?? context));
                    i = end + 1;
                    continue;
                }
                sb.Append(text[i]);
                i++;
            }

            string result = sb.ToString();
            if (regexTail == null) return result;
            var list = new List<object>();
            EvalInto("text" + regexTail, result, list);
            return list.Count == 0 ? "" : ToStringValue(list[0]);
        }

        /// <summary>{$..id} 的右括号：跳过 JSONPath 里成对的 [ ] 与括号。</summary>
        private static int FindJsonTemplateEnd(string text, int start)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '[' || c == '(') depth++;
                else if (c == ']' || c == ')') { if (depth > 0) depth--; }
                else if (c == '}' && depth == 0) return i;
            }
            return -1;
        }

        /// <summary>{{ }} 里是规则（$. / @css: / // / 默认语法）就求值；是 JS 就交给 JS 引擎。</summary>
        private static bool TryInnerRule(string inner, object context, out string value)
        {
            value = "";
            if (inner.Length == 0) return true;
            // {{=文字}}：引擎预先代入的变量值（书源地址、baseUrl 等），原样输出
            if (inner[0] == '=') { value = inner.Substring(1); return true; }
            if (LooksLikeRule(inner))
            {
                value = SelectString(inner, context);
                return true;
            }

            // JS 表达式：{{java.get('x')}} / {{baseUrl.match(...)[1]}} 等
            var js = LegadoJs.Current;
            if (js != null)
            {
                string result = js.Evaluate(inner, context);
                if (result != null) { value = result; return true; }
                if (!String.IsNullOrEmpty(js.LastError)) Warn("模板 JS 求值失败：" + js.LastError);
            }
            else Warn("模板里含无法求值的 JS 表达式：{{" + inner + "}}");
            return false;
        }

        private static bool LooksLikeRule(string inner)
        {
            if (inner.StartsWith("$", StringComparison.Ordinal)) return true;
            if (inner.StartsWith("@css:", StringComparison.OrdinalIgnoreCase) ||
                inner.StartsWith("@json:", StringComparison.OrdinalIgnoreCase) ||
                inner.StartsWith("@xpath:", StringComparison.OrdinalIgnoreCase) ||
                inner.StartsWith("//", StringComparison.Ordinal) ||
                inner.StartsWith("@@", StringComparison.Ordinal)) return true;
            // JS 的典型特征：函数调用、运算、java./source./book. 对象、字符串字面量
            if (inner.IndexOf('(') >= 0 || inner.IndexOf('\'') >= 0 || inner.IndexOf('"') >= 0) return false;
            if (inner.IndexOf('+') >= 0 || inner.IndexOf('=') >= 0 || inner.IndexOf('?') >= 0) return false;
            string lower = inner.ToLowerInvariant();
            if (lower.StartsWith("java.") || lower.StartsWith("source.") || lower.StartsWith("book.") ||
                lower.StartsWith("cookie.") || lower.StartsWith("baseurl") || lower.StartsWith("result")) return false;
            // 剩下的按默认（JSoup）规则：class.xx@text、tag.a@href 等
            return inner.IndexOf('@') >= 0 || inner.IndexOf('.') >= 0 || inner.IndexOf('#') >= 0;
        }

        private static bool IsEmptyResult(List<object> list)
        {
            if (list.Count == 0) return true;
            if (list.Count == 1)
            {
                string s = list[0] as string;
                if (s != null && s.Length == 0) return true;
            }
            return false;
        }

        private static List<object> EvalBase(string segment, object context)
        {
            string body;
            string kind = DetectKind(segment, context, out body);

            switch (kind)
            {
                case "css":
                    {
                        var root = AsHtml(context);
                        if (root == null) return new List<object>();
                        string selector;
                        List<string> ops;
                        ParseIndexOps(body, out selector, out ops);
                        var nodes = CssSelector.Select(root, selector.Length == 0 ? "*" : selector);
                        nodes = ApplyIndexOps(nodes, ops);
                        var list = new List<object>();
                        foreach (var n in nodes) list.Add(n);
                        return list;
                    }

                case "jsoup":
                    {
                        var root = AsHtml(context);
                        if (root == null) return new List<object>();
                        var list = new List<object>();
                        foreach (var n in JsoupRule.SelectSingle(root, body)) list.Add(n);
                        return list;
                    }

                case "xpath":
                    {
                        var root = AsHtml(context);
                        if (root == null) return new List<object>();
                        return XPathQuery.Select(root, body);
                    }

                case "json":
                    {
                        var json = AsJson(context);
                        if (json == null) return new List<object>();
                        var values = JsonPath.Select(json, body);
                        var list = new List<object>();
                        foreach (var v in values) list.Add(v);
                        return list;
                    }

                case "spec":
                    {
                        var list = new List<object>();
                        if (context != null) list.Add(context);
                        return ApplySpec(list, body, false);
                    }

                case "js":
                    {
                        // @js: 出现在规则段中段（例如 "xx @js:..."）时走这里
                        var js = LegadoJs.Current;
                        if (js == null) { Warn("书源依赖 JS 引擎，当前未启用 JS 支持"); return new List<object>(); }
                        string jsResult = js.Evaluate(body, context);
                        var jsList = new List<object>();
                        if (!String.IsNullOrEmpty(jsResult)) jsList.Add(jsResult);
                        return jsList;
                    }

                default:
                    Warn("暂不支持该规则类型：" + segment);
                    return new List<object>();
            }
        }

        private static string DetectKind(string segment, object context, out string body)
        {
            string lower = segment.ToLowerInvariant();

            if (lower.StartsWith("@css:", StringComparison.Ordinal))
            {
                body = segment.Substring(5);
                if (IsContentSpec(body)) return "spec";
                return "css";
            }
            if (lower.StartsWith("@xpath:", StringComparison.Ordinal)) { body = segment.Substring(7); return "xpath"; }
            if (lower.StartsWith("@json:", StringComparison.Ordinal)) { body = segment.Substring(6); return "json"; }
            if (lower.StartsWith("@js:", StringComparison.Ordinal)) { body = segment.Substring(4); return "js"; }
            // @@ 前缀表示使用默认（JSoup）规则
            if (segment.StartsWith("@@", StringComparison.Ordinal)) { body = segment.Substring(2); return "jsoup"; }

            if (segment.StartsWith("$", StringComparison.Ordinal)) { body = segment; return "json"; }
            if (segment.StartsWith("//", StringComparison.Ordinal) || segment.StartsWith("/", StringComparison.Ordinal))
            {
                body = segment;
                return "xpath";
            }

            if (IsContentSpec(segment) && !(context is JsonValue)) { body = segment; return "spec"; }

            if (context is JsonValue)
            {
                body = segment.StartsWith("$", StringComparison.Ordinal) ? segment
                     : "$" + (segment.StartsWith(".", StringComparison.Ordinal) ? "" : ".") + segment;
                return "json";
            }

            body = segment;
            return "jsoup";
        }

        private static readonly string[] ContentSpecs = { "text", "owntext", "textnodes", "html", "all", "href", "src" };

        private static bool IsContentSpec(string s)
        {
            if (String.IsNullOrEmpty(s)) return false;
            string lower = s.Trim().ToLowerInvariant();
            for (int i = 0; i < ContentSpecs.Length; i++)
                if (ContentSpecs[i] == lower) return true;
            return false;
        }

        private static readonly string[] KnownAttributes = { "href", "src", "title", "alt", "value", "content", "data-src" };

        private static List<object> ApplySpec(List<object> items, string spec, bool jsoup)
        {
            // "@js:" 作为后续段（如 "$.path @js:java.aesBase64DecodeToString(result,...)"）：
            // 对每个元素求值一次，result 是该元素
            if (spec != null && spec.TrimStart().StartsWith("@js:", StringComparison.OrdinalIgnoreCase))
            {
                var js = LegadoJs.Current;
                var jsOut = new List<object>();
                if (js != null)
                {
                    string script = ExtractScript(spec);
                    foreach (var item in items)
                    {
                        string jsResult2 = js.Evaluate(script, item);
                        if (!String.IsNullOrEmpty(jsResult2)) jsOut.Add(jsResult2);
                    }
                }
                else Warn("书源依赖 JS 引擎，当前未启用 JS 支持");
                return jsOut;
            }

            if (jsoup && HasHtmlItem(items)) return ApplyJsoupSpec(items, spec);

            string selector;
            List<string> ops;
            ParseIndexOps(spec, out selector, out ops);

            var produced = new List<object>();
            string s = selector.Trim();
            if (s.Length == 0)
            {
                produced.AddRange(items);
            }
            else
            {
                string lower = s.ToLowerInvariant();

                foreach (var item in items)
                {
                    var node = item as HtmlNode;
                    if (node != null && node.IsElement)
                    {
                        switch (lower)
                        {
                            case "text": produced.Add(Html.InnerText(node)); continue;
                            case "owntext": produced.Add(Html.OwnText(node)); continue;
                            case "textnodes":
                                foreach (var t in Html.TextNodes(node)) produced.Add(Html.Normalize(t));
                                continue;
                            case "html": produced.Add(Html.InnerHtml(node)); continue;
                            case "all": produced.Add(Html.OuterHtml(node)); continue;
                        }

                        if (IsAttributeSpec(node, s))
                        {
                            produced.Add(node.GetAttribute(s) ?? "");
                            continue;
                        }

                        foreach (var c in CssSelector.Select(node, s)) produced.Add(c);
                        continue;
                    }

                    var json = item as JsonValue;
                    if (json != null)
                    {
                        string path = s.StartsWith("$", StringComparison.Ordinal) ? s
                                    : "$" + (s.StartsWith(".", StringComparison.Ordinal) ? "" : ".") + s;
                        foreach (var v in JsonPath.Select(json, path)) produced.Add(v);
                        continue;
                    }

                    string text = ToStringValue(item);
                    if (IsContentSpec(s) || lower == "content")
                    {
                        produced.Add(text);
                        continue;
                    }

                    var root = HtmlParser.Parse(text);
                    foreach (var c in CssSelector.Select(root, s)) produced.Add(c);
                }
            }

            if (ops.Count == 0) return produced;

            // 索引/排除运算符只对元素结果有意义
            var nodes2 = new List<HtmlNode>();
            foreach (var item in produced)
            {
                var node = item as HtmlNode;
                if (node == null) return produced;
                nodes2.Add(node);
            }
            var filtered = ApplyIndexOps(nodes2, ops);
            var result = new List<object>();
            foreach (var n in filtered) result.Add(n);
            return result;
        }

        private static bool HasHtmlItem(List<object> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var node = items[i] as HtmlNode;
                if (node != null && node.IsElement) return true;
            }
            return false;
        }

        /// <summary>
        /// 默认（JSoup）语法的后续段：内容类型（text/html/属性名）直接取值，
        /// 否则逐个父元素做 class./tag./id./text./CSS 查找，索引在每个父元素内单独计算（与 Legado 一致）。
        /// </summary>
        private static List<object> ApplyJsoupSpec(List<object> items, string spec)
        {
            var produced = new List<object>();
            string s = spec.Trim();
            if (s.Length == 0) { produced.AddRange(items); return produced; }
            string lower = s.ToLowerInvariant();

            foreach (var item in items)
            {
                var node = item as HtmlNode;
                if (node == null || !node.IsElement)
                {
                    produced.AddRange(ApplySpec(new List<object> { item }, spec, false));
                    continue;
                }

                switch (lower)
                {
                    case "text": produced.Add(Html.InnerText(node)); continue;
                    case "owntext": produced.Add(Html.OwnText(node)); continue;
                    case "textnodes":
                        foreach (var t in Html.TextNodes(node)) produced.Add(Html.Normalize(t));
                        continue;
                    case "html": produced.Add(Html.InnerHtml(node)); continue;
                    case "all": produced.Add(Html.OuterHtml(node)); continue;
                }

                if (IsAttributeSpec(node, s))
                {
                    produced.Add(node.GetAttribute(s) ?? "");
                    continue;
                }

                foreach (var c in JsoupRule.SelectSingle(node, s)) produced.Add(c);
            }
            return produced;
        }

        // ---------------------------------------------------------------- 索引运算符

        /// <summary>拆出规则段末尾的索引运算符：`!n` 排除、`[n]` 取第 n 个、`[a:b(:c)]` 区间。</summary>
        private static void ParseIndexOps(string part, out string selector, out List<string> ops)
        {
            ops = new List<string>();
            string s = part == null ? "" : part.Trim();

            while (s.Length > 0)
            {
                char last = s[s.Length - 1];

                if (last == ']')
                {
                    int open = s.LastIndexOf('[');
                    if (open < 0) break;
                    string content = s.Substring(open + 1, s.Length - open - 2).Trim();
                    if (!IsIndexContent(content)) break;
                    ops.Insert(0, content);
                    s = s.Substring(0, open).TrimEnd();
                    continue;
                }

                int bang = s.LastIndexOf('!');
                if (bang >= 0)
                {
                    string tail = s.Substring(bang + 1).Trim();
                    if (IsInteger(tail)) { ops.Insert(0, "!" + tail); s = s.Substring(0, bang).TrimEnd(); continue; }
                }
                break;
            }

            selector = s;
        }

        private static bool IsIndexContent(string content)
        {
            if (String.IsNullOrEmpty(content)) return false;
            bool hasDigit = false;
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c >= '0' && c <= '9') { hasDigit = true; continue; }
                if (c == '!' || c == '-' || c == ':') continue;
                return false;
            }
            return hasDigit;
        }

        private static bool IsInteger(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            int start = text[0] == '-' ? 1 : 0;
            if (start >= text.Length) return false;
            for (int i = start; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9') return false;
            return true;
        }

        private static List<HtmlNode> ApplyIndexOps(List<HtmlNode> nodes, List<string> ops)
        {
            var current = nodes;
            for (int i = 0; i < ops.Count; i++)
            {
                string op = ops[i];
                int length = current.Count;

                if (op.StartsWith("!", StringComparison.Ordinal))
                {
                    int index = NormalizeIndex(ParseInt(op.Substring(1)), length);
                    var kept = new List<HtmlNode>();
                    for (int k = 0; k < current.Count; k++)
                        if (k != index) kept.Add(current[k]);
                    current = kept;
                    continue;
                }

                if (op.IndexOf(':') >= 0)
                {
                    var parts = op.Split(':');
                    int start = parts.Length > 0 && parts[0].Trim().Length > 0 ? NormalizeIndex(ParseInt(parts[0]), length) : 0;
                    int end = parts.Length > 1 && parts[1].Trim().Length > 0 ? NormalizeIndex(ParseInt(parts[1]), length) : length - 1;
                    int step = parts.Length > 2 && parts[2].Trim().Length > 0 ? Math.Abs(ParseInt(parts[2])) : 1;
                    if (step == 0) step = 1;

                    var picked = new List<HtmlNode>();
                    if (start <= end)
                    {
                        for (int k = start; k <= end; k += step)
                            if (k >= 0 && k < length) picked.Add(current[k]);
                    }
                    else
                    {
                        for (int k = start; k >= end; k -= step)
                            if (k >= 0 && k < length) picked.Add(current[k]);
                    }
                    current = picked;
                    continue;
                }

                int single = NormalizeIndex(ParseInt(op), length);
                var one = new List<HtmlNode>();
                if (single >= 0 && single < length) one.Add(current[single]);
                current = one;
            }
            return current;
        }

        private static int NormalizeIndex(int index, int length)
        {
            return index < 0 ? index + length : index;
        }

        private static int ParseInt(string text)
        {
            int value;
            return Int32.TryParse(text == null ? "" : text.Trim(), out value) ? value : 0;
        }

        private static bool IsAttributeSpec(HtmlNode node, string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!(Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':')) return false;
            }
            if (node.HasAttribute(name)) return true;
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < KnownAttributes.Length; i++)
                if (KnownAttributes[i] == lower) return true;
            return false;
        }

        private static List<object> ApplyRegex(List<object> items, string pattern, string replacement, bool replaceFirst)
        {
            Regex regex;
            try
            {
                // 不加 Singleline：Legado（Java 正则）里 . 不匹配换行，书源作者要跨行时会显式写 [\s\S]
                regex = new Regex(pattern);
            }
            catch (Exception ex)
            {
                Warn("正则无效：" + pattern + " -> " + ex.Message);
                return items;
            }

            var output = new List<object>();
            foreach (var item in items)
            {
                string text = ToStringValue(item);
                if (replaceFirst)
                {
                    var match = regex.Match(text);
                    output.Add(match.Success ? regex.Replace(match.Value, replacement, 1) : "");
                }
                else
                {
                    output.Add(regex.Replace(text, replacement ?? ""));
                }
            }
            return output;
        }

        private static HtmlNode AsHtml(object context)
        {
            var node = context as HtmlNode;
            if (node != null) return node;
            var text = context as string;
            if (text != null) return HtmlParser.Parse(text);
            return null;
        }

        private static JsonValue AsJson(object context)
        {
            var json = context as JsonValue;
            if (json != null) return json;
            var text = context as string;
            if (text != null) return LegadoJson.ParseLoose(text);
            var node = context as HtmlNode;
            if (node != null) return LegadoJson.ParseLoose(Html.InnerText(node));
            return null;
        }

        // ---------------------------------------------------------------- 切分工具

        private static int PrefixLengthAt(string text, int index)
        {
            if (index >= text.Length || text[index] != '@') return 0;
            string[] prefixes = { "@css:", "@xpath:", "@json:", "@js:" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                var p = prefixes[i];
                if (index + p.Length <= text.Length &&
                    String.Compare(text, index, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return p.Length;
            }
            return 0;
        }

        /// <summary>按 @ 切分规则段，已知前缀保持完整，忽略方括号与引号内部的 @。</summary>
        private static List<string> SplitSegments(string rule)
        {
            var segments = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            int depth = 0;
            int i = 0;

            while (i < rule.Length)
            {
                char c = rule[i];

                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; sb.Append(c); i++; continue; }
                if (c == '[' || c == '(') { depth++; sb.Append(c); i++; continue; }
                if (c == ']' || c == ')') { if (depth > 0) depth--; sb.Append(c); i++; continue; }

                if (c == '@' && depth == 0)
                {
                    int prefixLength = PrefixLengthAt(rule, i);
                    if (sb.Length > 0) { segments.Add(sb.ToString()); sb.Length = 0; }
                    if (prefixLength > 0)
                    {
                        sb.Append(rule, i, prefixLength);
                        i += prefixLength;
                    }
                    else i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            if (sb.Length > 0) segments.Add(sb.ToString());
            return segments;
        }

        private static List<string> SplitTopLevel(string text, string separator)
        {
            var parts = new List<string>();
            char quote = '\0';
            int depth = 0;
            int start = 0;
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; i++; continue; }
                if (c == '[' || c == '(') { depth++; i++; continue; }
                if (c == ']' || c == ')') { if (depth > 0) depth--; i++; continue; }

                if (depth == 0 && i + separator.Length <= text.Length &&
                    String.Compare(text, i, separator, 0, separator.Length, StringComparison.Ordinal) == 0)
                {
                    parts.Add(text.Substring(start, i - start));
                    i += separator.Length;
                    start = i;
                    continue;
                }
                i++;
            }

            parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>公开给引擎（ LegadoEngine 的 @put 值解析等）。</summary>
        public static int IndexOfTopLevel(string text, string token)
        {
            return IndexOfTopLevel(text, token, 0);
        }

        private static int IndexOfTopLevel(string text, string token, int start)
        {
            char quote = '\0';
            int depth = 0;
            for (int i = start; i + token.Length <= text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '[' || c == '(') { depth++; continue; }
                if (c == ']' || c == ')') { if (depth > 0) depth--; continue; }
                if (depth == 0 && String.Compare(text, i, token, 0, token.Length, StringComparison.Ordinal) == 0)
                    return i;
            }
            return -1;
        }
    }
}
