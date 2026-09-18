using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IWorkspaceLaunchService
{
    Task<ExternalLaunchPreparationResult> PrepareAsync(
        ExternalLaunchIntent intent,
        FidelityAuditSnapshot audit,
        CancellationToken cancellationToken = default);

    Task<ExternalLaunchResult> LaunchAsync(
        ExternalLaunchPreparation preparation,
        ExternalLaunchApproval approval,
        IProgress<ExternalLaunchEvent>? progress = null,
        CancellationToken cancellationToken = default);
}
