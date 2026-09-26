using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Projects an already verified canonical package into deterministic Location selector data.
/// It performs no discovery, extraction, correlation, translation, persistence, or mutation.
/// </summary>
public static class CanonicalLocationSelectorProjector
{
    public static LocationPresentationPolicyId PresentationPolicyId { get; } =
        new("grid.location.presentation.v1");

    public const string ExactPresentationPolicyVersion = "1";

    private static readonly ImmutableArray<LocationSemanticRoleId> RoleOrder =
    [
        LocationSemanticRoles.World,
        LocationSemanticRoles.Region,
        LocationSemanticRoles.AdministrativeArea,
        LocationSemanticRoles.Settlement,
        LocationSemanticRoles.District,
        LocationSemanticRoles.AreaZone,
        LocationSemanticRoles.Route,
        LocationSemanticRoles.Landmark,
        LocationSemanticRoles.Business,
        LocationSemanticRoles.Property,
        LocationSemanticRoles.Residence,
        LocationSemanticRoles.Facility,
        LocationSemanticRoles.Building,
        LocationSemanticRoles.ExteriorPlace,
        LocationSemanticRoles.Interior,
        LocationSemanticRoles.Room,
        LocationSemanticRoles.Dungeon,
        LocationSemanticRoles.Cave,
        LocationSemanticRoles.Instance,
        LocationSemanticRoles.MissionPlace,
        LocationSemanticRoles.TransitNode,
    ];

    public static LocationSelectorResult Project(
        CanonicalCatalogPackage package,
        LocationSelectorQuery query,
        LocationCoverageReportId coverageReportId,
        IReadOnlyDictionary<KnowledgeRecordId, LocationApplicabilityState>? applicability = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(query);
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException("Only a structurally valid canonical package can be queried.");
        var payload = package.Payload;
        var coverageReport = payload.LocationCoverageReports.SingleOrDefault(value => value.Id == coverageReportId);
        if (coverageReport is null)
            throw new ArgumentException("The requested Location coverage report is not present in the package.", nameof(coverageReportId));
        var coveredLocationIds = coverageReport.SourceFamilies
            .SelectMany(value => value.EmittedLocationRecordIds)
            .ToHashSet();

        applicability ??= ImmutableDictionary<KnowledgeRecordId, LocationApplicabilityState>.Empty;
        var sourceScopes = payload.SourceRevisions.ToDictionary(value => value.Revision.Id, value => value.SourceScope);
        var fileEvidenceIds = payload.FileEvidenceReceipts.Select(value => value.Id).ToHashSet();
        var referenceEvidenceIds = payload.ReferenceEvidenceReceipts.Select(value => value.Id).ToHashSet();
        var strictRelationships = payload.RelationshipAssertions
            .Where(value => LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId))
            .ToImmutableArray();

        var options = ImmutableArray.CreateBuilder<LocationSelectorOption>();
        foreach (var record in payload.KnowledgeRecords.Where(value =>
                     value.Kind == KnowledgeKind.Location && coveredLocationIds.Contains(value.Id)))
        {
            var state = applicability.TryGetValue(record.Id, out var observedState)
                ? observedState
                : LocationApplicabilityState.Applicable;
            if (state != LocationApplicabilityState.Applicable && !query.IncludeInapplicableForInspection)
                continue;

            var terms = payload.TerminologyAssertions
                .Where(value => value.KnowledgeRecordId == record.Id)
                .OrderBy(value => value.Role)
                .ThenBy(value => value.LanguageTag, StringComparer.Ordinal)
                .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
                .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var aliases = terms.Where(value => value.Role == TerminologyAssertionRole.Alias).ToImmutableArray();
            var nativeTypes = payload.SourceNativeLocationTypeAssertions
                .Where(value => value.KnowledgeRecordId == record.Id)
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var classifications = payload.LocationSemanticClassificationAssertions
                .Where(value => value.KnowledgeRecordId == record.Id)
                .OrderBy(value => RoleRank(value.RoleId))
                .ThenBy(value => value.RoleId.Value, StringComparer.Ordinal)
                .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (!query.LocationSemanticRoleIds.IsEmpty &&
                !classifications.Any(value => query.LocationSemanticRoleIds.Contains(value.RoleId)))
                continue;

            var relationships = payload.RelationshipAssertions
                .Where(value => value.SubjectKnowledgeRecordId == record.Id)
                .OrderBy(value => value.SemanticId.Value, StringComparer.Ordinal)
                .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (query.ParentKnowledgeRecordId is { } parent && !strictRelationships.Any(value =>
                    value.SubjectKnowledgeRecordId == record.Id &&
                    value.ResolvedTargetKnowledgeRecordId == parent))
                continue;

            var primaryTerms = terms.Where(value => value.Role == TerminologyAssertionRole.PrimaryName).ToImmutableArray();
            if (primaryTerms.IsEmpty && !query.IncludeIdentifierOnly)
                continue;
            if (!MatchesSearch(query.SearchText, terms, record.NativeIdentity, query.IncludeIdentifierOnly))
                continue;

            var parentPaths = strictRelationships
                .Where(value => value.SubjectKnowledgeRecordId == record.Id && value.ResolvedTargetKnowledgeRecordId is not null)
                .OrderBy(value => value.ResolvedTargetKnowledgeRecordId!.Value.Value, StringComparer.Ordinal)
                .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
                .Select(value => new LocationAuthoritativeParentPath(
                    [value.ResolvedTargetKnowledgeRecordId!.Value],
                    [EvidenceClaimContentId.DeriveV1(value)]))
                .ToImmutableArray();
            var recordBindings = payload.EvidenceBindings.Where(value => value.KnowledgeRecordId == record.Id).ToImmutableArray();
            var evidenceStates = ImmutableArray.CreateBuilder<EvidenceVerificationKind>();
            if (recordBindings.Any(value => fileEvidenceIds.Contains(value.EvidenceReceiptId)))
                evidenceStates.Add(EvidenceVerificationKind.FileVerified);
            if (recordBindings.Any(value => referenceEvidenceIds.Contains(value.EvidenceReceiptId)))
                evidenceStates.Add(EvidenceVerificationKind.ReferenceVerified);
            var correlations = payload.CorrelationEnvelopes
                .Where(value => value.Record.MemberIds.Contains(record.Id))
                .Select(value => value.Id)
                .OrderBy(value => value.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var correlatedRelationships = payload.CorrelatedRelationshipEnvelopes
                .Where(value => value.SubjectKnowledgeRecordId == record.Id)
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var conflict = primaryTerms.Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).Skip(1).Any()
                ? LocationConflictState.Terminology
                : LocationConflictState.None;
            if (correlatedRelationships.Any(value =>
                    value.Outcome == CorrelationOutcome.Ambiguous &&
                    LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)))
                conflict |= LocationConflictState.Hierarchy;
            if (correlatedRelationships.Any(value =>
                    value.Outcome == CorrelationOutcome.Ambiguous &&
                    !LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)))
                conflict |= LocationConflictState.Relationship;
            var resolution = relationships.Any(value => value.Resolution == CanonicalResolutionState.Unresolved) ||
                             correlatedRelationships.Any(value =>
                                 value.Outcome is CorrelationOutcome.Ambiguous or CorrelationOutcome.Unresolved)
                ? CanonicalResolutionState.Unresolved
                : CanonicalResolutionState.Resolved;

            options.Add(new LocationSelectorOption(
                record.Id,
                terms,
                aliases,
                record.NativeIdentity,
                nativeTypes,
                classifications,
                parentPaths,
                relationships,
                correlations,
                correlatedRelationships.Select(value => value.Id).ToImmutableArray(),
                evidenceStates.ToImmutable(),
                sourceScopes[record.SourceRevisionId],
                state,
                conflict,
                resolution));
        }

        var orderedOptions = options
            .OrderBy(value => value.SemanticClassifications.IsEmpty
                ? int.MaxValue
                : value.SemanticClassifications.Min(item => RoleRank(item.RoleId)))
            .ThenBy(value => value.ExactTerminologyAssertions
                .FirstOrDefault(item => item.Role == TerminologyAssertionRole.PrimaryName)?.VerbatimValue,
                StringComparer.Ordinal)
            .ThenBy(value => value.KnowledgeRecordId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new LocationSelectorResult(
            package.Manifest.CatalogRevisionId,
            query.CatalogCompositionId,
            coverageReportId,
            PresentationPolicyId,
            ExactPresentationPolicyVersion,
            orderedOptions);
    }

    private static bool MatchesSearch(
        string? searchText,
        ImmutableArray<TerminologyAssertion> terminology,
        SourceNativeIdentifier nativeIdentity,
        bool includeIdentifierOnly)
    {
        if (string.IsNullOrEmpty(searchText)) return true;
        if (terminology.Any(value =>
                (value.Role is TerminologyAssertionRole.PrimaryName or TerminologyAssertionRole.Alias) &&
                value.VerbatimValue.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
            return true;
        return includeIdentifierOnly &&
               nativeIdentity.ExactRepresentation.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static int RoleRank(LocationSemanticRoleId roleId)
    {
        for (var index = 0; index < RoleOrder.Length; index++)
            if (RoleOrder[index] == roleId) return index;
        return RoleOrder.Length;
    }
}
