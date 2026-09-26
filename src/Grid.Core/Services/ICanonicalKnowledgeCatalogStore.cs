using Grid.Core.Models;

namespace Grid.Core.Services;

public interface ICanonicalKnowledgeCatalogStore
{
    Task<CanonicalCatalogLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<CanonicalCatalogAppendResult> AppendAsync(
        long expectedRevision,
        CanonicalCatalogRegistration registration,
        CancellationToken cancellationToken = default);

    Task<CanonicalCatalogImportResult> ImportPackageAsync(
        long expectedRevision,
        CanonicalCatalogPackage package,
        CancellationToken cancellationToken = default);
}
