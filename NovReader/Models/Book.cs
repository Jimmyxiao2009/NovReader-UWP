using System;

namespace NovReader.Models
{
    /// <summary>搜索结果或详情页得到的一本书。</summary>
    public sealed class Book
    {
        public string Name { get; set; }
        public string Author { get; set; }
        public string Kind { get; set; }
        public string Intro { get; set; }
        public string CoverUrl { get; set; }
        public string LastChapter { get; set; }
        public string WordCount { get; set; }
        public string Url { get; set; }
        /// <summary>目录页地址，未解析时为 null。</summary>
        public string TocUrl { get; set; }
        public BookSource Source { get; set; }
        /// <summary>最近一次抓取到该书的页面 HTML，用于免去重复请求。</summary>
        public string Snapshot { get; set; }
        /// <summary>Snapshot 对应的页面地址。</summary>
        public string SnapshotUrl { get; set; }
        /// <summary>相对链接的解析基准。</summary>
        public string BaseUrl { get; set; }

        public Book()
        {
            Name = ""; Author = ""; Kind = ""; Intro = ""; CoverUrl = "";
            LastChapter = ""; WordCount = ""; Url = ""; BaseUrl = "";
            Snapshot = ""; SnapshotUrl = "";
        }

        public override string ToString()
        {
            if (String.IsNullOrEmpty(Author)) return Name;
            return Name + "  " + Author;
        }
    }
}
