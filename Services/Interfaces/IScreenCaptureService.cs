using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 调用操作系统原生的"交互式截图"工具（框选 + 裁剪 + 标注），结果写入系统剪贴板。
/// 设计为不依赖 Avalonia/剪贴板读取，由 UI 层负责从剪贴板取回位图，保持职责清晰。
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>当前平台是否具备可用的截图能力。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 启动平台原生截图工具，截图结果由工具复制到系统剪贴板。
    /// 返回启动结果，告知调用方该工具是否会阻塞到用户完成，从而决定读取剪贴板的策略。
    /// </summary>
    Task<ScreenCaptureLaunchResult> LaunchInteractiveAsync(CancellationToken ct = default);
}

/// <summary>截图工具启动结果。</summary>
public enum ScreenCaptureLaunchResult
{
    /// <summary>当前平台不支持。</summary>
    Unsupported,

    /// <summary>启动失败（命令缺失或异常）。</summary>
    Failed,

    /// <summary>工具已阻塞运行至用户完成（macOS / Linux）；可立即读取剪贴板。</summary>
    CompletedBlocking,

    /// <summary>工具已异步启动，完成时机未知（Windows）；需轮询剪贴板等待结果。</summary>
    LaunchedAsync
}
