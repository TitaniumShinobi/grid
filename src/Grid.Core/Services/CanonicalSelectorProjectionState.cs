using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Diagnostics;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Opt-in, process-local observations for the canonical runtime benchmark.</summary>
public static class CanonicalRuntimeDiagnostics
{
    private static readonly AsyncLocal<Observer?> Current = new();

    public static IDisposable Observe(
        Action<CanonicalRuntimeStageMeasurement> stages,
        Action<CanonicalSelectorGraphSnapshot>? graphs = null)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var previous = Current.Value;
        Current.Value = new(stages, graphs);
        return new ObservationScope(previous);
    }

    public static IDisposable Measure(string stage) => Current.Value is { } observer
        ? new MeasurementScope(stage, observer.Stages)
        : EmptyScope.Instance;

    internal static void CaptureGraph(KnowledgeKind kind, bool includeIdentifierOnly, bool inspectionMode,
        CanonicalTerminologyLocalePreference locale, Func<ImmutableArray<CanonicalSelectorGraphEntry>> materialize) =>
        Current.Value?.Graphs?.Invoke(new(kind, includeIdentifierOnly, inspectionMode, locale, materialize));

    private sealed record Observer(Action<CanonicalRuntimeStageMeasurement> Stages,
        Action<CanonicalSelectorGraphSnapshot>? Graphs);

    private sealed class ObservationScope(Observer? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class MeasurementScope(string stage, Action<CanonicalRuntimeStageMeasurement> observer) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly long allocated = GC.GetTotalAllocatedBytes(false);
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            observer(new(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocated));
        }
    }
}

public sealed record CanonicalRuntimeStageMeasurement(string Stage, double ElapsedMilliseconds, long AllocatedBytes);

public sealed record CanonicalSelectorGraphSnapshot(KnowledgeKind KnowledgeKind, bool IncludeIdentifierOnly,
    bool InspectionMode, CanonicalTerminologyLocalePreference TerminologyLocale,
    Func<ImmutableArray<CanonicalSelectorGraphEntry>> Materialize);

public sealed record CanonicalSelectorGraphEntry(CanonicalNavigationNode Node,
    ImmutableArray<CanonicalNavigationPathId> OrderedChildPathIds);

/// <summary>Indexes share the validated input's immutable assertions; no canonical content is copied or changed.</summary>
internal sealed class CanonicalSelectorProjectionState
{
    private const int MaximumGraphs = 8;
    private readonly object graphGate = new();
    private readonly Dictionary<CanonicalProjectionGraphKey, (CanonicalProjectionGraph Graph,
        LinkedListNode<CanonicalProjectionGraphKey> Usage)> graphs = [];
    private readonly LinkedList<CanonicalProjectionGraphKey> usage = new();
    private readonly FrozenDictionary<KnowledgeKind, ImmutableArray<CanonicalKnowledgeRecord>> records;
    private readonly FrozenDictionary<KnowledgeRecordId, ImmutableArray<TerminologyAssertion>> terminology;
    private readonly FrozenDictionary<KnowledgeRecordId, ImmutableHashSet<CanonicalSemanticRoleId>> roles;
    private readonly FrozenDictionary<KnowledgeRecordId, ImmutableArray<CanonicalRecordContributionAssertion>> contributions;
    private readonly FrozenDictionary<KnowledgeRecordId, ImmutableArray<CanonicalOrganizationalValueAssertion>> organization;

    public CanonicalSelectorProjectionState(CanonicalSelectorProjectionInput input)
    {
        using var measurement = CanonicalRuntimeDiagnostics.Measure("projection.index-build");
        var applicable = input.Applicability.ApplicableKnowledgeRecordIds.ToHashSet();
        var excluded = input.ContributionAssertions.Where(value =>
            value.ContributionKind is CanonicalRecordContributionKind.Deleted or CanonicalRecordContributionKind.Modified)
            .Select(value => value.KnowledgeRecordId).ToHashSet();
        records = input.KnowledgeRecords.Where(value => applicable.Contains(value.Id) && !excluded.Contains(value.Id))
            .GroupBy(value => value.Kind).ToFrozenDictionary(group => group.Key,
                group => group.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray());
        terminology = input.TerminologyAssertions.GroupBy(value => value.KnowledgeRecordId)
            .ToFrozenDictionary(group => group.Key, group => group.OrderBy(value => value.Role)
                .ThenBy(value => value.VerbatimValue, StringComparer.Ordinal)
                .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
                .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal).ToImmutableArray());
        roles = input.SemanticClassifications.GroupBy(value => value.KnowledgeRecordId)
            .ToFrozenDictionary(group => group.Key, group => group.Select(value => value.RoleId).ToImmutableHashSet());
        contributions = input.ContributionAssertions.GroupBy(value => value.KnowledgeRecordId)
            .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
        organization = input.OrganizationalValueAssertions.GroupBy(value => value.KnowledgeRecordId)
            .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
        EstablishedZones = input.VerifiedPackage.Payload.LocationSemanticClassificationAssertions
            .Where(value => value.RoleId.Value == "grid.location.role.area-zone")
            .Select(value => value.KnowledgeRecordId).ToFrozenSet();
        StrictLocationRelationships = input.RelationshipAssertions
            .Where(value => LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)).ToImmutableArray();
    }

    public FrozenSet<KnowledgeRecordId> EstablishedZones { get; }
    public ImmutableArray<RelationshipAssertion> StrictLocationRelationships { get; }
    public ImmutableArray<CanonicalKnowledgeRecord> Records(KnowledgeKind kind) => records.GetValueOrDefault(kind, []);
    public ImmutableArray<TerminologyAssertion> Terminology(KnowledgeRecordId id) => terminology.GetValueOrDefault(id, []);
    public ImmutableHashSet<CanonicalSemanticRoleId> Roles(KnowledgeRecordId id) =>
        roles.GetValueOrDefault(id, ImmutableHashSet<CanonicalSemanticRoleId>.Empty);
    public ImmutableArray<CanonicalRecordContributionAssertion> Contributions(KnowledgeRecordId id) =>
        contributions.GetValueOrDefault(id, []);
    public ImmutableArray<CanonicalOrganizationalValueAssertion> Organization(KnowledgeRecordId id) =>
        organization.GetValueOrDefault(id, []);

    public CanonicalProjectionGraph GetGraph(CanonicalProjectionGraphKey key, Func<CanonicalProjectionGraph> create)
    {
        lock (graphGate)
        {
            if (graphs.TryGetValue(key, out var existing))
            {
                usage.Remove(existing.Usage);
                usage.AddLast(existing.Usage);
                return existing.Graph;
            }
            // Publish only a completely built graph. Failures leave no cache entry.
            var graph = create();
            if (graphs.Count == MaximumGraphs)
            {
                var oldest = usage.First!;
                graphs.Remove(oldest.Value);
                usage.RemoveFirst();
            }
            graphs.Add(key, (graph, usage.AddLast(key)));
            return graph;
        }
    }
}

internal readonly record struct CanonicalProjectionGraphKey(KnowledgeKind Kind, bool IncludeIdentifierOnly,
    bool InspectionMode, string RequestedLocale, string OrderedFallbacks)
{
    public static CanonicalProjectionGraphKey Create(KnowledgeKind kind, bool includeIdentifierOnly,
        bool inspectionMode, CanonicalTerminologyLocalePreference locale) =>
        new(kind, includeIdentifierOnly, inspectionMode, locale.RequestedLanguageTag,
            string.Join("\n", locale.ApprovedLanguageFallbackTags));
}

internal sealed record CanonicalProjectionGraphNode(CanonicalNavigationNode Node,
    ImmutableArray<CanonicalNavigationNode> Children);

internal sealed class CanonicalProjectionGraph(CanonicalNavigationPathId rootPath,
    FrozenDictionary<CanonicalNavigationPathId, CanonicalProjectionGraphNode> byPath)
{
    public CanonicalNavigationPathId RootPath { get; } = rootPath;
    public FrozenDictionary<CanonicalNavigationPathId, CanonicalProjectionGraphNode> ByPath { get; } = byPath;
    public ImmutableArray<CanonicalSelectorGraphEntry> Snapshot() => ByPath.Values
        .OrderBy(value => value.Node.PathId.Value, StringComparer.Ordinal)
        .Select(value => new CanonicalSelectorGraphEntry(value.Node,
            value.Children.Select(child => child.PathId).ToImmutableArray())).ToImmutableArray();
}
