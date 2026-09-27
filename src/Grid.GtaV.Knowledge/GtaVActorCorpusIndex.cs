using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Strict, immutable projection of the pinned FiveFury ped-metadata decode. The derived JSON is
/// never canonical evidence: every row is re-bound to one exact frozen Rockstar artifact and all
/// emitted receipts retain that raw artifact's digest and coordinate.
/// </summary>
public sealed class GtaVActorCorpusIndex
{
    public const string SchemaId = "grid.gta-v.actor-corpus-index";
    public const int SchemaVersion = 1;
    public const string DecoderMethodId = "fivefury.ymt.ped-metadata";
    public const string DecoderVersion = "0.5.1";
    public const string ApprovedDecoderArtifactSha256 =
        "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c";
    public const string ResidentFormatId = "rockstar.gta-v.ped-model-init-data-list-pso";
    public const string DlcListFormatId = "rockstar.gta-v.dlc-list-xml";
    public const string DlcXmlFormatId = "rockstar.gta-v.ped-model-init-data-list-xml";
    public const string DlcSetupFormatId = "rockstar.gta-v.dlc-setup-xml";
    public const string DlcContentFormatId = "rockstar.gta-v.dlc-content-xml";
    public const string ExactFormatVersion = "1";
    public const int ExpectedResidentRecordCount = 683;
    public const int ExpectedDlcRecordCount = 435;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ImmutableDictionary<SourceArtifactId, GtaVResidentPedSource> _residentByArtifact;
    private readonly ImmutableDictionary<SourceArtifactId, GtaVDlcPedSource> _dlcByArtifact;

    private GtaVActorCorpusIndex(
        ImmutableArray<GtaVActorArtifactCoordinate> residentCandidates,
        GtaVResidentPedSource resident,
        GtaVActorArtifactCoordinate dlcListArtifact,
        ImmutableArray<GtaVDlcPedSource> dlcPacks,
        ContentDigest sidecarDigest)
    {
        ResidentCandidates = residentCandidates;
        Resident = resident;
        DlcListArtifact = dlcListArtifact;
        DlcPacks = dlcPacks;
        SidecarDigest = sidecarDigest;
        _residentByArtifact = ImmutableDictionary<SourceArtifactId, GtaVResidentPedSource>.Empty
            .Add(resident.Artifact.ArtifactId, resident);
        _dlcByArtifact = dlcPacks.ToImmutableDictionary(value => value.Artifact.ArtifactId);
    }

    public ImmutableArray<GtaVActorArtifactCoordinate> ResidentCandidates { get; }
    public GtaVResidentPedSource Resident { get; }
    public GtaVActorArtifactCoordinate DlcListArtifact { get; }
    public ImmutableArray<GtaVDlcPedSource> DlcPacks { get; }
    public ContentDigest SidecarDigest { get; }

    public static GtaVActorCorpusIndex Load(
        ReadOnlySpan<byte> canonicalSidecarJson,
        IEnumerable<FrozenSourceArtifact> frozenArtifacts,
        GtaVActorCorpusIndexReceiptBinding receiptBinding)
    {
        if (canonicalSidecarJson.IsEmpty) throw new InvalidDataException("The Actor corpus sidecar is empty.");
        ArgumentNullException.ThrowIfNull(frozenArtifacts);
        ArgumentNullException.ThrowIfNull(receiptBinding);
        var sidecarDigest = ContentDigest.ComputeSha256(canonicalSidecarJson);
        if (!string.Equals(receiptBinding.SchemaId, SchemaId, StringComparison.Ordinal) ||
            receiptBinding.SchemaVersion != SchemaVersion ||
            !string.Equals(receiptBinding.DecoderMethodId, DecoderMethodId, StringComparison.Ordinal) ||
            !string.Equals(receiptBinding.DecoderVersion, DecoderVersion, StringComparison.Ordinal) ||
            !string.Equals(receiptBinding.DecoderArtifactSha256, ApprovedDecoderArtifactSha256, StringComparison.Ordinal) ||
            receiptBinding.ByteLength != canonicalSidecarJson.Length ||
            receiptBinding.Digest != sidecarDigest)
            throw new InvalidDataException("The Actor corpus sidecar differs from its immutable acquisition receipt binding.");
        JsonDocument document;
        try
        {
            _ = StrictUtf8.GetString(canonicalSidecarJson);
            document = JsonDocument.Parse(canonicalSidecarJson.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The Actor corpus sidecar is not strict UTF-8 JSON.", exception);
        }
        using (document)
        {
            ValidateNoDuplicateProperties(document.RootElement, "actor corpus sidecar");
            var root = RequireObject(document.RootElement, "actor corpus sidecar");
            RequireProperties(root, "actor corpus sidecar", "schemaId", "schemaVersion", "decoder", "resident", "dlcListArtifact", "dlcPacks");
            RequireText(root, "schemaId", SchemaId);
            RequireInt(root, "schemaVersion", SchemaVersion);
            ValidateDecoder(root.GetProperty("decoder"));

            var artifacts = frozenArtifacts.ToImmutableArray();
            if (artifacts.IsDefaultOrEmpty || artifacts.Any(value => value is null))
                throw new InvalidDataException("Actor corpus loading requires exact non-null frozen artifacts.");
            var byId = artifacts.GroupBy(value => value.Id).ToDictionary(
                value => value.Key,
                value => value.Count() == 1
                    ? value.Single()
                    : throw new InvalidDataException("The Actor corpus contains a duplicate frozen artifact identity."));

            var residentElement = RequireObject(root.GetProperty("resident"), "resident");
            RequireProperties(residentElement, "resident", "effectiveArtifact", "candidateArtifacts", "records");
            var candidates = RequireArray(residentElement, "candidateArtifacts")
                .EnumerateArray().Select((value, ordinal) =>
                    ParseArtifactCoordinate(value, $"resident.candidateArtifacts[{ordinal}]", ResidentFormatId, byId, requireSourceTier: true))
                .OrderBy(value => value.SourceCoordinate, StringComparer.Ordinal).ToImmutableArray();
            if (candidates.Length != 2 || candidates.Select(value => value.ArtifactId).Distinct().Count() != 2)
                throw new InvalidDataException("The resident Actor index requires exactly two distinct base/update candidates.");
            if (!candidates.Select(value => value.SourceCoordinate).Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { "update/update.rpf!/x64/data/peds.ymt", "x64a.rpf!/data/peds.ymt" },
                    StringComparer.Ordinal))
                throw new InvalidDataException("The resident Actor candidates are not the exact base/update peds.ymt pair.");
            var effective = ParseArtifactCoordinate(
                residentElement.GetProperty("effectiveArtifact"), "resident.effectiveArtifact", ResidentFormatId, byId,
                requireSourceTier: true);
            if (!candidates.Contains(effective) ||
                !string.Equals(effective.SourceCoordinate, "update/update.rpf!/x64/data/peds.ymt", StringComparison.Ordinal) ||
                effective.SourceTier != 2 || candidates.Count(value => value.SourceTier == 2) != 1 ||
                candidates.Count(value => value.SourceTier == 3) != 1 ||
                effective.SourceTier != candidates.Min(value => value.SourceTier))
                throw new InvalidDataException("The effective resident Actor source must be the declared update peds.ymt candidate.");
            var residentRecords = ParseResidentRecords(
                RequireArray(residentElement, "records"), effective.SourceCoordinate);
            if (residentRecords.Length != ExpectedResidentRecordCount)
                throw new InvalidDataException($"The verified build requires exactly {ExpectedResidentRecordCount} resident Actor rows.");
            var resident = new GtaVResidentPedSource(effective, residentRecords);
            var dlcList = ParseArtifactCoordinate(
                root.GetProperty("dlcListArtifact"), "DLC list artifact", DlcListFormatId, byId);
            if (!string.Equals(dlcList.SourceCoordinate,
                    "update/update.rpf!/common/data/dlclist.xml", StringComparison.Ordinal))
                throw new InvalidDataException("The Actor mount graph requires the exact Enhanced dlclist.xml artifact.");

            var packs = RequireArray(root, "dlcPacks").EnumerateArray()
                .Select((value, ordinal) => ParseDlcPack(value, ordinal, dlcList, byId))
                .OrderBy(value => value.DlclistOccurrenceOrdinals[0])
                .ThenBy(value => value.PackNameExact, StringComparer.Ordinal)
                .ToImmutableArray();
            if (packs.Length != 25)
                throw new InvalidDataException("The verified build requires exactly 25 mounted DLC ped source entries.");
            if (packs.Select(value => value.PackNameExact).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packs.Length ||
                packs.Select(value => value.NormalizedMountPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packs.Length ||
                packs.Select(value => value.PhysicalContainerCoordinate).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packs.Length)
                throw new InvalidDataException("Mounted DLC ped sources must identify 25 distinct exact packs, mounts, and physical containers.");
            if (packs.Select(value => value.Artifact.ArtifactId).Distinct().Count() != packs.Length)
                throw new InvalidDataException("Each mounted DLC ped source must have one distinct frozen peds.meta artifact.");
            if (packs.SelectMany(value => value.DlclistOccurrenceOrdinals).Distinct().Count() !=
                packs.Sum(value => value.DlclistOccurrenceOrdinals.Length))
                throw new InvalidDataException("DLC list occurrence ordinals must be globally unique across mounted ped sources.");
            if (packs.Sum(value => value.Records.Length) != ExpectedDlcRecordCount)
                throw new InvalidDataException($"The verified build requires exactly {ExpectedDlcRecordCount} mounted DLC Actor rows.");
            var referencedArtifacts = candidates.Select(value => value.ArtifactId).Append(dlcList.ArtifactId)
                .Concat(packs.SelectMany(value => new[]
                    { value.SetupArtifact.ArtifactId, value.ContentArtifact.ArtifactId, value.Artifact.ArtifactId }))
                .ToHashSet();
            if (referencedArtifacts.Count != byId.Count || !referencedArtifacts.SetEquals(byId.Keys))
                throw new InvalidDataException("The frozen Actor artifact set is not closed exactly by the v1 sidecar.");
            return new GtaVActorCorpusIndex(
                candidates, resident, dlcList, packs, sidecarDigest);
        }
    }

    public GtaVResidentPedSource GetResident(FrozenSourceArtifact artifact)
    {
        RequireExactArtifact(artifact, Resident.Artifact);
        return _residentByArtifact[artifact.Id];
    }

    public GtaVDlcPedSource GetDlc(FrozenSourceArtifact artifact)
    {
        if (!_dlcByArtifact.TryGetValue(artifact.Id, out var source))
            throw new InvalidDataException("The artifact is not a verified mounted DLC Actor source.");
        RequireExactArtifact(artifact, source.Artifact);
        return source;
    }

    private static void ValidateDecoder(JsonElement value)
    {
        var decoder = RequireObject(value, "decoder");
        RequireProperties(decoder, "decoder", "methodId", "exactVersion", "artifactSha256");
        RequireText(decoder, "methodId", DecoderMethodId);
        RequireText(decoder, "exactVersion", DecoderVersion);
        RequireText(decoder, "artifactSha256", ApprovedDecoderArtifactSha256);
    }

    private static GtaVActorArtifactCoordinate ParseArtifactCoordinate(
        JsonElement value,
        string name,
        string requiredFormat,
        IReadOnlyDictionary<SourceArtifactId, FrozenSourceArtifact> artifacts,
        bool requireSourceTier = false)
    {
        var item = RequireObject(value, name);
        if (requireSourceTier)
            RequireProperties(item, name, "sourceCoordinate", "byteLength", "sha256", "formatId", "formatVersion", "sourceTier");
        else
            RequireProperties(item, name, "sourceCoordinate", "byteLength", "sha256", "formatId", "formatVersion");
        var coordinate = RequireText(item, "sourceCoordinate");
        ValidateResourceCoordinate(coordinate, name);
        var byteLength = RequireInt(item, "byteLength");
        if (byteLength <= 0) throw new InvalidDataException($"{name}.byteLength must be positive.");
        var digest = new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(item, "sha256"));
        RequireText(item, "formatId", requiredFormat);
        RequireText(item, "formatVersion", ExactFormatVersion);
        var id = SourceArtifactId.DeriveV1(digest);
        if (!artifacts.TryGetValue(id, out var artifact) || artifact.Digest != digest ||
            artifact.ExactBytes.Length != byteLength ||
            !string.Equals(artifact.SourceCoordinate.ExactRepresentation, coordinate, StringComparison.Ordinal) ||
            !string.Equals(artifact.DeclaredFormat.FormatId, requiredFormat, StringComparison.Ordinal) ||
            !string.Equals(artifact.DeclaredFormat.ExactFormatVersion, ExactFormatVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"{name} does not bind one exact frozen Rockstar artifact.");
        var sourceTier = requireSourceTier ? RequireInt(item, "sourceTier") : (int?)null;
        if (sourceTier is not null and not (2 or 3))
            throw new InvalidDataException($"{name}.sourceTier is not a supported resident precedence tier.");
        return new GtaVActorArtifactCoordinate(id, digest, coordinate, byteLength,
            new KnowledgeFormatCoordinate(requiredFormat, ExactFormatVersion), sourceTier);
    }

    private static ImmutableArray<GtaVResidentPedFact> ParseResidentRecords(JsonElement array, string coordinate)
    {
        var values = ImmutableArray.CreateBuilder<GtaVResidentPedFact>();
        var seen = new HashSet<uint>();
        var ordinal = 0;
        foreach (var element in array.EnumerateArray())
        {
            var item = RequireObject(element, $"resident.records[{ordinal}]");
            RequireProperties(item, "resident record", "ordinal", "nameHash", "pedtypeHash", "recordLocator", "nameFieldLocator", "pedtypeFieldLocator");
            if (RequireInt(item, "ordinal") != ordinal) throw new InvalidDataException("Resident Actor ordinals must be contiguous and zero-based.");
            var name = ParseHash(item, "nameHash", allowZero: false);
            var pedtype = ParseHash(item, "pedtypeHash", allowZero: true);
            if (!seen.Add(name)) throw new InvalidDataException("Resident Actor Name hashes must be unique.");
            var expectedRecord = $"pso:{coordinate}#/CPedModelInfo__InitDataList/InitDatas/Item[{ordinal + 1}]";
            RequireText(item, "recordLocator", expectedRecord);
            RequireText(item, "nameFieldLocator", expectedRecord + "/Name");
            RequireText(item, "pedtypeFieldLocator", expectedRecord + "/Pedtype");
            values.Add(new GtaVResidentPedFact(
                ordinal, name, pedtype, expectedRecord, expectedRecord + "/Name", expectedRecord + "/Pedtype"));
            ordinal++;
        }
        return values.ToImmutable();
    }

    private static GtaVDlcPedSource ParseDlcPack(
        JsonElement value,
        int packOrdinal,
        GtaVActorArtifactCoordinate dlcListArtifact,
        IReadOnlyDictionary<SourceArtifactId, FrozenSourceArtifact> artifacts)
    {
        var item = RequireObject(value, $"dlcPacks[{packOrdinal}]");
        RequireProperties(item, "DLC pack", "packNameExact", "dlclistOccurrenceOrdinals", "dlclistOccurrences", "normalizedMountPath",
            "physicalContainerCoordinate", "setupArtifact", "contentArtifact", "pedMetadataDeclarationLocator",
            "pedsArtifact", "records");
        var packName = RequireText(item, "packNameExact");
        var ordinals = RequireArray(item, "dlclistOccurrenceOrdinals").EnumerateArray()
            .Select(value => RequireIntValue(value, "dlclist occurrence ordinal")).ToImmutableArray();
        if (ordinals.IsEmpty || ordinals.Any(value => value < 0) ||
            ordinals.Any(value => value >= 104) ||
            !ordinals.SequenceEqual(ordinals.Order()) || ordinals.Distinct().Count() != ordinals.Length)
            throw new InvalidDataException("DLC list occurrence ordinals must be nonempty, distinct, and ascending.");
        var mountPath = RequireText(item, "normalizedMountPath");
        var occurrences = RequireArray(item, "dlclistOccurrences").EnumerateArray()
            .Select((occurrence, ordinal) => ParseDlcListOccurrence(
                occurrence, ordinal, dlcListArtifact.SourceCoordinate, mountPath))
            .ToImmutableArray();
        if (!occurrences.Select(value => value.Ordinal).SequenceEqual(ordinals))
            throw new InvalidDataException("DLC list occurrence details do not close over their declared ordinals.");
        var container = RequireText(item, "physicalContainerCoordinate");
        var declaration = RequireText(item, "pedMetadataDeclarationLocator");
        ValidateResourceCoordinate(container, "physicalContainerCoordinate", requireMemberBoundary: false);
        var setup = ParseArtifactCoordinate(item.GetProperty("setupArtifact"), "DLC setup artifact", DlcSetupFormatId, artifacts);
        var content = ParseArtifactCoordinate(item.GetProperty("contentArtifact"), "DLC content artifact", DlcContentFormatId, artifacts);
        var declarationPrefix = "rpf7-member:" + content.SourceCoordinate +
            "#/CDataFileMgr__ContentsOfDataFileXml[1]/dataFiles[1]/Item[";
        var declarationOrdinalText = declaration.StartsWith(declarationPrefix, StringComparison.Ordinal) &&
                                     declaration.EndsWith(']')
            ? declaration[declarationPrefix.Length..^1]
            : string.Empty;
        if (!container.EndsWith($"/{packName}/dlc.rpf", StringComparison.Ordinal) ||
            !setup.SourceCoordinate.StartsWith(container + "!/", StringComparison.Ordinal) ||
            !content.SourceCoordinate.StartsWith(container + "!/", StringComparison.Ordinal) ||
            !setup.SourceCoordinate.EndsWith("/setup2.xml", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(declarationOrdinalText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var declarationOrdinal) ||
            declarationOrdinal <= 0 ||
            !string.Equals(mountPath, $"dlcpacks:/{packName}/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DLC pack mount, setup, content, and declaration coordinates are inconsistent.");
        var artifact = ParseArtifactCoordinate(item.GetProperty("pedsArtifact"), "DLC peds artifact", DlcXmlFormatId, artifacts);
        if (!artifact.SourceCoordinate.StartsWith(container + "!/", StringComparison.Ordinal) ||
            !artifact.SourceCoordinate.EndsWith("/peds.meta", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A DLC Actor artifact is not beneath its exact physical DLC container.");
        var records = ParseDlcRecords(RequireArray(item, "records"), artifact.SourceCoordinate);
        if (records.IsEmpty) throw new InvalidDataException("A mounted DLC Actor source contains no supported records.");
        return new GtaVDlcPedSource(
            packName, ordinals, occurrences, mountPath, container, dlcListArtifact,
            setup, content, declaration, artifact, records);
    }

    private static GtaVDlcMountOccurrence ParseDlcListOccurrence(
        JsonElement value,
        int occurrenceIndex,
        string dlcListCoordinate,
        string normalizedMountPath)
    {
        var item = RequireObject(value, $"dlclistOccurrences[{occurrenceIndex}]");
        RequireProperties(item, "DLC list occurrence", "ordinal", "rawMountPath", "fieldLocator");
        var ordinal = RequireInt(item, "ordinal");
        var raw = RequireText(item, "rawMountPath");
        var locator = RequireText(item, "fieldLocator");
        var exactLocator = $"rpf7-member:{dlcListCoordinate}#/SMandatoryPacksData[1]/Paths[1]/*[{ordinal + 1}]";
        if (ordinal is < 0 or >= 104 ||
            !string.Equals(raw, normalizedMountPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(locator, exactLocator, StringComparison.Ordinal))
            throw new InvalidDataException("A DLC list occurrence is not the exact source path and XML field locator.");
        return new GtaVDlcMountOccurrence(ordinal, raw, locator);
    }

    private static ImmutableArray<GtaVDlcPedFact> ParseDlcRecords(JsonElement array, string coordinate)
    {
        var values = ImmutableArray.CreateBuilder<GtaVDlcPedFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var element in array.EnumerateArray())
        {
            var item = RequireObject(element, $"DLC records[{ordinal}]");
            RequireProperties(item, "DLC record", "ordinal", "nameExact", "pedtypeExact", "recordLocator", "nameFieldLocator", "pedtypeFieldLocator");
            if (RequireInt(item, "ordinal") != ordinal) throw new InvalidDataException("DLC Actor ordinals must be contiguous and zero-based.");
            var name = RequireText(item, "nameExact");
            var pedtype = RequireText(item, "pedtypeExact");
            if (!seen.Add(name)) throw new InvalidDataException("DLC Actor Name values must be exact and unique per source.");
            var recordPath = $"/CPedModelInfo__InitDataList[1]/InitDatas[1]/Item[{ordinal + 1}]";
            var record = $"rpf7-member:{coordinate}#{recordPath}";
            RequireText(item, "recordLocator", record);
            RequireText(item, "nameFieldLocator", record + "/Name[1]");
            RequireText(item, "pedtypeFieldLocator", record + "/Pedtype[1]");
            values.Add(new GtaVDlcPedFact(
                ordinal, name, pedtype, record, record + "/Name[1]", record + "/Pedtype[1]"));
            ordinal++;
        }
        return values.ToImmutable();
    }

    private static void RequireExactArtifact(FrozenSourceArtifact artifact, GtaVActorArtifactCoordinate coordinate)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Id != coordinate.ArtifactId || artifact.Digest != coordinate.Digest ||
            artifact.ExactBytes.Length != coordinate.ByteLength ||
            !string.Equals(artifact.SourceCoordinate.ExactRepresentation, coordinate.SourceCoordinate, StringComparison.Ordinal) ||
            artifact.DeclaredFormat != coordinate.Format || ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != coordinate.Digest)
            throw new InvalidDataException("The requested Actor artifact differs from the sidecar-bound frozen Rockstar bytes.");
    }

    private static uint ParseHash(JsonElement root, string propertyName, bool allowZero)
    {
        var value = RequireText(root, propertyName);
        if (value.Length != 10 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var result) || !allowZero && result == 0)
            throw new InvalidDataException($"{propertyName} must be an exact nonnegative 0xXXXXXXXX hash.");
        return result;
    }

    private static void ValidateResourceCoordinate(string value, string name, bool requireMemberBoundary = true)
    {
        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Contains('#') ||
            value.Any(char.IsControl) || value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException($"{name} is not a normalized source coordinate.");
        if (requireMemberBoundary && !value.Contains("!/", StringComparison.Ordinal))
            throw new InvalidDataException($"{name} requires an explicit RPF member boundary.");
        foreach (var part in value.Split("!/", StringSplitOptions.None).SkipLast(1))
            if (!part.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{name} contains a non-RPF container boundary.");
    }

    public static SourceNativeIdentifier ResidentIdentity(uint hash)
    {
        if (hash == 0) throw new ArgumentOutOfRangeException(nameof(hash));
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier(
            "rockstar.gta-v.enhanced.resident-ped-model",
            "CPedModelInfo__InitData.NameHash",
            $"0x{hash:X8}", bytes.ToImmutableArray(),
            "grid.gta-v.joaat32-unsigned", 1);
    }

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{name} must be an object.");

    private static JsonElement RequireArray(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"{propertyName} must be an array.");
    }

    private static string RequireText(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{propertyName} must be text.");
        var text = value.GetString()!;
        if (text.Length == 0 || !string.Equals(text, text.Trim(), StringComparison.Ordinal) || text.Any(char.IsControl))
            throw new InvalidDataException($"{propertyName} must be nonempty exact text.");
        _ = StrictUtf8.GetBytes(text);
        return text;
    }

    private static void RequireText(JsonElement root, string propertyName, string expected)
    {
        if (!string.Equals(RequireText(root, propertyName), expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static int RequireInt(JsonElement root, string propertyName) =>
        RequireIntValue(root.GetProperty(propertyName), propertyName);

    private static int RequireIntValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"{name} must be an exact 32-bit integer.");
        return result;
    }

    private static void RequireInt(JsonElement root, string propertyName, int expected)
    {
        if (RequireInt(root, propertyName) != expected) throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static void RequireProperties(JsonElement value, string name, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var orderedExpected = expected.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
            throw new InvalidDataException($"{name} property set is not the exact v1 contract.");
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException($"{name} contains duplicate JSON properties.");
                ValidateNoDuplicateProperties(property.Value, name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item, name);
        }
    }
}

public sealed record GtaVActorArtifactCoordinate(
    SourceArtifactId ArtifactId,
    ContentDigest Digest,
    string SourceCoordinate,
    int ByteLength,
    KnowledgeFormatCoordinate Format,
    int? SourceTier);

public sealed record GtaVActorCorpusIndexReceiptBinding(
    string SchemaId,
    int SchemaVersion,
    string DecoderMethodId,
    string DecoderVersion,
    string DecoderArtifactSha256,
    long ByteLength,
    ContentDigest Digest);

public sealed record GtaVResidentPedSource(
    GtaVActorArtifactCoordinate Artifact,
    ImmutableArray<GtaVResidentPedFact> Records);

public sealed record GtaVResidentPedFact(
    int Ordinal,
    uint NameHash,
    uint PedtypeHash,
    string RecordLocator,
    string NameFieldLocator,
    string PedtypeFieldLocator);

public sealed record GtaVDlcPedSource(
    string PackNameExact,
    ImmutableArray<int> DlclistOccurrenceOrdinals,
    ImmutableArray<GtaVDlcMountOccurrence> DlclistOccurrences,
    string NormalizedMountPath,
    string PhysicalContainerCoordinate,
    GtaVActorArtifactCoordinate DlcListArtifact,
    GtaVActorArtifactCoordinate SetupArtifact,
    GtaVActorArtifactCoordinate ContentArtifact,
    string PedMetadataDeclarationLocator,
    GtaVActorArtifactCoordinate Artifact,
    ImmutableArray<GtaVDlcPedFact> Records);

public sealed record GtaVDlcMountOccurrence(
    int Ordinal,
    string RawMountPath,
    string FieldLocator);

public sealed record GtaVDlcPedFact(
    int Ordinal,
    string NameExact,
    string PedtypeExact,
    string RecordLocator,
    string NameFieldLocator,
    string PedtypeFieldLocator);
