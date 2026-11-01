using System;
using System.Collections.Generic;
using NovReader.Models;

namespace NovReader.Services.Legado
{
    /// <summary>Legado 书源 JSON 的导入导出，字段命名对齐阅读 App 的 bookSource 结构。</summary>
    public static class LegadoSourceJson
    {
        /// <summary>解析单个书源对象。不是对象时返回 null。</summary>
        public static BookSource ParseOne(JsonValue value)
        {
            if (value == null || value.Kind != JsonKind.Object) return null;

            var source = new BookSource();
            source.Name = Str(value, "bookSourceName");
            source.Url = Str(value, "bookSourceUrl");
            source.Group = Str(value, "bookSourceGroup");
            source.Comment = Str(value, "bookSourceComment");
            source.Header = Str(value, "header");
            source.SearchUrl = Str(value, "searchUrl");
            source.ExploreUrl = Str(value, "exploreUrl");

            var type = value.Get("bookSourceType");
            if (type != null && type.Kind == JsonKind.Number) source.Type = (int)type.Number;

            var enabled = value.Get("enabled");
            if (enabled != null && enabled.Kind == JsonKind.Boolean) source.Enabled = enabled.Bool;

            // 兼容 enabled 以字符串 "true"/"false" 出现的情况
            if (enabled != null && enabled.Kind == JsonKind.String)
                source.Enabled = !String.Equals(enabled.Str, "false", StringComparison.OrdinalIgnoreCase);

            source.Search = ParseSearch(value.Get("ruleSearch"));
            source.BookInfo = ParseBookInfo(value.Get("ruleBookInfo"));
            source.Toc = ParseToc(value.Get("ruleToc"));
            source.Content = ParseContent(value.Get("ruleContent"));

            if (String.IsNullOrEmpty(source.Name)) source.Name = source.Url;
            return source;
        }

        /// <summary>解析导入文本：既支持 Legado 的书源数组，也支持单个书源对象。</summary>
        public static List<BookSource> Parse(string json)
        {
            var list = new List<BookSource>();
            var root = LegadoJson.ParseLoose(json);
            if (root == null) return list;

            if (root.Kind == JsonKind.Array)
            {
                for (int i = 0; i < root.Items.Count; i++)
                {
                    var source = ParseOne(root.Items[i]);
                    if (source != null) list.Add(source);
                }
            }
            else if (root.Kind == JsonKind.Object)
            {
                // 可能整体是单个书源
                var source = ParseOne(root);
                if (source != null) list.Add(source);
            }
            return list;
        }

        public static string Serialize(IList<BookSource> sources)
        {
            var array = JsonValue.NewArray();
            for (int i = 0; i < sources.Count; i++) array.Items.Add(ToJson(sources[i]));
            return array.ToJson();
        }

        public static JsonValue ToJson(BookSource source)
        {
            var obj = JsonValue.NewObject();
            obj.Add("bookSourceName", Text(source.Name));
            obj.Add("bookSourceUrl", Text(source.Url));
            obj.Add("bookSourceGroup", Text(source.Group));
            obj.Add("bookSourceComment", Text(source.Comment));
            obj.Add("bookSourceType", new JsonValue { Kind = JsonKind.Number, Number = source.Type });
            obj.Add("enabled", new JsonValue { Kind = JsonKind.Boolean, Bool = source.Enabled });
            obj.Add("header", Text(source.Header));
            obj.Add("searchUrl", Text(source.SearchUrl));
            obj.Add("exploreUrl", Text(source.ExploreUrl));

            obj.Add("ruleSearch", ToJson(source.Search));
            obj.Add("ruleBookInfo", ToJson(source.BookInfo));
            obj.Add("ruleToc", ToJson(source.Toc));
            obj.Add("ruleContent", ToJson(source.Content));
            return obj;
        }

        private static JsonValue ToJson(SearchRule rule)
        {
            var obj = JsonValue.NewObject();
            obj.Add("bookList", Text(rule.BookList));
            obj.Add("name", Text(rule.Name));
            obj.Add("author", Text(rule.Author));
            obj.Add("kind", Text(rule.Kind));
            obj.Add("intro", Text(rule.Intro));
            obj.Add("lastChapter", Text(rule.LastChapter));
            obj.Add("coverUrl", Text(rule.CoverUrl));
            obj.Add("bookUrl", Text(rule.BookUrl));
            return obj;
        }

        private static JsonValue ToJson(BookInfoRule rule)
        {
            var obj = JsonValue.NewObject();
            obj.Add("init", Text(rule.Init));
            obj.Add("name", Text(rule.Name));
            obj.Add("author", Text(rule.Author));
            obj.Add("kind", Text(rule.Kind));
            obj.Add("lastChapter", Text(rule.LastChapter));
            obj.Add("intro", Text(rule.Intro));
            obj.Add("coverUrl", Text(rule.CoverUrl));
            obj.Add("wordCount", Text(rule.WordCount));
            obj.Add("tocUrl", Text(rule.TocUrl));
            return obj;
        }

        private static JsonValue ToJson(TocRule rule)
        {
            var obj = JsonValue.NewObject();
            obj.Add("chapterList", Text(rule.ChapterList));
            obj.Add("chapterName", Text(rule.ChapterName));
            obj.Add("chapterUrl", Text(rule.ChapterUrl));
            obj.Add("isVip", Text(rule.IsVip));
            obj.Add("updateTime", Text(rule.UpdateTime));
            obj.Add("nextTocUrl", Text(rule.NextTocUrl));
            return obj;
        }

        private static JsonValue ToJson(ContentRule rule)
        {
            var obj = JsonValue.NewObject();
            obj.Add("content", Text(rule.Content));
            obj.Add("nextContentUrl", Text(rule.NextContentUrl));
            return obj;
        }

        private static SearchRule ParseSearch(JsonValue value)
        {
            var rule = new SearchRule();
            if (value == null || value.Kind != JsonKind.Object) return rule;
            rule.BookList = Str(value, "bookList");
            rule.Name = Str(value, "name");
            rule.Author = Str(value, "author");
            rule.Kind = Str(value, "kind");
            rule.Intro = Str(value, "intro");
            rule.LastChapter = Str(value, "lastChapter");
            rule.CoverUrl = Str(value, "coverUrl");
            rule.BookUrl = Str(value, "bookUrl");
            return rule;
        }

        private static BookInfoRule ParseBookInfo(JsonValue value)
        {
            var rule = new BookInfoRule();
            if (value == null || value.Kind != JsonKind.Object) return rule;
            rule.Init = Str(value, "init");
            rule.Name = Str(value, "name");
            rule.Author = Str(value, "author");
            rule.Kind = Str(value, "kind");
            rule.LastChapter = Str(value, "lastChapter");
            rule.Intro = Str(value, "intro");
            rule.CoverUrl = Str(value, "coverUrl");
            rule.WordCount = Str(value, "wordCount");
            rule.TocUrl = Str(value, "tocUrl");
            return rule;
        }

        private static TocRule ParseToc(JsonValue value)
        {
            var rule = new TocRule();
            if (value == null || value.Kind != JsonKind.Object) return rule;
            rule.ChapterList = Str(value, "chapterList");
            rule.ChapterName = Str(value, "chapterName");
            rule.ChapterUrl = Str(value, "chapterUrl");
            rule.IsVip = Str(value, "isVip");
            rule.UpdateTime = Str(value, "updateTime");
            rule.NextTocUrl = Str(value, "nextTocUrl");
            return rule;
        }

        private static ContentRule ParseContent(JsonValue value)
        {
            var rule = new ContentRule();
            if (value == null || value.Kind != JsonKind.Object) return rule;
            rule.Content = Str(value, "content");
            rule.NextContentUrl = Str(value, "nextContentUrl");
            return rule;
        }

        private static string Str(JsonValue obj, string name)
        {
            var value = obj.Get(name);
            return value == null ? "" : value.AsString();
        }

        private static JsonValue Text(string value)
        {
            return new JsonValue { Kind = JsonKind.String, Str = value ?? "" };
        }
    }
}
