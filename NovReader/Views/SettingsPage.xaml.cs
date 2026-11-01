using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;

namespace NovReader.Views
{
    /// <summary>设置页：阅读偏好、缓存与数据管理、关于。</summary>
    public sealed partial class SettingsPage : Page
    {
        // 初始为 true：Slider 的 Minimum 大于 0，InitializeComponent 期间值会从 0 被拉到最小值并触发 ValueChanged，
        // 不拦住的话一打开设置页就会把字号、行距、页边距全部改成最小值并保存。LoadControls 结束时才放开。
        private bool _suppress = true;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadControls();
            RefreshStats();
        }

        private void LoadControls()
        {
            _suppress = true;
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
            PageTurnPage.IsChecked = s.PageTurn == PageTurnMode.Page;
            PageTurnScroll.IsChecked = s.PageTurn == PageTurnMode.Scroll;

            // 外观
            AppThemeSystem.IsChecked = s.AppTheme == AppThemeMode.System;
            AppThemeLight.IsChecked = s.AppTheme == AppThemeMode.Light;
            AppThemeDark.IsChecked = s.AppTheme == AppThemeMode.Dark;

            // 下载与导出
            ThreadsSlider.Value = s.DownloadThreads;
            ThreadsText.Text = s.DownloadThreads + " 线程";
            SearchThreadsSlider.Value = s.SearchThreads;
            SearchThreadsText.Text = s.SearchThreads + " 路";
            ComicParallelSwitch.IsOn = s.ComicParallelDownload;
            ComicThreadsSlider.Value = s.ComicImageThreads;
            ComicThreadsSlider.IsEnabled = s.ComicParallelDownload;
            ComicThreadsText.Text = s.ComicParallelDownload ? s.ComicImageThreads + " 路" : "1 路";
            SkipCachedCheck.IsChecked = s.SkipCached;
            SkipFailingCheck.IsChecked = s.SkipFailingSources;
            RefreshHealthText();

            ExportFormatCombo.ItemsSource = FormatChoices;
            ExportFormatCombo.SelectedIndex = (int)s.ExportFormat;
            ExportEncodingCombo.ItemsSource = EncodingChoices;
            ExportEncodingCombo.SelectedIndex = (int)s.ExportEncoding;
            RefreshExportHint();

            _suppress = false;
            RefreshThemeSelection();
        }

        private static readonly string[] FormatChoices =
        {
            TransferSettings.FormatName(ExportFormat.Txt),
            TransferSettings.FormatName(ExportFormat.Html),
            TransferSettings.FormatName(ExportFormat.Markdown),
            TransferSettings.FormatName(ExportFormat.Epub)
        };

        private static readonly string[] EncodingChoices =
        {
            TransferSettings.EncodingName(ExportEncoding.Utf8),
            TransferSettings.EncodingName(ExportEncoding.Utf8Bom),
            TransferSettings.EncodingName(ExportEncoding.Utf16Le),
            TransferSettings.EncodingName(ExportEncoding.Gbk),
            TransferSettings.EncodingName(ExportEncoding.Big5)
        };

        private void RefreshExportHint()
        {
            var format = AppState.Transfer.Format;
            ExportHintText.Text = format == ExportFormat.Epub
                ? "EPUB 内部固定使用 UTF-8，编码选项对它不起作用（电子书阅读器按规范读取）。"
                : "导出文件保存在应用数据目录的 exports 文件夹，可用「打开导出目录」查看，或在书籍详情页选「另存为」。";
        }

        // ================================================================ 外观

        private void AppTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;

            var mode = AppThemeDark.IsChecked == true
                ? AppThemeMode.Dark
                : AppThemeLight.IsChecked == true
                    ? AppThemeMode.Light
                    : AppThemeMode.System;

            AppState.Settings.AppTheme = mode;
            ThemeManager.Apply(mode);

            // 深色模式下同步把阅读纸张也切到夜间，避免白底刺眼；浅色则回到日间
            if (mode == AppThemeMode.Dark && AppState.Settings.Theme == ReaderTheme.Day)
                AppState.Settings.Theme = ReaderTheme.Night;
            else if (mode == AppThemeMode.Light && AppState.Settings.Theme == ReaderTheme.Night)
                AppState.Settings.Theme = ReaderTheme.Day;

            _suppress = true;
            RefreshThemeSelection();
            _suppress = false;

            SettingsStatusText.Text = "应用主题：" + ThemeManager.ThemeModeName(mode) + "。";
            Save();
        }

        // ================================================================ 下载与导出

        private void ComicParallelSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.ComicParallelDownload = ComicParallelSwitch.IsOn;
            ComicThreadsSlider.IsEnabled = ComicParallelSwitch.IsOn;
            ComicThreadsText.Text = ComicParallelSwitch.IsOn ? AppState.Settings.ComicImageThreads + " 路" : "1 路";
            Save();
        }

        private void ComicThreadsSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            int threads = Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, (int)e.NewValue));
            AppState.Settings.ComicImageThreads = threads;
            ComicThreadsText.Text = AppState.Settings.ComicParallelDownload ? threads + " 路" : "1 路";
            Save();
        }

        private void ThreadsSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            int threads = DownloadService.ClampThreads((int)Math.Round(e.NewValue));
            AppState.Settings.DownloadThreads = threads;
            AppState.Transfer.Threads = threads;
            ThreadsText.Text = threads + " 线程";
            Save();
        }

        private void SearchThreadsSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            int threads = MultiSearch.ClampConcurrency((int)Math.Round(e.NewValue));
            AppState.Settings.SearchThreads = threads;
            AppState.Transfer.SearchThreads = threads;
            SearchThreadsText.Text = threads + " 路";
            Save();
        }

        private void SkipFailing_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.SkipFailingSources = SkipFailingCheck.IsChecked == true;
            RefreshHealthText();
            Save();
        }

        private async void ResetHealth_Click(object sender, RoutedEventArgs e)
        {
            AppState.Health.Reset();
            await AppState.SaveHealthAsync();
            RefreshHealthText();
            SettingsStatusText.Text = "已清空书源健康记录，下次搜索会重新尝试全部书源。";
        }

        private void RefreshHealthText()
        {
            int skipped = AppState.Health.CountSkipped(AppState.Sources);
            HealthText.Text = skipped == 0
                ? "目前没有被跳过的书源。"
                : "目前有 " + skipped + " 个书源因连续 " + SourceHealth.SkipAfterFailures + " 次连接失败被暂时跳过，"
                  + (int)SourceHealth.RetryAfter.TotalDays + " 天后自动重试。";
        }

        private void SkipCached_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.SkipCached = SkipCachedCheck.IsChecked == true;
            AppState.Transfer.SkipCached = AppState.Settings.SkipCached;
            Save();
        }

        private void ExportFormat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            int index = ExportFormatCombo.SelectedIndex;
            if (index < 0) return;
            AppState.Settings.ExportFormat = (ExportFormat)index;
            AppState.Transfer.Format = (ExportFormat)index;
            RefreshExportHint();
            Save();
        }

        private void ExportEncoding_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            int index = ExportEncodingCombo.SelectedIndex;
            if (index < 0) return;
            AppState.Settings.ExportEncoding = (ExportEncoding)index;
            AppState.Transfer.Encoding = (ExportEncoding)index;
            Save();
        }

        private async void OpenExportFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "exports", Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.System.Launcher.LaunchFolderAsync(folder);
                SettingsStatusText.Text = "已打开导出目录：" + folder.Path;
            }
            catch (Exception ex)
            {
                string path = await ExportService.GetExportFolderPathAsync();
                SettingsStatusText.Text = "无法打开资源管理器（" + ex.Message + "）。目录：" + path;
            }
        }

        private void RefreshThemeSelection()
        {
            var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
            var normal = (SolidColorBrush)Application.Current.Resources["DividerBrush"];
            var theme = AppState.Settings.Theme;

            ThemeDay.BorderBrush = theme == ReaderTheme.Day ? accent : normal;
            ThemeSepia.BorderBrush = theme == ReaderTheme.Sepia ? accent : normal;
            ThemeGreen.BorderBrush = theme == ReaderTheme.Green ? accent : normal;
            ThemeNight.BorderBrush = theme == ReaderTheme.Night ? accent : normal;
            ThemeBlack.BorderBrush = theme == ReaderTheme.Black ? accent : normal;
        }

        private async void RefreshStats()
        {
            int usable = 0;
            for (int i = 0; i < AppState.Sources.Count; i++)
                if (AppState.Sources[i].IsUsable) usable++;

            SourceCountText.Text = "书源：" + AppState.Sources.Count + " 个（可用 " + usable + " 个）";
            ShelfCountText.Text = "书架：" + AppState.Shelf.Count + " 本";
            CacheCountText.Text = "已缓存：正文 " + AppState.ChapterCache.MemoryCount
                                  + " 章，目录 " + AppState.TocCache.MemoryCount + " 本";
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            AboutText.Text = "版本 " + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision
                + " · 目标设备 Lumia 950 / Windows 10 Mobile";
            var images = await AppState.ComicImageCache.StatsAsync();
            CacheCountText.Text += "；漫画图片 " + images.Count + " 张（" + (images.Bytes / 1048576.0).ToString("0.0") + " MB）";
        }

        // ================================================================ 阅读设置

        private void FontSizeSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.FontSize = Math.Round(e.NewValue);
            FontSizeText.Text = ((int)AppState.Settings.FontSize) + " px";
            Save();
        }

        private void LineHeightSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.LineHeight = Math.Round(e.NewValue, 1);
            LineHeightText.Text = AppState.Settings.LineHeight.ToString("0.0") + " 倍";
            Save();
        }

        private void PaddingSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.Padding = Math.Round(e.NewValue);
            PaddingText.Text = ((int)AppState.Settings.Padding) + " px";
            Save();
        }

        private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var item = FontCombo.SelectedItem as FontChoice;
            if (item == null) return;
            AppState.Settings.FontFamily = item.Family;
            SettingsStatusText.Text = "阅读字体：" + item.Label + "。";
            Save();
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null || button.Tag == null) return;

            ReaderTheme theme;
            if (!Enum.TryParse<ReaderTheme>(button.Tag.ToString(), out theme)) return;

            AppState.Settings.Theme = theme;
            RefreshThemeSelection();
            Save();
        }

        private void PageTurn_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.PageTurn = PageTurnScroll.IsChecked == true
                ? PageTurnMode.Scroll
                : PageTurnMode.Page;
            Save();
        }

        private void Layout_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.Indent = IndentCheck.IsChecked == true;
            AppState.Settings.BlankLineBetweenParagraphs = BlankLineCheck.IsChecked == true;
            Save();
        }

        private void KeepAwake_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AppState.Settings.KeepAwake = KeepAwakeCheck.IsChecked == true;
            Save();
        }

        private async void Save()
        {
            try
            {
                await AppState.SaveSettingsAsync();
                SettingsStatusText.Text = "设置已保存，进入阅读器即生效。";
            }
            catch (Exception ex)
            {
                SettingsStatusText.Text = "保存失败：" + ex.Message;
            }
        }

        // ================================================================ 数据管理

        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = "清空本地缓存",
                Content = "会删除本地保存的正文、目录和漫画图片，下次阅读需重新下载。确定继续吗？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            await AppState.ChapterCache.ClearAsync();
            await AppState.TocCache.ClearAsync();
            await AppState.ComicImageCache.ClearAsync();
            RefreshStats();
            SettingsStatusText.Text = "正文、目录与漫画图片缓存已清空。";
            AppState.Log("清空本地缓存");
        }

        private async void ClearShelf_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ContentDialog
            {
                Title = "清空书架",
                Content = "会移除全部书架条目与阅读进度（不影响书源）。确定继续吗？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            AppState.Shelf.Clear();
            await AppState.SaveShelfAsync();
            RefreshStats();
            SettingsStatusText.Text = "书架已清空。";
            AppState.Log("清空书架");

            var shell = MainPage.Current;
            if (shell != null) shell.RefreshHeader();
        }
    }
}
