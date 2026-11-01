using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using NovReader.Models;
using NovReader.Services.Legado;
namespace NovReader.Services
{
    /// <summary>
    /// 应用级共享状态：书源、书架、阅读设置、引擎实例。
    /// 各页面通过它读写同一份数据，避免每次导航都重新加载文件。
    /// </summary>
    public static class AppState
    {
        public static ObservableCollection<BookSource> Sources { get; private set; }
        public static ObservableCollection<ShelfBook> Shelf { get; private set; }
        public static ReadingSettings Settings { get; private set; }
        public static TransferSettings Transfer { get; private set; }

        public static LegadoEngine Engine { get; private set; }

        /// <summary>
        /// 最近一次完成请求的引擎，供「调试」页显示请求详情。
        /// 各页面用各自的引擎实例（共享一个会让 LastError 互相覆盖），用完后报到这里。
        /// </summary>
        public static LegadoEngine LastUsedEngine { get; set; }

        public static SourceStore SourceStore { get; private set; }
        public static ShelfStore ShelfStore { get; private set; }
        public static SettingsStore SettingsStore { get; private set; }
        public static ChapterCache ChapterCache { get; private set; }
        public static ComicImageCache ComicImageCache { get; private set; }
        public static ReadingBookmarkStore BookmarkStore { get; private set; }
        public static TocCache TocCache { get; private set; }
        public static SourceHealth Health { get; private set; }
        public static SourceHealthStore HealthStore { get; private set; }

        /// <summary>最近一次操作的说明文本，供“调试”页展示。</summary>
        public static List<string> DebugLog { get; private set; }

        private static bool _loaded;
        private static readonly object _gate = new object();

        static AppState()
        {
            Sources = new ObservableCollection<BookSource>();
            Shelf = new ObservableCollection<ShelfBook>();
            Settings = new ReadingSettings();
            Transfer = new TransferSettings();
            Engine = new LegadoEngine();
            SourceStore = new SourceStore();
            ShelfStore = new ShelfStore();
            SettingsStore = new SettingsStore();
            ChapterCache = new ChapterCache();
            ComicImageCache = new ComicImageCache(delegate
            {
                return System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "comic-image-cache");
            });
            ComicImageCache.Diagnostics = Legado.Js.JscBackend.TraceStage;
            BookmarkStore = new ReadingBookmarkStore(delegate
            {
                return System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "reading-bookmarks.json");
            });
            TocCache = new TocCache();
            Health = new SourceHealth();
            HealthStore = new SourceHealthStore();
            DebugLog = new List<string>();
        }

        /// <summary>首次进入时加载本地数据；重复调用只会加载一次。</summary>
        public static async Task EnsureLoadedAsync()
        {
            lock (_gate)
            {
                if (_loaded) return;
            }

            var sources = await SourceStore.LoadAsync();
            Sources.Clear();
            foreach (var s in sources) Sources.Add(s);

            var shelf = await ShelfStore.LoadAsync();
            Shelf.Clear();
            foreach (var b in shelf) Shelf.Add(b);

            var settings = await SettingsStore.LoadAsync();
            CopySettings(settings, Settings);
            CopyTransfer(settings, Transfer);
            await HealthStore.LoadAsync(Health);

            lock (_gate) { _loaded = true; }

            Log("载入本地数据：书源 " + Sources.Count + " 个，书架 " + Shelf.Count + " 本。");
        }

        public static async Task SaveSourcesAsync()
        {
            await SourceStore.SaveAsync(Sources);
        }

        public static async Task SaveShelfAsync()
        {
            await ShelfStore.SaveAsync(Shelf);
        }

        public static async Task SaveSettingsAsync()
        {
            await SettingsStore.SaveAsync(Settings);
        }

        public static async Task SaveHealthAsync()
        {
            await HealthStore.SaveAsync(Health);
        }

        /// <summary>聚合搜索的统一选项（健康度排序 / 跳过失效书源 / 搜索超时）。</summary>
        public static MultiSearchOptions SearchOptions()
        {
            return new MultiSearchOptions
            {
                Health = Health,
                SkipFailing = Settings.SkipFailingSources,
                TimeoutSeconds = MultiSearch.DefaultTimeoutSeconds
            };
        }

        // ---------------------------------------------------------------- 书源

        public static BookSource FindSource(string name)
        {
            if (String.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Sources.Count; i++)
                if (String.Equals(Sources[i].Name, name, StringComparison.Ordinal)) return Sources[i];
            return null;
        }

        /// <summary>按名字去重合并书源，返回新增数量。</summary>
        public static int MergeSources(IEnumerable<BookSource> sources)
        {
            int added = 0;
            foreach (var source in sources)
            {
                if (FindSource(source.Name) != null) continue;
                Sources.Add(source);
                added++;
            }
            return added;
        }

        // ---------------------------------------------------------------- 书架

        public static ShelfBook FindShelf(string bookUrl)
        {
            if (String.IsNullOrEmpty(bookUrl)) return null;
            for (int i = 0; i < Shelf.Count; i++)
                if (String.Equals(Shelf[i].BookUrl, bookUrl, StringComparison.Ordinal)) return Shelf[i];
            return null;
        }

        public static bool InShelf(string bookUrl) { return FindShelf(bookUrl) != null; }

        /// <summary>把搜索结果加入书架；已存在时只补全缺失信息。</summary>
        public static ShelfBook AddToShelf(Book book, BookSource source)
        {
            if (book == null || String.IsNullOrEmpty(book.Url)) return null;

            var existing = FindShelf(book.Url);
            if (existing != null)
            {
                if (String.IsNullOrEmpty(existing.CoverUrl)) existing.CoverUrl = book.CoverUrl ?? "";
                if (String.IsNullOrEmpty(existing.Intro)) existing.Intro = book.Intro ?? "";
                if (String.IsNullOrEmpty(existing.TocUrl)) existing.TocUrl = book.TocUrl ?? "";
                return existing;
            }

            var item = new ShelfBook
            {
                BookUrl = book.Url,
                Name = book.Name,
                Author = book.Author,
                CoverUrl = book.CoverUrl,
                Intro = book.Intro,
                TocUrl = book.TocUrl,
                SourceName = source != null ? source.Name : "",
                AddedTime = DateTime.Now,
                LastReadTime = DateTime.Now
            };
            Shelf.Insert(0, item);
            return item;
        }

        /// <summary>
        /// 书架条目换到另一个书源：书籍地址、目录地址、书源名都换成新的，阅读进度（第几章）保留。
        /// 章节数清零，等新书源的目录加载后再写回。
        /// </summary>
        public static ShelfBook SwitchShelfSource(ShelfBook item, Book book)
        {
            if (item == null || book == null || String.IsNullOrEmpty(book.Url)) return item;

            // 书架里已经有新地址的条目（同一本书从两个书源都加过）：合并成一条，保留最近读的进度
            var existing = FindShelf(book.Url);
            if (existing != null && !ReferenceEquals(existing, item))
            {
                if (existing.LastReadTime > item.LastReadTime)
                {
                    item.ChapterIndex = existing.ChapterIndex;
                    item.ChapterName = existing.ChapterName;
                    item.ScrollRatio = existing.ScrollRatio;
                    item.LastReadTime = existing.LastReadTime;
                }
                Shelf.Remove(existing);
            }

            item.BookUrl = book.Url;
            item.TocUrl = book.TocUrl ?? "";
            item.SourceName = book.Source != null ? book.Source.Name : "";
            if (!String.IsNullOrEmpty(book.CoverUrl)) item.CoverUrl = book.CoverUrl;
            if (!String.IsNullOrEmpty(book.Intro)) item.Intro = book.Intro;
            item.ChapterCount = 0;
            return item;
        }

        /// <summary>按书名+作者找书架条目（地址换过源后对不上时用）。</summary>
        public static ShelfBook FindShelfByTitle(string name, string author)
        {
            string key = SearchRanking.BookKey(name, author);
            for (int i = 0; i < Shelf.Count; i++)
                if (SearchRanking.BookKey(Shelf[i].Name, Shelf[i].Author) == key) return Shelf[i];
            return null;
        }

        public static void RemoveFromShelf(ShelfBook item)
        {
            if (item == null) return;
            Shelf.Remove(item);
        }

        /// <summary>记录阅读进度。</summary>
        public static void UpdateProgress(ShelfBook item, int chapterIndex, string chapterName,
            double scrollRatio, int chapterCount)
        {
            if (item == null) return;
            item.ChapterIndex = chapterIndex;
            item.ChapterName = chapterName ?? "";
            item.ScrollRatio = scrollRatio;
            if (chapterCount > 0) item.ChapterCount = chapterCount;
            item.LastReadTime = DateTime.Now;
        }

        // ---------------------------------------------------------------- 调试日志

        public static void Log(string message)
        {
            if (String.IsNullOrEmpty(message)) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            DebugLog.Insert(0, line);
            while (DebugLog.Count > 200) DebugLog.RemoveAt(DebugLog.Count - 1);

            // 同时落盘，便于应用崩溃或退出后回看（真机上也能导出排查）
            try
            {
                var ignored = Windows.Storage.ApplicationData.Current.LocalFolder
                    .CreateFileAsync("app.log", Windows.Storage.CreationCollisionOption.OpenIfExists)
                    .AsTask();
                ignored.ContinueWith(delegate (Task<Windows.Storage.StorageFile> t)
                {
                    if (t.Status != TaskStatus.RanToCompletion || t.Result == null) return;
                    var write = Windows.Storage.FileIO.AppendTextAsync(t.Result, line + Environment.NewLine).AsTask();
                    var swallow = write.ContinueWith(delegate (Task x) { var e = x.Exception; });
                });
            }
            catch (Exception)
            {
                // 日志写盘失败不能影响功能
            }
        }

        public static string DebugLogText
        {
            get
            {
                if (DebugLog.Count == 0) return "还没有操作记录。";
                return String.Join(Environment.NewLine, DebugLog.ToArray());
            }
        }

        private static void CopySettings(ReadingSettings from, ReadingSettings to)
        {
            to.FontSize = from.FontSize;
            to.LineHeight = from.LineHeight;
            to.ParagraphSpacing = from.ParagraphSpacing;
            to.FontFamily = from.FontFamily;
            to.Theme = from.Theme;
            to.PageTurn = from.PageTurn;
            to.ShowBars = from.ShowBars;
            to.Padding = from.Padding;
            to.Indent = from.Indent;
            to.BlankLineBetweenParagraphs = from.BlankLineBetweenParagraphs;
            to.KeepAwake = from.KeepAwake;
            to.AppTheme = from.AppTheme;
            // 搜索页读的是 Settings.SearchThreads；这几项漏拷会让设置重启后失效，下次保存还会被默认值覆盖
            to.DownloadThreads = DownloadService.ClampThreads(from.DownloadThreads);
            to.SearchThreads = MultiSearch.ClampConcurrency(from.SearchThreads);
            to.ComicParallelDownload = from.ComicParallelDownload;
            to.ComicImageThreads = Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, from.ComicImageThreads));
            to.SkipCached = from.SkipCached;
            to.SkipFailingSources = from.SkipFailingSources;
            to.ExportFormat = from.ExportFormat;
            to.ExportEncoding = from.ExportEncoding;
        }

        private static void CopyTransfer(ReadingSettings from, TransferSettings to)
        {
            to.Threads = DownloadService.ClampThreads(from.DownloadThreads);
            to.SearchThreads = MultiSearch.ClampConcurrency(from.SearchThreads);
            to.SkipCached = from.SkipCached;
            to.Format = from.ExportFormat;
            to.Encoding = from.ExportEncoding;
        }
    }
}
