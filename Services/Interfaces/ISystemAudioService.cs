using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ISystemAudioService
{
    bool IsSupported { get; }

    Task<SystemAudioResult> PlayFileAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class SystemAudioResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
