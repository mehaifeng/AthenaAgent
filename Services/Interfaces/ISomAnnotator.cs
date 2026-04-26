using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ISomAnnotator
{
    Task<SomObservation> AnnotateAsync(SomAnnotationRequest request, CancellationToken cancellationToken = default);
}
