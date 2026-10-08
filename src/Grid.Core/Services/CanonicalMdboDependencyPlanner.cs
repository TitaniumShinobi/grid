namespace Grid.Core.Services;

/// <summary>Deterministic capability execution order from semantic-parent dependencies (not lexical ordering).</summary>
public static class CanonicalMdboDependencyPlanner
{
    public static IReadOnlyList<string> PlanExecutionOrder(
        CanonicalMdboCapabilityRegistry registry,
        IEnumerable<string> goalCapabilityIds)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var goals = goalCapabilityIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(goalCapabilityIds));
        if (goals.Length == 0)
            throw new ArgumentException("At least one goal capability id is required.", nameof(goalCapabilityIds));

        var selected = new Dictionary<string, RelationshipRegistrationCapability>(StringComparer.Ordinal);
        foreach (var goalId in goals)
            CollectClosure(registry, goalId, selected);

        var remaining = new Dictionary<string, RelationshipRegistrationCapability>(selected, StringComparer.Ordinal);
        var ordered = new List<string>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(capability =>
                {
                    if (!registry.TryGetDependencyCapabilityId(capability, out var dependencyId))
                        return true;
                    return !remaining.ContainsKey(dependencyId!);
                })
                .OrderBy(c => c.CapabilityId, StringComparer.Ordinal)
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidDataException(
                    "MDBO capability dependency cycle: " +
                    string.Join(", ", remaining.Values.Select(c => c.CapabilityId).Order(StringComparer.Ordinal)));
            }

            foreach (var capability in ready)
            {
                ordered.Add(capability.CapabilityId);
                remaining.Remove(capability.CapabilityId);
            }
        }

        return ordered;
    }

    private static void CollectClosure(
        CanonicalMdboCapabilityRegistry registry,
        string capabilityId,
        Dictionary<string, RelationshipRegistrationCapability> selected)
    {
        if (selected.ContainsKey(capabilityId))
            return;
        var capability = registry.Require(capabilityId);
        selected.Add(capabilityId, capability);
        if (registry.TryGetDependencyCapabilityId(capability, out var dependencyId))
            CollectClosure(registry, dependencyId!, selected);
    }
}
