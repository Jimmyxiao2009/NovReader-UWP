namespace NovReader.Views
{
    /// <summary>
    /// 外壳内的页面。外壳页面被缓存，从详情页 / 阅读器返回时内部 Frame 不会重新导航，
    /// 页面靠这个回调刷新书架进度等可能已经变化的数据。
    /// </summary>
    public interface IShellPage
    {
        void OnShellResumed();
    }
}
