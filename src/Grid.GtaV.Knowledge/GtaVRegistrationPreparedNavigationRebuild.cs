using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Rebuilds profile-scoped registration-prepared Location navigation after catalog publication.</summary>
public static class GtaVRegistrationPreparedNavigationRebuild
{
    public static async Task RebuildAsync(
        RegistrationRefreshContext context,
        CanonicalCatalogLoadResult catalog,
        CatalogPackageId packageId,
        CancellationToken cancellationToken = default)
    {
        if (context.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId)
            return;
        var package = catalog.Snapshot.FindImportedPackage(packageId)
            ?? throw new InvalidDataException("Published package is missing from the validated catalog.");
        var baselinePayload = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(package.Payload);
        var corpus = GtaVLocationHierarchyRegistrationRefreshSupport.TryLoadCorpus(context);
        CanonicalRegistrationCandidate candidate;
        if (corpus is not null)
        {
            candidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
                new GtaVLocationRegistrationEvidenceAdapter(baselinePayload, corpus, context.ProfileId, package.Id),
                GtaVLocationRegistrationSourceArtifacts.CreateAdmittedSources(corpus, baselinePayload),
                GtaVLocationRegistrationRules.Create(),
                [package],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var index = GtaVLocationHierarchyRegistrationRefreshSupport.LoadIndex(context);
            candidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
                new GtaVLocationHierarchyRegistrationEvidenceAdapter(baselinePayload, index, context.ProfileId, package.Id),
                GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index),
                GtaVLocationHierarchyRegistrationRules.Create(),
                [package],
                cancellationToken).ConfigureAwait(false);
        }
        var navContext = new RegistrationNavigationContext(
            context.GameId.Value,
            context.ProfileId.Value,
            "en-US");
        var preparedRoot = Path.Combine(
            Path.GetDirectoryName(context.PreparedNavigationRoot)!,
            "prepared-registration");
        var prepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(
            preparedRoot,
            candidate,
            navContext,
            [package],
            cancellationToken).ConfigureAwait(false);
        var publication = new RegistrationPreparedNavigationPublication(
            RegistrationPreparedNavigationPublication.CurrentSchemaVersion,
            prepared.Digest,
            packageId,
            context.GameId,
            context.ProfileId,
            navContext.Locale,
            CanonicalRegistrationEncoding.Digest(candidate));
        new RegistrationPreparedNavigationPublicationStore(preparedRoot).Publish(context.ProfileId, publication);
    }
}
