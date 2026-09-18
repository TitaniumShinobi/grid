using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IWorkspaceToolOutputQueryService
{
    Task<ToolOutputObservationRefreshResult> RefreshAsync(
        WorkspaceToolOutputContext context,
        bool forceRefresh,
        IProgress<ExternalObservationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
