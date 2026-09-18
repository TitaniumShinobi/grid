using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IFidelityAuditService
{
    Task<FidelityAuditResult> RunAsync(
        FidelityAuditContext context,
        IProgress<FidelityAuditProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
