using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>
    /// 目录（章节列表）本地缓存。
    /// 目录页往往比正文页大得多、也更容易触发站点限流，缓存下来后
    /// 重新打开一本书、或在详情页和阅读器之间来回切换都不再重复请求。
    /// </summary>
    public sealed class TocCache
    {
        // 目录解析规则修正过（Legado 默认语法、目录分页），旧目录缓存里可能是空章节名或只有第一页，
        // 换个目录名让旧缓存整体失效，旧目录在首次写入时删掉。
        private const string FolderName = "toc-cache-v2";
        private const string LegacyFolderName = "toc-cache";
        private const int MaxFiles = 200;
        private bool _legacyCleaned;

        private readonly Dictionary<string, List<Chapter>> _memory =
            new Dictionary<string, List<Chapter>>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private int _puts;

        /// <summary>内存里已缓存的目录数量。</summary>
        public int MemoryCount { get { lock (_gate) return _memory.Count; } }

        public List<Chapter> Get(string bookUrl)
        {
            if (String.IsNullOrEmpty(bookUrl)) return null;
            lock (_gate)
            {
                List<Chapter> cached;
                if (_memory.TryGetValue(bookUrl, out cached)) return cached;
            }
            return null;
        }

        public async Task<List<Chapter>> GetAsync(string bookUrl)
        {
            var cached = Get(bookUrl);
            if (cached != null) return cached;
            if (String.IsNullOrEmpty(bookUrl)) return null;

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.GetFileAsync(Key(bookUrl) + ".json");
                string text = await FileIO.ReadTextAsync(file);
                var list = Deserialize(text);
                if (list == null || list.Count == 0) return null;

                lock (_gate) { _memory[bookUrl] = list; }
                return list;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task PutAsync(string bookUrl, IList<Chapter> chapters)
        {
            if (String.IsNullOrEmpty(bookUrl) || chapters == null || chapters.Count == 0) return;

            var copy = new List<Chapter>(chapters);
            lock (_gate) { _memory[bookUrl] = copy; }

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(Key(bookUrl) + ".json", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, Serialize(copy));
            }
            catch (Exception)
            {
                return;
            }

            _puts++;
            if (_puts % 20 == 0) await TrimAsync();
            if (!_legacyCleaned) await DeleteLegacyAsync();
        }

        private async Task DeleteLegacyAsync()
        {
            _legacyCleaned = true;
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(LegacyFolderName);
                if (item != null) await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception) { }
        }

        public async Task ClearAsync()
        {
            lock (_gate) { _memory.Clear(); }
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                foreach (var file in await folder.GetFilesAsync())
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception)
            {
                return;
            }
        }

        private async Task TrimAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var files = await folder.GetFilesAsync();
                if (files.Count <= MaxFiles) return;

                var list = new List<StorageFile>(files);
                list.Sort(delegate (StorageFile a, StorageFile b) { return a.DateCreated.CompareTo(b.DateCreated); });
                int remove = list.Count - MaxFiles / 2;
                for (int i = 0; i < remove && i < list.Count; i++)
                    await list[i].DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception)
            {
                return;
            }
        }

        // ---------------------------------------------------------------- 序列化

        private static string Serialize(List<Chapter> chapters)
        {
            var array = JsonValue.NewArray();
            for (int i = 0; i < chapters.Count; i++)
            {
                var node = JsonValue.NewObject();
                node.Add("name", Str(chapters[i].Name));
                node.Add("url", Str(chapters[i].Url));
                node.Add("index", Num(chapters[i].Index));
                node.Add("vip", new JsonValue { Kind = JsonKind.Boolean, Bool = chapters[i].IsVip });
                array.Items.Add(node);
            }
            return array.ToJson();
        }

        private static List<Chapter> Deserialize(string text)
        {
            var root = LegadoJson.ParseLoose(text);
            if (root == null || root.Kind != JsonKind.Array || root.Items == null) return null;

            var list = new List<Chapter>();
            for (int i = 0; i < root.Items.Count; i++)
            {
                var node = root.Items[i];
                if (node == null || node.Kind != JsonKind.Object) continue;
                list.Add(new Chapter
                {
                    Name = StrOf(node.Get("name")),
                    Url = StrOf(node.Get("url")),
                    Index = (int)NumOf(node.Get("index")),
                    IsVip = node.Get("vip") != null && node.Get("vip").Kind == JsonKind.Boolean && node.Get("vip").Bool
                });
            }
            return list;
        }

        private static JsonValue Str(string value) { return new JsonValue { Kind = JsonKind.String, Str = value ?? "" }; }
        private static JsonValue Num(double value) { return new JsonValue { Kind = JsonKind.Number, Number = value }; }

        private static string StrOf(JsonValue value) { return value == null ? "" : value.AsString(); }

        private static double NumOf(JsonValue value)
        {
            if (value == null) return 0;
            if (value.Kind == JsonKind.Number) return value.Number;
            double parsed;
            return Double.TryParse(value.AsString(), out parsed) ? parsed : 0;
        }

        private static string Key(string url)
        {
            if (String.IsNullOrEmpty(url)) return "empty";
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < url.Length; i++)
            {
                hash ^= url[i];
                hash *= 1099511628211UL;
            }
            var sb = new StringBuilder();
            sb.Append(hash.ToString("x16"));
            sb.Append('-');
            sb.Append(url.Length.ToString("x"));
            return sb.ToString();
        }
    }
}
