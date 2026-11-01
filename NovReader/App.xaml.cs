using System;
using System.Text;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using NovReader.Services;
using NovReader.Services.Legado;

namespace NovReader
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // UWP 默认只有 UTF-8/UTF-16，GBK/Big5 站点必须注册代码页提供程序，否则正文和章节名全是乱码。
            // 注册放在回调里、首次解码时才执行并由 HttpFetcher 吞掉异常，启动阶段不会因此失败。
            HttpFetcher.EncodingBootstrap = RegisterCodePages;

            UnhandledException += OnUnhandledException;
            NovReader.Services.Legado.Js.JscBackend.DiagnosticPathProvider = delegate
            {
                return System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "js-native.log");
            };
            HttpFetcher.BinaryDiagnostics = NovReader.Services.Legado.Js.JscBackend.TraceStage;
            NativeImageFetcher.CertificatePathProvider = delegate
            {
                return System.IO.Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Assets", "cacert.pem");
            };
            HttpFetcher.BinaryFallback = NativeImageFetcher.FetchAsync;

            // JS 引擎：手机上有 JsBridge.dll（JavaScriptCore）就用原生后端；
            // x64 调试机没有这个 DLL，JS 规则按“不可用”处理（测试台里用 Jint 验证）。
            NovReader.Services.Legado.Js.LegadoJs.BackendFactory = delegate
            {
                return NovReader.Services.Legado.Js.JscBackend.IsAvailable
                    ? new NovReader.Services.Legado.Js.JscBackend()
                    : null;
            };
        }

        private static void RegisterCodePages()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                rootFrame.Navigated += OnRootNavigated;
                Window.Current.Content = rootFrame;
                Nav.Frame = rootFrame;

                SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage), e.Arguments, new SuppressNavigationTransitionInfo());
            }

            Window.Current.Activate();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Dialogs.TryClose()) { e.Handled = true; return; }
            var frame = Nav.Frame;
            if (frame == null) return;
            var reader = frame.Content as Views.ReaderPage;
            if (reader != null && reader.HandleBack()) { e.Handled = true; return; }
            if (frame.CanGoBack)
            {
                e.Handled = true;
                frame.GoBack();
                return;
            }

            // 外壳内：先收起汉堡菜单 / 回到书架，都不需要时才让系统退出应用
            var shell = MainPage.Current;
            if (shell != null && frame.Content == shell && shell.HandleBack()) e.Handled = true;
        }

        /// <summary>手机用硬件返回键；桌面 / 平板模式在标题栏显示返回按钮。</summary>
        private void OnRootNavigated(object sender, NavigationEventArgs e)
        {
            var frame = sender as Frame;
            try
            {
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    frame != null && frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            }
            catch (Exception) { }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            AppState.Log("导航失败：" + e.SourcePageType.Name + " -> " + e.Exception.Message);
            throw new Exception("无法加载页面 " + e.SourcePageType.FullName, e.Exception);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppState.Log("未处理异常：" + HttpFetcher.Describe(e.Exception));
            // 不在这里设 Handled，避免掩盖真正的崩溃
        }
    }
}
