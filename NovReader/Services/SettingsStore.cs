using System;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>阅读设置持久化。</summary>
    public sealed class SettingsStore
    {
        private const string FileName = "settings.json";

        public async Task<ReadingSettings> LoadAsync()
        {
            var settings = new ReadingSettings();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string text = await FileIO.ReadTextAsync(file);
                var root = LegadoJson.ParseLoose(text);
                if (root == null || root.Kind != JsonKind.Object) return settings;

                settings.FontSize = Clamp(Num(root, "fontSize", settings.FontSize),
                    ReadingSettings.MinFontSize, ReadingSettings.MaxFontSize);
                settings.LineHeight = Clamp(Num(root, "lineHeight", settings.LineHeight),
                    ReadingSettings.MinLineHeight, ReadingSettings.MaxLineHeight);
                settings.ParagraphSpacing = Clamp(Num(root, "paragraphSpacing", settings.ParagraphSpacing), 0, 40);
                settings.Padding = Clamp(Num(root, "padding", settings.Padding), 8, 48);

                // 空串表示“系统默认”；旧版本存的中文字体名换成字体族名
                if (root.Get("fontFamily") != null) settings.FontFamily = FontCatalog.Normalize(Str(root, "fontFamily"));

                settings.Theme = (ReaderTheme)(int)Clamp(Num(root, "theme", (int)settings.Theme), 0, 4);
                settings.PageTurn = (PageTurnMode)(int)Clamp(Num(root, "pageTurn", (int)settings.PageTurn), 0, 1);
                settings.ShowBars = Bool(root, "showBars", settings.ShowBars);
                settings.Indent = Bool(root, "indent", settings.Indent);
                settings.BlankLineBetweenParagraphs = Bool(root, "blankLine", settings.BlankLineBetweenParagraphs);
                settings.KeepAwake = Bool(root, "keepAwake", settings.KeepAwake);
                settings.AppTheme = (AppThemeMode)(int)Clamp(Num(root, "appTheme", (int)settings.AppTheme), 0, 2);
                settings.DownloadThreads = (int)Clamp(Num(root, "downloadThreads", settings.DownloadThreads),
                    TransferSettings.MinThreads, TransferSettings.MaxThreads);
                settings.SearchThreads = (int)Clamp(Num(root, "searchThreads", settings.SearchThreads),
                    1, MultiSearch.MaxConcurrency);
                settings.ComicParallelDownload = Bool(root, "comicParallelDownload", settings.ComicParallelDownload);
                settings.ComicImageThreads = (int)Clamp(Num(root, "comicImageThreads", settings.ComicImageThreads),
                    1, ReadingSettings.MaxComicImageThreads);
                settings.SkipCached = Bool(root, "skipCached", settings.SkipCached);
                settings.SkipFailingSources = Bool(root, "skipFailingSources", settings.SkipFailingSources);
                settings.ExportFormat = (ExportFormat)(int)Clamp(Num(root, "exportFormat", (int)settings.ExportFormat), 0, 3);
                settings.ExportEncoding = (ExportEncoding)(int)Clamp(Num(root, "exportEncoding", (int)settings.ExportEncoding), 0, 4);
            }
            catch (Exception)
            {
                // 首次运行或文件损坏时用默认值
            }
            return settings;
        }

        public async Task SaveAsync(ReadingSettings settings)
        {
            var root = JsonValue.NewObject();
            root.Add("fontSize", NumOf(settings.FontSize));
            root.Add("lineHeight", NumOf(settings.LineHeight));
            root.Add("paragraphSpacing", NumOf(settings.ParagraphSpacing));
            root.Add("padding", NumOf(settings.Padding));
            root.Add("fontFamily", StrOf(settings.FontFamily));
            root.Add("theme", NumOf((int)settings.Theme));
            root.Add("pageTurn", NumOf((int)settings.PageTurn));
            root.Add("showBars", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.ShowBars });
            root.Add("indent", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.Indent });
            root.Add("blankLine", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.BlankLineBetweenParagraphs });
            root.Add("keepAwake", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.KeepAwake });
            root.Add("appTheme", NumOf((int)settings.AppTheme));
            root.Add("downloadThreads", NumOf(settings.DownloadThreads));
            root.Add("searchThreads", NumOf(settings.SearchThreads));
            root.Add("comicParallelDownload", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.ComicParallelDownload });
            root.Add("comicImageThreads", NumOf(settings.ComicImageThreads));
            root.Add("skipCached", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.SkipCached });
            root.Add("skipFailingSources", new JsonValue { Kind = JsonKind.Boolean, Bool = settings.SkipFailingSources });
            root.Add("exportFormat", NumOf((int)settings.ExportFormat));
            root.Add("exportEncoding", NumOf((int)settings.ExportEncoding));

            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, root.ToJson());
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static string Str(JsonValue node, string name)
        {
            var v = node.Get(name);
            return v == null ? "" : v.AsString();
        }

        private static double Num(JsonValue node, string name, double fallback)
        {
            var v = node.Get(name);
            if (v == null) return fallback;
            if (v.Kind == JsonKind.Number) return v.Number;
            double parsed;
            return Double.TryParse(v.AsString(), out parsed) ? parsed : fallback;
        }

        private static bool Bool(JsonValue node, string name, bool fallback)
        {
            var v = node.Get(name);
            if (v == null) return fallback;
            if (v.Kind == JsonKind.Boolean) return v.Bool;
            return String.Equals(v.AsString(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static JsonValue StrOf(string value) { return new JsonValue { Kind = JsonKind.String, Str = value ?? "" }; }
        private static JsonValue NumOf(double value) { return new JsonValue { Kind = JsonKind.Number, Number = value }; }
    }
}
