using System;

namespace NovReader.Models
{
    /// <summary>Legado（阅读）格式的搜索规则。</summary>
    public sealed class SearchRule
    {
        public string BookList { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Kind { get; set; }
        public string Intro { get; set; }
        public string LastChapter { get; set; }
        public string CoverUrl { get; set; }
        public string BookUrl { get; set; }

        public SearchRule()
        {
            BookList = ""; Name = ""; Author = ""; Kind = ""; Intro = "";
            LastChapter = ""; CoverUrl = ""; BookUrl = "";
        }
    }

    /// <summary>书籍详情规则。</summary>
    public sealed class BookInfoRule
    {
        public string Init { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Kind { get; set; }
        public string LastChapter { get; set; }
        public string Intro { get; set; }
        public string CoverUrl { get; set; }
        public string WordCount { get; set; }
        public string TocUrl { get; set; }

        public BookInfoRule()
        {
            Init = ""; Name = ""; Author = ""; Kind = ""; LastChapter = "";
            Intro = ""; CoverUrl = ""; WordCount = ""; TocUrl = "";
        }
    }

    /// <summary>目录（章节列表）规则。</summary>
    public sealed class TocRule
    {
        public string ChapterList { get; set; }
        public string ChapterName { get; set; }
        public string ChapterUrl { get; set; }
        public string IsVip { get; set; }
        public string UpdateTime { get; set; }
        /// <summary>目录分页：下一页（或全部分页）地址。</summary>
        public string NextTocUrl { get; set; }

        public TocRule()
        {
            ChapterList = ""; ChapterName = ""; ChapterUrl = "";
            IsVip = ""; UpdateTime = ""; NextTocUrl = "";
        }
    }

    /// <summary>正文规则。</summary>
    public sealed class ContentRule
    {
        public string Content { get; set; }
        public string NextContentUrl { get; set; }

        public ContentRule() { Content = ""; NextContentUrl = ""; }
    }

    /// <summary>书源。字段命名对齐 Legado 的 bookSource* / rule* 结构。全部用属性以便 XAML 绑定。</summary>
    public sealed class BookSource
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Group { get; set; }
        public string Comment { get; set; }
        public int Type { get; set; }
        public bool Enabled { get; set; }

        public string SearchUrl { get; set; }
        public string ExploreUrl { get; set; }

        public SearchRule Search { get; set; }
        public BookInfoRule BookInfo { get; set; }
        public TocRule Toc { get; set; }
        public ContentRule Content { get; set; }

        /// <summary>附加请求头，格式 "Name: Value"，每行一个。</summary>
        public string Header { get; set; }

        public BookSource()
        {
            Name = ""; Url = ""; Group = ""; Comment = ""; Enabled = true;
            SearchUrl = ""; ExploreUrl = ""; Header = "";
            Search = new SearchRule();
            BookInfo = new BookInfoRule();
            Toc = new TocRule();
            Content = new ContentRule();
        }

        /// <summary>列表里的主标题。</summary>
        public string DisplayName
        {
            get { return String.IsNullOrEmpty(Name) ? "未命名书源" : Name; }
        }

        /// <summary>列表里的副标题：分组 + 站点域名 + 规则完整度提示。</summary>
        public string DisplayDetail
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (!String.IsNullOrEmpty(Group)) parts.Add(Group);
                string host = HostOf(Url);
                if (!String.IsNullOrEmpty(host)) parts.Add(host);
                if (String.IsNullOrEmpty(SearchUrl)) parts.Add("无搜索地址");
                else if (String.IsNullOrEmpty(Search.BookList) || String.IsNullOrEmpty(Search.Name)) parts.Add("搜索规则不完整");
                return String.Join(" · ", parts.ToArray());
            }
        }

        /// <summary>是否具备跑通搜索所需的最小规则。</summary>
        public bool IsUsable
        {
            get
            {
                return !String.IsNullOrEmpty(SearchUrl)
                    && !String.IsNullOrEmpty(Search.BookList)
                    && !String.IsNullOrEmpty(Search.Name);
            }
        }

        public string StatusText
        {
            get
            {
                if (!Enabled) return "已停用";
                if (!IsUsable) return "规则不完整";
                return "可用";
            }
        }

        private static string HostOf(string url)
        {
            if (String.IsNullOrEmpty(url)) return "";
            try { return new Uri(url).Host; }
            catch (Exception) { return url; }
        }

        public override string ToString()
        {
            if (String.IsNullOrEmpty(Name)) return "未命名书源";
            if (String.IsNullOrEmpty(Url)) return Name;
            return Name + "  (" + Url + ")";
        }
    }
}
