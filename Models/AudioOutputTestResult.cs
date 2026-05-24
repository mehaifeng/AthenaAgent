using Athena.UI.Models;

namespace Athena.UI.Models;

public sealed class AudioOutputTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public ChatAttachment? Attachment { get; init; }
}
