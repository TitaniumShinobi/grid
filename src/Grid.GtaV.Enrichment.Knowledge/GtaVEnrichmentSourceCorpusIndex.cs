using System.Collections.Concurrent;
using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.GtaV.Knowledge;

namespace Grid.GtaV.Enrichment.Knowledge;

/// <summary>Shared immutable source index for the GTA enrichment adapters.</summary>
public sealed class GtaVEnrichmentSourceCorpusIndex
{
    private readonly ConcurrentDictionary<string, Lazy<ImmutableArray<GtaVEnrichmentParsing.PopulationZone>>> _populationZones =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _populationParseCounts = new(StringComparer.Ordinal);

    public GtaVEnrichmentSourceCorpusIndex(IEnumerable<FrozenSourceArtifact> artifacts)
        : this(new GtaVSupportedSourceCorpusIndex(artifacts))
    {
    }

    public GtaVEnrichmentSourceCorpusIndex(GtaVSupportedSourceCorpusIndex sources) =>
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));

    public GtaVSupportedSourceCorpusIndex Sources { get; }

    internal ImmutableArray<GtaVEnrichmentParsing.PopulationZone> GetPopulationZones(FrozenSourceArtifact artifact)
    {
        // GetStrictUtf8 performs the complete artifact/coordinate/format/byte revalidation before
        // the cache key can be used. SourceArtifactId alone is deliberately insufficient.
        _ = Sources.GetStrictUtf8(artifact, GtaVPopulationZonesKnowledgeAdapter.MaximumArtifactBytes);
        var key = string.Join('\n', artifact.Id.Value, artifact.Digest.ToString(),
            artifact.SourceCoordinate.Namespace, artifact.SourceCoordinate.ObjectType,
            artifact.SourceCoordinate.ExactRepresentation,
            Convert.ToHexString(artifact.SourceCoordinate.IdentityBytes.AsSpan()),
            artifact.SourceCoordinate.ComparisonMethodId,
            artifact.SourceCoordinate.ComparisonMethodVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            artifact.DeclaredFormat.FormatId, artifact.DeclaredFormat.ExactFormatVersion);
        return _populationZones.GetOrAdd(key, _ => new Lazy<ImmutableArray<GtaVEnrichmentParsing.PopulationZone>>(
            () =>
            {
                _populationParseCounts.AddOrUpdate(key, 1, (_, count) => checked(count + 1));
                return GtaVEnrichmentParsing.ParsePopulationZonesUncached(artifact, Sources);
            },
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public int GetPopulationZoneParseCount(FrozenSourceArtifact artifact)
    {
        _ = Sources.GetStrictUtf8(artifact, GtaVPopulationZonesKnowledgeAdapter.MaximumArtifactBytes);
        var key = string.Join('\n', artifact.Id.Value, artifact.Digest.ToString(),
            artifact.SourceCoordinate.Namespace, artifact.SourceCoordinate.ObjectType,
            artifact.SourceCoordinate.ExactRepresentation,
            Convert.ToHexString(artifact.SourceCoordinate.IdentityBytes.AsSpan()),
            artifact.SourceCoordinate.ComparisonMethodId,
            artifact.SourceCoordinate.ComparisonMethodVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            artifact.DeclaredFormat.FormatId, artifact.DeclaredFormat.ExactFormatVersion);
        return _populationParseCounts.TryGetValue(key, out var count) ? count : 0;
    }
}
