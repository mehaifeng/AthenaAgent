using Athena.UI.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Diagnostics;
using System.Reflection;

internal static class GtkWebViewFocusBridgeTests
{
    internal static void Run()
    {
        // The control stays unattached: the test exercises the real adapter events and Avalonia
        // focus subscriptions without starting a platform WebView or a GTK thread.
        var view = new NativeWebView();
        var window = new Window();
        var gtk = new FakeGtkThread();
        var activationBaseline = Subscribers(window, "Activated");
        var deactivationBaseline = Subscribers(window, "Deactivated");
        var bridge = GtkWebViewFocusBridge.Attach(view,
            new GtkWebViewFocusBridge.Operations(gtk.Schedule, gtk.Hook, gtk.SetFocus), () => window);
        var watch = GtkWebProcessWatch.Attach(view, _ => { },
            new GtkWebProcessWatch.Operations(gtk.Schedule, gtk.Watch));

        try
        {
            // Both the focus bridge and process watcher must ignore non-GTK adapters.
            RaiseAdapter(view, "AdapterCreated", new OtherHandle());
            Require(gtk.Pending == 0, "非 GTK 适配器不应排入 GTK 回调。");

            var first = gtk.NewHandle(0x100);
            RaiseAdapter(view, "AdapterCreated", first);
            Require(gtk.Pending == 2, "GTK 适配器创建后应分别安装焦点桥和进程观察器。");
            gtk.RunNext();
            gtk.RunNext();
            PumpUntil(() => bridge.HookTask.IsCompleted, "焦点桥没有完成安装。");
            Require(gtk.Hooks.SequenceEqual(new[] { first.WebKitWebView })
                    && gtk.Watches.SequenceEqual(new[] { first.WebKitWebView }),
                "活着的 WebKit 必须同时接上焦点和进程观察。");
            Require(Subscribers(window, "Activated") == activationBaseline + 1
                    && Subscribers(window, "Deactivated") == deactivationBaseline + 1,
                "活着的页面必须订阅窗口焦点变化。");

            RaiseWindow(window, "HandleDeactivated");
            gtk.RunNext();
            Require(gtk.FocusCalls.Last() == (first.WebKitWebView, false), "窗口失活时应释放 GTK 焦点。");
            RaiseWindow(window, "HandleActivated");
            Require(gtk.Pending == 0, "页面没有焦点时，窗口激活不能抢走输入法。");
            SetIsFocused(view, true);
            RaiseWindow(window, "HandleActivated");
            gtk.RunNext();
            Require(gtk.FocusCalls.Last() == (first.WebKitWebView, true), "页面持有焦点时，窗口激活应恢复 GTK 焦点。");
            view.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
            gtk.RunNext();
            Require(gtk.FocusCalls.Last() == (first.WebKitWebView, false), "页面失焦时应释放 GTK 焦点。");

            // The reported crash: the page disappears, the main window stays alive, and a
            // focus operation that was queued before destruction runs only afterwards.
            RaiseWindow(window, "HandleDeactivated");
            Require(gtk.Pending == 1, "应有一个尚未执行的 GTK 焦点操作。");
            var focusCount = gtk.FocusCalls.Count;
            RaiseAdapter(view, "AdapterDestroyed", first);
            gtk.Destroy(first);
            Require(Subscribers(window, "Activated") == activationBaseline
                    && Subscribers(window, "Deactivated") == deactivationBaseline,
                "适配器销毁时必须立刻解除窗口焦点订阅。");
            RaiseWindow(window, "HandleDeactivated");
            RaiseWindow(window, "HandleActivated");
            view.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
            Require(gtk.Pending == 1, "销毁后的窗口和页面焦点事件不能再排入操作。");
            gtk.RunNext();
            Require(gtk.FocusCalls.Count == focusCount && gtk.DeadTouches == 0,
                "销毁前排队的焦点操作不得访问已释放的 WebKit。");

            // An adapter can also disappear before either initial GTK hook has run.
            var early = gtk.NewHandle(0x200);
            RaiseAdapter(view, "AdapterCreated", early);
            var earlyHook = bridge.HookTask;
            Require(gtk.Pending == 2, "新适配器必须重新安排两个 GTK 安装操作。");
            RaiseAdapter(view, "AdapterDestroyed", early);
            gtk.Destroy(early);
            gtk.RunNext();
            gtk.RunNext();
            PumpUntil(() => earlyHook.IsCompleted, "销毁前排队的安装没有结束。");
            Require(!gtk.Hooks.Contains(early.WebKitWebView) && !gtk.Watches.Contains(early.WebKitWebView)
                    && gtk.DeadTouches == 0,
                "适配器销毁后，焦点和进程观察器的延迟安装都不得访问旧指针。");

            // GTK installation finished but its UI continuation has not run yet.
            var late = gtk.NewHandle(0x300);
            RaiseAdapter(view, "AdapterCreated", late);
            var lateHook = bridge.HookTask;
            gtk.RunNext(); // focus hook completes; do not pump the UI dispatcher yet
            RaiseAdapter(view, "AdapterDestroyed", late);
            gtk.Destroy(late);
            gtk.RunNext(); // delayed process watcher
            PumpUntil(() => lateHook.IsCompleted, "焦点桥的延迟 UI 续延没有结束。");
            Require(gtk.Hooks.Contains(late.WebKitWebView) && !gtk.Watches.Contains(late.WebKitWebView)
                    && Subscribers(window, "Activated") == activationBaseline
                    && Subscribers(window, "Deactivated") == deactivationBaseline,
                "GTK 安装后、UI 续延前销毁，不得重新挂回窗口事件。");

            // Reattachment needs a fresh lifetime; an old notification cannot cancel it.
            var replacement = gtk.NewHandle(0x400);
            RaiseAdapter(view, "AdapterCreated", replacement);
            gtk.RunNext();
            gtk.RunNext();
            PumpUntil(() => bridge.HookTask.IsCompleted, "重挂后的焦点桥没有完成安装。");
            RaiseAdapter(view, "AdapterDestroyed", late);
            Require(Subscribers(window, "Deactivated") == deactivationBaseline + 1,
                "旧适配器的迟到销毁通知不能撤销新适配器的订阅。");
            SetIsFocused(view, true);
            RaiseWindow(window, "HandleActivated");
            gtk.RunNext();
            Require(gtk.FocusCalls.Last() == (replacement.WebKitWebView, true) && gtk.DeadTouches == 0,
                "重挂后必须只操作新 WebKit，不能触碰已销毁的指针。");
            RaiseAdapter(view, "AdapterDestroyed", replacement);
            gtk.Destroy(replacement);
            Require(Subscribers(window, "Activated") == activationBaseline
                    && Subscribers(window, "Deactivated") == deactivationBaseline,
                "重挂页面销毁后也必须解绑窗口事件。");

            GC.KeepAlive(watch); // production's page holds the watcher state for its lifetime
            Console.WriteLine("[PASS] GTK WebView focus and process-watch adapter lifetimes");
        }
        finally
        {
            window.Close();
        }
    }

    private static void RaiseAdapter(NativeWebView view, string eventName, IPlatformHandle handle)
    {
        var field = typeof(NativeWebView).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"NativeWebView 的 {eventName} 事件字段不存在。");
        var handler = field.GetValue(view) as EventHandler<WebViewAdapterEventArgs>
            ?? throw new InvalidOperationException($"NativeWebView 没有订阅 {eventName}。");
        handler(view, new WebViewAdapterEventArgs(handle));
    }

    private static int Subscribers(Window window, string eventName)
    {
        var field = typeof(WindowBase).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"WindowBase 的 {eventName} 事件字段不存在。");
        return (field.GetValue(window) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void RaiseWindow(Window window, string method)
        => (typeof(WindowBase).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"WindowBase 的 {method} 方法不存在。"))
            .Invoke(window, null);

    private static void SetIsFocused(NativeWebView view, bool focused)
        => (typeof(InputElement).GetProperty(nameof(InputElement.IsFocused))?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("InputElement.IsFocused 的 setter 不存在。"))
            .Invoke(view, new object[] { focused });

    private static void PumpUntil(Func<bool> done, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(message);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class OtherHandle : IPlatformHandle
    {
        public nint Handle => 1;
        public string HandleDescriptor => "other";
    }

    private sealed class GtkHandle(nint webKit) : IGtkWebViewPlatformHandle
    {
        public nint Handle => webKit;
        public string HandleDescriptor => "XID";
        public nint WebKitWebView => webKit;
    }

    private sealed class FakeGtkThread
    {
        private readonly Queue<(Func<bool> Action, TaskCompletionSource<bool> Completion)> _pending = new();
        private readonly HashSet<nint> _live = new();

        internal int Pending => _pending.Count;
        internal int DeadTouches { get; private set; }
        internal List<nint> Hooks { get; } = new();
        internal List<nint> Watches { get; } = new();
        internal List<(nint Handle, bool Focused)> FocusCalls { get; } = new();

        internal GtkHandle NewHandle(nint value)
        {
            _live.Add(value);
            return new GtkHandle(value);
        }

        internal void Destroy(GtkHandle handle) => _live.Remove(handle.WebKitWebView);

        internal Task<bool> Schedule(Func<bool> action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion));
            return completion.Task;
        }

        internal void RunNext()
        {
            if (!_pending.TryDequeue(out var item)) throw new InvalidOperationException("没有待执行的 GTK 回调。");
            try { item.Completion.SetResult(item.Action()); }
            catch (Exception ex) { item.Completion.SetException(ex); }
        }

        internal bool Hook(nint webKit)
        {
            CheckAlive(webKit);
            Hooks.Add(webKit);
            return true;
        }

        internal void Watch(nint webKit, object state)
        {
            CheckAlive(webKit);
            Watches.Add(webKit);
        }

        internal void SetFocus(nint webKit, bool focused)
        {
            CheckAlive(webKit);
            FocusCalls.Add((webKit, focused));
        }

        private void CheckAlive(nint webKit)
        {
            if (_live.Contains(webKit)) return;
            DeadTouches++;
            throw new InvalidOperationException("原生回调访问了已释放的 WebKit。");
        }
    }
}
