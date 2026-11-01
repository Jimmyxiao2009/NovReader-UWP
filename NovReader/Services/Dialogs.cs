using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;

namespace NovReader.Services
{
    /// <summary>
    /// ContentDialog 串行化。
    /// UWP 同一时刻只允许一个 ContentDialog，重复打开会抛
    /// “Only a single ContentDialog can be open at any time.”。
    /// 这里统一入口并记录占用状态，避免用户在异步流程里连点造成崩溃。
    /// </summary>
    public static class Dialogs
    {
        private static bool _open;
        private static ContentDialog _current;
        private static readonly object _gate = new object();

        public static bool IsOpen
        {
            get { lock (_gate) return _open; }
        }

        /// <summary>显示一个对话框；已有对话框打开时返回 null，不做任何操作。</summary>
        public static async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog)
        {
            if (dialog == null) return null;

            lock (_gate)
            {
                if (_open) return null;
                _open = true;
                _current = dialog;
            }

            try
            {
                return await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                AppState.Log("对话框显示失败：" + ex.Message);
                return null;
            }
            finally
            {
                lock (_gate) { _open = false; _current = null; }
            }
        }

        /// <summary>便捷方法：只有点了主按钮才返回 true。</summary>
        public static bool TryClose()
        {
            ContentDialog dialog;
            lock (_gate) { dialog = _current; }
            if (dialog == null) return false;
            dialog.Hide();
            return true;
        }

        public static async Task<bool> ConfirmAsync(string title, string content,
            string primary = "确定", string close = "取消")
        {
            var result = await ShowAsync(new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primary,
                CloseButtonText = close
            });
            return result == ContentDialogResult.Primary;
        }
    }
}
