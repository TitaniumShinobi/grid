using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Explicit offline registration only. Adapters normalize source families, never UI lists.</summary>
public interface IRegistrationEvidenceAdapter
{
    string Id { get; }
    string Version { get; }
    // Discovery is restricted to the admitted input set. Network/machine-wide discovery is not implicit.
    Task<IReadOnlyList<RegistrationSource>> DiscoverAsync(IReadOnlyList<RegistrationSourceArtifact> sources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<RegistrationSource>>(sources.Select(s => s.Source).ToArray());
    }
    Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources,
        CancellationToken cancellationToken = default);
}
