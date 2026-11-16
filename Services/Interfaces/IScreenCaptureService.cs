using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 调用操作系统原生的交互式截图工具，由 UI 层导入截图文件或读取剪贴板。
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>当前平台是否具备可用的截图能力。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 启动平台原生截图工具，返回截图文件路径或读取剪贴板所需的状态。
    /// </summary>
    Task<ScreenCaptureResult> LaunchInteractiveAsync(CancellationToken ct = default);
}

public readonly record struct ScreenCaptureResult(ScreenCaptureLaunchResult Status, string? ImagePath = null);

/// <summary>截图工具启动结果。</summary>
public enum ScreenCaptureLaunchResult
{
    /// <summary>当前平台不支持。</summary>
    Unsupported,

    /// <summary>启动失败（命令缺失或异常）。</summary>
    Failed,

    /// <summary>截图交互已结束，可以导入返回的文件或读取剪贴板。</summary>
    CompletedBlocking,

    /// <summary>用户在截图工具中取消（退出码非零）；剪贴板大概率无结果，调用方应立即结束。</summary>
    Cancelled,

    /// <summary>桌面门户结束了截图交互，未提供图片或明确的取消状态。</summary>
    EndedWithoutImage,

    /// <summary>工具已异步启动，完成时机未知（Windows）；需轮询剪贴板等待结果。</summary>
    LaunchedAsync
}
