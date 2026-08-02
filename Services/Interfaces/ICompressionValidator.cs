using Athena.UI.Models;
using System.Threading;

namespace Athena.UI.Services.Interfaces;

public interface ICompressionValidator
{
    CompressionValidationResult Validate(
        CompressionPlan plan,
        CompressionCandidate candidate,
        CancellationToken cancellationToken = default);
}
