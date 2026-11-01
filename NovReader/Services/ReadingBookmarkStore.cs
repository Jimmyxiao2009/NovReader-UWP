using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    public sealed class ReadingBookmarkStore
    {
        private readonly Func<string> _path;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        public ReadingBookmarkStore(Func<string> path) { _path = path; }

        public async Task<List<ReadingBookmark>> LoadAsync(string bookUrl)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await ReadAllAsync().ConfigureAwait(false);
                return list.FindAll(item => item.BookUrl == bookUrl);
            }
            finally { _gate.Release(); }
        }
        public async Task SaveAsync(ReadingBookmark bookmark)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await ReadAllAsync().ConfigureAwait(false);
                list.RemoveAll(item => item.Id == bookmark.Id);
                list.Insert(0, bookmark);
                await WriteAsync(list).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        public async Task RemoveAsync(string id)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await ReadAllAsync().ConfigureAwait(false);
                list.RemoveAll(item => item.Id == id);
                await WriteAsync(list).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private async Task<List<ReadingBookmark>> ReadAllAsync()
        {
            string path = _path();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                try
                {
                    using (var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                    {
                        if (file.Length > 8 * 1024 * 1024) continue;
                        var bytes = new byte[(int)file.Length]; int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int read = await file.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
                            if (read == 0) break; offset += read;
                        }
                        var root = LegadoJson.ParseLoose(Encoding.UTF8.GetString(bytes, 0, offset));
                        if (root == null || root.Kind != JsonKind.Array) continue;
                        var result = new List<ReadingBookmark>();
                        foreach (var item in root.Items)
                        {
                            if (item.Kind != JsonKind.Object || String.IsNullOrEmpty(Text(item, "id"))) continue;
                            result.Add(new ReadingBookmark
                            {
                                Id = Text(item, "id"), BookUrl = Text(item, "bookUrl"),
                                ChapterUrl = Text(item, "chapterUrl"), ChapterName = Text(item, "chapterName"),
                                ChapterIndex = Math.Max(0, (int)Number(item, "chapterIndex")),
                                ScrollRatio = Math.Max(0, Math.Min(1, Number(item, "scrollRatio"))),
                                Note = Text(item, "note"), CreatedAt = Text(item, "createdAt")
                            });
                        }
                        return result;
                    }
                }
                catch (Exception) { }
            }
            return new List<ReadingBookmark>();
        }

        private async Task WriteAsync(List<ReadingBookmark> bookmarks)
        {
            var root = JsonValue.NewArray();
            foreach (var item in bookmarks)
            {
                var node = JsonValue.NewObject();
                Add(node, "id", item.Id); Add(node, "bookUrl", item.BookUrl); Add(node, "chapterUrl", item.ChapterUrl);
                Add(node, "chapterName", item.ChapterName); Add(node, "note", item.Note); Add(node, "createdAt", item.CreatedAt);
                node.Add("chapterIndex", new JsonValue { Kind = JsonKind.Number, Number = item.ChapterIndex });
                node.Add("scrollRatio", new JsonValue { Kind = JsonKind.Number, Number = item.ScrollRatio });
                root.Items.Add(node);
            }
            string path = _path(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToJson());
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await file.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false); await file.FlushAsync().ConfigureAwait(false);
            }
            if (File.Exists(path)) { File.Copy(path, path + ".bak", true); File.Delete(path); }
            File.Move(path + ".tmp", path);
        }
        private static string Text(JsonValue node, string name) { return node.Get(name)?.AsString() ?? ""; }
        private static double Number(JsonValue node, string name) { var n = node.Get(name); return n != null && n.Kind == JsonKind.Number ? n.Number : 0; }
        private static void Add(JsonValue node, string name, string value) { node.Add(name, new JsonValue { Kind = JsonKind.String, Str = value ?? "" }); }
    }
}
