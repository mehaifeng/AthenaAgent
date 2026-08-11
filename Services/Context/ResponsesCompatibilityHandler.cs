using Serilog;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Context;

/// <summary>
/// Normalizes narrow, schema-defined Responses API array fields before the strict OpenAI SDK
/// deserializer sees them. Some compatible endpoints serialize an empty list as JSON null.
/// The policy is endpoint- and model-agnostic and leaves unknown payloads byte-for-byte intact.
/// </summary>
internal sealed class ResponsesCompatibilityHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content == null || !response.IsSuccessStatusCode)
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var original = response.Content;
            var source = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var replacement = new StreamContent(new ResponsesSseNormalizingStream(source, original));
            CopyContentHeaders(original, replacement);
            response.Content = replacement;
            return response;
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var original = response.Content;
            var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var normalized = ResponsesPayloadNormalizer.NormalizeJson(bytes, out var changes);
            if (changes > 0)
            {
                var replacement = new ByteArrayContent(normalized);
                CopyContentHeaders(original, replacement);
                response.Content = replacement;
                original.Dispose();
                Log.Debug("Normalized {Count} null Responses API array fields in a JSON response", changes);
            }
        }

        return response;
    }

    private static void CopyContentHeaders(HttpContent source, HttpContent target)
    {
        foreach (var header in source.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            target.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}

internal static class ResponsesPayloadNormalizer
{
    private static readonly string[] NullableArrayPropertyHints =
        ["\"annotations\"", "\"logprobs\"", "\"content\"", "\"output\"", "\"summary\""];

    public static byte[] NormalizeJson(ReadOnlySpan<byte> utf8Json, out int changes)
    {
        changes = 0;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return utf8Json.ToArray();
        }

        NormalizeNode(root, ref changes);
        return changes == 0
            ? utf8Json.ToArray()
            : JsonSerializer.SerializeToUtf8Bytes(root);
    }

    public static string NormalizeSseLine(string line, out int changes)
    {
        changes = 0;
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return line;
        }

        var payloadStart = "data:".Length;
        while (payloadStart < line.Length && char.IsWhiteSpace(line[payloadStart])) payloadStart++;
        var payload = line[payloadStart..];
        if (payload.Length == 0 || payload == "[DONE]" || !MayContainNullableArray(payload))
        {
            return line;
        }

        var normalized = NormalizeJson(Encoding.UTF8.GetBytes(payload), out changes);
        return changes == 0 ? line : "data: " + Encoding.UTF8.GetString(normalized);
    }

    private static bool MayContainNullableArray(string payload)
    {
        if (!payload.Contains("null", StringComparison.Ordinal)) return false;
        foreach (var property in NullableArrayPropertyHints)
        {
            if (payload.Contains(property, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void NormalizeNode(JsonNode? node, ref int changes)
    {
        switch (node)
        {
            case JsonObject value:
                var type = GetString(value, "type");
                var objectType = GetString(value, "object");
                if (objectType == "response")
                {
                    NullToArray(value, "output", ref changes);
                }
                if (type == "message")
                {
                    NullToArray(value, "content", ref changes);
                }
                else if (type == "output_text")
                {
                    NullToArray(value, "annotations", ref changes);
                    NullToArray(value, "logprobs", ref changes);
                }
                else if (type == "reasoning")
                {
                    NullToArray(value, "summary", ref changes);
                    NullToArray(value, "content", ref changes);
                }

                foreach (var property in value.ToArray())
                {
                    NormalizeNode(property.Value, ref changes);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    NormalizeNode(item, ref changes);
                }
                break;
        }
    }

    private static string? GetString(JsonObject value, string propertyName)
    {
        if (!value.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue jsonValue)
        {
            return null;
        }
        return jsonValue.TryGetValue<string>(out var text) ? text : null;
    }

    private static void NullToArray(JsonObject value, string propertyName, ref int changes)
    {
        if (value.TryGetPropertyValue(propertyName, out var node) && node == null)
        {
            value[propertyName] = new JsonArray();
            changes++;
        }
    }
}

internal sealed class ResponsesSseNormalizingStream : Stream
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly HttpContent _owner;
    private readonly StreamReader _reader;
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _completed;

    public ResponsesSseNormalizingStream(Stream source, HttpContent owner)
    {
        _owner = owner;
        _reader = new StreamReader(
            source,
            Utf8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: false);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = Read(rented, 0, buffer.Length);
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (_pendingOffset >= _pending.Length)
        {
            if (_completed) return 0;
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                _completed = true;
                return 0;
            }

            var normalized = ResponsesPayloadNormalizer.NormalizeSseLine(line, out var changes);
            if (changes > 0)
            {
                Log.Debug("Normalized {Count} null Responses API array fields in an SSE event", changes);
            }
            _pending = Utf8.GetBytes(normalized + "\n");
            _pendingOffset = 0;
        }

        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
