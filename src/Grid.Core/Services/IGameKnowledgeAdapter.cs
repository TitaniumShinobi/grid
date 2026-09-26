using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Preproduction-only contract for deterministic discovery and extraction from frozen source artifacts.
/// Runtime package matching and DIF queries must not invoke this interface.
/// </summary>
public interface IGameKnowledgeAdapter
{
    GameKnowledgeAdapterDescriptor Descriptor { get; }

    Task<SourceDiscoveryResult> DiscoverAsync(
        PreproductionSourceDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<KnowledgeExtractionResult> ExtractAsync(
        PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default);
}
