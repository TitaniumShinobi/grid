using Grid.Core.Models;

namespace Grid.Core.Services;

public static class CanonicalRegistrationCandidateVerifier
{
    /// <summary>Replays the adapter against externally admitted bytes/revisions. This is the
    /// source-authenticity check; structural Verify alone does not authenticate an edited input manifest.</summary>
    public static async Task VerifyAgainstSourcesAsync(CanonicalRegistrationCandidate candidate,
        IRegistrationEvidenceAdapter adapter, IReadOnlyList<RegistrationSourceArtifact> sources,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null,
        CancellationToken cancellationToken = default)
    {
        Verify(candidate, existingPackages);
        var expected = await CanonicalRegistrationEngine.RegisterWithLegacyAsync(
            adapter, sources, candidate.Input.RuleSet, existingPackages ?? [], cancellationToken).ConfigureAwait(false);
        if (!CanonicalRegistrationEncoding.Bytes(candidate).AsSpan().SequenceEqual(CanonicalRegistrationEncoding.Bytes(expected)))
            throw new InvalidDataException("Candidate differs from adapter replay of admitted source evidence.");
    }

    public static void Verify(CanonicalRegistrationCandidate candidate, IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.FormatVersion != 1 || candidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("Unsupported or published registration candidate.");
        var normalized = CanonicalRegistrationEngine.Normalize(candidate.Input);
        var expected = CanonicalRegistrationEngine.Evaluate(normalized, existingPackages);
        if (!CanonicalRegistrationEncoding.Bytes(candidate).AsSpan().SequenceEqual(CanonicalRegistrationEncoding.Bytes(expected)))
            throw new InvalidDataException("Candidate identities, evidence, assertions, rulings or coordinates failed deterministic verification.");
    }
}
