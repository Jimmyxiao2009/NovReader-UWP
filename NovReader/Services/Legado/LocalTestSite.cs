using System;
using System.Collections.Generic;
using System.Text;
using NovReader.Models;

namespace NovReader.Services.Legado
{
    public sealed class LocalPage
    {
        public byte[] Bytes;
        /// <summary>为 null 时由 HttpFetcher 从 meta charset 嗅探。</summary>
        public string Charset;
        public string ContentType = "text/html";
    }

    /// <summary>
    /// 内置的离线测试站点。命中 local.test 的请求由 HttpFetcher 直接返回这里构造的页面，
    /// 因此搜索 → 详情 → 目录 → 正文 全流程可以完全不依赖外网地验证。
    /// 故意混合了 UTF-8 / GBK、相对链接、JSON 接口和分页正文。
    /// </summary>
    public static class LocalTestSite
    {
        public const string Host = "local.test";

        public static bool Handles(string url)
        {
            if (String.IsNullOrEmpty(url)) return false;
            try
            {
                var uri = new Uri(url);
                return String.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        public static LocalPage Get(string url)
        {
            Uri uri;
            try { uri = new Uri(url); }
            catch (Exception) { return null; }

            string path = uri.AbsolutePath.ToLowerInvariant();
            string query = uri.Query ?? "";

            switch (path)
            {
                case "/search":
                    return Html(SearchPage(QueryValue(query, "q")), null);
                case "/search-gbk":
                    return Html(SearchPage(QueryValue(query, "q")), "gbk");
                case "/search-json":
                    return Json(SearchJson(QueryValue(query, "q")));
                case "/book/1":
                    return Html(BookPage(), null);
                case "/book/2":
                    return Html(BookPage(), null);
                case "/book/1/toc":
                    return Html(TocPage(), null);
                case "/book/1/ch1.html":
                    return Html(ChapterPage("第一章 启程", "第一段：测试正文。", "第二段：CSS 选择器取值成功。"), null);
                case "/book/1/ch2.html":
                    // 故意用 GBK + meta 声明，验证编码嗅探
                    return Html(ChapterPage("第二章 风起", "这一章是 GBK 编码，用于验证解码路径。", "第二段中文内容。", "", "gbk"), null, true);
                case "/book/1/ch3.html":
                    return Html(ChapterPage("第三章 入海", "第一章 第一页内容。", "", "<a id='nextPage' href='ch3_2.html'>下一页</a>"), null);
                case "/book/1/ch3_2.html":
                    return Html(ChapterPage("第三章 入海", "第一章 第二页内容。"), null);

                // 分页目录：每页 2 章，共 3 页，带“下一页”和页码下拉两种翻页方式
                case "/book/3/toc":
                    return Html(PagedTocPage(QueryValue(query, "p")), null);

                // 目录是 JSON 接口，规则不写 $ 前缀
                case "/api/toc":
                    return Json("{\"code\":0,\"data\":{\"chapters\":[" +
                                "{\"title\":\"序章\",\"url\":\"/book/1/ch1.html\"}," +
                                "{\"title\":\"第一章\",\"url\":\"/book/1/ch2.html\"}]}}");

                // 搜索唯一命中时站点直接返回详情页
                case "/search-direct":
                    return Html(BookPage(), null);

                // 正文最后一页的“下一页”链接指向下一章
                case "/book/5/c1.html":
                    return Html(ChapterPage("第一章", "第一章正文。", "", "<a id='nextPage' href='c2.html'>下一页</a>"), null);
                case "/book/5/c2.html":
                    return Html(ChapterPage("第二章", "第二章正文。"), null);

                // 漫画：懒加载图片（真实地址在 data-original，src 是占位图）
                case "/comic/1.html":
                    return Html("<!DOCTYPE html><html><head><meta charset='utf-8'></head><body><div id='comic'>" +
                                "<img src='/img/loading.gif' data-original='/pic/001.jpg'>" +
                                "<img src='/img/loading.gif' data-original='https://cdn.test/pic/002.jpg'>" +
                                "</div></body></html>", null);
            }
            return null;
        }

        private static string PagedTocPage(string p)
        {
            int page;
            if (!Int32.TryParse(p, out page) || page < 1) page = 1;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'><title>目录</title></head><body><ul id='list'>");
            for (int i = 1; i <= 2; i++)
            {
                int n = (page - 1) * 2 + i;
                sb.Append("<li><a href='/book/3/c").Append(n).Append(".html'>第").Append(n).Append("章</a></li>");
            }
            sb.Append("</ul><select id='pages'>");
            for (int i = 1; i <= 3; i++)
                sb.Append("<option value='/book/3/toc?p=").Append(i).Append("'>第").Append(i).Append("页</option>");
            sb.Append("</select>");
            if (page < 3) sb.Append("<a id='next' href='/book/3/toc?p=").Append(page + 1).Append("'>下一页</a>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>内置示例书源，供离线自测使用（HTML 与 JSON 两种解析路径）。</summary>
        public static List<BookSource> SampleSources()
        {
            var html = new BookSource
            {
                Name = "本地测试-HTML",
                Group = "内置示例",
                Comment = "离线测试站点，验证 CSS 选择器与相对链接",
                Url = "https://local.test/",
                SearchUrl = "https://local.test/search?q={{key}}"
            };
            html.Search.BookList = "@css:.book-list .book";
            html.Search.Name = "@css:h3.title a @text";
            html.Search.Author = "@css:.author @text";
            html.Search.Kind = "@css:.kind @text";
            html.Search.Intro = "@css:.intro @text";
            html.Search.BookUrl = "@css:h3.title a @href";
            html.BookInfo.Name = "@css:.book-name @text";
            html.BookInfo.Author = "@css:.author @text";
            html.BookInfo.Intro = "@css:.intro @text";
            html.BookInfo.TocUrl = "@css:.toc-link @href";
            html.Toc.ChapterList = "@css:#list dd";
            html.Toc.ChapterName = "@css:a @text";
            html.Toc.ChapterUrl = "@css:a @href";
            html.Content.Content = "@css:#content";
            html.Content.NextContentUrl = "@css:#nextPage @href";

            var json = new BookSource
            {
                Name = "本地测试-JSON",
                Group = "内置示例",
                Comment = "离线 JSON 接口，验证 JSONPath 规则",
                Url = "https://local.test/",
                SearchUrl = "https://local.test/search-json?q={{key}}"
            };
            json.Search.BookList = "$.data.list";
            json.Search.Name = "$.name";
            json.Search.Author = "$.author";
            json.Search.BookUrl = "$.bookUrl";
            json.BookInfo.TocUrl = "@css:.toc-link @href";
            json.Toc.ChapterList = "@css:#list dd";
            json.Toc.ChapterName = "@css:a @text";
            json.Toc.ChapterUrl = "@css:a @href";
            json.Content.Content = "@css:#content";

            var list = new List<BookSource>();
            list.Add(html);
            list.Add(json);
            return list;
        }

        private static string QueryValue(string query, string name)        {
            if (String.IsNullOrEmpty(query)) return "";
            string q = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
            foreach (var pair in q.Split('&'))
            {
                int index = pair.IndexOf('=');
                if (index <= 0) continue;
                if (String.Equals(pair.Substring(0, index), name, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair.Substring(index + 1).Replace("+", " "));
            }
            return "";
        }

        private static LocalPage Html(string html, string charset, bool gbk = false)
        {
            var encoding = gbk ? HttpFetcher.GetEncoding("gbk") : new UTF8Encoding(false);
            return new LocalPage
            {
                Bytes = encoding.GetBytes(html),
                Charset = charset,
                ContentType = "text/html"
            };
        }

        private static LocalPage Json(string json)
        {
            return new LocalPage
            {
                Bytes = new UTF8Encoding(false).GetBytes(json),
                Charset = null,
                ContentType = "application/json"
            };
        }

        // ------------------------------------------------------------ 页面内容

        private static string SearchPage(string keyword)
        {
            return "<!DOCTYPE html><html><head><meta charset='utf-8'><title>搜索 " + keyword + "</title></head><body>" +
                "<div class='wrap'><ul class='book-list'>" +
                "<li class='book'><h3 class='title'><a href='/book/1'>星辰之主</a></h3>" +
                "<span class='author'>张三</span><span class='kind'>玄幻</span>" +
                "<p class='intro'>测试用的小说简介一。</p></li>" +
                "<li class='book'><h3 class='title'><a href='/book/2'>深海迷航</a></h3>" +
                "<span class='author'>李四</span><span class='kind'>科幻</span>" +
                "<p class='intro'>测试用的小说简介二。</p></li>" +
                "</ul></div></body></html>";
        }

        private static string SearchJson(string keyword)
        {
            return "{\"code\":0,\"msg\":\"ok\",\"data\":{\"list\":[" +
                "{\"name\":\"JSON 书一\",\"author\":\"作者甲\",\"bookUrl\":\"/book/1\"}," +
                "{\"name\":\"JSON 书二\",\"author\":\"作者乙\",\"bookUrl\":\"/book/2\"}]}}";
        }

        private static string BookPage()
        {
            return "<!DOCTYPE html><html><head><meta charset='utf-8'><title>星辰之主</title></head><body>" +
                "<div id='info'>" +
                "<h1 class='book-name'>星辰之主</h1>" +
                "<p class='author'>作者：张三</p>" +
                "<p class='kind'>分类：玄幻</p>" +
                "<div class='intro'>这是一本用于验证解析流程的测试书。</div>" +
                "<img class='cover' src='/img/1.jpg'/>" +
                "<a class='toc-link' href='/book/1/toc'>查看目录</a>" +
                "</div></body></html>";
        }

        private static string TocPage()
        {
            return "<!DOCTYPE html><html><head><meta charset='utf-8'><title>目录</title></head><body>" +
                "<div id='list'><dl>" +
                "<dd><a href='ch1.html'>第一章 启程</a></dd>" +
                "<dd><a href='ch2.html'>第二章 风起</a></dd>" +
                "<dd><a href='ch3.html'>第三章 入海</a></dd>" +
                "</dl></div></body></html>";
        }

        private static string ChapterPage(string title, string paragraph1, string paragraph2 = "", string extra = "", string metaCharset = "utf-8")
        {
            return "<!DOCTYPE html><html><head><meta charset='" + metaCharset + "'><title>" + title + "</title></head><body>" +
                "<h1>" + title + "</h1>" +
                "<div id='content'>" +
                "<p>" + paragraph1 + "</p>" +
                (String.IsNullOrEmpty(paragraph2) ? "" : "<p>" + paragraph2 + "</p>") +
                "<script>var ad = 1;</script>" +
                "</div>" +
                "<div class='ad'>这是广告，位于正文容器之外，规则不应取到。</div>" +
                extra + "</body></html>";
        }
    }
}
