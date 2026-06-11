using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Athena.UI;

#if !IOS
class Program
{
    private static string CrashLogPath = null!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 启动早期崩溃日志（Serilog 尚未初始化前的异常兜底）
        CrashLogPath = Path.Combine(AppContext.BaseDirectory, "AthenaData", "Logs", "crash.log");
        Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            WriteCrashLog("Startup", null, "进程启动");
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
