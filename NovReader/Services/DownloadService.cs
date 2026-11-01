using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>下载/导出过程中的进度快照。</summary>
    public sealed class TransferProgress
    {
        public int Total;
        public int Done;
        public int Failed;
        public int Skipped;
        public string Current = "";

        public int Percent
        {
            get { return Total <= 0 ? 0 : (int)Math.Round(Done * 100.0 / Total); }
        }
    }

    public sealed class TransferResult
    {
        public int Total;
        public int Done;
        public int Failed;
        public int Skipped;
        public string Error = "";

        public bool Ok { get { return Failed == 0 && String.IsNullOrEmpty(Error); } }

        public string Summary
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("共 ").Append(Total).Append(" 章，成功 ").Append(Done);
                if (Skipped > 0) sb.Append("，跳过已缓存 ").Append(Skipped);
                if (Failed > 0) sb.Append("，失败 ").Append(Failed);
                if (!String.IsNullOrEmpty(Error)) sb.Append("。错误：").Append(Error);
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// 多线程章节下载。把整本正文并发抓下来写进本地缓存（ChapterCache），
    /// 之后阅读、导出都直接读缓存，不再访问站点。
    /// </summary>
    public static class DownloadService
    {
        public static async Task<TransferResult> DownloadBookAsync(
            Book book,
            IList<Chapter> chapters,
            int threads,
            bool skipCached,
            Action<TransferProgress> onProgress,
            CancellationToken token)
        {
            var result = new TransferResult { Total = chapters == null ? 0 : chapters.Count };
            if (book == null || chapters == null || chapters.Count == 0)
            {
                result.Error = "没有可下载的章节";
                return result;
            }
            if (book.Source == null)
            {
                result.Error = "这本书没有关联书源";
                return result;
            }

            int concurrency = ClampThreads(threads);
            if (book.Source.Type == LegadoEngine.ImageSourceType)
                concurrency = Math.Min(concurrency, AppState.Settings.ComicParallelDownload
                    ? Math.Max(1, Math.Min(ReadingSettings.MaxComicImageThreads, AppState.Settings.ComicImageThreads)) : 1);
            var gate = new SemaphoreSlim(concurrency);
            var sync = new object();
            var progress = new TransferProgress { Total = chapters.Count };
            var tasks = new List<Task>();

            for (int i = 0; i < chapters.Count; i++)
            {
                int index = i;
                var chapter = chapters[i];
                tasks.Add(Task.Run(async delegate
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    bool counted = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();

                        if (String.IsNullOrEmpty(chapter.Url))
                        {
                            lock (sync) { result.Failed++; progress.Failed++; }
                            counted = true;
                            return;
                        }

                        string content = null;
                        if (skipCached)
                        {
                            content = await AppState.ChapterCache.GetAsync(chapter.Url).ConfigureAwait(false);
                            if (!String.IsNullOrEmpty(content) && await ImagesCompleteAsync(book, content, token).ConfigureAwait(false))
                            {
                                lock (sync) { result.Skipped++; progress.Skipped++; }
                                counted = true;
                                return;
                            }
                        }

                        if (String.IsNullOrEmpty(content))
                        {
                            // 每个线程用独立引擎实例；已有文字但缺图片时直接补图片。
                            var engine = new LegadoEngine();
                            string nextUrl = index + 1 < chapters.Count ? chapters[index + 1].Url : null;
                            content = await engine.GetContentAsync(book, chapter, nextUrl).ConfigureAwait(false);
                        }

                        if (String.IsNullOrEmpty(content))
                        {
                            lock (sync) { result.Failed++; progress.Failed++; }
                        }
                        else
                        {
                            await AppState.ChapterCache.PutAsync(chapter.Url, content).ConfigureAwait(false);
                            int imageFailures = await CacheImagesAsync(book, chapter, content, token).ConfigureAwait(false);
                            lock (sync)
                            {
                                if (imageFailures == 0) { result.Done++; progress.Done++; }
                                else { result.Failed++; progress.Failed++; }
                            }
                        }
                        counted = true;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!counted) lock (sync) { result.Failed++; progress.Failed++; }
                    }
                    catch (Exception)
                    {
                        if (!counted) lock (sync) { result.Failed++; progress.Failed++; }
                    }
                    finally
                    {
                        lock (sync)
                        {
                            progress.Current = chapter.DisplayName;
                            if (counted) { /* 计数已在上面完成 */ }
                        }
                        gate.Release();
                        Report(onProgress, progress, sync);
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            Report(onProgress, progress, sync);
            AppState.Log("下载《" + book.Name + "》：" + result.Summary);
            return result;
        }

        public static int ClampThreads(int threads)
        {
            if (threads < TransferSettings.MinThreads) return TransferSettings.MinThreads;
            if (threads > TransferSettings.MaxThreads) return TransferSettings.MaxThreads;
            return threads;
        }

        private static List<string> ImagesOf(string content)
        {
            var urls = new List<string>();
            foreach (string line in (content ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string url = LegadoEngine.ImageUrlOf(line);
                if (url != null) urls.Add(url);
            }
            return urls;
        }

        private static async Task<bool> ImagesCompleteAsync(Book book, string content, CancellationToken token)
        {
            foreach (string url in ImagesOf(content))
            {
                token.ThrowIfCancellationRequested();
                if (!await AppState.ComicImageCache.ContainsAsync(url, book.Source.Url).ConfigureAwait(false)) return false;
            }
            return true;
        }

        private static async Task<int> CacheImagesAsync(Book book, Chapter chapter, string content, CancellationToken token)
        {
            string headers = book.Source.Header ?? "";
            if (!String.IsNullOrEmpty(chapter.Url) && headers.IndexOf("Referer", StringComparison.OrdinalIgnoreCase) < 0)
                headers += (headers.Length > 0 ? "\n" : "") + "Referer: " + chapter.Url;
            int failed = 0;
            var images = ImagesOf(content);
            // Chapter workers provide the bounded parallelism; do not multiply it by another image pool.
            foreach (string url in images)
            {
                token.ThrowIfCancellationRequested();
                byte[] bytes = await AppState.ComicImageCache.GetOrFetchAsync(url, book.Source.Url,
                    delegate { return HttpFetcher.GetBytesAsync(url, headers, 30); }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (bytes == null || !await AppState.ComicImageCache.ContainsAsync(url, book.Source.Url).ConfigureAwait(false)) failed++;
            }
            if (images.Count > 0) Legado.Js.JscBackend.TraceStage("comic chapter offline cache: " + chapter.DisplayName
                + "; images=" + images.Count + "; failed=" + failed);
            return failed;
        }

        private static void Report(Action<TransferProgress> onProgress, TransferProgress source, object sync)
        {
            if (onProgress == null) return;
            TransferProgress snapshot;
            lock (sync)
            {
                snapshot = new TransferProgress
                {
                    Total = source.Total,
                    Done = source.Done,
                    Failed = source.Failed,
                    Skipped = source.Skipped,
                    Current = source.Current
                };
            }
            try { onProgress(snapshot); }
            catch (Exception) { }
        }
    }
}
