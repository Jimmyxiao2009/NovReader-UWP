using System;

namespace NovReader.Models
{
    /// <summary>应用整体外观（与阅读器内的纸张主题相互独立）。</summary>
    public enum AppThemeMode
    {
        /// <summary>跟随系统。</summary>
        System = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>导出文件格式。</summary>
    public enum ExportFormat
    {
        Txt = 0,
        Html = 1,
        Markdown = 2,
        Epub = 3
    }

    /// <summary>导出文件编码。</summary>
    public enum ExportEncoding
    {
        Utf8 = 0,
        Utf8Bom = 1,
        Utf16Le = 2,
        Gbk = 3,
        Big5 = 4
    }

    /// <summary>导出与下载相关的偏好。</summary>
    public sealed class TransferSettings
    {
        public const int MinThreads = 1;
        public const int MaxThreads = 16;

        /// <summary>下载/导出的并发线程数。</summary>
        public int Threads { get; set; }
        /// <summary>聚合搜索的并行书源数。</summary>
        public int SearchThreads { get; set; }
        /// <summary>下载整本时是否跳过已缓存的章节。</summary>
        public bool SkipCached { get; set; }
        public ExportFormat Format { get; set; }
        public ExportEncoding Encoding { get; set; }

        public TransferSettings()
        {
            Threads = 4;
            SearchThreads = 8;
            SkipCached = true;
            Format = ExportFormat.Txt;
            Encoding = ExportEncoding.Utf8;
        }

        public static string FormatName(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Html: return "HTML (.html)";
                case ExportFormat.Markdown: return "Markdown (.md)";
                case ExportFormat.Epub: return "EPUB 电子书 (.epub)";
                default: return "纯文本 (.txt)";
            }
        }

        public static string EncodingName(ExportEncoding encoding)
        {
            switch (encoding)
            {
                case ExportEncoding.Utf8Bom: return "UTF-8 带 BOM";
                case ExportEncoding.Utf16Le: return "UTF-16 LE";
                case ExportEncoding.Gbk: return "GBK / GB2312";
                case ExportEncoding.Big5: return "Big5 繁体";
                default: return "UTF-8 无 BOM";
            }
        }

        public static string ExtensionOf(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Html: return ".html";
                case ExportFormat.Markdown: return ".md";
                case ExportFormat.Epub: return ".epub";
                default: return ".txt";
            }
        }
    }
}
