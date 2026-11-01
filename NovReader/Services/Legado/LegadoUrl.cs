using System;
using System.Collections.Generic;

namespace NovReader.Services.Legado
{
    /// <summary>Legado 书源的 URL 规则：{{key}} 等占位符替换与相对地址解析。</summary>
    public static class LegadoUrl
    {
        /// <summary>替换 {{key}} / {{page}} 等占位符，并按 baseUrl 解析成绝对地址。</summary>
        public static string Build(string baseUrl, string urlRule, IDictionary<string, string> parameters)
        {
            if (String.IsNullOrEmpty(urlRule)) return baseUrl ?? "";
            string url = urlRule;

            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    string token = "{{" + pair.Key + "}}";
                    if (url.IndexOf(token, StringComparison.Ordinal) < 0) continue;
                    url = url.Replace(token, Escape(pair.Value ?? ""));
                }
            }

            // 仍有未替换的 {{...}}（多为 JS 表达式），去掉花括号里的内容会破坏地址，这里原样保留。
            return Resolve(baseUrl, url);
        }

        public static string Escape(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return Uri.EscapeDataString(value);
        }

        /// <summary>
        /// 按指定字符集做百分号编码。GBK 站点要求关键字按 GBK 字节编码，
        /// 用 UTF-8 编码会让站点搜不到任何结果。
        /// </summary>
        public static string Escape(string value, string charset)
        {
            if (String.IsNullOrEmpty(value)) return "";
            if (String.IsNullOrEmpty(charset)) return Escape(value);

            var encoding = HttpFetcher.GetEncoding(charset);
            if (encoding.CodePage == 65001) return Escape(value);

            byte[] bytes = encoding.GetBytes(value);
            var sb = new System.Text.StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                bool unreserved = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
                                  || b == '-' || b == '_' || b == '.' || b == '~';
                if (unreserved) sb.Append((char)b);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析可能带 Legado 选项（`地址,{"method":...}`）的链接：只解析地址部分，选项原样保留。
        /// 直接整体交给 Uri 会把 { } " 转义掉，后续就再也拆不出请求选项了。
        /// </summary>
        public static string ResolveRule(string baseUrl, string url)
        {
            if (String.IsNullOrEmpty(url)) return Resolve(baseUrl, url);
            string options;
            string address = SplitOptions(url, out options);
            string resolved = Resolve(baseUrl, address);
            return options == null ? resolved : resolved + "," + options;
        }

        /// <summary>拆出地址与选项 JSON（第一个后面紧跟 { 的逗号为分界），无选项时 options 为 null。</summary>
        public static string SplitOptions(string url, out string options)
        {
            options = null;
            if (String.IsNullOrEmpty(url)) return url ?? "";
            for (int i = 0; i < url.Length; i++)
            {
                if (url[i] != ',') continue;
                int j = i + 1;
                while (j < url.Length && Char.IsWhiteSpace(url[j])) j++;
                if (j < url.Length && url[j] == '{')
                {
                    options = url.Substring(i + 1).Trim();
                    return url.Substring(0, i).Trim();
                }
            }
            return url.Trim();
        }

        public static string Resolve(string baseUrl, string url)
        {
            if (String.IsNullOrEmpty(url)) return baseUrl ?? "";
            string u = url.Trim();

            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return u;

            if (u.StartsWith("//", StringComparison.Ordinal))
            {
                string scheme = IsHttp(baseUrl) ? "http" : "https";
                if (!String.IsNullOrEmpty(baseUrl) && baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) scheme = "http";
                return scheme + ":" + u;
            }

            if (String.IsNullOrEmpty(baseUrl)) return u;
            try
            {
                // 用 AbsoluteUri 保留百分号转义，ToString() 会把 %XX 还原成原字符
                return new Uri(new Uri(baseUrl), u).AbsoluteUri;
            }
            catch (Exception)
            {
                return u;
            }
        }

        /// <summary>去掉协议与末尾斜杠后比较，判断是否同一页面。</summary>
        public static bool IsSamePage(string a, string b)
        {
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) return false;
            string x = Normalize(a);
            string y = Normalize(b);
            return String.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string url)
        {
            if (String.IsNullOrEmpty(url)) return "";
            string u = url.Trim();
            int hash = u.IndexOf('#');
            if (hash >= 0) u = u.Substring(0, hash);
            string ignored;
            u = SplitOptions(u, out ignored);
            if (u.EndsWith("/", StringComparison.Ordinal) && u.Length > 8) u = u.Substring(0, u.Length - 1);
            int schemeIndex = u.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0) u = u.Substring(schemeIndex + 3);
            return u;
        }

        private static bool IsHttp(string url)
        {
            return !String.IsNullOrEmpty(url) && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
