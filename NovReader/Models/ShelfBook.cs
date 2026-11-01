using System;

namespace NovReader.Models
{
    /// <summary>
    /// 书架条目。只保存“重新打开这本书需要的最小信息”，
    /// 书源本体按名字在本地书源表里回查，避免书源更新后书架还留着旧规则。
    /// </summary>
    public sealed class ShelfBook
    {
        /// <summary>书籍详情页地址，作为书架的唯一定位键。</summary>
        public string BookUrl { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string CoverUrl { get; set; }
        public string Intro { get; set; }
        /// <summary>目录页地址，省去每次重新解析详情页。</summary>
        public string TocUrl { get; set; }
        public string SourceName { get; set; }

        public int ChapterIndex { get; set; }
        public string ChapterName { get; set; }
        /// <summary>章节内的滚动位置，0~1。</summary>
        public double ScrollRatio { get; set; }
        public int ChapterCount { get; set; }

        public DateTime AddedTime { get; set; }
        public DateTime LastReadTime { get; set; }

        public ShelfBook()
        {
            BookUrl = ""; Name = ""; Author = ""; CoverUrl = ""; Intro = "";
            TocUrl = ""; SourceName = ""; ChapterName = "";
            AddedTime = DateTime.Now; LastReadTime = DateTime.Now;
        }

        public string DisplayAuthor
        {
            get { return String.IsNullOrEmpty(Author) ? "未知作者" : Author; }
        }

        public string DisplayCover
        {
            get { return String.IsNullOrEmpty(CoverUrl) ? null : CoverUrl; }
        }

        /// <summary>没有封面图时显示的书名首字。</summary>
        public string CoverPlaceholder
        {
            get
            {
                return FirstLetter(Name);
            }
        }

        /// <summary>书架副标题：章节进度 + 最近阅读时间。</summary>
        public string ProgressText
        {
            get
            {
                string chapter = "";
                if (ChapterCount > 0)
                    chapter = "读至 " + (ChapterIndex + 1) + "/" + ChapterCount + " 章";
                else if (!String.IsNullOrEmpty(ChapterName))
                    chapter = "读至 " + ChapterName;
                else
                    chapter = "尚未开始";

                if (LastReadTime <= AddedTime) return chapter;
                return chapter + " · " + RelativeTime(LastReadTime);
            }
        }

        public double ProgressRatio
        {
            get
            {
                if (ChapterCount <= 0) return 0;
                return Math.Min(1.0, (ChapterIndex + ScrollRatio) / ChapterCount);
            }
        }

        public string ProgressPercent
        {
            get { return ((int)Math.Round(ProgressRatio * 100)) + "%"; }
        }

        /// <summary>封面占位字：跳过 “[科幻]” “《” 之类的前导符号。</summary>
        public static string FirstLetter(string name)
        {
            if (!String.IsNullOrEmpty(name))
                for (int i = 0; i < name.Length; i++)
                    if (Char.IsLetterOrDigit(name[i])) return name.Substring(i, 1);
            return "书";
        }

        private static string RelativeTime(DateTime time)
        {
            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return ((int)span.TotalMinutes) + " 分钟前";
            if (span.TotalHours < 24) return ((int)span.TotalHours) + " 小时前";
            if (span.TotalDays < 30) return ((int)span.TotalDays) + " 天前";
            return time.ToString("yyyy-MM-dd");
        }

        public override string ToString() { return Name; }
    }
}
