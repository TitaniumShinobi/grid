using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Contract 2 admission of adapter-normalized canonical instructions.
/// No source-format parsing, discovery, publication, or execution.</summary>
internal static class CanonicalRegistrationInstructionValidation
{
    internal static RegistrationInstructionClaim[]? Normalize(RegistrationInstructionClaim[]? claims) =>
        claims is null or { Length: 0 } ? null : claims.Select(claim => claim with
        {
            UnresolvedApplicability = OperationalInstructionInspection.NormalizeApplicability(claim.UnresolvedApplicability),
        }).OrderBy(claim => claim.Id, StringComparer.Ordinal).ToArray();

    internal static InstructionConflictGroup[]? Normalize(InstructionConflictGroup[]? conflicts) =>
        conflicts is null or { Length: 0 } ? null : conflicts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray();

    internal static RegisteredOperationalInstruction[]? Admit(RegistrationInput input, List<RegistrationRuling> rulings)
    {
        var claims = input.Evidence.Instructions ?? [];
        if (claims.Length > 256) throw new InvalidDataException("Instruction batch exceeds the bounded 256-claim limit.");
        var sources = input.Sources.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var assertionIds = new HashSet<InstructionAssertionId>();
        foreach (var claim in claims)
        {
            var a = claim.Assertion;
            if (!assertionIds.Add(a.Id) || !Enum.IsDefined(claim.Presence))
                throw new InvalidDataException("Duplicate or invalid instruction observation.");
            var content = a.Content;
            if (content.RetentionMode != InstructionRetentionMode.Verbatim || content.VerbatimText is null ||
                content.VerbatimText.Length > 1024 * 1024 || !content.StructuredFields.IsEmpty ||
                InstructionContent.FromVerbatim(content.VerbatimText).ContentDigest != content.ContentDigest)
                throw new InvalidDataException("Instruction content is not bounded exact verbatim content.");
            if (claim.Presence == OperationalInstructionSectionPresence.Missing && content.VerbatimText.Length != 0 ||
                claim.Presence == OperationalInstructionSectionPresence.EmptyUnresolved && !string.IsNullOrWhiteSpace(content.VerbatimText) ||
                claim.Presence == OperationalInstructionSectionPresence.Present && content.VerbatimText.Length == 0)
                throw new InvalidDataException("Instruction presence disagrees with retained content.");
            // Re-run canonical identity validation; never trust a serialized assertion ID.
            _ = new InstructionAssertion(a.Id, a.SourceRevisionId, a.SourceScope, a.NativeIdentity,
                a.CategoryId, a.CategoryMappingMethodId, a.CategoryMappingMethodVersion, a.ExactLanguageTag,
                InstructionContent.FromVerbatim(content.VerbatimText), a.SourceLocator,
                a.ApplicableRecordIds, a.ApplicablePackageIds);
            if (!a.ApplicableRecordIds.IsEmpty || !a.ApplicablePackageIds.IsEmpty)
                throw new InvalidDataException("Instruction target applicability needs its own canonical linkage proof.");

            var receipt = ValidateReceipt(claim.SourceId, claim.Receipt, sources);
            var locator = a.SourceLocator;
            if (receipt.Revision != a.SourceRevisionId || receipt.Artifact != locator.ArtifactId ||
                receipt.Object != locator.NativeObjectIdentity || receipt.RevisionIdentity != locator.ProviderObjectRevision ||
                receipt.Locator != locator.ExactFieldPathOrFragment)
                throw new InvalidDataException("Instruction assertion and exact source evidence differ.");

            var b = claim.Binding;
            if (b.Id.AlgorithmVersion != InstructionEvidenceBindingId.CurrentAlgorithmVersion ||
                b.EvidenceReceiptId != receipt.Id || b.InstructionAssertionId != a.Id ||
                b.SourceRevisionId != a.SourceRevisionId || b.ExactLocator != receipt.Locator ||
                b.Verification != receipt.Basis || b.ClaimContentId != EvidenceClaimContentId.DeriveV1(a) ||
                b.Id != InstructionEvidenceBindingId.DeriveV3(receipt.Id, a.Id, a.SourceRevisionId,
                    receipt.Locator, EvidenceClaimContentId.DeriveV1(a), receipt.Basis))
                throw new InvalidDataException("Instruction evidence binding failed exact claim verification.");

            var scope = claim.ScopeEvidence;
            var scopeReceipt = ValidateReceipt(scope.SourceId, scope.Receipt, sources);
            if (scope.SourceId != claim.SourceId || scopeReceipt.Revision != receipt.Revision ||
                scopeReceipt.Locator == receipt.Locator || scopeReceipt.Id == receipt.Id ||
                scopeReceipt.Object != receipt.Object ||
                CanonicalRegistrationEncoding.Digest(scope.Scope) != CanonicalRegistrationEncoding.Digest(a.SourceScope))
                throw new InvalidDataException("Instruction source scope requires independent exact metadata evidence.");
            foreach (var unresolved in claim.UnresolvedApplicability)
            {
                Require(unresolved.GameId);
                if (unresolved.ProfileId is not null) Require(unresolved.ProfileId);
                foreach (var id in unresolved.EvidenceIds) Require(id);
            }
            if (claim.Id != OperationalInstructionInspection.ClaimId(claim))
                throw new InvalidDataException("Instruction envelope identity does not match its observation/evidence.");

            var reason = claim.Presence switch
            {
                OperationalInstructionSectionPresence.Present => "instruction-section-admitted",
                OperationalInstructionSectionPresence.Missing => "instruction-section-missing",
                OperationalInstructionSectionPresence.ProvenAbsence => "instruction-section-absent",
                _ => "instruction-section-empty",
            };
            rulings.Add(new(claim.Id, "instruction",
                claim.Presence == OperationalInstructionSectionPresence.Present ? RegistrationOutcome.Correlated : RegistrationOutcome.Unresolved,
                reason, [receipt.Id.Value]));
            rulings.Add(new(claim.Id, "instruction", RegistrationOutcome.Unresolved,
                "instruction-applicability-unresolved", [scopeReceipt.Id.Value]));
        }
        var conflicts = input.Evidence.InstructionConflicts ?? [];
        var conflictIds = new HashSet<InstructionConflictGroupId>();
        foreach (var conflict in conflicts)
        {
            if (!conflictIds.Add(conflict.Id) ||
                conflict.MemberIds.Any(id => !assertionIds.Contains(id)) ||
                conflict.Id != InstructionConflictGroupId.DeriveV1(conflict.MemberIds, conflict.MethodId, conflict.MethodVersion))
                throw new InvalidDataException("Instruction conflict group is invalid or outside candidate closure.");
            foreach (var claim in claims.Where(value => conflict.MemberIds.Contains(value.Assertion.Id)))
                rulings.Add(new(claim.Id, "instruction", RegistrationOutcome.Ambiguous,
                    "instruction-guidance-conflicting", [claim.Binding.EvidenceReceiptId.Value]));
        }
        return claims.Length == 0 ? null : claims.Select(claim =>
            new RegisteredOperationalInstruction(claim, [], CanonicalResolutionState.Unresolved)).ToArray();
    }

    private sealed record ReceiptCoordinates(EvidenceReceiptId Id, CatalogSourceRevisionId Revision,
        SourceArtifactId Artifact, SourceNativeIdentifier? Object, SourceNativeVersion? RevisionIdentity,
        string Locator, EvidenceVerificationKind Basis);

    private static ReceiptCoordinates ValidateReceipt(string sourceId, RegistrationInstructionReceipt receipt,
        Dictionary<string, RegistrationSource> sources)
    {
        if (!sources.TryGetValue(sourceId, out var source) || source.InstructionSource is not { } closure)
            throw new InvalidDataException("Instruction evidence requires an admitted canonical source closure.");
        var digest = new ContentDigest("sha256", source.Sha256);
        var artifact = SourceArtifactId.DeriveV1(digest);
        var revision = closure.Revision;
        if (closure.Source.Kind != source.Kind ||
            closure.Source.Id != CatalogSourceId.DeriveV1(source.Kind, closure.Source.NativeIdentity) ||
            revision.SourceId != closure.Source.Id || revision.ArtifactIds.Length != 1 ||
            revision.ArtifactIds[0] != artifact ||
            revision.Id != CatalogSourceRevisionId.DeriveV1(revision.SourceId, revision.NativeRevision, revision.ArtifactIds))
            throw new InvalidDataException("Instruction canonical source/revision/digest closure is invalid.");
        Require(closure.Format.FormatId);
        Require(closure.Format.ExactFormatVersion);
        if ((receipt.File is null) == (receipt.Reference is null))
            throw new InvalidDataException("Exactly one FILE or REFERENCE receipt is required.");
        if (receipt.File is { } file)
        {
            var r = file.Receipt;
            if (source.Kind is KnowledgeSourceKind.ReferenceProvider or KnowledgeSourceKind.OfficialProvider or KnowledgeSourceKind.ModProvider ||
                file.Id != EvidenceReceiptId.DeriveV2(r) || r.SourceRevisionId != revision.Id ||
                r.ArtifactDigest != digest || r.SourceArtifactId != artifact ||
                r.ParserId != source.AdapterId || r.ParserVersion != source.AdapterVersion ||
                r.NativeObjectIdentity is null || r.NativeRecordLocator != r.NativeObjectIdentity.ExactRepresentation)
                throw new InvalidDataException("FILE evidence does not match its exact admitted source.");
            return new(file.Id, r.SourceRevisionId, artifact, r.NativeObjectIdentity, null,
                r.SourceFieldPath, EvidenceVerificationKind.FileVerified);
        }
        var reference = receipt.Reference!;
        var rr = reference.Receipt;
        if (source.Kind is not (KnowledgeSourceKind.ReferenceProvider or KnowledgeSourceKind.OfficialProvider or KnowledgeSourceKind.ModProvider) ||
            reference.Id != EvidenceReceiptId.DeriveV1(rr) || rr.ProviderCatalogSourceId != closure.Source.Id ||
            rr.SourceRevisionId != revision.Id || rr.ResponseArtifactId != artifact || rr.ResponseContentDigest != digest ||
            rr.NativeRevisionIdentity != revision.NativeRevision || rr.NativeObjectIdentity is null)
            throw new InvalidDataException("REFERENCE evidence does not match its exact admitted source.");
        return new(reference.Id, rr.SourceRevisionId, artifact, rr.NativeObjectIdentity, rr.NativeRevisionIdentity,
            rr.ResponseFieldPath, EvidenceVerificationKind.ReferenceVerified);
    }

    private static void Require(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Required instruction coordinate is empty.");
        CanonicalUtf8.Validate(value);
    }
}
