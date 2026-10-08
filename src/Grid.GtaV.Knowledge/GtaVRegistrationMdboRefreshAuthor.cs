using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed class GtaVRegistrationMdboRefreshAuthor : IRegistrationMdboRefreshAuthor
{
    private const string ContributorId = "grid.gta-v.enhanced.registration-refresh";
    private const string ContributorVersion = "2";
    internal const string FrozenReferenceMethodId = "grid.gta-v.location-hierarchy.frozen-reference";

    public GameId GameId => ProductionGridCatalogService.GrandTheftAutoVEnhancedId;

    /// <summary>Evidence adapter of the most recent v3 authoring, exposing the resolver ledger to proofs.</summary>
    public GtaVLocationRegistrationEvidenceAdapter? LastLocationAdapter { get; private set; }

    public async Task<RegistrationKnowledgeRefreshContribution?> TryAuthorRebuildAsync(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        IProgress<RegistrationRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.PinnedPackageId is not { } originPackageId)
            throw new InvalidDataException("GTA registration refresh requires a pinned canonical package.");
        var origin = catalog.Snapshot.FindImportedPackage(originPackageId) ??
            throw new InvalidDataException("Pinned canonical package is absent from the store.");
        if (GtaVEnhancedRegistrationKnowledgeRefreshContributor.HierarchyRelationshipsPresent(origin))
            return null;

        progress?.Report(new("location-sources", "Loading approved Location hierarchy sources.", 0.25));
        var adapterDigest = ContentDigest.ComputeSha256(
            File.ReadAllBytes(typeof(GtaVLocationHierarchySecondaryAssertionAdapter).Assembly.Location));
        var scope = KnowledgeSourceScope.BaseGame(origin.Manifest.GameScope.GameId, origin.Manifest.GameScope.ExactGameVersion);
        var baselinePayload = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(origin.Payload);

        progress?.Report(new("mdobo-register", "Authoring MDBO registration candidate.", 0.28));
        CanonicalRegistrationCandidate registrationCandidate;
        GtaVSecondaryAssertionBatch batch;
        ImmutableArray<FrozenSourceArtifact> artifacts;
        var corpus = GtaVLocationHierarchyRegistrationRefreshSupport.TryLoadCorpus(context);
        if (corpus is not null)
        {
            var adapter = new GtaVLocationRegistrationEvidenceAdapter(baselinePayload, corpus, context.ProfileId, originPackageId);
            registrationCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
                adapter,
                GtaVLocationRegistrationSourceArtifacts.CreateAdmittedSources(corpus, baselinePayload),
                GtaVLocationRegistrationRules.Create(),
                [origin],
                cancellationToken).ConfigureAwait(false);
            LastLocationAdapter = adapter;
            adapterDigest = GtaVLocationRegistrationRules.SemanticAdapterDigest();
            batch = GtaVLocationRegistrationCandidatePackageProjector.Project(adapterDigest, corpus, baselinePayload, scope, registrationCandidate);
            artifacts = corpus.Artifacts;
        }
        else
        {
            var index = GtaVLocationHierarchyRegistrationRefreshSupport.LoadIndex(context);
            registrationCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
                new GtaVLocationHierarchyRegistrationEvidenceAdapter(baselinePayload, index, context.ProfileId, originPackageId),
                GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index),
                GtaVLocationHierarchyRegistrationRules.Create(),
                [origin],
                cancellationToken).ConfigureAwait(false);
            batch = GtaVLocationHierarchyRegistrationCandidatePackageProjector.Project(adapterDigest, index, baselinePayload, scope, registrationCandidate);
            if (batch.RelationshipAssertions.Length != index.ExpectedPackageAssertionRelationshipCount)
                throw new InvalidDataException("Location hierarchy refresh projected an unexpected relationship count.");
            artifacts = index.Artifacts;
        }
        var correlatedRelationships = registrationCandidate.Relationships
            .Count(r => r.CorrelationOutcome == RegistrationOutcome.Correlated);
        var rejectedRelationshipRulings = registrationCandidate.Rulings
            .Count(r => r.Stage == "relationship" && r.Outcome is RegistrationOutcome.Rejected or RegistrationOutcome.Ambiguous or RegistrationOutcome.Unresolved);

        var originalAcquisition = baselinePayload.AcquisitionReceipts.First();
        var method = new AcquisitionMethodCoordinate(
            FrozenReferenceMethodId,
            "2",
            CanonicalRegistrationEngineCoordinates.EngineId,
            CanonicalRegistrationEngineCoordinates.EngineVersion,
            adapterDigest);
        var hierarchyArtifactIds = artifacts.Select(value => value.Id).ToHashSet();
        var retainedArtifactIds = baselinePayload.Artifacts.Select(value => value.Id).ToHashSet();
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        // Earlier frozen-reference receipts describe a superseded corpus and are replaced, not accumulated.
        foreach (var receipt in baselinePayload.AcquisitionReceipts)
        {
            if (receipt.Members.All(member => !hierarchyArtifactIds.Contains(member.ArtifactId)) &&
                !(receipt.AcquisitionMethod.MethodId == FrozenReferenceMethodId && receipt.Members.All(member => !retainedArtifactIds.Contains(member.ArtifactId))))
                receipts.Add(receipt);
        }
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        foreach (var binding in baselinePayload.ArtifactAcquisitionBindings)
        {
            if (!hierarchyArtifactIds.Contains(binding.ArtifactId))
                bindings.Add(binding);
        }
        foreach (var artifact in artifacts)
        {
            ImmutableArray<SourceAcquisitionMember> members =
                [new(artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, artifact.Id)];
            var id = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                scope.GameId,
                originalAcquisition.DistributionApplicationIdentity!,
                originalAcquisition.DistributionBuildVersion!,
                artifact.SourceCoordinate,
                artifact.ExactBytes.Length,
                artifact.Digest,
                method,
                members);
            receipts.Add(new(id, SourceAcquisitionReceipt.CurrentSchemaVersion, scope.GameId,
                originalAcquisition.DistributionApplicationIdentity, originalAcquisition.DistributionBuildVersion,
                artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, method, members));
            bindings.Add(new(artifact.Id, id, artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest));
        }

        var payload = batch.ApplyTo(baselinePayload, receipts.ToImmutable(), bindings.ToImmutable());
        var provenance = GtaVLocationHierarchyRegistrationRefreshSupport.BuildRegistrationRefreshProvenance(
            ContributorId, ContributorVersion);
        var candidate = CanonicalCatalogPackageKernel.CreateV6(
            origin.Manifest.PackageKind,
            new CatalogGameScope(scope.GameId, origin.Manifest.GameScope.ExactGameVersion,
                payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
            origin.Manifest.ModScope,
            origin.Manifest.RequiredBasePackageIds,
            origin.Manifest.CompositionPolicyVersion,
            payload,
            new CatalogValidationSummary(
                CatalogValidationStatus.Candidate,
                "grid.location-hierarchy.registration-refresh",
                "2",
                ContentDigest.ComputeSha256("registration-refresh:not-release-certified"u8)),
            provenance);
        var verification = CanonicalCatalogPackageKernel.Verify(candidate);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException(string.Join("; ", verification.Issues));
        if (registrationCandidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("MDBO registration candidate must remain NOT_PUBLISHED.");
        return new(
            candidate,
            originPackageId,
            correlatedRelationships,
            rejectedRelationshipRulings,
            [GtaVEnhancedRegistrationKnowledgeRefreshContributor.CapabilitiesRevisionDigest]);
    }
}
