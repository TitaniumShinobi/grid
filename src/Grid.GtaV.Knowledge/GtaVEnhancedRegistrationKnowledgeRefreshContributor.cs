using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed class GtaVEnhancedRegistrationKnowledgeRefreshContributor : IRegistrationKnowledgeRefreshContributor
{
    private const string ContributorId = "grid.gta-v.enhanced.registration-refresh";
    private const string ContributorVersion = "2";

    private static readonly ContentDigest CapabilitiesDigest = ContentDigest.ComputeSha256(
        Encoding.UTF8.GetBytes(ContributorId + "\0" + ContributorVersion + "\0" +
            typeof(GtaVLocationHierarchySecondaryAssertionAdapter).Assembly.Location));

    public static string CapabilitiesRevisionDigest => CapabilitiesDigest.HexValue;

    public GameId GameId => ProductionGridCatalogService.GrandTheftAutoVEnhancedId;

    public RegistrationKnowledgeContributorCapabilities Capabilities { get; } = new(
        SupportsLocationRelationshipRegistration: true,
        ContributorId,
        ContributorVersion,
        CapabilitiesDigest);

    public RegistrationKnowledgeRefreshObservation Observe(RegistrationRefreshContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sha = File.Exists(context.CatalogStorePath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(context.CatalogStorePath))).ToLowerInvariant()
            : string.Empty;
        return new(
            context.GameId,
            context.PinnedPackageId,
            sha,
            0,
            context.ProfileObservationFingerprint);
    }

    public bool IsKnowledgeCurrent(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        RegistrationRefreshReceipt? lastReceipt)
    {
        if (lastReceipt is not null &&
            lastReceipt.Status == RegistrationRefreshStatus.Completed &&
            string.Equals(lastReceipt.EngineVersion, CanonicalRegistrationEngineCoordinates.EngineVersion, StringComparison.Ordinal) &&
            string.Equals(lastReceipt.CatalogStoreSha256, observation.CatalogStoreSha256, StringComparison.OrdinalIgnoreCase) &&
            lastReceipt.PublishedPackageId == context.PinnedPackageId &&
            lastReceipt.ContributorRevisionIds.Contains(Capabilities.ContributorRevisionDigest.HexValue))
            return true;

        if (context.PinnedPackageId is not { } packageId) return false;
        var package = catalog.Snapshot.FindImportedPackage(packageId);
        return package is not null && HierarchyRelationshipsPresent(package);
    }

    public Task<RegistrationKnowledgeRefreshContribution?> TryRebuildAsync(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        IProgress<RegistrationRefreshProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.FromException<RegistrationKnowledgeRefreshContribution?>(new InvalidDataException(
            "GTA registration candidate authorship is owned by the MDBO refresh author."));

    internal static bool HierarchyRelationshipsPresent(CanonicalCatalogPackage package)
    {
        var count = package.Payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.ContainedBy &&
            value.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal) &&
            value.ResolvedTargetKnowledgeRecordId is not null);
        try
        {
            if (GtaVLocationHierarchyRegistrationRefreshSupport.TryLoadCorpus(null) is { } corpus)
            {
                var version = GtaVLocationRegistrationCandidatePackageProjector.RevisionVersion(corpus);
                return package.Payload.SourceRevisions.Any(value =>
                    string.Equals(value.Revision.NativeRevision?.ExactRepresentation, version, StringComparison.Ordinal));
            }
            return count == GtaVLocationHierarchyRegistrationRefreshSupport.LoadIndex(null).ExpectedRelationshipCount;
        }
        catch (IOException)
        {
            return count == 11;
        }
        catch (UnauthorizedAccessException)
        {
            return count == 11;
        }
    }
}
