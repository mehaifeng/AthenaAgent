using System;
using System.Threading.Tasks;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// <c>async void</c> 事件处理器的异常护栏。
///
/// async void 的异常不会回到任何调用方：UI 线程上的会走 <c>Dispatcher.UIThread.UnhandledException</c>，
/// 而 App.axaml.cs 里那个处理器只认 Office 预览挂载失败，其余一律落回崩溃路径；
/// 线程池上的（计时器回调、进程退出事件）更直接走 <c>AppDomain.UnhandledException</c> 终止进程。
/// 也就是说：拖进一个读不了的文件、外部改动知识库时 embedding 请求超时、终端进程异常退出，
/// 都足以让整个应用消失。
///
/// 约定：<c>async void</c> 只允许出现在真正的事件处理器上，且必须是一行——
/// 把工作体委托给同名的 <c>...Async</c> 方法并交给本护栏。这样"有没有被保护"
/// 可以一眼看出来，也能被 grep 检查。
/// </summary>
internal static class AsyncEventGuard
{
    /// <summary>
    /// 执行事件处理器的异步工作体，吞掉并记录任何异常。
    /// </summary>
    /// <param name="operation">工作体。第一个 await 之前的代码仍同步执行，
    /// 因此 <c>e.Handled</c> 一类必须同步设置的字段照常生效。</param>
    /// <param name="context">出错时写进日志的处理器名，用 <c>nameof</c> 传。</param>
    public static async void Run(Func<Task> operation, string context)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 取消是正常收尾，不是故障。
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception in async event handler {Handler}", context);
        }
    }
}
