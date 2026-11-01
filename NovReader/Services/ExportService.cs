using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>
    /// 多格式、多编码导出。
    /// 正文来源优先用本地缓存；缺的章节按设定的线程数并发补齐。
    /// 支持 TXT / HTML / Markdown / EPUB，编码支持 UTF-8、UTF-8 BOM、UTF-16LE、GBK、Big5。
    /// </summary>
    public static class ExportService
    {
        private const string FolderName = "exports";

        public sealed class ExportRequest
        {
            public Book Book;
            public IList<Chapter> Chapters;
            public ExportFormat Format = ExportFormat.Txt;
            public ExportEncoding Encoding = ExportEncoding.Utf8;
            public int Threads = 4;
            /// <summary>是否包含书名/作者等头部信息。</summary>
            public bool IncludeHeader = true;
        }

        /// <summary>导出并返回生成的文件路径。失败时抛异常。</summary>
        public static async Task<string> ExportAsync(
            ExportRequest request,
            Action<TransferProgress> onProgress,
            CancellationToken token)
        {
            if (request == null || request.Book == null) throw new ArgumentException("导出请求不完整");
            if (request.Chapters == null || request.Chapters.Count == 0) throw new InvalidOperationException("没有可导出的章节");

            // 1) 先把正文全部拿到本地（多线程）
            var texts = await CollectContentsAsync(request, onProgress, token).ConfigureAwait(false);
            if (texts.Count == 0) throw new InvalidOperationException("没有取到任何正文，无法导出");

            // 2) 按格式生成内容
            string safeName = SafeFileName(request.Book.Name, "book");
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);

            if (request.Format == ExportFormat.Epub)
            {
                return await WriteEpubAsync(folder, safeName, request, texts).ConfigureAwait(false);
            }

            string text = request.Format == ExportFormat.Html
                ? BuildHtml(request, texts)
                : request.Format == ExportFormat.Markdown
                    ? BuildMarkdown(request, texts)
                    : BuildTxt(request, texts);

            string fileName = safeName + TransferSettings.ExtensionOf(request.Format);
            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            var encoding = ResolveEncoding(request.Encoding);
            // FileIO.WriteBytesAsync 返回 IAsyncAction，没有 ConfigureAwait
            await FileIO.WriteBytesAsync(file, encoding.GetBytes(text));

            AppState.Log("导出《" + request.Book.Name + "》为 " + TransferSettings.FormatName(request.Format)
                         + " / " + TransferSettings.EncodingName(request.Encoding) + "，共 "
                         + texts.Count + " 章");
            return file.Path;
        }

        /// <summary>把文件复制到用户选定的位置。</summary>
        public static async Task<string> SaveAsAsync(string sourcePath, StorageFile target)
        {
            if (target == null || String.IsNullOrEmpty(sourcePath)) return "";
            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            await source.CopyAndReplaceAsync(target);
            return target.Path;
        }

        // ================================================================ 取正文

        private sealed class ChapterText
        {
            public int Index;
            public string Title;
            public string Body;
        }

        private static async Task<List<ChapterText>> CollectContentsAsync(
            ExportRequest request,
            Action<TransferProgress> onProgress,
            CancellationToken token)
        {
            var results = new ChapterText[request.Chapters.Count];
            var gate = new SemaphoreSlim(DownloadService.ClampThreads(request.Threads));
            var sync = new object();
            var progress = new TransferProgress { Total = request.Chapters.Count };
            var tasks = new List<Task>();

            for (int i = 0; i < request.Chapters.Count; i++)
            {
                int index = i;
                var chapter = request.Chapters[i];
                tasks.Add(Task.Run(async delegate
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        string body = null;

                        if (!String.IsNullOrEmpty(chapter.Url))
                        {
                            body = await AppState.ChapterCache.GetAsync(chapter.Url).ConfigureAwait(false);
                            if (body == null)
                            {
                                var engine = new LegadoEngine();
                                string nextUrl = index + 1 < request.Chapters.Count ? request.Chapters[index + 1].Url : null;
                                body = await engine.GetContentAsync(request.Book, chapter, nextUrl).ConfigureAwait(false);
                                if (!String.IsNullOrEmpty(body))
                                    await AppState.ChapterCache.PutAsync(chapter.Url, body).ConfigureAwait(false);
                            }
                        }

                        if (String.IsNullOrEmpty(body))
                        {
                            lock (sync) { progress.Failed++; }
                        }
                        else
                        {
                            // 正文清理：即使缓存里是旧数据，导出文件也必须是纯文本
                            body = LegadoEngine.SanitizeContent(body);
                            results[index] = new ChapterText
                            {
                                Index = index,
                                Title = chapter.DisplayName,
                                Body = body
                            };
                            lock (sync) { progress.Done++; }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (sync) { progress.Failed++; }
                    }
                    catch (Exception)
                    {
                        lock (sync) { progress.Failed++; }
                    }
                    finally
                    {
                        lock (sync) { progress.Current = chapter.DisplayName; }
                        gate.Release();
                        if (onProgress != null)
                        {
                            TransferProgress snapshot;
                            lock (sync)
                            {
                                snapshot = new TransferProgress
                                {
                                    Total = progress.Total,
                                    Done = progress.Done,
                                    Failed = progress.Failed,
                                    Skipped = progress.Skipped,
                                    Current = progress.Current
                                };
                            }
                            try { onProgress(snapshot); } catch (Exception) { }
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var list = new List<ChapterText>();
            for (int i = 0; i < results.Length; i++)
                if (results[i] != null) list.Add(results[i]);
            return list;
        }

        // ================================================================ 各格式

        private static string BuildTxt(ExportRequest request, List<ChapterText> texts)
        {
            var sb = new StringBuilder();
            if (request.IncludeHeader)
            {
                sb.Append(request.Book.Name).Append('\n');
                if (!String.IsNullOrEmpty(request.Book.Author)) sb.Append("作者：").Append(request.Book.Author).Append('\n');
                sb.Append('\n');
            }

            for (int i = 0; i < texts.Count; i++)
            {
                sb.Append('\n').Append(texts[i].Title).Append('\n').Append('\n');
                sb.Append(NormalizeBody(texts[i].Body)).Append('\n');
            }
            return sb.ToString();
        }

        private static string BuildMarkdown(ExportRequest request, List<ChapterText> texts)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(request.Book.Name).Append('\n');
            if (!String.IsNullOrEmpty(request.Book.Author)) sb.Append('\n').Append("作者：").Append(request.Book.Author).Append('\n');
            if (!String.IsNullOrEmpty(request.Book.Intro))
                sb.Append('\n').Append("> ").Append(request.Book.Intro.Replace("\n", " ")).Append('\n');

            for (int i = 0; i < texts.Count; i++)
            {
                sb.Append('\n').Append("## ").Append(texts[i].Title).Append('\n').Append('\n');
                foreach (var line in SplitLines(NormalizeBody(texts[i].Body)))
                {
                    if (line.Length == 0) continue;
                    sb.Append("　　").Append(line).Append('\n').Append('\n');
                }
            }
            return sb.ToString();
        }

        private static string BuildHtml(ExportRequest request, List<ChapterText> texts)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\"/>\n");
            sb.Append("<title>").Append(Escape(request.Book.Name)).Append("</title>\n");
            sb.Append("<style>\n");
            sb.Append("body{font-family:\"Microsoft YaHei\",sans-serif;line-height:1.9;max-width:44em;margin:0 auto;padding:24px;}\n");
            sb.Append("h1{font-size:1.6em;} h2{font-size:1.2em;margin-top:2em;padding-bottom:.3em;border-bottom:1px solid #ddd;}\n");
            sb.Append("p{text-indent:2em;margin:.6em 0;} .meta{color:#666;font-size:.9em;}\n");
            sb.Append("</style>\n</head>\n<body>\n");

            sb.Append("<h1>").Append(Escape(request.Book.Name)).Append("</h1>\n");
            if (!String.IsNullOrEmpty(request.Book.Author))
                sb.Append("<p class=\"meta\">作者：").Append(Escape(request.Book.Author)).Append("</p>\n");
            if (!String.IsNullOrEmpty(request.Book.Intro))
                sb.Append("<p class=\"meta\">").Append(Escape(request.Book.Intro)).Append("</p>\n");

            for (int i = 0; i < texts.Count; i++)
            {
                sb.Append("<h2>").Append(Escape(texts[i].Title)).Append("</h2>\n");
                foreach (var line in SplitLines(NormalizeBody(texts[i].Body)))
                {
                    if (line.Length == 0) continue;
                    sb.Append("<p>").Append(Escape(line)).Append("</p>\n");
                }
            }
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        // ================================================================ EPUB

        private static async Task<string> WriteEpubAsync(
            StorageFolder folder, string safeName, ExportRequest request, List<ChapterText> texts)
        {
            string fileName = safeName + ".epub";
            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            string bookId = "urn:uuid:" + Guid.NewGuid().ToString();
            var buffer = new MemoryStream();

            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                // mimetype 必须是第一个条目且不压缩
                var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var w = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                    w.Write("application/epub+zip");

                AddText(zip, "META-INF/container.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
                    "  <rootfiles>\n" +
                    "    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n" +
                    "  </rootfiles>\n</container>\n");

                AddText(zip, "OEBPS/style.css",
                    "body{font-family:\"Microsoft YaHei\",sans-serif;line-height:1.8;margin:0 1em;}\n" +
                    "h1{font-size:1.4em;} h2{font-size:1.1em;margin-top:1.6em;}\n" +
                    "p{text-indent:2em;margin:.5em 0;}\n");

                var manifest = new StringBuilder();
                var spine = new StringBuilder();
                var navPoints = new StringBuilder();

                for (int i = 0; i < texts.Count; i++)
                {
                    string id = "ch" + i.ToString("D4");
                    string href = id + ".xhtml";
                    manifest.Append("    <item id=\"").Append(id).Append("\" href=\"").Append(href)
                            .Append("\" media-type=\"application/xhtml+xml\"/>\n");
                    spine.Append("    <itemref idref=\"").Append(id).Append("\"/>\n");
                    navPoints.Append("    <navPoint id=\"nav").Append(id).Append("\" playOrder=\"")
                             .Append(i + 1).Append("\">\n")
                             .Append("      <navLabel><text>").Append(Escape(texts[i].Title)).Append("</text></navLabel>\n")
                             .Append("      <content src=\"").Append(href).Append("\"/>\n")
                             .Append("    </navPoint>\n");

                    AddText(zip, "OEBPS/" + href, BuildXhtml(texts[i]));
                }

                AddText(zip, "OEBPS/toc.ncx",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n" +
                    "  <head>\n    <meta name=\"dtb:uid\" content=\"" + bookId + "\"/>\n  </head>\n" +
                    "  <docTitle><text>" + Escape(request.Book.Name) + "</text></docTitle>\n" +
                    "  <navMap>\n" + navPoints + "  </navMap>\n</ncx>\n");

                AddText(zip, "OEBPS/nav.xhtml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                    "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">\n" +
                    "<head><title>目录</title></head>\n<body>\n<nav epub:type=\"toc\" id=\"toc\"><ol>\n" +
                    BuildNavList(texts) +
                    "</ol></nav>\n</body>\n</html>\n");

                var opf = new StringBuilder();
                opf.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
                opf.Append("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"bookid\">\n");
                opf.Append("  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">\n");
                opf.Append("    <dc:title>").Append(Escape(request.Book.Name)).Append("</dc:title>\n");
                opf.Append("    <dc:language>zh-CN</dc:language>\n");
                opf.Append("    <dc:identifier id=\"bookid\">").Append(bookId).Append("</dc:identifier>\n");
                if (!String.IsNullOrEmpty(request.Book.Author))
                    opf.Append("    <dc:creator>").Append(Escape(request.Book.Author)).Append("</dc:creator>\n");
                if (!String.IsNullOrEmpty(request.Book.Intro))
                    opf.Append("    <dc:description>").Append(Escape(request.Book.Intro)).Append("</dc:description>\n");
                opf.Append("  </metadata>\n  <manifest>\n");
                opf.Append("    <item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>\n");
                opf.Append("    <item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
                opf.Append("    <item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>\n");
                opf.Append(manifest);
                opf.Append("  </manifest>\n  <spine toc=\"ncx\">\n");
                opf.Append(spine);
                opf.Append("  </spine>\n</package>\n");
                AddText(zip, "OEBPS/content.opf", opf.ToString());
            }

            buffer.Position = 0;
            await FileIO.WriteBytesAsync(file, buffer.ToArray());
            buffer.Dispose();

            AppState.Log("导出《" + request.Book.Name + "》为 EPUB，共 " + texts.Count + " 章");
            return file.Path;
        }

        private static string BuildXhtml(ChapterText text)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\">\n<head>\n");
            sb.Append("<title>").Append(Escape(text.Title)).Append("</title>\n");
            sb.Append("<link rel=\"stylesheet\" type=\"text/css\" href=\"style.css\"/>\n</head>\n<body>\n");
            sb.Append("<h2>").Append(Escape(text.Title)).Append("</h2>\n");
            foreach (var line in SplitLines(NormalizeBody(text.Body)))
            {
                if (line.Length == 0) continue;
                sb.Append("<p>").Append(Escape(line)).Append("</p>\n");
            }
            sb.Append("</body>\n</html>\n");
            return sb.ToString();
        }

        private static string BuildNavList(List<ChapterText> texts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < texts.Count; i++)
            {
                sb.Append("<li><a href=\"ch").Append(i.ToString("D4")).Append(".xhtml\">")
                  .Append(Escape(texts[i].Title)).Append("</a></li>\n");
            }
            return sb.ToString();
        }

        private static void AddText(ZipArchive zip, string path, string content)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        // ================================================================ 工具

        private static Encoding ResolveEncoding(ExportEncoding encoding)
        {
            switch (encoding)
            {
                case ExportEncoding.Utf8Bom: return new UTF8Encoding(true);
                case ExportEncoding.Utf16Le: return new UnicodeEncoding(false, true);
                case ExportEncoding.Gbk: return HttpFetcher.GetEncoding("gbk");
                case ExportEncoding.Big5: return HttpFetcher.GetEncoding("big5");
                default: return new UTF8Encoding(false);
            }
        }

        private static string NormalizeBody(string body)
        {
            if (String.IsNullOrEmpty(body)) return "";
            string text = body.Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder();
            foreach (var line in SplitLines(text))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(trimmed);
            }
            return sb.ToString();
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? "").Split('\n');
        }

        private static string Escape(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;");
        }

        private static string SafeFileName(string name, string fallback)
        {
            if (String.IsNullOrEmpty(name)) return fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool bad = false;
                for (int k = 0; k < invalid.Length; k++)
                    if (invalid[k] == c) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }
            string result = sb.ToString().Trim();
            if (result.Length > 80) result = result.Substring(0, 80);
            return result.Length == 0 ? fallback : result;
        }

        /// <summary>导出目录，供界面显示“打开文件夹”提示。</summary>
        public static async Task<string> GetExportFolderPathAsync()
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
            return folder.Path;
        }
    }
}
