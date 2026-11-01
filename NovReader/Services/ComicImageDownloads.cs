using System;
using System.Threading.Tasks;

namespace NovReader.Services
{
    /// <summary>有界的图片预取窗口；下载并行，显示按章节顺序串行，保留调用方 UI 上下文。</summary>
    public static class ComicImageDownloads
    {
        public static async Task RunAsync(int count, int concurrency, Func<int, Task<byte[]>> fetch,
            Func<int, byte[], Task> display, Func<bool> isCurrent)
        {
            if (count <= 0 || !isCurrent()) return;
            concurrency = Math.Max(1, Math.Min(8, Math.Min(count, concurrency)));
            var pending = new Task<byte[]>[concurrency];
            for (int i = 0; i < concurrency && isCurrent(); i++) pending[i] = FetchSafely(fetch, i);
            for (int i = 0; i < count && isCurrent(); i++)
            {
                int slot = i % concurrency;
                byte[] bytes = await pending[slot];
                pending[slot] = null;
                if (!isCurrent()) return;
                await display(i, bytes);
                if (!isCurrent()) return;
                int next = i + concurrency;
                if (next < count) pending[slot] = FetchSafely(fetch, next);
            }
        }

        private static async Task<byte[]> FetchSafely(Func<int, Task<byte[]>> fetch, int index)
        {
            try { return await fetch(index).ConfigureAwait(false); }
            catch (Exception) { return null; }
        }
    }
}
