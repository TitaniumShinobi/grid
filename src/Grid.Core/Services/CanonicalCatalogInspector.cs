using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Builds an immutable inspection projection. It has no persistence, mutation, approval, or publication authority.
/// </summary>
public sealed class CanonicalCatalogInspector
{
    public CanonicalCatalogInspectionReport Inspect(CanonicalCatalogPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var payload = package.Payload ?? throw new ArgumentException("Package payload is required.", nameof(package));
        var manifest = package.Manifest ?? throw new ArgumentException("Package manifest is required.", nameof(package));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        var revisions = payload.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var adapters = payload.AdapterDescriptors.ToDictionary(value => value.RevisionId);
        var artifacts = payload.Artifacts.ToDictionary(value => value.Id);
        var fileEvidence = payload.FileEvidenceReceipts.ToDictionary(value => value.Id);
        var referenceEvidence = payload.ReferenceEvidenceReceipts.ToDictionary(value => value.Id);

        var evidenceIds = payload.FileEvidenceReceipts.Select(value => value.Id)
            .Concat(payload.ReferenceEvidenceReceipts.Select(value => value.Id))
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var evidence = evidenceIds.Select(evidenceId =>
        {
            var hasFile = fileEvidence.TryGetValue(evidenceId, out var file);
            var hasReference = referenceEvidence.TryGetValue(evidenceId, out var reference);
            var sourceRevisionId = hasFile ? file!.Receipt.SourceRevisionId : reference!.Receipt.SourceRevisionId;
            var revision = revisions.TryGetValue(sourceRevisionId, out var foundRevision)
                ? foundRevision
                : throw new InvalidDataException("An inspection evidence receipt refers to an absent source revision.");
            var adapter = adapters.TryGetValue(revision.AdapterRevisionId, out var foundAdapter)
                ? foundAdapter
                : throw new InvalidDataException("An inspection source revision refers to an absent adapter descriptor.");
            var boundArtifacts = revision.Revision.ArtifactIds
                .Select(id => artifacts.TryGetValue(id, out var artifact)
                    ? artifact
                    : throw new InvalidDataException("An inspection source revision refers to an absent artifact."))
                .ToImmutableArray();
            if (hasFile == hasReference)
                throw new InvalidDataException("An inspection evidence ID must resolve to exactly one evidence receipt class.");
            return new CatalogInspectionEvidenceTrace(
                evidenceId,
                hasFile ? EvidenceVerificationKind.FileVerified : EvidenceVerificationKind.ReferenceVerified,
                file,
                reference,
                payload.EvidenceBindings.Where(value => value.EvidenceReceiptId == evidenceId)
                    .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
                payload.CorrelationEnvelopes.Where(value => value.SupportingEvidenceReceiptIds.Contains(evidenceId))
                    .Select(value => value.Id).OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray(),
                payload.UnresolvedSourceAssertions.Where(value => value.SupportingEvidenceReceiptIds.Contains(evidenceId))
                    .Select(value => value.Id).OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray(),
                revision,
                boundArtifacts,
                adapter);
        }).ToImmutableArray();

        var evidenceById = evidence.ToDictionary(value => value.EvidenceReceiptId);
        var records = payload.KnowledgeRecords.Select(record =>
        {
            var terms = payload.TerminologyAssertions
                .Where(value => value.KnowledgeRecordId == record.Id)
                .OrderBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var relationships = payload.RelationshipAssertions
                .Where(value => value.SubjectKnowledgeRecordId == record.Id)
                .OrderBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var bindings = payload.EvidenceBindings.Where(value => value.KnowledgeRecordId == record.Id)
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
            var correlations = payload.CorrelationEnvelopes
                .Where(value => value.Record.MemberIds.Contains(record.Id))
                .Select(value => value.Id)
                .OrderBy(value => value.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var evidenceStates = bindings
                .Select(value => evidenceById[value.EvidenceReceiptId].Verification)
                .Distinct()
                .Order()
                .ToImmutableArray();
            return new CatalogInspectionRecord(
                record, terms, relationships, bindings, correlations, evidenceStates, terms.IsEmpty);
        }).OrderBy(value => value.Record.Id.Value, StringComparer.Ordinal).ToImmutableArray();

        var relationships = payload.RelationshipAssertions.Select(assertion =>
        {
            var claimId = EvidenceClaimContentId.DeriveV1(assertion);
            var bindingIds = payload.EvidenceBindings.Where(binding =>
                    binding.ClaimKind == EvidenceClaimKind.Relationship &&
                    binding.KnowledgeRecordId == assertion.SubjectKnowledgeRecordId &&
                    binding.SourceRevisionId == assertion.SourceRevisionId &&
                    binding.ClaimContentId == claimId)
                .Select(binding => binding.Id)
                .OrderBy(value => value.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            return new CatalogInspectionRelationship(assertion, claimId, bindingIds);
        }).OrderBy(value => value.ClaimContentId.Value, StringComparer.Ordinal).ToImmutableArray();

        var conflicts = BuildConflicts(payload);
        var unresolved = BuildUnresolved(payload, records);
        var sources = payload.Sources.Select(source =>
        {
            var sourceRevisions = payload.SourceRevisions
                .Where(value => value.Revision.SourceId == source.Id)
                .OrderBy(value => value.Revision.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var sourceArtifacts = sourceRevisions.SelectMany(value => value.Revision.ArtifactIds)
                .Distinct()
                .OrderBy(value => value.Value, StringComparer.Ordinal)
                .Select(id => artifacts.TryGetValue(id, out var artifact)
                    ? artifact
                    : throw new InvalidDataException("An inspection source refers to an absent artifact."))
                .ToImmutableArray();
            return new CatalogInspectionSource(source, sourceRevisions, sourceArtifacts);
        }).OrderBy(value => value.Source.Id.Value, StringComparer.Ordinal).ToImmutableArray();

        var validation = new CatalogInspectionPackageValidation(
            manifest.PackageSchemaVersion,
            package.Id,
            manifest.CatalogRevisionId,
            manifest.PayloadDigest,
            manifest.ValidationStatus,
            manifest.ValidationPolicyId,
            manifest.ValidationPolicyVersion,
            manifest.ValidationResultDigest,
            verification.IsStructurallyValid,
            verification.Issues);

        var counts = Enum.GetValues<KnowledgeKind>()
            .ToImmutableDictionary(kind => kind, kind => records.Count(value => value.Record.Kind == kind));
        var summary = new CatalogInspectionSummary(
            manifest.GameScope.GameId,
            manifest.PackageKind,
            manifest.GameScope.ExactGameVersion,
            manifest.ModScope?.ExactModIdentity,
            manifest.ModScope?.ExactModVersion,
            manifest.EffectiveCoverage,
            package.Id,
            manifest.CatalogRevisionId,
            manifest.PayloadDigest,
            manifest.AdapterRevisions,
            counts,
            payload.TerminologyAssertions.Length,
            payload.RelationshipAssertions.Length,
            payload.CorrelationEnvelopes.Length,
            unresolved.Length,
            conflicts.Length,
            payload.FileEvidenceReceipts.Length,
            payload.ReferenceEvidenceReceipts.Length,
            payload.Sources.Length,
            payload.Artifacts.Length,
            verification.IsStructurallyValid,
            manifest.ValidationStatus);

        return new(summary, records, relationships, evidence, unresolved, conflicts, sources, validation);
    }

    private static ImmutableArray<CatalogInspectionUnresolvedItem> BuildUnresolved(
        CanonicalCatalogPayload payload,
        ImmutableArray<CatalogInspectionRecord> records)
    {
        var values = ImmutableArray.CreateBuilder<CatalogInspectionUnresolvedItem>();
        values.AddRange(payload.UnresolvedSourceAssertions.Select(value => new CatalogInspectionUnresolvedItem(
            CatalogInspectionUnresolvedKind.SourceAssertion,
            value.Id.Value,
            value.ReasonCode,
            SourceAssertion: value)));
        values.AddRange(payload.RelationshipAssertions
            .Where(value => value.Resolution == CanonicalResolutionState.Unresolved)
            .Select(value => new CatalogInspectionUnresolvedItem(
                CatalogInspectionUnresolvedKind.Relationship,
                EvidenceClaimContentId.DeriveV1(value).Value,
                value.SourceNativeTarget.ExactRepresentation,
                Relationship: value,
                KnowledgeRecordId: value.SubjectKnowledgeRecordId)));
        values.AddRange(payload.CorrelationEnvelopes
            .Where(value => value.Record.Outcome is CorrelationOutcome.Ambiguous or CorrelationOutcome.Unresolved)
            .Select(value => new CatalogInspectionUnresolvedItem(
                CatalogInspectionUnresolvedKind.Correlation,
                value.Id.Value,
                value.Record.Outcome.ToString(),
                Correlation: value)));
        values.AddRange(records.Where(value => value.IsTerminologyUnresolved)
            .Select(value => new CatalogInspectionUnresolvedItem(
                CatalogInspectionUnresolvedKind.MissingTerminology,
                value.Record.Id.Value,
                value.Record.NativeIdentity.ExactRepresentation,
                KnowledgeRecordId: value.Record.Id)));
        if (payload.EffectiveCoverage == KnowledgeCoverageState.Partial)
        {
            values.Add(new CatalogInspectionUnresolvedItem(
                CatalogInspectionUnresolvedKind.UnsupportedCoverage,
                "package.coverage",
                payload.EffectiveCoverage.ToString()));
        }
        return values.OrderBy(value => value.Kind).ThenBy(value => value.CanonicalCoordinate, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<CatalogInspectionConflict> BuildConflicts(CanonicalCatalogPayload payload)
    {
        var values = ImmutableArray.CreateBuilder<CatalogInspectionConflict>();
        foreach (var group in payload.TerminologyAssertions.GroupBy(value => (value.KnowledgeRecordId, value.Role)))
        {
            var assertions = group.Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).ToImmutableArray();
            if (assertions.Length > 1)
                values.Add(new(
                    CatalogInspectionConflictKind.TerminologyDisagreement,
                    $"{group.Key.KnowledgeRecordId.Value}:{group.Key.Role}",
                    assertions,
                    [group.Key.KnowledgeRecordId]));
        }

        foreach (var envelope in payload.CorrelationEnvelopes)
        {
            var memberIds = envelope.Record.MemberIds.ToHashSet();
            foreach (var roleGroup in payload.TerminologyAssertions
                         .Where(value => memberIds.Contains(value.KnowledgeRecordId))
                         .GroupBy(value => value.Role))
            {
                var assertions = roleGroup.Select(value => value.VerbatimValue)
                    .Distinct(StringComparer.Ordinal).ToImmutableArray();
                if (assertions.Length > 1)
                    values.Add(new(
                        CatalogInspectionConflictKind.TerminologyDisagreement,
                        $"{envelope.Id.Value}:{roleGroup.Key}",
                        assertions,
                        envelope.Record.MemberIds));
            }
        }

        foreach (var group in payload.RelationshipAssertions.GroupBy(value =>
                     (value.SubjectKnowledgeRecordId, value.SemanticId)))
        {
            var assertions = group.Select(value =>
                    value.ResolvedTargetKnowledgeRecordId?.Value ?? value.SourceNativeTarget.ExactRepresentation)
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            if (assertions.Length > 1)
                values.Add(new(
                    CatalogInspectionConflictKind.RelationshipDisagreement,
                    $"{group.Key.SubjectKnowledgeRecordId.Value}:{group.Key.SemanticId.Value}",
                    assertions,
                    [group.Key.SubjectKnowledgeRecordId]));
        }

        values.AddRange(payload.CorrelationEnvelopes
            .Where(value => value.Record.Outcome == CorrelationOutcome.Ambiguous)
            .Select(value => new CatalogInspectionConflict(
                CatalogInspectionConflictKind.AmbiguousCorrelation,
                value.Id.Value,
                value.Record.MemberIds.Select(member => member.Value).ToImmutableArray(),
                value.Record.MemberIds)));

        return values.OrderBy(value => value.Kind).ThenBy(value => value.CanonicalCoordinate, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
