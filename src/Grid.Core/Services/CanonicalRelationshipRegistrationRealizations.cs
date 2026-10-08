using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Production realization bindings referenced by the relationship-registration capability manifest.</summary>
public static class CanonicalRelationshipRegistrationRealizations
{
    public const string DiscoverEvidence = "Grid.Core.Services.IRegistrationEvidenceAdapter.ExtractAsync";
    public const string ResolveEndpoints = "Grid.Core.Services.CanonicalRegistrationEngine.Evaluate";
    public const string ResolveCanonical = "Grid.Core.Services.CanonicalRegistrationEngine.Evaluate";
    public const string ValidateGraphRealization = "Grid.Core.Services.CanonicalRelationshipGraphValidator.Validate";
    public const string ProjectNavigation = "Grid.Core.Services.CanonicalRegistrationPreparationBuilder.BuildAsync";
    public const string ResolveLocationContainmentRealization =
        "Grid.Core.Services.CanonicalLocationRelationshipRegistrationEngine.Resolve";

    public static void EnsureManifestBindings(RelationshipRegistrationCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var capability in manifest.Capabilities)
        {
            if (!IsBound(capability.Realization))
                throw new InvalidDataException("Unbound MDBO realization: " + capability.Realization);
        }
    }

    public static bool IsBound(string realization) => realization switch
    {
        DiscoverEvidence or ResolveEndpoints or ResolveCanonical or ValidateGraphRealization or ProjectNavigation
            or ResolveLocationContainmentRealization => true,
        _ => false,
    };

    public static async Task<RegistrationEvidenceSet> DiscoverEvidenceAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.Equals(adapter.Id, CanonicalRelationshipRegistrationProductionBindings.ProductionEvidenceAdapterId, StringComparison.Ordinal))
            CanonicalRelationshipRegistrationProductionBindings.EnsureProductionEvidenceAdapter(adapter);
        _ = await adapter.DiscoverAsync(sources, cancellationToken).ConfigureAwait(false);
        return await adapter.ExtractAsync(sources, cancellationToken).ConfigureAwait(false);
    }

    public static CanonicalRegistrationCandidate ResolveCandidate(RegistrationInput input,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null) =>
        CanonicalRegistrationEngine.Evaluate(input, existingPackages);

    public static void ValidateGraph(CanonicalRegistrationCandidate candidate) =>
        CanonicalRelationshipGraphValidator.Validate(candidate);

    public static Task<RegistrationPreparedCandidate> ProjectNavigationAsync(string outputRoot,
        CanonicalRegistrationCandidate candidate, RegistrationNavigationContext context,
        CancellationToken cancellationToken = default, IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null) =>
        CanonicalRegistrationPreparationBuilder.BuildAsync(outputRoot, candidate, context, cancellationToken, existingPackages);

    public static LocationContainmentRegistrationResult ResolveLocationContainment(
        CanonicalCatalogPayload payload, ImmutableArray<LocationContainmentRegistrationClaim> claims) =>
        CanonicalLocationRelationshipRegistrationEngine.Resolve(payload, claims);
}
