using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>Admits source identities once; historical Item identities are never re-created.</summary>
public class GtaVMountedItemKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    private readonly GtaVItemCorpusIndex _index;
    private readonly string _family;
    private readonly ImmutableHashSet<(string Type, string Key)> _historical;
    private readonly ImmutableDictionary<(string Type, string Key), string> _primaryCoordinates;

    public GtaVMountedItemKnowledgeAdapter(string family, ContentDigest digest, GtaVItemCorpusIndex index,
        CanonicalCatalogPayload? historical = null)
        : base(digest, "grid.gta-v.enhanced.mounted-item." + family, "mounted-item-native-identities-v1",
            GtaVItemCorpusIndex.FormatPrefix + family + "-xml", SourceObjectType(GtaVItemCorpusIndex.FormatPrefix + family + "-xml"),
            ".meta", KnowledgeKind.Item, GtaVItemCorpusIndex.MaximumArtifactBytes, 1024, 500_000)
    {
        if (family is not ("vehicles" or "weapons" or "components" or "apparel" or "pickups"))
            throw new ArgumentException("Unsupported primary Item family.", nameof(family));
        _family = family;
        _index = index;
        _historical = (historical?.KnowledgeRecords ?? []).Where(x => x.Kind == KnowledgeKind.Item)
            .Select(x => (x.NativeIdentity.ObjectType, x.NativeIdentity.ExactRepresentation)).ToImmutableHashSet();
        // Selection chooses the identity's provenance anchor, not precedence for names or applicability.
        // Every candidate's facts are retained independently by the secondary adapter.
        _primaryCoordinates = index.Rows.Where(x => x.Source.Family == family)
            .GroupBy(x => (x.ObjectType, x.NativeKey)).ToImmutableDictionary(g => g.Key,
                g => g.OrderBy(x => x.Source.MountOrdinal).ThenBy(x => x.Source.Coordinate, StringComparer.Ordinal).First().Source.Coordinate);
    }

    public ImmutableArray<FrozenSourceArtifact> PrimaryArtifacts => _index.Sources.Where(x => x.Family == _family &&
        _index.Rows.Any(row => row.Source.Coordinate == x.Coordinate && !_historical.Contains((row.ObjectType,row.NativeKey)) &&
            _primaryCoordinates[(row.ObjectType,row.NativeKey)] == x.Coordinate)).Select(x => x.Artifact).ToImmutableArray();
    private protected override string ParserId => "grid.gta-v-enhanced.mounted-item-xml";
    private protected override string ParserVersion => "1";
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.Item;
    private protected override string RecordNamespace => _family == "weapons" ? "rockstar.gta-v.enhanced.weapons.meta" : "rockstar.gta-v.enhanced.item." + _family;
    private protected override string RecordComparisonMethod => _family == "weapons" ? "grid.gta-v.meta-name.exact-utf8" : "grid.gta-v.item-native-key.exact-utf8";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var source = _index.GetSource(artifact);
        if (source.Family != _family) throw new InvalidDataException("Item source family mismatch.");
        return new ParsedArtifact(_index.Rows.Where(x => x.Source.Coordinate == source.Coordinate &&
                !_historical.Contains((x.ObjectType,x.NativeKey)) && _primaryCoordinates[(x.ObjectType,x.NativeKey)] == source.Coordinate)
            .GroupBy(x => (x.ObjectType, x.NativeKey))
            .Select(g => new ParsedRecord(g.Key.ObjectType, g.Key.NativeKey,
                [new ParsedEvidenceLocation(g.First().Locator,g.First().IdentityField)]))
            .ToImmutableArray(), []);
    }

    public static string SourceObjectType(string format) => format switch
    {
        GtaVItemCorpusIndex.FormatPrefix + "vehicles-xml" => "CVehicleModelInfo__InitDataList",
        GtaVItemCorpusIndex.FormatPrefix + "weapons-xml" => "CWeaponInfoBlob",
        GtaVItemCorpusIndex.FormatPrefix + "components-xml" => "CWeaponComponentInfoBlob",
        GtaVItemCorpusIndex.FormatPrefix + "apparel-xml" => "ShopPedApparel",
        GtaVItemCorpusIndex.FormatPrefix + "pickups-xml" => "CPickupDataManager",
        _ => "MountedItemSource",
    };
}
