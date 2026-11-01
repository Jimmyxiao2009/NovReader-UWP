using System;

namespace NovReader.Models
{
    /// <summary>目录里的一章。</summary>
    public sealed class Chapter
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public int Index { get; set; }
        public bool IsVip { get; set; }

        public Chapter() { Name = ""; Url = ""; }

        /// <summary>用于列表显示的标题，缺标题时退回“第 N 章”。</summary>
        public string DisplayName
        {
            get
            {
                return String.IsNullOrEmpty(Name) ? ("第 " + (Index + 1) + " 章") : Name;
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
