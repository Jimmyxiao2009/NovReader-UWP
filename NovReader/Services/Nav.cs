using System;
using Windows.UI.Xaml.Controls;

namespace NovReader.Services
{
    /// <summary>极简页面导航。所有页面共用一个 Frame，便于统一处理返回键。</summary>
    public static class Nav
    {
        public static Frame Frame { get; set; }

        /// <summary>进入阅读器时传递的书架条目，避免用字符串参数序列化。</summary>
        public static object PendingReaderArgument { get; set; }

        public static bool CanGoBack
        {
            get { return Frame != null && Frame.CanGoBack; }
        }

        public static void To(Type pageType, object parameter = null)
        {
            if (Frame == null || pageType == null) return;
            Frame.Navigate(pageType, parameter);
        }

        private static Type _lastType;
        private static DateTime _lastAt;

        /// <summary>导航并记录日志，便于排查“点了没反应”。</summary>
        public static void ToLogged(Type pageType)
        {
            ToLogged(pageType, null);
        }

        /// <summary>
        /// 带参数导航。参数保存在返回栈里，从下一页返回时页面会重新创建，靠它恢复内容。
        /// 同一页面 800 毫秒内的重复导航（手机上连点两下）直接忽略，否则会叠出两个详情页。
        /// </summary>
        public static void ToLogged(Type pageType, object parameter)
        {
            if (Frame == null)
            {
                AppState.Log("导航失败：Nav.Frame 为空，无法打开 " + pageType.Name);
                return;
            }
            var now = DateTime.UtcNow;
            if (pageType == _lastType && (now - _lastAt).TotalMilliseconds < 800)
            {
                AppState.Log("忽略重复打开：" + pageType.Name);
                return;
            }
            _lastType = pageType;
            _lastAt = now;

            AppState.Log("打开页面：" + pageType.Name);
            bool ok = Frame.Navigate(pageType, parameter);
            if (!ok) AppState.Log("导航被拒绝：" + pageType.Name);
        }

        public static void Back()
        {
            if (CanGoBack) Frame.GoBack();
        }
    }
}
