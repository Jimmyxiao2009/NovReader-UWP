using System;
using System.Collections.Generic;

namespace NovReader.Services
{
    public static class ChapterTextSearch
    {
        public const int MaxResults = 500;
        public static List<int> Find(string text, string keyword)
        {
            var result = new List<int>();
            if (String.IsNullOrEmpty(text) || String.IsNullOrWhiteSpace(keyword)) return result;
            int offset = 0;
            while (offset <= text.Length - keyword.Length && result.Count < MaxResults)
            {
                int index = text.IndexOf(keyword, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                result.Add(index); offset = index + keyword.Length;
            }
            return result;
        }
        public static string Preview(string text, int offset, int length)
        {
            if (String.IsNullOrEmpty(text)) return "";
            offset = Math.Max(0, Math.Min(text.Length, offset));
            int start = Math.Max(0, offset - 16), end = Math.Min(text.Length, offset + Math.Max(1, length) + 32);
            return (start > 0 ? "…" : "") + text.Substring(start, end - start).Replace('\r', ' ').Replace('\n', ' ')
                + (end < text.Length ? "…" : "");
        }
    }
}
