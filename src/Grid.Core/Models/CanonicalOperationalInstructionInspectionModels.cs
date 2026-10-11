namespace Grid.Core.Models;

/// <summary>Source-owned state; missing is never proof of absence.</summary>
public enum OperationalInstructionSectionPresence { Present, Missing, ProvenAbsence, EmptyUnresolved }

/// <summary>Optional canonical source closure for instruction-bearing registration artifacts.
/// The adapter's format coordinate is metadata, never a grammar selected by Core.</summary>
public sealed record RegistrationInstructionSource(
    CatalogSourceRecord Source, CatalogSourceRevisionRecord Revision, KnowledgeFormatCoordinate Format);

/// <summary>Exactly one canonical receipt class. No FILE/REFERENCE promotion.</summary>
public sealed record RegistrationInstructionReceipt(
    CatalogFileEvidenceReceipt? File, CatalogReferenceEvidenceReceipt? Reference);

/// <summary>Separate adapter-extracted source scope evidence. An instruction section cannot
/// serve as scope evidence. Scope is not a claim of compatibility or profile applicability.</summary>
public sealed record RegistrationInstructionScopeEvidence(
    string SourceId, KnowledgeSourceScope Scope, RegistrationInstructionReceipt Receipt);

/// <summary>Typed adapter output, using the canonical assertion/content/locator/binding contracts.
/// Requested applicability remains unresolved in this slice; text evidence never proves it.</summary>
public sealed record RegistrationInstructionClaim(
    string Id,
    string SourceId,
    InstructionAssertion Assertion,
    OperationalInstructionSectionPresence Presence,
    RegistrationInstructionReceipt Receipt,
    InstructionEvidenceBinding Binding,
    RegistrationInstructionScopeEvidence ScopeEvidence,
    RegistrationApplicability[] UnresolvedApplicability);

/// <summary>Candidate envelope around the existing canonical assertion, not another instruction model.</summary>
public sealed record RegisteredOperationalInstruction(
    RegistrationInstructionClaim Claim,
    RegistrationApplicability[] Applicability,
    CanonicalResolutionState ApplicabilityResolution);

public static class OperationalInstructionInspection
{
    public const string CategoryId = "GR-20";

    // Canonical framed encoding; the envelope adds observation state and provenance to the
    // existing assertion identity, whose algorithm and golden vectors remain unchanged.
    public static string ClaimId(RegistrationInstructionClaim claim)
    {
        var writer = new CanonicalIdentityWriter("registration-instruction", 1);
        writer.AddString("source", claim.SourceId);
        writer.AddString("assertion", claim.Assertion.Id.Value);
        writer.AddInt32("presence", (int)claim.Presence);
        writer.AddString("binding", claim.Binding.Id.Value);
        writer.AddString("scope-source", claim.ScopeEvidence.SourceId);
        writer.AddString("scope", CanonicalRegistrationEncoding.Digest(claim.ScopeEvidence.Scope));
        writer.AddString("scope-receipt", (claim.ScopeEvidence.Receipt.File?.Id ?? claim.ScopeEvidence.Receipt.Reference!.Id).Value);
        foreach (var value in NormalizeApplicability(claim.UnresolvedApplicability))
            writer.AddString("unresolved-applicability", CanonicalRegistrationEncoding.Digest(value));
        return writer.Derive();
    }

    internal static RegistrationApplicability[] NormalizeApplicability(RegistrationApplicability[] values) =>
        values.Select(value => value with { EvidenceIds = CanonicalRegistrationEncoding.Ordered(value.EvidenceIds) })
            .DistinctBy(CanonicalRegistrationEncoding.Digest)
            .OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray();

    public static RegistrationInstructionClaim Create(
        string sourceId, InstructionAssertion assertion, OperationalInstructionSectionPresence presence,
        RegistrationInstructionReceipt receipt, RegistrationInstructionScopeEvidence scopeEvidence,
        RegistrationApplicability[]? unresolvedApplicability = null)
    {
        var receiptId = receipt.File?.Id ?? receipt.Reference?.Id ??
            throw new InvalidDataException("Instruction requires an exact canonical receipt.");
        var basis = receipt.File is not null ? EvidenceVerificationKind.FileVerified : EvidenceVerificationKind.ReferenceVerified;
        var contentId = EvidenceClaimContentId.DeriveV1(assertion);
        var locator = assertion.SourceLocator.ExactFieldPathOrFragment;
        var binding = new InstructionEvidenceBinding(
            InstructionEvidenceBindingId.DeriveV3(receiptId, assertion.Id, assertion.SourceRevisionId, locator, contentId, basis),
            receiptId, assertion.Id, assertion.SourceRevisionId, locator, contentId, basis);
        var claim = new RegistrationInstructionClaim("", sourceId, assertion, presence, receipt, binding,
            scopeEvidence, NormalizeApplicability(unresolvedApplicability ?? []));
        return claim with { Id = ClaimId(claim) };
    }
}
