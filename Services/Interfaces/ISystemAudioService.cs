using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Athena.UI.Services.Interfaces;

public interface ISystemAudioService
{
    bool IsSupported { get; }

    Task<IReadOnlyList<string>> GetAvailableVoicesAsync(CancellationToken cancellationToken = default);

    Task<SystemAudioResult> SynthesizeToFileAsync(string text, string voice, string outputPath, CancellationToken cancellationToken = default);

    Task<SystemAudioResult> PlayFileAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed class SystemAudioResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
