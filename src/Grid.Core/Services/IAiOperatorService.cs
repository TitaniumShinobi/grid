using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IAiOperatorService
{
    Task<OperatorServiceResponse> RespondAsync(
        OperatorRequest request,
        CancellationToken cancellationToken = default);
}
