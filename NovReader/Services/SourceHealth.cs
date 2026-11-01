using System;
using System.Collections.Generic;
using System.Globalization;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>搜索时一个书源的表现。</summary>
    public enum SearchOutcome
    {
        /// <summary>有结果。</summary>
        Hit,
        /// <summary>请求成功但这个关键字没结果。书源本身是好的，不算失败。</summary>
        Empty,
        /// <summary>网络错误、超时、HTTP 错误。</summary>
        Failed
    }

    /// <summary>
    /// 书源健康度。上千个导入的书源里大半已经失效，每次搜索都要等它们超时，搜索就会很慢。
    /// 这里记录每个书源最近的表现：有结果、响应快的排在前面先搜，结果最快出来；
    /// 连续失败多次的暂时跳过，过一段时间再自动重试一次（站点可能恢复）。
    /// 不依赖 UWP，持久化格式是 JSON 字符串，由调用方负责读写文件。
    /// </summary>
    public sealed class SourceHealth
    {
        /// <summary>连续失败多少次后暂时跳过。</summary>
        public const int SkipAfterFailures = 3;

        /// <summary>跳过多久后再重试一次。</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(2);

        private sealed class Entry
        {
            public int ConsecutiveFailures;
            public int Hits;
            public DateTime LastFailUtc;
            public DateTime LastHitUtc;
            /// <summary>成功响应的平均耗时（毫秒，指数平均）。</summary>
            public double AvgMs;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        /// <summary>测试用：可替换当前时间。</summary>
        public Func<DateTime> UtcNow = delegate { return DateTime.UtcNow; };

        public void Record(string source, SearchOutcome outcome, double elapsedMs)
        {
            if (String.IsNullOrEmpty(source)) return;
            lock (_gate)
            {
                Entry e;
                if (!_entries.TryGetValue(source, out e)) { e = new Entry(); _entries[source] = e; }

                if (outcome == SearchOutcome.Failed)
                {
                    e.ConsecutiveFailures++;
                    e.LastFailUtc = UtcNow();
                    return;
                }

                e.ConsecutiveFailures = 0;
                e.AvgMs = e.AvgMs <= 0 ? elapsedMs : e.AvgMs * 0.7 + elapsedMs * 0.3;
                if (outcome == SearchOutcome.Hit)
                {
                    e.Hits++;
                    e.LastHitUtc = UtcNow();
                }
            }
        }

        /// <summary>连续失败达到阈值且还没到重试时间的书源本次跳过。</summary>
        public bool ShouldSkip(string source)
        {
            lock (_gate)
            {
                Entry e;
                if (!_entries.TryGetValue(source ?? "", out e)) return false;
                if (e.ConsecutiveFailures < SkipAfterFailures) return false;
                return UtcNow() - e.LastFailUtc < RetryAfter;
            }
        }

        /// <summary>排序分：越小越先搜。常出结果且快的在前，没记录的居中，失败过的在后。</summary>
        private double Rank(string source)
        {
            Entry e;
            if (!_entries.TryGetValue(source ?? "", out e)) return 5000;
            if (e.ConsecutiveFailures > 0) return 20000 + e.ConsecutiveFailures * 1000;
            double rank = e.AvgMs <= 0 ? 5000 : e.AvgMs;
            if (e.Hits > 0) rank -= 3000 + Math.Min(e.Hits, 20) * 50;
            return rank;
        }

        /// <summary>按健康度排好序并去掉需要跳过的书源；skipped 返回被跳过的数量。</summary>
        public List<BookSource> Plan(IList<BookSource> sources, bool skipFailing, out int skipped)
        {
            skipped = 0;
            var kept = new List<KeyValuePair<double, BookSource>>();
            lock (_gate)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    if (skipFailing && ShouldSkip(s.Name)) { skipped++; continue; }
                    // 加上原始位置的小数部分，保证同分时保持原顺序（List.Sort 不稳定）
                    kept.Add(new KeyValuePair<double, BookSource>(Rank(s.Name) + i / 1e6, s));
                }
            }
            kept.Sort(delegate (KeyValuePair<double, BookSource> a, KeyValuePair<double, BookSource> b) { return a.Key.CompareTo(b.Key); });
            var output = new List<BookSource>(kept.Count);
            for (int i = 0; i < kept.Count; i++) output.Add(kept[i].Value);
            return output;
        }

        /// <summary>当前被跳过的书源数（用于界面提示）。</summary>
        public int CountSkipped(IList<BookSource> sources)
        {
            int n = 0;
            for (int i = 0; i < sources.Count; i++) if (ShouldSkip(sources[i].Name)) n++;
            return n;
        }

        public void Reset()
        {
            lock (_gate) _entries.Clear();
        }

        // ---------------------------------------------------------------- 持久化

        public string Serialize()
        {
            var root = JsonValue.NewObject();
            lock (_gate)
            {
                foreach (var pair in _entries)
                {
                    var e = pair.Value;
                    var node = JsonValue.NewObject();
                    node.Add("f", Num(e.ConsecutiveFailures));
                    node.Add("h", Num(e.Hits));
                    node.Add("ms", Num(Math.Round(e.AvgMs)));
                    node.Add("lf", Num(Ticks(e.LastFailUtc)));
                    node.Add("lh", Num(Ticks(e.LastHitUtc)));
                    root.Add(pair.Key, node);
                }
            }
            return root.ToJson();
        }

        public void Load(string json)
        {
            var root = LegadoJson.Parse(json);
            if (root == null || root.Kind != JsonKind.Object || root.Members == null) return;
            lock (_gate)
            {
                _entries.Clear();
                for (int i = 0; i < root.Members.Count; i++)
                {
                    var node = root.Members[i].Value;
                    if (node == null || node.Kind != JsonKind.Object) continue;
                    _entries[root.Members[i].Key] = new Entry
                    {
                        ConsecutiveFailures = (int)NumOf(node.Get("f")),
                        Hits = (int)NumOf(node.Get("h")),
                        AvgMs = NumOf(node.Get("ms")),
                        LastFailUtc = FromTicks(NumOf(node.Get("lf"))),
                        LastHitUtc = FromTicks(NumOf(node.Get("lh")))
                    };
                }
            }
        }

        // 以“分钟”存时间，JSON 数字是 double，直接存 Ticks 会丢精度
        private static double Ticks(DateTime utc) { return utc == default(DateTime) ? 0 : Math.Floor((utc - DateTime.MinValue).TotalMinutes); }
        private static DateTime FromTicks(double minutes) { return minutes <= 0 ? default(DateTime) : DateTime.MinValue.AddMinutes(minutes); }

        private static JsonValue Num(double value) { return new JsonValue { Kind = JsonKind.Number, Number = value }; }

        private static double NumOf(JsonValue value)
        {
            if (value == null) return 0;
            if (value.Kind == JsonKind.Number) return value.Number;
            double parsed;
            return Double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }
    }
}
