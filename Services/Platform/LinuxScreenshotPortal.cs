using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Serilog;
using Tmds.DBus.Protocol;

namespace Athena.UI.Services.Platform;

internal static class LinuxScreenshotPortal
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string DesktopPath = "/org/freedesktop/portal/desktop";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    // null 表示门户未能启动，可尝试本地截图命令。
    internal static async Task<ScreenCaptureResult?> TryCaptureAsync(ILogger logger, CancellationToken ct)
    {
        if (DBusAddress.Session is not { } address)
        {
            return null;
        }

        using var connection = new DBusConnection(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;

        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(token);
            var sender = connection.UniqueName![1..].Replace('.', '_');
            var handleToken = $"athena_{Guid.NewGuid():N}";
            var requestPath = $"{DesktopPath}/request/{sender}/{handleToken}";
            var response = new TaskCompletionSource<(uint Code, string? Uri)>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Response 可能紧随方法返回；先按 handle_token 推导路径并订阅信号。
            using var subscription = await connection.WatchSignalAsync(
                Service, requestPath, RequestInterface, "Response", ReadResponse,
                notification =>
                {
                    if (notification.HasValue)
                        response.TrySetResult(notification.Value);
                    else if (notification.Exception is { } error)
                        response.TrySetException(error);
                },
                flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed,
                emitOnCapturedContext: false).AsTask().WaitAsync(token);

            var actualPath = await RequestScreenshotAsync(connection, handleToken).WaitAsync(token);
            if (actualPath != requestPath)
            {
                throw new InvalidOperationException("Unexpected screenshot portal request handle.");
            }

            var (code, uri) = await response.Task.WaitAsync(token);
            if (code == 0 && Uri.TryCreate(uri, UriKind.Absolute, out var image) &&
                image.IsFile && image.Host.Length == 0 && File.Exists(image.LocalPath))
            {
                return new(ScreenCaptureLaunchResult.CompletedBlocking, image.LocalPath);
            }

            if (code == 1)
            {
                return new(ScreenCaptureLaunchResult.Cancelled);
            }

            // GNOME 同样会在按 Esc 关闭截图浮层时给出 2。
            if (code == 2)
            {
                logger.Debug("Screenshot portal ended without an image (response {Code})", code);
                return new(ScreenCaptureLaunchResult.EndedWithoutImage);
            }

            logger.Warning("Screenshot portal did not return a usable image (response {Code})", code);
            return new(ScreenCaptureLaunchResult.Failed);
        }
        catch (Exception ex) when (ex is DBusConnectFailedException ||
            ex is DBusErrorReplyException { ErrorName: "org.freedesktop.DBus.Error.UnknownMethod" or
                "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown" })
        {
            logger.Debug(ex, "Desktop screenshot portal unavailable");
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.Warning("Timed out waiting for desktop screenshot portal");
            return new(ScreenCaptureLaunchResult.Failed);
        }
    }

    private static Task<string> RequestScreenshotAsync(DBusConnection connection, string handleToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, DesktopPath, "org.freedesktop.portal.Screenshot", "Screenshot", "sa{sv}");
        writer.WriteString(""); // 截图可以在 Athena 窗口最小化时运行。
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = handleToken,
            ["interactive"] = true,
        });
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadObjectPathAsString());
    }

    private static (uint Code, string? Uri) ReadResponse(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var code = reader.ReadUInt32();
        var results = reader.ReadDictionaryOfStringToVariantValue();
        return (code, results.TryGetValue("uri", out var uri) && uri.Type == VariantValueType.String ? uri.GetString() : null);
    }
}
