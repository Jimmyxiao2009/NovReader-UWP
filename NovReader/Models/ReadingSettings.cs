using System;

namespace NovReader.Models
{
    /// <summary>阅读主题配色。</summary>
    public enum ReaderTheme
    {
        Day = 0,
        Night = 1,
        Sepia = 2,
        Green = 3,
        Black = 4
    }

    /// <summary>翻页方式。</summary>
    public enum PageTurnMode
    {
        /// <summary>整屏翻页（点两侧翻页）。</summary>
        Page = 0,
        /// <summary>连续滚动。</summary>
        Scroll = 1
    }

    /// <summary>阅读设置。字段用属性以便 XAML 双向绑定。</summary>
    public sealed class ReadingSettings
    {
        public const double MinFontSize = 14;
        public const double MaxFontSize = 40;
        public const double MinLineHeight = 1.2;
        public const double MaxLineHeight = 2.6;
        public const int MaxComicImageThreads = 8;

        public double FontSize { get; set; }
        /// <summary>行距倍数。</summary>
        public double LineHeight { get; set; }
        /// <summary>段间距（像素）。</summary>
        public double ParagraphSpacing { get; set; }
        public string FontFamily { get; set; }
        public ReaderTheme Theme { get; set; }
        public PageTurnMode PageTurn { get; set; }
        /// <summary>是否显示顶部/底部状态栏（沉浸模式开关）。</summary>
        public bool ShowBars { get; set; }
        /// <summary>页面左右内边距。</summary>
        public double Padding { get; set; }
        /// <summary>首行缩进两个全角空格。</summary>
        public bool Indent { get; set; }
        /// <summary>段落之间空一行。</summary>
        public bool BlankLineBetweenParagraphs { get; set; }
        /// <summary>阅读时保持屏幕常亮。</summary>
        public bool KeepAwake { get; set; }
        /// <summary>应用整体外观（浅色/深色/跟随系统）。</summary>
        public AppThemeMode AppTheme { get; set; }
        /// <summary>下载/导出的并发线程数。</summary>
        public int DownloadThreads { get; set; }
        /// <summary>聚合搜索的并行书源数。</summary>
        public int SearchThreads { get; set; }
        /// <summary>漫画当前章节的图片使用独立并发设置。</summary>
        public bool ComicParallelDownload { get; set; }
        public int ComicImageThreads { get; set; }
        /// <summary>下载整本时跳过已缓存章节。</summary>
        public bool SkipCached { get; set; }
        /// <summary>聚合搜索时跳过近期连续失败的书源。</summary>
        public bool SkipFailingSources { get; set; }
        /// <summary>上次使用的导出格式。</summary>
        public ExportFormat ExportFormat { get; set; }
        /// <summary>上次使用的导出编码。</summary>
        public ExportEncoding ExportEncoding { get; set; }

        public ReadingSettings()
        {
            FontSize = 20;
            LineHeight = 1.8;
            ParagraphSpacing = 12;
            FontFamily = "";
            Theme = ReaderTheme.Day;
            PageTurn = PageTurnMode.Page;
            ShowBars = true;
            Padding = 20;
            Indent = true;
            BlankLineBetweenParagraphs = true;
            KeepAwake = false;
            AppTheme = AppThemeMode.System;
            DownloadThreads = 4;
            SearchThreads = 16;
            ComicParallelDownload = true;
            ComicImageThreads = 4;
            SkipCached = true;
            SkipFailingSources = true;
            ExportFormat = ExportFormat.Txt;
            ExportEncoding = ExportEncoding.Utf8;
        }

        public ReadingSettings Clone()
        {
            return new ReadingSettings
            {
                FontSize = FontSize,
                LineHeight = LineHeight,
                ParagraphSpacing = ParagraphSpacing,
                FontFamily = FontFamily,
                Theme = Theme,
                PageTurn = PageTurn,
                ShowBars = ShowBars,
                Padding = Padding,
                Indent = Indent,
                BlankLineBetweenParagraphs = BlankLineBetweenParagraphs,
                KeepAwake = KeepAwake,
                AppTheme = AppTheme,
                DownloadThreads = DownloadThreads,
                SearchThreads = SearchThreads,
                ComicParallelDownload = ComicParallelDownload,
                ComicImageThreads = ComicImageThreads,
                SkipCached = SkipCached,
                SkipFailingSources = SkipFailingSources,
                ExportFormat = ExportFormat,
                ExportEncoding = ExportEncoding
            };
        }

        /// <summary>正文区域的实际行高（像素）。翻页时按它的整数倍滚动，保证不切断行。</summary>
        public double LinePixelHeight
        {
            get { return Math.Round(FontSize * LineHeight); }
        }

        /// <summary>主题对应的背景色。</summary>
        public string BackgroundColor
        {
            get
            {
                switch (Theme)
                {
                    case ReaderTheme.Night: return "#FF121212";
                    case ReaderTheme.Black: return "#FF000000";
                    case ReaderTheme.Sepia: return "#FFF6ECD8";
                    case ReaderTheme.Green: return "#FFCCE8CF";
                    default: return "#FFFFFFFF";
                }
            }
        }

        /// <summary>主题对应的文字色。</summary>
        public string ForegroundColor
        {
            get
            {
                switch (Theme)
                {
                    case ReaderTheme.Night: return "#FF9E9E9E";
                    case ReaderTheme.Black: return "#FF8A8A8A";
                    case ReaderTheme.Sepia: return "#FF4A3C2A";
                    case ReaderTheme.Green: return "#FF203026";
                    default: return "#FF1A1A1A";
                }
            }
        }

        /// <summary>状态栏/浮层背景色，和正文背景保持同一色系。</summary>
        public string BarBackgroundColor
        {
            get
            {
                switch (Theme)
                {
                    case ReaderTheme.Night: return "#F2202020";
                    case ReaderTheme.Black: return "#F21A1A1A";
                    case ReaderTheme.Sepia: return "#F2EADFC4";
                    case ReaderTheme.Green: return "#F2B9DCC0";
                    default: return "#F2F2F2F2";
                }
            }
        }

        public string ThemeName
        {
            get
            {
                switch (Theme)
                {
                    case ReaderTheme.Night: return "夜间";
                    case ReaderTheme.Black: return "纯黑";
                    case ReaderTheme.Sepia: return "羊皮纸";
                    case ReaderTheme.Green: return "护眼绿";
                    default: return "日间";
                }
            }
        }
    }
}
