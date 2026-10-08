using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed record RegistrationKnowledgeRefreshObservation(
    GameId GameId,
    CatalogPackageId? PinnedPackageId,
    string CatalogStoreSha256,
    long CatalogStoreRevision,
    string? ProfileObservationFingerprint);

public sealed record RegistrationKnowledgeContributorCapabilities(
    bool SupportsLocationRelationshipRegistration,
    string ContributorId,
    string ContributorVersion,
    ContentDigest ContributorRevisionDigest);

public sealed record RegistrationKnowledgeRefreshContribution(
    CanonicalCatalogPackage Package,
    CatalogPackageId PreviousPackageId,
    int AdmittedLocationRelationships,
    int RejectedLocationRelationships,
    ImmutableArray<string> ContributorRevisionIds,
    bool CandidateReadyWithoutImport = false);

public interface IRegistrationKnowledgeRefreshContributor
{
    GameId GameId { get; }

    RegistrationKnowledgeContributorCapabilities Capabilities { get; }

    RegistrationKnowledgeRefreshObservation Observe(RegistrationRefreshContext context);

    bool IsKnowledgeCurrent(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        RegistrationRefreshReceipt? lastReceipt);

    Task<RegistrationKnowledgeRefreshContribution?> TryRebuildAsync(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        IProgress<RegistrationRefreshProgress>? progress,
        CancellationToken cancellationToken);
}
