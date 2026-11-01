using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using NovReader.Services;
using NovReader.Views;

namespace NovReader
{
    /// <summary>
    /// 应用外壳：顶栏 + 汉堡菜单（SplitView）+ 内容 Frame，对应 Windows 10 Mobile 的导航模式。
    /// 主页面（书架 / 搜索 / 书源 / 设置 / 调试）由内部 Frame 承载，
    /// 阅读器与书籍详情则用外层 Frame 整屏打开。外壳页面被缓存，返回时保留当前页面和搜索结果。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /// <summary>当前外壳实例，供内部页面切换页面用。</summary>
        public static MainPage Current { get; private set; }

        public sealed class NavItem
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public string Glyph { get; set; }
            public Type PageType { get; set; }

            // 列表项的辅助功能名称取自 ToString，屏幕阅读器和 UI 自动化靠它识别菜单项
            public override string ToString() { return Title; }
        }

        private readonly List<NavItem> _main = new List<NavItem>();
        private readonly List<NavItem> _footer = new List<NavItem>();
        private string _currentKey = "";
        private bool _ready;
        private bool _syncingSelection;

        public MainPage()
        {
            InitializeComponent();

            _main.Add(new NavItem { Key = "shelf", Title = "书架", Glyph = "\uE8F1", PageType = typeof(ShelfPage) });
            _main.Add(new NavItem { Key = "search", Title = "搜索", Glyph = "\uE721", PageType = typeof(SearchPage) });
            _main.Add(new NavItem { Key = "sources", Title = "书源", Glyph = "\uE909", PageType = typeof(SourcesPage) });
            _footer.Add(new NavItem { Key = "debug", Title = "调试", Glyph = "\uE90F", PageType = typeof(DebugPage) });
            _footer.Add(new NavItem { Key = "settings", Title = "设置", Glyph = "\uE713", PageType = typeof(SettingsPage) });

            NavList.ItemsSource = _main;
            NavFooterList.ItemsSource = _footer;

            ContentFrame.Navigated += ContentFrame_Navigated;
            Current = this;
            Loaded += MainPage_Loaded;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_ready) return;
            _ready = true;

            await AppState.EnsureLoadedAsync();
            ThemeManager.Apply(AppState.Settings.AppTheme);
            Navigate("shelf");
            RefreshHeader();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.NavigationMode != NavigationMode.Back) return;

            // 从详情页 / 阅读器返回：内部 Frame 没有重新导航，主动通知当前页面刷新
            var page = ContentFrame.Content as IShellPage;
            if (page != null)
            {
                try { page.OnShellResumed(); }
                catch (Exception ex) { AppState.Log("页面刷新失败：" + ex.Message); }
            }
            RefreshHeader();
        }

        /// <summary>供其它页面调用：切到某个主页面。</summary>
        public void Navigate(string key)
        {
            var target = Find(key);
            if (target == null) return;

            _currentKey = key;
            if (ContentFrame.CurrentSourcePageType != target.PageType)
                ContentFrame.Navigate(target.PageType, null, new EntranceNavigationTransitionInfo());
            SyncSelection();
            RefreshHeader();
            ShellSplit.IsPaneOpen = false;
        }

        /// <summary>
        /// 硬件返回键：先收起菜单，再回到书架；已经在书架时交给系统（退出应用）。
        /// </summary>
        public bool HandleBack()
        {
            if (ShellSplit.IsPaneOpen && ShellSplit.DisplayMode != SplitViewDisplayMode.Inline)
            {
                ShellSplit.IsPaneOpen = false;
                return true;
            }
            if (_currentKey != "shelf" && _currentKey.Length > 0)
            {
                Navigate("shelf");
                return true;
            }
            return false;
        }

        private NavItem Find(string key)
        {
            for (int i = 0; i < _main.Count; i++) if (_main[i].Key == key) return _main[i];
            for (int i = 0; i < _footer.Count; i++) if (_footer[i].Key == key) return _footer[i];
            return null;
        }

        private void Hamburger_Click(object sender, RoutedEventArgs e)
        {
            ShellSplit.IsPaneOpen = !ShellSplit.IsPaneOpen;
        }

        private void Nav_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as NavItem;
            if (item != null) Navigate(item.Key);
        }

        private void HeaderSearch_Click(object sender, RoutedEventArgs e) { Navigate("search"); }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // 页面内部跳转时（例如设置页跳到书源页）同步菜单选中项
            foreach (var list in new[] { _main, _footer })
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].PageType != e.SourcePageType) continue;
                    _currentKey = list[i].Key;
                    SyncSelection();
                    RefreshHeader();
                    return;
                }
            }
        }

        private void SyncSelection()
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            try
            {
                NavList.SelectedItem = null;
                NavFooterList.SelectedItem = null;
                var item = Find(_currentKey);
                if (item == null) return;
                if (_main.Contains(item)) NavList.SelectedItem = item;
                else NavFooterList.SelectedItem = item;
            }
            finally { _syncingSelection = false; }
        }

        /// <summary>刷新顶栏标题与副标题（例如显示书架数量、当前书源数量）。</summary>
        public void RefreshHeader()
        {
            var current = Find(_currentKey);
            HeaderTitleText.Text = current == null ? "NovReader" : current.Title;
            HeaderSearchButton.Visibility = _currentKey == "search" ? Visibility.Collapsed : Visibility.Visible;

            switch (_currentKey)
            {
                case "shelf":
                    HeaderSubtitleText.Text = AppState.Shelf.Count == 0 ? "" : AppState.Shelf.Count + " 本";
                    break;
                case "sources":
                    HeaderSubtitleText.Text = AppState.Sources.Count == 0
                        ? "还没有书源"
                        : AppState.Sources.Count + " 个书源";
                    break;
                case "search":
                    HeaderSubtitleText.Text = AppState.Sources.Count == 0
                        ? "先去「书源」导入书源"
                        : CountUsableSources() + " 个可用书源";
                    break;
                default:
                    HeaderSubtitleText.Text = "";
                    break;
            }
        }

        private static int CountUsableSources()
        {
            int count = 0;
            for (int i = 0; i < AppState.Sources.Count; i++)
                if (AppState.Sources[i].Enabled && AppState.Sources[i].IsUsable) count++;
            return count;
        }
    }
}
