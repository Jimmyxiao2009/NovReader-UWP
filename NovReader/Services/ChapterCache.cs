using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>
    /// 章节正文缓存：内存 + 本地文件两级。
    /// 翻回上一章、或同一章反复进出时不必重复请求站点，省流量也更快。
    ///
    /// 写入和读出都会做一次正文清理：写入时保证落盘的是纯文本，
    /// 读出时兼容旧版本缓存里可能残留的 HTML 片段。
    /// </summary>
    public sealed class ChapterCache
    {
        private const string FolderName = "chapter-cache";
        private const int MaxFiles = 800;

        private readonly Dictionary<string, string> _memory = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private int _puts;

        public int MemoryCount { get { lock (_gate) return _memory.Count; } }

        public bool TryGetMemory(string url, out string text)
        {
            lock (_gate) { return _memory.TryGetValue(Key(url), out text); }
        }

        public async Task<string> GetAsync(string url)
        {
            string cached;
            if (TryGetMemory(url, out cached)) return IsFailureText(cached) ? null : LegadoEngine.SanitizeContent(cached);

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.GetFileAsync(Key(url) + ".txt");
                string text = await FileIO.ReadTextAsync(file);
                // 旧版本会把“（抓取失败：…）”当正文写进缓存，读到时当未命中，重新抓取
                if (IsFailureText(text)) return null;
                lock (_gate) { _memory[Key(url)] = text; }
                return LegadoEngine.SanitizeContent(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsFailureText(string text)
        {
            return !String.IsNullOrEmpty(text) && text.StartsWith("（抓取失败", StringComparison.Ordinal);
        }

        public async Task PutAsync(string url, string text)
        {
            if (String.IsNullOrEmpty(url)) return;
            string clean = LegadoEngine.SanitizeContent(text);
            if (String.IsNullOrEmpty(clean) || IsFailureText(clean)) return;

            lock (_gate) { _memory[Key(url)] = clean; }

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(Key(url) + ".txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, clean);
            }
            catch (Exception)
            {
                return;
            }

            _puts++;
            if (_puts % 100 == 0) await TrimAsync();
        }

        public async Task ClearAsync()
        {
            lock (_gate) { _memory.Clear(); }
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var files = await folder.GetFilesAsync();
                foreach (var file in files) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>缓存文件过多时按最后修改时间删掉一半，避免无限增长。</summary>
        private async Task TrimAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    FolderName, CreationCollisionOption.OpenIfExists);
                var files = await folder.GetFilesAsync();
                if (files.Count <= MaxFiles) return;

                var list = new List<StorageFile>(files);
                list.Sort(delegate (StorageFile a, StorageFile b)
                {
                    return a.DateCreated.CompareTo(b.DateCreated);
                });
                int remove = list.Count - MaxFiles / 2;
                for (int i = 0; i < remove && i < list.Count; i++)
                    await list[i].DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception)
            {
                return;
            }
        }

        /// <summary>把 URL 映射成稳定且文件名安全的名字。</summary>
        private static string Key(string url)
        {
            if (String.IsNullOrEmpty(url)) return "empty";
            // FNV-1a 64 位，纯托管实现，避免依赖加密库
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
