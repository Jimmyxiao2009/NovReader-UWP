using System;
using NovReader.Models;
using NovReader.Views;

namespace NovReader.Services
{
    /// <summary>
    /// 打开阅读器的统一入口。阅读器需要的数据通过 <see cref="ReaderRequest"/> 传递，
    /// 避免把对象序列化成导航参数。
    /// </summary>
    public static class ReaderLauncher
    {
        /// <summary>待打开的阅读请求，导航前设置，ReaderPage 读取后清空。</summary>
        public static ReaderRequest Pending { get; set; }

        /// <summary>从书架打开（已有进度，直接跳到上次位置）。</summary>
        public static void Open(ShelfBook item)
        {
            if (item == null) return;
            Pending = new ReaderRequest { Shelf = item, Resume = true };
            Go();
        }

        /// <summary>从书架打开并指定章节。</summary>
        public static void Open(ShelfBook item, int chapterIndex)
        {
            if (item == null) return;
            Pending = new ReaderRequest
            {
                Shelf = item,
                Resume = false,
                StartChapterIndex = chapterIndex
            };
            Go();
        }

        /// <summary>从搜索结果或详情页打开（尚未加入书架，从头读）。</summary>
        public static void Open(Book book, BookSource source, int startChapter = 0)
        {
            if (book == null) return;
            Pending = new ReaderRequest
            {
                Book = book,
                Source = source,
                StartChapterIndex = startChapter,
                Resume = false
            };
            Go();
        }

        private static void Go()
        {
            Nav.ToLogged(typeof(ReaderPage), Pending);
        }
    }

    /// <summary>阅读器启动参数。</summary>
    public sealed class ReaderRequest
    {
        /// <summary>来自书架时直接给出条目。</summary>
        public ShelfBook Shelf { get; set; }
        /// <summary>来自搜索结果时给出书籍与书源。</summary>
        public Book Book { get; set; }
        public BookSource Source { get; set; }
        public int StartChapterIndex { get; set; }
        /// <summary>是否恢复上次阅读进度。</summary>
        public bool Resume { get; set; }
    }
}
