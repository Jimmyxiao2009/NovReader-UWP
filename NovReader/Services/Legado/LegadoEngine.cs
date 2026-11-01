using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovReader.Models;
using NovReader.Services.Legado.Js;

namespace NovReader.Services.Legado
{
    public interface IBookSourceEngine
    {
        Task<IList<Book>> SearchAsync(BookSource source, string keyword, int page);
        Task<Book> GetBookInfoAsync(Book book);
        Task<IList<Chapter>> GetChaptersAsync(Book book);
        Task<string> GetContentAsync(Book book, Chapter chapter);
    }

    /// <summary>
    /// Legado 风格书源引擎：按 ruleSearch / ruleBookInfo / ruleToc / ruleContent 走完
    /// 搜索 → 详情 → 目录 → 正文 全流程，并保留最后一次请求的诊断信息。
    /// </summary>
    public sealed class LegadoEngine : IBookSourceEngine
    {
        /// <summary>目录分页的最大页数，防止 nextTocUrl 规则写错时无限翻页。</summary>
        public const int MaxTocPages = 300;

        /// <summary>正文分页的最大页数。</summary>
        public const int MaxContentPages = 12;

        /// <summary>最近一次 HTTP 请求的详细信息，供调试面板显示。</summary>
        public FetchResult LastFetch { get; private set; }

        /// <summary>最近一次实际请求的 URL。</summary>
        public string LastUrl { get; private set; }

        /// <summary>最近一次失败原因，成功时为空。</summary>
        public string LastError { get; private set; }

        /// <summary>最近一次目录抓取经过的页数（含第一页）。</summary>
        public int LastTocPages { get; private set; }

        /// <summary>搜索请求的超时秒数（0 表示用全局默认）。聚合搜索用较短超时，失效书源不拖慢整体。</summary>
        public int SearchTimeoutSeconds { get; set; }

        public List<string> LastWarnings { get { return RuleEngine.Warnings; } }

        public Task<IList<Book>> SearchAsync(BookSource source, string keyword)
        {
            return SearchAsync(source, keyword, 1);
        }

        // ------------------------------------------------------------ 搜索

        /// <summary>本引擎的 JS 运行环境（java.put/get 的变量、当前页面上下文都在里面）。</summary>
        private readonly LegadoJs _js = new LegadoJs();

        public async Task<IList<Book>> SearchAsync(BookSource source, string keyword, int page)
        {
            var books = new List<Book>();
            RuleEngine.ResetWarnings();
            LastError = "";

            if (source == null) { LastError = "书源为空"; return books; }
            if (String.IsNullOrEmpty(source.SearchUrl)) { LastError = "书源没有配置 searchUrl"; return books; }

            LegadoJs.Current = _js;
            _js.SetSource(source);
            _js.SetBook(null);
            _js.SetUrlVars(keyword ?? "", page.ToString());

            var parameters = new Dictionary<string, string>();
            parameters["key"] = keyword ?? "";
            parameters["page"] = page.ToString();

            var urlRule = UrlRule.Parse(source.Url, Expand(source.SearchUrl, source, source.Url), parameters);
            var fetch = await FetchAsync(urlRule, source.Header, SearchTimeoutSeconds);
            if (String.IsNullOrEmpty(fetch.Text)) return books;

            var doc = new Doc(fetch.Text);
            var rule = source.Search;
            List<object> items;
            if (String.IsNullOrEmpty(rule.BookList))
            {
                items = new List<object>();
                items.Add(doc.For(""));
            }
            else
            {
                items = Flatten(RuleEngine.Eval(rule.BookList, doc.For(rule.BookList)));
            }

            string bookUrlRule = Expand(rule.BookUrl, source, fetch.FinalUrl);
            string coverRule = Expand(rule.CoverUrl, source, fetch.FinalUrl);
            int dropped = 0;
            foreach (var item in items)
            {
                string name = CleanField(Trim(RuleEngine.SelectString(rule.Name, item)), "书名");
                if (String.IsNullOrEmpty(name)) continue;

                string bookUrl = RuleEngine.SelectString(bookUrlRule, item);
                if (String.IsNullOrEmpty(bookUrl)) bookUrl = DefaultLink(item);
                // 取不到可用地址的结果点进去只会是空目录（以前会回落成搜索页地址），直接丢掉
                if (!IsUsableLink(bookUrl)) { dropped++; continue; }

                var book = new Book
                {
                    Name = name,
                    Author = CleanAuthor(CleanField(RuleEngine.SelectString(rule.Author, item), "作者")),
                    Kind = CleanField(RuleEngine.SelectString(rule.Kind, item), "分类"),
                    Intro = CleanField(RuleEngine.SelectString(rule.Intro, item), "简介"),
                    LastChapter = CleanField(Trim(RuleEngine.SelectString(rule.LastChapter, item)), "最新章节"),
                    CoverUrl = CoverOrEmpty(fetch.FinalUrl, RuleEngine.SelectString(coverRule, item)),
                    Url = LegadoUrl.ResolveRule(fetch.FinalUrl, bookUrl),
                    BaseUrl = fetch.FinalUrl,
                    Source = source
                };
                books.Add(book);
            }

            if (dropped > 0 && books.Count == 0)
                LastError = "搜到 " + dropped + " 条但取不到书籍地址（bookUrl 规则可能依赖 JS）";

            // 很多站点搜索到唯一结果时直接 302 到书籍详情页，此时 bookList 什么也取不到。
            // Legado 会把这一页当作详情页解析，这里同样处理。
            if (books.Count == 0 && dropped == 0)
            {
                var direct = TryParseAsDetail(source, doc, fetch.FinalUrl, keyword);
                if (direct != null) books.Add(direct);
            }
            return books;
        }

        private static Book TryParseAsDetail(BookSource source, Doc doc, string finalUrl, string keyword)
        {
            var rule = source.BookInfo;
            if (String.IsNullOrEmpty(rule.Name)) return null;

            var book = new Book { Url = finalUrl, BaseUrl = finalUrl, Source = source };
            ApplyBookInfo(book, doc, finalUrl);
            if (String.IsNullOrEmpty(book.Name)) return null;

            // 没结果的搜索页也可能命中详情页的书名规则（比如页面标题），
            // 只有书名或作者确实包含关键字时才当作“直接跳到详情页”。
            string key = (keyword ?? "").Trim();
            if (key.Length > 0 &&
                book.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 &&
                (book.Author ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            book.Snapshot = doc.Body;
            book.SnapshotUrl = finalUrl;
            return book;
        }

        // ------------------------------------------------------------ 详情

        public async Task<Book> GetBookInfoAsync(Book book)
        {
            if (book == null || book.Source == null) return book;
            RuleEngine.ResetWarnings();
            LastError = "";
            LegadoJs.Current = _js;
            _js.SetBook(book);
            _js.SetSource(book.Source);
            await LoadBookInfoAsync(book);
            return book;
        }

        /// <summary>请求详情页并按 ruleBookInfo 填充字段；保证结束后 TocUrl 有值。</summary>
        private async Task LoadBookInfoAsync(Book book)
        {
            var rule = book.Source.BookInfo;
            if (!HasBookInfoRules(rule))
            {
                if (String.IsNullOrEmpty(book.TocUrl)) book.TocUrl = book.Url;
                return;
            }

            var page = await LoadPageAsync(book, book.Url);
            if (page == null) return;

            book.BaseUrl = page.FinalUrl;
            ApplyBookInfo(book, new Doc(page.Body), page.FinalUrl);
        }

        private static void ApplyBookInfo(Book book, Doc doc, string finalUrl)
        {
            var rule = book.Source.BookInfo;

            // init 规则先把上下文缩小到某个节点 / JSON 对象，其余字段都在它上面求值；
            // "@put:{A:规则,B:规则}" 先把变量存起来（潇湘书院等书源用），字段里再 "@get:{A}" 取回
            if (!String.IsNullOrEmpty(rule.Init) && rule.Init.TrimStart().StartsWith("@put:", StringComparison.OrdinalIgnoreCase))
            {
                PutRules(rule.Init, doc, finalUrl);
                rule.Init = "";
            }

            object initContext = null;
            if (!String.IsNullOrEmpty(rule.Init))
            {
                var initResult = Flatten(RuleEngine.Eval(rule.Init, doc.For(rule.Init)));
                if (initResult.Count > 0)
                {
                    var first = initResult[0];
                    if (first is HtmlNode || first is JsonValue) initContext = first;
                    else
                    {
                        string text = RuleEngine.ToStringValue(first);
                        var json = LooksLikeJson(text) ? LegadoJson.Parse(text) : null;
                        initContext = json != null ? (object)json : RuleEngine.ParseHtml(text);
                    }
                }
            }

            Func<string, string> field = delegate (string r)
            {
                if (String.IsNullOrEmpty(r)) return "";
                return RuleEngine.SelectString(ExpandGet(r), initContext ?? doc.For(r));
            };

            book.Name = Override(CleanField(Trim(field(rule.Name)), "书名"), book.Name);
            book.Author = Override(CleanAuthor(CleanField(field(rule.Author), "作者")), book.Author);
            book.Kind = Override(CleanField(field(rule.Kind), "分类"), book.Kind);
            book.Intro = Override(CleanField(field(rule.Intro), "简介"), book.Intro);
            book.LastChapter = Override(CleanField(Trim(field(rule.LastChapter)), "最新章节"), book.LastChapter);
            book.WordCount = Override(CleanField(field(rule.WordCount), "字数"), book.WordCount);

            string cover = CoverOrEmpty(finalUrl, field(Expand(rule.CoverUrl, book.Source, finalUrl)));
            if (cover.Length > 0) book.CoverUrl = cover;

            string tocUrl = field(Expand(rule.TocUrl, book.Source, finalUrl));
            if (IsUsableLink(tocUrl))
            {
                book.TocUrl = LegadoUrl.ResolveRule(finalUrl, tocUrl);
            }
            else
            {
                // 没配置 tocUrl：目录就在详情页上（Legado 用重定向后的地址）
                string options;
                LegadoUrl.SplitOptions(book.Url, out options);
                book.TocUrl = options == null ? finalUrl : book.Url;
            }
        }

        /// <summary>"@get:{key}" 换成 java.put 存下的值。</summary>
        private static string ExpandGet(string rule)
        {
            if (String.IsNullOrEmpty(rule) || rule.IndexOf("@get:", StringComparison.OrdinalIgnoreCase) < 0) return rule;
            var js = LegadoJs.Current;
            if (js == null) return rule;

            var sb = new StringBuilder(rule.Length);
            int i = 0;
            while (i < rule.Length)
            {
                int at = rule.IndexOf("@get:", i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { sb.Append(rule, i, rule.Length - i); break; }
                int open = rule.IndexOf('{', at + 5);
                int close = open >= 0 ? rule.IndexOf('}', open + 1) : -1;
                if (open != at + 5 || close < 0) { sb.Append(rule, i, rule.Length - i); break; }
                sb.Append(rule, i, at - i);
                sb.Append(js.Get(rule.Substring(open + 1, close - open - 1).Trim()));
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>"@put:{A:规则,B:规则}"：逐条求值并存入变量。</summary>
        private static void PutRules(string init, Doc doc, string finalUrl)
        {
            var js = LegadoJs.Current;
            if (js == null) return;
            string text = init.Trim();
            int open = text.IndexOf('{');
            int close = text.LastIndexOf('}');
            if (open < 0 || close <= open) return;

            // 按顶层逗号切分（值里可能有引号和嵌套括号）
            var parts = SplitMapEntries(text.Substring(open + 1, close - open - 1));
            foreach (var part in parts)
            {
                int colon = RuleEngine.IndexOfTopLevel(part, ":");
                if (colon <= 0) continue;
                string key = part.Substring(0, colon).Trim().Trim('"', '\'');
                string rule = part.Substring(colon + 1).Trim();
                if (key.Length == 0 || rule.Length == 0) continue;
                js.Put(key, Trim(RuleEngine.SelectString(rule, doc.For(rule))));
            }
        }

        /// <summary>按不在引号和括号里的逗号切分。</summary>
        private static List<string> SplitMapEntries(string text)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            int depth = 0;
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
                if (c == '[' || c == '(' || c == '{') { depth++; sb.Append(c); continue; }
                if (c == ']' || c == ')' || c == '}') { if (depth > 0) depth--; sb.Append(c); continue; }
                if (c == ',' && depth == 0) { parts.Add(sb.ToString()); sb.Length = 0; continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>
        /// 规则里含 {{...}} / @html 等我们无法求值的 JS 片段时，
        /// 取到的会是规则的原文（例如 "{{@@div.description@html}}"），
        /// 这种东西不能显示给用户，也不能写进导出文件，这里一律当作空值处理。
        /// </summary>
        public static string CleanField(string value, string fieldName)
        {
            if (String.IsNullOrEmpty(value)) return "";
            if (IsBareLabel(value)) return "";
            if (!LooksLikeRawRule(value)) return value;

            RuleEngine.AddWarning("字段“" + fieldName + "”的规则包含无法求值的表达式，已忽略：" + Shorten(value));
            return "";
        }

        /// <summary>只有标签没有值，例如站点留空时的“作者：”“类型：”。</summary>
        private static bool IsBareLabel(string value)
        {
            string v = value.Trim();
            if (v.Length == 0) return true;
            char last = v[v.Length - 1];
            return (last == '：' || last == ':') && v.Length <= 6;
        }

        /// <summary>去掉站点常见的“作者：”前缀，方便去重与显示。</summary>
        public static string CleanAuthor(string value)
        {
            string v = Trim(value);
            string[] prefixes = { "作者：", "作者:", "作 者：", "作 者:", "作者 ：" };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (v.StartsWith(prefixes[i], StringComparison.Ordinal) && v.Length > prefixes[i].Length)
                {
                    v = v.Substring(prefixes[i].Length).Trim();
                    break;
                }
            }
            return v;
        }

        /// <summary>判断文本是不是没被求值的规则原文。</summary>
        public static bool LooksLikeRawRule(string value)
        {
            if (String.IsNullOrEmpty(value)) return false;

            // {{...}} 模板：{{key}} / {{page}} 是变量，其余是 JS 表达式
            int index = value.IndexOf("{{", StringComparison.Ordinal);
            while (index >= 0)
            {
                int end = value.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (end < 0) break;
                string inner = value.Substring(index + 2, end - index - 2).Trim();
                if (!String.Equals(inner, "key", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(inner, "page", StringComparison.OrdinalIgnoreCase))
                    return true;
                index = value.IndexOf("{{", end + 2, StringComparison.Ordinal);
            }

            // 规则前缀泄漏
            if (value.IndexOf("@html", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (value.IndexOf("@css:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (value.IndexOf("@xpath:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (value.IndexOf("@json:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (value.IndexOf("@js:", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static string Shorten(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 60 ? text : text.Substring(0, 60) + "…";
        }

        private static string Trim(string value)
        {
            return String.IsNullOrEmpty(value) ? "" : value.Trim();
        }

        /// <summary>
        /// 代入 Legado 里不需要执行 JS 的常用变量。代入成 {{=值}}，让规则仍按模板求值
        /// （否则 http://... 这种整串文字会被当成选择器）。
        /// </summary>
        public static string Expand(string rule, BookSource source, string baseUrl)
        {
            if (String.IsNullOrEmpty(rule) || rule.IndexOf("{{", StringComparison.Ordinal) < 0) return rule;
            string site = source == null ? "" : (source.Url ?? "").TrimEnd('/');
            string result = rule;
            result = ReplaceToken(result, "source.bookSourceUrl", site);
            result = ReplaceToken(result, "source.getKey()", site);
            result = ReplaceToken(result, "source.key", site);
            result = ReplaceToken(result, "baseUrl", baseUrl ?? "");
            return result;
        }

        private static string ReplaceToken(string text, string token, string value)
        {
            string a = "{{" + token + "}}";
            string b = "{{ " + token + " }}";
            if (text.IndexOf(a, StringComparison.Ordinal) < 0 && text.IndexOf(b, StringComparison.Ordinal) < 0) return text;
            return text.Replace(a, "{{=" + value + "}}").Replace(b, "{{=" + value + "}}");
        }

        /// <summary>看起来能请求的链接：非空、不是规则原文、地址部分没有空白（纯文本描述不是地址）。</summary>
        public static bool IsUsableLink(string raw)
        {
            string value = Trim(raw);
            if (value.Length == 0 || LooksLikeRawRule(value)) return false;
            if (value.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)) return false;
            string options;
            string address = LegadoUrl.SplitOptions(value, out options);
            if (address.Length == 0) return false;
            for (int i = 0; i < address.Length; i++)
                if (Char.IsWhiteSpace(address[i])) return false;
            return true;
        }

        private static string CoverOrEmpty(string baseUrl, string raw)
        {
            if (!IsUsableLink(raw)) return "";
            string url = LegadoUrl.Resolve(baseUrl, raw.Trim());
            return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "";
        }

        private static bool HasBookInfoRules(BookInfoRule rule)
        {
            return !String.IsNullOrEmpty(rule.Init) || !String.IsNullOrEmpty(rule.Name) || !String.IsNullOrEmpty(rule.Author)
                || !String.IsNullOrEmpty(rule.Kind) || !String.IsNullOrEmpty(rule.Intro) || !String.IsNullOrEmpty(rule.CoverUrl)
                || !String.IsNullOrEmpty(rule.WordCount) || !String.IsNullOrEmpty(rule.TocUrl) || !String.IsNullOrEmpty(rule.LastChapter);
        }

        // ------------------------------------------------------------ 目录

        public async Task<IList<Chapter>> GetChaptersAsync(Book book)
        {
            var chapters = new List<Chapter>();
            LastTocPages = 0;
            if (book == null || book.Source == null) return chapters;
            RuleEngine.ResetWarnings();
            LastError = "";
            LegadoJs.Current = _js;
            _js.SetBook(book);
            _js.SetSource(book.Source);

            var rule = book.Source.Toc;
            if (String.IsNullOrEmpty(rule.ChapterList))
            {
                LastError = "书源没有配置 ruleToc.chapterList";
                return chapters;
            }

            // 目录地址来自详情页的 tocUrl 规则。调用方没先请求详情（比如从书架、
            // 从搜索结果直接加入书架后打开）时必须在这里补上，否则会拿详情页当目录页解析。
            if (String.IsNullOrEmpty(book.TocUrl)) await LoadBookInfoAsync(book);

            string url = String.IsNullOrEmpty(book.TocUrl) ? book.Url : book.TocUrl;
            var first = await LoadPageAsync(book, url);
            if (first == null) return chapters;

            // "-" 前缀表示整份目录倒序（站点按最新在前排列）。分页时要在合并后整体反转，
            // 交给规则引擎逐页反转会把页序打乱。
            string listRule = rule.ChapterList.Trim();
            bool reverse = false;
            if (listRule.StartsWith("-", StringComparison.Ordinal))
            {
                reverse = true;
                listRule = listRule.Substring(1);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visited.Add(LegadoUrl.Normalize(url));
            visited.Add(LegadoUrl.Normalize(first.FinalUrl));

            var firstDoc = new Doc(first.Body);
            ParseTocPage(book.Source, listRule, firstDoc, first.FinalUrl, chapters, seen);
            LastTocPages = 1;

            if (!String.IsNullOrEmpty(rule.NextTocUrl))
            {
                var next = NextTocUrls(Expand(rule.NextTocUrl, book.Source, first.FinalUrl), firstDoc, first.FinalUrl, visited);
                if (next.Count == 1)
                {
                    // 只给出“下一页”：顺着一页一页往下翻
                    string nextUrl = next[0];
                    while (!String.IsNullOrEmpty(nextUrl) && LastTocPages < MaxTocPages)
                    {
                        var page = await FetchPageAsync(book, nextUrl);
                        if (page == null) break;
                        visited.Add(LegadoUrl.Normalize(page.FinalUrl));
                        LastTocPages++;

                        var doc = new Doc(page.Body);
                        int before = chapters.Count;
                        ParseTocPage(book.Source, listRule, doc, page.FinalUrl, chapters, seen);
                        if (chapters.Count == before) break;

                        var more = NextTocUrls(Expand(rule.NextTocUrl, book.Source, page.FinalUrl), doc, page.FinalUrl, visited);
                        nextUrl = more.Count > 0 ? more[0] : null;
                    }
                }
                else if (next.Count > 1)
                {
                    // 一次给出全部分页（常见于下拉选择页码）：并发抓取，按页码顺序合并
                    if (next.Count > MaxTocPages - 1) next = next.GetRange(0, MaxTocPages - 1);
                    var pages = await FetchPagesAsync(book, next);
                    for (int i = 0; i < pages.Length; i++)
                    {
                        if (pages[i] == null) continue;
                        LastTocPages++;
                        ParseTocPage(book.Source, listRule, new Doc(pages[i].Body), pages[i].FinalUrl, chapters, seen);
                    }
                }
            }

            if (reverse) chapters.Reverse();
            for (int i = 0; i < chapters.Count; i++) chapters[i].Index = i;
            if (chapters.Count == 0 && String.IsNullOrEmpty(LastError))
                LastError = "目录规则没有匹配到任何章节（地址：" + first.FinalUrl + "）";
            return chapters;
        }

        private static void ParseTocPage(BookSource source, string listRule, Doc doc, string baseUrl,
                                         List<Chapter> chapters, HashSet<string> seen)
        {
            var rule = source.Toc;
            var items = Flatten(RuleEngine.Eval(listRule, doc.For(listRule)));
            string urlRule = Expand(rule.ChapterUrl, source, baseUrl);

            foreach (var item in items)
            {
                string name = Trim(RuleEngine.SelectString(rule.ChapterName, item));
                string rawUrl = RuleEngine.SelectString(urlRule, item);
                if (String.IsNullOrEmpty(rawUrl)) rawUrl = DefaultLink(item);
                if (!IsUsableLink(rawUrl)) rawUrl = "";
                string chapterUrl = LegadoUrl.ResolveRule(baseUrl, rawUrl);

                if (String.IsNullOrEmpty(name) && String.IsNullOrEmpty(rawUrl)) continue;
                if (!String.IsNullOrEmpty(chapterUrl) && !seen.Add(chapterUrl)) continue;

                bool vip = false;
                if (!String.IsNullOrEmpty(rule.IsVip))
                {
                    string flag = Trim(RuleEngine.SelectString(rule.IsVip, item)).ToLowerInvariant();
                    vip = flag.Length > 0 && flag != "false" && flag != "0" && flag != "null";
                }

                chapters.Add(new Chapter { Name = name, Url = chapterUrl, Index = chapters.Count, IsVip = vip });
            }
        }

        private static List<string> NextTocUrls(string rule, Doc doc, string baseUrl, HashSet<string> visited)
        {
            var output = new List<string>();
            var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in RuleEngine.SelectStrings(rule, doc.For(rule)))
            {
                string value = Trim(raw);
                if (value.Length == 0 || LooksLikeRawRule(value)) continue;
                if (value.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)) continue;
                string url = LegadoUrl.ResolveRule(baseUrl, value);
                string key = LegadoUrl.Normalize(url);
                if (visited.Contains(key) || !local.Add(key)) continue;
                output.Add(url);
            }
            return output;
        }

        private async Task<PageText[]> FetchPagesAsync(Book book, List<string> urls)
        {
            var results = new PageText[urls.Count];
            var gate = new SemaphoreSlim(4);
            var tasks = new List<Task>();
            for (int i = 0; i < urls.Count; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async delegate
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { results[index] = await FetchPageAsync(book, urls[index]).ConfigureAwait(false); }
                    catch (Exception) { results[index] = null; }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results;
        }

        // ------------------------------------------------------------ 正文

        public Task<string> GetContentAsync(Book book, Chapter chapter)
        {
            return GetContentAsync(book, chapter, null);
        }

        /// <summary>
        /// 抓取正文并跟随 nextContentUrl 翻页。nextChapterUrl 为下一章地址：
        /// 很多站点最后一页的“下一页”按钮就指向下一章，碰到它必须停下，否则会把下一章拼进来。
        /// </summary>
        public async Task<string> GetContentAsync(Book book, Chapter chapter, string nextChapterUrl)
        {
            if (book == null || book.Source == null || chapter == null) return "";
            RuleEngine.ResetWarnings();
            LastError = "";
            LegadoJs.Current = _js;
            _js.SetBook(book);
            _js.SetSource(book.Source);

            if (String.IsNullOrEmpty(chapter.Url)) { LastError = "章节地址为空"; return ""; }

            string stopAt = String.IsNullOrEmpty(nextChapterUrl) ? "" : LegadoUrl.Normalize(nextChapterUrl);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string url = chapter.Url;
            var content = new StringBuilder();
            int pages = 0;

            while (!String.IsNullOrEmpty(url) && pages < MaxContentPages)
            {
                pages++;
                visited.Add(LegadoUrl.Normalize(url));

                var page = await FetchPageAsync(book, url);
                // 失败时返回空串、原因留在 LastError：返回提示文字会被当成正文写进缓存
                if (page == null)
                {
                    if (content.Length == 0) return "";
                    break;
                }
                visited.Add(LegadoUrl.Normalize(page.FinalUrl));

                var doc = new Doc(page.Body);
                var rule = book.Source.Content.Content;
                string part = ExtractContent(rule, doc.For(rule), page.FinalUrl, book.Source.Type == ImageSourceType);
                if (!String.IsNullOrEmpty(part))
                {
                    if (content.Length > 0) content.Append('\n');
                    content.Append(part);
                }

                string nextRule = Expand(book.Source.Content.NextContentUrl, book.Source, page.FinalUrl);
                if (String.IsNullOrEmpty(nextRule)) break;

                string next = Trim(RuleEngine.SelectString(nextRule, doc.For(nextRule)));
                if (next.Length == 0 || LooksLikeRawRule(next) ||
                    next.StartsWith("javascript", StringComparison.OrdinalIgnoreCase)) break;

                string nextUrl = LegadoUrl.ResolveRule(page.FinalUrl, next);
                string key = LegadoUrl.Normalize(nextUrl);
                if (visited.Contains(key)) break;
                if (stopAt.Length > 0 && String.Equals(key, stopAt, StringComparison.OrdinalIgnoreCase)) break;
                url = nextUrl;
            }

            if (content.Length == 0 && String.IsNullOrEmpty(LastError) && !String.IsNullOrEmpty(_js.LastError))
                LastError = "JS 求值失败：" + _js.LastError;
            return content.ToString().Trim();
        }

        /// <summary>Legado 书源类型：0 文本、1 音频、2 图片（漫画）、3 文件。</summary>
        public const int AudioSourceType = 1;
        public const int ImageSourceType = 2;

        private static string ExtractContent(string rule, object context, string baseUrl, bool imageSource)
        {
            // JS 规则可能有网络请求、eval 和变量写入，不能先当节点求值一次、再当字符串求值一次。
            var values = RuleEngine.Eval(rule, context);
            var nodes = new List<HtmlNode>();
            foreach (var value in values)
            {
                var node = value as HtmlNode;
                if (node != null) nodes.Add(node);
            }
            if (nodes.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var node in nodes)
                {
                    // 含 <img> 的走 HTML 字符串路径，把图片保留成图片行（漫画、插图）；纯文字仍按节点转纯文本
                    bool hasImage = node.Name == "img" || CssSelector.SelectFirst(node, "img") != null;
                    string text = hasImage
                        ? SanitizeContent(AbsolutizeImages(Html.OuterHtml(node), baseUrl))
                        : Html.ToPlainText(node);
                    if (text.Length == 0) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
                if (sb.Length > 0) return sb.ToString();
            }

            var strings = new List<string>();
            foreach (var value in values) strings.Add(RuleEngine.ToStringValue(value));
            var lines = new List<string>();
            foreach (var s in strings)
            {
                // 漫画书源常直接返回图片地址列表（img@src / @data-original）
                if (imageSource && LooksLikeImageUrl(s))
                {
                    lines.Add(ImageLine(LegadoUrl.Resolve(baseUrl, s.Trim())));
                    continue;
                }
                lines.Add(AbsolutizeImages(s, baseUrl));
            }
            // 规则里带了 @html / @all 时会直接返回 HTML 字符串，
            // 这里必须转成纯文本，否则阅读器里会看到一堆 <p> 标签。
            return SanitizeContent(String.Join("\n", lines.ToArray()));
        }

        // ------------------------------------------------------------ 图片行

        private const char ImageOpen = '\uE000';
        private const char ImageClose = '\uE001';

        private static readonly System.Text.RegularExpressions.Regex ImgTag = new System.Text.RegularExpressions.Regex(
            "<img\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex ImgSrc = new System.Text.RegularExpressions.Regex(
            "\\b(?:data-original|data-src|data-echo|data-lazy-src|src)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>正文里的图片统一写成单独一行的 &lt;img src="绝对地址"&gt;（与 Legado 一致）。</summary>
        public static string ImageLine(string url)
        {
            return "<img src=\"" + url + "\">";
        }

        /// <summary>取出正文行里的图片地址；不是图片行时返回 null。</summary>
        public static string ImageUrlOf(string line)
        {
            if (String.IsNullOrEmpty(line)) return null;
            string t = line.Trim();
            if (!t.StartsWith("<img", StringComparison.OrdinalIgnoreCase)) return null;
            var m = ImgSrc.Match(t);
            if (!m.Success) return null;
            string url = m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value
                       : m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Value;
            url = Html.DecodeEntities(url.Trim());
            return url.Length == 0 ? null : url;
        }

        public static bool HasImages(string content)
        {
            return !String.IsNullOrEmpty(content) && content.IndexOf("<img", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeImageUrl(string value)
        {
            if (!IsUsableLink(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            int q = v.IndexOf('?');
            string path = q >= 0 ? v.Substring(0, q) : v;
            return v.StartsWith("http") || v.StartsWith("//") || v.StartsWith("/") ||
                   path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") ||
                   path.EndsWith(".webp") || path.EndsWith(".gif");
        }

        /// <summary>把 &lt;img&gt; 规范成只含绝对地址的图片标签（懒加载的 data-original 优先于占位 src）。</summary>
        private static string AbsolutizeImages(string html, string baseUrl)
        {
            if (String.IsNullOrEmpty(html) || html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0) return html ?? "";
            return ImgTag.Replace(html, delegate (System.Text.RegularExpressions.Match m)
            {
                string tag = m.Value;
                string best = null;
                foreach (System.Text.RegularExpressions.Match a in ImgSrc.Matches(tag))
                {
                    string v = a.Groups[1].Success && a.Groups[1].Length > 0 ? a.Groups[1].Value
                             : a.Groups[2].Success && a.Groups[2].Length > 0 ? a.Groups[2].Value : a.Groups[3].Value;
                    v = v.Trim();
                    if (v.Length == 0 || v.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                    bool lazy = !a.Value.TrimStart().StartsWith("src", StringComparison.OrdinalIgnoreCase);
                    if (best == null || lazy) best = v;
                    if (lazy) break;
                }
                if (best == null) return "";
                return "\n" + ImageLine(LegadoUrl.Resolve(baseUrl, Html.DecodeEntities(best))) + "\n";
            });
        }

        /// <summary>
        /// 正文清理：与 Legado 的 HtmlFormatter.format 等价——
        /// 去掉 script/style，块级标签转换行，其余标签剔除，实体解码；&lt;img&gt; 保留为单独的图片行。
        /// 已经是纯文本时原样返回，不做多余处理。
        /// </summary>
        public static string SanitizeContent(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            if (text.IndexOf('<') < 0) return Html.DecodeEntities(text).Trim();

            // 先把图片标签换成私用区占位符，转纯文本时不会被当成标签删掉，最后再还原
            var images = new List<string>();
            string protectedText = ImgTag.Replace(text, delegate (System.Text.RegularExpressions.Match m)
            {
                string url = ImageUrlOf(m.Value);
                if (url == null || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "";
                images.Add(url);
                return "\n" + ImageOpen + (images.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ImageClose + "\n";
            });

            string result = Html.DecodeEntities(Html.ToPlainText(protectedText));
            if (images.Count > 0)
            {
                var sb = new StringBuilder(result.Length + images.Count * 40);
                int i = 0;
                while (i < result.Length)
                {
                    char c = result[i];
                    if (c == ImageOpen)
                    {
                        int end = result.IndexOf(ImageClose, i + 1);
                        int index;
                        if (end > i && Int32.TryParse(result.Substring(i + 1, end - i - 1), out index) && index >= 0 && index < images.Count)
                        {
                            sb.Append(ImageLine(images[index]));
                            i = end + 1;
                            continue;
                        }
                    }
                    sb.Append(c);
                    i++;
                }
                // 占位符前后加的换行会在每次清理时累积成空行；有图片时去掉空行，保证反复清理结果不变
                var kept = new List<string>();
                foreach (var line in sb.ToString().Replace("\r\n", "\n").Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length > 0) kept.Add(t);
                }
                result = String.Join("\n", kept.ToArray());
            }
            return result.Trim();
        }

        // ------------------------------------------------------------ 工具

        private sealed class PageText
        {
            public string Body;
            public string FinalUrl;
        }

        /// <summary>取一页：与上次抓到的页面是同一地址时直接复用快照，省一次请求。</summary>
        private async Task<PageText> LoadPageAsync(Book book, string url)
        {
            if (!String.IsNullOrEmpty(book.Snapshot) && LegadoUrl.IsSamePage(book.SnapshotUrl, url))
                return new PageText { Body = book.Snapshot, FinalUrl = book.SnapshotUrl };

            var page = await FetchPageAsync(book, url);
            if (page == null) return null;
            book.Snapshot = page.Body;
            book.SnapshotUrl = page.FinalUrl;
            return page;
        }

        /// <summary>按 Legado URL 规则（含 ,{method/body/charset/headers} 选项）请求一页。</summary>
        private async Task<PageText> FetchPageAsync(Book book, string url)
        {
            string baseUrl = String.IsNullOrEmpty(book.BaseUrl) ? book.Source.Url : book.BaseUrl;
            var rule = UrlRule.Parse(baseUrl, url, null);
            var fetch = await FetchAsync(rule, book.Source.Header).ConfigureAwait(false);
            if (String.IsNullOrEmpty(fetch.Text)) return null;
            return new PageText { Body = fetch.Text, FinalUrl = String.IsNullOrEmpty(fetch.FinalUrl) ? rule.Url : fetch.FinalUrl };
        }

        private Task<FetchResult> FetchAsync(UrlRule rule, string headerLines)
        {
            return FetchAsync(rule, headerLines, 0);
        }

        private async Task<FetchResult> FetchAsync(UrlRule rule, string headerLines, int timeoutSeconds)
        {
            LastUrl = rule.Url;
            foreach (var warning in rule.Warnings) RuleEngine.AddWarning(warning);

            string headers = ExpandHeaders(MergeHeaders(headerLines, rule.HeaderLines), rule.Url);
            var fetch = await HttpFetcher.SendAsync(rule.Url, headers, rule.Method, rule.Body, rule.Charset, timeoutSeconds).ConfigureAwait(false);
            _js.SetPage(fetch.Text, fetch.FinalUrl);
            LastFetch = fetch;
            LastError = fetch.Ok ? "" : fetch.Error;
            if (fetch.Ok && String.IsNullOrEmpty(fetch.Text)) LastError = "响应内容为空：" + rule.Url;
            return fetch;
        }

        /// <summary>header 字段本身是一段 JS（G社漫画：headers={"Referer":baseUrl}…）时求值；不是 JS 原样返回。</summary>
        private string ExpandHeaders(string headers, string url)
        {
            if (String.IsNullOrEmpty(headers)) return headers;
            string trimmed = headers.TrimStart();
            if (!trimmed.StartsWith("<js>", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("@js:", StringComparison.OrdinalIgnoreCase)) return headers;

            string script = RuleEngine.ExtractScript(headers);
            string result = _js.Evaluate(script, url);
            if (!String.IsNullOrEmpty(result)) return result;
            RuleEngine.AddWarning("header 的 JS 求值失败：" + (_js.LastError ?? ""));
            return headers;
        }

        private static string MergeHeaders(string a, string b)
        {
            if (String.IsNullOrEmpty(a)) return b ?? "";
            if (String.IsNullOrEmpty(b)) return a;
            return a + "\n" + b;
        }

        private static bool LooksLikeJson(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            string t = text.TrimStart();
            return t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal);
        }

        /// <summary>
        /// 一份响应的两种视图：HTML 树与 JSON。Legado 按响应内容自动判断默认解析方式，
        /// 所以 JSON 接口上写成 "data.list" 这种不带 $ 的规则也要按 JSONPath 求值。
        /// </summary>
        private sealed class Doc
        {
            public readonly string Body;
            private HtmlNode _root;
            private JsonValue _json;
            private bool _jsonTried;

            public Doc(string body) { Body = body ?? ""; }

            public HtmlNode Root
            {
                get
                {
                    if (_root == null) _root = RuleEngine.ParseHtml(Body);
                    return _root;
                }
            }

            public JsonValue Json
            {
                get
                {
                    if (!_jsonTried)
                    {
                        _jsonTried = true;
                        _json = LooksLikeJson(Body) ? LegadoJson.Parse(Body) : null;
                    }
                    return _json;
                }
            }

            public object For(string rule)
            {
                string r = (rule ?? "").TrimStart();
                while (r.Length > 0 && (r[0] == '-' || r[0] == ':' || r[0] == '+')) r = r.Substring(1).TrimStart();

                if (r.StartsWith("@css:", StringComparison.OrdinalIgnoreCase) ||
                    r.StartsWith("@xpath:", StringComparison.OrdinalIgnoreCase) ||
                    r.StartsWith("/", StringComparison.Ordinal))
                    return Root;

                if (r.StartsWith("$", StringComparison.Ordinal) || r.StartsWith("@json:", StringComparison.OrdinalIgnoreCase))
                    return (object)Json ?? Body;

                if (Json != null) return Json;
                return Root;
            }
        }

        /// <summary>JSON 规则常返回数组（如 $.data.list），这里把数组展开成逐项，与 Legado 的列表语义一致。</summary>
        private static List<object> Flatten(List<object> items)
        {
            bool hasArray = false;
            for (int i = 0; i < items.Count; i++)
            {
                var json = items[i] as JsonValue;
                if (json != null && json.Kind == JsonKind.Array) { hasArray = true; break; }
            }
            if (!hasArray) return items;

            var output = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var json = items[i] as JsonValue;
                if (json != null && json.Kind == JsonKind.Array && json.Items != null)
                {
                    for (int j = 0; j < json.Items.Count; j++) output.Add(json.Items[j]);
                    continue;
                }
                output.Add(items[i]);
            }
            return output;
        }

        private static string Override(string value, string fallback)
        {
            return String.IsNullOrEmpty(value) ? (fallback ?? "") : value;
        }

        /// <summary>规则为空时的链接回落：项自身是 a 则取 href，否则取第一个后代 a 的 href。</summary>
        private static string DefaultLink(object item)
        {
            var node = item as HtmlNode;
            if (node == null || !node.IsElement) return "";
            if (node.Name == "a")
            {
                string href = node.GetAttribute("href");
                if (!String.IsNullOrEmpty(href)) return href;
            }
            var anchor = CssSelector.SelectFirst(node, "a");
            return anchor == null ? "" : (anchor.GetAttribute("href") ?? "");
        }
    }
}
