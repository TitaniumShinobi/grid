using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Contract 2 registration pipeline: entity claims → relationship claims → resolution → validation → nav preparation inputs.
/// Concrete MDBO realizations delegate into existing Contract 2 engines rather than duplicating logic.
/// </summary>
public static class CanonicalRelationshipRegistrationPipeline
{
    public static Task<CanonicalRegistrationCandidate> RegisterAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        CancellationToken cancellationToken = default)
        => CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(adapter, sources, rules, null, cancellationToken);

    public static Task<CanonicalRegistrationCandidate> RegisterWithLegacyAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        IReadOnlyList<CanonicalCatalogPackage> existingPackages, CancellationToken cancellationToken = default)
        => CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(adapter, sources, rules, existingPackages, cancellationToken);

    public static CanonicalRegistrationCandidate Evaluate(RegistrationInput input,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
    {
        var candidate = CanonicalRegistrationEngine.Evaluate(input, existingPackages);
        CanonicalRelationshipGraphValidator.Validate(candidate);
        return candidate;
    }

    public static Task<RegistrationPreparedCandidate> PrepareNavigationAsync(string outputRoot,
        CanonicalRegistrationCandidate candidate, RegistrationNavigationContext context,
        CancellationToken cancellationToken = default, IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
        => CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(
            outputRoot, candidate, context, existingPackages, cancellationToken);

    public static LocationContainmentRegistrationResult ResolveLocationContainment(
        CanonicalCatalogPayload payload, ImmutableArray<LocationContainmentRegistrationClaim> claims)
        => CanonicalRelationshipRegistrationMdboComposer.ResolveLocationContainment(payload, claims);
}
