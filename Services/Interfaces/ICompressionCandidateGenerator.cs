using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ICompressionCandidateGenerator
{
    Task<CompressionGenerationResult> GenerateAsync(
        CompressionPlan plan,
        CancellationToken cancellationToken = default);
}

public interface ICompressionTextGenerator
{
    string ModelFingerprint { get; }

    Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken cancellationToken = default);
}
