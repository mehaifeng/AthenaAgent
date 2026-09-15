using Serilog;
using System;
using System.Collections.Generic;
using System.ClientModel.Primitives;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Context;

/// <summary>
/// 把 OpenAI 兼容端点回的 SSE 里 <c>finish_reason</c> 的未知取值改写成 SDK 认得的形状，
/// 并把原值挪到一个自定义字段上交给传输层判读。
///
/// 为什么必须在 HTTP 管线这一层做：<c>ChatFinishReason</c> 是**闭集枚举**，SDK 生成的
/// <c>ToChatFinishReason</c> 对 stop/length/tool_calls/content_filter/function_call 之外的任何取值
/// 直接抛 <see cref="ArgumentOutOfRangeException"/>，异常从 <c>MoveNextAsync</c> 里飞出来，
/// 调用方连这个 chunk 的存在都看不到（openai-dotnet#340「closed as not planned」，2.13.0 仍是这个行为）。
/// OpenRouter 在上游中途失败时回的正是 <c>"finish_reason": "error"</c>，于是一次普通的上游抖动
/// 在界面上变成一句 SDK 的英文断言，而真正的错误原因（同一 chunk 里的 <c>error</c> 对象）被整条丢掉。
/// </summary>
internal static class ProviderStreamSanitizer
{
    /// <summary>改写后承载原始取值的字段名；传输层按 <c>$.choices[N].&lt;此名&gt;</c> 从 Patch 读回。</summary>
    internal const string RawFinishReasonProperty = "athena_raw_finish_reason";

    private const string DataPrefix = "data:";
    private const int MaxLoggedChunkChars = 2000;

    private static readonly string[] KnownFinishReasons =
        ["stop", "length", "tool_calls", "content_filter", "function_call"];

    /// <summary>
    /// 处理一条 SSE 行。返回 true 表示做了改写，<paramref name="sanitized"/> 是替换后的整行。
    /// 非 data 行、解析不了的行、finish_reason 合法的行一律原样放行。
    /// </summary>
    internal static bool TrySanitizeLine(string line, out string sanitized, out string? rawFinishReason)
    {
        sanitized = line;
        rawFinishReason = null;

        var trimmed = line.TrimEnd('\r', '\n');
        if (!trimmed.StartsWith(DataPrefix, StringComparison.Ordinal)) return false;

        var payload = trimmed[DataPrefix.Length..].TrimStart();
        if (payload.Length == 0 || payload[0] != '{') return false;
        if (!payload.Contains("finish_reason", StringComparison.Ordinal)) return false;
        if (!HasUnknownFinishReason(payload)) return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            // 半截 JSON 不该由这里判死刑——原样放行，让 SDK 按它自己的规则报错。
            Log.Debug(ex, "SSE chunk carrying an unknown finish_reason could not be parsed; passing it through untouched");
            return false;
        }

        if (root is not JsonObject obj || obj["choices"] is not JsonArray choices) return false;

        var rewritten = false;
        foreach (var choice in choices)
        {
            if (choice is not JsonObject choiceObj) continue;
            if (choiceObj["finish_reason"]?.GetValueKind() != JsonValueKind.String) continue;

            var value = choiceObj["finish_reason"]!.GetValue<string>();
            if (IsKnownFinishReason(value)) continue;

            rawFinishReason ??= value;
            choiceObj[RawFinishReasonProperty] = value;
            choiceObj["finish_reason"] = null;
            rewritten = true;
        }

        if (!rewritten) return false;

        // 整条原始 chunk 进日志：上游真正的失败原因（OpenRouter 的顶层 error 对象）只在这里出现过一次。
        Log.Warning(
            "ProviderStreamUnknownFinishReason FinishReason={FinishReason} RawChunk={RawChunk}",
            rawFinishReason,
            payload.Length <= MaxLoggedChunkChars ? payload : payload[..MaxLoggedChunkChars] + "…");

        var leading = line[..(line.Length - line.TrimStart().Length)];
        var trailing = line[trimmed.Length..];
        sanitized = leading + DataPrefix + " " + root.ToJsonString() + trailing;
        return true;
    }

    private static bool IsKnownFinishReason(string value)
        => Array.Exists(KnownFinishReasons, known => string.Equals(known, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 不解析 JSON 的快速判定：绝大多数 chunk 的 finish_reason 是 null 或 stop，
    /// 让它们连一次 JSON 解析都不用付。
    /// </summary>
    private static bool HasUnknownFinishReason(string payload)
    {
        var index = 0;
        while (true)
        {
            index = payload.IndexOf("finish_reason", index, StringComparison.Ordinal);
            if (index < 0) return false;
            index += "finish_reason".Length;

            var cursor = index;
            if (cursor < payload.Length && payload[cursor] == '"') cursor++; // 键名的收尾引号
            while (cursor < payload.Length && char.IsWhiteSpace(payload[cursor])) cursor++;
            if (cursor >= payload.Length || payload[cursor] != ':') continue; // 不是键，是值里出现的同名字串
            cursor++;
            while (cursor < payload.Length && char.IsWhiteSpace(payload[cursor])) cursor++;
            if (cursor >= payload.Length || payload[cursor] != '"') continue; // null / 数字 / 畸形：交给 SDK

            var end = payload.IndexOf('"', cursor + 1);
            if (end < 0) continue;
            if (!IsKnownFinishReason(payload[(cursor + 1)..end])) return true;
        }
    }

    /// <summary>包一层逐行改写；<see cref="Policy"/> 与断言共用这一个入口。</summary>
    internal static Stream Wrap(Stream inner) => new SanitizingSseStream(inner);

    /// <summary>把流式响应体换成会做上述改写的包装流。非 SSE 响应一概不碰。</summary>
    internal sealed class Policy : PipelinePolicy
    {
        internal static Policy Instance { get; } = new();

        private Policy()
        {
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ProcessNext(message, pipeline, currentIndex);
            Wrap(message);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
            Wrap(message);
        }

        private static void Wrap(PipelineMessage message)
        {
            if (message.Response is not { } response) return;
            if (response.ContentStream is not { } content || content is SanitizingSseStream) return;
            if (!response.Headers.TryGetValue("Content-Type", out var contentType)
                || contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            response.ContentStream = new SanitizingSseStream(content);
        }
    }

    /// <summary>
    /// 逐行改写的只读包装流。SSE 的语义单位是行，调用方在拿到整行之前也做不了任何事，
    /// 所以按行缓冲不引入额外延迟；未收全的最后一行在流结束时原样吐出。
    /// </summary>
    private sealed class SanitizingSseStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly byte[] _readBuffer = new byte[8192];
        private readonly List<byte> _pendingLine = [];
        private readonly MemoryStream _ready = new();
        private int _readyOffset;
        private bool _innerEnded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (TryDrain(buffer.AsSpan(offset, count), out var drained)) return drained;
                if (_innerEnded) return 0;

                Absorb(_inner.Read(_readBuffer, 0, _readBuffer.Length));
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (TryDrain(buffer.Span, out var drained)) return drained;
                if (_innerEnded) return 0;

                Absorb(await _inner.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false));
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Absorb(int read)
        {
            if (read <= 0)
            {
                _innerEnded = true;
                if (_pendingLine.Count > 0) EmitLine();
                return;
            }

            var data = _readBuffer.AsSpan(0, read);
            while (!data.IsEmpty)
            {
                var newline = data.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    _pendingLine.AddRange(data);
                    return;
                }

                _pendingLine.AddRange(data[..(newline + 1)]);
                EmitLine();
                data = data[(newline + 1)..];
            }
        }

        private void EmitLine()
        {
            var raw = _pendingLine.ToArray();
            _pendingLine.Clear();

            var line = Encoding.UTF8.GetString(raw);
            var bytes = TrySanitizeLine(line, out var sanitized, out _)
                ? Encoding.UTF8.GetBytes(sanitized)
                : raw;

            // 已被读空的字节没有任何用处，趁机压回开头，免得一次长回复把整段响应留在内存里。
            if (_readyOffset > 0 && _readyOffset == _ready.Length)
            {
                _ready.SetLength(0);
                _readyOffset = 0;
            }

            _ready.Seek(0, SeekOrigin.End);
            _ready.Write(bytes, 0, bytes.Length);
        }

        private bool TryDrain(Span<byte> destination, out int count)
        {
            count = 0;
            var available = (int)(_ready.Length - _readyOffset);
            if (available <= 0 || destination.IsEmpty) return false;

            var take = Math.Min(available, destination.Length);
            _ready.Position = _readyOffset;
            count = _ready.Read(destination[..take]);
            _readyOffset += count;
            return count > 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _ready.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await _ready.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
