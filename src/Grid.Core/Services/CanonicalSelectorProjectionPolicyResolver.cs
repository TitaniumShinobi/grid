using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Selects the canonical selector projection policy for a registered game identity.</summary>
public static class CanonicalSelectorProjectionPolicyResolver
{
    public static CanonicalSelectorProjectionPolicy ResolveForGame(GameId gameId, KnowledgeKind kind) =>
        kind == KnowledgeKind.Location && gameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId
            ? CanonicalSelectorProjectionPolicy.LocationPrepared : ResolveForGame(gameId);

    public static CanonicalSelectorProjectionPolicy ResolveForGame(GameId gameId) =>
        gameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
        gameId == ProductionGridCatalogService.GrandTheftAutoVLegacyId
            ? CanonicalSelectorProjectionPolicy.GtaEnhanced
            : CanonicalSelectorProjectionPolicy.Current;

    public static CanonicalSelectorProjectionPolicy ResolveForCatalogPackage(CanonicalCatalogPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return ResolveForGame(package.Manifest.GameScope.GameId);
    }

    public static bool IsSupportedPolicy(
        CanonicalSelectorProjectionPolicyId policyId,
        string exactVersion)
    {
        foreach (var policy in SupportedPolicies)
        {
            if (policy.Id == policyId &&
                string.Equals(policy.ExactVersion, exactVersion, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static bool MatchesGamePolicy(
        GameId gameId,
        CanonicalSelectorProjectionPolicyId policyId,
        string exactVersion)
    {
        var expected = ResolveForGame(gameId);
        return expected.Id == policyId &&
               string.Equals(expected.ExactVersion, exactVersion, StringComparison.Ordinal);
    }

    public static bool MatchesGamePolicy(GameId gameId, KnowledgeKind kind,
        CanonicalSelectorProjectionPolicyId policyId, string exactVersion)
    {
        var expected = ResolveForGame(gameId, kind);
        // Historical selections remain readable; the runtime validates their exact active generation.
        return (expected.Id == policyId && expected.ExactVersion == exactVersion) ||
               MatchesGamePolicy(gameId, policyId, exactVersion);
    }

    public static IEnumerable<CanonicalSelectorProjectionPolicy> SupportedPolicies
    {
        get
        {
            yield return CanonicalSelectorProjectionPolicy.V1;
            yield return CanonicalSelectorProjectionPolicy.V2;
            yield return CanonicalSelectorProjectionPolicy.V3;
            yield return CanonicalSelectorProjectionPolicy.V4;
            yield return CanonicalSelectorProjectionPolicy.GtaEnhanced;
            yield return CanonicalSelectorProjectionPolicy.LocationPrepared;
        }
    }
}
