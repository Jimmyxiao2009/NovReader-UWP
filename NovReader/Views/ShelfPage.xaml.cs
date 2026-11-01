using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using NovReader.Models;
using NovReader.Services;

namespace NovReader.Views
{
    /// <summary>书架：显示已收藏的书，点一下继续阅读，长按（或点「…」）打开详情 / 移出书架。</summary>
    public sealed partial class ShelfPage : Page, IShellPage
    {
        private bool _recentFirst = true;

        public ShelfPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AppState.Shelf.CollectionChanged += Shelf_CollectionChanged;
            Reload();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            AppState.Shelf.CollectionChanged -= Shelf_CollectionChanged;
        }

        /// <summary>从阅读器返回时进度已经变了，重新排一遍。</summary>
        public void OnShellResumed() { Reload(); }

        private void Shelf_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Reload();
        }

        private void Reload()
        {
            var list = new List<ShelfBook>(AppState.Shelf);
            if (_recentFirst)
                list.Sort(delegate (ShelfBook a, ShelfBook b) { return b.LastReadTime.CompareTo(a.LastReadTime); });
            else
                list.Sort(delegate (ShelfBook a, ShelfBook b) { return b.AddedTime.CompareTo(a.AddedTime); });

            ShelfView.ItemsSource = null;
            ShelfView.ItemsSource = list;

            bool empty = list.Count == 0;
            EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ShelfPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ContinueButton.IsEnabled = !empty;
            SortButton.IsEnabled = list.Count > 1;
            SortButton.Label = _recentFirst ? "按加入时间" : "按最近阅读";

            if (!empty)
            {
                int reading = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].ChapterCount > 0) reading++;
                ShelfSummaryText.Text = "共 " + list.Count + " 本，" + reading + " 本在读 · "
                                        + (_recentFirst ? "按最近阅读排列" : "按加入时间排列") + " · 长按可管理";
            }

            var shell = MainPage.Current;
            if (shell != null) shell.RefreshHeader();
        }

        private void ShelfView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ShelfBook;
            if (item == null) return;
            ReaderLauncher.Open(item);
        }

        // ---------------------------------------------------------------- 长按菜单

        private void Item_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            ShowMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            ShowMenu(sender as FrameworkElement);
        }

        /// <summary>菜单在代码里建：附加到模板里的 Flyout 不一定能拿到条目的 DataContext。</summary>
        private void ShowMenu(FrameworkElement anchor)
        {
            var item = anchor == null ? null : anchor.DataContext as ShelfBook;
            if (item == null) return;

            var menu = new MenuFlyout();
            var read = new MenuFlyoutItem { Text = "继续阅读" };
            read.Click += delegate { ReaderLauncher.Open(item); };
            var detail = new MenuFlyoutItem { Text = "详情与目录" };
            detail.Click += delegate { BookDetailPage.OpenShelf(item); };
            var remove = new MenuFlyoutItem { Text = "移出书架" };
            remove.Click += delegate
            {
                AppState.RemoveFromShelf(item);
                SaveShelf();
            };
            menu.Items.Add(read);
            menu.Items.Add(detail);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(remove);
            menu.ShowAt(anchor);
        }

        // ---------------------------------------------------------------- 应用栏

        private void ContinueReading_Click(object sender, RoutedEventArgs e)
        {
            ShelfBook latest = null;
            for (int i = 0; i < AppState.Shelf.Count; i++)
            {
                var item = AppState.Shelf[i];
                if (item.ChapterCount == 0) continue;
                if (latest == null || item.LastReadTime > latest.LastReadTime) latest = item;
            }
            if (latest == null && AppState.Shelf.Count > 0) latest = AppState.Shelf[0];
            if (latest == null) return;
            ReaderLauncher.Open(latest);
        }

        private void Sort_Click(object sender, RoutedEventArgs e)
        {
            _recentFirst = !_recentFirst;
            Reload();
        }

        private void GoSearch_Click(object sender, RoutedEventArgs e) { Navigate("search"); }
        private void GoSources_Click(object sender, RoutedEventArgs e) { Navigate("sources"); }

        private static void Navigate(string key)
        {
            var shell = MainPage.Current;
            if (shell != null) shell.Navigate(key);
        }

        private static async void SaveShelf()
        {
            try { await AppState.SaveShelfAsync(); }
            catch (Exception ex) { AppState.Log("保存书架失败：" + ex.Message); }
        }
    }
}
