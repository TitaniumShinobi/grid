using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public static class CanonicalInstructionQueryService
{
    public static InstructionQueryResult Query(
        InstructionQuery query,
        ImmutableArray<InstructionAssertion> assertions,
        ImmutableArray<InstructionConflictGroup> conflictGroups,
        ImmutableArray<CatalogPackageId> activePackageIds,
        ImmutableArray<KnowledgeRecordId> applicableRecordIds,
        KnowledgeCoverageState coverageState)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.CategoryIds.IsDefault || assertions.IsDefault || conflictGroups.IsDefault ||
            activePackageIds.IsDefault || applicableRecordIds.IsDefault)
            throw new ArgumentException("Instruction query collections must be initialized.");
        var activePackages = activePackageIds.ToHashSet();
        var applicableRecords = applicableRecordIds.ToHashSet();
        var values = assertions.Where(value =>
                (query.CategoryIds.IsEmpty || value.CategoryId is { } category && query.CategoryIds.Contains(category)) &&
                (query.KnowledgeRecordId is null || value.ApplicableRecordIds.Contains(query.KnowledgeRecordId.Value)) &&
                (query.SourceRevisionId is null || value.SourceRevisionId == query.SourceRevisionId.Value) &&
                (query.ExactLanguageTag is null || string.Equals(value.ExactLanguageTag, query.ExactLanguageTag, StringComparison.Ordinal)) &&
                (value.ApplicablePackageIds.IsEmpty || value.ApplicablePackageIds.Any(activePackages.Contains)) &&
                (value.ApplicableRecordIds.IsEmpty || value.ApplicableRecordIds.Any(applicableRecords.Contains)))
            .OrderBy(value => value.CategoryId?.Value, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var ids = values.Select(value => value.Id).ToHashSet();
        var conflicts = query.IncludeConflicts
            ? conflictGroups.Where(value => value.MemberIds.Any(ids.Contains))
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray()
            : [];
        return new(query.CatalogRevisionId, query.CatalogCompositionId, values, conflicts, coverageState);
    }
}

public static class DeterministicCanonicalResolver
{
    public static DeterministicResolverResult Resolve(
        DeterministicResolverInput input,
        CanonicalCatalogPackage verifiedPackage,
        CanonicalApplicabilityProjection? applicability = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Policy);
        ArgumentNullException.ThrowIfNull(verifiedPackage);
        if (input.CandidateKnowledgeRecordIds.IsDefault || input.Features.IsDefault)
            throw new ArgumentException("Resolver candidate and evidence collections must be initialized.", nameof(input));
        if (input.Policy != DeterministicResolverPolicy.V1)
            throw new ArgumentException("The resolver v1 policy ID, version, thresholds, and weighting contract are immutable.", nameof(input));
        var verification = CanonicalCatalogPackageKernel.Verify(verifiedPackage);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException(
                $"Resolver inputs require a structurally verified catalog package: {string.Join("; ", verification.Issues)}");
        if (input.CatalogRevisionId != verifiedPackage.Manifest.CatalogRevisionId)
            throw new InvalidDataException("Resolver catalog revision does not match the verified package.");
        if (applicability is not null && applicability.CompositionId != input.CatalogCompositionId)
            throw new InvalidDataException("Resolver applicability does not match the pinned catalog composition.");

        var declaredCandidates = input.CandidateKnowledgeRecordIds.ToHashSet();
        if (input.Features.Any(value => !declaredCandidates.Contains(value.CandidateId)))
            throw new ArgumentException("Resolver evidence may refer only to explicitly proposed candidates.", nameof(input));
        var registeredRecords = verifiedPackage.Payload.KnowledgeRecords;
        var candidates = input.CandidateKnowledgeRecordIds
            .Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .Where(id => registeredRecords.Any(record => record.Id == id &&
                record.GameId == input.RequiredGameId && record.Kind == input.RequiredKnowledgeKind))
            .ToImmutableArray();
        var eligibleCandidateIds = candidates.ToImmutableHashSet();

        var scores = candidates.Select(candidate => Score(
                candidate,
                input.Features,
                input.Policy,
                input.CatalogCompositionId,
                applicability,
                verifiedPackage.Payload))
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.KnowledgeRecordId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var conflicts = FindAuthoritativeConflicts(
            input.Features,
            eligibleCandidateIds,
            input.CatalogCompositionId,
            applicability,
            verifiedPackage.Payload);
        var reason = conflicts.IsEmpty
            ? DetermineReason(scores, input.Policy)
            : DeterministicResolverUnresolvedReason.AuthoritativeIdentityConflict;
        var resolved = reason == DeterministicResolverUnresolvedReason.None ? scores[0].KnowledgeRecordId : (KnowledgeRecordId?)null;
        var outcome = resolved is null ? DeterministicResolverOutcome.Unresolved : DeterministicResolverOutcome.Resolved;
        var explanation = Explain(input.Policy, scores, reason, conflicts);
        return new(outcome, resolved, explanation);
    }

    private static ResolverCandidateScore Score(
        KnowledgeRecordId candidate,
        ImmutableArray<ResolverEvidenceFeature> features,
        DeterministicResolverPolicy policy,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection? applicability,
        CanonicalCatalogPayload payload)
    {
        if (features.IsDefault) throw new ArgumentException("Resolver features must be initialized.", nameof(features));
        var ordered = features.Where(value => value.CandidateId == candidate)
            .OrderByDescending(value => policy.Weight(value.Kind))
            .ThenBy(value => value.Kind)
            .ThenBy(value => value.ExactCoordinate, StringComparer.Ordinal)
            .ThenBy(value => string.Join("|", value.SupportingEvidenceReceiptIds.Select(item => item.Value)), StringComparer.Ordinal)
            .ToImmutableArray();
        var counted = ImmutableArray.CreateBuilder<ResolverEvidenceFeature>();
        var rejected = ImmutableArray.CreateBuilder<ResolverFeatureEvaluation>();
        var seenFeatures = new HashSet<string>(StringComparer.Ordinal);
        var usedReceipts = new HashSet<EvidenceReceiptId>();
        foreach (var feature in ordered)
        {
            var featureKey = FeatureKey(feature);
            if (!seenFeatures.Add(featureKey))
            {
                rejected.Add(new(feature, ResolverFeatureRejectionReason.DuplicateFeature));
                continue;
            }
            var rejection = ValidateFeature(feature, compositionId, applicability, payload);
            if (rejection != ResolverFeatureRejectionReason.None)
            {
                rejected.Add(new(feature, rejection));
                continue;
            }
            if (feature.SupportingEvidenceReceiptIds.Any(usedReceipts.Contains))
            {
                rejected.Add(new(feature, ResolverFeatureRejectionReason.DuplicateEvidence));
                continue;
            }
            counted.Add(feature);
            usedReceipts.UnionWith(feature.SupportingEvidenceReceiptIds);
        }
        var accepted = counted.ToImmutable();
        var weights = accepted.Select(value => policy.Weight(value.Kind)).ToImmutableArray();
        return new(candidate, weights.Sum(), weights.IsEmpty ? 0 : weights.Max(), accepted, rejected.ToImmutable());
    }

    private static ResolverFeatureRejectionReason ValidateFeature(
        ResolverEvidenceFeature feature,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection? applicability,
        CanonicalCatalogPayload payload)
    {
        var record = payload.KnowledgeRecords.Single(value => value.Id == feature.CandidateId);
        var receipts = feature.SupportingEvidenceReceiptIds.Select(id => FindReceipt(payload, id)).ToImmutableArray();
        if (receipts.Any(value => value is null))
            return ResolverFeatureRejectionReason.EvidenceReceiptNotFound;
        if (receipts.Any(value => !EvidenceClassMatches(feature.Kind, value!.Value.IsFile)))
            return ResolverFeatureRejectionReason.EvidenceClassMismatch;
        if (receipts.Any(value => !ReceiptSupportsCandidate(feature, record, value!.Value, compositionId, applicability, payload)))
            return ResolverFeatureRejectionReason.ReceiptNotBoundToCandidate;
        if (feature.Kind is ResolverEvidenceFeatureKind.ExactPrimaryTerminology or
            ResolverEvidenceFeatureKind.ExactAuthoritativeAlias)
            return ResolverFeatureRejectionReason.None;
        var expected = receipts.Select(value => ExpectedCoordinate(
                feature.Kind, record, value!.Value, compositionId, payload))
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (expected.Length != 1 || !string.Equals(expected[0], feature.ExactCoordinate, StringComparison.Ordinal))
            return ResolverFeatureRejectionReason.EvidenceCoordinateMismatch;
        return ResolverFeatureRejectionReason.None;
    }

    private static (bool IsFile, CatalogFileEvidenceReceipt? File, CatalogReferenceEvidenceReceipt? Reference)? FindReceipt(
        CanonicalCatalogPayload payload,
        EvidenceReceiptId id)
    {
        var file = payload.FileEvidenceReceipts.FirstOrDefault(value => value.Id == id);
        if (file is not null) return (true, file, null);
        var reference = payload.ReferenceEvidenceReceipts.FirstOrDefault(value => value.Id == id);
        return reference is null ? null : (false, null, reference);
    }

    private static bool EvidenceClassMatches(ResolverEvidenceFeatureKind kind, bool isFile) => kind switch
    {
        ResolverEvidenceFeatureKind.ExactArtifactRecordLocator or ResolverEvidenceFeatureKind.FileVerifiedIdentity => isFile,
        ResolverEvidenceFeatureKind.ExactProviderObjectRevision or ResolverEvidenceFeatureKind.ReferenceVerifiedIdentity => !isFile,
        _ => true,
    };

    private static bool ReceiptSupportsCandidate(
        ResolverEvidenceFeature feature,
        CanonicalKnowledgeRecord record,
        (bool IsFile, CatalogFileEvidenceReceipt? File, CatalogReferenceEvidenceReceipt? Reference) receipt,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection? applicability,
        CanonicalCatalogPayload payload)
    {
        if (feature.Kind == ResolverEvidenceFeatureKind.DeterministicCorrelation)
            return payload.CorrelationEnvelopes.Any(value =>
                value.Record.MemberIds.Contains(record.Id) &&
                value.SupportingEvidenceReceiptIds.Contains(receipt.File?.Id ?? receipt.Reference!.Id));

        var receiptId = receipt.File?.Id ?? receipt.Reference!.Id;
        if (feature.Kind is ResolverEvidenceFeatureKind.ExactPrimaryTerminology or
            ResolverEvidenceFeatureKind.ExactAuthoritativeAlias)
        {
            var requiredRole = feature.Kind == ResolverEvidenceFeatureKind.ExactPrimaryTerminology
                ? TerminologyAssertionRole.PrimaryName
                : TerminologyAssertionRole.Alias;
            return payload.TerminologyAssertions.Any(assertion =>
                assertion.KnowledgeRecordId == record.Id && assertion.Role == requiredRole &&
                string.Equals(assertion.VerbatimValue, feature.ExactCoordinate, StringComparison.Ordinal) &&
                payload.EvidenceBindings.Any(binding =>
                    binding.EvidenceReceiptId == receiptId &&
                    binding.ClaimKind == EvidenceClaimKind.Terminology &&
                    binding.KnowledgeRecordId == record.Id &&
                    binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion)));
        }

        if (feature.Kind == ResolverEvidenceFeatureKind.ExactApplicability &&
            (applicability is null || applicability.CompositionId != compositionId || !applicability.IsApplicable(record.Id)))
            return false;

        return payload.EvidenceBindings.Any(binding =>
            binding.EvidenceReceiptId == receiptId &&
            binding.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
            binding.KnowledgeRecordId == record.Id &&
            binding.SourceRevisionId == record.SourceRevisionId);
    }

    private static string ExpectedCoordinate(
        ResolverEvidenceFeatureKind kind,
        CanonicalKnowledgeRecord record,
        (bool IsFile, CatalogFileEvidenceReceipt? File, CatalogReferenceEvidenceReceipt? Reference) receipt,
        CatalogCompositionId compositionId,
        CanonicalCatalogPayload payload) => kind switch
    {
        ResolverEvidenceFeatureKind.ExactSourceNativeIdentity or
        ResolverEvidenceFeatureKind.ExactPluginRecordIdentity => record.NativeIdentity.ExactRepresentation,
        ResolverEvidenceFeatureKind.ExactProviderObjectRevision => receipt.Reference!.Id.Value,
        ResolverEvidenceFeatureKind.ExactArtifactRecordLocator => receipt.File!.Receipt.NativeRecordLocator,
        ResolverEvidenceFeatureKind.DeterministicCorrelation => payload.CorrelationEnvelopes.Single(value =>
            value.Record.MemberIds.Contains(record.Id) && value.SupportingEvidenceReceiptIds.Contains(receipt.File?.Id ?? receipt.Reference!.Id)).Id.Value,
        ResolverEvidenceFeatureKind.ExactGameVersion => record.GameVersion?.ExactRepresentation ?? string.Empty,
        ResolverEvidenceFeatureKind.ExactModIdentityVersion => record.SourceRevisionId.Value,
        ResolverEvidenceFeatureKind.ExactApplicability => compositionId.Value,
        ResolverEvidenceFeatureKind.FileVerifiedIdentity => receipt.File!.Id.Value,
        ResolverEvidenceFeatureKind.ReferenceVerifiedIdentity => receipt.Reference!.Id.Value,
        ResolverEvidenceFeatureKind.ExactPrimaryTerminology or
        ResolverEvidenceFeatureKind.ExactAuthoritativeAlias => throw new InvalidOperationException("Terminology coordinates are validated before coordinate derivation."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ImmutableArray<string> FindAuthoritativeConflicts(
        ImmutableArray<ResolverEvidenceFeature> features,
        ImmutableHashSet<KnowledgeRecordId> eligibleCandidateIds,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection? applicability,
        CanonicalCatalogPayload payload)
    {
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var authoritative = features.Where(value =>
                eligibleCandidateIds.Contains(value.CandidateId) &&
                IsAuthoritativeIdentity(value.Kind) &&
                IsCandidateBoundForConflict(value, compositionId, applicability, payload))
            .ToImmutableArray();
        foreach (var group in authoritative.SelectMany(feature => feature.SupportingEvidenceReceiptIds.Select(id => (id, feature)))
                     .GroupBy(value => (value.id, value.feature.Kind)))
        {
            var coordinates = group.Select(value => value.feature.ExactCoordinate)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (coordinates.Length > 1)
                conflicts.Add(
                    $"receipt:{group.Key.id.Value}:domain:{group.Key.Kind}:{string.Join("|", coordinates)}");
        }
        foreach (var group in authoritative.GroupBy(value => (value.CandidateId, value.Kind)))
        {
            var validCoordinates = group.Select(value => value.ExactCoordinate).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToImmutableArray();
            if (validCoordinates.Length > 1)
                conflicts.Add(
                    $"candidate:{group.Key.CandidateId.Value}:domain:{group.Key.Kind}:{string.Join("|", validCoordinates)}");
        }
        return conflicts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool IsCandidateBoundForConflict(
        ResolverEvidenceFeature feature,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection? applicability,
        CanonicalCatalogPayload payload)
    {
        var record = payload.KnowledgeRecords.FirstOrDefault(value => value.Id == feature.CandidateId);
        if (record is null) return false;
        foreach (var receiptId in feature.SupportingEvidenceReceiptIds)
        {
            var receipt = FindReceipt(payload, receiptId);
            if (receipt is null ||
                !EvidenceClassMatches(feature.Kind, receipt.Value.IsFile) ||
                !ReceiptSupportsCandidate(
                    feature,
                    record,
                    receipt.Value,
                    compositionId,
                    applicability,
                    payload))
                return false;
        }
        return true;
    }

    private static bool IsAuthoritativeIdentity(ResolverEvidenceFeatureKind kind) => kind is
        ResolverEvidenceFeatureKind.ExactSourceNativeIdentity or
        ResolverEvidenceFeatureKind.ExactPluginRecordIdentity or
        ResolverEvidenceFeatureKind.ExactProviderObjectRevision or
        ResolverEvidenceFeatureKind.ExactArtifactRecordLocator;

    private static string FeatureKey(ResolverEvidenceFeature feature) =>
        $"{(int)feature.Kind}\u001f{feature.ExactCoordinate}\u001f{string.Join("\u001e", feature.SupportingEvidenceReceiptIds.Select(value => value.Value))}";

    private static DeterministicResolverUnresolvedReason DetermineReason(
        ImmutableArray<ResolverCandidateScore> scores,
        DeterministicResolverPolicy policy)
    {
        if (scores.IsEmpty) return DeterministicResolverUnresolvedReason.NoEligibleCandidate;
        var first = scores[0];
        if (first.Score < policy.MinimumScore) return DeterministicResolverUnresolvedReason.BelowThreshold;
        if (first.StrongestFeatureWeight < policy.MinimumStrongFeatureWeight)
            return DeterministicResolverUnresolvedReason.NoStrongIdentity;
        if (scores.Length > 1 && first.Score == scores[1].Score) return DeterministicResolverUnresolvedReason.Tie;
        if (scores.Length > 1 && first.Score - scores[1].Score < policy.RequiredWinningMargin)
            return DeterministicResolverUnresolvedReason.InsufficientMargin;
        return DeterministicResolverUnresolvedReason.None;
    }

    private static DeterministicResolverExplanation Explain(
        DeterministicResolverPolicy policy,
        ImmutableArray<ResolverCandidateScore> scores,
        DeterministicResolverUnresolvedReason reason,
        ImmutableArray<string> authoritativeConflicts)
    {
        var writer = new CanonicalIdentityWriter("canonical-resolver-explanation", 1);
        writer.AddString("policy-id", policy.PolicyId);
        writer.AddString("policy-version", policy.ExactVersion);
        writer.AddInt32("minimum-score", policy.MinimumScore);
        writer.AddInt32("minimum-strong-feature", policy.MinimumStrongFeatureWeight);
        writer.AddInt32("required-margin", policy.RequiredWinningMargin);
        writer.AddInt32("reason", (int)reason);
        writer.AddInt32("candidate-count", scores.Length);
        for (var index = 0; index < scores.Length; index++)
        {
            var score = scores[index];
            writer.AddString($"candidate.{index}.id", score.KnowledgeRecordId.Value);
            writer.AddInt32($"candidate.{index}.score", score.Score);
            writer.AddInt32($"candidate.{index}.strongest", score.StrongestFeatureWeight);
            writer.AddInt32($"candidate.{index}.feature-count", score.CountedFeatures.Length);
            for (var featureIndex = 0; featureIndex < score.CountedFeatures.Length; featureIndex++)
            {
                var feature = score.CountedFeatures[featureIndex];
                writer.AddInt32($"candidate.{index}.feature.{featureIndex}.kind", (int)feature.Kind);
                writer.AddString($"candidate.{index}.feature.{featureIndex}.coordinate", feature.ExactCoordinate);
                writer.AddInt32($"candidate.{index}.feature.{featureIndex}.receipt-count", feature.SupportingEvidenceReceiptIds.Length);
                for (var receiptIndex = 0; receiptIndex < feature.SupportingEvidenceReceiptIds.Length; receiptIndex++)
                    writer.AddString($"candidate.{index}.feature.{featureIndex}.receipt.{receiptIndex}",
                        feature.SupportingEvidenceReceiptIds[receiptIndex].Value);
            }
            writer.AddInt32($"candidate.{index}.rejected-count", score.RejectedFeatures.Length);
            for (var rejectedIndex = 0; rejectedIndex < score.RejectedFeatures.Length; rejectedIndex++)
            {
                var rejected = score.RejectedFeatures[rejectedIndex];
                writer.AddInt32($"candidate.{index}.rejected.{rejectedIndex}.kind", (int)rejected.Feature.Kind);
                writer.AddString($"candidate.{index}.rejected.{rejectedIndex}.coordinate", rejected.Feature.ExactCoordinate);
                writer.AddInt32($"candidate.{index}.rejected.{rejectedIndex}.reason", (int)rejected.RejectionReason);
            }
        }
        writer.AddInt32("authoritative-conflict-count", authoritativeConflicts.Length);
        for (var index = 0; index < authoritativeConflicts.Length; index++)
            writer.AddString($"authoritative-conflict.{index}", authoritativeConflicts[index]);
        return new(
            policy.PolicyId, policy.ExactVersion, policy.MinimumScore,
            policy.MinimumStrongFeatureWeight, policy.RequiredWinningMargin,
            scores, reason, scores.IsEmpty ? null : scores[0].Score,
            scores.Length < 2 ? null : scores[1].Score, authoritativeConflicts, writer.Derive());
    }
}

public static class CanonicalModelProposalValidator
{
    public static CanonicalProposalValidationResult Validate(
        ModelCanonicalProposal proposal,
        ImmutableArray<CanonicalKnowledgeRecord> registeredRecords)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (registeredRecords.IsDefault) throw new ArgumentException("Registered records must be initialized.", nameof(registeredRecords));
        if (proposal.VerbatimInputSpanReferences.IsDefault || proposal.ProposedExistingKnowledgeRecordIds.IsDefault)
            throw new ArgumentException("Model proposal collections must be initialized.", nameof(proposal));
        var requested = proposal.ProposedExistingKnowledgeRecordIds.Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var valid = requested.Where(id => registeredRecords.Any(value =>
            value.Id == id && value.Kind == proposal.RequestedKnowledgeKind)).ToImmutableArray();
        var validSet = valid.ToHashSet();
        return new(proposal.ProposalId, valid, requested.Where(value => !validSet.Contains(value)).ToImmutableArray());
    }
}
