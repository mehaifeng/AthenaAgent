using System;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.Functions;

/// <summary>
/// Turns System.Text.Json's position-based parse errors into something a model can act on.
/// A truncated payload and a mistyped brace produce the same class of exception, but the fix is
/// completely different: one means "shorten and resend", the other means "look at your syntax".
/// Reported verbatim, "Expected depth to be zero at the end of the JSON payload. BytePositionInLine:
/// 13779" costs a full round trip while the model works out which one it hit.
/// </summary>
internal static class JsonPayloadDiagnostics
{
    /// <summary>
    /// Returns <paramref name="exception"/>'s message, extended with a truncation diagnosis when the
    /// parser ran out of input. Payloads are passed as (name, value) so the message can name the
    /// offending argument and state its size.
    /// </summary>
    public static string Explain(Exception exception, params (string Name, string? Payload)[] payloads)
    {
        if (exception is not JsonException json || !RanOutOfInput(json)) return exception.Message;

        foreach (var (name, payload) in payloads)
        {
            if (payload is null) continue;
            return $"{exception.Message} The '{name}' argument ended mid-structure at "
                   + $"{payload.Length} characters ({Encoding.UTF8.GetByteCount(payload)} bytes), so it was most "
                   + "likely cut off while being generated rather than mistyped. Resend it shorter — fewer items "
                   + "per call, tighter text, drop optional fields — or build the result over several calls.";
        }

        return exception.Message;
    }

    /// <summary>
    /// True when the parser hit the end of the payload while a structure was still open. These two
    /// phrases are System.Text.Json's own wording for exactly that case, so matching them does not
    /// misfire on ordinary syntax errors, which report a concrete offending token instead.
    /// </summary>
    private static bool RanOutOfInput(JsonException exception) =>
        exception.Message.Contains("depth to be zero", StringComparison.Ordinal)
        || exception.Message.Contains("reached end of data", StringComparison.Ordinal);
}
