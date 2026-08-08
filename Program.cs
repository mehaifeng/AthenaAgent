using Avalonia;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI;

#if !IOS
class Program
{
    private static string CrashLogPath = null!;

    // 单实例：保持对命名互斥量的静态引用，防止被 GC 回收（否则其终结器
    // 会释放互斥量，导致运行期间可被多开）。进程退出时操作系统自动释放句柄。
    private static Mutex? _singleInstanceMutex;

    // 命名互斥量名称（含固定 GUID 避免与其他程序冲突）。未加前缀即位于会话本地
    // 命名空间（Windows 的 Local\），故不同登录用户各自允许一个实例。
    private const string SingleInstanceMutexName = "Athena.UI-SingleInstance-9F2A4C7E-3B1D-4E8A-9C6F-A1B2C3D4E5F6";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 注册代码页提供程序：Windows 上的 PowerShell/cmd 按系统 OEM 代码页（中文系统为 GBK/936）
        // 向管道写输出，.NET Core 默认不内置这些代码页，注册后 CliService 才能按它们解码。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // macOS DMG 安装：Playwright 驱动打包在 Contents/Resources/.playwright
        //（放在 Contents/MacOS 下会令 codesign 将其误判为嵌套代码对象导致签名失败），
        // 故将驱动搜索路径指向 Resources。平铺安装（tar.gz/应用内更新）时驱动就在
        // 应用目录下，保持默认搜索即可，且更新后优先使用随包更新的新驱动。
        var baseDirectory = AppContext.BaseDirectory;
        var resourcesDriverPath = Path.Combine(baseDirectory, "..", "Resources", ".playwright");
        if (Directory.Exists(resourcesDriverPath) && !Directory.Exists(Path.Combine(baseDirectory, ".playwright")))
        {
            Environment.SetEnvironmentVariable(
                "PLAYWRIGHT_DRIVER_SEARCH_PATH",
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources")));
        }


        // 启动早期崩溃日志（Serilog 尚未初始化前的异常兜底）
        CrashLogPath = Path.Combine(AppContext.BaseDirectory, "AthenaData", "Logs", "crash.log");
        Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);

        // 单实例检测：通过命名互斥量确保有且仅有一个应用实例运行。
        // createdNew 为 false 说明已有实例持有该互斥量，直接退出本次启动。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            WriteCrashLog("SingleInstance", null, "Another Athena instance is already running; startup was canceled.");
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            WriteCrashLog("Startup", null, "Process started.");
            WriteCrashLog("Startup", null, $"BaseDirectory: {AppContext.BaseDirectory}");
            WriteCrashLog("Startup", null, $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
            WriteCrashLog("Startup", null, $"OS: {Environment.OSVersion}");
            WriteCrashLog("Startup", null, $"64-bit: {Environment.Is64BitProcess}");

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main.Fatal", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS 专用（其他平台忽略此选项）：Avalonia 12 默认优先 Metal 后端，
            // 连续出帧时中间调颜色会产生肉眼可见的频闪（Metal 后端尚不稳定，
            // 参见 AvaloniaUI/Avalonia#14829、#17025），故退回 OpenGL。
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[]
                {
                    AvaloniaNativeRenderingMode.OpenGl,
                    AvaloniaNativeRenderingMode.Software
                }
            })
            .WithInterFont()
            .LogToTrace();

    private static void WriteCrashLog(string source, Exception? ex, string? message = null)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message ?? ex?.Message ?? "(no message)"}";
            if (ex != null)
                line += Environment.NewLine + ex;

            File.AppendAllText(CrashLogPath, line + Environment.NewLine);
        }
        catch
        {
            // 写日志本身不能再抛异常
        }
    }
}
#endif
