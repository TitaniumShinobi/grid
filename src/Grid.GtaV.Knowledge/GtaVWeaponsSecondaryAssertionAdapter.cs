using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Preproduction-only enrichment for already-registered GTA V Enhanced weapon records.
/// It never derives or replaces a KnowledgeRecordId. Exact weapons.meta fields are linked
/// to exact American GXT2 entries through schema-v6 cross-source assertion envelopes.
/// </summary>
public sealed class GtaVWeaponsSecondaryAssertionAdapter
{
    public const string Gxt2FormatId = "rockstar.gta-v.gxt2-binary";
    public const string Gxt2FormatVersion = "1";
    public const string Rpf7FormatId = "rockstar.rpf7-container";
    public const string Rpf7FormatVersion = "1";
    public const string ParserId = "grid.gta-v.weapons-gxt2-secondary";
    public const string ParserVersion = "1";
    public const string MappingMethodId = "grid.gta-v.human-name-hash-gxt2.joaat32";
    public const string MappingMethodVersion = "1";
    public const string ClassificationMethodId = "grid.gta-v.weapons-meta.cweaponinfo-type";
    public const string ClassificationMethodVersion = "1";

    private const uint Gxt2Magic = 0x47585432;
    private const string WeaponsCoordinate = "common.rpf!/data/ai/weapons.meta";
    private const string BaseLanguageRpfCoordinate = "x64b.rpf!/data/lang/american_rel.rpf";
    private const string BaseGxt2Coordinate = "x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2";
    private const string RecordComparisonMethod = "grid.gta-v.meta-name.exact-utf8";
    private readonly GtaVSupportedSourceCorpusIndex? _corpusIndex;

    public GtaVWeaponsSecondaryAssertionAdapter(ContentDigest adapterArtifactDigest, GtaVSupportedSourceCorpusIndex? corpusIndex = null)
    {
        _corpusIndex = corpusIndex;
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.weapons-gxt2-secondary"),
            "1",
            adapterArtifactDigest,
            1,
            "weapon-family-and-human-name-gxt2-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                new SupportedKnowledgeFormat(
                    GtaVWeaponsMetaKnowledgeAdapter.FormatId,
                    GtaVWeaponsMetaKnowledgeAdapter.ExactFormatVersion,
                    ["rpf7-member"], ["CWeaponInfoBlob"], [KnowledgeKind.Item],
                    supportsTerminology: false, supportsRelationships: true, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    Rpf7FormatId, Rpf7FormatVersion,
                    ["rpf7-member"], ["Rpf7Container"], [],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    Gxt2FormatId, Gxt2FormatVersion,
                    ["nested-rpf7-member"], ["GXT2"], [KnowledgeKind.Item],
                    supportsTerminology: true, supportsRelationships: false, supportsHierarchy: false),
            ],
            new KnowledgeAdapterResourceLimits(
                64L * 1024 * 1024,
                maximumArtifacts: 3,
                maximumKnowledgeRecords: 100_000,
                maximumRelationships: 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVSecondaryAssertionBatch Extract(
        CanonicalCatalogPayload originPayload,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact weaponsArtifact,
        FrozenSourceArtifact baseLanguageRpfArtifact,
        FrozenSourceArtifact baseGxt2Artifact)
    {
        ArgumentNullException.ThrowIfNull(originPayload);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(weaponsArtifact);
        ArgumentNullException.ThrowIfNull(baseLanguageRpfArtifact);
        ArgumentNullException.ThrowIfNull(baseGxt2Artifact);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Secondary GTA assertions require the exact Enhanced base-game scope.");
        RequireArtifact(weaponsArtifact, WeaponsCoordinate, GtaVWeaponsMetaKnowledgeAdapter.Format);
        RequireArtifact(baseLanguageRpfArtifact, BaseLanguageRpfCoordinate,
            new KnowledgeFormatCoordinate(Rpf7FormatId, Rpf7FormatVersion));
        RequireArtifact(baseGxt2Artifact, BaseGxt2Coordinate,
            new KnowledgeFormatCoordinate(Gxt2FormatId, Gxt2FormatVersion));

        var parsedWeapons = ParseWeapons(
            weaponsArtifact,
            _corpusIndex?.GetXmlDocument(weaponsArtifact, GtaVWeaponsMetaKnowledgeAdapter.MaximumArtifactBytes));
        var gxt = _corpusIndex?.GetGxt2(baseGxt2Artifact, 64L * 1024 * 1024)
            ?? ParseGxt2(baseGxt2Artifact);
        var records = originPayload.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId && value.Kind == KnowledgeKind.Item)
            .ToDictionary(value => (value.NativeIdentity.ObjectType, value.NativeIdentity.ExactRepresentation));
        var originRevisionIds = records.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (originRevisionIds.Length != 1 ||
            !originPayload.SourceRevisions.Any(value =>
                value.Revision.Id == originRevisionIds[0] &&
                value.Revision.ArtifactIds.Contains(weaponsArtifact.Id)))
            throw new InvalidDataException("The established Item records do not share the exact acquired weapons.meta origin.");
        var originRevision = originPayload.SourceRevisions.Single(value => value.Revision.Id == originRevisionIds[0]);
        var source = originPayload.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var artifacts = ImmutableArray.Create(
            weaponsArtifact.Id,
            baseLanguageRpfArtifact.Id,
            baseGxt2Artifact.Id);
        var assertingRevisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id, null, artifacts, Descriptor.RevisionId);
        var assertingRevision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(assertingRevisionId, source.Id, null, artifacts),
            Descriptor.RevisionId,
            sourceScope,
            [
                weaponsArtifact.FormatBinding,
                baseLanguageRpfArtifact.FormatBinding,
                baseGxt2Artifact.FormatBinding,
            ]);

        var terminology = ImmutableArray.CreateBuilder<TerminologyAssertion>();
        var classifications = ImmutableArray.CreateBuilder<CanonicalSemanticClassificationAssertion>();
        var evidence = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();

        foreach (var parsed in parsedWeapons.OrderBy(value => value.NativeKey, StringComparer.Ordinal))
        {
            if (!records.TryGetValue((parsed.ObjectType, parsed.NativeKey), out var target))
                throw new InvalidDataException("A secondary weapons assertion cannot target an absent established record.");
            if (parsed.ObjectType != "CWeaponInfo") continue;

            var classification = CreateWeaponClassification(target, assertingRevisionId, parsed.TypeFieldLocator);
            classifications.Add(classification);
            AddExactNativeEnvelope(
                target, assertingRevisionId, weaponsArtifact, parsed.TypeFieldLocator,
                classification, EvidenceClaimKind.SemanticClassification,
                CrossSourceCanonicalAssertionKind.SemanticClassification,
                ClassificationMethodId, ClassificationMethodVersion,
                originPayload, evidence, bindings, links, envelopes);

            if (parsed.HumanNameHash is null || parsed.HumanNameHash == "WT_INVALID") continue;
            var hash = ComputeJoaat32(parsed.HumanNameHash);
            if (!gxt.TryGetValue(hash, out var entry)) continue;
            var hashIdentity = CreateGxtHashIdentity(hash);
            var terminologyAssertion = new TerminologyAssertion(
                target.Id,
                assertingRevisionId,
                TerminologyAssertionRole.PrimaryName,
                entry.Text,
                entry.FieldLocator,
                "en-US",
                hashIdentity);
            terminology.Add(terminologyAssertion);
            AddLabelEnvelope(
                target, assertingRevisionId, weaponsArtifact, baseGxt2Artifact,
                parsed.HumanNameFieldLocator!, entry, hashIdentity, terminologyAssertion,
                originPayload, evidence, bindings, links, envelopes);
        }

        return new GtaVSecondaryAssertionBatch(
            Descriptor,
            assertingRevision,
            [
                new SourceArtifactRecord(baseLanguageRpfArtifact.Id, baseLanguageRpfArtifact.Digest),
                new SourceArtifactRecord(baseGxt2Artifact.Id, baseGxt2Artifact.Digest),
            ],
            terminology.ToImmutable(),
            classifications.ToImmutable(),
            [],
            evidence.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.ToImmutable(),
            envelopes.ToImmutable());
    }

    private static CanonicalSemanticClassificationAssertion CreateWeaponClassification(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        string fieldPath)
    {
        var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
            target.Id, revisionId, CanonicalProjectionSemantics.ItemWeapons,
            "grid.item-family", "1", ClassificationMethodId, ClassificationMethodVersion, fieldPath);
        return new CanonicalSemanticClassificationAssertion(
            id, target.Id, revisionId, CanonicalProjectionSemantics.ItemWeapons,
            "grid.item-family", "1", ClassificationMethodId, ClassificationMethodVersion, fieldPath);
    }

    private static void AddExactNativeEnvelope(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        string fieldPath,
        CanonicalSemanticClassificationAssertion assertion,
        EvidenceClaimKind claimKind,
        CrossSourceCanonicalAssertionKind assertionKind,
        string methodId,
        string methodVersion,
        CanonicalCatalogPayload origin,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var claimContent = EvidenceClaimContentId.DeriveV1(assertion);
        var receipt = AddReceipt(revisionId, artifact, target.NativeIdentity, fieldPath, null, null, evidence);
        var claimBinding = AddBinding(receipt.Id, claimKind, target.Id, revisionId, fieldPath, claimContent, bindings);
        var link = CreateLink(
            target, revisionId, target.NativeIdentity, artifact.Id, fieldPath,
            target.NativeIdentity, artifact.Id, fieldPath,
            CrossSourceTargetLinkKind.ExactSourceNativeIdentity, methodId, methodVersion);
        links.Add(link);
        var linkContent = EvidenceClaimContentId.DeriveV1(link);
        var linkBinding = AddBinding(receipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, fieldPath, linkContent, bindings);
        AddEnvelope(target, revisionId, assertionKind, claimContent, link,
            target.NativeIdentity, methodId, methodVersion,
            origin, [receipt.Id], [claimBinding.Id, linkBinding.Id], envelopes);
    }

    private static void AddLabelEnvelope(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact weaponsArtifact,
        FrozenSourceArtifact gxtArtifact,
        string targetFieldPath,
        GtaVIndexedGxt2Entry entry,
        SourceNativeIdentifier hashIdentity,
        TerminologyAssertion assertion,
        CanonicalCatalogPayload origin,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var claimContent = EvidenceClaimContentId.DeriveV1(assertion);
        var targetReceipt = AddReceipt(
            revisionId, weaponsArtifact, target.NativeIdentity, targetFieldPath, null, null, evidence);
        var textBytes = Encoding.UTF8.GetBytes(entry.Text);
        var gxtReceipt = AddReceipt(
            revisionId, gxtArtifact, hashIdentity, entry.FieldLocator,
            entry.TextOffset, textBytes.Length, evidence, ContentDigest.ComputeSha256(textBytes));
        var claimBinding = AddBinding(gxtReceipt.Id, EvidenceClaimKind.Terminology,
            target.Id, revisionId, entry.FieldLocator, claimContent, bindings);
        var link = CreateLink(
            target, revisionId, hashIdentity, weaponsArtifact.Id, targetFieldPath,
            hashIdentity, gxtArtifact.Id, entry.FieldLocator,
            CrossSourceTargetLinkKind.ExactLabelOrHashKey, MappingMethodId, MappingMethodVersion);
        links.Add(link);
        var linkContent = EvidenceClaimContentId.DeriveV1(link);
        var targetLinkBinding = AddBinding(targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, targetFieldPath, linkContent, bindings);
        var assertingLinkBinding = AddBinding(gxtReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, entry.FieldLocator, linkContent, bindings);
        AddEnvelope(target, revisionId, CrossSourceCanonicalAssertionKind.Terminology,
            claimContent, link, hashIdentity, MappingMethodId, MappingMethodVersion,
            origin, [targetReceipt.Id, gxtReceipt.Id],
            [claimBinding.Id, targetLinkBinding.Id, assertingLinkBinding.Id], envelopes);
    }

    private static CrossSourceTargetLinkClaim CreateLink(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        SourceNativeIdentifier targetCoordinate,
        SourceArtifactId targetArtifactId,
        string targetFieldPath,
        SourceNativeIdentifier assertingCoordinate,
        SourceArtifactId assertingArtifactId,
        string assertingFieldPath,
        CrossSourceTargetLinkKind kind,
        string methodId,
        string methodVersion)
    {
        var id = CrossSourceTargetLinkClaimId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            targetCoordinate, targetArtifactId, targetFieldPath,
            revisionId, assertingCoordinate, assertingArtifactId, assertingFieldPath,
            kind, methodId, methodVersion);
        return new CrossSourceTargetLinkClaim(
            id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            targetCoordinate, targetArtifactId, targetFieldPath,
            revisionId, assertingCoordinate, assertingArtifactId, assertingFieldPath,
            kind, methodId, methodVersion);
    }

    private static void AddEnvelope(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        CrossSourceCanonicalAssertionKind kind,
        EvidenceClaimContentId claimContent,
        CrossSourceTargetLinkClaim link,
        SourceNativeIdentifier exactLinkKey,
        string methodId,
        string methodVersion,
        CanonicalCatalogPayload origin,
        ImmutableArray<EvidenceReceiptId> secondaryReceiptIds,
        ImmutableArray<EvidenceBindingId> secondaryBindingIds,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var originBinding = origin.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == target.Id &&
            value.SourceRevisionId == target.SourceRevisionId &&
            value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
            value.ClaimContentId is null);
        var receiptIds = secondaryReceiptIds.Add(originBinding.EvidenceReceiptId);
        var bindingIds = secondaryBindingIds.Add(originBinding.Id);
        var id = CrossSourceCanonicalAssertionId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, kind, claimContent, link.Id, link.LinkKind, exactLinkKey,
            methodId, methodVersion, receiptIds, bindingIds, [], []);
        envelopes.Add(new CrossSourceCanonicalAssertion(
            id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, kind, claimContent, link.Id, link.LinkKind, exactLinkKey,
            methodId, methodVersion, receiptIds, bindingIds, [], []));
    }

    private static CatalogFileEvidenceReceipt AddReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string fieldPath,
        long? offset,
        int? length,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ContentDigest? interpretedDigest = null)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            ParserId, ParserVersion, nativeObject.ExactRepresentation, fieldPath,
            offset, length, interpretedDigest, artifact.ObservedAtUtc, nativeObject);
        var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
        evidence.Add(receipt);
        return receipt;
    }

    private static EvidenceBinding AddBinding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string locator,
        EvidenceClaimContentId contentId,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
        var binding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, locator, contentId),
            receiptId, kind, recordId, revisionId, locator, contentId);
        bindings.Add(binding);
        return binding;
    }

    private static ImmutableArray<ParsedWeapon> ParseWeapons(FrozenSourceArtifact artifact, XDocument? indexedDocument = null)
    {
        XDocument document;
        if (indexedDocument is not null)
        {
            document = indexedDocument;
        }
        else
        try
        {
            using var stream = new MemoryStream(artifact.ExactBytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = GtaVWeaponsMetaKnowledgeAdapter.MaximumArtifactBytes,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException("The exact weapons.meta bytes are malformed.", exception);
        }
        if (document.Root?.Name.LocalName != "CWeaponInfoBlob" ||
            document.Root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("The secondary adapter accepts only exact non-namespaced CWeaponInfoBlob XML.");

        var result = ImmutableArray.CreateBuilder<ParsedWeapon>();
        foreach (var item in document.Descendants("Item"))
        {
            var type = item.Attribute("type")?.Value;
            if (type is not ("CWeaponInfo" or "CAmmoInfo")) continue;
            var names = item.Elements("Name").ToArray();
            if (names.Length != 1) throw new InvalidDataException("Each weapon/ammo record requires one exact Name field.");
            var humanNames = item.Elements("HumanNameHash").ToArray();
            if (humanNames.Length > 1) throw new InvalidDataException("A weapon record has multiple HumanNameHash fields.");
            var basePath = CreateXmlLocator(artifact.SourceCoordinate.ExactRepresentation, GetElementPath(item));
            result.Add(new ParsedWeapon(
                type, names[0].Value,
                basePath + "/@type",
                humanNames.Length == 1 ? humanNames[0].Value : null,
                humanNames.Length == 1
                    ? CreateXmlLocator(artifact.SourceCoordinate.ExactRepresentation, GetElementPath(humanNames[0]))
                    : null));
        }
        if (result.Select(value => (value.ObjectType, value.NativeKey)).Distinct().Count() != result.Count)
            throw new InvalidDataException("The weapons source contains duplicate native identities.");
        return result.ToImmutable();
    }

    private static ImmutableDictionary<uint, GtaVIndexedGxt2Entry> ParseGxt2(FrozenSourceArtifact artifact)
    {
        var bytes = artifact.ExactBytes.AsSpan();
        if (bytes.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Gxt2Magic)
            throw new InvalidDataException("The frozen language resource is not exact GXT2 data.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var tableSize = checked(8L + count * 8L);
        if (tableSize > int.MaxValue || tableSize + 8 > bytes.Length)
            throw new InvalidDataException("The GXT2 entry table is truncated.");
        var tableEnd = (int)tableSize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[tableEnd..]) != Gxt2Magic)
            throw new InvalidDataException("The GXT2 string block marker is invalid.");
        var endOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tableEnd + 4)..]);
        if (endOffsetValue > int.MaxValue)
            throw new InvalidDataException("The GXT2 string block end is out of range.");
        var endOffset = (int)endOffsetValue;
        if (endOffset < tableEnd + 8 || endOffset > bytes.Length)
            throw new InvalidDataException("The GXT2 string block end is out of range.");

        var result = ImmutableDictionary.CreateBuilder<uint, GtaVIndexedGxt2Entry>();
        var strictUtf8 = new UTF8Encoding(false, true);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = checked(8 + index * 8);
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[entryOffset..]);
            var textOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 4)..]);
            if (textOffsetValue > int.MaxValue)
                throw new InvalidDataException("A GXT2 text offset is out of range.");
            var textOffset = (int)textOffsetValue;
            if (textOffset < tableEnd + 8 || textOffset >= endOffset)
                throw new InvalidDataException("A GXT2 text offset is out of range.");
            var terminator = bytes[textOffset..endOffset].IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("A GXT2 value is not NUL terminated.");
            string text;
            try { text = strictUtf8.GetString(bytes.Slice(textOffset, terminator)); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("A GXT2 value is not strict UTF-8.", exception);
            }
            var field = $"rpf7-member:{artifact.SourceCoordinate.ExactRepresentation}#entries[0x{hash:X8}]/text";
            if (!result.TryAdd(hash, new GtaVIndexedGxt2Entry(hash, text, textOffset, terminator, field)))
                throw new InvalidDataException("The GXT2 resource contains duplicate label hashes.");
        }
        return result.ToImmutable();
    }

    private static uint ComputeJoaat32(string value)
    {
        if (value.Length == 0 || value.Any(character => character > 0x7f || char.IsControl(character)))
            throw new InvalidDataException("The v1 GTA label-key mapping accepts exact printable ASCII keys only.");
        uint hash = 0;
        foreach (var character in value)
        {
            var lowered = character is >= 'A' and <= 'Z' ? (byte)(character + 32) : (byte)character;
            hash += lowered;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    private static SourceNativeIdentifier CreateGxtHashIdentity(uint hash)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier(
            "rockstar.gta-v.gxt2", "LabelHash", $"0x{hash:X8}",
            bytes.ToImmutableArray(), "rockstar.gta-v.joaat32-little-endian", 1);
    }

    private static string GetElementPath(XElement element) => string.Concat(
        element.AncestorsAndSelf().Reverse().Select(value =>
            $"/{value.Name.LocalName}[{(value.Parent is null ? 1 : value.ElementsBeforeSelf(value.Name).Count() + 1)}]"));

    private static string CreateXmlLocator(string coordinate, string path) =>
        $"rpf7-member:{coordinate}#{path}";

    private static void RequireArtifact(
        FrozenSourceArtifact artifact,
        string coordinate,
        KnowledgeFormatCoordinate format)
    {
        if (!string.Equals(artifact.SourceCoordinate.ExactRepresentation, coordinate, StringComparison.Ordinal) ||
            artifact.DeclaredFormat != format ||
            SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id ||
            ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest)
            throw new InvalidDataException("A secondary assertion artifact has the wrong coordinate, format, or digest.");
    }

    private sealed record ParsedWeapon(
        string ObjectType,
        string NativeKey,
        string TypeFieldLocator,
        string? HumanNameHash,
        string? HumanNameFieldLocator);

}

public sealed record GtaVSecondaryAssertionBatch(
    GameKnowledgeAdapterDescriptor AdapterDescriptor,
    AdapterBoundCatalogSourceRevisionRecord SourceRevision,
    ImmutableArray<SourceArtifactRecord> AdditionalArtifacts,
    ImmutableArray<TerminologyAssertion> TerminologyAssertions,
    ImmutableArray<CanonicalSemanticClassificationAssertion> SemanticClassifications,
    ImmutableArray<CanonicalOrganizationalValueAssertion> OrganizationalValues,
    ImmutableArray<CatalogFileEvidenceReceipt> FileEvidenceReceipts,
    ImmutableArray<EvidenceBinding> EvidenceBindings,
    ImmutableArray<CrossSourceTargetLinkClaim> TargetLinkClaims,
    ImmutableArray<CrossSourceCanonicalAssertion> CrossSourceAssertions)
{
    /// <summary>
    /// Additional non-record sources needed to close secondary assertions. Existing local-only
    /// adapters leave this empty; provider-backed adapters use it to retain their exact source.
    /// </summary>
    public ImmutableArray<CatalogSourceRecord> AdditionalSources { get; init; } = [];

    /// <summary>
    /// Exact REFERENCE_VERIFIED receipts emitted by provider-backed secondary adapters.
    /// </summary>
    public ImmutableArray<CatalogReferenceEvidenceReceipt> ReferenceEvidenceReceipts { get; init; } = [];

    public ImmutableArray<UnresolvedCrossSourceClaimContent> UnresolvedCrossSourceClaimContents { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceEvidenceBinding> UnresolvedCrossSourceEvidenceBindings { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceAssertion> UnresolvedCrossSourceAssertions { get; init; } = [];
    public ImmutableArray<CanonicalCorrelationEnvelope> CorrelationEnvelopes { get; init; } = [];
}
