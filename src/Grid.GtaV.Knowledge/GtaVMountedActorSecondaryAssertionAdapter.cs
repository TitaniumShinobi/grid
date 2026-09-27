using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed record GtaVMountedActorSecondaryAssertionMetrics(
    int ResidentRecordCount,
    int DlcRecordCount,
    int PlayerCharacterClassificationCount,
    int NpcClassificationCount,
    int UnclassifiedRecordCount,
    int DlcOrganizationalValueCount,
    int Gen9ResidentCorrelationCount);

public sealed record GtaVMountedActorSecondaryAssertionResult(
    ImmutableArray<GtaVSecondaryAssertionBatch> Batches,
    GtaVMountedActorSecondaryAssertionMetrics Metrics);

/// <summary>
/// Adds only exact Pedtype roles, mounted-DLC provenance, and exact Gen9-to-resident hash
/// correlations to already registered Actor identities. It never emits Actors or terminology.
/// </summary>
public sealed class GtaVMountedActorSecondaryAssertionAdapter
{
    public const string RoleMethodId = "grid.gta-v.pedtype.actor-role.exact";
    public const string RoleMethodVersion = "1";
    public const string DlcMethodId = "grid.gta-v.mounted-ped-member.dlc-pack.exact";
    public const string DlcMethodVersion = "1";
    public const string LinkMethodId = "grid.gta-v.ped-init-name.same-object";
    public const string LinkMethodVersion = "1";
    public const string DlcLinkMethodId = "grid.gta-v.mounted-ped-source.dlc-pack.exact";
    public const string DlcLinkMethodVersion = "1";
    public const string CorrelationMethodId = "grid.gta-v.gen9-ped-name.resident-joaat32";
    public const string CorrelationMethodVersion = "1";
    private const string VocabularyId = "grid.actor-role";
    private const string VocabularyVersion = "1";
    private readonly GtaVActorCorpusIndex _index;

    private static readonly ImmutableDictionary<uint, CanonicalSemanticRoleId> ResidentRoles =
        new Dictionary<uint, CanonicalSemanticRoleId>
        {
            [0x0CDC5452] = CanonicalProjectionSemantics.ActorPlayerCharacter,
            [0xBEA537E5] = CanonicalProjectionSemantics.ActorPlayerCharacter,
            [0x7AF2B04D] = CanonicalProjectionSemantics.ActorPlayerCharacter,
            [0x02B8FA80] = CanonicalProjectionSemantics.ActorNpc,
            [0x47033600] = CanonicalProjectionSemantics.ActorNpc,
            [0x3A0EB4FD] = CanonicalProjectionSemantics.ActorNpc,
            [0xA49E591C] = CanonicalProjectionSemantics.ActorNpc,
            [0xE3D976F3] = CanonicalProjectionSemantics.ActorNpc,
            [0xB0423AA0] = CanonicalProjectionSemantics.ActorNpc,
            [0xFC2CA767] = CanonicalProjectionSemantics.ActorNpc,
            [0x98787966] = CanonicalProjectionSemantics.ActorNpc,
        }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, CanonicalSemanticRoleId> DlcRoles =
        ResidentRoles.ToImmutableDictionary(
            value => value.Key switch
            {
                0x0CDC5452 => "PLAYER_0", 0xBEA537E5 => "PLAYER_1", 0x7AF2B04D => "PLAYER_2",
                0x02B8FA80 => "CIVMALE", 0x47033600 => "CIVFEMALE", 0x3A0EB4FD => "ANIMAL",
                0xA49E591C => "COP", 0xE3D976F3 => "ARMY", 0xB0423AA0 => "MEDIC",
                0xFC2CA767 => "FIREMAN", 0x98787966 => "SWAT",
                _ => throw new InvalidOperationException("The exact Pedtype map is incomplete."),
            },
            value => value.Value,
            StringComparer.Ordinal);

    public GtaVMountedActorSecondaryAssertionAdapter(
        ContentDigest adapterArtifactDigest,
        GtaVActorCorpusIndex actorIndex)
    {
        _index = actorIndex ?? throw new ArgumentNullException(nameof(actorIndex));
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.mounted-actor-secondary"),
            "1", adapterArtifactDigest, 1, "mounted-pedtype-dlc-correlation-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                new SupportedKnowledgeFormat(
                    GtaVActorCorpusIndex.ResidentFormatId, GtaVActorCorpusIndex.ExactFormatVersion,
                    ["rpf7-member"], ["CPedModelInfo__InitDataListPso"], [KnowledgeKind.Actor],
                    false, false, false),
                new SupportedKnowledgeFormat(
                    GtaVActorCorpusIndex.DlcXmlFormatId, GtaVActorCorpusIndex.ExactFormatVersion,
                    ["rpf7-member"], ["CPedModelInfo__InitDataListXml"], [KnowledgeKind.Actor],
                    false, false, false),
                new SupportedKnowledgeFormat(
                    GtaVActorCorpusIndex.DlcListFormatId, GtaVActorCorpusIndex.ExactFormatVersion,
                    ["rpf7-member"], ["MountedActorSource"], [KnowledgeKind.Actor],
                    false, false, false),
                new SupportedKnowledgeFormat(
                    GtaVActorCorpusIndex.DlcSetupFormatId, GtaVActorCorpusIndex.ExactFormatVersion,
                    ["rpf7-member"], ["MountedActorSource"], [KnowledgeKind.Actor],
                    false, false, false),
                new SupportedKnowledgeFormat(
                    GtaVActorCorpusIndex.DlcContentFormatId, GtaVActorCorpusIndex.ExactFormatVersion,
                    ["rpf7-member"], ["MountedActorSource"], [KnowledgeKind.Actor],
                    false, false, false),
                new SupportedKnowledgeFormat(
                    GtaVGen9PedsKnowledgeAdapter.FormatId, GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion,
                    ["rpf7-member"], ["Gen9ExclusiveAssetsDataPeds"], [KnowledgeKind.Actor],
                    false, false, false),
            ],
            new KnowledgeAdapterResourceLimits(64L * 1024 * 1024, 128, 10_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVMountedActorSecondaryAssertionResult Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact residentArtifact,
        ImmutableArray<FrozenSourceArtifact> dlcArtifacts,
        FrozenSourceArtifact? gen9Artifact = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(residentArtifact);
        if (dlcArtifacts.IsDefault || dlcArtifacts.Any(value => value is null))
            throw new ArgumentException("Mounted DLC Actor artifacts must be initialized and non-null.", nameof(dlcArtifacts));
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Mounted Actor assertions require the exact Enhanced base-game scope.");

        var resident = _index.GetResident(residentArtifact);
        var dlcArtifactsById = dlcArtifacts.GroupBy(value => value.Id).ToDictionary(
            value => value.Key,
            value => value.Count() == 1
                ? value.Single()
                : throw new InvalidDataException("The mounted Actor input contains a duplicate artifact identity."));
        var requiredDlcArtifactIds = _index.DlcPacks.SelectMany(value => new[]
            { value.Artifact.ArtifactId, value.SetupArtifact.ArtifactId, value.ContentArtifact.ArtifactId })
            .Append(_index.DlcListArtifact.ArtifactId).ToHashSet();
        if (dlcArtifactsById.Count != requiredDlcArtifactIds.Count ||
            !dlcArtifactsById.Keys.ToHashSet().SetEquals(requiredDlcArtifactIds))
            throw new InvalidDataException("The secondary pass requires the complete exact mounted DLC ped/setup/content corpus.");
        var dlcSources = _index.DlcPacks;

        var batches = ImmutableArray.CreateBuilder<GtaVSecondaryAssertionBatch>();
        batches.Add(BuildResidentBatch(origin, sourceScope, residentArtifact, resident));
        foreach (var source in dlcSources.OrderBy(value => value.Artifact.ArtifactId.Value, StringComparer.Ordinal))
            batches.Add(BuildDlcBatch(
                origin, sourceScope,
                dlcArtifactsById[source.Artifact.ArtifactId],
                dlcArtifactsById[source.DlcListArtifact.ArtifactId],
                dlcArtifactsById[source.SetupArtifact.ArtifactId],
                dlcArtifactsById[source.ContentArtifact.ArtifactId],
                source));

        var correlations = gen9Artifact is null
            ? ImmutableArray<CanonicalCorrelationEnvelope>.Empty
            : CreateGen9Correlations(origin, residentArtifact, gen9Artifact);
        if (!correlations.IsEmpty)
        {
            var first = batches[0];
            batches[0] = first with { CorrelationEnvelopes = correlations };
        }

        var values = batches.ToImmutable();
        var classifications = values.SelectMany(value => value.SemanticClassifications).ToImmutableArray();
        return new GtaVMountedActorSecondaryAssertionResult(
            values,
            new GtaVMountedActorSecondaryAssertionMetrics(
                resident.Records.Length,
                dlcSources.Sum(value => value.Records.Length),
                classifications.Count(value => value.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter),
                classifications.Count(value => value.RoleId == CanonicalProjectionSemantics.ActorNpc),
                resident.Records.Length + dlcSources.Sum(value => value.Records.Length) - classifications.Length,
                values.Sum(value => value.OrganizationalValues.Length),
                correlations.Length));
    }

    private GtaVSecondaryAssertionBatch BuildResidentBatch(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope scope,
        FrozenSourceArtifact artifact,
        GtaVResidentPedSource source)
    {
        var facts = source.Records.Select(value => new ActorFact(
            $"0x{value.NameHash:X8}", value.NameFieldLocator, value.RecordLocator,
            ResidentRoles.TryGetValue(value.PedtypeHash, out var role) ? role : null,
            PedtypeHashIdentity(value.PedtypeHash),
            value.PedtypeFieldLocator, null)).ToImmutableArray();
        return BuildBatch(origin, scope, artifact,
            "rockstar.gta-v.enhanced.resident-ped-model", facts, []);
    }

    private GtaVSecondaryAssertionBatch BuildDlcBatch(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope scope,
        FrozenSourceArtifact artifact,
        FrozenSourceArtifact dlcListArtifact,
        FrozenSourceArtifact setupArtifact,
        FrozenSourceArtifact contentArtifact,
        GtaVDlcPedSource source)
    {
        var packIdentity = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.dlc-pack", "MountedDlcPack", source.PackNameExact,
            "grid.gta-v.dlc-pack-name.exact-utf8", 1);
        var occurrence = source.DlclistOccurrences[0];
        var facts = source.Records.Select(value => new ActorFact(
            value.NameExact, value.NameFieldLocator, value.RecordLocator,
            DlcRoles.TryGetValue(value.PedtypeExact, out var role) ? role : null,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.pedtype", "Pedtype", value.PedtypeExact,
                "grid.gta-v.pedtype.exact-utf8", 1),
            value.PedtypeFieldLocator,
            new DlcFact(
                packIdentity, source.PackNameExact, dlcListArtifact,
                occurrence.FieldLocator, occurrence.FieldLocator)))
            .ToImmutableArray();
        return BuildBatch(origin, scope, artifact,
            "rockstar.gta-v.enhanced.mounted-dlc-ped-model", facts,
            [dlcListArtifact, setupArtifact, contentArtifact]);
    }

    private GtaVSecondaryAssertionBatch BuildBatch(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope scope,
        FrozenSourceArtifact artifact,
        string recordNamespace,
        ImmutableArray<ActorFact> facts,
        ImmutableArray<FrozenSourceArtifact> supportingArtifacts)
    {
        var sourceRevisionIds = origin.KnowledgeRecords
            .Where(value => value.Kind == KnowledgeKind.Actor &&
                string.Equals(value.NativeIdentity.Namespace, recordNamespace, StringComparison.Ordinal))
            .Select(value => value.SourceRevisionId).Distinct().Where(id => origin.SourceRevisions.Any(revision =>
                revision.Revision.Id == id && revision.Revision.ArtifactIds.SequenceEqual([artifact.Id])))
            .ToImmutableArray();
        if (sourceRevisionIds.Length != 1)
            throw new InvalidDataException("An Actor source artifact must resolve to exactly one primary source revision.");
        var originRevision = origin.SourceRevisions.Single(value => value.Revision.Id == sourceRevisionIds[0]);
        var source = origin.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var records = origin.KnowledgeRecords.Where(value => value.SourceRevisionId == sourceRevisionIds[0])
            .ToDictionary(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal);
        if (records.Count != facts.Length || facts.Any(value => !records.ContainsKey(value.NativeIdentity)))
            throw new InvalidDataException("Actor sidecar facts do not close over the exact primary Actor set.");
        var revisionArtifacts = supportingArtifacts.Prepend(artifact).Select(value => value.Id)
            .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var artifactFormats = supportingArtifacts.Prepend(artifact).Select(value => value.FormatBinding)
            .DistinctBy(value => value.ArtifactId).OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, revisionArtifacts, Descriptor.RevisionId);
        var revision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, revisionArtifacts),
            Descriptor.RevisionId, scope, artifactFormats);

        var classifications = ImmutableArray.CreateBuilder<CanonicalSemanticClassificationAssertion>();
        var organizations = ImmutableArray.CreateBuilder<CanonicalOrganizationalValueAssertion>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();
        foreach (var fact in facts.OrderBy(value => value.NativeIdentity, StringComparer.Ordinal))
        {
            var target = records[fact.NativeIdentity];
            if (fact.Role is CanonicalSemanticRoleId role)
            {
                var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
                    target.Id, revisionId, role, VocabularyId, VocabularyVersion,
                    RoleMethodId, RoleMethodVersion, fact.PedtypeFieldLocator);
                var assertion = new CanonicalSemanticClassificationAssertion(
                    id, target.Id, revisionId, role, VocabularyId, VocabularyVersion,
                    RoleMethodId, RoleMethodVersion, fact.PedtypeFieldLocator);
                classifications.Add(assertion);
                AddEnvelope(origin, target, revisionId, artifact, fact,
                    artifact, fact.RecordLocator, fact.PedtypeIdentity, fact.PedtypeFieldLocator,
                    EvidenceClaimKind.SemanticClassification,
                    CrossSourceCanonicalAssertionKind.SemanticClassification,
                    EvidenceClaimContentId.DeriveV1(assertion),
                    LinkMethodId, LinkMethodVersion,
                    receipts, bindings, links, envelopes);
            }
            if (fact.Dlc is DlcFact dlc)
            {
                var id = CanonicalOrganizationalValueAssertionId.DeriveV1(
                    target.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
                    dlc.PackIdentity, dlc.DisplayValue, DlcMethodId, DlcMethodVersion, dlc.SourceFieldPath);
                var assertion = new CanonicalOrganizationalValueAssertion(
                    id, target.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
                    dlc.PackIdentity, dlc.DisplayValue, DlcMethodId, DlcMethodVersion, dlc.SourceFieldPath);
                organizations.Add(assertion);
                AddEnvelope(origin, target, revisionId, artifact, fact,
                    dlc.Artifact, dlc.RecordLocator, dlc.PackIdentity, dlc.SourceFieldPath,
                    EvidenceClaimKind.OrganizationalValue,
                    CrossSourceCanonicalAssertionKind.OrganizationalValue,
                    EvidenceClaimContentId.DeriveV1(assertion),
                    DlcLinkMethodId, DlcLinkMethodVersion,
                    receipts, bindings, links, envelopes);
            }
        }
        return new GtaVSecondaryAssertionBatch(
            Descriptor, revision,
            supportingArtifacts.Select(value => new SourceArtifactRecord(value.Id, value.Digest)).ToImmutableArray(),
            [], classifications.ToImmutable(), organizations.ToImmutable(),
            receipts.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            envelopes.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static void AddEnvelope(
        CanonicalCatalogPayload origin,
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact targetArtifact,
        ActorFact fact,
        FrozenSourceArtifact claimArtifact,
        string claimRecordLocator,
        SourceNativeIdentifier assertingIdentity,
        string claimFieldPath,
        EvidenceClaimKind claimKind,
        CrossSourceCanonicalAssertionKind assertionKind,
        EvidenceClaimContentId claimContent,
        string linkMethodId,
        string linkMethodVersion,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceTargetLinkClaim>.Builder links,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        var originBinding = origin.EvidenceBindings.Where(value =>
                value.KnowledgeRecordId == target.Id && value.SourceRevisionId == target.SourceRevisionId &&
                value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && value.ClaimContentId is null)
            .OrderBy(value => value.ClaimLocator, StringComparer.Ordinal).First();
        var targetReceipt = AddReceipt(revisionId, targetArtifact, target.NativeIdentity,
            fact.RecordLocator, fact.NameFieldLocator, receipts);
        var claimReceipt = AddReceipt(revisionId, claimArtifact, assertingIdentity,
            claimRecordLocator, claimFieldPath, receipts);
        var linkId = CrossSourceTargetLinkClaimId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            target.NativeIdentity, targetArtifact.Id, fact.NameFieldLocator,
            revisionId, assertingIdentity, claimArtifact.Id, claimFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping, linkMethodId, linkMethodVersion);
        var link = new CrossSourceTargetLinkClaim(
            linkId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            target.NativeIdentity, targetArtifact.Id, fact.NameFieldLocator,
            revisionId, assertingIdentity, claimArtifact.Id, claimFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping, linkMethodId, linkMethodVersion);
        links.Add(link);
        var linkContent = EvidenceClaimContentId.DeriveV1(link);
        var targetLinkBinding = AddBinding(targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, fact.NameFieldLocator, linkContent, bindings);
        var assertingLinkBinding = AddBinding(claimReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
            target.Id, revisionId, claimFieldPath, linkContent, bindings);
        var claimBinding = AddBinding(claimReceipt.Id, claimKind,
            target.Id, revisionId, claimFieldPath, claimContent, bindings);
        var receiptIds = ImmutableArray.Create(originBinding.EvidenceReceiptId, targetReceipt.Id, claimReceipt.Id)
            .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var bindingIds = ImmutableArray.Create(originBinding.Id, targetLinkBinding.Id, assertingLinkBinding.Id, claimBinding.Id)
            .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var envelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, assertionKind, claimContent,
            link.Id, link.LinkKind, assertingIdentity,
            linkMethodId, linkMethodVersion, receiptIds, bindingIds, [], []);
        envelopes.Add(new CrossSourceCanonicalAssertion(
            envelopeId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            revisionId, assertionKind, claimContent,
            link.Id, link.LinkKind, assertingIdentity,
            linkMethodId, linkMethodVersion, receiptIds, bindingIds, [], []));
    }

    private static ImmutableArray<CanonicalCorrelationEnvelope> CreateGen9Correlations(
        CanonicalCatalogPayload origin,
        FrozenSourceArtifact residentArtifact,
        FrozenSourceArtifact gen9Artifact)
    {
        RequirePayloadArtifact(origin, residentArtifact);
        RequirePayloadArtifact(origin, gen9Artifact);
        var resident = origin.KnowledgeRecords.Where(value => value.Kind == KnowledgeKind.Actor &&
                string.Equals(value.NativeIdentity.Namespace, "rockstar.gta-v.enhanced.resident-ped-model", StringComparison.Ordinal))
            .ToDictionary(value => ParseHash(value.NativeIdentity.ExactRepresentation));
        var gen9 = origin.KnowledgeRecords.Where(value => value.Kind == KnowledgeKind.Actor &&
                string.Equals(value.NativeIdentity.Namespace, "rockstar.gta-v.enhanced.gen9-exclusive-peds", StringComparison.Ordinal))
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var residentRevisionIds = resident.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (residentRevisionIds.Length != 1 ||
            !origin.SourceRevisions.Single(value => value.Revision.Id == residentRevisionIds[0])
                .Revision.ArtifactIds.Contains(residentArtifact.Id))
            throw new InvalidDataException("The resident correlation artifact does not belong to the exact resident Actor revision.");
        if (gen9.Length != 2) throw new InvalidDataException("The exact Gen9 Actor history must contain two records before correlation.");
        var gen9RevisionIds = gen9.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (gen9RevisionIds.Length != 1 ||
            !origin.SourceRevisions.Single(value => value.Revision.Id == gen9RevisionIds[0])
                .Revision.ArtifactIds.Contains(gen9Artifact.Id))
            throw new InvalidDataException("The Gen9 correlation artifact does not belong to the exact Gen9 Actor revision.");
        var values = ImmutableArray.CreateBuilder<CanonicalCorrelationEnvelope>();
        foreach (var record in gen9)
        {
            var hash = ComputeJoaat32(record.NativeIdentity.ExactRepresentation);
            if (!resident.TryGetValue(hash, out var residentRecord))
                throw new InvalidDataException("A Gen9 Actor has no exact resident Name-hash counterpart.");
            var evidenceIds = origin.EvidenceBindings.Where(value =>
                    (value.KnowledgeRecordId == record.Id || value.KnowledgeRecordId == residentRecord.Id) &&
                    value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && value.ClaimContentId is null)
                .Select(value => value.EvidenceReceiptId).Distinct()
                .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
            var correlation = new CorrelationRecord(
                [record.Id, residentRecord.Id], CorrelationMethodId, CorrelationMethodVersion,
                CorrelationOutcome.Correlated);
            values.Add(new CanonicalCorrelationEnvelope(
                CorrelationRecordId.DeriveV1(correlation, evidenceIds), correlation, evidenceIds));
        }
        return values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static SourceNativeIdentifier PedtypeHashIdentity(uint hash)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier(
            "rockstar.gta-v.pedtype", "PedtypeHash", $"0x{hash:X8}", bytes.ToImmutableArray(),
            "grid.gta-v.joaat32-unsigned", 1);
    }

    private static void RequirePayloadArtifact(CanonicalCatalogPayload origin, FrozenSourceArtifact artifact)
    {
        if (ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest ||
            !origin.Artifacts.Any(value => value.Id == artifact.Id && value.Digest == artifact.Digest))
            throw new InvalidDataException("A correlation artifact is not the exact artifact registered in the origin payload.");
    }

    private static CatalogFileEvidenceReceipt AddReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string recordLocator,
        string fieldPath,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            "grid.gta-v-enhanced.mounted-actor-secondary", "1",
            recordLocator, fieldPath, null, null, null, artifact.ObservedAtUtc, nativeObject);
        var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
        receipts.Add(receipt);
        return receipt;
    }

    private static EvidenceBinding AddBinding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string locator,
        EvidenceClaimContentId content,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
        var value = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, locator, content),
            receiptId, kind, recordId, revisionId, locator, content);
        bindings.Add(value);
        return value;
    }

    private static uint ParseHash(string value)
    {
        if (value.Length != 10 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var result) || result == 0)
            throw new InvalidDataException("A resident Actor identity is not exact 0xXXXXXXXX.");
        return result;
    }

    private static uint ComputeJoaat32(string value)
    {
        if (value.Length == 0 || value.Any(character => character > 0x7f || char.IsControl(character)))
            throw new InvalidDataException("Gen9 resident correlation accepts exact printable ASCII model names only.");
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

    private sealed record ActorFact(
        string NativeIdentity,
        string NameFieldLocator,
        string RecordLocator,
        CanonicalSemanticRoleId? Role,
        SourceNativeIdentifier PedtypeIdentity,
        string PedtypeFieldLocator,
        DlcFact? Dlc);

    private sealed record DlcFact(
        SourceNativeIdentifier PackIdentity,
        string DisplayValue,
        FrozenSourceArtifact Artifact,
        string RecordLocator,
        string SourceFieldPath);
}
