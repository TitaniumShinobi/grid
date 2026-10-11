using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>MDBO execution for Contract 2 relationship registration. Candidate-only; publication remains NOT_PUBLISHED.</summary>
public static class CanonicalRelationshipRegistrationMdboComposer
{
    public const string RegisterCandidateGoalCapability = "grid.registration.relationship.validate-graph";
    public const string ProjectNavigationGoalCapability = "grid.registration.relationship.project-navigation";
    public const string LocationContainmentGoalCapability = "grid.registration.location-relationship.resolve";

    public static IReadOnlyList<string> PlannedRegistrationStages() =>
        PlanForGoals(RegisterCandidateGoalCapability);

    public static IReadOnlyList<string> PlanForGoals(params string[] goalCapabilityIds) =>
        CanonicalMdboDependencyPlanner.PlanExecutionOrder(
            CanonicalMdboCapabilityRegistry.RelationshipRegistration,
            goalCapabilityIds);

    public static async Task<CanonicalRegistrationCandidate> RegisterCandidateAsync(
        IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources,
        RegistrationRuleSet rules,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        CanonicalRelationshipRegistrationRealizations.EnsureManifestBindings(
            CanonicalRelationshipRegistrationCapabilities.Load());
        var stages = PlannedRegistrationStages();
        if (stages[^1] != RegisterCandidateGoalCapability)
            throw new InvalidDataException("MDBO registration plan must terminate at graph validation.");

        var frozen = sources.Select(s => new RegistrationSourceArtifact(s.Source, s.Bytes.ToArray()))
            .OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();
        foreach (var item in frozen)
            if (item.Source.AdapterId != adapter.Id || item.Source.AdapterVersion != adapter.Version ||
                CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256)
                throw new InvalidDataException("Source bytes or adapter coordinates do not match the admitted manifest.");
        rules = System.Text.Json.JsonSerializer.Deserialize<RegistrationRuleSet>(CanonicalRegistrationEncoding.Bytes(rules), CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Mapping rules are empty.");

        cancellationToken.ThrowIfCancellationRequested();
        var evidence = await ExecuteDiscoverEvidenceAsync(adapter, frozen, cancellationToken).ConfigureAwait(false);
        if (frozen.Any(item => CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256))
            throw new InvalidDataException("An adapter modified its admitted source bytes.");

        var input = CanonicalRegistrationEngine.Normalize(new(
            frozen.Select(s => s.Source).ToArray(), rules, evidence));
        var candidate = ExecuteResolveAndValidate(input, existingPackages);
        if (candidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("MDBO registration must remain NOT_PUBLISHED.");
        return candidate;
    }

    public static async Task<RegistrationPreparedCandidate> PrepareNavigationAsync(
        string outputRoot,
        CanonicalRegistrationCandidate candidate,
        RegistrationNavigationContext context,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null,
        CancellationToken cancellationToken = default)
    {
        var stages = PlanForGoals(ProjectNavigationGoalCapability);
        if (stages[^1] != ProjectNavigationGoalCapability)
            throw new InvalidDataException("MDBO navigation plan must terminate at projection.");
        _ = stages;
        return await CanonicalRelationshipRegistrationRealizations.ProjectNavigationAsync(
            outputRoot, candidate, context, cancellationToken, existingPackages).ConfigureAwait(false);
    }

    public static LocationContainmentRegistrationResult ResolveLocationContainment(
        CanonicalCatalogPayload payload,
        ImmutableArray<LocationContainmentRegistrationClaim> claims)
    {
        var stages = PlanForGoals(LocationContainmentGoalCapability);
        if (!stages.Contains(LocationContainmentGoalCapability, StringComparer.Ordinal))
            throw new InvalidDataException("MDBO plan must include location containment resolution.");
        _ = stages;
        return CanonicalRelationshipRegistrationRealizations.ResolveLocationContainment(payload, claims);
    }

    private static async Task<RegistrationEvidenceSet> ExecuteDiscoverEvidenceAsync(
        IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources,
        CancellationToken cancellationToken) =>
        await CanonicalRelationshipRegistrationRealizations.DiscoverEvidenceAsync(adapter, sources, cancellationToken)
            .ConfigureAwait(false);

    private static CanonicalRegistrationCandidate ExecuteResolveAndValidate(
        RegistrationInput input,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages)
    {
        var candidate = CanonicalRelationshipRegistrationRealizations.ResolveCandidate(input, existingPackages);
        CanonicalRelationshipRegistrationRealizations.ValidateGraph(candidate);
        return candidate;
    }
}
