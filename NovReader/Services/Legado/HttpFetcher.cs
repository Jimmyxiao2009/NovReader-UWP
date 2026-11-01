using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NovReader.Services.Legado
{
    /// <summary>一次 HTTP 抓取的结果与诊断信息。</summary>
    public sealed class FetchResult
    {
        public string Url = "";
        public string FinalUrl = "";
        public int StatusCode;
        public int ByteLength;
        public string EncodingName = "";
        public string Text = "";
        public string Error = "";

        public bool Ok { get { return String.IsNullOrEmpty(Error); } }
    }

    /// <summary>带编码探测的简单 GET 抓取器。GBK/Big5 通过可选的代码页提供程序支持。</summary>
    public static class HttpFetcher
    {
        /// <summary>UWP 端注入的编码提供程序初始化回调（共享代码里不直接引用 CodePages 类型）。</summary>
        public static Action EncodingBootstrap;
        /// <summary>二进制下载失败诊断；由 UWP 注入同步日志，不能影响请求结果。</summary>
        public static Action<string> BinaryDiagnostics;
        public static Func<string, string, int, Task<byte[]>> BinaryFallback;
        private static readonly HashSet<string> NativeBinaryHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static int TimeoutSeconds = 20;

        private static readonly object _lock = new object();
        private static HttpClient _client;
        private static volatile bool _codePagesTried;
        /// <summary>共享 Cookie 罐：java.getCookie 与需要登录态的书源靠它跨请求保持会话。</summary>
        private static readonly CookieContainer _cookies = new CookieContainer();
        private static string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        public static void SetUserAgent(string userAgent)
        {
            if (!String.IsNullOrEmpty(userAgent)) _userAgent = userAgent;
        }

        /// <summary>读共享罐里某站点的 Cookie；带 name 时只取那一项。失败返回空串。</summary>
        public static string GetCookie(string url)
        {
            if (String.IsNullOrEmpty(url)) return "";
            try
            {
                var all = _cookies.GetCookies(new Uri(url));
                if (all == null || all.Count == 0) return "";
                var sb = new StringBuilder();
                foreach (Cookie cookie in all)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(cookie.Name).Append('=').Append(cookie.Value);
                }
                return sb.ToString();
            }
            catch (Exception) { return ""; }
        }

        private static HttpClient Client
        {
            get
            {
                lock (_lock)
                {
                    if (_client != null) return _client;
                    var handler = new HttpClientHandler();
                    try
                    {
                        handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    }
                    catch (Exception) { }
                    try
                    {
                        handler.UseCookies = true;
                        handler.CookieContainer = _cookies;
                    }
                    catch (Exception) { }
                    _client = new HttpClient(handler);
                    _client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds + 10);
                    return _client;
                }
            }
        }

        public static async Task<FetchResult> GetAsync(string url, string headerLines)
        {
            return await SendAsync(url, headerLines, "GET", null, null).ConfigureAwait(false);
        }

        /// <summary>发送请求，支持 POST 请求体与强制字符集。</summary>
        public static Task<FetchResult> SendAsync(string url, string headerLines, string method, string body, string charset)
        {
            return SendAsync(url, headerLines, method, body, charset, 0);
        }

        /// <summary>timeoutSeconds &lt;= 0 时用全局 TimeoutSeconds。</summary>
        public static async Task<FetchResult> SendAsync(string url, string headerLines, string method, string body, string charset, int timeoutSeconds)
        {
            int timeout = timeoutSeconds > 0 ? Math.Min(timeoutSeconds, TimeoutSeconds) : TimeoutSeconds;
            var result = new FetchResult { Url = url ?? "" };
            if (String.IsNullOrEmpty(url))
            {
                result.Error = "地址为空";
                return result;
            }

            // 内置离线测试站点：不走网络，直接返回构造好的页面（含编码探测路径）。
            if (LocalTestSite.Handles(url))
            {
                var page = LocalTestSite.Get(url);
                if (page == null)
                {
                    result.Error = "本地测试站点没有该地址：" + url;
                    return result;
                }
                var localEncoding = ResolveEncoding(page.Charset, page.Bytes);
                result.FinalUrl = url;
                result.StatusCode = 200;
                result.ByteLength = page.Bytes.Length;
                result.EncodingName = localEncoding.WebName;
                result.Text = localEncoding.GetString(page.Bytes, 0, page.Bytes.Length);
                return result;
            }

            try
            {
                bool isPost = String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
                var request = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, url);
                try
                {
                    ApplyHeaders(request, headerLines);

                    if (isPost)
                    {
                        var bodyEncoding = String.IsNullOrEmpty(charset) ? new UTF8Encoding(false) : GetEncoding(charset);
                        request.Content = new ByteArrayContent(bodyEncoding.GetBytes(body ?? ""));
                        if (String.IsNullOrEmpty(charset))
                            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
                        else
                            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded; charset=" + charset);
                    }

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout)))
                    {
                        using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                        {
                            result.StatusCode = (int)response.StatusCode;
                            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            bytes = MaybeGunzip(bytes);
                            result.ByteLength = bytes.Length;

                            if (response.RequestMessage != null && response.RequestMessage.RequestUri != null)
                                result.FinalUrl = response.RequestMessage.RequestUri.AbsoluteUri;
                            else result.FinalUrl = url;

                            string responseCharset = charset;
                            if (String.IsNullOrEmpty(responseCharset))
                            {
                                var contentType = response.Content.Headers.ContentType;
                                if (contentType != null) responseCharset = contentType.CharSet;
                            }

                            var encoding = ResolveEncoding(responseCharset, bytes);
                            result.EncodingName = encoding.WebName;
                            result.Text = encoding.GetString(bytes, 0, bytes.Length);

                            if (!response.IsSuccessStatusCode)
                                result.Error = "HTTP " + result.StatusCode;
                        }
                    }
                }
                finally
                {
                    request.Dispose();
                }
            }
            catch (Exception ex)
            {
                result.Error = Describe(ex);
            }
            return result;
        }

        /// <summary>下载二进制内容（漫画图片）。失败返回 null。</summary>
        public static async Task<byte[]> GetBytesAsync(string url, string headerLines, int timeoutSeconds)
        {
            if (String.IsNullOrEmpty(url)) return null;
            string host = "";
            try { host = new Uri(url).Host; } catch (Exception) { }
            bool useNative;
            lock (_lock) useNative = NativeBinaryHosts.Contains(host);
            if (useNative && BinaryFallback != null) return await TryBinaryFallback(url, headerLines, timeoutSeconds, host).ConfigureAwait(false);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    ApplyHeaders(request, headerLines);
                    // W10M cannot decode WebP; override modern formats advertised by sources.
                    request.Headers.Remove("Accept");
                    request.Headers.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/gif;q=0.9,*/*;q=0.1");
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : TimeoutSeconds)))
                    using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            ReportBinaryError("image download HTTP " + (int)response.StatusCode + ": " + url);
                            return await TryBinaryFallback(url, headerLines, timeoutSeconds, host).ConfigureAwait(false);
                        }
                        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportBinaryError("image download failed: " + url + " -> " + Describe(ex));
                return await TryBinaryFallback(url, headerLines, timeoutSeconds, host).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> TryBinaryFallback(string url, string headers, int timeout, string host)
        {
            if (BinaryFallback == null) return null;
            try
            {
                byte[] bytes = await BinaryFallback(url, headers, timeout).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 0) { lock (_lock) NativeBinaryHosts.Add(host); }
                return bytes;
            }
            catch (Exception error) { ReportBinaryError("image fallback failed: " + Describe(error)); return null; }
        }

        private static void ReportBinaryError(string message)
        {
            try { if (BinaryDiagnostics != null) BinaryDiagnostics(message); }
            catch (Exception) { }
        }

        /// <summary>
        /// UWP 的 HttpRequestException.Message 常常只有一句“Exception of type ... was thrown.”，
        /// 真正的原因（TLS、DNS、AppContainer 网络隔离等）在内层异常里，这里把整条链拼出来。
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "";
            var sb = new StringBuilder();
            int depth = 0;
            for (var current = ex; current != null && depth < 5; current = current.InnerException, depth++)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            }
            return sb.ToString();
        }

        private static void ApplyHeaders(HttpRequestMessage request, string headerLines)
        {
            try
            {
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,*/*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.5");
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            }
            catch (Exception) { }

            foreach (var pair in ParseHeaders(headerLines))
            {
                try
                {
                    // 书源常自带 User-Agent，必须覆盖默认值而不是追加出两个
                    if (request.Headers.Contains(pair.Key)) request.Headers.Remove(pair.Key);
                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 解析请求头。Legado 书源的 header 字段是 JSON 对象（{"User-Agent":"..."}），
        /// URL 选项里的 headers 转出来是 “名: 值” 行，两者合并后会混在一起，这里都认。
        /// 以前按行切分 JSON，得到 `{ "User-Agent"` 这种非法名字被丢弃，书源自带的 UA / Referer / Cookie 从没生效。
        /// </summary>
        public static List<KeyValuePair<string, string>> ParseHeaders(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (String.IsNullOrEmpty(text)) return result;
            string t = text.Trim();
            if (t.StartsWith("@js:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<js>", StringComparison.OrdinalIgnoreCase))
                return result;

            // 先把开头的 JSON 对象拆出来（后面可能还跟着 “名: 值” 行）
            if (t.StartsWith("{", StringComparison.Ordinal))
            {
                int end = MatchingBrace(t);
                string json = end > 0 ? t.Substring(0, end + 1) : t;
                var obj = LegadoJson.ParseLoose(json);
                if (obj != null && obj.Kind == JsonKind.Object && obj.Members != null)
                {
                    for (int i = 0; i < obj.Members.Count; i++)
                    {
                        string name = (obj.Members[i].Key ?? "").Trim();
                        if (name.Length == 0) continue;
                        result.Add(new KeyValuePair<string, string>(name, obj.Members[i].Value == null ? "" : obj.Members[i].Value.AsString()));
                    }
                }
                t = end > 0 ? t.Substring(end + 1) : "";
            }

            foreach (var line in t.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index = line.IndexOf(':');
                if (index <= 0) continue;
                string name = line.Substring(0, index).Trim().Trim('"', '\'', ',', '{', '}', ' ');
                string value = line.Substring(index + 1).Trim().TrimEnd(',').Trim().Trim('"', '\'');
                if (name.Length == 0 || name.IndexOf(' ') >= 0) continue;
                result.Add(new KeyValuePair<string, string>(name, value));
            }
            return result;
        }

        private static int MatchingBrace(string text)
        {
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\') { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static Encoding ResolveEncoding(string charset, byte[] bytes)
        {
            if (String.IsNullOrEmpty(charset)) charset = SniffCharset(bytes);
            return GetEncoding(charset);
        }

        /// <summary>书源广场的个别文件是裸 gzip（无 Content-Encoding），按魔数解压。</summary>
        private static byte[] MaybeGunzip(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 2) return bytes;
            if (!(bytes[0] == 0x1F && bytes[1] == 0x8B)) return bytes;
            try
            {
                using (var input = new MemoryStream(bytes))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return bytes;
            }
        }

        private static string SniffCharset(byte[] bytes)
        {
            int length = Math.Min(bytes.Length, 4096);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append((char)bytes[i]);
            var match = Regex.Match(sb.ToString(), "charset\\s*=\\s*[\"']?([A-Za-z0-9_\\-]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static Encoding GetEncoding(string name)
        {
            if (String.IsNullOrEmpty(name)) return new UTF8Encoding(false);
            string n = name.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
            if (n.Length == 0) return new UTF8Encoding(false);
            if (n == "utf-8" || n == "utf8") return new UTF8Encoding(false);
            if (n == "gb2312" || n == "gbk" || n == "gb18030" || n == "cp936" || n == "ms936" || n == "x-gbk") n = "gb18030";
            if (n == "big5" || n == "big-5" || n == "cp950") n = "big5";

            try
            {
                EnsureCodePages();
                return Encoding.GetEncoding(n);
            }
            catch (Exception)
            {
                return new UTF8Encoding(false);
            }
        }

        private static void EnsureCodePages()
        {
            if (_codePagesTried) return;
            // 并行搜索时多个线程会同时首次解码，必须等注册完成再继续，否则个别请求会退回 UTF-8
            lock (_lock)
            {
                if (_codePagesTried) return;

                // 桌面 .NET 自带 GBK/Big5；UWP 需要由 App 注入 CodePages 提供程序。
                var bootstrap = EncodingBootstrap;
                if (bootstrap != null)
                {
                    try { bootstrap(); }
                    catch (Exception) { }
                }
                _codePagesTried = true;
            }
        }
    }
}
