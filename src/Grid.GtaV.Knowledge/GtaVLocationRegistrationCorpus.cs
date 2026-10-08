using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>One digest-pinned reference page extract admitted by scoped discovery.</summary>
public sealed record GtaVLocationReferencePage(
    string ReferenceSourceId,
    string Title,
    long Revision,
    string Coordinate,
    string RelativePath,
    bool ProductScope,
    ImmutableArray<string> DiscoveredVia,
    ImmutableArray<string> RedirectAliases,
    FrozenSourceArtifact Artifact,
    string Text);

/// <summary>
/// The v3 Location registration corpus: the pinned native zone table plus every reference page extract that
/// exhaustive scoped discovery admitted. Nothing here names a place; the manifest and ledger are generated.
/// </summary>
public sealed class GtaVLocationRegistrationCorpus
{
    public const string ManifestFileName = "gta_v_enhanced_location_hierarchy_sources.v3.json";
    public const string NativeTableFileName = "cfx-zones-ad60ae80.md";

    public FrozenSourceArtifact NativeTable { get; }
    public ImmutableArray<GtaVLocationReferencePage> Pages { get; }
    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }
    public ImmutableArray<string> ProductScopeMarkers { get; }
    public ContentDigest ManifestDigest { get; }
    public ContentDigest DiscoveryLedgerDigest { get; }
    public byte[] DiscoveryLedger { get; }
    public ImmutableDictionary<string, string> NativeNameCodes { get; }
    public ImmutableHashSet<string> AmbiguousNativeNames { get; }
    public DateTimeOffset AcquiredAtUtc { get; }

    private readonly ImmutableDictionary<string, string> revisionsByCoordinate;

    private GtaVLocationRegistrationCorpus(
        FrozenSourceArtifact nativeTable,
        ImmutableArray<GtaVLocationReferencePage> pages,
        ImmutableArray<string> markers,
        ContentDigest manifestDigest,
        byte[] ledger,
        DateTimeOffset acquiredAtUtc)
    {
        NativeTable = nativeTable;
        Pages = pages;
        ProductScopeMarkers = markers;
        ManifestDigest = manifestDigest;
        DiscoveryLedger = ledger;
        DiscoveryLedgerDigest = ContentDigest.ComputeSha256(ledger);
        AcquiredAtUtc = acquiredAtUtc;
        Artifacts = [nativeTable, .. pages.Select(p => p.Artifact)];
        var tableText = Encoding.UTF8.GetString(nativeTable.ExactBytes.AsSpan());
        NativeNameCodes = GtaVLocationHierarchyCorpusIndex.ParseNativeTable(tableText);
        AmbiguousNativeNames = GtaVLocationHierarchyCorpusIndex.ParseAmbiguousNativeTableNames(tableText);
        revisionsByCoordinate = pages
            .Select(p => KeyValuePair.Create(p.Coordinate, p.Revision.ToString(CultureInfo.InvariantCulture)))
            .Append(KeyValuePair.Create(GtaVLocationHierarchyCorpusIndex.CfxCoordinate, GtaVLocationHierarchyCorpusIndex.CfxRevision))
            .ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Exact pinned revision of an admitted coordinate; unknown coordinates fail closed.</summary>
    public string ResolvePinnedRevision(string coordinate) =>
        revisionsByCoordinate.TryGetValue(coordinate, out var revision)
            ? revision
            : throw new InvalidDataException("Unsupported Location registration reference coordinate: " + coordinate);

    public static bool IsAvailable(string referencesDirectory) => ResolveManifestPath(referencesDirectory) is not null;

    public static GtaVLocationRegistrationCorpus LoadFromRepository(string referencesDirectory)
    {
        referencesDirectory = Path.GetFullPath(referencesDirectory);
        var manifestPath = ResolveManifestPath(referencesDirectory)
            ?? throw new FileNotFoundException("Location registration v3 source manifest is unavailable.", ManifestFileName);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        using var json = JsonDocument.Parse(manifestBytes);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 3)
            throw new InvalidDataException("Location registration requires source manifest schema version 3.");
        if (root.GetProperty("gameId").GetString() != ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value)
            throw new InvalidDataException("Location registration manifest targets a different game.");
        var acquired = DateTimeOffset.Parse(root.GetProperty("acquiredAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        var native = root.GetProperty("nativeTable");
        var nativeBytes = File.ReadAllBytes(Path.Combine(referencesDirectory, NativeTableFileName));
        var nativeArtifact = Frozen(native.GetProperty("coordinate").GetString()!, nativeBytes, acquired);
        GtaVPresentationCorpusIndex.RequireDigest(nativeArtifact, native.GetProperty("sha256").GetString()!);

        var ledgerElement = root.GetProperty("discoveryLedger");
        var ledgerBytes = File.ReadAllBytes(ResolveCorpusPath(referencesDirectory, ledgerElement.GetProperty("path").GetString()!));
        if (ContentDigest.ComputeSha256(ledgerBytes).HexValue != ledgerElement.GetProperty("sha256").GetString())
            throw new InvalidDataException("Location discovery ledger digest does not match the v3 manifest.");

        var markers = root.GetProperty("productScopeMarkers").EnumerateArray().Select(v => v.GetString()!).ToImmutableArray();
        var pages = ImmutableArray.CreateBuilder<GtaVLocationReferencePage>();
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in root.GetProperty("sources").EnumerateArray())
        {
            var coordinate = source.GetProperty("coordinate").GetString()!;
            if (!coordinates.Add(coordinate))
                throw new InvalidDataException("Location registration manifest repeats a reference coordinate: " + coordinate);
            var relative = source.GetProperty("path").GetString()!;
            var bytes = File.ReadAllBytes(ResolveCorpusPath(referencesDirectory, relative));
            var artifact = Frozen(coordinate, bytes, acquired);
            GtaVPresentationCorpusIndex.RequireDigest(artifact, source.GetProperty("sha256").GetString()!);
            pages.Add(new(
                source.GetProperty("source").GetString()!,
                source.GetProperty("title").GetString()!,
                source.GetProperty("revid").GetInt64(),
                coordinate,
                relative,
                source.GetProperty("scope").GetString() == "product",
                Strings(source, "discoveredVia"),
                Strings(source, "redirectAliases"),
                artifact,
                Encoding.UTF8.GetString(bytes)));
        }
        return new(nativeArtifact,
            pages.ToImmutable().OrderBy(p => p.ReferenceSourceId, StringComparer.Ordinal).ThenBy(p => p.Title, StringComparer.Ordinal).ToImmutableArray(),
            markers, ContentDigest.ComputeSha256(manifestBytes), ledgerBytes, acquired);
    }

    private static ImmutableArray<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToImmutableArray()
            : [];

    private static string? ResolveManifestPath(string referencesDirectory)
    {
        var parent = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(referencesDirectory))!, ManifestFileName);
        if (File.Exists(parent)) return parent;
        var local = Path.Combine(referencesDirectory, ManifestFileName);
        return File.Exists(local) ? local : null;
    }

    /// <summary>Manifest paths are catalog-relative ("references/..."); bundles flatten the references directory.</summary>
    private static string ResolveCorpusPath(string referencesDirectory, string relative)
    {
        const string prefix = "references/";
        var trimmed = relative.StartsWith(prefix, StringComparison.Ordinal) ? relative[prefix.Length..] : relative;
        if (trimmed.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
            throw new InvalidDataException("Location registration manifest path escapes the references directory.");
        return Path.Combine(referencesDirectory, trimmed.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static FrozenSourceArtifact Frozen(string coordinate, byte[] bytes, DateTimeOffset observedAtUtc)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new(SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8("grid.gta-v.location-hierarchy.reference", "PinnedReference", coordinate, "grid.exact-utf8", 1),
            new KnowledgeFormatCoordinate(GtaVPresentationCorpusIndex.ReferenceFormatId, "1"), bytes.ToImmutableArray(),
            observedAtUtc);
    }
}
