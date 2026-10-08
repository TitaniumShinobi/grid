using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>Immutable source-family index. Every fact retains its exact raw artifact and XML/GXT locator.</summary>
public sealed class GtaVItemCorpusIndex
{
    public const string SchemaId = "grid.gta-v.item-corpus-index";
    public const string FormatPrefix = "rockstar.gta-v.item.";
    public const string DecoderVersion = "0.5.1";
    public const string ApprovedDecoderArtifactSha256 = "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c";
    public const long MaximumArtifactBytes = 64L * 1024 * 1024;
    public ImmutableArray<GtaVItemSource> Sources { get; }
    public ImmutableArray<GtaVItemRow> Rows { get; }
    public ImmutableArray<GtaVItemSource> LocalizationSources { get; }
    public ImmutableArray<string> Unresolved { get; }
    private readonly ImmutableDictionary<string, GtaVItemSource> _sources;
    private readonly ImmutableDictionary<string, ImmutableDictionary<uint, GtaVItemText>> _text;

    public GtaVItemCorpusIndex(ReadOnlySpan<byte> indexBytes, IEnumerable<FrozenSourceArtifact> artifacts)
    {
        using var document = JsonDocument.Parse(indexBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (root.GetProperty("schemaId").GetString() != SchemaId || root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported Item corpus index.");
        var byCoordinate = artifacts.ToDictionary(x => x.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);
        var sources = ImmutableArray.CreateBuilder<GtaVItemSource>();
        var rows = ImmutableArray.CreateBuilder<GtaVItemRow>();
        var unresolved = ImmutableArray.CreateBuilder<string>();
        var text = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<uint, GtaVItemText>>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("sources").EnumerateArray())
        {
            var coordinate = Required(item, "sourceCoordinate");
            if (!byCoordinate.TryGetValue(coordinate, out var artifact) ||
                ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest ||
                SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id)
                throw new InvalidDataException("Item index source has no exact frozen artifact.");
            var family = Required(item, "family");
            var expectedFormat = family switch { "localization" => "rockstar.gta-v.gxt2-binary", "localization-container" => "rockstar.rpf7-container", _ => FormatPrefix + family + "-xml" };
            if (artifact.DeclaredFormat != new KnowledgeFormatCoordinate(expectedFormat, "1"))
                throw new InvalidDataException("Item family does not match the acquired source format.");
            var pack = item.GetProperty("pack").GetString() ?? "";
            var ordinal = item.GetProperty("mountOrdinal").GetInt32();
            var locale = item.GetProperty("locale").GetString();
            if (family == "localization" && locale != "en-US")
                throw new InvalidDataException("The Item v1 localization boundary is en-US.");
            var source = new GtaVItemSource(artifact, family, pack, ordinal, locale,
                item.GetProperty("declaration").GetString());
            sources.Add(source);
            if (family == "localization") text.Add(coordinate, ReadGxt(source));
            else if (family is "vehicles" or "weapons" or "components" or "apparel" or "pickups" or "vehicle-shop" or "weapon-shop")
                rows.AddRange(ParseRows(source, unresolved));
        }
        if (sources.Select(x => x.Coordinate).Distinct(StringComparer.Ordinal).Count() != sources.Count || sources.Count != byCoordinate.Count)
            throw new InvalidDataException("Item source index is not an exact closed artifact set.");
        foreach (var mount in root.GetProperty("mountLedger").EnumerateArray())
        {
            if (mount.GetProperty("status").GetString() == "declared-container-absent")
                unresolved.Add("mount-absent:" + Required(mount, "rawMountPath"));
            if (mount.TryGetProperty("unresolved", out var values))
                foreach (var value in values.EnumerateArray()) unresolved.Add(Required(value, "reason") + ":" + Required(value, "filename"));
        }
        Sources = sources.OrderBy(x => x.Coordinate, StringComparer.Ordinal).ToImmutableArray();
        Rows = rows.OrderBy(x => x.Source.Coordinate, StringComparer.Ordinal).ThenBy(x => x.Locator, StringComparer.Ordinal).ToImmutableArray();
        LocalizationSources = Sources.Where(x => x.Family == "localization").ToImmutableArray();
        _sources = Sources.ToImmutableDictionary(x => x.Coordinate, StringComparer.Ordinal);
        _text = text.ToImmutable();
        Unresolved = unresolved.ToImmutable();
        ValidateDeclarations();
    }

    private void ValidateDeclarations()
    {
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        var typeFamilies = new Dictionary<string,string>(StringComparer.Ordinal) {
            ["VEHICLE_METADATA_FILE"] = "vehicles", ["VEHICLE_SHOP_DLC_FILE"] = "vehicle-shop",
            ["WEAPONINFO_FILE"] = "weapons", ["WEAPONINFO_FILE_PATCH"] = "weapons",
            ["WEAPONCOMPONENTSINFO_FILE"] = "components", ["WEAPON_SHOP_INFO_METADATA_FILE"] = "weapon-shop",
            ["DLC_WEAPON_PICKUPS"] = "pickups", ["SHOP_PED_APPAREL_META_FILE"] = "apparel", ["TEXTFILE_METAFILE"] = "text-manifest" };
        foreach (var source in Sources.Where(x => x.Declaration is not null))
        {
            var declaration = source.Declaration!;
            var match = System.Text.RegularExpressions.Regex.Match(declaration,
                @"^rpf7-member:(.+)#/CDataFileMgr__ContentsOfDataFileXml\[1\]/dataFiles\[(\d+)\]/Item\[(\d+)\]$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success || !_sources.TryGetValue(match.Groups[1].Value, out var manifest) || manifest.Family != "dlc-content")
                throw new InvalidDataException("Item content declaration does not address a frozen manifest.");
            if (!documents.TryGetValue(manifest.Coordinate, out var doc))
            {
                using var stream = new MemoryStream(manifest.Artifact.ExactBytes.ToArray());
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumArtifactBytes });
                doc = XDocument.Load(reader); documents.Add(manifest.Coordinate, doc);
            }
            var group = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var item = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var entry = doc.Root?.Elements("dataFiles").ElementAtOrDefault(group - 1)?.Elements("Item").ElementAtOrDefault(item - 1);
            var filename = entry?.Element("filename")?.Value;
            var type = entry?.Element("fileType")?.Value;
            var split = filename?.IndexOf(":/", StringComparison.Ordinal) ?? -1;
            if (split < 0 || type is null || !typeFamilies.TryGetValue(type, out var family) || family != source.Family)
                throw new InvalidDataException("Item source type differs from its raw mounted declaration.");
            var member = filename![(split + 2)..].Replace("%PLATFORM%", "x64", StringComparison.OrdinalIgnoreCase);
            var expected = manifest.Coordinate[..(manifest.Coordinate.LastIndexOf("!/", StringComparison.Ordinal) + 2)] + member;
            if (!string.Equals(expected, source.Coordinate, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Item source coordinate differs from its raw mounted declaration.");
        }
    }

    public GtaVItemSource GetSource(FrozenSourceArtifact artifact)
    {
        if (!_sources.TryGetValue(artifact.SourceCoordinate.ExactRepresentation, out var value) || value.Artifact.Id != artifact.Id || value.Artifact.Digest != artifact.Digest)
            throw new InvalidDataException("Artifact is outside the Item corpus.");
        return value;
    }

    /// <summary>Same-pack strings then global candidates; unequal text remains a conflict, never last-wins.</summary>
    public ImmutableArray<GtaVItemText> ResolveText(GtaVItemSource subject, string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label is "NULL" or "WT_INVALID" or "CARNOTFOUND") return [];
        var hash = Joaat(label);
        var candidates = LocalizationSources.Where(x => _text[x.Coordinate].ContainsKey(hash)).ToArray();
        var pack = candidates.Where(x => subject.Pack.Length > 0 && x.Pack == subject.Pack).ToArray();
        if (pack.Length > 0) candidates = pack;
        else
        {
            var global = candidates.Where(x => x.Pack.Length == 0).ToArray();
            if (global.Length > 0)
            {
                // Patch global is a declared replacement; retain its exact field provenance.
                var patch = global.Where(x => x.MountOrdinal == -2).ToArray();
                candidates = patch.Length > 0 ? patch : global;
            }
        }
        return candidates.Select(x => _text[x.Coordinate][hash]).OrderBy(x => x.Source.Coordinate, StringComparer.Ordinal).ToImmutableArray();
    }

    public static uint Joaat(string value)
    {
        if (value.Any(x => x > 127 || char.IsControl(x))) throw new InvalidDataException("Label keys must be printable ASCII.");
        uint h = 0;
        unchecked { foreach (var c in value) { h += (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c); h += h << 10; h ^= h >> 6; } h += h << 3; h ^= h >> 11; h += h << 15; }
        return h;
    }

    private static ImmutableDictionary<uint, GtaVItemText> ReadGxt(GtaVItemSource source)
    {
        var bytes = source.Artifact.ExactBytes.AsSpan();
        if (bytes.Length < 16 || bytes.Length > MaximumArtifactBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x47585432)
            throw new InvalidDataException("Invalid Item GXT2 header.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var endTable = checked(8L + count * 8L);
        if (endTable + 8 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)endTable..]) != 0x47585432)
            throw new InvalidDataException("Invalid Item GXT2 table.");
        var end = BinaryPrimitives.ReadUInt32LittleEndian(bytes[((int)endTable + 4)..]);
        if (end > bytes.Length || end < endTable + 8) throw new InvalidDataException("Invalid GXT2 text end.");
        var output = ImmutableDictionary.CreateBuilder<uint, GtaVItemText>();
        for (int i = 0; i < count; i++)
        {
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(8 + i * 8)..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(12 + i * 8)..]);
            if (offset < endTable + 8 || offset >= end) throw new InvalidDataException("Invalid GXT2 string offset.");
            var tail = bytes[(int)offset..(int)end]; var length = tail.IndexOf((byte)0);
            if (length < 0) throw new InvalidDataException("GXT2 string lacks terminator.");
            var value = new UTF8Encoding(false, true).GetString(tail[..length]);
            if (!output.TryAdd(hash, new(source, hash, value, $"rpf7-member:{source.Coordinate}#entries[0x{hash:X8}]/text", (int)offset, length)))
                throw new InvalidDataException("GXT2 contains duplicate hashes.");
        }
        return output.ToImmutable();
    }

    private static ImmutableArray<GtaVItemRow> ParseRows(GtaVItemSource source, ImmutableArray<string>.Builder unresolved)
    {
        using var stream = new MemoryStream(source.Artifact.ExactBytes.ToArray(), false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumArtifactBytes });
        var doc = XDocument.Load(reader); var root = doc.Root ?? throw new InvalidDataException("Missing XML root.");
        var expected = source.Family switch { "vehicles" => "CVehicleModelInfo__InitDataList", "weapons" => "CWeaponInfoBlob", "components" => "CWeaponComponentInfoBlob", "apparel" => "ShopPedApparel", "pickups" => "CPickupDataManager", "vehicle-shop" => "ShopVehicleDataArray", "weapon-shop" => "WeaponShopItemArray", _ => throw new InvalidDataException("Unknown Item family.") };
        if (root.Name.LocalName != expected) throw new InvalidDataException("Item XML root mismatch.");
        var result = ImmutableArray.CreateBuilder<GtaVItemRow>();
        IEnumerable<XElement> selected = source.Family switch
        {
            "vehicles" => root.Elements("InitDatas").Elements("Item"),
            "components" => root.Elements("Infos").Elements("Item"),
            "weapons" => root.Descendants("Item").Where(x => (string?)x.Attribute("type") is "CWeaponInfo" or "CAmmoInfo"),
            "apparel" => root.Elements().Where(x => x.Name.LocalName is "pedOutfits" or "pedComponents" or "pedProps").Elements("Item"),
            "pickups" => root.Elements().Where(x => x.Name.LocalName is "pickupData" or "actionData" or "rewardData").Elements("Item"),
            "vehicle-shop" => root.Elements("Vehicles").Elements("Item"),
            "weapon-shop" => root.Elements("weaponShopItems").Elements("Item"),
            _ => [],
        };
        foreach (var element in selected)
        {
            var objectType = source.Family switch { "vehicles" => "CVehicleModelInfo.modelName", "weapons" => (string)element.Attribute("type")!, "components" => "CWeaponComponentInfo", "apparel" => "ShopPedApparel." + element.Parent!.Name.LocalName, "pickups" => "CPickupDataManager." + element.Parent!.Name.LocalName, "vehicle-shop" => "VehicleShop", _ => "WeaponShop" };
            var keyName = source.Family switch { "vehicles" => "modelName", "apparel" => "uniqueNameHash", "vehicle-shop" => "modelNameHash", "weapon-shop" => "nameHash", _ => "Name" };
            var key = Scalar(element, keyName, true)!;
            var scalarGroups = element.Elements().Where(x => !x.HasElements).GroupBy(x => x.Name.LocalName, StringComparer.Ordinal).ToArray();
            foreach (var ambiguous in scalarGroups.Where(x => x.Count() > 1))
                unresolved.Add("repeated-scalar-field:" + source.Coordinate + "#" + XmlPath(element) + "/" + ambiguous.Key);
            // Repeated source fields are not assigned an arbitrary winner. The raw bytes and
            // every typed reference remain available; scalar-dependent presentation is withheld.
            var fields = scalarGroups.Where(g => g.Count() == 1).ToDictionary(g => g.Key,
                g => (string?)g.Single().Attribute("value") ?? g.Single().Value, StringComparer.Ordinal);
            if (source.Family == "apparel")
            {
                fields["pedName"] = Scalar(root, "pedName", true)!;
                fields["fullDlcName"] = Scalar(root, "fullDlcName", true)!;
                fields["eCharacter"] = Scalar(root, "eCharacter", true)!;
                key = fields["pedName"] + "/" + fields["fullDlcName"] + "/" + objectType + "/" + key;
            }
            var locator = $"rpf7-member:{source.Coordinate}#{XmlPath(element)}";
            var labelField = source.Family switch { "vehicles" => "gameName", "weapons" => "HumanNameHash", "components" => "LocName", "apparel" or "vehicle-shop" or "weapon-shop" => "textLabel", _ => null };
            var refs = element.Descendants().Where(x => x.Name.LocalName is "AmmoInfo" or "WeaponRef" or "AmmoRef" or "componentName" or "txdName" or "handlingId" or "vehicleMakeName" or "type" or "eCompType" or "eAnchorPoint" or "componentId" or "drawableIndex" or "DrawableIndex" or "textureIndex" or "localDrawableIndex" or "propIndex" or "localPropIndex" or "tagNameHash" or "nameHash" or "enumValue" or "Model" ||
                    (x.Name.LocalName == "Name" && x.Ancestors("Components").Any()) ||
                    (x.Name.LocalName == "Item" && !x.HasElements && x.Parent?.Name.LocalName is "trailers" or "additionalTrailers" or "Actions" or "Rewards" or "OnFootPickupActions" or "InCarPickupActions" or "OnShotPickupActions" or "restrictionTags") ||
                    (x.Name.LocalName == "driverName"))
                .Select(x => new GtaVItemReference(x.Name.LocalName == "Item" ? x.Parent!.Name.LocalName : x.Name.LocalName,
                    (string?)x.Attribute("ref") ?? (string?)x.Attribute("value") ?? x.Value, $"rpf7-member:{source.Coordinate}#{XmlPath(x)}"))
                .Where(x => x.NativeKey.Length > 0).ToImmutableArray();
            result.Add(new(source, objectType, key, locator, locator + "/" + keyName + "[1]", labelField,
                labelField is null ? null : fields.GetValueOrDefault(labelField), fields.ToImmutableDictionary(StringComparer.Ordinal), refs));
        }
        // Repeated definitions are separate source assertions, not extra identities.
        // In particular mpheist ships two velum2 definitions; neither is silently discarded.
        return result.ToImmutable();
    }

    private static string? Scalar(XElement parent, string field, bool required)
    {
        var values = parent.Elements(field).ToArray();
        if (values.Length > 1 || (required && values.Length != 1) || values.Any(x => x.HasElements)) throw new InvalidDataException("Ambiguous Item scalar field: " + field);
        var text = values.SingleOrDefault()?.Value;
        if (required && string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Missing Item field: " + field);
        return text;
    }

    public static string XmlPath(XElement node) => "/" + string.Join("/", node.AncestorsAndSelf().Reverse().Select(x => x.Name.LocalName + "[" + (x.ElementsBeforeSelf(x.Name).Count() + 1) + "]"));
    private static string Required(JsonElement element, string name) => element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new InvalidDataException("Missing Item index field.");
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicateProperties(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

public sealed record GtaVItemSource(FrozenSourceArtifact Artifact, string Family, string Pack, int MountOrdinal, string? Locale, string? Declaration)
{ public string Coordinate => Artifact.SourceCoordinate.ExactRepresentation; }
public sealed record GtaVItemReference(string Kind, string NativeKey, string FieldLocator);
public sealed record GtaVItemRow(GtaVItemSource Source, string ObjectType, string NativeKey, string Locator, string IdentityField,
    string? LabelField, string? LabelKey, ImmutableDictionary<string,string> Fields, ImmutableArray<GtaVItemReference> References);
public sealed record GtaVItemText(GtaVItemSource Source, uint Hash, string Text, string FieldLocator, int TextOffset, int ByteLength);
