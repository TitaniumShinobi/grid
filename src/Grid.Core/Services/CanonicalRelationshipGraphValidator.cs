using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Fail-closed graph validation for registered canonical relationship edges.</summary>
public static class CanonicalRelationshipGraphValidator
{
    public static void Validate(CanonicalRegistrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var entities = candidate.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var hierarchy = candidate.Relationships
            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated &&
                        CanonicalRelationshipRegistrationSemantics.IsNavigationParentage(r.Semantic))
            .Select(r => (Edge: r, Child: r.SubjectId, Parent: r.TargetId))
            .ToArray();

        var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in hierarchy)
        {
            if (edge.Child == edge.Parent)
                throw new InvalidDataException("Graph validation rejected self-parenting: " + edge.Edge.Id);
            if (!entities.ContainsKey(edge.Child) || !entities.ContainsKey(edge.Parent))
                throw new InvalidDataException("Graph validation rejected missing endpoint: " + edge.Edge.Id);
            var child = entities[edge.Child];
            var parent = entities[edge.Parent];
            if (child.Selector != parent.Selector)
                throw new InvalidDataException("Graph validation rejected cross-selector parentage: " + edge.Edge.Id);
            if (child.GameId is not null && parent.GameId is not null && child.GameId != parent.GameId)
                throw new InvalidDataException("Graph validation rejected cross-game parentage: " + edge.Edge.Id);
            var key = edge.Child + "\0" + CanonicalRelationshipRegistrationSemantics.Normalize(edge.Edge.Semantic) + "\0" + edge.Parent;
            if (!duplicateKeys.Add(key))
                throw new InvalidDataException("Graph validation rejected duplicate edge: " + edge.Edge.Id);
        }

        var parentsByChild = hierarchy.GroupBy(e => e.Child).ToDictionary(g => g.Key, g => g.Select(x => x.Parent).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (complete.Contains(id)) return;
            if (!visiting.Add(id))
                throw new InvalidDataException("Graph validation rejected a cycle at: " + id);
            if (parentsByChild.TryGetValue(id, out var parents))
                foreach (var parent in parents) Visit(parent);
            visiting.Remove(id);
            complete.Add(id);
        }
        foreach (var id in entities.Keys) Visit(id);

        foreach (var edge in candidate.Relationships.Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated))
        {
            if (CanonicalRelationshipRegistrationSemantics.Normalize(edge.Semantic) == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership)
            {
                if (CanonicalRelationshipRegistrationSemantics.IsNavigationParentage(edge.Semantic))
                    throw new InvalidDataException("Alternate-view membership cannot be treated as containment.");
                continue;
            }
            if (!entities.ContainsKey(edge.SubjectId))
                throw new InvalidDataException("Admitted relationship lacks a resolved subject: " + edge.Id);
            if (!entities.ContainsKey(edge.TargetId))
                throw new InvalidDataException("Admitted relationship lacks a resolved target: " + edge.Id);
        }
    }
}
