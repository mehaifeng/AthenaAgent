using System;
using System.Threading;

namespace Athena.UI.Services.SubAgents;

/// <summary>
/// 工具执行的环境上下文（AsyncLocal）。主对话循环在执行工具前写入主取消令牌，
/// 长耗时工具（dispatch_subagents、run_browser_task 等）可读取它以响应"停止"。
/// 通过 AsyncLocal 在 await 链中向下游传播，避免给每个工具签名都加 CancellationToken。
/// </summary>
public static class ToolExecutionContext
{
    private static readonly AsyncLocal<CancellationToken> _token = new();

    /// <summary>当前工具执行可用的取消令牌；未设置时为 CancellationToken.None。</summary>
    public static CancellationToken CurrentCancellationToken => _token.Value;

    /// <summary>进入一个工具执行作用域，设置取消令牌；Dispose 时恢复先前值。</summary>
    public static IDisposable Enter(CancellationToken token)
    {
        var previous = _token.Value;
        _token.Value = token;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CancellationToken _previous;
        private bool _disposed;

        public Scope(CancellationToken previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _token.Value = _previous;
        }
    }
}
