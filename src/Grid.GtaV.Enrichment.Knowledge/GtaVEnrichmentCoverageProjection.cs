using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Enrichment.Knowledge;

/// <summary>Creates the single open GTA Location coverage ledger after all exact enrichments are applied.</summary>
public static class GtaVEnrichmentCoverageProjection
{
    public const string ConsolidatedManifestVersion = "gta-v-enhanced-mapzones-and-population-zones-v1";

    public static CanonicalCatalogPayload ApplyConsolidatedLocationCoverage(
        CanonicalCatalogPayload payload,
        KnowledgeSourceScope sourceScope) =>
        payload with { LocationCoverageReports = [CreateConsolidatedLocationCoverage(payload, sourceScope)] };

    public static LocationCoverageReport CreateConsolidatedLocationCoverage(
        CanonicalCatalogPayload payload,
        KnowledgeSourceScope sourceScope)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(sourceScope);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Consolidated GTA Location coverage requires the Enhanced base-game scope.");
        if (payload.LocationCoverageReports.Length != 2 ||
            payload.LocationCoverageReports.Any(value => value.Manifest.SourceScope != sourceScope))
            throw new InvalidDataException("Exactly the map-zone and population-zone coverage reports are required.");

        var reports = payload.LocationCoverageReports.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var terminologyDescriptor = payload.AdapterDescriptors.Single(value =>
            value.AdapterId == new KnowledgeAdapterId("grid.gta-v.enhanced.population-zone-gxt2-secondary"));
        var terminologyRevision = payload.SourceRevisions.Single(value =>
            value.AdapterRevisionId == terminologyDescriptor.RevisionId);
        var rawDeclarations = reports.SelectMany(value => value.Manifest.SourceFamilies).ToImmutableArray();
        var declarations = rawDeclarations.Select(value =>
        {
            if (value.SourceFamilyId != new LocationSourceFamilyId(GtaVPopulationZonesKnowledgeAdapter.CoverageSourceFamily))
                return value;
            return new LocationSourceFamilyDeclaration(
                value.SourceFamilyId,
                value.Format,
                value.AdapterRevisionId,
                value.IsApplicable,
                value.ExpectedArtifactIds.Concat(terminologyRevision.Revision.ArtifactIds).Distinct().ToImmutableArray(),
                value.ExpectedSourceRevisionIds.Add(terminologyRevision.Revision.Id),
                value.ExpectedSemanticRoles);
        }).OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        var rawFamilies = reports.SelectMany(value => value.SourceFamilies).ToImmutableArray();
        var families = rawFamilies.Select(value =>
        {
            if (value.SourceFamilyId != new LocationSourceFamilyId(GtaVPopulationZonesKnowledgeAdapter.CoverageSourceFamily))
                return value;
            return new LocationSourceFamilyCoverage(
                value.SourceFamilyId,
                value.ArtifactIds.Concat(terminologyRevision.Revision.ArtifactIds).Distinct().ToImmutableArray(),
                value.SourceRevisionIds.Add(terminologyRevision.Revision.Id),
                value.DiscoveredObjectCount,
                value.AcquiredObjectCount,
                value.ParsedObjectCount,
                value.EmittedLocationRecordIds,
                value.UnresolvedSourceAssertionIds,
                value.EvidenceBackedExclusions,
                value.UnsupportedObjectCount,
                value.ParserErrorCount,
                value.MissingArtifactCount,
                value.AmbiguousClassificationCount);
        }).OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (declarations.Select(value => value.SourceFamilyId).Distinct().Count() != 2 ||
            families.Select(value => value.SourceFamilyId).Distinct().Count() != 2 ||
            !declarations.Select(value => value.SourceFamilyId).SequenceEqual(families.Select(value => value.SourceFamilyId)))
            throw new InvalidDataException("The Location reports do not contain two distinct matching source families.");

        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate, "grid.location.coverage.qcs", "1",
            ContentDigest.ComputeSha256("grid.location.coverage.qcs.pending"u8));
        var manifest = new LocationCoverageManifest(
            LocationCoverageManifestId.DeriveV1(
                sourceScope, ConsolidatedManifestVersion, false, validation, declarations),
            sourceScope, ConsolidatedManifestVersion, false, validation, declarations);
        var recordIds = families.SelectMany(value => value.EmittedLocationRecordIds).ToHashSet();
        var actualRecords = payload.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId && value.Kind == KnowledgeKind.Location)
            .Select(value => value.Id).ToHashSet();
        if (!recordIds.SetEquals(actualRecords))
            throw new InvalidDataException("Consolidated Location coverage must account for every exact Location record.");

        var categories = payload.LocationSemanticClassificationAssertions
            .Where(value => recordIds.Contains(value.KnowledgeRecordId))
            .Join(
                payload.SourceNativeLocationTypeAssertions,
                classification => classification.SourceNativeTypeAssertionId,
                type => (SourceNativeLocationTypeAssertionId?)type.Id,
                (classification, type) => new { classification.RoleId, type.ExactNativeType, classification.KnowledgeRecordId })
            .GroupBy(value => (value.ExactNativeType, value.RoleId))
            .Select(value => new LocationSemanticCategoryCoverage(
                value.Key.ExactNativeType,
                value.Key.RoleId,
                value.Select(item => item.KnowledgeRecordId).Distinct().ToImmutableArray(),
                0))
            .ToImmutableArray();
        if (categories.SelectMany(value => value.RecordIds).Distinct().Count() != recordIds.Count)
            throw new InvalidDataException("Every consolidated Location requires exact native-type and semantic-role coverage.");

        var terminology = payload.TerminologyAssertions
            .Where(value => recordIds.Contains(value.KnowledgeRecordId)).ToImmutableArray();
        var named = terminology.Where(value => value.Role == TerminologyAssertionRole.PrimaryName)
            .Select(value => value.KnowledgeRecordId).Distinct().Count();
        var conflicts = terminology.Where(value => value.Role == TerminologyAssertionRole.PrimaryName)
            .GroupBy(value => value.KnowledgeRecordId)
            .Count(value => value.Select(assertion => assertion.VerbatimValue).Distinct(StringComparer.Ordinal).Count() > 1);
        var aliasCount = terminology.Count(value => value.Role == TerminologyAssertionRole.Alias);
        var relationshipCoverage = payload.RelationshipAssertions
            .Where(value => recordIds.Contains(value.SubjectKnowledgeRecordId))
            .GroupBy(value => value.SemanticId)
            .Select(group =>
            {
                var values = group.ToImmutableArray();
                var fileCount = values.Count(assertion => payload.EvidenceBindings.Any(binding =>
                    binding.ClaimKind == EvidenceClaimKind.Relationship &&
                    binding.KnowledgeRecordId == assertion.SubjectKnowledgeRecordId &&
                    binding.SourceRevisionId == assertion.SourceRevisionId &&
                    binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                    payload.FileEvidenceReceipts.Any(receipt => receipt.Id == binding.EvidenceReceiptId)));
                var referenceCount = values.Count(assertion => payload.EvidenceBindings.Any(binding =>
                    binding.ClaimKind == EvidenceClaimKind.Relationship &&
                    binding.KnowledgeRecordId == assertion.SubjectKnowledgeRecordId &&
                    binding.SourceRevisionId == assertion.SourceRevisionId &&
                    binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                    payload.ReferenceEvidenceReceipts.Any(receipt => receipt.Id == binding.EvidenceReceiptId)));
                return new LocationRelationshipCoverage(
                    group.Key, values.Length,
                    values.Count(value => value.ResolvedTargetKnowledgeRecordId is not null),
                    values.Count(value => value.ResolvedTargetKnowledgeRecordId is null),
                    fileCount, referenceCount, 0);
            }).ToImmutableArray();
        var hierarchyAssertions = payload.RelationshipAssertions.Where(value =>
            recordIds.Contains(value.SubjectKnowledgeRecordId) &&
            LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)).ToImmutableArray();
        var unresolved = reports.SelectMany(value => value.Unresolved).ToImmutableArray();
        return LocationCoverageReport.Create(
            manifest,
            families,
            categories,
            new LocationTerminologyCoverage(
                recordIds.Count, named, aliasCount, recordIds.Count - named, conflicts),
            new LocationHierarchyCoverage(
                hierarchyAssertions.Length,
                hierarchyAssertions.Count(value => value.ResolvedTargetKnowledgeRecordId is not null),
                hierarchyAssertions.Count(value => value.ResolvedTargetKnowledgeRecordId is null),
                0,
                hierarchyAssertions.IsEmpty),
            relationshipCoverage,
            unresolved);
    }
}
