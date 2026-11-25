using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

// The Cua Driver v1 ABI returns owned buffers and completes each accepted invocation once.
internal sealed class CuaDriverNative : IAsyncDisposable
{
    private const int Cancelled = 5;
    private const int MaxReplyBytes = 24 * 1024 * 1024;
    private static readonly Completion Complete = OnCompleted;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly FreeBuffer _freeBuffer;
    private readonly Destroy _destroy;
    private readonly Invoke _invoke;
    private readonly Cancel _cancel;
    private readonly ReleaseOperation _release;
    private nint _handle;

    public static string LibraryPath => Environment.GetEnvironmentVariable("ATHENA_CUA_DRIVER_LIBRARY")
        ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "cua_driver_sdk.dll"
            : OperatingSystem.IsMacOS() ? "libcua_driver_sdk.dylib" : "libcua_driver_sdk.so");

    public CuaDriverNative()
    {
        var path = Path.GetFullPath(LibraryPath);
        var library = NativeLibrary.Load(path);
        try
        {
            var create = Export<Create>(library, "cua_driver_create_v1");
            _freeBuffer = Export<FreeBuffer>(library, "cua_driver_buffer_free_v1");
            _destroy = Export<Destroy>(library, "cua_driver_destroy_v1");
            _invoke = Export<Invoke>(library, "cua_driver_invoke_v1");
            _cancel = Export<Cancel>(library, "cua_driver_operation_cancel_v1");
            _release = Export<ReleaseOperation>(library, "cua_driver_operation_release_v1");

            var status = create(0, 0, out _handle, out var error);
            try
            {
                if (status != 0 || _handle == 0)
                    throw new InvalidOperationException($"Cua Driver initialization failed ({status}): {Read(error)}");
            }
            finally
            {
                _freeBuffer(ref error);
            }
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            var name = Encoding.UTF8.GetBytes(tool);
            var input = JsonSerializer.SerializeToUtf8Bytes(arguments);
            var pending = new PendingCall(_freeBuffer);
            var context = GCHandle.Alloc(pending);
            nint operation = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = _invoke(_handle, name, (nuint)name.Length, input, (nuint)input.Length,
                    Complete, GCHandle.ToIntPtr(context), out operation, out var error);
                try
                {
                    if (status != 0)
                        throw new InvalidOperationException($"Cua Driver refused {tool} ({status}): {Read(error)}");
                }
                finally
                {
                    _freeBuffer(ref error);
                }

                using var registration = cancellationToken.Register(() => _cancel(operation));
                var reply = await pending.Result.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return reply;
            }
            finally
            {
                if (operation != 0)
                    _release(ref operation);
                context.Free();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void OnCompleted(nint context, int status, Buffer reply, Buffer error)
    {
        var pending = (PendingCall)GCHandle.FromIntPtr(context).Target!;
        JsonElement result = default;
        Exception? failure = null;
        try
        {
            if (status == 0)
            {
                using var json = JsonDocument.Parse(Read(reply));
                result = json.RootElement.Clone();
            }
            else if (status != Cancelled)
            {
                failure = new InvalidOperationException($"Cua Driver failed ({status}): {Read(error)}");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            pending.Free(ref reply);
            pending.Free(ref error);
        }
        if (status == Cancelled)
            pending.Result.TrySetCanceled();
        else if (failure is not null)
            pending.Result.TrySetException(failure);
        else
            pending.Result.TrySetResult(result);
    }

    private static string Read(Buffer buffer)
    {
        if (buffer.Length > MaxReplyBytes)
            throw new InvalidOperationException("Cua Driver returned an oversized response.");
        return buffer.Data == 0 ? string.Empty : Marshal.PtrToStringUTF8(buffer.Data, (int)buffer.Length) ?? string.Empty;
    }

    private static T Export<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_handle != 0)
                _destroy(ref _handle);
            // Keep the library mapped for the lifetime of the process. Its native executors own threads.
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record PendingCall(FreeBuffer Free)
    {
        public TaskCompletionSource<JsonElement> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer
    {
        public nint Data;
        public nuint Length;
        public nuint Capacity;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Create(nint options, nuint length, out nint handle, out Buffer error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Destroy(ref nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBuffer(ref Buffer buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Completion(nint context, int status, Buffer reply, Buffer error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Invoke(nint handle, byte[] name, nuint nameLength, byte[] input, nuint inputLength,
        Completion completed, nint context, out nint operation, out Buffer error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Cancel(nint operation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseOperation(ref nint operation);
}
