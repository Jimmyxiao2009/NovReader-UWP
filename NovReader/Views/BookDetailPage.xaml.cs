using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;
using NovReader.Services.Legado;

namespace NovReader.Views
{
    /// <summary>
    /// 书籍详情：展示封面/作者/分类/简介，加载目录，支持换源、多线程下载全本与多格式导出。
    /// 从搜索结果、书架、阅读器都能进来。
    /// </summary>
    public sealed partial class BookDetailPage : Page
    {
        private static BookDetailRequest _pending;

        private Book _book;
        private BookSource _source;
        private ShelfBook _shelf;
        private List<Chapter> _chapters = new List<Chapter>();
        private readonly List<Book> _alternatives = new List<Book>();
        private CancellationTokenSource _transferCancel;
        private bool _transferring;
        private bool _loading;
        private bool _tocReversed;

        public BookDetailPage()
        {
            InitializeComponent();
        }

        /// <summary>从搜索结果打开。</summary>
        public static void Open(Book book, BookSource source)
        {
            Open(book, source, null);
        }

        /// <summary>从聚合搜索结果打开，alternatives 是同一本书在其它书源的命中。</summary>
        public static void Open(Book book, BookSource source, IList<Book> alternatives)
        {
            if (book == null) return;
            _pending = new BookDetailRequest { Book = book, Source = source, Alternatives = alternatives };
            Nav.ToLogged(typeof(BookDetailPage), _pending);
        }

        /// <summary>从书架打开。</summary>
        public static void OpenShelf(ShelfBook item)
        {
            if (item == null) return;
            _pending = new BookDetailRequest { Shelf = item };
            Nav.ToLogged(typeof(BookDetailPage), _pending);
        }

        private BookDetailRequest _request;

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 导航参数随返回栈保存：从阅读器返回时页面是新建的，靠它恢复（以前这里会变成空白页）
            var request = e.Parameter as BookDetailRequest ?? _pending;
            _pending = null;

            if (request == null)
            {
                if (_book == null) HeaderText.Text = "书籍详情";
                else RefreshShelfButton();
                return;
            }
            _request = request;

            // 返回栈里的旧页面：书架条目可能已在别的详情页换了源，这时以书架条目为准，
            // 否则会显示旧书源、点「加入书架」还会再加一条重复的
            if (e.NavigationMode == NavigationMode.Back && request.Book != null)
            {
                var entry = request.Shelf != null && AppState.Shelf.Contains(request.Shelf)
                    ? request.Shelf
                    : AppState.FindShelf(request.Book.Url) ?? AppState.FindShelfByTitle(request.Book.Name, request.Book.Author);
                if (entry != null && !String.Equals(entry.BookUrl, request.Book.Url, StringComparison.Ordinal))
                {
                    request.Book = null;
                    request.Shelf = entry;
                }
            }

            // request.Book 带着完整详情（换源成功后会更新成新书源的那本），优先用它
            if (request.Book != null)
            {
                _book = request.Book;
                _source = request.Source ?? request.Book.Source;
                _shelf = AppState.FindShelf(_book.Url);
                if (_shelf != null) request.Shelf = _shelf;
            }
            else if (request.Shelf != null)
            {
                _shelf = request.Shelf;
                _source = AppState.FindSource(_shelf.SourceName);
                _book = BookFromShelf(_shelf, _source);
            }
            else
            {
                HeaderText.Text = "书籍详情";
                return;
            }

            _alternatives.Clear();
            if (request.Alternatives != null) _alternatives.AddRange(request.Alternatives);

            Bind();
            await LoadDetailAsync(false);
        }

        private static Book BookFromShelf(ShelfBook shelf, BookSource source)
        {
            return new Book
            {
                Name = shelf.Name,
                Author = shelf.Author,
                CoverUrl = shelf.CoverUrl,
                Intro = shelf.Intro,
                Url = shelf.BookUrl,
                TocUrl = shelf.TocUrl,
                Source = source,
                BaseUrl = source == null ? shelf.BookUrl : source.Url
            };
        }

        // ---------------------------------------------------------------- 展示

        private void Bind()
        {
            HeaderText.Text = String.IsNullOrEmpty(_book.Name) ? "书籍详情" : _book.Name;
            NameText.Text = String.IsNullOrEmpty(_book.Name) ? "未命名" : _book.Name;
            AuthorText.Text = String.IsNullOrEmpty(_book.Author) ? "未知作者" : _book.Author;
            CoverPlaceholder.Text = ShelfBook.FirstLetter(_book.Name);

            var meta = new List<string>();
            if (!String.IsNullOrEmpty(_book.Kind)) meta.Add(_book.Kind);
            if (!String.IsNullOrEmpty(_book.WordCount)) meta.Add(_book.WordCount);
            if (!String.IsNullOrEmpty(_book.LastChapter)) meta.Add("最新：" + _book.LastChapter);
            MetaText.Text = meta.Count == 0 ? "" : String.Join(" · ", meta.ToArray());
            MetaText.Visibility = meta.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            string sourceLine = _source == null ? "来源：未知书源" : "来源：" + _source.Name;
            if (_alternatives.Count > 1) sourceLine += "（共 " + _alternatives.Count + " 个书源可换）";
            SourceText.Text = sourceLine;
            IntroText.Text = String.IsNullOrEmpty(_book.Intro) ? "（暂无简介）" : _book.Intro.Trim();

            SetCover(_book.CoverUrl);
            RefreshShelfButton();
        }

        private void SetCover(string url)
        {
            CoverImage.Source = null;
            if (String.IsNullOrEmpty(url)) return;
            try
            {
                CoverImage.Source = new BitmapImage(new Uri(url)) { DecodePixelWidth = 192 };
            }
            catch (Exception)
            {
                // 封面地址非法时保持占位文字
            }
        }

        private void RefreshShelfButton()
        {
            _shelf = AppState.FindShelf(_book == null ? "" : _book.Url);
            bool inShelf = _shelf != null;
            ShelfButton.Label = inShelf ? "移出书架" : "加入书架";
            ShelfButton.Icon = new SymbolIcon(inShelf ? Symbol.Remove : Symbol.Add);
            ReadButton.Label = inShelf && _shelf.ChapterCount > 0 ? "继续阅读" : "开始阅读";
        }

        private void SetBusy(bool busy)
        {
            _loading = busy;
            LoadProgress.IsIndeterminate = true;
            LoadProgress.Visibility = busy || _transferring ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.IsEnabled = !busy;
            SwitchSourceButton.IsEnabled = !busy && !_transferring;
            // 加载 / 换源进行中不允许开始阅读、点章节或改书架，否则会拿到一半切换的状态
            ReadButton.IsEnabled = !busy;
            ShelfButton.IsEnabled = !busy;
            ChapterView.IsItemClickEnabled = !busy;
        }

        /// <summary>后台补全详情字段（用独立引擎，不影响「调试」页记录的主请求）。</summary>
        private async void RefreshInfoQuietly(Book book)
        {
            try
            {
                var info = new Book
                {
                    Name = book.Name, Author = book.Author, Url = book.Url, BaseUrl = book.BaseUrl,
                    Source = book.Source, Intro = book.Intro, CoverUrl = book.CoverUrl, TocUrl = book.TocUrl
                };
                await new LegadoEngine().GetBookInfoAsync(info);
                if (book != _book) return;
                if (String.IsNullOrEmpty(book.Kind)) book.Kind = info.Kind;
                if (String.IsNullOrEmpty(book.LastChapter)) book.LastChapter = info.LastChapter;
                if (String.IsNullOrEmpty(book.WordCount)) book.WordCount = info.WordCount;
                if (String.IsNullOrEmpty(book.CoverUrl)) book.CoverUrl = info.CoverUrl;
                if (String.IsNullOrEmpty(book.Intro)) book.Intro = info.Intro;
                Bind();
            }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- 数据

        /// <summary>加载详情与目录。force 为 true 时忽略本地目录缓存并重新请求详情页。</summary>
        private async Task LoadDetailAsync(bool force)
        {
            if (_source == null)
            {
                TocStatusText.Text = "找不到书源「" + (_shelf == null ? "" : _shelf.SourceName) + "」，可点「换源」换一个。";
                return;
            }

            // 目录没准备好之前不允许下载/导出
            DownloadButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            ReverseTocButton.IsEnabled = false;
            TransferStatusText.Text = "";
            SetBusy(true);

            var book = _book;
            var engine = new LegadoEngine();
            try
            {
                book.Source = _source;
                if (String.IsNullOrEmpty(book.BaseUrl)) book.BaseUrl = _source.Url;

                // 目录优先读本地缓存，避免每次进来都重新抓一遍目录页
                if (!force)
                {
                    var cached = await AppState.TocCache.GetAsync(book.Url);
                    if (book != _book) return;
                    if (cached != null && cached.Count > 0)
                    {
                        ApplyChapters(cached);
                        TocStatusText.Text = "共 " + cached.Count + " 章（本地缓存，可在「…」里重新获取）";
                        AppState.Log("目录[" + book.Name + "] " + cached.Count + " 章（本地缓存）");
                        // 从书架进来时只有书名/作者/简介，分类、最新章节等在后台补上，不影响目录
                        if (String.IsNullOrEmpty(book.LastChapter) && String.IsNullOrEmpty(book.Kind))
                            RefreshInfoQuietly(book);
                        return;
                    }
                }

                // 详情页决定了目录地址（ruleBookInfo.tocUrl）。搜索结果自带简介时也必须请求，
                // 否则 tocUrl 为空，会拿详情页当目录页解析，目录永远为空。
                if (force || String.IsNullOrEmpty(book.TocUrl))
                {
                    TocStatusText.Text = "正在加载详情…";
                    await engine.GetBookInfoAsync(book);
                    AppState.LastUsedEngine = engine;
                    if (book != _book) return;
                    Bind();
                }

                TocStatusText.Text = "正在加载目录…";
                var chapters = await engine.GetChaptersAsync(book);
                AppState.LastUsedEngine = engine;
                if (book != _book) return;

                var list = new List<Chapter>(chapters);
                if (list.Count == 0)
                {
                    ApplyChapters(list);
                    string reason = engine.LastError;
                    TocStatusText.Text = "目录为空" + (String.IsNullOrEmpty(reason) ? "" : "：" + reason);
                    TransferStatusText.Text = "这个书源没取到目录。可以点「换源」试试其它书源，或到「调试」页查看请求详情。";
                    AppState.Log("目录[" + book.Name + "] 为空：" + reason);
                    return;
                }

                await AppState.TocCache.PutAsync(book.Url, list);
                ApplyChapters(list);
                int pages = engine.LastTocPages;
                TocStatusText.Text = "共 " + list.Count + " 章" + (pages > 1 ? "（目录共 " + pages + " 页）" : "");
                AppState.Log("目录[" + book.Name + "] " + list.Count + " 章，" + pages + " 页（已写入本地缓存）");

                if (_shelf != null)
                {
                    _shelf.ChapterCount = list.Count;
                    if (!String.IsNullOrEmpty(book.TocUrl)) _shelf.TocUrl = book.TocUrl;
                    await AppState.SaveShelfAsync();
                    RefreshShelfButton();
                }
            }
            catch (Exception ex)
            {
                TocStatusText.Text = "加载失败：" + ex.Message;
                AppState.Log("详情/目录加载失败：" + ex.Message);
            }
            finally
            {
                if (book == _book) SetBusy(false);
            }
        }

        /// <summary>应用目录列表并同步下载/导出按钮状态。</summary>
        private void ApplyChapters(List<Chapter> chapters)
        {
            _chapters = new List<Chapter>(chapters);
            for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;
            ShowChapters();

            DownloadButton.IsEnabled = _chapters.Count > 0 && !_transferring;
            ExportButton.IsEnabled = _chapters.Count > 0 && !_transferring;
            ReverseTocButton.IsEnabled = _chapters.Count > 1;

            // 书架里的书直接把目录滚到正在读的那一章
            if (_shelf != null && _shelf.ChapterIndex > 0 && _shelf.ChapterIndex < _chapters.Count && !_tocReversed)
                ChapterView.ScrollIntoView(_chapters[_shelf.ChapterIndex]);
        }

        private void ShowChapters()
        {
            if (!_tocReversed)
            {
                ChapterView.ItemsSource = _chapters;
                ReverseTocButton.Content = "倒序";
                return;
            }
            var reversed = new List<Chapter>(_chapters);
            reversed.Reverse();
            ChapterView.ItemsSource = reversed;
            ReverseTocButton.Content = "正序";
        }

        // ---------------------------------------------------------------- 交互

        private void ReverseToc_Click(object sender, RoutedEventArgs e)
        {
            _tocReversed = !_tocReversed;
            ShowChapters();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null || _loading) return;
            // 清掉快照与目录地址，强制重新请求详情页和目录页
            _book.Snapshot = "";
            _book.SnapshotUrl = "";
            _book.TocUrl = "";
            await LoadDetailAsync(true);
        }

        private async void Shelf_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null) return;

            if (AppState.InShelf(_book.Url))
            {
                var existing = AppState.FindShelf(_book.Url);
                AppState.RemoveFromShelf(existing);
                AppState.Log("移出书架：" + _book.Name);
            }
            else
            {
                var item = AppState.AddToShelf(_book, _source);
                if (item != null && _chapters.Count > 0) item.ChapterCount = _chapters.Count;
                AppState.Log("加入书架：" + _book.Name);
            }
            RefreshShelfButton();

            try { await AppState.SaveShelfAsync(); }
            catch (Exception ex) { AppState.Log("保存书架失败：" + ex.Message); }
        }

        private void Read_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null) return;

            AppState.Log("详情页点开始阅读：" + _book.Name + "，目录 " + _chapters.Count + " 章");

            var shelf = AppState.FindShelf(_book.Url);
            if (shelf != null)
            {
                ReaderLauncher.Open(shelf);
                return;
            }

            ReaderLauncher.Open(_book, _source);
        }

        private void Chapter_Click(object sender, ItemClickEventArgs e)
        {
            var chapter = e.ClickedItem as Chapter;
            if (chapter == null || _book == null || _loading) return;

            var shelf = AppState.FindShelf(_book.Url);
            if (shelf != null)
            {
                ReaderLauncher.Open(shelf, chapter.Index);
                return;
            }

            ReaderLauncher.Open(_book, _source, chapter.Index);
        }

        // ---------------------------------------------------------------- 换源

        private async void SwitchSource_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null || _loading || _transferring) return;

            // 从书架进来时没有候选：在全部书源里按书名搜一遍，只留书名+作者一致的
            if (_alternatives.Count <= 1) await FindAlternativesAsync();
            if (_book == null) return;

            var candidates = new List<Book>();
            for (int i = 0; i < _alternatives.Count; i++)
                if (!String.Equals(_alternatives[i].Url, _book.Url, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(_alternatives[i]);

            if (candidates.Count == 0)
            {
                TransferStatusText.Text = "没有在其它书源里找到《" + _book.Name + "》。";
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 360,
                ItemContainerStyle = (Style)Application.Current.Resources["PlainListViewItemStyle"]
            };
            var labels = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var b = candidates[i];
                string label = (b.Source == null ? "未知书源" : b.Source.Name);
                if (!String.IsNullOrEmpty(b.LastChapter)) label += "\n最新：" + b.LastChapter;
                labels.Add(label);
            }
            list.ItemsSource = labels;
            list.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                Title = "换源（" + candidates.Count + " 个）",
                Content = list,
                PrimaryButtonText = "换到这个书源",
                SecondaryButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            int index = list.SelectedIndex;
            if (index < 0 || index >= candidates.Count) return;

            await SwitchToAsync(candidates[index]);
        }

        private async Task FindAlternativesAsync()
        {
            if (!_alternatives.Exists(delegate (Book b) { return b.Url == _book.Url; })) _alternatives.Insert(0, _book);

            var sources = new List<BookSource>(AppState.Sources);
            string key = SearchRanking.BookKey(_book.Name, _book.Author);
            string nameKey = SearchRanking.Normalize(_book.Name);
            var found = new List<Book>();
            var sync = new object();

            TransferStatusText.Text = "正在其它书源里查找《" + _book.Name + "》…";
            SetBusy(true);
            // 上千个书源全部跑完要好几分钟：找到足够多的候选或超过 25 秒就停
            const int Enough = 8;
            var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            bool done = false;
            try
            {
                await MultiSearch.WhenDoneOrCancelled(MultiSearch.SearchAsync(
                    sources, _book.Name, 1, MultiSearch.ClampConcurrency(AppState.Settings.SearchThreads),
                    delegate (IList<Book> books)
                    {
                        lock (sync)
                        {
                            for (int i = 0; i < books.Count; i++)
                            {
                                var b = books[i];
                                // 作者缺失时只比书名，避免漏掉不写作者的书源
                                bool same = SearchRanking.BookKey(b.Name, b.Author) == key ||
                                            (SearchRanking.Normalize(b.Name) == nameKey &&
                                             (String.IsNullOrEmpty(b.Author) || String.IsNullOrEmpty(_book.Author)));
                                if (same) found.Add(b);
                            }
                            if (found.Count >= Enough) cancel.Cancel();
                        }
                    },
                    delegate (MultiSearchProgress p)
                    {
                        var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                        {
                            // 搜索结束后迟到的进度不能再盖掉结果提示
                            if (done || cancel.IsCancellationRequested) return;
                            TransferStatusText.Text = "正在其它书源里查找《" + _book.Name + "》… " + p.Done + "/" + p.Total
                                                      + "，已找到 " + found.Count + " 个";
                        });
                    },
                    cancel.Token, AppState.SearchOptions()), cancel.Token);
                var ignoredSave = AppState.SaveHealthAsync();
            }
            catch (Exception ex)
            {
                AppState.Log("换源搜索失败：" + ex.Message);
            }
            finally { done = true; SetBusy(false); }

            lock (sync)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    var b = found[i];
                    if (!_alternatives.Exists(delegate (Book x) { return String.Equals(x.Url, b.Url, StringComparison.OrdinalIgnoreCase); }))
                        _alternatives.Add(b);
                }
            }
            TransferStatusText.Text = "";
            // 记进返回栈参数：从阅读器回来再点「换源」不用重新搜 25 秒
            if (_request != null) _request.Alternatives = new List<Book>(_alternatives);
            Bind();
        }

        /// <summary>
        /// 换源是“先试后换”：先在新书源上拿到详情和目录，成功了才切换页面、写书架；
        /// 失败就保持原书源不动。以前是先改书架再加载，新书源一失败，书架条目就坏了，详情和目录全没了。
        /// </summary>
        private async Task SwitchToAsync(Book target)
        {
            var oldBook = _book;
            string oldName = _source == null ? "未知书源" : _source.Name;
            string newName = target.Source == null ? "未知书源" : target.Source.Name;
            var shelf = _shelf;

            SetBusy(true);
            TransferStatusText.Text = "正在从「" + newName + "」获取详情和目录…";
            List<Chapter> chapters = null;
            string reason = "";
            try
            {
                var engine = new LegadoEngine();
                if (String.IsNullOrEmpty(target.BaseUrl) && target.Source != null) target.BaseUrl = target.Source.Url;

                var cached = await AppState.TocCache.GetAsync(target.Url);
                if (cached != null && cached.Count > 0)
                {
                    chapters = cached;
                }
                else
                {
                    if (String.IsNullOrEmpty(target.TocUrl)) await engine.GetBookInfoAsync(target);
                    chapters = new List<Chapter>(await engine.GetChaptersAsync(target));
                    reason = engine.LastError;
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            if (_book != oldBook) { SetBusy(false); return; } // 期间页面已换成别的书

            if (chapters == null || chapters.Count == 0)
            {
                SetBusy(false);
                TransferStatusText.Text = "「" + newName + "」没取到目录" + (String.IsNullOrEmpty(reason) ? "" : "（" + reason + "）")
                                          + "，仍使用「" + oldName + "」。可以再选一个书源。";
                AppState.Log("换源失败：《" + oldBook.Name + "》 " + oldName + " → " + newName + "：" + reason);
                return;
            }

            // 新书源缺的展示字段沿用原来的，避免换源后简介、封面变空
            if (String.IsNullOrEmpty(target.Intro)) target.Intro = oldBook.Intro;
            if (String.IsNullOrEmpty(target.CoverUrl)) target.CoverUrl = oldBook.CoverUrl;
            if (String.IsNullOrEmpty(target.Author)) target.Author = oldBook.Author;
            if (String.IsNullOrEmpty(target.Kind)) target.Kind = oldBook.Kind;

            // 先把所有同步状态一次改完（页面、书架条目、返回栈参数），再做需要等待的保存，
            // 最后才放开按钮——中间放开的话，点「开始阅读」会按新地址再加一条书架记录
            _book = target;
            _source = target.Source;
            _tocReversed = false;
            if (shelf != null)
            {
                shelf = AppState.SwitchShelfSource(shelf, target);
                shelf.ChapterCount = chapters.Count;
                if (shelf.ChapterIndex >= chapters.Count) shelf.ChapterIndex = Math.Max(0, chapters.Count - 1);
            }
            if (_request != null) { _request.Book = target; _request.Source = target.Source; _request.Shelf = shelf; }
            Bind();
            ApplyChapters(chapters);
            TocStatusText.Text = "共 " + chapters.Count + " 章";
            TransferStatusText.Text = "已换到「" + newName + "」。";
            AppState.Log("换源：《" + target.Name + "》 " + oldName + " → " + newName + "，目录 " + chapters.Count + " 章");

            try
            {
                await AppState.TocCache.PutAsync(target.Url, chapters);
                if (shelf != null) await AppState.SaveShelfAsync();
            }
            catch (Exception ex) { AppState.Log("保存书架失败：" + ex.Message); }
            finally { SetBusy(false); }
        }

        // ---------------------------------------------------------------- 下载全本

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_transferring) return;
            if (!EnsureChapters()) return;

            int threads = DownloadService.ClampThreads(AppState.Transfer.Threads);
            bool comic = _source != null && _source.Type == LegadoEngine.ImageSourceType;
            if (comic) threads = Math.Min(threads, AppState.Settings.ComicParallelDownload
                ? Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, AppState.Settings.ComicImageThreads)) : 1);
            var confirm = new ContentDialog
            {
                Title = "下载全本",
                Content = "将用 " + threads + " 个线程下载《" + _book.Name + "》的 " + _chapters.Count
                          + (comic ? " 章及全部图片并保存到本地，之后离线也能阅读。" : " 章正文并保存到本地，之后离线也能阅读。")
                          + (AppState.Transfer.SkipCached ? "完整缓存的章节会跳过，缺失的图片会补齐。" : "")
                          + "确定开始吗？",
                PrimaryButtonText = "开始下载",
                SecondaryButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            BeginTransfer("正在下载…");
            try
            {
                var result = await DownloadService.DownloadBookAsync(
                    _book, _chapters, threads, AppState.Transfer.SkipCached, OnTransferProgress, _transferCancel.Token);
                TransferStatusText.Text = "下载完成：" + result.Summary;
            }
            catch (Exception ex)
            {
                TransferStatusText.Text = "下载失败：" + ex.Message;
            }
            finally { EndTransfer(); }
        }

        // ---------------------------------------------------------------- 导出

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_transferring) return;
            if (!EnsureChapters()) return;

            var formatCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            formatCombo.ItemsSource = new[]
            {
                TransferSettings.FormatName(ExportFormat.Txt),
                TransferSettings.FormatName(ExportFormat.Html),
                TransferSettings.FormatName(ExportFormat.Markdown),
                TransferSettings.FormatName(ExportFormat.Epub)
            };
            formatCombo.SelectedIndex = (int)AppState.Transfer.Format;

            var encodingCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
            encodingCombo.ItemsSource = new[]
            {
                TransferSettings.EncodingName(ExportEncoding.Utf8),
                TransferSettings.EncodingName(ExportEncoding.Utf8Bom),
                TransferSettings.EncodingName(ExportEncoding.Utf16Le),
                TransferSettings.EncodingName(ExportEncoding.Gbk),
                TransferSettings.EncodingName(ExportEncoding.Big5)
            };
            encodingCombo.SelectedIndex = (int)AppState.Transfer.Encoding;

            var headerCheck = new CheckBox { Content = "包含书名与作者信息", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
            var hint = new TextBlock
            {
                Text = "共 " + _chapters.Count + " 章，使用 " + DownloadService.ClampThreads(AppState.Transfer.Threads) + " 个线程获取正文。",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "文件格式" });
            panel.Children.Add(formatCombo);
            panel.Children.Add(new TextBlock { Text = "文件编码", Margin = new Thickness(0, 10, 0, 0) });
            panel.Children.Add(encodingCombo);
            panel.Children.Add(headerCheck);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                Title = "导出《" + _book.Name + "》",
                Content = panel,
                PrimaryButtonText = "开始导出",
                SecondaryButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            var format = (ExportFormat)Math.Max(0, formatCombo.SelectedIndex);
            var encoding = (ExportEncoding)Math.Max(0, encodingCombo.SelectedIndex);

            AppState.Transfer.Format = format;
            AppState.Transfer.Encoding = encoding;
            AppState.Settings.ExportFormat = format;
            AppState.Settings.ExportEncoding = encoding;
            await AppState.SaveSettingsAsync();

            BeginTransfer("正在导出…");
            string path = null;
            try
            {
                path = await ExportService.ExportAsync(new ExportService.ExportRequest
                {
                    Book = _book,
                    Chapters = _chapters,
                    Format = format,
                    Encoding = encoding,
                    Threads = DownloadService.ClampThreads(AppState.Transfer.Threads),
                    IncludeHeader = headerCheck.IsChecked == true
                }, OnTransferProgress, _transferCancel.Token);

                TransferStatusText.Text = "导出完成：" + path;
            }
            catch (Exception ex)
            {
                TransferStatusText.Text = "导出失败：" + ex.Message;
                AppState.Log("导出失败：" + ex.Message);
            }
            finally { EndTransfer(); }

            if (String.IsNullOrEmpty(path)) return;
            await OfferSaveAsAsync(path, format);
        }

        /// <summary>导出成功后询问是否另存到用户指定位置。</summary>
        private async Task OfferSaveAsAsync(string path, ExportFormat format)
        {
            // 等上一个对话框完全关闭，否则可能被 Dialogs 判为“已有对话框打开”而直接跳过
            await Task.Delay(250);

            var ask = new ContentDialog
            {
                Title = "导出完成",
                Content = "文件已保存到：\n" + path + "\n\n要另存到其它位置吗？",
                PrimaryButtonText = "另存为…",
                SecondaryButtonText = "不用了"
            };
            if (await Dialogs.ShowAsync(ask) != ContentDialogResult.Primary) return;

            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add(
                    TransferSettings.FormatName(format),
                    new List<string> { TransferSettings.ExtensionOf(format) });
                picker.SuggestedFileName = _book.Name;

                var target = await picker.PickSaveFileAsync();
                if (target == null) return;

                string saved = await ExportService.SaveAsAsync(path, target);
                TransferStatusText.Text = "已另存到：" + saved;
            }
            catch (Exception ex)
            {
                TransferStatusText.Text = "另存失败：" + ex.Message;
            }
        }

        // ---------------------------------------------------------------- 传输状态

        private bool EnsureChapters()
        {
            if (_book == null) return false;
            if (_chapters.Count == 0)
            {
                TransferStatusText.Text = "目录还没加载好，请等目录加载完成后再试。";
                return false;
            }
            return true;
        }

        private void BeginTransfer(string message)
        {
            _transferring = true;
            _transferCancel = new CancellationTokenSource();
            LoadProgress.Visibility = Visibility.Visible;
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Value = 0;
            DownloadButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            SwitchSourceButton.IsEnabled = false;
            CancelTransferButton.IsEnabled = true;
            TransferStatusText.Text = message;
        }

        private void EndTransfer()
        {
            _transferring = false;
            LoadProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
            LoadProgress.IsIndeterminate = true;
            DownloadButton.IsEnabled = _chapters.Count > 0;
            ExportButton.IsEnabled = _chapters.Count > 0;
            SwitchSourceButton.IsEnabled = !_loading;
            CancelTransferButton.IsEnabled = false;
            if (_transferCancel != null) { _transferCancel.Dispose(); _transferCancel = null; }
        }

        private void CancelTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (_transferCancel == null) return;
            _transferCancel.Cancel();
            TransferStatusText.Text = "正在停止…";
        }

        private void OnTransferProgress(TransferProgress p)
        {
            // 回调来自线程池，必须切回 UI 线程
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                if (!_transferring) return;
                LoadProgress.Value = p.Percent;
                var sb = new StringBuilder();
                sb.Append(p.Percent).Append("%（").Append(p.Done).Append('/').Append(p.Total).Append("）");
                if (p.Skipped > 0) sb.Append(" 跳过 ").Append(p.Skipped);
                if (p.Failed > 0) sb.Append(" 失败 ").Append(p.Failed);
                if (!String.IsNullOrEmpty(p.Current)) sb.Append(" · ").Append(p.Current);
                TransferStatusText.Text = sb.ToString();
            });
        }
    }
}
