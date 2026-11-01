using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NovReader.Models;

namespace NovReader.Services.Legado
{
    /// <summary>书源广场里的一个合集。</summary>
    public sealed class PlazaCollection : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;
        private string _url;
        private int _count;
        private string _error;

        public string Name
        {
            get { return _name; }
            set { _name = value ?? ""; Raise("Name"); Raise("DisplayName"); Raise("DisplayDetail"); }
        }

        public string Url
        {
            get { return _url; }
            set { _url = value ?? ""; Raise("Url"); Raise("DisplayDetail"); }
        }

        /// <summary>最近一次拉取到的书源数量，-1 表示尚未拉取。</summary>
        public int Count
        {
            get { return _count; }
            set { _count = value; Raise("Count"); Raise("DisplayDetail"); }
        }

        public string Error
        {
            get { return _error; }
            set { _error = value ?? ""; Raise("Error"); Raise("DisplayDetail"); }
        }

        public PlazaCollection()
        {
            _name = ""; _url = ""; _count = -1; _error = "";
        }

        /// <summary>列表主标题：合集文件名 + 是否内置回落。</summary>
        public string DisplayName
        {
            get { return String.IsNullOrEmpty(Name) ? Url : Name; }
        }

        /// <summary>列表副标题：来源域名、已解析数量或错误。</summary>
        public string DisplayDetail
        {
            get
            {
                if (!String.IsNullOrEmpty(Error)) return "拉取失败：" + Error;
                if (Count > 0) return "已解析 " + Count + " 个书源 · 点击导入";
                if (Count == 0) return "没有解析到书源";
                return "点击下载并导入";
            }
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public override string ToString()
        {
            return Count < 0 ? Name : Name + "  (" + Count + " 个书源)";
        }
    }

    /// <summary>
    /// 在线书源广场：从 yuedu.miaogongzi.net 的书源页发现 yuedu:// / legado:// 导入链接，
    /// 下载对应的 Legado 书源合集 JSON，供一键导入。
    /// </summary>
    public static class SourcePlaza
    {
        public const string PlazaRoot = "https://yuedu.miaogongzi.net/";

        private static readonly string[] PlazaPages = { "gx.html", "jh.html" };

        /// <summary>站点不可达时使用的内置合集地址，取自站点 gx.html。</summary>
        private static readonly string[] FallbackUrls =
        {
            "https://legado.miaogongzi.org/阅读书源/sy.json",
            "https://legado.miaogongzi.org/阅读书源/hff.json",
            "https://cdn.mgz.la/shuyuan/guaner25.02.03.txt",
            "https://cdn.miaogongzi.cc/shuyuan/搜索引擎通用.json",
            "https://shuyuan.nyasama.cc/shuyuan/c5dccfb4fea13577819b10e0a3a810ea.json",
            "https://shuyuan.nyasama.cc/shuyuan/f5b15e9641d164937061974cfefb675c.json",
            "https://sy.nyasama.cc/shuyuan/469b68b8c8e7344eb38664d2fb1bdd17.json",
            "https://cdn.mgz.la/shuyuan/250701最后一版一程.json",
            "https://shuyuan-api.yiove.com/import/book-source-collection/eaedccd4-1252-4962-a565-c7a342f95f90"
        };

        /// <summary>最近一次发现过程中站点页面的抓取错误；为空表示在线发现成功。</summary>
        public static string LastDiscoveryError { get; private set; }

        /// <summary>发现书源合集列表；站点不可达时回落到内置地址。</summary>
        public static async Task<List<PlazaCollection>> DiscoverAsync()
        {
            var list = new List<PlazaCollection>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();

            foreach (var page in PlazaPages)
            {
                var fetch = await HttpFetcher.GetAsync(PlazaRoot + page, "");
                if (!fetch.Ok || String.IsNullOrEmpty(fetch.Text))
                {
                    errors.Add(page + " -> " + (String.IsNullOrEmpty(fetch.Error) ? "无内容" : fetch.Error));
                    continue;
                }

                foreach (Match match in Regex.Matches(fetch.Text, "href=\"([^\"]*(?:yuedu|legado)://[^\"]*)\"", RegexOptions.IgnoreCase))
                {
                    string href = match.Groups[1].Value;
                    int index = href.IndexOf("src=", StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;

                    string src = Uri.UnescapeDataString(href.Substring(index + 4)).Trim();
                    if (src.Length == 0 || !seen.Add(src)) continue;
                    list.Add(new PlazaCollection { Name = LabelFor(src), Url = src });
                }
            }

            LastDiscoveryError = String.Join("；", errors.ToArray());

            if (list.Count == 0)
            {
                foreach (var url in FallbackUrls)
                {
                    if (!seen.Add(url)) continue;
                    list.Add(new PlazaCollection { Name = LabelFor(url) + "（内置）", Url = url });
                }
            }
            return list;
        }

        /// <summary>下载并解析一个书源合集。</summary>
        public static async Task<List<BookSource>> FetchCollectionAsync(PlazaCollection collection)
        {
            if (collection == null) return new List<BookSource>();

            var fetch = await HttpFetcher.GetAsync(collection.Url, "");
            collection.Error = fetch.Ok ? "" : fetch.Error;
            if (!fetch.Ok || String.IsNullOrEmpty(fetch.Text)) return new List<BookSource>();

            var sources = LegadoSourceJson.Parse(fetch.Text);
            collection.Count = sources.Count;
            return sources;
        }

        private static string LabelFor(string url)
        {
            try
            {
                var uri = new Uri(url);
                string path = uri.AbsolutePath.TrimEnd('/');
                int slash = path.LastIndexOf('/');
                string file = slash >= 0 ? path.Substring(slash + 1) : path;
                file = Uri.UnescapeDataString(file);
                if (file.Length == 0) return uri.Host;
                return uri.Host + "/" + file;
            }
            catch (Exception)
            {
                return url;
            }
        }
    }
}
