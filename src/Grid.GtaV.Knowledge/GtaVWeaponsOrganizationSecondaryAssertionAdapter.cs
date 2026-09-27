using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Attaches exact weapons.meta organization fields to the established CWeaponInfo records.
/// Source-native values remain identifier-only unless a separately verified terminology source
/// supplies their player-facing labels.
/// </summary>
public sealed class GtaVWeaponsOrganizationSecondaryAssertionAdapter
{
    public const string ParserId = "grid.gta-v-enhanced.weapons-organization-secondary";
    public const string ParserVersion = "1";
    public const string MappingMethodId = "grid.gta-v.weapons-meta.organization-membership";
    public const string MappingMethodVersion = "1";

    public static CanonicalOrganizationalSemanticId GroupDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.group");
    public static CanonicalOrganizationalSemanticId SlotDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.slot");
    public static CanonicalOrganizationalSemanticId NavigateOrderEntryDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.slot-navigate-order-entry");
    public static CanonicalOrganizationalSemanticId NavigateOrderNumberDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.slot-navigate-order-number");
    public static CanonicalOrganizationalSemanticId BestOrderEntryDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.slot-best-order-entry");
    public static CanonicalOrganizationalSemanticId BestOrderNumberDimension { get; } =
        new("rockstar.gta-v.weapons-meta.dimension.slot-best-order-number");

    private const string Coordinate = "common.rpf!/data/ai/weapons.meta";
    private const string ValueNamespace = "rockstar.gta-v.weapons-meta.organization";
    private readonly GtaVSupportedSourceCorpusIndex? _corpusIndex;

    public GtaVWeaponsOrganizationSecondaryAssertionAdapter(ContentDigest adapterArtifactDigest, GtaVSupportedSourceCorpusIndex? corpusIndex = null)
    {
        _corpusIndex = corpusIndex;
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.weapons-organization-secondary"),
            "1", adapterArtifactDigest, 1, "weapons-meta-organization-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [new SupportedKnowledgeFormat(
                GtaVWeaponsMetaKnowledgeAdapter.FormatId,
                GtaVWeaponsMetaKnowledgeAdapter.ExactFormatVersion,
                ["rpf7-member"], ["CWeaponInfoBlob"], [KnowledgeKind.Item],
                supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false)],
            new KnowledgeAdapterResourceLimits(
                GtaVWeaponsMetaKnowledgeAdapter.MaximumArtifactBytes, 1, 100_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVSecondaryAssertionBatch Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(artifact);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame ||
            !string.Equals(artifact.SourceCoordinate.ExactRepresentation, Coordinate, StringComparison.Ordinal) ||
            artifact.DeclaredFormat != GtaVWeaponsMetaKnowledgeAdapter.Format ||
            SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id ||
            ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest)
            throw new InvalidDataException("Weapon organization requires the exact acquired Enhanced weapons.meta artifact.");

        var parsed = Parse(artifact, _corpusIndex?.GetXmlDocument(artifact, GtaVWeaponsMetaKnowledgeAdapter.MaximumArtifactBytes));
        var records = origin.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId && value.Kind == KnowledgeKind.Item &&
                            value.NativeIdentity.ObjectType == "CWeaponInfo")
            .ToDictionary(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal);
        if (records.Count == 0 || parsed.Weapons.Length != records.Count)
            throw new InvalidDataException("Weapon organization must cover the complete established CWeaponInfo set.");

        var originRevisionIds = records.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (originRevisionIds.Length != 1 || !origin.SourceRevisions.Any(value =>
                value.Revision.Id == originRevisionIds[0] && value.Revision.ArtifactIds.Contains(artifact.Id)))
            throw new InvalidDataException("The established weapon records do not share the exact acquired origin.");
        var originRevision = origin.SourceRevisions.Single(value => value.Revision.Id == originRevisionIds[0]);
        var source = origin.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var revisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id, null, [artifact.Id], Descriptor.RevisionId);
        var revision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, [artifact.Id]),
            Descriptor.RevisionId, sourceScope, [artifact.FormatBinding]);

        var assertions = ImmutableArray.CreateBuilder<CanonicalOrganizationalValueAssertion>();
        var evidence = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();

        foreach (var weapon in parsed.Weapons.OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            if (!records.TryGetValue(weapon.Name, out var target))
                throw new InvalidDataException("A weapon organization assertion cannot target an absent established record.");

            AddDirectAssertion(target, revisionId, artifact, origin,
                CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
                "WheelSlot", weapon.WheelSlot, weapon.WheelSlotFieldPath, weapon.NameFieldPath,
                assertions, evidence, bindings, links, envelopes);
            if (weapon.Group is not null)
                AddDirectAssertion(target, revisionId, artifact, origin,
                    GroupDimension,
                    "Group", weapon.Group, weapon.GroupFieldPath!, weapon.NameFieldPath,
                    assertions, evidence, bindings, links, envelopes);
            if (weapon.Slot is null) continue;
            AddDirectAssertion(target, revisionId, artifact, origin,
                SlotDimension,
                "Slot", weapon.Slot, weapon.SlotFieldPath!, weapon.NameFieldPath,
                assertions, evidence, bindings, links, envelopes);

            foreach (var entry in parsed.NavigateOrder.Where(value =>
                         string.Equals(value.Entry, weapon.Slot, StringComparison.Ordinal)))
                AddOrderAssertions(target, revisionId, artifact, origin, weapon, entry,
                    NavigateOrderEntryDimension,
                    NavigateOrderNumberDimension,
                    "SlotNavigateOrder", assertions, evidence, bindings, links, envelopes);
            foreach (var entry in parsed.BestOrder.Where(value =>
                         string.Equals(value.Entry, weapon.Slot, StringComparison.Ordinal)))
                AddOrderAssertions(target, revisionId, artifact, origin, weapon, entry,
                    BestOrderEntryDimension,
                    BestOrderNumberDimension,
                    "SlotBestOrder", assertions, evidence, bindings, links, envelopes);
        }

        return new GtaVSecondaryAssertionBatch(
            Descriptor, revision, [], [], [],
            assertions.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            evidence.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            envelopes.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static void AddDirectAssertion(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        CanonicalCatalogPayload origin,
        CanonicalOrganizationalSemanticId dimension,
        string objectType,
        string exactValue,
        string claimFieldPath,
        string nameFieldPath,
        ImmutableArray<CanonicalOrganizationalValueAssertion>.Builder assertions,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var valueIdentity = ValueIdentity(objectType, exactValue);
        var assertion = Assertion(target, revisionId, dimension, valueIdentity, claimFieldPath);
        assertions.Add(assertion);
        var claimReceipt = Receipt(revisionId, artifact, target.NativeIdentity, claimFieldPath);
        AddEnvelope(target, revisionId, artifact, origin, assertion,
            target.NativeIdentity, nameFieldPath, target.NativeIdentity, nameFieldPath,
            CrossSourceTargetLinkKind.ExactSourceNativeIdentity,
            claimReceipt, evidence, bindings, links, envelopes);
    }

    private static void AddOrderAssertions(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        CanonicalCatalogPayload origin,
        ParsedWeapon weapon,
        ParsedOrderEntry entry,
        CanonicalOrganizationalSemanticId entryDimension,
        CanonicalOrganizationalSemanticId numberDimension,
        string orderType,
        ImmutableArray<CanonicalOrganizationalValueAssertion>.Builder assertions,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var slotIdentity = ValueIdentity("Slot", weapon.Slot!);
        var orderObject = SourceNativeIdentifier.FromExactUtf8(
            ValueNamespace, orderType + ".Item", entry.ItemFieldPath,
            "grid.gta-v.weapons-meta.xml-coordinate", 1);
        var entryAssertion = Assertion(target, revisionId, entryDimension,
            ValueIdentity(orderType + ".Entry", entry.Entry), entry.EntryFieldPath);
        assertions.Add(entryAssertion);
        AddEnvelope(target, revisionId, artifact, origin, entryAssertion,
            slotIdentity, weapon.SlotFieldPath!, slotIdentity, entry.EntryFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            Receipt(revisionId, artifact, orderObject, entry.EntryFieldPath),
            evidence, bindings, links, envelopes);

        var numberAssertion = Assertion(target, revisionId, numberDimension,
            ValueIdentity(orderType + ".OrderNumber", entry.OrderNumber), entry.OrderNumberFieldPath);
        assertions.Add(numberAssertion);
        AddEnvelope(target, revisionId, artifact, origin, numberAssertion,
            slotIdentity, weapon.SlotFieldPath!, slotIdentity, entry.EntryFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            Receipt(revisionId, artifact, orderObject, entry.OrderNumberFieldPath),
            evidence, bindings, links, envelopes);
    }

    private static CanonicalOrganizationalValueAssertion Assertion(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        CanonicalOrganizationalSemanticId dimension,
        SourceNativeIdentifier value,
        string fieldPath)
    {
        var id = CanonicalOrganizationalValueAssertionId.DeriveV1(
            target.Id, revisionId, dimension, value, null,
            MappingMethodId, MappingMethodVersion, fieldPath);
        return new CanonicalOrganizationalValueAssertion(
            id, target.Id, revisionId, dimension, value, null,
            MappingMethodId, MappingMethodVersion, fieldPath);
    }

    private static void AddEnvelope(
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        CanonicalCatalogPayload origin,
        CanonicalOrganizationalValueAssertion assertion,
        SourceNativeIdentifier targetCoordinate,
        string targetFieldPath,
        SourceNativeIdentifier assertingCoordinate,
        string assertingFieldPath,
        CrossSourceTargetLinkKind linkKind,
        CatalogFileEvidenceReceipt claimReceipt,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var targetLinkReceipt = Receipt(revisionId, artifact, target.NativeIdentity, targetFieldPath);
        var assertingLinkReceipt = Receipt(revisionId, artifact, assertingCoordinate, assertingFieldPath);
        evidence.Add(claimReceipt);
        evidence.Add(targetLinkReceipt);
        evidence.Add(assertingLinkReceipt);
        var claimContent = EvidenceClaimContentId.DeriveV1(assertion);
        var claimBinding = Binding(claimReceipt.Id, EvidenceClaimKind.OrganizationalValue,
            target.Id, revisionId, assertion.SourceFieldPath, claimContent);
        bindings.Add(claimBinding);

        var linkId = CrossSourceTargetLinkClaimId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            targetCoordinate, artifact.Id, targetFieldPath,
            revisionId, assertingCoordinate, artifact.Id, assertingFieldPath,
            linkKind, MappingMethodId, MappingMethodVersion);
        var link = new CrossSourceTargetLinkClaim(
            linkId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            targetCoordinate, artifact.Id, targetFieldPath,
            revisionId, assertingCoordinate, artifact.Id, assertingFieldPath,
            linkKind, MappingMethodId, MappingMethodVersion);
        links.Add(link);
        var linkContent = EvidenceClaimContentId.DeriveV1(link);
        var targetLinkBinding = Binding(targetLinkReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, targetFieldPath, linkContent);
        var assertingLinkBinding = Binding(assertingLinkReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, assertingFieldPath, linkContent);
        bindings.Add(targetLinkBinding);
        bindings.Add(assertingLinkBinding);

        var originBinding = origin.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == target.Id &&
            value.SourceRevisionId == target.SourceRevisionId &&
            value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
            value.ClaimContentId is null);
        var receiptIds = ImmutableArray.Create(
            originBinding.EvidenceReceiptId, claimReceipt.Id,
            targetLinkReceipt.Id, assertingLinkReceipt.Id).Distinct().ToImmutableArray();
        var bindingIds = ImmutableArray.Create(
            originBinding.Id, claimBinding.Id,
            targetLinkBinding.Id, assertingLinkBinding.Id).Distinct().ToImmutableArray();
        var envelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, CrossSourceCanonicalAssertionKind.OrganizationalValue,
            claimContent, link.Id, link.LinkKind, assertingCoordinate,
            MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []);
        envelopes.Add(new CrossSourceCanonicalAssertion(
            envelopeId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, CrossSourceCanonicalAssertionKind.OrganizationalValue,
            claimContent, link.Id, link.LinkKind, assertingCoordinate,
            MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []));
    }

    private static CatalogFileEvidenceReceipt Receipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string fieldPath)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            ParserId, ParserVersion, nativeObject.ExactRepresentation, fieldPath,
            null, null, null, artifact.ObservedAtUtc, nativeObject);
        return new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
    }

    private static EvidenceBinding Binding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string fieldPath,
        EvidenceClaimContentId claimContent) => new(
        EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, fieldPath, claimContent),
        receiptId, kind, recordId, revisionId, fieldPath, claimContent);

    private static SourceNativeIdentifier ValueIdentity(string objectType, string value) =>
        SourceNativeIdentifier.FromExactUtf8(
            ValueNamespace, objectType, value,
            "grid.gta-v.weapons-meta.exact-utf8", 1);

    private static ParsedDocument Parse(FrozenSourceArtifact artifact, XDocument? indexedDocument = null)
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
        var root = document.Root;
        if (root?.Name != XName.Get("CWeaponInfoBlob") ||
            root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Only exact non-namespaced CWeaponInfoBlob XML is supported.");

        var weapons = ImmutableArray.CreateBuilder<ParsedWeapon>();
        foreach (var item in root.Descendants("Item").Where(value => value.Attribute("type")?.Value == "CWeaponInfo"))
        {
            var name = Single(item, "Name", requiredNonEmpty: true);
            var wheel = Single(item, "WheelSlot", requiredNonEmpty: true);
            var group = Single(item, "Group", requiredNonEmpty: false);
            var slot = Single(item, "Slot", requiredNonEmpty: false);
            weapons.Add(new ParsedWeapon(
                name.Value, Field(artifact, name), wheel.Value, Field(artifact, wheel),
                EmptyToNull(group.Value), Field(artifact, group),
                EmptyToNull(slot.Value), Field(artifact, slot)));
        }
        if (weapons.Count == 0 || weapons.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != weapons.Count)
            throw new InvalidDataException("Weapon organization requires distinct CWeaponInfo identities.");

        var navigate = ParseOrder(root, artifact, "SlotNavigateOrder");
        var best = ParseOrder(root, artifact, "SlotBestOrder");
        return new ParsedDocument(weapons.ToImmutable(), navigate, best);
    }

    private static ImmutableArray<ParsedOrderEntry> ParseOrder(
        XElement root,
        FrozenSourceArtifact artifact,
        string elementName)
    {
        var collections = root.Elements(elementName).ToArray();
        if (collections.Length != 1)
            throw new InvalidDataException($"Exactly one {elementName} collection is required.");
        var result = ImmutableArray.CreateBuilder<ParsedOrderEntry>();
        foreach (var item in collections[0].Descendants("Item"))
        {
            var entries = item.Elements("Entry").ToArray();
            var numbers = item.Elements("OrderNumber").ToArray();
            if (entries.Length == 0 && numbers.Length == 0) continue;
            if (entries.Length != 1 || numbers.Length != 1 || numbers[0].Attribute("value") is not XAttribute number)
                throw new InvalidDataException($"Every {elementName} entry requires one Entry and one OrderNumber value.");
            if (string.IsNullOrEmpty(entries[0].Value) || string.IsNullOrEmpty(number.Value))
                throw new InvalidDataException($"{elementName} values cannot be empty.");
            result.Add(new ParsedOrderEntry(
                entries[0].Value, number.Value,
                Field(artifact, item), Field(artifact, entries[0]),
                Field(artifact, numbers[0]) + "/@value"));
        }
        if (result.Count == 0) throw new InvalidDataException($"{elementName} cannot be empty.");
        return result.ToImmutable();
    }

    private static XElement Single(XElement parent, string name, bool requiredNonEmpty)
    {
        var values = parent.Elements(name).ToArray();
        if (values.Length != 1 || requiredNonEmpty && string.IsNullOrEmpty(values[0].Value))
            throw new InvalidDataException($"Every CWeaponInfo requires exactly one valid {name} field.");
        return values[0];
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static string Field(FrozenSourceArtifact artifact, XElement element) =>
        $"rpf7-member:{artifact.SourceCoordinate.ExactRepresentation}#{Path(element)}";
    private static string Path(XElement element) => string.Concat(
        element.AncestorsAndSelf().Reverse().Select(value =>
            $"/{value.Name.LocalName}[{(value.Parent is null ? 1 : value.ElementsBeforeSelf(value.Name).Count() + 1)}]"));

    private sealed record ParsedDocument(
        ImmutableArray<ParsedWeapon> Weapons,
        ImmutableArray<ParsedOrderEntry> NavigateOrder,
        ImmutableArray<ParsedOrderEntry> BestOrder);
    private sealed record ParsedWeapon(
        string Name,
        string NameFieldPath,
        string WheelSlot,
        string WheelSlotFieldPath,
        string? Group,
        string? GroupFieldPath,
        string? Slot,
        string? SlotFieldPath);
    private sealed record ParsedOrderEntry(
        string Entry,
        string OrderNumber,
        string ItemFieldPath,
        string EntryFieldPath,
        string OrderNumberFieldPath);
}
