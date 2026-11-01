using System;
using NovReader.Models;

namespace NovReader.Views
{
    /// <summary>书籍详情页的启动参数。</summary>
    public sealed class BookDetailRequest
    {
        public Book Book;
        public BookSource Source;
        public ShelfBook Shelf;
        /// <summary>聚合搜索里同一本书在其它书源的命中，供“换源”直接使用。</summary>
        public System.Collections.Generic.IList<Book> Alternatives;
    }
}
