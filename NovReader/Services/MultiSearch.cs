using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>聚合搜索的进度快照。</summary>
    public sealed class MultiSearchProgress
    {
        public int Total;
        public int Done;
        public int Succeeded;
        public int Failed;
        /// <summary>因近期连续失败被跳过的书源数（不计入 Total）。</summary>
        public int Skipped;
        public string CurrentSource = "";
        public int ResultCount;
    }

    /// <summary>聚合搜索的可选项。</summary>
    public sealed class MultiSearchOptions
    {
        /// <summary>书源健康度；为 null 时不排序、不跳过、不记录。</summary>
        public SourceHealth Health;
        /// <summary>跳过近期连续失败的书源。</summary>
        public bool SkipFailing = true;
        /// <summary>单个书源搜索请求的超时秒数。</summary>
        public int TimeoutSeconds = MultiSearch.DefaultTimeoutSeconds;
    }

    /// <summary>
    /// 多书源并行聚合搜索。
    /// 关键点：结果边搜边回调（onBatch），不等所有书源跑完，所以界面能立刻看到内容；
    /// N 个工作线程按顺序从队列取书源（健康、快的书源排在前面），每个书源用独立引擎实例，
    /// 单个书源失败或超时不影响其它书源。
    /// 同一本书在不同书源的命中都会回传（同一书源内按书名+作者去重），
    /// 由界面合并成一条“多个书源”的结果，供详情页换源。
    /// </summary>
    public static class MultiSearch
    {
        public const int DefaultConcurrency = 16;
        public const int MaxConcurrency = 32;

        /// <summary>
        /// 搜索请求默认 10 秒超时（目录/正文仍是 20 秒）：能用的书源几乎都在几秒内返回，
        /// 等满 20 秒的基本是挂掉的站点。
        /// </summary>
        public const int DefaultTimeoutSeconds = 10;

        public static Task<int> SearchAsync(
            IList<BookSource> sources,
            string keyword,
            int page,
            int concurrency,
            Action<IList<Book>> onBatch,
            Action<MultiSearchProgress> onProgress,
            CancellationToken token)
        {
            return SearchAsync(sources, keyword, page, concurrency, onBatch, onProgress, token, null);
        }

        /// <summary>
        /// 并行搜索。每拿到一个书源的结果就通过 onBatch 回传；取消后不再回传。
        /// 返回回传的结果总数。
        /// </summary>
        public static async Task<int> SearchAsync(
            IList<BookSource> sources,
            string keyword,
            int page,
            int concurrency,
            Action<IList<Book>> onBatch,
            Action<MultiSearchProgress> onProgress,
            CancellationToken token,
            MultiSearchOptions options)
        {
            if (options == null) options = new MultiSearchOptions();

            var usable = new List<BookSource>();
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s.Enabled && s.IsUsable) usable.Add(s);
            }

            int skipped = 0;
            if (options.Health != null) usable = options.Health.Plan(usable, options.SkipFailing, out skipped);
            if (usable.Count == 0) return 0;

            var progress = new MultiSearchProgress { Total = usable.Count, Skipped = skipped };
            var sync = new object();
            int next = -1;

            Func<Task> worker = async delegate
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= usable.Count || token.IsCancellationRequested) return;
                    await SearchOneAsync(usable[index], keyword, page, onBatch, onProgress, token, options, progress, sync)
                        .ConfigureAwait(false);
                }
            };

            int workers = Math.Min(ClampConcurrency(concurrency), usable.Count);
            var tasks = new List<Task>(workers);
            for (int i = 0; i < workers; i++) tasks.Add(Task.Run(worker));

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 取消或个别任务异常都按“已拿到的结果为准”
            }

            int total;
            lock (sync) total = progress.ResultCount;
            return total;
        }

        private static async Task SearchOneAsync(
            BookSource source, string keyword, int page,
            Action<IList<Book>> onBatch, Action<MultiSearchProgress> onProgress,
            CancellationToken token, MultiSearchOptions options,
            MultiSearchProgress progress, object sync)
        {
            IList<Book> fresh = null;
            var watch = Stopwatch.StartNew();
            try
            {
                var engine = new LegadoEngine { SearchTimeoutSeconds = options.TimeoutSeconds };
                var books = await engine.SearchAsync(source, keyword, page).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                if (books != null && books.Count > 0)
                {
                    fresh = new List<Book>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (int k = 0; k < books.Count; k++)
                    {
                        var book = books[k];
                        if (book == null || String.IsNullOrEmpty(book.Name)) continue;
                        if (book.Source == null) book.Source = source;
                        if (!seen.Add(SearchRanking.BookKey(book.Name, book.Author))) continue;
                        fresh.Add(book);
                    }
                    if (fresh.Count == 0) fresh = null;
                }

                // 只有连不上、超时、5xx 才算书源坏了；有些站点“没搜到”会回 404，不能因此被跳过
                var fetch = engine.LastFetch;
                bool broken = !String.IsNullOrEmpty(engine.LastError) &&
                              (fetch == null || fetch.StatusCode == 0 || fetch.StatusCode >= 500);
                SearchOutcome outcome = fresh != null ? SearchOutcome.Hit
                    : (broken ? SearchOutcome.Failed : SearchOutcome.Empty);
                if (options.Health != null) options.Health.Record(source.Name, outcome, watch.Elapsed.TotalMilliseconds);

                lock (sync)
                {
                    if (fresh != null) { progress.Succeeded++; progress.ResultCount += fresh.Count; }
                    if (outcome == SearchOutcome.Failed) progress.Failed++;
                }
            }
            catch (Exception)
            {
                // 单个书源异常（超时、站点挂了、反爬）不影响其它书源
                if (options.Health != null && !token.IsCancellationRequested)
                    options.Health.Record(source.Name, SearchOutcome.Failed, watch.Elapsed.TotalMilliseconds);
                lock (sync) { progress.Failed++; }
            }
            finally
            {
                lock (sync)
                {
                    progress.Done++;
                    progress.CurrentSource = source.Name;
                }

                // 先回传结果，再回传进度，让界面尽快出现内容
                if (fresh != null && onBatch != null && !token.IsCancellationRequested)
                {
                    try { onBatch(fresh); } catch (Exception) { }
                }
                Report(onProgress, progress, sync);
            }
        }

        /// <summary>
        /// 等搜索结束，或在取消时立即返回（不等仍在进行的 HTTP 请求超时）。
        /// 取消后迟到的结果不会再回调（见 SearchAsync），所以提前返回是安全的。返回结果总数，提前返回时为 -1。
        /// </summary>
        public static async Task<int> WhenDoneOrCancelled(Task<int> search, CancellationToken token)
        {
            var cancelled = Task.Delay(Timeout.Infinite, token);
            var first = await Task.WhenAny(search, cancelled).ConfigureAwait(true);
            if (first == search) return await search.ConfigureAwait(true);
            // 让后台任务的异常有人观察，避免 UnobservedTaskException
            var ignored = search.ContinueWith(delegate (Task<int> t) { var e = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return -1;
        }

        public static int ClampConcurrency(int value)
        {
            if (value < 1) return DefaultConcurrency;
            if (value > MaxConcurrency) return MaxConcurrency;
            return value;
        }

        private static void Report(Action<MultiSearchProgress> onProgress, MultiSearchProgress source, object sync)
        {
            if (onProgress == null) return;
            MultiSearchProgress snapshot;
            lock (sync)
            {
                snapshot = new MultiSearchProgress
                {
                    Total = source.Total,
                    Done = source.Done,
                    Succeeded = source.Succeeded,
                    Failed = source.Failed,
                    Skipped = source.Skipped,
                    CurrentSource = source.CurrentSource,
                    ResultCount = source.ResultCount
                };
            }
            try { onProgress(snapshot); } catch (Exception) { }
        }
    }
}
