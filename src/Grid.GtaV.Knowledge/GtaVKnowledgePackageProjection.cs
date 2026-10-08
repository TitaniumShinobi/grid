using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Pure preproduction projection from universal adapter output to the semantic package payload.
/// It performs no persistence, publication, matching, or applicability work.
/// </summary>
public static class GtaVKnowledgePackageProjection
{
    public static CanonicalCatalogPayload AddSecondaryAssertions(
        CanonicalCatalogPayload origin,
        GtaVSecondaryAssertionBatch secondary,
        ImmutableArray<SourceAcquisitionReceipt> acquisitionReceipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> artifactAcquisitionBindings)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(secondary);
        var artifacts = DistinctBy(
            origin.Artifacts.Concat(secondary.AdditionalArtifacts), value => value.Id.Value);
        var acquisition = NarrowAcquisitionClosure(
            artifacts, acquisitionReceipts, artifactAcquisitionBindings);
        return new CanonicalCatalogPayload(
            origin.EffectiveCoverage,
            DistinctBy(origin.AdapterDescriptors.Add(secondary.AdapterDescriptor), value => value.RevisionId.Value),
            DistinctBy(origin.Sources.Concat(secondary.AdditionalSources), value => value.Id.Value),
            artifacts,
            DistinctBy(origin.SourceRevisions.Add(secondary.SourceRevision).AddRange(secondary.AdditionalSourceRevisions), value => value.Revision.Id.Value),
            DistinctBy(origin.KnowledgeRecords.Concat(secondary.AdditionalKnowledgeRecords), value => value.Id.Value),
            DistinctBy(origin.TerminologyAssertions.Concat(secondary.TerminologyAssertions),
                value => EvidenceClaimContentId.DeriveV1(value).Value),
            origin.RelationshipAssertions,
            DistinctBy(origin.FileEvidenceReceipts.Concat(secondary.FileEvidenceReceipts), value => value.Id.Value),
            DistinctBy(origin.ReferenceEvidenceReceipts.Concat(secondary.ReferenceEvidenceReceipts), value => value.Id.Value),
            DistinctBy(origin.EvidenceBindings.Concat(secondary.EvidenceBindings), value => value.Id.Value),
            DistinctBy(origin.CorrelationEnvelopes.Concat(secondary.CorrelationEnvelopes), value => value.Id.Value),
            origin.UnresolvedSourceAssertions,
            acquisition.Receipts,
            acquisition.Bindings)
        {
            SourceNativeLocationTypeAssertions = origin.SourceNativeLocationTypeAssertions,
            LocationSemanticClassificationAssertions = origin.LocationSemanticClassificationAssertions,
            RecordLifecycleAssertions = origin.RecordLifecycleAssertions,
            CorrelatedRelationshipEnvelopes = origin.CorrelatedRelationshipEnvelopes,
            LocationCoverageReports = DistinctBy(
                origin.LocationCoverageReports.Concat(secondary.AdditionalLocationCoverageReports),
                value => value.Id.Value),
            SemanticClassificationAssertions = DistinctBy(
                origin.SemanticClassificationAssertions.Concat(secondary.SemanticClassifications),
                value => value.Id.Value),
            RecordContributionAssertions = origin.RecordContributionAssertions,
            OrganizationalValueAssertions = DistinctBy(
                origin.OrganizationalValueAssertions.Concat(secondary.OrganizationalValues),
                value => value.Id.Value),
            InstructionAssertions = origin.InstructionAssertions,
            InstructionEvidenceBindings = origin.InstructionEvidenceBindings,
            InstructionConflictGroups = origin.InstructionConflictGroups,
            CrossSourceAssertions = DistinctBy(
                origin.CrossSourceAssertions.Concat(secondary.CrossSourceAssertions), value => value.Id.Value),
            CrossSourceTargetLinkClaims = DistinctBy(
                origin.CrossSourceTargetLinkClaims.Concat(secondary.TargetLinkClaims), value => value.Id.Value),
            UnresolvedCrossSourceClaimContents = DistinctBy(
                origin.UnresolvedCrossSourceClaimContents.Concat(secondary.UnresolvedCrossSourceClaimContents),
                value => value.Id.Value),
            UnresolvedCrossSourceEvidenceBindings = DistinctBy(
                origin.UnresolvedCrossSourceEvidenceBindings.Concat(secondary.UnresolvedCrossSourceEvidenceBindings),
                value => value.Id.Value),
            UnresolvedCrossSourceAssertions = DistinctBy(
                origin.UnresolvedCrossSourceAssertions.Concat(secondary.UnresolvedCrossSourceAssertions),
                value => value.Id.Value),
        };
    }

    public static CanonicalCatalogPayload CreatePayload(KnowledgeExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return CreatePayload([extraction], [], []);
    }

    public static CanonicalCatalogPayload CreatePayload(
        IEnumerable<KnowledgeExtractionResult> extractions,
        ImmutableArray<SourceAcquisitionReceipt> acquisitionReceipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> artifactAcquisitionBindings)
    {
        ArgumentNullException.ThrowIfNull(extractions);
        if (acquisitionReceipts.IsDefault || artifactAcquisitionBindings.IsDefault)
            throw new ArgumentException("Acquisition collections must be initialized.");
        var extractionValues = extractions.ToImmutableArray();
        if (extractionValues.IsDefaultOrEmpty || extractionValues.Any(value => value is null))
            throw new InvalidDataException("At least one non-null extraction result is required.");
        if (extractionValues.Any(value =>
                value.CoverageState == KnowledgeCoverageState.Unsupported ||
                value.CanonicalRegistrations.IsEmpty))
            throw new InvalidDataException("Unsupported or empty extraction output cannot become a catalog payload.");

        var values = extractionValues.SelectMany(value => value.CanonicalRegistrations).ToImmutableArray();
        var artifacts = DistinctBy(
            values.SelectMany(value => value.Registration.Artifacts),
            value => value.Id.Value);
        var acquisition = NarrowAcquisitionClosure(
            artifacts,
            acquisitionReceipts,
            artifactAcquisitionBindings);
        return new CanonicalCatalogPayload(
            extractionValues.All(value => value.CoverageState == KnowledgeCoverageState.Complete)
                ? KnowledgeCoverageState.Complete
                : KnowledgeCoverageState.Partial,
            DistinctBy(values.Select(value => value.AdapterDescriptor), value => value.RevisionId.Value),
            DistinctBy(values.Select(value => value.Registration.Source), value => value.Id.Value),
            artifacts,
            DistinctBy(values.Select(value => new AdapterBoundCatalogSourceRevisionRecord(
                    value.Registration.SourceRevision,
                    value.AdapterRevisionId,
                    value.SourceScope,
                    value.ArtifactFormats)),
                value => value.Revision.Id.Value),
            DistinctBy(values.SelectMany(value => value.Registration.KnowledgeRecords), value => value.Id.Value),
            DistinctBy(
                values.SelectMany(value => value.Registration.TerminologyAssertions),
                value => EvidenceClaimContentId.DeriveV1(value).Value),
            DistinctBy(
                values.SelectMany(value => value.Registration.RelationshipAssertions),
                value => EvidenceClaimContentId.DeriveV1(value).Value),
            DistinctBy(values.SelectMany(value => value.Registration.FileEvidenceReceipts), value => value.Id.Value),
            DistinctBy(values.SelectMany(value => value.Registration.ReferenceEvidenceReceipts), value => value.Id.Value),
            DistinctBy(values.SelectMany(value => value.Registration.EvidenceBindings), value => value.Id.Value),
            DistinctBy(
                extractionValues.SelectMany(value => value.CorrelationCandidates),
                value => value.Id.Value),
            DistinctBy(
                extractionValues.SelectMany(value => value.UnresolvedSourceAssertions),
                value => value.Id.Value),
            acquisition.Receipts,
            acquisition.Bindings)
        {
            SourceNativeLocationTypeAssertions = DistinctBy(
                values.SelectMany(value => value.Registration.SourceNativeLocationTypeAssertions),
                value => value.Id.Value),
            LocationSemanticClassificationAssertions = DistinctBy(
                values.SelectMany(value => value.Registration.LocationSemanticClassificationAssertions),
                value => value.Id.Value),
            RecordLifecycleAssertions = DistinctBy(
                values.SelectMany(value => value.Registration.RecordLifecycleAssertions),
                value => value.Id.Value),
            LocationCoverageReports = DistinctBy(
                extractionValues.SelectMany(value => value.LocationCoverageReports),
                value => value.Id.Value),
            SemanticClassificationAssertions = DistinctBy(
                values.SelectMany(value => value.SemanticClassificationAssertions),
                value => value.Id.Value),
            OrganizationalValueAssertions = DistinctBy(
                values.SelectMany(value => value.OrganizationalValueAssertions),
                value => value.Id.Value),
        };
    }

    private static AcquisitionClosure NarrowAcquisitionClosure(
        ImmutableArray<SourceArtifactRecord> artifacts,
        ImmutableArray<SourceAcquisitionReceipt> receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> bindings)
    {
        if (receipts.IsEmpty && bindings.IsEmpty) return new([], []);
        if (receipts.IsEmpty || bindings.IsEmpty)
            throw new InvalidDataException("Acquisition receipts and bindings must either both be absent or both be present.");

        var artifactIds = artifacts.Select(value => value.Id).ToHashSet();
        var receiptsById = receipts.ToDictionary(value => value.Id);
        var selectedBindings = bindings
            .Where(value => artifactIds.Contains(value.ArtifactId))
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (selectedBindings.Select(value => value.ArtifactId).Distinct().Count() != artifacts.Length ||
            selectedBindings.Length != artifacts.Length)
            throw new InvalidDataException("Every payload artifact requires exactly one acquisition binding.");

        var narrowedReceipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var narrowedBindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        foreach (var receiptGroup in selectedBindings.GroupBy(value => value.AcquisitionReceiptId)
                     .OrderBy(value => value.Key.Value, StringComparer.Ordinal))
        {
            if (!receiptsById.TryGetValue(receiptGroup.Key, out var receipt))
                throw new InvalidDataException("An acquisition binding references an absent receipt.");
            var members = receiptGroup.Select(binding =>
            {
                var member = receipt.Members.SingleOrDefault(value =>
                    value.ArtifactId == binding.ArtifactId &&
                    value.MemberCoordinate == binding.MemberCoordinate &&
                    value.ByteLength == binding.MemberByteLength &&
                    value.Digest == binding.MemberDigest);
                return member ?? throw new InvalidDataException(
                    "An acquisition binding does not match its exact receipt member.");
            }).ToImmutableArray();
            var id = SourceAcquisitionReceiptId.DeriveV1(
                receipt.ReceiptSchemaVersion,
                receipt.GameId,
                receipt.DistributionApplicationIdentity,
                receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate,
                receipt.ContainerByteLength,
                receipt.ContainerDigest,
                receipt.AcquisitionMethod,
                members);
            var narrowed = new SourceAcquisitionReceipt(
                id,
                receipt.ReceiptSchemaVersion,
                receipt.GameId,
                receipt.DistributionApplicationIdentity,
                receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate,
                receipt.ContainerByteLength,
                receipt.ContainerDigest,
                receipt.AcquisitionMethod,
                members);
            narrowedReceipts.Add(narrowed);
            narrowedBindings.AddRange(narrowed.Members.Select(member => new SourceArtifactAcquisitionBinding(
                member.ArtifactId,
                narrowed.Id,
                member.MemberCoordinate,
                member.ByteLength,
                member.Digest)));
        }

        return new(
            narrowedReceipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            narrowedBindings.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static ImmutableArray<T> DistinctBy<T>(
        IEnumerable<T> values,
        Func<T, string> identity)
    {
        var result = ImmutableArray.CreateBuilder<T>();
        var seen = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values.OrderBy(identity, StringComparer.Ordinal))
        {
            var id = identity(value);
            if (seen.TryGetValue(id, out var existing))
            {
                // Record equality compares ImmutableArray members by reference, so a persisted value and its
                // rebuilt twin differ there; only differing serialized content is a real conflict.
                if (!EqualityComparer<T>.Default.Equals(existing, value) &&
                    !System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(existing).AsSpan()
                        .SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value)))
                    throw new InvalidDataException($"Conflicting payload values share identity '{id}'.");
                continue;
            }
            seen.Add(id, value);
            result.Add(value);
        }
        return result.ToImmutable();
    }

    private sealed record AcquisitionClosure(
        ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);
}
