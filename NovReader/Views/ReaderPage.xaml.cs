using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;
using NovReader.Services.Legado;

namespace NovReader.Views
{
    /// <summary>
    /// 阅读器。
    /// 翻页模式按行高整数倍滚动，所以页面边界永远落在行与行之间，不会切断文字；
    /// 滚动模式则是普通连续滚动。两种模式共用同一个 ScrollViewer。
    /// </summary>
    public sealed partial class ReaderPage : Page
    {
        private Book _book;
        private BookSource _source;
        private ShelfBook _shelf;
        private ReaderRequest _request;
        private List<Chapter> _chapters = new List<Chapter>();
        private int _chapterIndex;
        private bool _loading;
        private bool _suppressSlider;
        // 初始为 true：InitializeComponent 期间 Slider 会从 0 被拉到 Minimum 并触发 ValueChanged，
        // 不拦住会把阅读设置改成最小值。InitSettingsControls 结束时放开。
        private bool _suppressSettingsEvents = true;
        private bool _barsVisible = true;
        private string _currentContent = "";
        private double _pageStep;
        private int _pageIndex, _pageCount;
        private Windows.System.Display.DisplayRequest _displayRequest;

        public ReaderPage()
        {
            InitializeComponent();
            KeyDown += ReaderPage_KeyDown;
            ReaderScroll.DirectManipulationStarted += delegate { _pendingImageBookmarkRatio = null; };
        }

        // ================================================================ 生命周期

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _leaving = false;

            AppState.Log("进入阅读器");

            // 优先用导航参数：它跟着返回栈走，从详情页（换源）返回阅读器时也能恢复
            var request = e.Parameter as ReaderRequest ?? ReaderLauncher.Pending;
            ReaderLauncher.Pending = null;
            if (request != null && e.NavigationMode == NavigationMode.Back && request.Shelf != null)
                request.Resume = true;
            if (request == null) { ShowError("没有可阅读的内容。"); return; }

            if (!Resolve(request))
            {
                ShowError("无法确定要阅读的书（缺少书籍或书源信息）。");
                return;
            }
            // 已经加入书架：以后从返回栈回到这里时按书架条目恢复（换源后书架条目会指向新书源）
            if (request.Shelf == null && _shelf != null) { request.Shelf = _shelf; request.Book = null; }
            _request = request;

            AppState.Log("阅读《" + _book.Name + "》，书源=" + (_source == null ? "(无)" : _source.Name));

            ApplyTheme();
            InitSettingsControls();
            ApplyPageTurnMode();

            BookTitleText.Text = _book.Name;
            TocList.ItemsSource = _chapters;

            await StartReadingAsync();
        }

        /// <summary>加载目录并打开起始章节（首次进入与“重试”共用）。</summary>
        private async Task StartReadingAsync()
        {
            await LoadChaptersAsync();
            if (_leaving || _chapters.Count == 0) return;

            var request = _request;
            bool resume = request != null && request.Resume && _shelf != null;
            int start = resume ? _shelf.ChapterIndex : (request == null ? 0 : request.StartChapterIndex);
            if (start < 0) start = 0;
            if (start >= _chapters.Count) start = Math.Max(0, _chapters.Count - 1);

            double restoreRatio = resume ? _shelf.ScrollRatio : 0;
            await OpenChapterAsync(start, restoreRatio);
            if (!_leaving) UpdateKeepAwake();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CloseImageZoom();
            // 章节还在加载时离开：不要用新章节号配旧的滚动位置覆盖进度
            if (!_loading) SaveProgress();
            _leaving = true;
            _openGeneration++;
            ReleaseKeepAwake();
            _imageGeneration++; // 停止还没下载完的图片
            StopAudio();
        }

        /// <summary>页面已离开：异步流程醒来后不再改界面、存进度或申请常亮。</summary>
        private bool _leaving;

        /// <summary>每次打开章节递增；旧的加载完成时发现代号变了就丢弃结果（连点下一章时不串章）。</summary>
        private int _openGeneration;

        /// <summary>本页独立的引擎：共享的 AppState.Engine 会被详情页同时使用，LastError 互相覆盖。</summary>
        private readonly LegadoEngine _engine = new LegadoEngine();

        private bool Resolve(ReaderRequest request)
        {
            if (request.Shelf != null)
            {
                _shelf = request.Shelf;
                _source = AppState.FindSource(_shelf.SourceName);
                _book = new Book
                {
                    Name = _shelf.Name,
                    Author = _shelf.Author,
                    CoverUrl = _shelf.CoverUrl,
                    Intro = _shelf.Intro,
                    Url = _shelf.BookUrl,
                    TocUrl = _shelf.TocUrl,
                    Source = _source,
                    BaseUrl = _source == null ? _shelf.BookUrl : _source.Url
                };
                return true;
            }

            if (request.Book != null)
            {
                _book = request.Book;
                _source = request.Source ?? request.Book.Source;
                _book.Source = _source;
                if (String.IsNullOrEmpty(_book.BaseUrl) && _source != null) _book.BaseUrl = _source.Url;
                // 没有书源就读不了，也不能往书架里加一条没有书源的条目
                if (_source == null) return false;

                // 阅读过程中要记录进度，因此确保书架里有这一条
                _shelf = AppState.AddToShelf(_book, _source);
                return true;
            }

            return false;
        }

        // ================================================================ 目录

        private async Task LoadChaptersAsync()
        {
            if (_chapters.Count > 0) return;

            if (_source == null)
            {
                ShowError("找不到这本书的书源「" + (_shelf == null ? "" : _shelf.SourceName) + "」（可能已被删除）。\n点「换源 / 详情」换一个书源。");
                return;
            }
            if (String.IsNullOrEmpty(_source.Toc.ChapterList))
            {
                ShowError("这个书源没有配置目录规则，无法读取章节。可以「换源」。");
                return;
            }

            // 1) 先看本地目录缓存：重新打开一本书时不再请求站点
            var cached = await AppState.TocCache.GetAsync(_book.Url);
            if (cached != null && cached.Count > 0)
            {
                ApplyChapters(cached, true);
                return;
            }

            // 2) 没有缓存才走网络
            SetLoading(true, "正在加载目录…");
            try
            {
                var list = await _engine.GetChaptersAsync(_book);
                AppState.LastUsedEngine = _engine;
                if (_leaving) return;
                if (list != null && list.Count > 0)
                {
                    await AppState.TocCache.PutAsync(_book.Url, list);
                    ApplyChapters(new List<Chapter>(list), false);
                }
                else
                {
                    string reason = _engine.LastError;
                    AppState.Log("阅读器目录为空：" + reason);
                    ShowError("目录为空" + (String.IsNullOrEmpty(reason) ? "" : "：" + reason)
                              + "\n可以在书籍详情里「换源」，或到「调试」页查看请求详情。");
                }
            }
            catch (Exception ex)
            {
                ShowError("目录加载失败：" + ex.Message);
                AppState.Log("目录加载失败：" + ex.Message);
            }
            finally { SetLoading(false, ""); }
        }

        /// <summary>把目录应用到界面（无论来自缓存还是网络）。</summary>
        private async void ApplyChapters(List<Chapter> list, bool fromCache)
        {
            _chapters = list;
            for (int i = 0; i < _chapters.Count; i++) _chapters[i].Index = i;

            TocList.ItemsSource = _chapters;
            TocCountText.Text = _chapters.Count + " 章";
            ChapterSlider.Maximum = Math.Max(0, _chapters.Count - 1);
            AppState.Log("打开《" + _book.Name + "》目录 " + _chapters.Count + " 章"
                         + (fromCache ? "（本地缓存）" : "（已写入本地缓存）"));

            if (_shelf != null && _chapters.Count > 0)
            {
                _shelf.ChapterCount = _chapters.Count;
                if (!String.IsNullOrEmpty(_book.TocUrl)) _shelf.TocUrl = _book.TocUrl;
                try { await AppState.SaveShelfAsync(); }
                catch (Exception) { }
            }
        }

        // ================================================================ 正文

        private async Task OpenChapterAsync(int index, double scrollRatio = 0)
        {
            if (index < 0 || index >= _chapters.Count || _leaving) return;

            int generation = ++_openGeneration;
            _imageGeneration++;
            CloseImageZoom();
            FindPanel.Visibility = Visibility.Collapsed;
            _findOffsets.Clear();
            _pendingImageBookmarkRatio = null;
            _chapterIndex = index;
            var chapter = _chapters[index];

            ChapterTitleText.Text = chapter.DisplayName;
            _suppressSlider = true;
            ChapterSlider.Value = index;
            _suppressSlider = false;
            ProgressText.Text = (index + 1) + " / " + _chapters.Count;

            HighlightToc(index);
            SetLoading(true, "正在加载《" + chapter.DisplayName + "》…");
            HideError();

            string text = null;
            string reason = "";
            try
            {
                if (!String.IsNullOrEmpty(chapter.Url))
                {
                    text = await AppState.ChapterCache.GetAsync(chapter.Url);
                    if (text == null)
                    {
                        if (_source != null && _source.Type == LegadoEngine.ImageSourceType)
                            Services.Legado.Js.JscBackend.TraceStage("comic content begin: " + _source.Name + " / " + chapter.DisplayName);
                        var engine = new LegadoEngine();
                        text = await engine.GetContentAsync(_book, chapter, NextChapterUrl(index));
                        if (_source != null && _source.Type == LegadoEngine.ImageSourceType)
                            Services.Legado.Js.JscBackend.TraceStage("comic content returned; chars=" + (text == null ? 0 : text.Length));
                        reason = engine.LastError;
                        AppState.LastUsedEngine = engine;
                        if (!String.IsNullOrEmpty(text)) await AppState.ChapterCache.PutAsync(chapter.Url, text);
                        else if (!String.IsNullOrEmpty(reason)) AppState.Log("正文抓取失败：" + reason);
                    }
                }
                else reason = "章节地址为空";
            }
            catch (Exception ex)
            {
                AppState.Log("正文加载失败：" + ex.Message);
                reason = ex.Message;
                text = null;
            }

            // 期间又打开了别的章节，或已经离开阅读页：这次的结果作废
            if (generation != _openGeneration || _leaving) return;
            SetLoading(false, "");

            if (String.IsNullOrEmpty(text))
            {
                AppState.Log("第 " + (index + 1) + " 章正文为空" + (String.IsNullOrEmpty(reason) ? "" : "：" + reason));
                ShowError("这一章没有取到正文" + (String.IsNullOrEmpty(reason) ? "。" : "：" + reason)
                          + "\n可以「重试」，或「换源」换一个书源接着读。");
                return;
            }

            AppState.Log("第 " + (index + 1) + " 章正文 " + text.Length + " 字");

            _currentContent = LegadoEngine.SanitizeContent(text);
            RenderContent(_currentContent);

            // 先同步完成布局，再按真实高度定位滚动位置并计算页数（以前靠等 60 毫秒，常常定位到顶部）
            ReaderScroll.UpdateLayout();
            RecalculatePages();
            if (scrollRatio > 0) ScrollToRatio(scrollRatio);
            else ScrollToTop();
            SaveProgress(scrollRatio);

            // 后台预取相邻章节，翻页时就能直接命中缓存，不用再等
            PrefetchAround(index);
        }

        /// <summary>
        /// 后台预取相邻章节并写入本地缓存。
        /// 只补缓存里没有的，且同一章不会重复预取，避免浪费流量。
        /// </summary>
        private void PrefetchAround(int index)
        {
            int[] targets = { index + 1, index - 1, index + 2 };
            for (int i = 0; i < targets.Length; i++)
            {
                int target = targets[i];
                if (target < 0 || target >= _chapters.Count) continue;
                PrefetchChapter(_chapters[target], NextChapterUrl(target));
            }
        }

        private string NextChapterUrl(int index)
        {
            return index + 1 >= 0 && index + 1 < _chapters.Count ? _chapters[index + 1].Url : null;
        }

        private readonly HashSet<string> _prefetching = new HashSet<string>(StringComparer.Ordinal);

        private async void PrefetchChapter(Chapter chapter, string nextChapterUrl)
        {
            if (chapter == null || String.IsNullOrEmpty(chapter.Url)) return;

            // 已有缓存就不动
            string cached;
            if (AppState.ChapterCache.TryGetMemory(chapter.Url, out cached)) return;

            lock (_prefetching)
            {
                if (_prefetching.Contains(chapter.Url)) return;
                _prefetching.Add(chapter.Url);
            }

            try
            {
                var disk = await AppState.ChapterCache.GetAsync(chapter.Url);
                if (!String.IsNullOrEmpty(disk)) return;

                var engine = new LegadoEngine();
                string content = await engine.GetContentAsync(_book, chapter, nextChapterUrl);
                if (!String.IsNullOrEmpty(content))
                    await AppState.ChapterCache.PutAsync(chapter.Url, content);
            }
            catch (Exception)
            {
                // 预取失败不影响当前阅读，正式翻页时会再试一次
            }
            finally
            {
                lock (_prefetching) { _prefetching.Remove(chapter.Url); }
            }
        }

        private bool _imageMode;
        private int _imageGeneration;

        /// <summary>把正文整理成段落，按当前设置排版。图片正文（漫画）和音频地址（有声书）走各自的显示方式。</summary>
        private void RenderContent(string raw)
        {
            _imageGeneration++;
            FindPanel.Visibility = Visibility.Collapsed;
            _findOffsets.Clear();
            string audio = AudioUrlOf(raw);
            _imageMode = audio == null && LegadoEngine.HasImages(raw);

            ContentBlock.Visibility = !_imageMode && audio == null ? Visibility.Visible : Visibility.Collapsed;
            ImageHost.Visibility = _imageMode ? Visibility.Visible : Visibility.Collapsed;
            AudioPanel.Visibility = audio != null ? Visibility.Visible : Visibility.Collapsed;
            if (audio == null) StopAudio();

            if (audio != null) { RenderAudio(audio); return; }
            if (_imageMode) { RenderImages(raw); return; }

            ImageHost.Children.Clear();
            _comicSlots.Clear();
            RenderText(raw);
        }

        private void RenderText(string raw)
        {
            var settings = AppState.Settings;
            var paragraphs = SplitParagraphs(raw);

            string separator = settings.BlankLineBetweenParagraphs
                ? Environment.NewLine + Environment.NewLine
                : Environment.NewLine;

            var sb = new StringBuilder();
            for (int i = 0; i < paragraphs.Count; i++)
            {
                if (i > 0) sb.Append(separator);
                if (settings.Indent) sb.Append("　　");
                sb.Append(paragraphs[i]);
            }

            ContentBlock.Text = sb.ToString();
            ApplyTypography();
        }

        // ================================================================ 漫画 / 有声

        /// <summary>有声书源的正文是一个音频地址。</summary>
        private string AudioUrlOf(string raw)
        {
            if (_source == null || _source.Type != LegadoEngine.AudioSourceType || String.IsNullOrEmpty(raw)) return null;
            foreach (var line in SplitParagraphs(raw))
            {
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return LegadoEngine.IsUsableLink(line) ? line : null;
            }
            return null;
        }

        private void RenderAudio(string url)
        {
            ImageHost.Children.Clear();
            _comicSlots.Clear();
            string title = _chapterIndex >= 0 && _chapterIndex < _chapters.Count ? _chapters[_chapterIndex].DisplayName : _book.Name;
            AudioTitleText.Text = title;
            AudioTitleText.Foreground = new SolidColorBrush(ParseColor(AppState.Settings.ForegroundColor));
            AudioStatusText.Foreground = AudioTitleText.Foreground;
            AudioStatusText.Text = "点播放键开始收听。离开阅读页后会停止播放。";
            try { AudioPlayer.Source = new Uri(url); }
            catch (Exception ex) { AudioStatusText.Text = "音频地址无效：" + ex.Message; }
        }

        private void StopAudio()
        {
            try
            {
                if (AudioPlayer.Source != null) { AudioPlayer.Stop(); AudioPlayer.Source = null; }
            }
            catch (Exception) { }
        }

        private void AudioPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            AudioStatusText.Text = "播放失败：" + e.ErrorMessage + "。可以换源再试。";
            AppState.Log("音频播放失败：" + e.ErrorMessage);
        }

        /// <summary>
        /// 图片正文：文字段落与图片按顺序竖排。图片自己下载（带书源请求头和 Referer），
        /// 直接把地址交给 Image 控件的话不带 Referer，很多漫画站会防盗链返回 403。
        /// </summary>
        private void RenderImages(string raw)
        {
            Services.Legado.Js.JscBackend.TraceStage("comic image layout begin");
            var settings = AppState.Settings;
            var foreground = new SolidColorBrush(ParseColor(settings.ForegroundColor));
            double pad = settings.Padding;
            ContentHost.Padding = new Thickness(_imagePadding, 0, _imagePadding, 60);
            ImageHost.Children.Clear();
            _comicSlots.Clear();
            int imageConcurrency = AppState.Settings.ComicParallelDownload ? AppState.Settings.ComicImageThreads : 1;
            _comicFetchGate = new System.Threading.SemaphoreSlim(Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, imageConcurrency)));

            var pending = new List<KeyValuePair<Image, string>>();
            foreach (var line in SplitParagraphs(raw))
            {
                string url = LegadoEngine.ImageUrlOf(line);
                if (url == null)
                {
                    ImageHost.Children.Add(new TextBlock
                    {
                        Text = line,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = settings.FontSize,
                        FontFamily = FontCatalog.ToFontFamily(settings.FontFamily),
                        Foreground = foreground,
                        Margin = new Thickness(pad, 8, pad, 8)
                    });
                    continue;
                }

                // 下载完成前用固定高度占位，避免整页高度频繁跳动
                var slot = CreateComicSlot(url, pending.Count);
                _comicSlots.Add(slot);
                ImageHost.Children.Add(slot.Frame);
                pending.Add(new KeyValuePair<Image, string>(slot.Image, url));
            }

            string referer = _chapterIndex >= 0 && _chapterIndex < _chapters.Count ? _chapters[_chapterIndex].Url : "";
            LoadImages(pending, referer, _imageGeneration);
        }

        private const double _imagePadding = 0;
        private System.Threading.SemaphoreSlim _comicFetchGate;

        private async void LoadImages(List<KeyValuePair<Image, string>> items, string referer, int generation)
        {
            int concurrency = AppState.Settings.ComicParallelDownload ? AppState.Settings.ComicImageThreads : 1;
            concurrency = Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, concurrency));
            Services.Legado.Js.JscBackend.TraceStage("comic image downloads begin; count=" + items.Count + "; concurrency=" + concurrency);
            string headers = _source == null ? "" : (_source.Header ?? "");
            if (!String.IsNullOrEmpty(referer) && headers.IndexOf("Referer", StringComparison.OrdinalIgnoreCase) < 0)
                headers = (headers.Length > 0 ? headers + "\n" : "") + "Referer: " + referer;

            int decodeWidth = (int)Math.Max(480, ReaderScroll.ActualWidth * Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel);
            int failed = 0;
            string sourceKey = _source == null ? "" : _source.Url;
            var fetchGate = _comicFetchGate;
            await ComicImageDownloads.RunAsync(items.Count, concurrency,
                async delegate(int index)
                {
                    SetComicSlotLoading(items[index].Key);
                    await fetchGate.WaitAsync();
                    try
                    {
                        if (generation != _imageGeneration || _leaving) return null;
                        return await AppState.ComicImageCache.GetOrFetchAsync(items[index].Value, sourceKey,
                            delegate { return HttpFetcher.GetBytesAsync(items[index].Value, headers, 30); });
                    }
                    finally { fetchGate.Release(); }
                },
                async delegate(int i, byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0) { SetComicSlotFailed(items[i].Key, "下载失败"); failed++; return; }
                if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                    bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
                {
                    Services.Legado.Js.JscBackend.TraceStage("image format unsupported: WebP; index=" + i + "; url=" + items[i].Value);
                    SetComicSlotFailed(items[i].Key, "不支持 WebP 格式");
                    failed++; return;
                }
                try
                {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = decodeWidth };
                    Services.Legado.Js.JscBackend.TraceStage("image decode begin: " + i + "; bytes=" + bytes.Length + "; width=" + decodeWidth);
                    using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
                        stream.Seek(0);
                        await bitmap.SetSourceAsync(stream);
                    }
                    if (generation != _imageGeneration) return;
                    items[i].Key.Source = bitmap;
                    items[i].Key.MinHeight = 0;
                    SetComicSlotReady(items[i].Key);
                    Services.Legado.Js.JscBackend.TraceStage("image decode complete: " + i);
                }
                catch (Exception error)
                {
                    Services.Legado.Js.JscBackend.TraceStage("image decode failed: " + i + "; " + HttpFetcher.Describe(error));
                    await AppState.ComicImageCache.RemoveAsync(items[i].Value, sourceKey);
                    SetComicSlotFailed(items[i].Key, "图片解码失败");
                    failed++;
                }
            }, delegate { return generation == _imageGeneration && !_leaving; });
            if (generation != _imageGeneration || _leaving) return;
            Services.Legado.Js.JscBackend.TraceStage("comic image downloads complete; count=" + items.Count + "; failed=" + failed);
            RestoreImageBookmarkIfReady();
            int remainingFailed = _comicSlots.FindAll(slot => slot.Failed).Count;
            if (remainingFailed > 0 && generation == _imageGeneration)
            {
                AppState.Log("第 " + (_chapterIndex + 1) + " 章有 " + remainingFailed + " 张图片加载失败");
                FlashHint(remainingFailed + " 张图片加载失败，可点重试");
            }
            RecalculatePages();
        }

        private static List<string> SplitParagraphs(string raw)
        {
            var result = new List<string>();
            if (String.IsNullOrEmpty(raw)) return result;

            string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var piece in normalized.Split('\n'))
            {
                string line = piece.Trim();
                if (line.Length == 0) continue;
                result.Add(line);
            }
            return result;
        }

        private void ApplyTypography()
        {
            var settings = AppState.Settings;

            ContentBlock.FontSize = settings.FontSize;
            ContentBlock.LineHeight = settings.LinePixelHeight;
            ContentBlock.Foreground = new SolidColorBrush(ParseColor(settings.ForegroundColor));

            ContentBlock.FontFamily = FontCatalog.ToFontFamily(settings.FontFamily);

            double pad = settings.Padding;
            // 顶部不留边：翻页步长是行高的整数倍、从 0 算起，顶部多 10px 会让每页都切在行中间
            ContentHost.Padding = new Thickness(pad, 0, pad, 60);

            ContentBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        private void ApplyTheme()
        {
            var settings = AppState.Settings;
            var background = new SolidColorBrush(ParseColor(settings.BackgroundColor));
            var bar = new SolidColorBrush(ParseColor(settings.BarBackgroundColor));

            RootGrid.Background = background;
            TopBar.Background = bar;
            BottomBar.Background = bar;
            SettingsPanel.Background = bar;
            ApplyTypography();
            UpdateThemeSelection();
        }

        private void UpdateThemeSelection()
        {
            var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
            var normal = (SolidColorBrush)Application.Current.Resources["DividerBrush"];

            ThemeDay.BorderBrush = AppState.Settings.Theme == ReaderTheme.Day ? accent : normal;
            ThemeSepia.BorderBrush = AppState.Settings.Theme == ReaderTheme.Sepia ? accent : normal;
            ThemeGreen.BorderBrush = AppState.Settings.Theme == ReaderTheme.Green ? accent : normal;
            ThemeNight.BorderBrush = AppState.Settings.Theme == ReaderTheme.Night ? accent : normal;
            ThemeBlack.BorderBrush = AppState.Settings.Theme == ReaderTheme.Black ? accent : normal;

            // 目录抽屉与设置面板的浅色背景在夜间主题下要压暗，否则刺眼
            bool dark = AppState.Settings.Theme == ReaderTheme.Night || AppState.Settings.Theme == ReaderTheme.Black;
            var panelBg = dark
                ? new SolidColorBrush(ParseColor("#FF1E1E1E"))
                : (SolidColorBrush)Application.Current.Resources["CardBrush"];
            if (TocList != null) TocList.Background = panelBg;

            var textBrush = new SolidColorBrush(ParseColor(AppState.Settings.ForegroundColor));
            BookTitleText.Foreground = textBrush;
            ChapterTitleText.Foreground = dark ? new SolidColorBrush(ParseColor("#FF8A8A8A")) : textBrush;
        }

        private static Color ParseColor(string hex)
        {
            hex = (hex ?? "").TrimStart('#');
            if (hex.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
            }
            if (hex.Length == 6)
            {
                return Color.FromArgb(255,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            return Colors.White;
        }

        // ================================================================ 翻页

        private void ApplyPageTurnMode()
        {
            bool pageMode = AppState.Settings.PageTurn == PageTurnMode.Page;
            ReaderScroll.VerticalScrollBarVisibility = pageMode
                ? ScrollBarVisibility.Hidden
                : ScrollBarVisibility.Auto;
            PageIndicator.Visibility = pageMode ? Visibility.Visible : Visibility.Collapsed;
            if (!pageMode) UpdatePageIndicator();
        }

        private void RecalculatePages()
        {
            if (AppState.Settings.PageTurn != PageTurnMode.Page || _imageMode) { _pageStep = 0; return; }

            double linePx = AppState.Settings.LinePixelHeight;
            double viewport = ReaderScroll.ViewportHeight;
            if (linePx <= 0 || viewport <= 0) { _pageStep = 0; return; }

            int lines = (int)Math.Floor(viewport / linePx);
            if (lines < 1) lines = 1;
            _pageStep = lines * linePx;

            double scrollable = ReaderScroll.ScrollableHeight;
            _pageCount = _pageStep <= 0 ? 1 : (int)Math.Ceiling(scrollable / _pageStep) + 1;
            _pageIndex = _pageStep <= 0 ? 0 : (int)Math.Floor(ReaderScroll.VerticalOffset / _pageStep);
            UpdatePageIndicator();
        }

        private void UpdatePageIndicator()
        {
            if (AppState.Settings.PageTurn != PageTurnMode.Page) return;
            int total = Math.Max(1, _pageCount);
            int current = Math.Min(total, _pageIndex + 1);
            PageIndicatorText.Text = current + " / " + total;
        }

        /// <summary>按屏翻（图片模式与滚动模式下的点按/按键翻页），到底了进入下一章。</summary>
        private void ScrollByScreen(int direction)
        {
            _pendingImageBookmarkRatio = null;
            double step = Math.Max(100, ReaderScroll.ViewportHeight * 0.9);
            double target = ReaderScroll.VerticalOffset + direction * step;
            if (direction > 0 && ReaderScroll.VerticalOffset >= ReaderScroll.ScrollableHeight - 1) { NextChapter(); return; }
            if (direction < 0 && ReaderScroll.VerticalOffset <= 1) { PrevChapter(1.0); return; }
            ReaderScroll.ChangeView(null, Math.Max(0, Math.Min(ReaderScroll.ScrollableHeight, target)), null, false);
        }

        private void NextPage()
        {
            if (_imageMode) { ScrollByScreen(1); return; }
            if (_pageStep <= 0) { RecalculatePages(); if (_pageStep <= 0) return; }

            double target = (Math.Floor(ReaderScroll.VerticalOffset / _pageStep) + 1) * _pageStep;
            double max = ReaderScroll.ScrollableHeight;
            if (target > max) target = max;
            if (target <= ReaderScroll.VerticalOffset + 1) { NextChapter(); return; }
            ReaderScroll.ChangeView(null, target, null, true);
        }

        private void PrevPage()
        {
            if (_imageMode) { ScrollByScreen(-1); return; }
            if (_pageStep <= 0) { RecalculatePages(); if (_pageStep <= 0) return; }

            double target = (Math.Ceiling(ReaderScroll.VerticalOffset / _pageStep) - 1) * _pageStep;
            if (target < 0) target = 0;
            // 在本章第一页往前翻：进入上一章的最后一页，而不是上一章开头
            if (target >= ReaderScroll.VerticalOffset - 1) { PrevChapter(1.0); return; }
            ReaderScroll.ChangeView(null, target, null, true);
        }

        private void ReaderScroll_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_imageMode)
            {
                // 漫画：点左侧上一屏、右侧下一屏、中间显示/隐藏工具栏
                var ip = e.GetPosition(ReaderScroll);
                double w = ReaderScroll.ActualWidth;
                if (w <= 0) return;
                if (ip.X < w * 0.3) ScrollByScreen(-1);
                else if (ip.X > w * 0.7) ScrollByScreen(1);
                else ToggleBars();
                return;
            }

            if (AppState.Settings.PageTurn != PageTurnMode.Page)
            {
                // 滚动模式下点中间也能收起/展开状态栏
                var pos = e.GetPosition(ReaderScroll);
                if (pos.X > ReaderScroll.ActualWidth * 0.3 && pos.X < ReaderScroll.ActualWidth * 0.7)
                    ToggleBars();
                return;
            }

            var p = e.GetPosition(ReaderScroll);
            double width = ReaderScroll.ActualWidth;
            if (width <= 0) return;

            if (p.X < width * 0.25) PrevPage();
            else if (p.X > width * 0.75) NextPage();
            else ToggleBars();
        }

        private void ReaderScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (AppState.Settings.PageTurn == PageTurnMode.Page && _pageStep > 0)
            {
                _pageIndex = (int)Math.Floor((ReaderScroll.VerticalOffset + 1) / _pageStep);
                UpdatePageIndicator();
            }
            SaveProgressDebounced();
        }

        private void ReaderScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RecalculatePages();
        }

        private void TogglePageMode_Click(object sender, RoutedEventArgs e)
        {
            AppState.Settings.PageTurn = AppState.Settings.PageTurn == PageTurnMode.Page
                ? PageTurnMode.Scroll
                : PageTurnMode.Page;
            SyncPageTurnControls();
            ApplyPageTurnMode();
            RecalculatePages();
            SaveSettings();
        }

        // ================================================================ 章节跳转

        private void PrevChapter_Click(object sender, RoutedEventArgs e) { PrevChapter(); }
        private void NextChapter_Click(object sender, RoutedEventArgs e) { NextChapter(); }

        private void PrevChapter()
        {
            PrevChapter(0);
        }

        private void PrevChapter(double ratio)
        {
            if (_chapterIndex <= 0) { FlashHint("已经是第一章了"); return; }
            OpenChapterAsync(_chapterIndex - 1, ratio).Forget();
        }

        private void NextChapter()
        {
            if (_chapterIndex >= _chapters.Count - 1) { FlashHint("已经是最后一章了"); return; }
            OpenChapterAsync(_chapterIndex + 1).Forget();
        }

        private int _sliderTicket;

        /// <summary>
        /// 拖动滑块时只更新进度文字，停手 400 毫秒后才打开章节；
        /// 以前拖动中的第一个值就开始加载，松手的位置被忽略，滑块还会跳回去。
        /// </summary>
        private async void ChapterSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSlider) return;
            int index = (int)Math.Round(e.NewValue);
            if (index < 0 || index >= _chapters.Count) return;
            ProgressText.Text = (index + 1) + " / " + _chapters.Count + " · " + _chapters[index].DisplayName;

            int ticket = ++_sliderTicket;
            await Task.Delay(400);
            if (ticket != _sliderTicket || _leaving) return;
            if (index != _chapterIndex) OpenChapterAsync(index).Forget();
        }

        private void HighlightToc(int index)
        {
            if (index < 0 || index >= _chapters.Count) return;
            TocList.SelectedIndex = index;
            try
            {
                var container = TocList.ContainerFromIndex(index) as ListViewItem;
                if (container != null) container.StartBringIntoView();
            }
            catch (Exception) { }
        }

        // ================================================================ 目录抽屉

        private void ToggleToc_Click(object sender, RoutedEventArgs e)
        {
            bool show = TocLayer.Visibility != Visibility.Visible;
            if (show) { BookmarkLayer.Visibility = SettingsLayer.Visibility = FindPanel.Visibility = Visibility.Collapsed; }
            TocLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) HighlightToc(_chapterIndex);
        }

        private void TocBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
        {
            TocLayer.Visibility = Visibility.Collapsed;
        }

        private void TocList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chapter = e.ClickedItem as Chapter;
            if (chapter == null) return;
            TocLayer.Visibility = Visibility.Collapsed;
            OpenChapterAsync(chapter.Index).Forget();
        }

        // ================================================================ 设置面板

        private void ToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            bool show = SettingsLayer.Visibility != Visibility.Visible;
            if (show) { BookmarkLayer.Visibility = TocLayer.Visibility = FindPanel.Visibility = Visibility.Collapsed; }
            SettingsLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SettingsBackdrop_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsLayer.Visibility = Visibility.Collapsed;
        }

        private void InitSettingsControls()
        {
            _suppressSettingsEvents = true;
            var s = AppState.Settings;

            FontSizeSlider.Value = s.FontSize;
            LineHeightSlider.Value = s.LineHeight;
            PaddingSlider.Value = s.Padding;

            FontSizeText.Text = ((int)s.FontSize) + " px";
            LineHeightText.Text = s.LineHeight.ToString("0.0") + " 倍";
            PaddingText.Text = ((int)s.Padding) + " px";

            FontCombo.ItemsSource = FontCatalog.Available();
            FontCombo.SelectedItem = FontCatalog.Find(s.FontFamily);

            IndentCheck.IsChecked = s.Indent;
            BlankLineCheck.IsChecked = s.BlankLineBetweenParagraphs;
            KeepAwakeCheck.IsChecked = s.KeepAwake;

            SyncPageTurnControls();
            _suppressSettingsEvents = false;
        }

        private void SyncPageTurnControls()
        {
            _suppressSettingsEvents = true;
            PageTurnPage.IsChecked = AppState.Settings.PageTurn == PageTurnMode.Page;
            PageTurnScroll.IsChecked = AppState.Settings.PageTurn == PageTurnMode.Scroll;
            _suppressSettingsEvents = false;
        }

        private void FontSizeSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.FontSize = Math.Round(e.NewValue);
            FontSizeText.Text = ((int)AppState.Settings.FontSize) + " px";
            OnTypographyChanged();
        }

        private void LineHeightSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.LineHeight = Math.Round(e.NewValue, 1);
            LineHeightText.Text = AppState.Settings.LineHeight.ToString("0.0") + " 倍";
            OnTypographyChanged();
        }

        private void PaddingSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.Padding = Math.Round(e.NewValue);
            PaddingText.Text = ((int)AppState.Settings.Padding) + " px";
            OnTypographyChanged();
        }

        private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            var item = FontCombo.SelectedItem as FontChoice;
            if (item == null) return;
            AppState.Settings.FontFamily = item.Family;
            OnTypographyChanged();
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;
            ReaderTheme theme;
            if (!Enum.TryParse<ReaderTheme>(button.Tag.ToString(), out theme)) return;
            AppState.Settings.Theme = theme;
            ApplyTheme();
            SaveSettings();
        }

        private void PageTurn_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.PageTurn = PageTurnScroll.IsChecked == true ? PageTurnMode.Scroll : PageTurnMode.Page;
            ApplyPageTurnMode();
            RecalculatePages();
            SaveSettings();
        }

        private void Layout_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.Indent = IndentCheck.IsChecked == true;
            AppState.Settings.BlankLineBetweenParagraphs = BlankLineCheck.IsChecked == true;
            if (!String.IsNullOrEmpty(_currentContent))
            {
                double offset = ReaderScroll.VerticalOffset;
                RenderContent(_currentContent);
                RecalculatePages();
                ReaderScroll.ChangeView(null, offset, null, true);
            }
            SaveSettings();
        }

        private void KeepAwake_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSettingsEvents) return;
            AppState.Settings.KeepAwake = KeepAwakeCheck.IsChecked == true;
            UpdateKeepAwake();
            SaveSettings();
        }

        private void OnTypographyChanged()
        {
            // 记住阅读位置：字号变了以后要尽量停在原来那段文字上
            double ratio = ScrollRatio();
            if (_imageMode) RenderContent(_currentContent); // 图片模式里的文字段落是单独的 TextBlock
            else ApplyTypography();
            // 先同步布局，页数和滚动位置才按新字号的高度计算
            ReaderScroll.UpdateLayout();
            RecalculatePages();
            ScrollToRatio(ratio);
            SaveSettingsDebounced();
        }

        // ================================================================ 滚动位置

        private double ScrollRatio()
        {
            double scrollable = ReaderScroll.ScrollableHeight;
            if (scrollable <= 0) return 0;
            return Math.Min(1.0, Math.Max(0.0, ReaderScroll.VerticalOffset / scrollable));
        }

        private void ScrollToTop() { ReaderScroll.ChangeView(null, 0, null, true); }

        private void ScrollToRatio(double ratio)
        {
            if (ratio <= 0) { ScrollToTop(); return; }
            double target = ReaderScroll.ScrollableHeight * Math.Min(1.0, ratio);
            ReaderScroll.ChangeView(null, target, null, true);
        }

        // ================================================================ 进度保存

        private void SaveProgress()
        {
            SaveProgress(-1);
        }

        /// <summary>ratio &lt; 0 时取当前滚动位置；刚 ChangeView 完偏移量还没更新，调用方直接给出目标比例。</summary>
        private void SaveProgress(double ratio)
        {
            if (_leaving || _shelf == null || _chapters.Count == 0) return;
            string name = _chapterIndex >= 0 && _chapterIndex < _chapters.Count
                ? _chapters[_chapterIndex].DisplayName
                : "";
            AppState.UpdateProgress(_shelf, _chapterIndex, name, ratio >= 0 ? ratio : ScrollRatio(), _chapters.Count);
            SaveShelfDebounced();
        }

        private bool _shelfSavePending;
        private bool _settingsSavePending;

        private async void SaveShelfDebounced()
        {
            if (_shelfSavePending) return;
            _shelfSavePending = true;
            await Task.Delay(1200);
            _shelfSavePending = false;
            try { await AppState.SaveShelfAsync(); }
            catch (Exception ex) { AppState.Log("保存进度失败：" + ex.Message); }
        }

        private async void SaveSettingsDebounced()
        {
            if (_settingsSavePending) return;
            _settingsSavePending = true;
            await Task.Delay(800);
            _settingsSavePending = false;
            await SaveSettingsCoreAsync();
        }

        private async void SaveSettings()
        {
            await SaveSettingsCoreAsync();
        }

        private async Task SaveSettingsCoreAsync()
        {
            try { await AppState.SaveSettingsAsync(); }
            catch (Exception ex) { AppState.Log("保存阅读设置失败：" + ex.Message); }
        }

        private bool _progressSavePending;

        private async void SaveProgressDebounced()
        {
            if (_progressSavePending || _shelf == null) return;
            _progressSavePending = true;
            await Task.Delay(1000);
            _progressSavePending = false;
            SaveProgress();
        }

        // ================================================================ 状态栏与键盘

        private void ToggleBars_Click(object sender, RoutedEventArgs e) { ToggleBars(); }

        private void ToggleBars()
        {
            _barsVisible = !_barsVisible;
            TopBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
            BottomBar.Visibility = _barsVisible ? Visibility.Visible : Visibility.Collapsed;
            AppState.Settings.ShowBars = _barsVisible;
            RecalculatePages();
            SaveSettingsDebounced();
        }

        private void ReaderPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape && (e.OriginalSource is TextBox || FocusManager.GetFocusedElement() is TextBox)) return;
            if (ImageZoomLayer.Visibility == Visibility.Visible && e.Key != VirtualKey.Escape) return;
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    if (!HandleBack()) { SaveProgress(); Nav.Back(); }
                    e.Handled = true;
                    break;
                case VirtualKey.Left:
                case VirtualKey.PageUp:
                    PrevPage(); e.Handled = true; break;
                case VirtualKey.Right:
                case VirtualKey.PageDown:
                case VirtualKey.Space:
                    NextPage(); e.Handled = true; break;
                case VirtualKey.Up:
                    _pendingImageBookmarkRatio = null;
                    ReaderScroll.ChangeView(null, Math.Max(0, ReaderScroll.VerticalOffset - 60), null, false);
                    e.Handled = true; break;
                case VirtualKey.Down:
                    _pendingImageBookmarkRatio = null;
                    ReaderScroll.ChangeView(null, ReaderScroll.VerticalOffset + 60, null, false);
                    e.Handled = true; break;
            }
        }

        // ================================================================ 提示与状态

        private void SetLoading(bool active, string message)
        {
            _loading = active;
            LoadingPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!String.IsNullOrEmpty(message)) LoadingText.Text = message;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            HideError();
            // 目录还没取到时要在取到后接着打开章节，否则重试成功也是一片空白
            if (_chapters.Count == 0) StartReadingAsync().Forget();
            else OpenChapterAsync(_chapterIndex).Forget();
        }

        /// <summary>正文/目录取不到时去详情页换源；换源后返回，阅读器按书架条目重新加载。</summary>
        private void OpenDetail_Click(object sender, RoutedEventArgs e)
        {
            SaveProgress();
            var shelf = _shelf ?? (_request == null ? null : _request.Shelf);
            if (shelf != null) BookDetailPage.OpenShelf(shelf);
            else if (_book != null) BookDetailPage.Open(_book, _source);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (HandleBack()) return;
            SaveProgress();
            Nav.Back();
        }

        private async void FlashHint(string message)
        {
            PageIndicatorText.Text = message;
            PageIndicator.Visibility = Visibility.Visible;
            await Task.Delay(1400);
            if (AppState.Settings.PageTurn == PageTurnMode.Page) UpdatePageIndicator();
            else PageIndicator.Visibility = Visibility.Collapsed;
        }

        // ================================================================ 屏幕常亮

        private void UpdateKeepAwake()
        {
            if (_leaving) return;
            if (AppState.Settings.KeepAwake) AcquireKeepAwake();
            else ReleaseKeepAwake();
        }

        private void AcquireKeepAwake()
        {
            if (_displayRequest != null) return;
            try
            {
                _displayRequest = new Windows.System.Display.DisplayRequest();
                _displayRequest.RequestActive();
            }
            catch (Exception ex)
            {
                _displayRequest = null;
                AppState.Log("屏幕常亮不可用：" + ex.Message);
            }
        }

        private void ReleaseKeepAwake()
        {
            if (_displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception) { }
            _displayRequest = null;
        }
    }

    /// <summary>让“即发即弃”的异步调用不触发编译器告警。</summary>
    internal static class TaskExtensions
    {
        public static void Forget(this Task task)
        {
            if (task == null) return;
            var ignored = task.ContinueWith(delegate (Task t)
            {
                var unused = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
