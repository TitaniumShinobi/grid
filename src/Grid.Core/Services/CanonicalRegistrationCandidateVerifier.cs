using Grid.Core.Models;

namespace Grid.Core.Services;

public static class CanonicalRegistrationCandidateVerifier
{
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
