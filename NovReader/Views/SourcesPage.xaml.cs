using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;
using NovReader.Services.Legado;

namespace NovReader.Views
{
    /// <summary>书源管理：本地书源列表 + 在线书源广场。</summary>
    public sealed partial class SourcesPage : Page
    {
        private readonly ObservableCollection<BookSource> _filtered = new ObservableCollection<BookSource>();
        private readonly ObservableCollection<PlazaCollection> _plaza = new ObservableCollection<PlazaCollection>();
        private bool _loadingPlaza;
        private bool _importingAll;

        public SourcesPage()
        {
            InitializeComponent();
            SourceView.ItemsSource = _filtered;
            PlazaView.ItemsSource = _plaza;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFilter();
            SetSourceStatus(AppState.Sources.Count == 0
                ? "还没有书源。可以点「示例书源」加入离线测试源，或到「书源广场」导入在线书源。"
                : "共 " + AppState.Sources.Count + " 个书源，其中可用 " + CountUsable() + " 个。");
        }

        private static int CountUsable()
        {
            int count = 0;
            for (int i = 0; i < AppState.Sources.Count; i++)
                if (AppState.Sources[i].IsUsable) count++;
            return count;
        }

        // ================================================================ 我的书源

        private void ApplyFilter()
        {
            string filter = FilterBox.Text == null ? "" : FilterBox.Text.Trim();
            _filtered.Clear();

            for (int i = 0; i < AppState.Sources.Count; i++)
            {
                var source = AppState.Sources[i];
                if (filter.Length > 0 && !Matches(source, filter)) continue;
                _filtered.Add(source);
            }
        }

        private static bool Matches(BookSource source, string filter)
        {
            return Contains(source.Name, filter)
                || Contains(source.Group, filter)
                || Contains(source.Url, filter)
                || Contains(source.Comment, filter);
        }

        private static bool Contains(string haystack, string needle)
        {
            if (String.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Filter_Changed(object sender, TextChangedEventArgs e) { ApplyFilter(); }

        private void SourceView_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        /// <summary>两个分页各有一套应用栏按钮。</summary>
        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MineBar == null || PlazaBar == null) return;
            bool plaza = MainPivot.SelectedIndex == 1;
            MineBar.Visibility = plaza ? Visibility.Collapsed : Visibility.Visible;
            PlazaBar.Visibility = plaza ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void SourceView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var source = e.ClickedItem as BookSource;
            if (source == null) return;
            await ShowSourceDetailAsync(source);
        }

        /// <summary>查看书源规则详情，可切换启用状态或删除。</summary>
        private async Task ShowSourceDetailAsync(BookSource source)
        {
            var text = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 300,
                FontFamily = new Windows.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                Text = DescribeSource(source)
            };

            var toggle = new CheckBox
            {
                Content = "启用这个书源",
                IsChecked = source.Enabled,
                Margin = new Windows.UI.Xaml.Thickness(0, 8, 0, 0)
            };

            var panel = new StackPanel();
            panel.Children.Add(new ScrollViewer { Content = text });
            panel.Children.Add(toggle);

            var dialog = new ContentDialog
            {
                Title = source.DisplayName,
                Content = panel,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "删除书源",
                CloseButtonText = "关闭"
            };

            var result = await Dialogs.ShowAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                source.Enabled = toggle.IsChecked == true;
                await AppState.SaveSourcesAsync();
                ApplyFilter();
                SetSourceStatus("已更新书源：" + source.Name);
                AppState.Log("更新书源设置：" + source.Name + "，启用=" + source.Enabled);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                AppState.Sources.Remove(source);
                await AppState.SaveSourcesAsync();
                ApplyFilter();
                SetSourceStatus("已删除书源：" + source.Name);
                AppState.Log("删除书源：" + source.Name);
                RefreshShellHeader();
            }
        }

        private static string DescribeSource(BookSource s)
        {
            var sb = new StringBuilder();
            sb.Append("名称：").Append(s.Name).Append('\n');
            sb.Append("分组：").Append(s.Group).Append('\n');
            sb.Append("站点：").Append(s.Url).Append('\n');
            sb.Append("搜索地址：").Append(s.SearchUrl).Append('\n');
            if (!String.IsNullOrEmpty(s.Comment)) sb.Append("备注：").Append(s.Comment).Append('\n');
            sb.Append('\n').Append("搜索规则").Append('\n');
            sb.Append("  bookList：").Append(s.Search.BookList).Append('\n');
            sb.Append("  name：").Append(s.Search.Name).Append('\n');
            sb.Append("  author：").Append(s.Search.Author).Append('\n');
            sb.Append("  kind：").Append(s.Search.Kind).Append('\n');
            sb.Append("  intro：").Append(s.Search.Intro).Append('\n');
            sb.Append("  bookUrl：").Append(s.Search.BookUrl).Append('\n');
            sb.Append("  lastChapter：").Append(s.Search.LastChapter).Append('\n');
            sb.Append("  coverUrl：").Append(s.Search.CoverUrl).Append('\n');
            sb.Append('\n').Append("详情规则").Append('\n');
            sb.Append("  init：").Append(s.BookInfo.Init).Append('\n');
            sb.Append("  name：").Append(s.BookInfo.Name).Append('\n');
            sb.Append("  author：").Append(s.BookInfo.Author).Append('\n');
            sb.Append("  kind：").Append(s.BookInfo.Kind).Append('\n');
            sb.Append("  wordCount：").Append(s.BookInfo.WordCount).Append('\n');
            sb.Append("  intro：").Append(s.BookInfo.Intro).Append('\n');
            sb.Append("  coverUrl：").Append(s.BookInfo.CoverUrl).Append('\n');
            sb.Append("  tocUrl：").Append(s.BookInfo.TocUrl).Append('\n');
            sb.Append("  lastChapter：").Append(s.BookInfo.LastChapter).Append('\n');
            sb.Append('\n').Append("目录规则").Append('\n');
            sb.Append("  chapterList：").Append(s.Toc.ChapterList).Append('\n');
            sb.Append("  chapterName：").Append(s.Toc.ChapterName).Append('\n');
            sb.Append("  chapterUrl：").Append(s.Toc.ChapterUrl).Append('\n');
            sb.Append("  isVip：").Append(s.Toc.IsVip).Append('\n');
            sb.Append("  updateTime：").Append(s.Toc.UpdateTime).Append('\n');
            sb.Append("  nextTocUrl：").Append(s.Toc.NextTocUrl).Append('\n');
            sb.Append('\n').Append("正文规则").Append('\n');
            sb.Append("  content：").Append(s.Content.Content).Append('\n');
            sb.Append("  nextContentUrl：").Append(s.Content.NextContentUrl).Append('\n');
            if (!String.IsNullOrEmpty(s.Header)) sb.Append('\n').Append("请求头").Append('\n').Append(s.Header).Append('\n');
            return sb.ToString();
        }

        private async void AddSample_Click(object sender, RoutedEventArgs e)
        {
            int added = AppState.MergeSources(LocalTestSite.SampleSources());
            await AppState.SaveSourcesAsync();
            ApplyFilter();
            SetSourceStatus(added == 0
                ? "内置示例书源已经存在。"
                : "已加入 " + added + " 个内置示例书源（离线测试用，不需要联网）。");
            AppState.Log("加入示例书源 " + added + " 个");
            RefreshShellHeader();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 260,
                PlaceholderText = "粘贴 Legado 书源 JSON（数组或单个对象）"
            };
            var dialog = new ContentDialog
            {
                Title = "导入书源 JSON",
                Content = new ScrollViewer { Content = input },
                PrimaryButtonText = "导入",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            var parsed = LegadoSourceJson.Parse(input.Text);
            if (parsed.Count == 0)
            {
                SetSourceStatus("导入失败：没有解析到书源，请检查 JSON 格式。");
                return;
            }

            int added = AppState.MergeSources(parsed);
            await AppState.SaveSourcesAsync();
            ApplyFilter();
            SetSourceStatus("导入完成：解析 " + parsed.Count + " 个，新增 " + added + " 个。");
            AppState.Log("导入书源 " + parsed.Count + " 个，新增 " + added);
            RefreshShellHeader();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var output = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 300,
                Text = LegadoSourceJson.Serialize(AppState.Sources)
            };
            output.SelectAll();
            var dialog = new ContentDialog
            {
                Title = "导出书源 JSON（可全选复制）",
                Content = new ScrollViewer { Content = output },
                CloseButtonText = "关闭"
            };
            await Dialogs.ShowAsync(dialog);
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var source = SourceView.SelectedItem as BookSource;
            if (source == null) { SetSourceStatus("请先在列表里选中要删除的书源。"); return; }

            var confirm = new ContentDialog
            {
                Title = "删除书源",
                Content = "确定删除《" + source.Name + "》吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            AppState.Sources.Remove(source);
            await AppState.SaveSourcesAsync();
            ApplyFilter();
            SetSourceStatus("已删除书源：" + source.Name);
            AppState.Log("删除书源：" + source.Name);
            RefreshShellHeader();
        }

        private async void Reload_Click(object sender, RoutedEventArgs e)
        {
            var saved = await AppState.SourceStore.LoadAsync();
            AppState.Sources.Clear();
            foreach (var s in saved) AppState.Sources.Add(s);
            ApplyFilter();
            SetSourceStatus("已重新载入书源，共 " + AppState.Sources.Count + " 个。");
            RefreshShellHeader();
        }

        // ================================================================ 书源广场

        private async void LoadPlaza_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingPlaza) return;
            _loadingPlaza = true;
            ImportAllButton.IsEnabled = false;
            PlazaBusyRing.IsActive = true;
            SetPlazaStatus("正在获取在线书源列表…");
            try
            {
                var list = await SourcePlaza.DiscoverAsync();
                _plaza.Clear();
                foreach (var item in list) _plaza.Add(item);

                if (list.Count == 0)
                    SetPlazaStatus("没有发现书源合集。可以手动粘贴合集地址导入。");
                else if (String.IsNullOrEmpty(SourcePlaza.LastDiscoveryError))
                    SetPlazaStatus("已从 yuedu.miaogongzi.net 发现 " + list.Count + " 个合集，点击任意一项即可导入。");
                else
                    SetPlazaStatus("在线发现失败，已回落到内置合集列表（" + list.Count
                                   + " 项）。原因：" + SourcePlaza.LastDiscoveryError);
                AppState.Log("书源广场发现 " + list.Count + " 个合集");
            }
            catch (Exception ex)
            {
                SetPlazaStatus("获取失败：" + ex.Message);
            }
            finally
            {
                _loadingPlaza = false;
                ImportAllButton.IsEnabled = true;
                PlazaBusyRing.IsActive = false;
            }
        }

        private async void PlazaView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var collection = e.ClickedItem as PlazaCollection;
            if (collection == null) return;

            SetPlazaStatus("正在下载并解析：" + collection.DisplayName);
            try
            {
                var sources = await SourcePlaza.FetchCollectionAsync(collection);
                if (sources.Count == 0)
                {
                    SetPlazaStatus("该合集没有解析到书源。"
                        + (String.IsNullOrEmpty(collection.Error) ? "" : "错误：" + collection.Error));
                    return;
                }

                int added = AppState.MergeSources(sources);
                await AppState.SaveSourcesAsync();
                ApplyFilter();
                SetPlazaStatus("已从《" + collection.DisplayName + "》导入 " + added
                               + " 个书源（合集共 " + sources.Count + " 个，去重后新增）。");
                AppState.Log("从广场导入 " + collection.DisplayName + "：" + added + "/" + sources.Count);
                RefreshShellHeader();
            }
            catch (Exception ex)
            {
                SetPlazaStatus("导入失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 一键全部导入：把当前列表里的每个合集依次下载解析，全部合并去重后只保存一次。
        /// 单个合集失败只记录，不影响其它合集。
        /// </summary>
        private async void ImportAll_Click(object sender, RoutedEventArgs e)
        {
            if (_importingAll) return;

            if (_plaza.Count == 0)
            {
                SetPlazaStatus("请先点「获取在线书源列表」，再执行一键全部导入。");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "一键全部导入",
                Content = "将依次下载并导入当前列表里的 " + _plaza.Count
                          + " 个合集，去重后合并到「我的书源」。合集较大时可能需要几分钟，确定继续吗？",
                PrimaryButtonText = "开始导入",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            _importingAll = true;
            ImportAllButton.IsEnabled = false;
            PlazaBusyRing.IsActive = true;

            int totalParsed = 0, totalAdded = 0, okCollections = 0, failedCollections = 0;
            var failures = new List<string>();

            try
            {
                for (int i = 0; i < _plaza.Count; i++)
                {
                    var collection = _plaza[i];
                    SetPlazaStatus("(" + (i + 1) + "/" + _plaza.Count + ") 正在下载并解析："
                                   + collection.DisplayName + "…");

                    try
                    {
                        var sources = await SourcePlaza.FetchCollectionAsync(collection);
                        if (sources.Count == 0)
                        {
                            failedCollections++;
                            failures.Add(collection.DisplayName + "（"
                                + (String.IsNullOrEmpty(collection.Error) ? "没有解析到书源" : collection.Error) + "）");
                            continue;
                        }

                        totalParsed += sources.Count;
                        totalAdded += AppState.MergeSources(sources);
                        okCollections++;

                        // 每处理完一个就落盘一次，中途失败也不会丢掉已导入的部分
                        await AppState.SaveSourcesAsync();
                        ApplyFilter();
                        RefreshShellHeader();
                    }
                    catch (Exception ex)
                    {
                        failedCollections++;
                        failures.Add(collection.DisplayName + "（" + ex.Message + "）");
                    }
                }

                var summary = new StringBuilder();
                summary.Append("一键导入完成：成功 ").Append(okCollections).Append(" 个合集");
                if (failedCollections > 0) summary.Append("，失败 ").Append(failedCollections).Append(" 个");
                summary.Append("；共解析 ").Append(totalParsed).Append(" 个书源，去重后新增 ")
                       .Append(totalAdded).Append(" 个。当前书源总数 ").Append(AppState.Sources.Count).Append(" 个。");
                if (failures.Count > 0)
                {
                    summary.Append(" 失败合集：");
                    int show = Math.Min(3, failures.Count);
                    for (int i = 0; i < show; i++)
                    {
                        if (i > 0) summary.Append("；");
                        summary.Append(failures[i]);
                    }
                    if (failures.Count > show) summary.Append(" 等 ").Append(failures.Count).Append(" 个");
                }
                SetPlazaStatus(summary.ToString());
                AppState.Log("一键全部导入：成功 " + okCollections + "/" + _plaza.Count
                             + "，解析 " + totalParsed + "，新增 " + totalAdded);
            }
            finally
            {
                _importingAll = false;
                ImportAllButton.IsEnabled = true;
                PlazaBusyRing.IsActive = false;
            }
        }

        private async void ImportFromUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = PlazaUrlBox.Text == null ? "" : PlazaUrlBox.Text.Trim();
            if (url.Length == 0) { SetPlazaStatus("请先粘贴书源合集地址。"); return; }

            SetPlazaStatus("正在下载：" + url);
            try
            {
                var collection = new PlazaCollection { Name = url, Url = url };
                var sources = await SourcePlaza.FetchCollectionAsync(collection);
                if (sources.Count == 0)
                {
                    SetPlazaStatus("没有解析到书源。"
                        + (String.IsNullOrEmpty(collection.Error) ? "" : "错误：" + collection.Error));
                    return;
                }

                int added = AppState.MergeSources(sources);
                await AppState.SaveSourcesAsync();
                ApplyFilter();
                SetPlazaStatus("导入完成：新增 " + added + " 个书源（共解析 " + sources.Count + " 个）。");
                AppState.Log("从链接导入书源：" + added + "/" + sources.Count);
                RefreshShellHeader();
            }
            catch (Exception ex)
            {
                SetPlazaStatus("导入失败：" + ex.Message);
            }
        }

        private async void Manual_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 260,
                PlaceholderText = "粘贴单个书源 JSON 对象"
            };
            var dialog = new ContentDialog
            {
                Title = "手动添加书源",
                Content = new ScrollViewer { Content = input },
                PrimaryButtonText = "添加",
                CloseButtonText = "取消"
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            var parsed = LegadoSourceJson.Parse(input.Text);
            if (parsed.Count == 0) { SetPlazaStatus("没有解析到书源，请检查 JSON。"); return; }

            int added = AppState.MergeSources(parsed);
            await AppState.SaveSourcesAsync();
            ApplyFilter();
            SetPlazaStatus("已添加 " + added + " 个书源（解析 " + parsed.Count + " 个）。");
            RefreshShellHeader();
        }

        // ================================================================ 辅助

        private void SetSourceStatus(string text) { SourceStatusText.Text = text; }
        private void SetPlazaStatus(string text) { PlazaStatusText.Text = text; }

        private static void RefreshShellHeader()
        {
            var shell = MainPage.Current;
            if (shell != null) shell.RefreshHeader();
        }
    }
}
