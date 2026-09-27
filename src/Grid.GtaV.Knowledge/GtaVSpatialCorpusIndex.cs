using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Strict immutable projection of the bounded mounted YTYP/YMAP/YMF decode. The JSON sidecar is
/// derived indexing data, never canonical evidence: every semantic row is rebound to one exact
/// frozen Rockstar artifact before an adapter can consume it.
/// </summary>
public sealed class GtaVSpatialCorpusIndex
{
    public const string SchemaId = "grid.gta-v.spatial-corpus-index";
    public const int SchemaVersion = 1;
    public const string DecoderMethodId = "fivefury.gta-v.mounted-mlo-spatial";
    public const string DecoderVersion = "0.5.1";
    public const string YtypFormatId = "rockstar.gta-v.ytyp-pso";
    public const string YmapFormatId = "rockstar.gta-v.ymap-pso";
    public const string YmfFormatId = "rockstar.gta-v.ymf-pso";
    public const string ExactFormatVersion = "1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ImmutableDictionary<SourceArtifactId, FrozenSourceArtifact> _artifacts;

    private GtaVSpatialCorpusIndex(
        ImmutableArray<FrozenSourceArtifact> artifacts,
        ImmutableArray<GtaVMloArchetypeSource> ytyps,
        ImmutableArray<GtaVMloInstanceSource> ymaps,
        ImmutableArray<GtaVYmfSpatialSource> ymfs,
        ImmutableArray<GtaVUnsupportedSpatialObject> unsupported,
        ContentDigest sidecarDigest)
    {
        Artifacts = artifacts;
        Ytyps = ytyps;
        Ymaps = ymaps;
        Ymfs = ymfs;
        Unsupported = unsupported;
        SidecarDigest = sidecarDigest;
        _artifacts = artifacts.ToImmutableDictionary(value => value.Id);
    }

    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }
    public ImmutableArray<GtaVMloArchetypeSource> Ytyps { get; }
    public ImmutableArray<GtaVMloInstanceSource> Ymaps { get; }
    public ImmutableArray<GtaVYmfSpatialSource> Ymfs { get; }
    public ImmutableArray<GtaVUnsupportedSpatialObject> Unsupported { get; }
    public ContentDigest SidecarDigest { get; }

    public static GtaVSpatialCorpusIndex Load(
        ReadOnlySpan<byte> canonicalSidecarJson,
        IEnumerable<FrozenSourceArtifact> frozenArtifacts,
        GtaVSpatialCorpusIndexReceiptBinding receiptBinding)
    {
        if (canonicalSidecarJson.IsEmpty) throw new InvalidDataException("The spatial corpus sidecar is empty.");
        ArgumentNullException.ThrowIfNull(frozenArtifacts);
        ArgumentNullException.ThrowIfNull(receiptBinding);
        var sidecarDigest = ContentDigest.ComputeSha256(canonicalSidecarJson);
        if (!string.Equals(receiptBinding.SchemaId, SchemaId, StringComparison.Ordinal) ||
            receiptBinding.SchemaVersion != SchemaVersion ||
            !string.Equals(receiptBinding.DecoderMethodId, DecoderMethodId, StringComparison.Ordinal) ||
            !string.Equals(receiptBinding.DecoderVersion, DecoderVersion, StringComparison.Ordinal) ||
            receiptBinding.ByteLength != canonicalSidecarJson.Length ||
            receiptBinding.Digest != sidecarDigest)
            throw new InvalidDataException("The spatial sidecar differs from its immutable acquisition binding.");

        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetString(canonicalSidecarJson);
            document = JsonDocument.Parse(canonicalSidecarJson.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 48,
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The spatial corpus sidecar is not strict UTF-8 JSON.", exception);
        }

        using (document)
        {
            ValidateNoDuplicateProperties(document.RootElement, "spatial corpus sidecar");
            var root = RequireObject(document.RootElement, "spatial corpus sidecar");
            RequireAllowedProperties(root, "spatial corpus sidecar",
                ["schemaId", "schemaVersion", "decoder", "mountGraph", "counts", "dlcListArtifact", "mountScope", "packs",
                    "artifacts", "ytyps", "ymaps", "ymfs", "unsupported", "unsupportedFamilies"]);
            RequireText(root, "schemaId", SchemaId);
            if (RequireInt(root, "schemaVersion") != SchemaVersion)
                throw new InvalidDataException("The spatial sidecar schema version is unsupported.");
            ValidateDecoder(root.GetProperty("decoder"));
            var decoderDigest = RequireText(RequireObject(root.GetProperty("decoder"), "decoder"), "artifactSha256");
            if (!string.Equals(decoderDigest, receiptBinding.DecoderArtifactSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The spatial decoder artifact differs from its receipt binding.");
            if (root.TryGetProperty("mountGraph", out var mountGraph))
                _ = RequireObject(mountGraph, "mountGraph");

            var supplied = frozenArtifacts.ToImmutableArray();
            if (supplied.IsDefaultOrEmpty || supplied.Any(value => value is null) ||
                supplied.Select(value => value.Id).Distinct().Count() != supplied.Length)
                throw new InvalidDataException("Spatial corpus loading requires distinct non-null frozen artifacts.");
            var suppliedByCoordinate = supplied.ToDictionary(
                value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);
            if (suppliedByCoordinate.Count != supplied.Length)
                throw new InvalidDataException("Spatial frozen source coordinates must be distinct.");

            var declared = RequireArray(root, "artifacts").EnumerateArray()
                .Select((value, ordinal) => ParseArtifact(value, ordinal, suppliedByCoordinate))
                .OrderBy(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .ToImmutableArray();
            if (declared.Length != supplied.Length || !declared.Select(value => value.Id).ToHashSet().SetEquals(supplied.Select(value => value.Id)))
                throw new InvalidDataException("The v1 spatial sidecar does not close exactly over the supplied frozen artifacts.");
            var byCoordinate = declared.ToDictionary(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);

            var ytyps = EnumerateSpatialFamily(root, "ytyps")
                .Select((value, ordinal) => ParseYtyp(value, ordinal, byCoordinate))
                .OrderBy(value => value.Artifact.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .ToImmutableArray();
            var ymaps = EnumerateSpatialFamily(root, "ymaps")
                .Select((value, ordinal) => ParseYmap(value, ordinal, byCoordinate))
                .OrderBy(value => value.Artifact.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .ToImmutableArray();
            var ymfs = EnumerateSpatialFamily(root, "ymfs")
                .Select((value, ordinal) => ParseYmf(value, ordinal, byCoordinate))
                .OrderBy(value => value.Artifact.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .ToImmutableArray();
            var unsupported = root.TryGetProperty("unsupported", out var unsupportedValue)
                ? RequireArray(unsupportedValue, "unsupported").EnumerateArray()
                    .Select((value, ordinal) => ParseUnsupported(value, ordinal)).ToImmutableArray()
                : ImmutableArray<GtaVUnsupportedSpatialObject>.Empty;

            var consumed = ytyps.Select(value => value.Artifact.Id)
                .Concat(ymaps.Select(value => value.Artifact.Id))
                .Concat(ymfs.Select(value => value.Artifact.Id)).ToHashSet();
            if (!consumed.SetEquals(declared.Select(value => value.Id)))
                throw new InvalidDataException("Every frozen spatial artifact must be indexed exactly as YTYP, YMAP, or YMF.");
            if (ytyps.Select(value => value.Artifact.Id).Concat(ymaps.Select(value => value.Artifact.Id))
                .Concat(ymfs.Select(value => value.Artifact.Id)).Distinct().Count() != declared.Length)
                throw new InvalidDataException("A spatial artifact cannot be assigned to multiple source families.");

            return new GtaVSpatialCorpusIndex(declared, ytyps, ymaps, ymfs, unsupported, sidecarDigest);
        }
    }

    public void RequireExactArtifacts(IEnumerable<FrozenSourceArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var values = artifacts.ToImmutableArray();
        if (values.Length != _artifacts.Count || values.Any(value => value is null) ||
            values.Select(value => value.Id).Distinct().Count() != values.Length ||
            !values.Select(value => value.Id).ToHashSet().SetEquals(_artifacts.Keys))
            throw new InvalidDataException("The adapter input does not match the exact verified spatial corpus.");
        foreach (var value in values)
        {
            var exact = _artifacts[value.Id];
            if (value.Digest != exact.Digest || value.DeclaredFormat != exact.DeclaredFormat ||
                value.SourceCoordinate != exact.SourceCoordinate || !value.ExactBytes.SequenceEqual(exact.ExactBytes))
                throw new InvalidDataException("A spatial artifact differs from its verified indexed bytes or coordinates.");
        }
    }

    private static FrozenSourceArtifact ParseArtifact(JsonElement value, int ordinal,
        IReadOnlyDictionary<string, FrozenSourceArtifact> supplied)
    {
        var item = RequireObject(value, $"artifacts[{ordinal}]");
        RequireAllowedProperties(item, "spatial artifact", ["sourceCoordinate", "byteLength", "sha256", "formatId", "formatVersion", "mountTier", "packIdentity", "consumptionStatus"]);
        var coordinate = RequireText(item, "sourceCoordinate");
        if (!supplied.TryGetValue(coordinate, out var artifact))
            throw new InvalidDataException("A declared spatial artifact coordinate is absent from the frozen corpus.");
        if (RequireInt(item, "byteLength") != artifact.ExactBytes.Length ||
            !string.Equals(RequireText(item, "sha256"), artifact.Digest.HexValue, StringComparison.Ordinal) ||
            !string.Equals(RequireText(item, "formatId"), artifact.DeclaredFormat.FormatId, StringComparison.Ordinal) ||
            !string.Equals(RequireText(item, "formatVersion"), artifact.DeclaredFormat.ExactFormatVersion, StringComparison.Ordinal) ||
            SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id ||
            ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest)
            throw new InvalidDataException("A spatial artifact declaration does not bind its exact bytes, digest, or format.");
        if (artifact.DeclaredFormat.ExactFormatVersion != ExactFormatVersion ||
            artifact.DeclaredFormat.FormatId is not (YtypFormatId or YmapFormatId or YmfFormatId))
            throw new InvalidDataException("The spatial corpus contains an unsupported exact format tuple.");
        return artifact;
    }

    private static GtaVMloArchetypeSource ParseYtyp(JsonElement value, int sourceOrdinal,
        IReadOnlyDictionary<string, FrozenSourceArtifact> artifacts)
    {
        var item = RequireObject(value, $"ytyps[{sourceOrdinal}]");
        RequireAllowedProperties(item, "YTYP index", ["artifact", "archetypes"]);
        var artifact = ResolveArtifact(item.GetProperty("artifact"), artifacts, YtypFormatId);
        var archetypes = RequireArray(item, "archetypes").EnumerateArray()
            .Select(entry => ParseArchetype(entry, artifact)).ToImmutableArray();
        if (archetypes.Select(value => value.Ordinal).Distinct().Count() != archetypes.Length ||
            archetypes.Select(value => value.NameHash).Distinct().Count() != archetypes.Length)
            throw new InvalidDataException("One YTYP cannot contain duplicate MLO archetype ordinals or Name hashes.");
        return new GtaVMloArchetypeSource(artifact, archetypes);
    }

    private static GtaVMloArchetypeFact ParseArchetype(JsonElement value, FrozenSourceArtifact artifact)
    {
        var item = RequireObject(value, "MLO archetype");
        RequireAllowedProperties(item, "MLO archetype", ["ordinal", "nameHash", "assetNameHash", "nameExact", "nativeType", "fieldLocator", "byteOffset", "rooms", "portals", "entitySets", "entitySetCount"]);
        var ordinal = RequireNonNegativeInt(item, "ordinal");
        if (!string.Equals(RequireText(item, "nativeType"), "CMloArchetypeDef", StringComparison.Ordinal))
            throw new InvalidDataException("MLO archetypes require exact CMloArchetypeDef type semantics.");
        var hash = ParseHash(item, "nameHash", allowZero: false);
        var assetHash = ParseHash(item, "assetNameHash", allowZero: false);
        var locator = RequireLocator(item, "fieldLocator", artifact);
        var rooms = RequireArray(item, "rooms").EnumerateArray()
            .Select(entry => ParseRoom(entry, artifact, locator)).ToImmutableArray();
        var portals = RequireArray(item, "portals").EnumerateArray()
            .Select(entry => ParsePortal(entry, artifact, locator, rooms)).ToImmutableArray();
        if (rooms.Select(value => value.Ordinal).Distinct().Count() != rooms.Length ||
            portals.Select(value => value.Ordinal).Distinct().Count() != portals.Length)
            throw new InvalidDataException("One MLO archetype cannot contain duplicate room or portal source ordinals.");
        if (item.TryGetProperty("entitySets", out var sets) && sets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MLO entitySets must be an array when present.");
        return new GtaVMloArchetypeFact(ordinal, hash, assetHash, locator, rooms, portals);
    }

    private static GtaVMloRoomFact ParseRoom(JsonElement value, FrozenSourceArtifact artifact,
        string archetypeLocator)
    {
        var item = RequireObject(value, "MLO room");
        RequireAllowedProperties(item, "MLO room", ["ordinal", "nameHash", "nameExact", "fieldLocator", "byteOffset", "bounds", "boundingBoxMin", "boundingBoxMax", "flags", "floorId"]);
        var ordinal = RequireNonNegativeInt(item, "ordinal");
        var locator = RequireLocator(item, "fieldLocator", artifact);
        if (!locator.StartsWith(archetypeLocator, StringComparison.Ordinal))
            throw new InvalidDataException("An MLO room locator must be beneath its archetype locator.");
        uint? nameHash = item.TryGetProperty("nameHash", out _) ? ParseHash(item, "nameHash", true) : null;
        var nameExact = item.TryGetProperty("nameExact", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()! : string.Empty;
        if (nameExact.Any(char.IsControl)) throw new InvalidDataException("An MLO room name contains control characters.");
        return new GtaVMloRoomFact(ordinal, nameHash, nameExact, locator);
    }

    private static GtaVMloPortalFact ParsePortal(JsonElement value, FrozenSourceArtifact artifact,
        string archetypeLocator, ImmutableArray<GtaVMloRoomFact> rooms)
    {
        var item = RequireObject(value, "MLO portal");
        RequireAllowedProperties(item, "MLO portal", ["ordinal", "roomFromOrdinal", "roomToOrdinal", "fieldLocator", "byteOffset", "flags"]);
        var ordinal = RequireNonNegativeInt(item, "ordinal");
        var from = RequireNonNegativeInt(item, "roomFromOrdinal");
        var to = RequireNonNegativeInt(item, "roomToOrdinal");
        var roomOrdinals = rooms.Select(value => value.Ordinal).ToHashSet();
        if (!roomOrdinals.Contains(from) || !roomOrdinals.Contains(to) || from == to)
            throw new InvalidDataException("An MLO portal must connect two distinct rooms in the same archetype.");
        var locator = RequireLocator(item, "fieldLocator", artifact);
        if (!locator.StartsWith(archetypeLocator, StringComparison.Ordinal))
            throw new InvalidDataException("An MLO portal locator must be beneath its archetype locator.");
        return new GtaVMloPortalFact(ordinal, from, to, locator);
    }

    private static GtaVMloInstanceSource ParseYmap(JsonElement value, int sourceOrdinal,
        IReadOnlyDictionary<string, FrozenSourceArtifact> artifacts)
    {
        var item = RequireObject(value, $"ymaps[{sourceOrdinal}]");
        RequireAllowedProperties(item, "YMAP index", ["artifact", "mapNameHash", "instances"]);
        var artifact = ResolveArtifact(item.GetProperty("artifact"), artifacts, YmapFormatId);
        var mapNameHash = ParseHash(item, "mapNameHash", false);
        var instances = RequireArray(item, "instances").EnumerateArray()
            .Select(entry => ParseInstance(entry, artifact)).ToImmutableArray();
        if (instances.Select(value => value.Guid).Distinct().Count() != instances.Length ||
            instances.Select(value => value.Ordinal).Distinct().Count() != instances.Length)
            throw new InvalidDataException("One YMAP cannot contain duplicate MLO instance GUIDs or source ordinals.");
        return new GtaVMloInstanceSource(artifact, mapNameHash, instances);
    }

    private static GtaVMloInstanceFact ParseInstance(JsonElement value, FrozenSourceArtifact artifact)
    {
        var item = RequireObject(value, "MLO instance");
        RequireAllowedProperties(item, "MLO instance", ["ordinal", "guid", "archetypeNameHash", "archetypeHash", "fieldLocator", "byteOffset", "position", "rotation", "groupId", "floorId"]);
        var ordinal = RequireNonNegativeInt(item, "ordinal");
        var archetypeProperty = item.TryGetProperty("archetypeNameHash", out _) ? "archetypeNameHash" : "archetypeHash";
        return new GtaVMloInstanceFact(ordinal, ParseUnsigned(item, "guid", false),
            ParseHash(item, archetypeProperty, false), RequireLocator(item, "fieldLocator", artifact));
    }

    private static GtaVYmfSpatialSource ParseYmf(JsonElement value, int sourceOrdinal,
        IReadOnlyDictionary<string, FrozenSourceArtifact> artifacts)
    {
        var item = RequireObject(value, $"ymfs[{sourceOrdinal}]");
        RequireAllowedProperties(item, "YMF index", ["artifact", "itypDependencies", "imapDependencies", "interiorBounds", "relations", "relationships", "fieldLocator"]);
        var artifact = ResolveArtifact(item.GetProperty("artifact"), artifacts, YmfFormatId);
        return new GtaVYmfSpatialSource(artifact);
    }

    private static GtaVUnsupportedSpatialObject ParseUnsupported(JsonElement value, int ordinal)
    {
        var item = RequireObject(value, $"unsupported[{ordinal}]");
        RequireAllowedProperties(item, "unsupported spatial object", ["sourceCoordinate", "formatId", "reasonCode", "detailCode", "packIdentity"]);
        return new GtaVUnsupportedSpatialObject(
            RequireText(item, "sourceCoordinate"),
            item.TryGetProperty("formatId", out var format) && format.ValueKind == JsonValueKind.String ? format.GetString()! : string.Empty,
            RequireText(item, "reasonCode"));
    }

    private static FrozenSourceArtifact ResolveArtifact(JsonElement value,
        IReadOnlyDictionary<string, FrozenSourceArtifact> artifacts, string requiredFormat)
    {
        var coordinate = value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : RequireText(RequireObject(value, "artifact reference"), "sourceCoordinate");
        if (!artifacts.TryGetValue(coordinate, out var artifact) ||
            !string.Equals(artifact.DeclaredFormat.FormatId, requiredFormat, StringComparison.Ordinal))
            throw new InvalidDataException("A spatial source references an absent artifact or wrong exact format.");
        return artifact;
    }

    private static string RequireLocator(JsonElement item, string property, FrozenSourceArtifact artifact)
    {
        var value = RequireText(item, property);
        var prefix = "rpf7-member:" + artifact.SourceCoordinate.ExactRepresentation + "#";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length || value.Any(char.IsControl))
            throw new InvalidDataException("A spatial fact locator is not bound to its exact Rockstar artifact.");
        return value;
    }

    private static uint ParseHash(JsonElement item, string property, bool allowZero)
    {
        var value = RequireText(item, property);
        if (value.Length != 10 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var hash) || !allowZero && hash == 0)
            throw new InvalidDataException($"{property} must be exact 0xXXXXXXXX unsigned hash text.");
        return hash;
    }

    private static uint ParseUnsigned(JsonElement item, string property, bool allowZero)
    {
        if (!item.TryGetProperty(property, out var value))
            throw new InvalidDataException($"{property} is required.");
        uint? valid = value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)
            ? number : null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.Length == 10 && text.StartsWith("0x", StringComparison.Ordinal) &&
                uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var hex)) valid = hex;
            else if (uint.TryParse(text, System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out var parsed)) valid = parsed;
        }
        if (valid is not uint result || !allowZero && result == 0)
            throw new InvalidDataException($"{property} must be an exact unsigned 32-bit value.");
        return result;
    }

    private static IEnumerable<JsonElement> EnumerateSpatialFamily(JsonElement root, string family)
    {
        if (root.TryGetProperty(family, out var flat))
            return RequireArray(flat, family).EnumerateArray().ToArray();
        if (!root.TryGetProperty("packs", out var packs))
            throw new InvalidDataException("The spatial sidecar has neither flat source arrays nor mounted pack closure.");
        var values = new List<JsonElement>();
        foreach (var pack in RequireArray(packs, "packs").EnumerateArray())
        {
            var packObject = RequireObject(pack, "spatial pack");
            if (!packObject.TryGetProperty("spatialRpfDeclarations", out var declarations))
                throw new InvalidDataException("A spatial pack lacks its declared mounted RPF set.");
            foreach (var declaration in RequireArray(declarations, "spatialRpfDeclarations").EnumerateArray())
            {
                var declarationObject = RequireObject(declaration, "spatial RPF declaration");
                if (!declarationObject.TryGetProperty(family, out var familyValues))
                    throw new InvalidDataException("A spatial RPF declaration lacks one source-family array.");
                values.AddRange(RequireArray(familyValues, family).EnumerateArray());
            }
        }
        return values;
    }

    private static void ValidateDecoder(JsonElement value)
    {
        var item = RequireObject(value, "decoder");
        RequireAllowedProperties(item, "decoder", ["methodId", "exactVersion", "artifactSha256"]);
        RequireText(item, "methodId", DecoderMethodId);
        RequireText(item, "exactVersion", DecoderVersion);
        var digest = RequireText(item, "artifactSha256");
        if (digest.Length != 64 || digest.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException("The spatial decoder artifact digest must be exact SHA-256 text.");
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{name} must be an object.");
        return value;
    }

    private static JsonElement RequireArray(JsonElement item, string property)
    {
        var value = item.ValueKind == JsonValueKind.Object ? item.GetProperty(property) : item;
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{property} must be an array.");
        return value;
    }

    private static string RequireText(JsonElement item, string property, string? expected = null)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString())) throw new InvalidDataException($"{property} must be nonempty text.");
        var text = value.GetString()!;
        if (expected is not null && !string.Equals(text, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{property} has an unsupported value.");
        return text;
    }

    private static int RequireInt(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"{property} must be an exact 32-bit integer.");
        return result;
    }

    private static int RequireNonNegativeInt(JsonElement item, string property)
    {
        var result = RequireInt(item, property);
        if (result < 0) throw new InvalidDataException($"{property} must be non-negative.");
        return result;
    }

    private static void RequireAllowedProperties(JsonElement item, string name, IReadOnlyCollection<string> allowed)
    {
        foreach (var required in allowed.TakeWhile(value => value is "schemaId" or "schemaVersion" or "decoder" or "artifacts" or "ytyps" or "ymaps" or "ymfs"))
            if (!item.TryGetProperty(required, out _)) throw new InvalidDataException($"{name} is missing {required}.");
        if (item.EnumerateObject().Any(value => !allowed.Contains(value.Name, StringComparer.Ordinal)))
            throw new InvalidDataException($"{name} contains an undeclared property.");
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException($"{path} contains a duplicate property.");
                ValidateNoDuplicateProperties(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var ordinal = 0;
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item, $"{path}[{ordinal++}]");
        }
    }
}

public sealed record GtaVSpatialCorpusIndexReceiptBinding(
    string SchemaId, int SchemaVersion, string DecoderMethodId, string DecoderVersion, string DecoderArtifactSha256,
    long ByteLength, ContentDigest Digest);

public sealed record GtaVMloArchetypeSource(FrozenSourceArtifact Artifact, ImmutableArray<GtaVMloArchetypeFact> Archetypes);
public sealed record GtaVMloArchetypeFact(int Ordinal, uint NameHash, uint AssetNameHash, string FieldLocator,
    ImmutableArray<GtaVMloRoomFact> Rooms, ImmutableArray<GtaVMloPortalFact> Portals);
public sealed record GtaVMloRoomFact(int Ordinal, uint? NameHash, string NameExact, string FieldLocator);
public sealed record GtaVMloPortalFact(int Ordinal, int RoomFromOrdinal, int RoomToOrdinal, string FieldLocator);
public sealed record GtaVMloInstanceSource(FrozenSourceArtifact Artifact, uint MapNameHash, ImmutableArray<GtaVMloInstanceFact> Instances);
public sealed record GtaVMloInstanceFact(int Ordinal, uint Guid, uint ArchetypeHash, string FieldLocator);
public sealed record GtaVYmfSpatialSource(FrozenSourceArtifact Artifact);
public sealed record GtaVUnsupportedSpatialObject(string SourceCoordinate, string FormatId, string ReasonCode);
