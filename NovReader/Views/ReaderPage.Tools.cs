using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using NovReader.Models;
using NovReader.Services;

namespace NovReader.Views
{
    public sealed partial class ReaderPage
    {
        private sealed class ComicImageSlot
        {
            public Image Image;
            public Grid Frame;
            public StackPanel Placeholder;
            public TextBlock Status;
            public Button Retry;
            public string Url;
            public int Index;
            public bool Loading, Failed;
        }
        private readonly List<ComicImageSlot> _comicSlots = new List<ComicImageSlot>();
        private ComicImageSlot CreateComicSlot(string url, int index)
        {
            var slot = new ComicImageSlot { Url = url, Index = index };
            slot.Frame = new Grid { MinHeight = 240 };
            slot.Image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
            slot.Placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            slot.Status = new TextBlock { Text = "正在加载第 " + (index + 1) + " 张图片…", FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(ParseColor(AppState.Settings.ForegroundColor)) };
            slot.Retry = new Button { Content = "重试这张图片", Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            slot.Retry.Click += delegate { RetryComicSlots(new List<ComicImageSlot> { slot }); };
            slot.Placeholder.Children.Add(slot.Status); slot.Placeholder.Children.Add(slot.Retry);
            slot.Frame.Children.Add(slot.Image); slot.Frame.Children.Add(slot.Placeholder);
            slot.Image.Holding += delegate(object sender, HoldingRoutedEventArgs e)
            {
                if (e.HoldingState == Windows.UI.Input.HoldingState.Started) { e.Handled = true; OpenImageZoom(slot); }
            };
            return slot;
        }
        private ComicImageSlot SlotOf(Image image) { return _comicSlots.Find(slot => slot.Image == image); }
        private void SetComicSlotLoading(Image image)
        {
            var slot = SlotOf(image); if (slot == null) return;
            slot.Loading = true; slot.Failed = false;
            slot.Status.Text = "正在加载第 " + (slot.Index + 1) + " 张图片…";
            slot.Retry.Visibility = Visibility.Collapsed;
        }
        private void SetComicSlotFailed(Image image, string reason)
        {
            var slot = SlotOf(image); if (slot == null) return;
            slot.Loading = false; slot.Failed = true;
            slot.Status.Text = "第 " + (slot.Index + 1) + " 张图片：" + reason;
            slot.Placeholder.Visibility = Visibility.Visible; slot.Retry.Visibility = Visibility.Visible;
        }
        private void SetComicSlotReady(Image image)
        {
            var slot = SlotOf(image); if (slot == null) return;
            slot.Loading = false; slot.Failed = false;
            slot.Placeholder.Visibility = Visibility.Collapsed; slot.Frame.MinHeight = 0;
        }
        private void RetryComicSlots(List<ComicImageSlot> slots)
        {
            if (_leaving || !_imageMode) return;
            var pending = new List<KeyValuePair<Image, string>>();
            foreach (var slot in slots)
            {
                if (slot.Loading || !slot.Failed || !_comicSlots.Contains(slot)) continue;
                SetComicSlotLoading(slot.Image); pending.Add(new KeyValuePair<Image, string>(slot.Image, slot.Url));
            }
            if (pending.Count == 0) return;
            string referer = _chapterIndex >= 0 && _chapterIndex < _chapters.Count ? _chapters[_chapterIndex].Url : "";
            LoadImages(pending, referer, _imageGeneration);
        }
        private void RetryFailedImages_Click(object sender, RoutedEventArgs e)
        {
            if (!_imageMode) { FlashHint("当前章节没有漫画图片"); return; }
            var failed = _comicSlots.FindAll(slot => slot.Failed && !slot.Loading);
            if (failed.Count == 0) { FlashHint("当前没有需要重试的图片"); return; }
            RetryComicSlots(failed);
        }

        private void OpenImageZoom(ComicImageSlot slot)
        {
            if (_leaving || slot.Image.Source == null) return;
            ImageZoomContent.Source = slot.Image.Source;
            ImageZoomContent.Width = Math.Max(1, RootGrid.ActualWidth);
            ImageZoomLayer.Visibility = Visibility.Visible;
            ImageZoomScroll.UpdateLayout(); ImageZoomScroll.ChangeView(0, 0, 1, true);
        }
        private void ZoomCurrentImage_Click(object sender, RoutedEventArgs e)
        {
            if (!_imageMode) { FlashHint("当前章节没有漫画图片"); return; }
            double middle = ReaderScroll.ViewportHeight / 2; ComicImageSlot best = null; double distance = Double.MaxValue;
            foreach (var slot in _comicSlots)
            {
                if (slot.Image.Source == null) continue;
                var point = slot.Frame.TransformToVisual(ReaderScroll).TransformPoint(new Point(0, slot.Frame.ActualHeight / 2));
                double d = Math.Abs(point.Y - middle); if (d < distance) { best = slot; distance = d; }
            }
            if (best == null) { FlashHint("等待图片加载完成后再放大"); return; }
            OpenImageZoom(best);
        }
        private void CloseImageZoom() { ImageZoomLayer.Visibility = Visibility.Collapsed; ImageZoomContent.Source = null; }
        private void CloseZoom_Click(object sender, RoutedEventArgs e) { CloseImageZoom(); }
        private void ImageZoomScroll_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            ImageZoomScroll.ChangeView(0, 0, 1, true); e.Handled = true;
        }

        private async void AddBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || _leaving || _book == null || _chapters.Count == 0 || String.IsNullOrEmpty(_currentContent))
            { FlashHint("正文加载完成后可以添加书签"); return; }
            var chapter = _chapters[_chapterIndex];
            var bookmark = new ReadingBookmark { BookUrl = _book.Url, ChapterUrl = chapter.Url,
                ChapterName = chapter.DisplayName, ChapterIndex = _chapterIndex, ScrollRatio = ScrollRatio() };
            var note = new TextBox { Header = "备注（可选）", PlaceholderText = "记录一句话或阅读心得", MaxLength = 1000,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
            var result = await Dialogs.ShowAsync(new ContentDialog { Title = "添加书签", Content = note,
                PrimaryButtonText = "保存", CloseButtonText = "取消" });
            if (result != ContentDialogResult.Primary) return;
            bookmark.Note = note.Text;
            try
            {
                await AppState.BookmarkStore.SaveAsync(bookmark);
                Services.Legado.Js.JscBackend.TraceStage("reading bookmark saved: " + bookmark.ChapterName);
                FlashHint("书签已保存");
                if (BookmarkLayer.Visibility == Visibility.Visible) await RefreshBookmarksAsync();
            }
            catch (Exception error) { FlashHint("书签保存失败：" + error.Message); }
        }
        private async Task RefreshBookmarksAsync()
        {
            string book = _book.Url;
            var bookmarks = await AppState.BookmarkStore.LoadAsync(book);
            if (_leaving || _book.Url != book) return;
            BookmarkList.ItemsSource = bookmarks;
            BookmarkEmptyText.Visibility = bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private async void ShowBookmarks_Click(object sender, RoutedEventArgs e)
        {
            if (_book == null) return;
            TocLayer.Visibility = SettingsLayer.Visibility = FindPanel.Visibility = Visibility.Collapsed;
            BookmarkLayer.Visibility = Visibility.Visible;
            try { await RefreshBookmarksAsync(); } catch (Exception error) { FlashHint(error.Message); }
        }
        private async void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var bookmark = e.ClickedItem as ReadingBookmark; if (bookmark == null) return;
            int index = _chapters.FindIndex(chapter => chapter.Url == bookmark.ChapterUrl);
            if (index < 0) index = _chapters.FindIndex(chapter => chapter.DisplayName == bookmark.ChapterName);
            if (index < 0) index = bookmark.ChapterIndex;
            if (index < 0 || index >= _chapters.Count) { FlashHint("书签对应章节已不存在"); return; }
            BookmarkLayer.Visibility = Visibility.Collapsed;
            if (index == _chapterIndex && !_loading && !String.IsNullOrEmpty(_currentContent)) ScrollToRatio(bookmark.ScrollRatio);
            else await OpenChapterAsync(index, bookmark.ScrollRatio);
            if (_leaving || index != _chapterIndex) return;
            if (_imageMode)
            {
                _pendingImageBookmarkRatio = bookmark.ScrollRatio;
                RestoreImageBookmarkIfReady();
            }
            SaveProgress(bookmark.ScrollRatio);
        }
        private double? _pendingImageBookmarkRatio;
        private void RestoreImageBookmarkIfReady()
        {
            if (!_pendingImageBookmarkRatio.HasValue || !_imageMode || _leaving ||
                !_comicSlots.TrueForAll(slot => slot.Image.Source != null || slot.Failed)) return;
            double ratio = _pendingImageBookmarkRatio.Value; _pendingImageBookmarkRatio = null;
            ReaderScroll.UpdateLayout(); ScrollToRatio(ratio); SaveProgress(ratio);
        }
        private async void EditBookmark_Click(object sender, RoutedEventArgs e)
        {
            var bookmark = (sender as FrameworkElement)?.DataContext as ReadingBookmark; if (bookmark == null) return;
            var note = new TextBox { Text = bookmark.Note, MaxLength = 1000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
            if (await Dialogs.ShowAsync(new ContentDialog { Title = "书签备注", Content = note,
                PrimaryButtonText = "保存", CloseButtonText = "取消" }) != ContentDialogResult.Primary) return;
            bookmark.Note = note.Text;
            try { await AppState.BookmarkStore.SaveAsync(bookmark); await RefreshBookmarksAsync(); }
            catch (Exception error) { FlashHint(error.Message); }
        }
        private async void DeleteBookmark_Click(object sender, RoutedEventArgs e)
        {
            var bookmark = (sender as FrameworkElement)?.DataContext as ReadingBookmark; if (bookmark == null) return;
            try { await AppState.BookmarkStore.RemoveAsync(bookmark.Id); await RefreshBookmarksAsync(); }
            catch (Exception error) { FlashHint(error.Message); }
        }
        private void CloseBookmarks_Click(object sender, RoutedEventArgs e) { BookmarkLayer.Visibility = Visibility.Collapsed; }
        private void BookmarkBackdrop_Tapped(object sender, TappedRoutedEventArgs e) { BookmarkLayer.Visibility = Visibility.Collapsed; }

        private readonly List<int> _findOffsets = new List<int>();
        private int _findIndex;
        private string _findKeyword = "";
        private void ShowFind_Click(object sender, RoutedEventArgs e)
        {
            if (_imageMode || ContentBlock.Visibility != Visibility.Visible || String.IsNullOrEmpty(ContentBlock.Text))
            { FlashHint("当前只能查找小说章节中的文字"); return; }
            BookmarkLayer.Visibility = TocLayer.Visibility = SettingsLayer.Visibility = Visibility.Collapsed;
            FindPanel.Visibility = Visibility.Visible; FindBox.Focus(FocusState.Programmatic);
        }
        private void FindText_Click(object sender, RoutedEventArgs e)
        {
            _findKeyword = FindBox.Text.Trim(); _findOffsets.Clear(); _findOffsets.AddRange(ChapterTextSearch.Find(ContentBlock.Text, _findKeyword));
            _findIndex = 0; ShowFindResult();
        }
        private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter) { FindText_Click(sender, null); e.Handled = true; }
        }
        private void FindPrevious_Click(object sender, RoutedEventArgs e)
        {
            if (_findOffsets.Count > 0) _findIndex = (_findIndex + _findOffsets.Count - 1) % _findOffsets.Count;
            ShowFindResult();
        }
        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            if (_findOffsets.Count > 0) _findIndex = (_findIndex + 1) % _findOffsets.Count;
            ShowFindResult();
        }
        private void ShowFindResult()
        {
            if (_findOffsets.Count == 0) { FindStatusText.Text = String.IsNullOrEmpty(_findKeyword) ? "输入要查找的文字" : "本章没有找到匹配内容"; return; }
            int offset = _findOffsets[_findIndex];
            FindStatusText.Text = (_findIndex + 1) + " / " + _findOffsets.Count + (_findOffsets.Count == ChapterTextSearch.MaxResults ? "（最多显示500处）" : "")
                + "\n" + ChapterTextSearch.Preview(ContentBlock.Text, offset, _findKeyword.Length);
            RootGrid.UpdateLayout();
            try
            {
                var run = ContentBlock.Inlines.Count > 0 ? ContentBlock.Inlines[0] as Run : null;
                var start = run == null ? ContentBlock.ContentStart : run.ContentStart;
                var pointer = start.GetPositionAtOffset(offset, LogicalDirection.Forward);
                if (pointer == null) return;
                var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                var point = ContentBlock.TransformToVisual(ContentHost).TransformPoint(new Point(rect.X, rect.Y));
                ReaderScroll.ChangeView(null, Math.Max(0, point.Y - 12), null, true);
            }
            catch (Exception error) { AppState.Log("章节内查找定位失败：" + error.Message); }
        }
        private void CloseFind_Click(object sender, RoutedEventArgs e) { FindPanel.Visibility = Visibility.Collapsed; }

        public bool HandleBack()
        {
            if (ImageZoomLayer.Visibility == Visibility.Visible) { CloseImageZoom(); return true; }
            if (BookmarkLayer.Visibility == Visibility.Visible) { BookmarkLayer.Visibility = Visibility.Collapsed; return true; }
            if (FindPanel.Visibility == Visibility.Visible) { FindPanel.Visibility = Visibility.Collapsed; return true; }
            if (TocLayer.Visibility == Visibility.Visible) { TocLayer.Visibility = Visibility.Collapsed; return true; }
            if (SettingsLayer.Visibility == Visibility.Visible) { SettingsLayer.Visibility = Visibility.Collapsed; return true; }
            return false;
        }
    }
}
