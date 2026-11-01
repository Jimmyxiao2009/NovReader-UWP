using System;
using System.Collections.Generic;
using System.Text;
using NovReader.Services.Legado.Js;

namespace NovReader.Services.Legado
{
    /// <summary>
    /// Legado 的 URL 规则：形如 `地址,{"method":"POST","body":"...","charset":"gbk","headers":{...}}`。
    /// 负责拆分选项、替换 {{key}} 等变量，并把剩余无法求值的 {{...}}（JS 表达式）剔除并记录警告。
    /// </summary>
    public sealed class UrlRule
    {
        public string Url = "";
        public string Method = "GET";
        public string Body = "";
        public string Charset = "";
        public string HeaderLines = "";
        public readonly List<string> Warnings = new List<string>();

        public bool IsPost { get { return String.Equals(Method, "POST", StringComparison.OrdinalIgnoreCase); } }

        public static UrlRule Parse(string baseUrl, string urlRule, IDictionary<string, string> parameters)
        {
            var rule = new UrlRule();
            string text = urlRule ?? "";

            // 整条 URL 是 JS（如 lofter 的搜索地址）：求值后当普通地址继续走。
            // 结果可能还带 ,{options} 或 {{子规则}}，交给后面的流程处理。
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("@js:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<js>", StringComparison.OrdinalIgnoreCase))
            {
                var jsUrl = LegadoJs.Current;
                if (jsUrl != null)
                {
                    jsUrl.SetUrlVars(
                        parameters != null && parameters.ContainsKey("key") ? parameters["key"] : "",
                        parameters != null && parameters.ContainsKey("page") ? parameters["page"] : "1");
                    string script = trimmed.StartsWith("@js:", StringComparison.OrdinalIgnoreCase)
                        ? trimmed.Substring(4)
                        : (trimmed.LastIndexOf("</js>", StringComparison.OrdinalIgnoreCase) > 4
                            ? trimmed.Substring(4, trimmed.LastIndexOf("</js>", StringComparison.OrdinalIgnoreCase) - 4)
                            : trimmed.Substring(4));
                    string result = jsUrl.Evaluate(script, baseUrl);
                    if (!String.IsNullOrEmpty(result)) text = result.Trim();
                    else rule.Warnings.Add("URL 的 JS 求值失败：" + jsUrl.LastError);
                }
                else
                {
                    rule.Warnings.Add("书源依赖 JS 引擎，当前未启用 JS 支持");
                    rule.Url = "";
                    return rule;
                }
            }

            string optionsJson = null;
            int index = IndexOfOptions(text);
            if (index >= 0)
            {
                optionsJson = text.Substring(index + 1).Trim();
                text = text.Substring(0, index);
            }

            // 先读选项：关键字的百分号编码要按选项里的 charset 来做
            JsonValue options = null;
            if (optionsJson != null)
            {
                options = LegadoJson.ParseLoose(optionsJson);
                if (options == null || options.Kind != JsonKind.Object)
                {
                    rule.Warnings.Add("URL 选项不是合法 JSON：" + optionsJson);
                    options = null;
                }
            }

            if (options != null)
            {
                string charset = Str(options, "charset");
                if (!String.IsNullOrEmpty(charset)) rule.Charset = charset;
            }

            text = Substitute(text, parameters, rule, baseUrl);
            rule.Url = LegadoUrl.Resolve(baseUrl, text.Trim());

            if (options != null)
            {
                string method = Str(options, "method");
                if (!String.IsNullOrEmpty(method)) rule.Method = method.ToUpperInvariant();

                string body = Str(options, "body");
                if (!String.IsNullOrEmpty(body)) rule.Body = Substitute(body, parameters, rule, baseUrl);

                var headers = options.Get("headers");
                if (headers != null) rule.HeaderLines = HeadersToLines(headers);
            }

            return rule;
        }

        /// <summary>找到 URL 与选项 JSON 的分界：第一个后面紧跟 { 的逗号。</summary>
        private static int IndexOfOptions(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ',') continue;
                int j = i + 1;
                while (j < text.Length && Html.IsSpace(text[j])) j++;
                if (j < text.Length && text[j] == '{') return i;
            }
            return -1;
        }

        private static string Substitute(string text, IDictionary<string, string> parameters, UrlRule rule, string baseUrl)
        {
            if (String.IsNullOrEmpty(text)) return "";

            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    string token = "{{" + pair.Key + "}}";
                    if (text.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                    text = text.Replace(token, LegadoUrl.Escape(pair.Value ?? "", rule.Charset));
                }
            }

            // 剩余的 {{...}}：{{=文字}} 原样输出；JS 表达式交给 JS 引擎；都不行才剔除并记录
            int start = text.IndexOf("{{", StringComparison.Ordinal);
            if (start >= 0)
            {
                var js = LegadoJs.Current;
                var sb = new StringBuilder(text.Length);
                int i = 0;
                while (i < text.Length)
                {
                    int open = text.IndexOf("{{", i, StringComparison.Ordinal);
                    if (open < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, open - i);
                    int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
                    if (close < 0) { sb.Append(text, open, text.Length - open); break; }
                    string expression = text.Substring(open + 2, close - open - 2).Trim();

                    if (expression.StartsWith("=", StringComparison.Ordinal))
                    {
                        sb.Append(expression.Substring(1));
                        i = close + 2;
                        continue;
                    }

                    string jsResult = js != null ? js.Evaluate(expression, baseUrl) : null;
                    if (jsResult != null)
                    {
                        sb.Append(jsResult);
                        i = close + 2;
                        continue;
                    }
                    if (js != null && !String.IsNullOrEmpty(js.LastError))
                        rule.Warnings.Add("URL 里的 JS 求值失败：{{" + expression + "}} → " + js.LastError);
                    else
                        rule.Warnings.Add("忽略了无法求值的 JS 表达式：{{" + expression + "}}");
                    i = close + 2;
                }
                text = sb.ToString();
            }

            return text;
        }

        private static string Str(JsonValue obj, string name)
        {
            var value = obj.Get(name);
            return value == null ? "" : value.AsString();
        }

        private static string HeadersToLines(JsonValue headers)
        {
            if (headers.Kind == JsonKind.String) return headers.Str ?? "";
            if (headers.Kind != JsonKind.Object || headers.Members == null) return "";

            var sb = new StringBuilder();
            for (int i = 0; i < headers.Members.Count; i++)
            {
                var pair = headers.Members[i];
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(pair.Key).Append(": ").Append(pair.Value == null ? "" : pair.Value.AsString());
            }
            return sb.ToString();
        }
    }
}
