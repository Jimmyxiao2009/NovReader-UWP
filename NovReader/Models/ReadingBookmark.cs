using System;

namespace NovReader.Models
{
    public sealed class ReadingBookmark
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string BookUrl { get; set; } = "";
        public string ChapterUrl { get; set; } = "";
        public string ChapterName { get; set; } = "";
        public int ChapterIndex { get; set; }
        public double ScrollRatio { get; set; }
        public string Note { get; set; } = "";
        public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        public string DisplayText { get { return ChapterName + " · " + Math.Round(ScrollRatio * 100) + "%"; } }
        public string Detail { get { return String.IsNullOrEmpty(Note) ? CreatedAt : Note + "\n" + CreatedAt; } }
    }
}
