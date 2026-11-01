using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;
using NovReader.ViewModels;

namespace NovReader.Views
{
    /// <summary>
    /// 搜索页：单书源与多书源聚合搜索。
    /// 聚合结果按“书名+作者”合并成一条（记住所有命中的书源，详情页可换源），
    /// 默认按与关键字的相关度排序，边搜边插入到正确位置。
    /// </summary>
    public sealed partial class SearchPage : Page, IShellPage
    {
        private enum SortMode { Relevance = 0, Arrival = 1, Name = 2, SourceCount = 3 }

        private readonly ObservableCollection<SearchResultItem> _results = new ObservableCollection<SearchResultItem>();
        private readonly Dictionary<string, SearchResultItem> _byKey = new Dictionary<string, SearchResultItem>(StringComparer.Ordinal);
        /// <summary>按到达顺序保存的全部结果（含精准搜索过滤掉之前的），切换排序/过滤时不丢数据。</summary>
        private readonly List<SearchResultItem> _all = new List<SearchResultItem>();
        private readonly SearchHistoryStore _historyStore = new SearchHistoryStore();
        private readonly List<string> _history = new List<string>();

        private CancellationTokenSource _cancel;
        /// <summary>每次新搜索递增；线程池回调带着旧的代号回来时直接丢弃，避免上一次搜索的结果混进来。</summary>
        private int _generation;
        private bool _running;
        private string _keyword = "";
        private int _arrival;
        private int _hitCount;
        private int _skipped;
        private SortMode _sort = SortMode.Relevance;
        private bool _historyLoaded;

        /// <summary>
        /// XAML 里的初始选中项会在 InitializeComponent 期间就触发 SelectionChanged/Checked，
        /// 那时后面的字段还是 null，直接访问会空引用崩溃。所以初始化完成前一律忽略事件。
        /// </summary>
        private bool _uiReady;

        public SearchPage()
        {
            InitializeComponent();
            ResultsView.ItemsSource = _results;
            SourceBox.ItemsSource = AppState.Sources;
            _uiReady = true;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_historyLoaded)
            {
                _historyLoaded = true;
                await LoadHistoryAsync();
            }
            UpdateSourceBoxState();
            if (!_running && _results.Count == 0) RefreshStatusIdle();
            UpdatePanels();
        }

        /// <summary>从详情页 / 阅读器返回时刷新“已在书架”标记。</summary>
        public void OnShellResumed()
        {
            for (int i = 0; i < _results.Count; i++) _results[i].NotifyShelfChanged();
        }

        // ---------------------------------------------------------------- 书源与历史

        private bool SingleSourceMode { get { return ScopeBox.SelectedIndex == 1; } }

        private void Scope_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            UpdateSourceBoxState();
            if (!_running) RefreshStatusIdle();
        }

        private void UpdateSourceBoxState()
        {
            bool one = SingleSourceMode;
            SourceBox.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
            if (one && SourceBox.SelectedItem == null && AppState.Sources.Count > 0)
                SourceBox.SelectedIndex = 0;
        }

        private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady || _running) return;
            RefreshStatusIdle();
        }

        private void Precise_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            if (_all.Count > 0) Rebuild();
        }

        private async Task LoadHistoryAsync()
        {
            var loaded = await _historyStore.LoadAsync();
            _history.Clear();
            _history.AddRange(loaded);
            RefreshHistoryList();
        }

        private void RefreshHistoryList()
        {
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = new List<string>(_history);
            UpdatePanels();
        }

        /// <summary>没有结果、也没在搜索时显示最近搜索。</summary>
        private void UpdatePanels()
        {
            bool showHistory = !_running && _results.Count == 0 && _history.Count > 0;
            HistoryPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
            ResultsView.Visibility = showHistory ? Visibility.Collapsed : Visibility.Visible;
            SortButton.IsEnabled = _results.Count > 1;
        }

        private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var keyword = e.ClickedItem as string;
            if (String.IsNullOrEmpty(keyword)) return;
            KeywordBox.Text = keyword;
            StartSearch(keyword);
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            RefreshHistoryList();
            try { await _historyStore.SaveAsync(_history); }
            catch (Exception) { }
        }

        private void KeywordBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

            string text = (sender.Text ?? "").Trim();
            var matches = new List<string>();
            for (int i = 0; i < _history.Count && matches.Count < 8; i++)
            {
                if (text.Length == 0 || _history[i].IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add(_history[i]);
            }
            sender.ItemsSource = matches;
        }

        private void KeywordBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            var text = args.SelectedItem as string;
            if (text != null) sender.Text = text;
        }

        private void KeywordBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string keyword = args.ChosenSuggestion as string ?? args.QueryText ?? sender.Text;
            StartSearch(keyword);
        }

        private void RefreshStatusIdle()
        {
            if (AppState.Sources.Count == 0)
            {
                SetStatus("还没有书源。请先到「书源」页导入在线书源或添加示例书源。");
                return;
            }

            if (SingleSourceMode)
            {
                var source = SourceBox.SelectedItem as BookSource;
                SetStatus(source == null ? "请选择书源后搜索。" : "当前书源：" + source.Name);
                return;
            }

            int usable = CountUsable();
            SetStatus(usable == 0
                ? "没有可用的书源（搜索规则不完整）。请到「书源」页检查。"
                : "将在 " + usable + " 个可用书源中并行查找。");
        }

        private static int CountUsable()
        {
            int usable = 0;
            for (int i = 0; i < AppState.Sources.Count; i++)
                if (AppState.Sources[i].Enabled && AppState.Sources[i].IsUsable) usable++;
            return usable;
        }

        // ---------------------------------------------------------------- 搜索

        private async void StartSearch(string raw)
        {
            string keyword = raw == null ? "" : raw.Trim();
            if (keyword.Length == 0) { SetStatus("请先输入关键字。"); return; }

            // 收起软键盘，把屏幕留给结果
            ResultsView.Focus(FocusState.Programmatic);

            BookSource single = null;
            if (SingleSourceMode)
            {
                single = SourceBox.SelectedItem as BookSource;
                if (single == null) { SetStatus("请先选择书源。"); return; }
            }

            var sources = new List<BookSource>();
            if (single != null) sources.Add(single);
            else for (int i = 0; i < AppState.Sources.Count; i++) sources.Add(AppState.Sources[i]);

            if (sources.Count == 0) { SetStatus("没有书源可用。"); return; }

            await RememberKeywordAsync(keyword);
            await RunSearchAsync(keyword, sources, single != null);
        }

        private async Task RunSearchAsync(string keyword, List<BookSource> sources, bool single)
        {
            if (_cancel != null) _cancel.Cancel();
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            int generation = ++_generation;

            _keyword = keyword;
            _arrival = 0;
            _hitCount = 0;
            _skipped = 0;
            _byKey.Clear();
            _all.Clear();
            _results.Clear();
            _running = true;
            UpdatePanels();

            StopButton.IsEnabled = true;
            SearchProgress.Visibility = Visibility.Visible;
            SearchProgress.IsIndeterminate = true;
            SearchProgress.Value = 0;

            int concurrency = single ? 1 : MultiSearch.ClampConcurrency(AppState.Settings.SearchThreads);
            SetStatus(single
                ? "正在「" + sources[0].Name + "」中搜索《" + keyword + "》…"
                : "正在并行搜索《" + keyword + "》…（" + concurrency + " 路并发）");

            int total = 0;
            string error = null;
            try
            {
                if (single)
                {
                    // 指定书源时走共享引擎，「调试」页才能看到这次请求的详情；停用的书源也允许单独测试
                    var books = await AppState.Engine.SearchAsync(sources[0], keyword, 1);
                    AppState.LastUsedEngine = AppState.Engine;
                    if (generation == _generation && !cancel.IsCancellationRequested)
                    {
                        for (int i = 0; i < books.Count; i++)
                        {
                            if (books[i].Source == null) books[i].Source = sources[0];
                            Accept(books[i]);
                        }
                    }
                    total = books.Count;
                    if (books.Count == 0 && !String.IsNullOrEmpty(AppState.Engine.LastError))
                        error = AppState.Engine.LastError;
                }
                else
                {
                    total = await MultiSearch.WhenDoneOrCancelled(MultiSearch.SearchAsync(
                        sources, keyword, 1, concurrency,
                        delegate (IList<Book> books) { OnBatch(books, generation); },
                        delegate (MultiSearchProgress p) { OnProgress(p, generation); },
                        cancel.Token, AppState.SearchOptions()), cancel.Token);
                    if (total < 0) total = _hitCount;
                    var ignoredSave = AppState.SaveHealthAsync();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppState.Log("搜索失败：" + ex.Message);
            }

            // 新的搜索已经开始，这次的收尾交给新的那次
            if (generation != _generation) return;

            // 让排在 Normal 优先级的最后一批结果先落到列表里，再写总结
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate { });

            _running = false;
            _cancel = null;
            StopButton.IsEnabled = false;
            SearchProgress.Visibility = Visibility.Collapsed;
            // 书源数会在搜索过程中变化，结束时按最终数据再排一次
            if (_sort == SortMode.SourceCount || _sort == SortMode.Relevance) Rebuild();
            UpdatePanels();

            AppState.Log("搜索《" + keyword + "》" + (single ? "[" + sources[0].Name + "]" : "[聚合]")
                         + " -> " + total + " 条命中，合并为 " + _all.Count + " 本");

            if (error != null) { SetStatus("搜索失败：" + error); return; }
            if (cancel.IsCancellationRequested) { SetStatus("已停止。保留已找到的 " + _results.Count + " 本。"); return; }
            SetStatus(Summary(true));
        }

        /// <summary>
        /// 收到一批新结果：合并到已有条目或按当前排序插入，不等其它书源。
        /// 回调来自线程池，必须切回 UI 线程再动集合。
        /// </summary>
        private void OnBatch(IList<Book> books, int generation)
        {
            if (books == null || books.Count == 0) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                if (generation != _generation) return;
                // Dispatcher 回调里的异常会被静默吞掉（之前出现过“36 条命中，合并为 0 本”），这里逐条兜住并记日志
                for (int i = 0; i < books.Count; i++)
                {
                    try { Accept(books[i]); }
                    catch (Exception ex) { AppState.Log("合并搜索结果失败：" + (books[i] == null ? "" : books[i].Name) + " -> " + ex.Message); }
                }
                UpdatePanels();
            });
        }

        private void Accept(Book book)
        {
            _hitCount++;
            string key = SearchRanking.BookKey(book.Name, book.Author);
            SearchResultItem existing;
            if (_byKey.TryGetValue(key, out existing))
            {
                existing.AddOrigin(book);
                return;
            }

            var item = new SearchResultItem(book, book.Source, _keyword, _arrival++);
            _byKey[key] = item;
            _all.Add(item);
            if (PassesFilter(item)) Insert(item);
        }

        private bool PassesFilter(SearchResultItem item)
        {
            if (PreciseCheck.IsChecked != true) return true;
            return SearchRanking.IsRelevant(item.Book.Name, item.Book.Author, _keyword);
        }

        private void Insert(SearchResultItem item)
        {
            int lo = 0, hi = _results.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Compare(_results[mid], item) <= 0) lo = mid + 1;
                else hi = mid;
            }
            _results.Insert(lo, item);
        }

        private int Compare(SearchResultItem a, SearchResultItem b)
        {
            int c;
            switch (_sort)
            {
                case SortMode.Relevance:
                    c = b.Score.CompareTo(a.Score);
                    if (c != 0) return c;
                    // 同样相关时，被越多书源收录的越可能是正版那本
                    c = b.OriginCount.CompareTo(a.OriginCount);
                    if (c != 0) return c;
                    break;
                case SortMode.Name:
                    c = String.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                    if (c != 0) return c;
                    break;
                case SortMode.SourceCount:
                    c = b.OriginCount.CompareTo(a.OriginCount);
                    if (c != 0) return c;
                    c = b.Score.CompareTo(a.Score);
                    if (c != 0) return c;
                    break;
            }
            return a.Arrival.CompareTo(b.Arrival);
        }

        /// <summary>按当前排序与过滤条件重建列表。</summary>
        private void Rebuild()
        {
            var list = new List<SearchResultItem>();
            for (int i = 0; i < _all.Count; i++)
                if (PassesFilter(_all[i])) list.Add(_all[i]);
            list.Sort(Compare);

            _results.Clear();
            for (int i = 0; i < list.Count; i++) _results.Add(list[i]);
            UpdatePanels();
            if (!_running && _all.Count > 0) SetStatus(Summary(true));
        }

        private string Summary(bool finished)
        {
            if (_all.Count == 0)
            {
                return SingleSourceMode
                    ? "没有结果。可到「调试」页查看请求详情。"
                    : "所有书源都没有结果。多数在线书源已失效，可换关键字或换书源再试。";
            }
            string text = (finished ? "共 " : "已找到 ") + _results.Count + " 本";
            if (_results.Count != _all.Count) text += "（精准搜索过滤掉 " + (_all.Count - _results.Count) + " 本）";
            if (_hitCount > _all.Count) text += "，" + _hitCount + " 条命中已按书名+作者合并";
            if (finished && _skipped > 0) text += "；跳过了 " + _skipped + " 个近期失效书源（可在设置里关闭）";
            return text + "。";
        }

        private void OnProgress(MultiSearchProgress p, int generation)
        {
            // 回调来自线程池，必须切回 UI 线程
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                if (generation != _generation || !_running) return;
                if (p.Total > 0)
                {
                    SearchProgress.IsIndeterminate = false;
                    SearchProgress.Value = (double)p.Done / p.Total;
                }
                _skipped = p.Skipped;
                SetStatus(Summary(false) + " 进度 " + p.Done + "/" + p.Total
                          + "，" + p.Succeeded + " 个书源有结果"
                          + (p.Skipped > 0 ? "，跳过 " + p.Skipped + " 个近期失效书源" : ""));
            });
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cancel != null) _cancel.Cancel();
            StopButton.IsEnabled = false;
            SetStatus("正在停止…");
        }

        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            if (item == null || item.Tag == null) return;
            int mode;
            if (!Int32.TryParse(item.Tag.ToString(), out mode)) return;
            _sort = (SortMode)mode;
            Rebuild();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_cancel != null) _cancel.Cancel();
            _generation++;
            _running = false;
            StopButton.IsEnabled = false;
            SearchProgress.Visibility = Visibility.Collapsed;
            KeywordBox.Text = "";
            _byKey.Clear();
            _all.Clear();
            _results.Clear();
            UpdatePanels();
            RefreshStatusIdle();
        }

        private async Task RememberKeywordAsync(string keyword)
        {
            _history.RemoveAll(delegate (string s) { return String.Equals(s, keyword, StringComparison.OrdinalIgnoreCase); });
            _history.Insert(0, keyword);
            while (_history.Count > 30) _history.RemoveAt(_history.Count - 1);
            RefreshHistoryList();

            try { await _historyStore.SaveAsync(_history); }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- 结果操作

        private void ResultsView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as SearchResultItem;
            if (item == null) return;
            BookDetailPage.Open(item.Book, item.Source, item.Origins);
        }

        private async void AddShelf_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var item = button == null ? null : button.DataContext as SearchResultItem;
            if (item == null) return;

            if (AppState.InShelf(item.Book.Url))
            {
                SetStatus("《" + item.Book.Name + "》已经在书架里了。");
                return;
            }

            AppState.AddToShelf(item.Book, item.Source);
            item.NotifyShelfChanged();
            SetStatus("已加入书架：《" + item.Book.Name + "》");
            AppState.Log("加入书架：" + item.Book.Name);
            try { await AppState.SaveShelfAsync(); }
            catch (Exception ex) { AppState.Log("保存书架失败：" + ex.Message); }
        }

        private void SetStatus(string text) { StatusText.Text = text; }
    }
}
