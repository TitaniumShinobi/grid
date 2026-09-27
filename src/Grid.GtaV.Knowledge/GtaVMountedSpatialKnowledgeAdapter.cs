using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed record GtaVMountedSpatialMetrics(
    int ArchetypeCount,
    int RoomCount,
    int InstanceCount,
    int ContainedByCount,
    int ResolvedInstanceOfCount,
    int UnresolvedInstanceOfCount,
    int ConnectsToCount,
    int UnsupportedObjectCount);

/// <summary>
/// Registers only source-native MLO definitions, their source-defined rooms, and placed MLO
/// instances. Rendering/LOD parents, coordinates, bounds, and overlap are deliberately absent
/// from the adapter contract and therefore cannot manufacture Location hierarchy.
/// </summary>
public sealed class GtaVMountedSpatialKnowledgeAdapter : IGameKnowledgeAdapter
{
    public const string AdapterId = "grid.gta-v.enhanced.mounted-mlo-spatial";
    public const string AdapterVersion = "1";
    public const string MappingRulesVersion = "mlo-archetype-room-instance-portal-v1";
    public const string ParserId = "grid.gta-v.mounted-spatial-index";
    public const string ParserVersion = "1";
    public const string CoverageManifestVersion = "grid.gta-v.mounted-spatial-coverage.v1";
    public const string ArchetypeSourceFamily = "grid.gta-v.location-source.mlo-archetypes";
    public const string InstanceSourceFamily = "grid.gta-v.location-source.mlo-instances";
    public const string YmfSourceFamily = "grid.gta-v.location-source.mlo-manifests";
    public const string ClassificationMethod = "grid.gta-v.mlo-native-type.semantic-role";
    public const string ClassificationMethodVersion = "1";

    private const string IdentityNamespace = "rockstar.gta-v.enhanced.mounted-spatial-location";
    private const string IdentityComparison = "grid.gta-v.mounted-spatial-coordinate.exact-utf8";
    private readonly GtaVSpatialCorpusIndex _index;

    public GtaVMountedSpatialKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVSpatialCorpusIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId(AdapterId), AdapterVersion, adapterArtifactDigest, 1, MappingRulesVersion,
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                Supported(GtaVSpatialCorpusIndex.YtypFormatId, "CMloArchetypeDef"),
                Supported(GtaVSpatialCorpusIndex.YmapFormatId, "CMloInstance"),
                Supported(GtaVSpatialCorpusIndex.YmfFormatId, "MountedMloManifest"),
            ],
            new KnowledgeAdapterResourceLimits(512L * 1024 * 1024, 100_000, 1_000_000, 2_000_000),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public GtaVMountedSpatialMetrics? LastMetrics { get; private set; }

    public Task<SourceDiscoveryResult> DiscoverAsync(PreproductionSourceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId)
            return Task.FromResult(new SourceDiscoveryResult([], request.Artifacts.Select(value =>
                new UnsupportedKnowledgeArtifact(value.Id, "unsupported-game-id")).ToImmutableArray(),
                KnowledgeCoverageState.Unsupported, [new KnowledgeBuildIssue("unsupported-game-id", "The adapter is Enhanced-only.")]));
        try
        {
            _index.RequireExactArtifacts(request.Artifacts);
            var source = CreateSource();
            return Task.FromResult(new SourceDiscoveryResult(
                [new DiscoveredKnowledgeSource(source, null, request.Artifacts.Select(value => value.FormatBinding).ToImmutableArray())],
                [], KnowledgeCoverageState.Partial, []));
        }
        catch (InvalidDataException exception)
        {
            return Task.FromResult(new SourceDiscoveryResult([], request.Artifacts.Select(value =>
                new UnsupportedKnowledgeArtifact(value.Id, "spatial-corpus-mismatch")).ToImmutableArray(),
                KnowledgeCoverageState.Unsupported, [new KnowledgeBuildIssue("spatial-corpus-mismatch", exception.Message)]));
        }
    }

    public Task<KnowledgeExtractionResult> ExtractAsync(PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            request.ExactModIdentity is not null || request.ExactModVersion is not null ||
            request.AdapterRevisionId != Descriptor.RevisionId)
            throw new InvalidDataException("Mounted spatial registration requires the exact Enhanced base-game adapter scope.");
        _index.RequireExactArtifacts(request.Artifacts);

        var source = CreateSource();
        var artifactIds = request.Artifacts.Select(value => value.Id).OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, artifactIds, Descriptor.RevisionId);
        var revision = new CatalogSourceRevisionRecord(revisionId, source.Id, null, artifactIds);
        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>();
        var nativeTypes = ImmutableArray.CreateBuilder<SourceNativeLocationTypeAssertion>();
        var classifications = ImmutableArray.CreateBuilder<LocationSemanticClassificationAssertion>();
        var relationships = ImmutableArray.CreateBuilder<RelationshipAssertion>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var receiptIds = new HashSet<EvidenceReceiptId>();
        var bindingIds = new HashSet<EvidenceBindingId>();
        var indexedRecords = new Dictionary<SpatialKey, SpatialRecord>();

        foreach (var sourceFile in _index.Ytyps)
        foreach (var archetype in sourceFile.Archetypes)
        {
            var archetypeKey = new SpatialKey(sourceFile.Artifact.SourceCoordinate.ExactRepresentation,
                "CMloArchetypeDef", archetype.Ordinal);
            var archetypeRecord = AddRecord(archetypeKey,
                "CMloArchetypeDef",
                $"{archetypeKey.Coordinate}#CMloArchetypeDef[{archetype.Ordinal}]:0x{archetype.NameHash:X8}",
                archetype.FieldLocator, sourceFile.Artifact, LocationSemanticRoles.Interior,
                request, revisionId, records, nativeTypes, classifications, receipts, bindings,
                receiptIds, bindingIds);
            indexedRecords.Add(archetypeKey, archetypeRecord);
            foreach (var room in archetype.Rooms)
            {
                var roomKey = new SpatialKey(sourceFile.Artifact.SourceCoordinate.ExactRepresentation,
                    $"CMloArchetypeDef[{archetype.Ordinal}].CMloRoomDef", room.Ordinal);
                var roomRecord = AddRecord(roomKey,
                    "CMloRoomDef",
                    $"{roomKey.Coordinate}#CMloArchetypeDef[{archetype.Ordinal}]/CMloRoomDef[{room.Ordinal}]:{room.NameExact}",
                    room.FieldLocator, sourceFile.Artifact, LocationSemanticRoles.Room,
                    request, revisionId, records, nativeTypes, classifications, receipts, bindings,
                    receiptIds, bindingIds);
                indexedRecords.Add(roomKey, roomRecord);
                AddRelationship(roomRecord, archetypeRecord, LocationRelationshipSemantics.ContainedBy,
                    "CMloRoomDef.ownerArchetype", room.FieldLocator, sourceFile.Artifact,
                    revisionId, relationships, receipts, bindings, receiptIds, bindingIds);
            }
        }

        var archetypesByHash = _index.Ytyps.SelectMany(value => value.Archetypes.Select(fact =>
                (Source: value, Fact: fact, Key: new SpatialKey(value.Artifact.SourceCoordinate.ExactRepresentation,
                    "CMloArchetypeDef", fact.Ordinal))))
            // CEntityDef.archetypeName is a direct reference to CBaseArchetypeDef.name. assetName
            // names the streamed asset and is deliberately not used as a substitute join key.
            .GroupBy(value => value.Fact.NameHash).ToDictionary(value => value.Key, value => value.ToImmutableArray());

        foreach (var sourceFile in _index.Ymaps)
        foreach (var instance in sourceFile.Instances)
        {
            var key = new SpatialKey(sourceFile.Artifact.SourceCoordinate.ExactRepresentation, "CMloInstance", instance.Ordinal);
            var record = AddRecord(key,
                "CMloInstance",
                $"{key.Coordinate}#CMapData[0x{sourceFile.MapNameHash:X8}]/CMloInstance[guid=0x{instance.Guid:X8}]:0x{instance.ArchetypeHash:X8}",
                instance.FieldLocator, sourceFile.Artifact, LocationSemanticRoles.Instance,
                request, revisionId, records, nativeTypes, classifications, receipts, bindings,
                receiptIds, bindingIds, additionalRole: LocationSemanticRoles.Interior);
            indexedRecords.Add(key, record);
            var matches = archetypesByHash.GetValueOrDefault(instance.ArchetypeHash, []);
            var targetIdentity = MloHashIdentity(instance.ArchetypeHash);
            SpatialRecord? target = null;
            if (matches.Length == 1) target = indexedRecords[matches[0].Key];
            AddRelationship(record, target, LocationRelationshipSemantics.InstanceOf,
                "CEntityDef.archetypeName", instance.FieldLocator, sourceFile.Artifact,
                revisionId, relationships, receipts, bindings, receiptIds, bindingIds, targetIdentity);
        }

        foreach (var sourceFile in _index.Ytyps)
        foreach (var archetype in sourceFile.Archetypes)
        foreach (var portal in archetype.Portals)
        {
            var subjectKey = new SpatialKey(sourceFile.Artifact.SourceCoordinate.ExactRepresentation,
                $"CMloArchetypeDef[{archetype.Ordinal}].CMloRoomDef", portal.RoomFromOrdinal);
            var targetKey = subjectKey with { Ordinal = portal.RoomToOrdinal };
            AddRelationship(indexedRecords[subjectKey], indexedRecords[targetKey], LocationRelationshipSemantics.ConnectsTo,
                "CMloPortalDef.roomFrom-roomTo", portal.FieldLocator, sourceFile.Artifact,
                revisionId, relationships, receipts, bindings, receiptIds, bindingIds);
        }

        var registration = new CanonicalCatalogRegistration(
            source,
            request.Artifacts.Select(value => new SourceArtifactRecord(value.Id, value.Digest))
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            revision,
            records.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            [], relationships.OrderBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal).ToImmutableArray(),
            receipts.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            [], bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray())
        {
            SourceNativeLocationTypeAssertions = nativeTypes.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            LocationSemanticClassificationAssertions = classifications.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        };
        var bound = new AdapterBoundCanonicalCatalogRegistration(registration, Descriptor, request.SourceScope,
            request.Artifacts.Select(value => value.FormatBinding).ToImmutableArray());
        LastMetrics = new GtaVMountedSpatialMetrics(
            _index.Ytyps.Sum(value => value.Archetypes.Length),
            _index.Ytyps.Sum(value => value.Archetypes.Sum(item => item.Rooms.Length)),
            _index.Ymaps.Sum(value => value.Instances.Length),
            relationships.Count(value => value.SemanticId == LocationRelationshipSemantics.ContainedBy),
            relationships.Count(value => value.SemanticId == LocationRelationshipSemantics.InstanceOf && value.Resolution == CanonicalResolutionState.Resolved),
            relationships.Count(value => value.SemanticId == LocationRelationshipSemantics.InstanceOf && value.Resolution == CanonicalResolutionState.Unresolved),
            relationships.Count(value => value.SemanticId == LocationRelationshipSemantics.ConnectsTo),
            _index.Unsupported.Length);
        var report = CreateCoverageReport(bound, request.SourceScope, LastMetrics);
        return Task.FromResult(new KnowledgeExtractionResult([bound], [], [], KnowledgeCoverageState.Partial, [])
        {
            LocationCoverageReports = [report],
        });
    }

    private SpatialRecord AddRecord(
        SpatialKey key, string nativeObjectType, string exactIdentity, string locator, FrozenSourceArtifact artifact,
        LocationSemanticRoleId role, PreproductionKnowledgeExtractionRequest request,
        CatalogSourceRevisionId revisionId,
        ImmutableArray<CanonicalKnowledgeRecord>.Builder records,
        ImmutableArray<SourceNativeLocationTypeAssertion>.Builder nativeTypes,
        ImmutableArray<LocationSemanticClassificationAssertion>.Builder classifications,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        HashSet<EvidenceReceiptId> receiptIds,
        HashSet<EvidenceBindingId> bindingIds,
        LocationSemanticRoleId? additionalRole = null)
    {
        var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
            IdentityNamespace, nativeObjectType, exactIdentity, IdentityComparison, 1);
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(request.GameId, nativeIdentity);
        var recordId = KnowledgeRecordId.DeriveV1(request.GameId, request.ExactGameVersion,
            null, revisionId, KnowledgeKind.Location, nativeRecordId);
        var record = new CanonicalKnowledgeRecord(recordId, request.GameId, request.ExactGameVersion,
            null, revisionId, KnowledgeKind.Location, nativeRecordId, nativeIdentity);
        records.Add(record);
        AddEvidence(record, artifact, revisionId, EvidenceClaimKind.KnowledgeIdentity, locator, locator,
            null, receipts, bindings, receiptIds, bindingIds);

        var nativeTypeIdentity = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.mlo.location-type", "location-record-type", nativeObjectType,
            "grid.gta-v.location-native-type.exact-utf8", 1);
        var typeId = SourceNativeLocationTypeAssertionId.DeriveV1(record.Id, revisionId, nativeTypeIdentity, locator);
        var type = new SourceNativeLocationTypeAssertion(typeId, record.Id, revisionId, nativeTypeIdentity, locator);
        nativeTypes.Add(type);
        AddEvidence(record, artifact, revisionId, EvidenceClaimKind.LocationNativeType, locator, locator,
            EvidenceClaimContentId.DeriveV1(type), receipts, bindings, receiptIds, bindingIds);
        AddClassification(record, artifact, revisionId, type, role, locator, classifications, receipts, bindings,
            receiptIds, bindingIds);
        if (additionalRole is { } second)
            AddClassification(record, artifact, revisionId, type, second, locator, classifications, receipts, bindings,
                receiptIds, bindingIds);
        return new SpatialRecord(record, artifact, locator);
    }

    private void AddClassification(CanonicalKnowledgeRecord record, FrozenSourceArtifact artifact,
        CatalogSourceRevisionId revisionId, SourceNativeLocationTypeAssertion type,
        LocationSemanticRoleId role, string locator,
        ImmutableArray<LocationSemanticClassificationAssertion>.Builder classifications,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        HashSet<EvidenceReceiptId> receiptIds,
        HashSet<EvidenceBindingId> bindingIds)
    {
        var id = LocationSemanticClassificationAssertionId.DeriveV1(record.Id, revisionId, type.Id, role,
            LocationSemanticRoles.CurrentVocabularyVersion, ClassificationMethod, ClassificationMethodVersion, locator);
        var assertion = new LocationSemanticClassificationAssertion(id, record.Id, revisionId, type.Id, role,
            LocationSemanticRoles.CurrentVocabularyVersion, ClassificationMethod, ClassificationMethodVersion, locator);
        classifications.Add(assertion);
        AddEvidence(record, artifact, revisionId, EvidenceClaimKind.LocationSemanticClassification, locator, locator,
            EvidenceClaimContentId.DeriveV1(assertion), receipts, bindings, receiptIds, bindingIds);
    }

    private void AddRelationship(SpatialRecord subject, SpatialRecord? target, RelationshipSemanticId semantic,
        string nativeType, string locator, FrozenSourceArtifact artifact, CatalogSourceRevisionId revisionId,
        ImmutableArray<RelationshipAssertion>.Builder relationships,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        HashSet<EvidenceReceiptId> receiptIds,
        HashSet<EvidenceBindingId> bindingIds,
        SourceNativeIdentifier? unresolvedTarget = null)
    {
        var targetIdentity = target?.Record.NativeIdentity ?? unresolvedTarget ??
            throw new InvalidDataException("A spatial relationship requires an exact native target identity.");
        var assertion = new RelationshipAssertion(subject.Record.Id, revisionId, semantic, nativeType, locator,
            targetIdentity, target?.Record.Id);
        relationships.Add(assertion);
        AddEvidence(subject.Record, artifact, revisionId, EvidenceClaimKind.Relationship, locator, locator,
            EvidenceClaimContentId.DeriveV1(assertion), receipts, bindings, receiptIds, bindingIds);
    }

    private static void AddEvidence(CanonicalKnowledgeRecord record, FrozenSourceArtifact artifact,
        CatalogSourceRevisionId revisionId, EvidenceClaimKind kind, string recordLocator, string fieldLocator,
        EvidenceClaimContentId? contentId,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        HashSet<EvidenceReceiptId> receiptIds,
        HashSet<EvidenceBindingId> bindingIds)
    {
        var receipt = new FileEvidenceReceipt(revisionId, artifact.Id, artifact.Digest, ParserId, ParserVersion,
            recordLocator, fieldLocator, null, null, null, artifact.ObservedAtUtc, record.NativeIdentity);
        var receiptId = EvidenceReceiptId.DeriveV2(receipt);
        if (receiptIds.Add(receiptId)) receipts.Add(new CatalogFileEvidenceReceipt(receiptId, receipt));
        var bindingId = EvidenceBindingId.DeriveV2(receiptId, kind, record.Id, revisionId, fieldLocator, contentId);
        if (bindingIds.Add(bindingId))
            bindings.Add(new EvidenceBinding(bindingId, receiptId, kind, record.Id, revisionId, fieldLocator, contentId));
    }

    private LocationCoverageReport CreateCoverageReport(AdapterBoundCanonicalCatalogRegistration bound,
        KnowledgeSourceScope scope, GtaVMountedSpatialMetrics metrics)
    {
        var registration = bound.Registration;
        var archIds = registration.KnowledgeRecords.Where(value => value.NativeIdentity.ObjectType == "CMloArchetypeDef").Select(value => value.Id).ToImmutableArray();
        var roomIds = registration.KnowledgeRecords.Where(value => value.NativeIdentity.ObjectType.Contains("CMloRoomDef", StringComparison.Ordinal)).Select(value => value.Id).ToImmutableArray();
        var instanceIds = registration.KnowledgeRecords.Where(value => value.NativeIdentity.ObjectType == "CMloInstance").Select(value => value.Id).ToImmutableArray();
        var ytypArtifacts = _index.Ytyps.Select(value => value.Artifact.Id).ToImmutableArray();
        var ymapArtifacts = _index.Ymaps.Select(value => value.Artifact.Id).ToImmutableArray();
        var ymfArtifacts = _index.Ymfs.Select(value => value.Artifact.Id).ToImmutableArray();
        var declarations = ImmutableArray.Create(
            Declaration(ArchetypeSourceFamily, GtaVSpatialCorpusIndex.YtypFormatId, ytypArtifacts,
                [LocationSemanticRoles.Interior, LocationSemanticRoles.Room], registration.SourceRevision.Id),
            Declaration(InstanceSourceFamily, GtaVSpatialCorpusIndex.YmapFormatId, ymapArtifacts,
                [LocationSemanticRoles.Instance, LocationSemanticRoles.Interior], registration.SourceRevision.Id),
            Declaration(YmfSourceFamily, GtaVSpatialCorpusIndex.YmfFormatId, ymfArtifacts, [], registration.SourceRevision.Id));
        var validation = new CatalogValidationSummary(CatalogValidationStatus.Candidate,
            "grid.location.coverage.qcs", "1", ContentDigest.ComputeSha256("grid.location.coverage.qcs.pending"u8));
        var manifestId = LocationCoverageManifestId.DeriveV1(scope, CoverageManifestVersion, false, validation, declarations);
        var manifest = new LocationCoverageManifest(manifestId, scope, CoverageManifestVersion, false, validation, declarations);
        var families = ImmutableArray.Create(
            Family(ArchetypeSourceFamily, ytypArtifacts, registration.SourceRevision.Id, archIds.AddRange(roomIds), 0),
            Family(InstanceSourceFamily, ymapArtifacts, registration.SourceRevision.Id, instanceIds, 0),
            // Missing declared mount members have no frozen semantic artifact and therefore cannot be
            // attributed to one acquired YTYP/YMAP/YMF family inside this canonical report. They remain
            // explicit in LastMetrics and the receipt-bound registration coverage ledger instead of being
            // mislabeled as YMF parser failures.
            Family(YmfSourceFamily, ymfArtifacts, registration.SourceRevision.Id, [], 0));
        var categories = ImmutableArray.Create(
            Category("CMloArchetypeDef", LocationSemanticRoles.Interior, archIds),
            Category("CMloArchetypeDef[0].CMloRoomDef", LocationSemanticRoles.Room, roomIds, normalizeRoomType: true),
            Category("CMloInstance", LocationSemanticRoles.Instance, instanceIds),
            Category("CMloInstance", LocationSemanticRoles.Interior, instanceIds));
        var relationships = registration.RelationshipAssertions.GroupBy(value => value.SemanticId)
            .Select(group => new LocationRelationshipCoverage(group.Key, group.Count(),
                group.Count(value => value.Resolution == CanonicalResolutionState.Resolved),
                group.Count(value => value.Resolution == CanonicalResolutionState.Unresolved),
                group.Count(), 0, 0)).ToImmutableArray();
        var strict = registration.RelationshipAssertions.Where(value => LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)).ToImmutableArray();
        return LocationCoverageReport.Create(manifest, families, categories,
            new LocationTerminologyCoverage(registration.KnowledgeRecords.Length, 0, 0, registration.KnowledgeRecords.Length, 0),
            new LocationHierarchyCoverage(strict.Length,
                strict.Count(value => value.Resolution == CanonicalResolutionState.Resolved),
                strict.Count(value => value.Resolution == CanonicalResolutionState.Unresolved), 0, false),
            relationships, []);
    }

    private LocationSourceFamilyDeclaration Declaration(string id, string format,
        ImmutableArray<SourceArtifactId> artifacts, ImmutableArray<LocationSemanticRoleId> roles,
        CatalogSourceRevisionId revision) => new(new LocationSourceFamilyId(id),
        new KnowledgeFormatCoordinate(format, GtaVSpatialCorpusIndex.ExactFormatVersion), Descriptor.RevisionId,
        true, artifacts, [revision], roles);

    private static LocationSourceFamilyCoverage Family(string id, ImmutableArray<SourceArtifactId> artifacts,
        CatalogSourceRevisionId revision, ImmutableArray<KnowledgeRecordId> records, int unsupported) =>
        new(new LocationSourceFamilyId(id), artifacts, [revision], records.Length, records.Length, records.Length,
            records, [], [], unsupported, 0, 0, 0);

    private static LocationSemanticCategoryCoverage Category(string type, LocationSemanticRoleId role,
        ImmutableArray<KnowledgeRecordId> records, bool normalizeRoomType = false)
    {
        var exactType = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.mlo.location-type",
            "location-record-type", normalizeRoomType ? "CMloRoomDef" : type,
            "grid.gta-v.location-native-type.exact-utf8", 1);
        return new LocationSemanticCategoryCoverage(exactType, role, records, 0);
    }

    private static SupportedKnowledgeFormat Supported(string format, string objectType) => new(
        format, GtaVSpatialCorpusIndex.ExactFormatVersion, ["rpf7-member"], [objectType], [KnowledgeKind.Location],
        false, true, true);

    private static CatalogSourceRecord CreateSource()
    {
        var identity = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.source-family",
            "MountedMloSpatialCorpus", "mounted-ytyp-ymap-ymf-mlo-interior",
            "grid.gta-v.source-family.exact-utf8", 1);
        return new CatalogSourceRecord(CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, identity),
            KnowledgeSourceKind.LocalGameDistribution, identity);
    }

    private static SourceNativeIdentifier MloHashIdentity(uint hash)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier("rockstar.gta-v.ytyp", "CMloArchetypeDef.NameHash", $"0x{hash:X8}",
            bytes.ToImmutableArray(), "rockstar.gta-v.joaat32-little-endian", 1);
    }

    private readonly record struct SpatialKey(string Coordinate, string ObjectType, int Ordinal);
    private sealed record SpatialRecord(CanonicalKnowledgeRecord Record, FrozenSourceArtifact Artifact, string Locator);
}
