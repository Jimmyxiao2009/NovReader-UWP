using System;
using System.Collections.Generic;
using System.ComponentModel;
using NovReader.Models;
using NovReader.Services;

namespace NovReader.ViewModels
{
    /// <summary>
    /// 一条聚合搜索结果：同一本书（书名+作者归一化后相同）在多个书源里的命中合并为一条，
    /// 第一个到达的书源作为默认来源，其余留作“换源”候选。
    /// </summary>
    public sealed class SearchResultItem : INotifyPropertyChanged
    {
        private readonly List<Book> _origins = new List<Book>();

        public SearchResultItem(Book book, BookSource source, string keyword, int arrival)
        {
            if (book.Source == null) book.Source = source;
            _origins.Add(book);
            Key = SearchRanking.BookKey(book.Name, book.Author);
            Score = SearchRanking.Score(book.Name, book.Author, keyword);
            Arrival = arrival;
        }

        public string Key { get; private set; }

        /// <summary>与关键字的相关度，越大越靠前。</summary>
        public int Score { get; private set; }

        /// <summary>到达顺序，用于“按到达顺序”排序和同分时的稳定排序。</summary>
        public int Arrival { get; private set; }

        public Book Book { get { return _origins[0]; } }
        public BookSource Source { get { return Book.Source; } }

        /// <summary>所有命中这本书的书源（第一个为默认来源）。</summary>
        public IList<Book> Origins { get { return _origins; } }

        public int OriginCount { get { return _origins.Count; } }

        /// <summary>合并另一个书源的同一本书；缺失的展示字段用它补齐。</summary>
        public void AddOrigin(Book book)
        {
            if (book == null) return;
            for (int i = 0; i < _origins.Count; i++)
                if (String.Equals(_origins[i].Url, book.Url, StringComparison.OrdinalIgnoreCase)) return;
            _origins.Add(book);
            Raise("SourceLabel");
            Raise("OriginCount");
        }

        public string Name { get { return Book.Name; } }

        public string AuthorLine
        {
            get
            {
                var parts = new List<string>();
                parts.Add(String.IsNullOrEmpty(Book.Author) ? "未知作者" : Book.Author);
                string kind = FirstNonEmpty(delegate (Book b) { return b.Kind; });
                if (!String.IsNullOrEmpty(kind)) parts.Add(Shorten(kind, 16));
                string words = FirstNonEmpty(delegate (Book b) { return b.WordCount; });
                if (!String.IsNullOrEmpty(words)) parts.Add(words);
                return String.Join(" · ", parts.ToArray());
            }
        }

        /// <summary>作者 · 分类 · 最新章节，空的自动省略。</summary>
        public string Subtitle
        {
            get
            {
                string last = FirstNonEmpty(delegate (Book b) { return b.LastChapter; });
                return String.IsNullOrEmpty(last) ? AuthorLine : AuthorLine + " · 最新：" + last;
            }
        }

        public string LatestLine
        {
            get
            {
                string last = FirstNonEmpty(delegate (Book b) { return b.LastChapter; });
                return String.IsNullOrEmpty(last) ? "" : "最新：" + last;
            }
        }

        public string IntroLine
        {
            get
            {
                string intro = FirstNonEmpty(delegate (Book b) { return b.Intro; });
                if (String.IsNullOrEmpty(intro)) return "";
                return Shorten(intro.Replace("\r", " ").Replace("\n", " ").Trim(), 80);
            }
        }

        public string CoverUrl
        {
            get
            {
                string cover = FirstNonEmpty(delegate (Book b) { return b.CoverUrl; });
                if (String.IsNullOrEmpty(cover)) return null;
                Uri uri;
                return Uri.TryCreate(cover, UriKind.Absolute, out uri) ? cover : null;
            }
        }

        public string CoverPlaceholder
        {
            get { return ShelfBook.FirstLetter(Book.Name); }
        }

        public string SourceLabel
        {
            get
            {
                string name = Source == null ? "未知书源" : Source.Name;
                return _origins.Count <= 1 ? name : name + " 等 " + _origins.Count + " 个书源";
            }
        }

        public string ShelfGlyph
        {
            get { return AppState.InShelf(Book.Url) ? "\uE73E" : "\uE710"; }
        }

        public string ShelfLabel
        {
            get { return AppState.InShelf(Book.Url) ? "已在书架" : "加入书架"; }
        }

        /// <summary>加入书架后通知界面刷新按钮。</summary>
        public void NotifyShelfChanged()
        {
            Raise("ShelfLabel");
            Raise("ShelfGlyph");
        }

        private string FirstNonEmpty(Func<Book, string> pick)
        {
            for (int i = 0; i < _origins.Count; i++)
            {
                string value = pick(_origins[i]);
                if (!String.IsNullOrEmpty(value)) return value.Trim();
            }
            return "";
        }

        private static string Shorten(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
