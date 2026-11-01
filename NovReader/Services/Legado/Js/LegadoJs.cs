using System;
using System.Collections.Generic;
using System.Text;
using NovReader.Models;

namespace NovReader.Services.Legado.Js
{
    /// <summary>JS 后端把宿主函数暴露给脚本用的回调集合（java.* 全部走这里）。</summary>
    public sealed class JsHost
    {
        /// <summary>java.ajax(url)：同步取网页（JS 求值都在后台线程）。</summary>
        public Func<string, string> Ajax;
        /// <summary>java.post(url, body, headers)。</summary>
        public Func<string, string, string, string> Post;
        /// <summary>java.get(url, headers)。</summary>
        public Func<string, string, string> HttpGet;
        /// <summary>java.getString / java.getElement：按规则从当前页面取值。</summary>
        public Func<string, string> RuleString;
        /// <summary>java.getCookie(url, name)。</summary>
        public Func<string, string, string> GetCookie;
        /// <summary>java.get(key)：取变量。</summary>
        public Func<string, string> GetVar;
        /// <summary>java.put(key, value)。</summary>
        public Action<string, string> Put;
        /// <summary>base64 / md5 / sha / aes（C# 本地实现，不联网）。</summary>
        public Func<string, string> Base64Encode;
        public Func<string, string> Base64Decode;
        public Func<string, string> Base64Url;
        public Func<string, string> Md5;
        public Func<string, string> Md5_16;
        public Func<string, string> Sha1;
        public Func<string, string> Sha256;
        /// <summary>java.aesBase64DecodeToString(data, key, transformation, iv)。</summary>
        public Func<string, string, string, string, string> AesDecode;
        public Func<string, string, string, string, string> AesEncode;
    }

    /// <summary>
    /// JS 引擎后端。UWP 用 JavaScriptCore 原生桥（JsBridge.dll），测试台用 Jint。
    /// 语义与 Legado 一致：返回“最后一个表达式的值”，为空时回退到 result 变量。
    /// </summary>
    public interface IJsBackend : IDisposable
    {
        /// <summary>
        /// 求值。bindings：字符串绑定（src/baseUrl/key/page/result 文本等）；
        /// jsonBindings：JSON 绑定（book/source/result 需要真对象/数组时）。
        /// 失败返回 null，原因写入 LastError。
        /// </summary>
        string Evaluate(string script, IDictionary<string, string> bindings,
                        IDictionary<string, string> jsonBindings, JsHost host);

        string LastError { get; }
    }

    /// <summary>
    /// Legado 的 JS 运行环境：书源里的 &lt;js&gt; / @js: / {{js 表达式}} 在这里求值。
    /// 持有变量（java.put/get）、当前页面上下文和书/书源信息；具体求值交给 <see cref="IJsBackend"/>。
    /// </summary>
    public sealed class LegadoJs
    {
        // AsyncLocal：并行搜索时每个异步流有自己的实例，互不串台
        private static readonly System.Threading.AsyncLocal<LegadoJs> _current =
            new System.Threading.AsyncLocal<LegadoJs>();

        /// <summary>后端工厂：UWP 设 JavaScriptCore，测试台设 Jint。返回 null 表示无 JS 能力。</summary>
        public static Func<LegadoJs, IJsBackend> BackendFactory;

        /// <summary>后端初始化失败时置位，之后所有 JS 按“不可用”处理，不再反复尝试。</summary>
        public static bool RuntimeBroken;

        public static LegadoJs Current
        {
            get { return _current.Value; }
            set { _current.Value = value; }
        }

        private IJsBackend _backend;
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _src = "";
        private string _baseUrl = "";
        private string _key = "";
        private string _page = "1";
        private Book _book;
        private BookSource _source;
        private JsHost _host;

        public string LastError { get; private set; }

        public void SetPage(string body, string url)
        {
            lock (_gate) { _src = body ?? ""; _baseUrl = url ?? ""; }
        }

        public void SetBook(Book book) { lock (_gate) _book = book; }

        public void SetSource(BookSource source) { lock (_gate) _source = source; }

        public void SetUrlVars(string key, string page)
        {
            lock (_gate) { _key = key ?? ""; _page = page ?? "1"; }
        }

        public void Put(string key, string value)
        {
            if (String.IsNullOrEmpty(key)) return;
            lock (_gate) _vars[key] = value ?? "";
        }

        public string Get(string key)
        {
            if (String.IsNullOrEmpty(key)) return "";
            lock (_gate) { string v; return _vars.TryGetValue(key, out v) ? v : ""; }
        }

        /// <summary>
        /// 求值一段脚本。返回“最后一个表达式的值”，为空时取 result 变量（与 Legado/Rhino 一致）。
        /// 失败返回 null（原因在 LastError）。
        /// </summary>
        public string Evaluate(string script, object resultValue)
        {
            LastError = "";
            if (RuntimeBroken || BackendFactory == null)
            {
                LastError = RuntimeBroken ? "JS 引擎不可用" : "本运行环境没有 JS 引擎";
                return null;
            }
            if (String.IsNullOrEmpty(script)) return "";

            lock (_gate)
            {
                try
                {
                    if (_backend == null)
                    {
                        _backend = BackendFactory(this);
                        if (_backend == null) { RuntimeBroken = true; LastError = "JS 引擎初始化失败"; return null; }
                    }
                    if (_host == null) _host = CreateHost();

                    var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
                    var jsonBindings = new Dictionary<string, string>(StringComparer.Ordinal);

                    string src, baseUrl, key, page;
                    Book book; BookSource source;
                    lock (_gate) { src = _src; baseUrl = _baseUrl; key = _key; page = _page; book = _book; source = _source; }

                    bindings["src"] = src;
                    bindings["baseUrl"] = baseUrl;
                    bindings["key"] = key;
                    bindings["page"] = page;

                    var node = resultValue as HtmlNode;
                    if (node != null) bindings["result"] = Html.OuterHtml(node);
                    else
                    {
                        var json = resultValue as JsonValue;
                        if (json != null && (json.Kind == JsonKind.Array || json.Kind == JsonKind.Object))
                            jsonBindings["result"] = json.ToJson();
                        else bindings["result"] = resultValue as string ?? "";
                    }

                    jsonBindings["book"] = BookToJson(book);
                    jsonBindings["source"] = SourceToJson(source);

                    string value = _backend.Evaluate(script, bindings, jsonBindings, _host);
                    LastError = _backend.LastError ?? "";
                    return value;
                }
                catch (Exception ex)
                {
                    LastError = Shorten(ex.Message);
                    return null;
                }
            }
        }

        private JsHost CreateHost()
        {
            return new JsHost
            {
                Ajax = delegate (string url) { return HttpString(url, null, null); },
                Post = delegate (string url, string body, string headers) { return HttpString(url, body, headers); },
                HttpGet = delegate (string url, string headers) { return HttpString(url, null, headers); },
                RuleString = delegate (string rule) { return RuleString(rule); },
                GetCookie = delegate (string url, string name) { return HttpFetcher.GetCookie(url); },
                GetVar = Get,
                Put = Put,
                Base64Encode = delegate (string t) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(t ?? "")); },
                Base64Decode = delegate (string t)
                {
                    try
                    {
                        string text = (t ?? "").Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");
                        return Encoding.UTF8.GetString(Convert.FromBase64String(text));
                    }
                    catch (Exception) { return ""; }
                },
                Base64Url = delegate (string t)
                {
                    return Convert.ToBase64String(Encoding.UTF8.GetBytes(t ?? "")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                },
                Md5 = delegate (string t) { return HashHex(t, "MD5"); },
                Md5_16 = delegate (string t) { return HashHex(t, "MD5").Substring(8, 16); },
                Sha1 = delegate (string t) { return HashHex(t, "SHA1"); },
                Sha256 = delegate (string t) { return HashHex(t, "SHA256"); },
                AesDecode = delegate (string d, string k, string tr, string iv) { return AesTransform(d, k, tr, iv, false); },
                AesEncode = delegate (string d, string k, string tr, string iv) { return AesTransform(d, k, tr, iv, true); }
            };
        }

        /// <summary>AES/CBC|ECB/PKCS5|NoPadding 的本地实现（UWP 没有 DES；DES 书源会明确报错）。</summary>
        private static string AesTransform(string data, string key, string transformation, string iv, bool encrypt)
        {
            string upper = (transformation ?? "").ToUpperInvariant();
            bool cbc = upper.Contains("CBC");
            bool noPadding = upper.Contains("NOPADDING");

            byte[] keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            Array.Resize(ref keyBytes, 16);
            byte[] input = encrypt
                ? Encoding.UTF8.GetBytes(data ?? "")
                : Convert.FromBase64String((data ?? "").Trim().Replace(" ", "+"));

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = keyBytes;
                aes.Mode = cbc ? System.Security.Cryptography.CipherMode.CBC : System.Security.Cryptography.CipherMode.ECB;
                aes.Padding = noPadding ? System.Security.Cryptography.PaddingMode.None : System.Security.Cryptography.PaddingMode.PKCS7;
                if (cbc)
                {
                    byte[] ivBytes = Encoding.UTF8.GetBytes(iv ?? "");
                    Array.Resize(ref ivBytes, 16);
                    aes.IV = ivBytes;
                }
                var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
                byte[] output = transform.TransformFinalBlock(input, 0, input.Length);
                return encrypt ? Convert.ToBase64String(output) : Encoding.UTF8.GetString(output);
            }
        }

        private static string HashHex(string text, string algorithm)
        {
            using (System.Security.Cryptography.HashAlgorithm hash =
                algorithm == "MD5" ? (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.MD5.Create() :
                algorithm == "SHA1" ? (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA1.Create() :
                (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private string HttpString(string url, string body, string headers)
        {
            if (String.IsNullOrEmpty(url) || !LegadoEngine.IsUsableLink(url)) return "";
            var rule = UrlRule.Parse(_baseUrl, url, null);
            string merged = String.IsNullOrEmpty(headers) ? rule.HeaderLines
                : (String.IsNullOrEmpty(rule.HeaderLines) ? headers : rule.HeaderLines + "\n" + headers);
            var fetch = HttpFetcher.SendAsync(rule.Url, merged, rule.Method, rule.Body, rule.Charset)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            SetPage(fetch.Text, fetch.FinalUrl);
            return fetch.Ok ? fetch.Text : "";
        }

        private string RuleString(string rule)
        {
            if (String.IsNullOrEmpty(rule)) return "";
            string src;
            lock (_gate) src = _src;
            // 大部分 getString 是从详情页 / 目录页的 JSON 里取字段
            string trimmed = src.TrimStart();
            if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
            {
                var json = LegadoJson.ParseLoose(src);
                if (json != null) return RuleEngine.SelectString(rule, json);
            }
            return RuleEngine.SelectString(rule, RuleEngine.ParseHtml(src));
        }

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 160 ? text : text.Substring(0, 160) + "…";
        }

        private static string JsonQuote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\' || c == '\n' || c == '\r') sb.Append('\\');
                if (c == '\n') { sb.Append('n'); continue; }
                if (c == '\r') { sb.Append('r'); continue; }
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string BookToJson(Book book)
        {
            var b = book ?? new Book();
            var sb = new StringBuilder(256);
            sb.Append("{\"name\":").Append(JsonQuote(b.Name ?? ""));
            sb.Append(",\"author\":").Append(JsonQuote(b.Author ?? ""));
            sb.Append(",\"kind\":").Append(JsonQuote(b.Kind ?? ""));
            sb.Append(",\"intro\":").Append(JsonQuote(b.Intro ?? ""));
            sb.Append(",\"lastChapter\":").Append(JsonQuote(b.LastChapter ?? ""));
            sb.Append(",\"wordCount\":").Append(JsonQuote(b.WordCount ?? ""));
            sb.Append(",\"coverUrl\":").Append(JsonQuote(b.CoverUrl ?? ""));
            sb.Append(",\"bookUrl\":").Append(JsonQuote(b.Url ?? ""));
            sb.Append(",\"tocUrl\":").Append(JsonQuote(b.TocUrl ?? ""));
            sb.Append("}");
            return sb.ToString();
        }

        private static string SourceToJson(BookSource source)
        {
            var s = source ?? new BookSource();
            var sb = new StringBuilder(256);
            sb.Append("{\"bookSourceName\":").Append(JsonQuote(s.Name ?? ""));
            sb.Append(",\"name\":").Append(JsonQuote(s.Name ?? ""));
            sb.Append(",\"bookSourceUrl\":").Append(JsonQuote(s.Url ?? ""));
            sb.Append(",\"url\":").Append(JsonQuote(s.Url ?? ""));
            sb.Append(",\"bookSourceGroup\":").Append(JsonQuote(s.Group ?? ""));
            sb.Append(",\"bookSourceType\":").Append(s.Type);
            sb.Append("}");
            return sb.ToString();
        }
    }
}
