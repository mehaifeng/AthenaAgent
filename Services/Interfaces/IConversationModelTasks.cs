using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IConversationTitleGenerator
{
    Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, bool useAi, CancellationToken cancellationToken = default);
}

public interface IContextCompressionService
{
    Task<CompressionResult> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        string? existingSummary,
        int keepRecentRounds = 3,
        CancellationToken cancellationToken = default);
}

public interface IWorkspaceKnowledgeCompressor
{
    Task<string?> CompressAsync(string content, int tokenBudget, CancellationToken cancellationToken = default);
}
