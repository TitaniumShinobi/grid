using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>Receipt-bound decoded YND nodes. Display text is always read from exact GXT2 bytes.</summary>
public sealed class GtaVRouteCorpusIndex
{
    public const string SchemaId = "grid.gta-v.route-corpus-index";
    public const int SchemaVersion = 1;
    public const string DecoderMethodId = "fivefury.gta-v.update-paths-named-routes";
    public const string DecoderVersion = "0.5.1";
    public const string DecoderArtifactSha256 = "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c";
    public const string YndFormatId = "rockstar.gta-v.ynd-rsc7";
    public const string GxtFormatId = "rockstar.gta-v.gxt2-binary";
    public const string RpfFormatId = "rockstar.rpf7-container";
    public const string PathsCoordinate = "update/update.rpf!/x64/levels/gta5/paths.rpf";
    public const string LanguageCoordinate = "x64b.rpf!/data/lang/american_rel.rpf";
    public const string GxtCoordinate = LanguageCoordinate + "!/global.gxt2";
    public const string Scope = "bounded-update-paths-snapshot-not-effective-mounted-road-registry";
    public const string Locale = "en-US";
    private const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private GtaVRouteCorpusIndex(ImmutableArray<FrozenSourceArtifact> artifacts,
        ImmutableArray<GtaVRouteNodeSource> sources, FrozenSourceArtifact gxt,
        ImmutableDictionary<uint, GtaVIndexedGxt2Entry> terms, ContentDigest digest)
    {
        Artifacts = artifacts;
        Ynds = sources;
        Gxt = gxt;
        Terminology = terms;
        SidecarDigest = digest;
        var hashes = sources.SelectMany(s => s.Nodes).Select(n => n.StreetNameHash).Where(h => h != 0).ToImmutableHashSet();
        NamedHashes = hashes.Where(h => terms.TryGetValue(h, out var term) && IsPlayerText(term.Text)).Order().ToImmutableArray();
        UnmatchedHashes = hashes.Except(NamedHashes).Order().ToImmutableArray();
        Metrics = new GtaVRouteMetrics(sources.Length, sources.Sum(s => s.Nodes.Length), hashes.Count,
            NamedHashes.Length, UnmatchedHashes.Length, sources.Sum(s => s.Nodes.Count(n => n.StreetNameHash == 0)));
    }

    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }
    public ImmutableArray<GtaVRouteNodeSource> Ynds { get; }
    public FrozenSourceArtifact Gxt { get; }
    public ImmutableDictionary<uint, GtaVIndexedGxt2Entry> Terminology { get; }
    public ImmutableArray<uint> NamedHashes { get; }
    public ImmutableArray<uint> UnmatchedHashes { get; }
    public GtaVRouteMetrics Metrics { get; }
    public ContentDigest SidecarDigest { get; }
    public static bool IsPlayerText(string text) => !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);

    public static GtaVRouteCorpusIndex Load(ReadOnlySpan<byte> sidecar,
        IEnumerable<FrozenSourceArtifact> frozenArtifacts, GtaVRouteCorpusIndexReceiptBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var digest = ContentDigest.ComputeSha256(sidecar);
        Require(sidecar.Length is > 0 and <= MaximumBytes && binding.SchemaId == SchemaId && binding.SchemaVersion == 1 &&
            binding.DecoderMethodId == DecoderMethodId && binding.DecoderVersion == DecoderVersion &&
            binding.DecoderArtifactSha256 == DecoderArtifactSha256 && binding.ByteLength == sidecar.Length && binding.Digest == digest,
            "Route index differs from its exact acquisition/decoder binding.");
        try
        {
            _ = StrictUtf8.GetString(sidecar);
            using var document = JsonDocument.Parse(sidecar.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            ValidateJson(root);
            Properties(root, "schemaId", "schemaVersion", "decoder", "locale", "scope", "artifacts", "ynds", "gxtCoordinate", "counts");
            Require(Text(root, "schemaId") == SchemaId && root.GetProperty("schemaVersion").GetInt32() == 1 &&
                Text(root, "locale") == Locale && Text(root, "scope") == Scope && Text(root, "gxtCoordinate") == GxtCoordinate,
                "Unsupported route index scope, schema, or locale.");
            var decoder = root.GetProperty("decoder");
            Properties(decoder, "methodId", "exactVersion", "artifactSha256");
            Require(Text(decoder, "methodId") == DecoderMethodId && Text(decoder, "exactVersion") == DecoderVersion &&
                Text(decoder, "artifactSha256") == DecoderArtifactSha256, "Route decoder identity mismatch.");
            var supplied = frozenArtifacts.ToImmutableArray();
            Require(!supplied.IsDefaultOrEmpty && supplied.Length <= 1024 &&
                supplied.Select(a => a.Id).Distinct().Count() == supplied.Length &&
                supplied.Select(a => a.SourceCoordinate.ExactRepresentation).Distinct(StringComparer.Ordinal).Count() == supplied.Length,
                "Route corpus requires distinct exact artifacts.");
            var byCoordinate = supplied.ToDictionary(a => a.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("artifacts").EnumerateArray())
            {
                Properties(item, "sourceCoordinate", "byteLength", "sha256", "formatId", "formatVersion");
                var coordinate = Text(item, "sourceCoordinate");
                Require(declared.Add(coordinate) && byCoordinate.ContainsKey(coordinate), "Route artifact is duplicated or unacquired.");
                var artifact = byCoordinate[coordinate];
                var format = coordinate is PathsCoordinate or LanguageCoordinate ? RpfFormatId : coordinate == GxtCoordinate ? GxtFormatId : YndFormatId;
                Require(coordinate is PathsCoordinate or LanguageCoordinate or GxtCoordinate ||
                    Regex.IsMatch(coordinate, "^" + Regex.Escape(PathsCoordinate) + @"!/nodes[0-9]+\.ynd$", RegexOptions.CultureInvariant),
                    "Route artifact lies outside the admitted source family.");
                Require(artifact.ExactBytes.Length is > 0 and <= MaximumBytes &&
                    artifact.ExactBytes.Length == item.GetProperty("byteLength").GetInt64() &&
                    artifact.Digest == ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) &&
                    artifact.Digest == new ContentDigest(ContentDigest.Sha256Algorithm, Text(item, "sha256")) &&
                    artifact.Id == SourceArtifactId.DeriveV1(artifact.Digest) &&
                    artifact.DeclaredFormat == new KnowledgeFormatCoordinate(format, "1") &&
                    Text(item, "formatId") == format && Text(item, "formatVersion") == "1",
                    "Route artifact bytes or exact format differ from the bound source.");
            }
            Require(declared.SetEquals(byCoordinate.Keys) && declared.Contains(PathsCoordinate) &&
                declared.Contains(LanguageCoordinate) && declared.Contains(GxtCoordinate), "Route source/parent closure is incomplete.");
            var sources = ImmutableArray.CreateBuilder<GtaVRouteNodeSource>();
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in root.GetProperty("ynds").EnumerateArray())
            {
                Properties(source, "sourceCoordinate", "nodes");
                var coordinate = Text(source, "sourceCoordinate");
                Require(indexed.Add(coordinate) && byCoordinate.TryGetValue(coordinate, out _) &&
                    byCoordinate[coordinate].DeclaredFormat.FormatId == YndFormatId, "Route node source is duplicated or unbound.");
                var nodes = ImmutableArray.CreateBuilder<GtaVRouteNodeFact>();
                var identities = new HashSet<(ushort, ushort)>();
                foreach (var node in source.GetProperty("nodes").EnumerateArray())
                {
                    Properties(node, "ordinal", "areaId", "nodeId", "streetNameHash");
                    var ordinal = node.GetProperty("ordinal").GetInt32();
                    var area = node.GetProperty("areaId").GetUInt16();
                    var nativeId = node.GetProperty("nodeId").GetUInt16();
                    var hash = node.GetProperty("streetNameHash").GetUInt32();
                    Require(ordinal == nodes.Count && nodes.Count < 1_000_000 && identities.Add((area, nativeId)),
                        "Route nodes require complete source ordinals and unique native node identities.");
                    nodes.Add(new GtaVRouteNodeFact(ordinal, area, nativeId, hash,
                        $"rpf7-member:{coordinate}#/nodes[{ordinal}]/streetNameHash"));
                }
                sources.Add(new GtaVRouteNodeSource(byCoordinate[coordinate], nodes.ToImmutable()));
            }
            Require(indexed.SetEquals(supplied.Where(a => a.DeclaredFormat.FormatId == YndFormatId)
                .Select(a => a.SourceCoordinate.ExactRepresentation)) && indexed.Count > 0,
                "Every route YND must be indexed exactly once.");
            var gxt = byCoordinate[GxtCoordinate];
            var terms = new GtaVSupportedSourceCorpusIndex([gxt]).GetGxt2(gxt, MaximumBytes);
            var result = new GtaVRouteCorpusIndex(supplied.OrderBy(a => a.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal).ToImmutableArray(),
                sources.OrderBy(s => s.Artifact.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal).ToImmutableArray(), gxt, terms, digest);
            var counts = root.GetProperty("counts");
            Properties(counts, "yndArtifactCount", "nodeCount", "distinctNonzeroHashes", "namedRouteCount", "unmatchedHashCount");
            Require(counts.GetProperty("yndArtifactCount").GetInt32() == result.Metrics.YndArtifactCount &&
                counts.GetProperty("nodeCount").GetInt32() == result.Metrics.NodeCount &&
                counts.GetProperty("distinctNonzeroHashes").GetInt32() == result.Metrics.DistinctNonzeroHashes &&
                counts.GetProperty("namedRouteCount").GetInt32() == result.Metrics.NamedRouteCount &&
                counts.GetProperty("unmatchedHashCount").GetInt32() == result.Metrics.UnmatchedHashCount,
                "Route index coverage differs from its deterministic native-key/GXT2 join.");
            return result;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or
            OverflowException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("The route index is not supported exact source data.", exception); }
    }

    public void RequireExactArtifacts(IEnumerable<FrozenSourceArtifact> artifacts)
    {
        var actual = artifacts.OrderBy(a => a.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal).ToImmutableArray();
        Require(actual.Length == Artifacts.Length, "Route adapter corpus size mismatch.");
        for (var i = 0; i < actual.Length; i++)
            Require(actual[i].Id == Artifacts[i].Id && actual[i].Digest == Artifacts[i].Digest &&
                actual[i].DeclaredFormat == Artifacts[i].DeclaredFormat && actual[i].SourceCoordinate == Artifacts[i].SourceCoordinate &&
                actual[i].ObservedAtUtc == Artifacts[i].ObservedAtUtc && actual[i].ExactBytes.SequenceEqual(Artifacts[i].ExactBytes),
                "Route adapter source bytes, coordinate, observation, or format mismatch.");
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new InvalidDataException("Route string is null.");
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    private static void Properties(JsonElement value, params string[] names) => Require(value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names), "Route object schema mismatch.");
    private static void ValidateJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { Require(names.Add(property.Name), "Route JSON contains duplicate fields."); ValidateJson(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateJson(item);
    }
}

public sealed record GtaVRouteCorpusIndexReceiptBinding(string SchemaId, int SchemaVersion, string DecoderMethodId,
    string DecoderVersion, string DecoderArtifactSha256, long ByteLength, ContentDigest Digest);
public sealed record GtaVRouteNodeSource(FrozenSourceArtifact Artifact, ImmutableArray<GtaVRouteNodeFact> Nodes);
public sealed record GtaVRouteNodeFact(int Ordinal, ushort AreaId, ushort NodeId, uint StreetNameHash, string FieldLocator);
public sealed record GtaVRouteMetrics(int YndArtifactCount, int NodeCount, int DistinctNonzeroHashes,
    int NamedRouteCount, int UnmatchedHashCount, int ZeroHashNodeCount);
