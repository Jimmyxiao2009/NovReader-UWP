using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>书架持久化。自己写 JSON，不依赖序列化框架。</summary>
    public sealed class ShelfStore
    {
        private const string FileName = "shelf.json";

        public async Task<List<ShelfBook>> LoadAsync()
        {
            var result = new List<ShelfBook>();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                // 写完临时文件、还没来得及改名时被关掉：正式文件不在，就用临时文件
                var file = (await folder.TryGetItemAsync(FileName) ?? await folder.TryGetItemAsync(FileName + ".tmp")) as StorageFile;
                if (file == null) return result;
                string text = await FileIO.ReadTextAsync(file);
                var root = LegadoJson.ParseLoose(text);
                if (root == null || root.Kind != JsonKind.Array || root.Items == null) return result;

                foreach (var node in root.Items)
                {
                    if (node == null || node.Kind != JsonKind.Object) continue;
                    result.Add(new ShelfBook
                    {
                        BookUrl = Str(node, "bookUrl"),
                        Name = Str(node, "name"),
                        Author = Str(node, "author"),
                        CoverUrl = Str(node, "coverUrl"),
                        Intro = Str(node, "intro"),
                        TocUrl = Str(node, "tocUrl"),
                        SourceName = Str(node, "sourceName"),
                        ChapterIndex = (int)Num(node, "chapterIndex"),
                        ChapterName = Str(node, "chapterName"),
                        ScrollRatio = Num(node, "scrollRatio"),
                        ChapterCount = (int)Num(node, "chapterCount"),
                        AddedTime = Time(node, "addedTime"),
                        LastReadTime = Time(node, "lastReadTime")
                    });
                }
            }
            catch (Exception)
            {
                // 首次运行或文件损坏时返回空书架
            }
            return result;
        }

        // 阅读进度、详情页、换源都会存书架，必须串行；写临时文件再替换，写到一半被杀也不会丢掉整个书架
        private readonly System.Threading.SemaphoreSlim _saveGate = new System.Threading.SemaphoreSlim(1, 1);

        public async Task SaveAsync(IList<ShelfBook> shelf)
        {
            // 先在 UI 线程上拍快照，序列化期间集合再变也不受影响
            string json = Serialize(new List<ShelfBook>(shelf));
            await _saveGate.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var temp = await folder.CreateFileAsync(FileName + ".tmp", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(temp, json);
                await temp.RenameAsync(FileName, NameCollisionOption.ReplaceExisting);
            }
            finally
            {
                _saveGate.Release();
            }
        }

        private static string Serialize(IList<ShelfBook> shelf)
        {
            var array = JsonValue.NewArray();
            foreach (var item in shelf)
            {
                var node = JsonValue.NewObject();
                node.Add("bookUrl", StrOf(item.BookUrl));
                node.Add("name", StrOf(item.Name));
                node.Add("author", StrOf(item.Author));
                node.Add("coverUrl", StrOf(item.CoverUrl));
                node.Add("intro", StrOf(item.Intro));
                node.Add("tocUrl", StrOf(item.TocUrl));
                node.Add("sourceName", StrOf(item.SourceName));
                node.Add("chapterIndex", NumOf(item.ChapterIndex));
                node.Add("chapterName", StrOf(item.ChapterName));
                node.Add("scrollRatio", NumOf(item.ScrollRatio));
                node.Add("chapterCount", NumOf(item.ChapterCount));
                node.Add("addedTime", NumOf(item.AddedTime.Ticks));
                node.Add("lastReadTime", NumOf(item.LastReadTime.Ticks));
                array.Items.Add(node);
            }
            return array.ToJson();
        }

        private static string Str(JsonValue node, string name)
        {
            var v = node.Get(name);
            return v == null ? "" : v.AsString();
        }

        private static double Num(JsonValue node, string name)
        {
            var v = node.Get(name);
            if (v == null) return 0;
            if (v.Kind == JsonKind.Number) return v.Number;
            double parsed;
            if (Double.TryParse(v.AsString(), out parsed)) return parsed;
            return 0;
        }

        private static DateTime Time(JsonValue node, string name)
        {
            double ticks = Num(node, name);
            if (ticks <= 0) return DateTime.Now;
            try { return new DateTime((long)ticks); }
            catch (Exception) { return DateTime.Now; }
        }

        private static JsonValue StrOf(string value) { return new JsonValue { Kind = JsonKind.String, Str = value ?? "" }; }
        private static JsonValue NumOf(double value) { return new JsonValue { Kind = JsonKind.Number, Number = value }; }
    }
}
