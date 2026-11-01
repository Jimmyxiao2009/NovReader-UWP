using System;
using System.Text;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using NovReader.Services;

namespace NovReader.Views
{
    /// <summary>调试页：最近一次请求详情、规则警告、操作日志与运行环境。</summary>
    public sealed partial class DebugPage : Page
    {
        public DebugPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Refresh(); }

        private void Refresh()
        {
            RefreshFetch();
            RefreshWarnings();
            RefreshLog();
            RefreshEnvironment();
        }

        private void RefreshFetch()
        {
            var engine = AppState.LastUsedEngine ?? AppState.Engine;
            var fetch = engine.LastFetch;

            var sb = new StringBuilder();
            if (fetch == null)
            {
                sb.Append("还没有请求记录。先到「搜索」页搜一本书，或到「书源」页测试导入。");
                BodyText.Text = "";
            }
            else
            {
                sb.Append("请求地址：").Append(fetch.Url).Append('\n');
                if (!String.Equals(fetch.Url, fetch.FinalUrl, StringComparison.Ordinal))
                    sb.Append("最终地址：").Append(fetch.FinalUrl).Append('\n');
                sb.Append("状态码  ：").Append(fetch.StatusCode).Append('\n');
                sb.Append("编码    ：").Append(fetch.EncodingName).Append('\n');
                sb.Append("字节数  ：").Append(fetch.ByteLength).Append('\n');
                if (!String.IsNullOrEmpty(engine.LastError))
                    sb.Append("错误    ：").Append(engine.LastError).Append('\n');
                if (!String.IsNullOrEmpty(fetch.Error))
                    sb.Append("HTTP 错误：").Append(fetch.Error).Append('\n');

                string body = fetch.Text ?? "";
                if (body.Length > 4000) body = body.Substring(0, 4000) + "…";
                BodyText.Text = body;
            }
            FetchInfoText.Text = sb.ToString();
        }

        private void RefreshWarnings()
        {
            var warnings = AppState.Engine.LastWarnings;
            if (warnings == null || warnings.Count == 0)
            {
                WarningsText.Text = "没有警告。";
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < warnings.Count; i++) sb.Append("- ").Append(warnings[i]).Append('\n');
            WarningsText.Text = sb.ToString();
        }

        private void RefreshLog()
        {
            LogText.Text = AppState.DebugLogText;
            string native = Services.Legado.Js.JscBackend.ReadDiagnosticLog();
            if (!String.IsNullOrEmpty(native)) LogText.Text += "\n\nJS / 漫画加载日志（包括上次退出前的记录）：\n" + native;
        }

        private void RefreshEnvironment()
        {
            var sb = new StringBuilder();
            sb.Append("应用版本：").Append(GetVersion()).Append('\n');
            sb.Append("书源数量：").Append(AppState.Sources.Count).Append('\n');
            sb.Append("书架数量：").Append(AppState.Shelf.Count).Append('\n');
            sb.Append("缓存章节：").Append(AppState.ChapterCache.MemoryCount).Append('\n');
            sb.Append("缓存目录：").Append(AppState.TocCache.MemoryCount).Append(" 本").Append('\n');
            sb.Append("阅读设置：字号 ").Append((int)AppState.Settings.FontSize)
              .Append("，行距 ").Append(AppState.Settings.LineHeight.ToString("0.0"))
              .Append("，主题 ").Append(AppState.Settings.ThemeName).Append('\n');
            sb.Append("请求超时：").Append(Services.Legado.HttpFetcher.TimeoutSeconds).Append(" 秒").Append('\n');
            sb.Append('\n');
            sb.Append("说明：JS 规则使用 JavaScriptCore 原生引擎；加载失败会记录在操作日志中。").Append('\n');
            sb.Append("如果响应原文是一段 JS 跳转代码或“请开启 JavaScript”提示，").Append('\n');
            sb.Append("说明该站点需要浏览器环境，书源脚本引擎无法直接完成浏览器挑战。").Append('\n');
            EnvText.Text = sb.ToString();
        }

        private static string GetVersion()
        {
            try
            {
                var version = Package.Current.Id.Version;
                return version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        private async void ReloadSources_Click(object sender, RoutedEventArgs e)
        {
            var saved = await AppState.SourceStore.LoadAsync();
            AppState.Sources.Clear();
            foreach (var s in saved) AppState.Sources.Add(s);
            AppState.Log("重载书源 " + AppState.Sources.Count + " 个");
            Refresh();

            var shell = MainPage.Current;
            if (shell != null) shell.RefreshHeader();
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            AppState.DebugLog.Clear();
            Refresh();
        }
    }
}
