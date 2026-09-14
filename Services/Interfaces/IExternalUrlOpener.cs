namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 把一个 URL 交给系统默认浏览器。抽出来是为了让依赖它的流程可以被断言——
/// 测试不该真的弹出一个浏览器窗口。
/// </summary>
public interface IExternalUrlOpener
{
    /// <summary>尝试打开。返回 false 表示没能启动浏览器，调用方应给出手动打开链接的退路，而不是直接失败。</summary>
    bool TryOpen(string url);
}
