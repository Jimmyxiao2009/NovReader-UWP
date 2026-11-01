using System;
using System.Text;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>
    /// 搜索结果的归并与排序规则（不依赖 UWP，测试台可直接编译）。
    /// 同一本书在不同书源里写法常有出入（空格、全角符号、“作者：”前缀），
    /// 归一化后再比较，才能把它们合并成一条“多个书源”的结果。
    /// </summary>
    public static class SearchRanking
    {
        public static string Normalize(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (Char.IsWhiteSpace(c)) continue;
                if (c == '《' || c == '》' || c == '【' || c == '】' || c == '[' || c == ']' || c == '"' || c == '“' || c == '”') continue;
                // 全角字母数字转半角
                if (c >= '\uFF01' && c <= '\uFF5E') c = (char)(c - 0xFEE0);
                sb.Append(Char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static string NormalizeAuthor(string author)
        {
            string a = Normalize(LegadoEngine.CleanAuthor(author));
            if (a.EndsWith("著", StringComparison.Ordinal) && a.Length > 1) a = a.Substring(0, a.Length - 1);
            if (a.EndsWith("/", StringComparison.Ordinal) && a.Length > 1) a = a.Substring(0, a.Length - 1);
            return a;
        }

        /// <summary>合并用的键：归一化后的书名 + 作者。</summary>
        public static string BookKey(string name, string author)
        {
            return Normalize(name) + "|" + NormalizeAuthor(author);
        }

        /// <summary>相关度：书名完全一致 &gt; 书名以关键字开头 &gt; 书名包含 &gt; 作者匹配 &gt; 其它。</summary>
        public static int Score(string name, string author, string keyword)
        {
            string k = Normalize(keyword);
            if (k.Length == 0) return 0;
            string n = Normalize(name);
            string a = NormalizeAuthor(author);

            int score = 0;
            if (n == k) score = 1000;
            else if (n.StartsWith(k, StringComparison.Ordinal)) score = 800;
            else if (n.IndexOf(k, StringComparison.Ordinal) >= 0) score = 600;
            else if (a == k) score = 500;
            else if (a.Length > 0 && a.IndexOf(k, StringComparison.Ordinal) >= 0) score = 400;
            else if (k.IndexOf(n, StringComparison.Ordinal) >= 0 && n.Length >= 2) score = 300;

            // 同档内书名越短越接近关键字（“斗破苍穹” 排在 “斗破苍穹之无上之境” 前面）
            if (score > 0) score -= Math.Min(99, Math.Max(0, n.Length - k.Length));
            return score;
        }

        /// <summary>精准搜索：只保留书名或作者包含关键字的结果。</summary>
        public static bool IsRelevant(string name, string author, string keyword)
        {
            string k = Normalize(keyword);
            if (k.Length == 0) return true;
            if (Normalize(name).IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            string a = NormalizeAuthor(author);
            return a.Length > 0 && a.IndexOf(k, StringComparison.Ordinal) >= 0;
        }
    }
}
