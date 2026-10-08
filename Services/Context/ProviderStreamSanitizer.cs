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
/// 把 OpenAI 兼容端点回的 SSE 改写成 SDK 读得下去的形状。
///
/// 为什么必须在 HTTP 管线这一层做：SDK 的 Chat 流式反序列化里有三个**闭集枚举**字段——
/// <c>choices[].finish_reason</c>、<c>choices[].delta.role</c>、<c>choices[].delta.tool_calls[].type</c>
/// （反编译 OpenAI 2.12.0 逐个分支核对过，就这三个）。生成的 <c>ToChatFinishReason</c> /
/// <c>ToChatMessageRole</c> / <c>ToChatToolCallKind</c> 遇到 null 跳过，遇到集合之外的任何字符串——
/// 包括空串——直接抛 <see cref="ArgumentOutOfRangeException"/>，异常从 <c>MoveNextAsync</c> 里飞出来，
/// 调用方连这个 chunk 的存在都看不到（openai-dotnet#340「closed as not planned」，2.13.0 仍是这个行为）。
///
/// 两种改写，含义不同：
/// <list type="bullet">
/// <item>三个字段的空白串一律读成「没有这个值」，改成 null，不记 Warning。MiniMax 官方端点（2026-10-08，
/// MiniMax-M3）的一部分后端把没有值的字段写成零值字符串而不是省略：每个中间 chunk 都带 <c>"finish_reason":""</c>，
/// 工具调用从第二片起是 <c>{"id":"","type":"","function":{"name":"",…}}</c>。<c>type:""</c> 让每一轮工具调用
/// 都死在第二片上，气泡却说「上游中途中断，回复继续即可」。同一片里的 id / name 空串 SDK 读得下去，
/// 累积器本来就不拿空值覆盖第一片的（见 <c>OpenAIChatService.ProcessStreamAsync</c>）。</item>
/// <item>非空的未知 <c>finish_reason</c> 是上游在宣告结局：OpenRouter 在上游中途失败时回的正是
/// <c>"finish_reason": "error"</c>，真正的原因在同一 chunk 的 <c>error</c> 对象里。原值挪到
/// <see cref="RawFinishReasonProperty"/> 交给传输层判读，整条 chunk 进一次 Warning。</item>
/// </list>
/// role / type 的非空未知值不在这里改：它们不是「没有值」的另一种写法，猜不出含义，交给传输层的兜底报成格式不兼容。
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
    /// 处理一条 SSE 行。返回 true 表示做了改写，<paramref name="sanitized"/> 是替换后的整行；
    /// <paramref name="rawFinishReason"/> 只报非空的未知取值，空白串不算（它不是上游在宣告什么）。
    /// 非 data 行、解析不了的行、取值都合法的行一律原样放行。
    /// </summary>
    internal static bool TrySanitizeLine(string line, out string sanitized, out string? rawFinishReason)
        => TrySanitizeLine(line, out sanitized, out rawFinishReason, out _);

    /// <param name="blankFields">这一行里被读成「没有值」改成 null 的字段，供包装流按响应去重记日志。</param>
    internal static bool TrySanitizeLine(
        string line,
        out string sanitized,
        out string? rawFinishReason,
        out IReadOnlyList<string> blankFields)
    {
        sanitized = line;
        rawFinishReason = null;
        blankFields = [];

        var trimmed = line.TrimEnd('\r', '\n');
        if (!trimmed.StartsWith(DataPrefix, StringComparison.Ordinal)) return false;

        var payload = trimmed[DataPrefix.Length..].TrimStart();
        if (payload.Length == 0 || payload[0] != '{') return false;
        if (!MayNeedRewrite(payload)) return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            // 半截 JSON 不该由这里判死刑——原样放行，让 SDK 按它自己的规则报错。
            Log.Debug(ex, "SSE chunk that looked rewritable could not be parsed; passing it through untouched");
            return false;
        }

        if (root is not JsonObject obj || obj["choices"] is not JsonArray choices) return false;

        List<string>? blanked = null;
        foreach (var choice in choices)
        {
            if (choice is not JsonObject choiceObj) continue;

            if (StringValue(choiceObj, "finish_reason") is { } finishReason)
            {
                if (string.IsNullOrWhiteSpace(finishReason))
                {
                    choiceObj["finish_reason"] = null;
                    (blanked ??= []).Add("finish_reason");
                }
                else if (!IsKnownFinishReason(finishReason))
                {
                    rawFinishReason ??= finishReason;
                    choiceObj[RawFinishReasonProperty] = finishReason;
                    choiceObj["finish_reason"] = null;
                }
            }

            if (choiceObj["delta"] is not JsonObject delta) continue;
            if (NullIfBlank(delta, "role")) (blanked ??= []).Add("delta.role");
            if (delta["tool_calls"] is not JsonArray toolCalls) continue;
            foreach (var toolCall in toolCalls)
            {
                if (toolCall is JsonObject call && NullIfBlank(call, "type")) (blanked ??= []).Add("delta.tool_calls[].type");
            }
        }

        if (rawFinishReason == null && blanked == null) return false;
        blankFields = blanked ?? [];

        if (rawFinishReason != null)
        {
            // 整条原始 chunk 进日志：上游真正的失败原因（OpenRouter 的顶层 error 对象）只在这里出现过一次。
            Log.Warning(
                "ProviderStreamUnknownFinishReason FinishReason={FinishReason} RawChunk={RawChunk}",
                rawFinishReason,
                payload.Length <= MaxLoggedChunkChars ? payload : payload[..MaxLoggedChunkChars] + "…");
        }

        var leading = line[..(line.Length - line.TrimStart().Length)];
        var trailing = line[trimmed.Length..];
        sanitized = leading + DataPrefix + " " + root.ToJsonString() + trailing;
        return true;
    }

    private static bool IsKnownFinishReason(string value)
        => Array.Exists(KnownFinishReasons, known => string.Equals(known, value, StringComparison.OrdinalIgnoreCase));

    private static string? StringValue(JsonObject owner, string key)
        => owner[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>空白串改成 null——SDK 对 null 的处理正是「没有这个值」。返回是否改了。</summary>
    private static bool NullIfBlank(JsonObject owner, string key)
    {
        if (StringValue(owner, key) is not { } value || !string.IsNullOrWhiteSpace(value)) return false;
        owner[key] = null;
        return true;
    }

    /// <summary>
    /// 不解析 JSON 的快速判定：绝大多数 chunk 的 finish_reason 是 null 或 stop、role / type 是合法值，
    /// 让它们连一次 JSON 解析都不用付。这里只回答「可能要改」：多报只多一次解析，漏报就是一次崩溃。
    /// </summary>
    private static bool MayNeedRewrite(string payload)
        => HasStringValue(payload, "finish_reason", value => string.IsNullOrWhiteSpace(value) || !IsKnownFinishReason(value))
           || HasStringValue(payload, "type", string.IsNullOrWhiteSpace)
           || HasStringValue(payload, "role", string.IsNullOrWhiteSpace);

    /// <summary>
    /// 找 <c>"key": "value"</c> 形状的键值对交给 <paramref name="predicate"/>。键名连引号一起匹配：
    /// 正文里转义过的 <c>\"type\"</c> 和 <c>athena_raw_finish_reason</c> 都不会被误认成键。
    /// </summary>
    private static bool HasStringValue(string payload, string key, Func<string, bool> predicate)
    {
        var needle = "\"" + key + "\"";
        var index = 0;
        while (true)
        {
            index = payload.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0) return false;
            index += needle.Length;

            var cursor = index;
            while (cursor < payload.Length && char.IsWhiteSpace(payload[cursor])) cursor++;
            if (cursor >= payload.Length || payload[cursor] != ':') continue; // 不是键，是一个恰好同名的字符串值
            cursor++;
            while (cursor < payload.Length && char.IsWhiteSpace(payload[cursor])) cursor++;
            if (cursor >= payload.Length || payload[cursor] != '"') continue; // null / 数字 / 畸形：交给 SDK

            var end = payload.IndexOf('"', cursor + 1);
            if (end < 0) continue;
            if (predicate(payload[(cursor + 1)..end])) return true;
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
        private HashSet<string>? _reportedBlankFields;

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
            var bytes = raw;
            if (TrySanitizeLine(line, out var sanitized, out _, out var blankFields))
            {
                bytes = Encoding.UTF8.GetBytes(sanitized);
                ReportBlankFields(blankFields);
            }

            // 已被读空的字节没有任何用处，趁机压回开头，免得一次长回复把整段响应留在内存里。
            if (_readyOffset > 0 && _readyOffset == _ready.Length)
            {
                _ready.SetLength(0);
                _readyOffset = 0;
            }

            _ready.Seek(0, SeekOrigin.End);
            _ready.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// 空白串是这类端点的常态（MiniMax 每个中间 chunk 都带一个），逐 chunk 记日志只会淹没别的东西；
        /// 每个响应、每个字段记一次 Debug，够回答「这条流被改过没有、改的是哪个字段」。
        /// </summary>
        private void ReportBlankFields(IReadOnlyList<string> blankFields)
        {
            foreach (var field in blankFields)
            {
                if ((_reportedBlankFields ??= new HashSet<string>(StringComparer.Ordinal)).Add(field))
                {
                    Log.Debug(
                        "ProviderStreamBlankEnumValue Field={Field}: the provider sent an empty string where the protocol has no value; read as absent",
                        field);
                }
            }
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
