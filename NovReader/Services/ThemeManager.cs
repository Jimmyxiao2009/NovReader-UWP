using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using NovReader.Models;

namespace NovReader.Services
{
    /// <summary>
    /// 应用级深色模式。
    /// App.xaml 里的画刷是 StaticResource，运行时改这些画刷的 Color 就能整体换肤，
    /// 不需要重建页面；控件模板本身的明暗再通过 ElementTheme 交给系统处理。
    /// </summary>
    public static class ThemeManager
    {
        private sealed class Palette
        {
            public string AppBackground, Card, CardHover, Divider, Subtle;
            public string TextPrimary, TextSecondary, TextTertiary;
            public string Accent, AccentSoft, AccentPressed;
            public string Chrome, ChromeBorder;
            public string Success, Warning, Danger;
        }

        // Windows 10 Mobile 系统配色：浅色纯白底、深色纯黑底（OLED 屏省电），
        // 次要文字取系统 BaseMedium（60% 不透明度）对应的实色，chrome 与系统应用栏同色。
        private static readonly Palette Light = new Palette
        {
            AppBackground = "#FFFFFFFF",
            Card = "#FFF2F2F2",
            CardHover = "#FFE6E6E6",
            Divider = "#FFE6E6E6",
            Subtle = "#FFF2F2F2",
            TextPrimary = "#FF000000",
            TextSecondary = "#FF666666",
            TextTertiary = "#FF999999",
            Accent = "#FF0078D7",
            AccentSoft = "#330078D7",
            AccentPressed = "#FF005A9E",
            Chrome = "#FFF2F2F2",
            ChromeBorder = "#FFE6E6E6",
            Success = "#FF107C10",
            Warning = "#FFCA5010",
            Danger = "#FFE81123"
        };

        private static readonly Palette Dark = new Palette
        {
            AppBackground = "#FF000000",
            Card = "#FF1F1F1F",
            CardHover = "#FF2B2B2B",
            Divider = "#FF333333",
            Subtle = "#FF1F1F1F",
            TextPrimary = "#FFFFFFFF",
            TextSecondary = "#FF999999",
            TextTertiary = "#FF808080",
            Accent = "#FF0078D7",
            AccentSoft = "#330078D7",
            AccentPressed = "#FF429CE3",
            Chrome = "#FF1F1F1F",
            ChromeBorder = "#FF333333",
            Success = "#FF6BB700",
            Warning = "#FFF7630C",
            Danger = "#FFFF4343"
        };

        /// <summary>当前是否处于深色外观。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>主题变化后触发，页面可据此刷新自己画的颜色。</summary>
        public static event Action Changed;

        public static void Apply(AppThemeMode mode)
        {
            bool dark;
            switch (mode)
            {
                case AppThemeMode.Light: dark = false; break;
                case AppThemeMode.Dark: dark = true; break;
                default:
                    dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
                    break;
            }

            var palette = dark ? Dark : Light;
            IsDark = dark;

            SetBrush("AppBackgroundBrush", palette.AppBackground);
            SetBrush("CardBrush", palette.Card);
            SetBrush("CardHoverBrush", palette.CardHover);
            SetBrush("DividerBrush", palette.Divider);
            SetBrush("SubtleBrush", palette.Subtle);
            SetBrush("TextPrimaryBrush", palette.TextPrimary);
            SetBrush("TextSecondaryBrush", palette.TextSecondary);
            SetBrush("TextTertiaryBrush", palette.TextTertiary);
            ApplyAccent(palette);
            SetBrush("ChromeBrush", palette.Chrome);
            SetBrush("ChromeBorderBrush", palette.ChromeBorder);
            SetBrush("SuccessBrush", palette.Success);
            SetBrush("WarningBrush", palette.Warning);
            SetBrush("DangerBrush", palette.Danger);

            ApplyElementTheme(dark);
            RaiseChanged();
        }

        /// <summary>
        /// 让系统控件的默认模板也跟随明暗（文本框、按钮边框、滚动条、对话框等）。
        /// </summary>
        private static void ApplyElementTheme(bool dark)
        {
            var root = Window.Current == null ? null : Window.Current.Content as FrameworkElement;
            if (root != null)
                root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        }

        /// <summary>页面切换后调用，确保新页面的系统控件也跟随当前主题。</summary>
        public static void ReapplyToRoot()
        {
            ApplyElementTheme(IsDark);
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception) { }
            }
        }

        /// <summary>强调色跟随系统“个性化 → 颜色”，读不到时退回调色板里的默认蓝。</summary>
        private static void ApplyAccent(Palette palette)
        {
            Color accent = ParseColor(palette.Accent);
            try
            {
                // 系统主题资源不在应用字典里，TryGetValue 查不到，只能用索引器向上查找
                object value = Application.Current.Resources["SystemAccentColor"];
                if (value is Color) accent = (Color)value;
            }
            catch (Exception) { }

            SetBrush("AccentBrush", accent);
            SetBrush("AccentSoftBrush", Color.FromArgb(0x33, accent.R, accent.G, accent.B));
            // 按下态：浅色主题压暗、深色主题提亮
            SetBrush("AccentPressedBrush", IsDark ? Mix(accent, Colors.White, 0.3) : Mix(accent, Colors.Black, 0.25));
            try { Application.Current.Resources["AccentColor"] = accent; }
            catch (Exception) { }
        }

        private static Color Mix(Color a, Color b, double amount)
        {
            return Color.FromArgb(255,
                (byte)(a.R + (b.R - a.R) * amount),
                (byte)(a.G + (b.G - a.G) * amount),
                (byte)(a.B + (b.B - a.B) * amount));
        }

        private static void SetBrush(string key, Color color)
        {
            try
            {
                var brush = Application.Current.Resources[key] as SolidColorBrush;
                if (brush != null) brush.Color = color;
            }
            catch (Exception) { }
        }

        private static void SetBrush(string key, string hex)
        {
            try
            {
                var resources = Application.Current.Resources;
                if (!resources.ContainsKey(key)) return;
                var brush = resources[key] as SolidColorBrush;
                if (brush == null) return;
                brush.Color = ParseColor(hex);
            }
            catch (Exception)
            {
                // 资源缺失时忽略，不影响功能
            }
        }

        public static Color ParseColor(string hex)
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

        public static string ThemeModeName(AppThemeMode mode)
        {
            switch (mode)
            {
                case AppThemeMode.Light: return "浅色";
                case AppThemeMode.Dark: return "深色";
                default: return "跟随系统";
            }
        }
    }
}
