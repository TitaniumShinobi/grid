using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed record GtaVCloudJobHeaderSecondaryAssertionMetrics(
    int ProviderObjectCount,
    int ExactMatchedProviderObjectCount,
    int ExactMatchedTargetRecordCount,
    int TitledTargetRecordCount,
    int FamilyOrganizedTargetRecordCount,
    int OnlineClassifiedTargetRecordCount,
    int UnmatchedProviderObjectCount,
    int UnmatchedTargetRecordCount,
    int AmbiguousProviderObjectCount,
    int EquivalentDuplicateCount,
    int TitleConflictTargetRecordCount,
    int FamilyConflictTargetRecordCount);

public sealed record GtaVCloudJobHeaderSecondaryAssertionResult(
    GtaVSecondaryAssertionBatch Batch,
    GtaVCloudJobHeaderSecondaryAssertionMetrics Metrics);

/// <summary>
/// Adds provider-backed assertions to established UGC fmnm records. It emits no records and
/// performs only an ordinal, exact UTF-8 fmnm join over a previously verified frozen snapshot.
/// </summary>
public sealed class GtaVCloudJobHeaderSecondaryAssertionAdapter
{
    public const string SnapshotFormatId = GtaVRockstarCloudSnapshotBundleLoader.SnapshotFormatId;
    public const string SnapshotFormatVersion = "1";
    public const string ParserId = "grid.gta-v.rockstar-cloud-job-snapshot";
    public const string ParserVersion = "1";
    public const string MappingMethodId = "grid.gta-v.rockstar-cloud-job-fmnm.exact-utf8";
    public const string MappingMethodVersion = "1";
    public const string OnlineClassificationMethodId = "grid.gta-v.rockstar-cloud-job-online-field";
    public const string ActivityFamilyMethodId = "grid.gta-v.rockstar-cloud-job-activity-family";

    private const string UcgNamespace = "rockstar.gta-v.enhanced.ugc-mission";
    private const string UcgObjectType = "UGCMissionFmnm";

    public GtaVCloudJobHeaderSecondaryAssertionAdapter(ContentDigest adapterArtifactDigest)
    {
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.rockstar-cloud-job-secondary"),
            "1",
            adapterArtifactDigest,
            1,
            "cloud-job-fmnm-title-family-link-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                new SupportedKnowledgeFormat(
                    GtaVUgcMissionKnowledgeAdapter.FormatId,
                    GtaVUgcMissionKnowledgeAdapter.ExactFormatVersion,
                    ["rpf7-member"], ["GtaVUgcMissionResource"], [KnowledgeKind.MissionQuest],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    SnapshotFormatId, SnapshotFormatVersion,
                    ["provider-response-envelope"], ["RockstarCloudJobSnapshot"], [KnowledgeKind.MissionQuest],
                    supportsTerminology: true, supportsRelationships: false, supportsHierarchy: false),
            ],
            new KnowledgeAdapterResourceLimits(
                256L * 1024 * 1024,
                maximumArtifacts: 2_000,
                maximumKnowledgeRecords: 10_000,
                maximumRelationships: 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVCloudJobHeaderSecondaryAssertionResult Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        GtaVRockstarCloudSnapshotBundle snapshot)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(snapshot);
        var providerSource = snapshot.ProviderSource;
        var providerRegistryRevision = snapshot.ProviderRegistryRevision;
        var responseEnvelopeArtifact = snapshot.FrozenEnvelopeArtifact;
        var capturedAtUtc = snapshot.CapturedAtUtc;
        var exactLanguageTag = snapshot.Locale;
        var providerObjects = snapshot.Index.Objects;
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Cloud-job assertions require the exact Enhanced base-game scope.");
        if (providerSource.Kind != KnowledgeSourceKind.OfficialProvider ||
            CatalogSourceId.DeriveV1(providerSource.Kind, providerSource.NativeIdentity) != providerSource.Id)
            throw new InvalidDataException("The cloud-job source must be one exact OfficialProvider source.");
        if (!string.Equals(responseEnvelopeArtifact.DeclaredFormat.FormatId, SnapshotFormatId, StringComparison.Ordinal) ||
            !string.Equals(responseEnvelopeArtifact.DeclaredFormat.ExactFormatVersion, SnapshotFormatVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The cloud-job response artifact has an unsupported format coordinate.");
        if (providerObjects.IsDefault || providerObjects.Any(value => value is null))
            throw new ArgumentException("Provider objects must be initialized and non-null.", nameof(providerObjects));

        var exactObjects = providerObjects
            .OrderBy(value => value.ProviderObjectIdentity.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.NativeRevisionIdentity?.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.ObjectFieldPath, StringComparer.Ordinal)
            .ToImmutableArray();
        if (exactObjects.Select(value => (value.ProviderObjectIdentity, value.NativeRevisionIdentity))
            .Distinct().Count() != exactObjects.Length)
            throw new InvalidDataException("The cloud-job index contains duplicate provider object/revision identities.");

        var targetRecords = origin.KnowledgeRecords
            .Where(value => value.Kind == KnowledgeKind.MissionQuest &&
                string.Equals(value.NativeIdentity.Namespace, UcgNamespace, StringComparison.Ordinal) &&
                string.Equals(value.NativeIdentity.ObjectType, UcgObjectType, StringComparison.Ordinal))
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (targetRecords.IsEmpty)
            throw new InvalidDataException("The payload contains no established UGC fmnm MissionQuest records.");
        var targetsByFmnm = targetRecords
            .GroupBy(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.ToImmutableArray(), StringComparer.Ordinal);

        var revisionsById = origin.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var artifactsById = origin.Artifacts.ToDictionary(value => value.Id);
        var fileEvidenceById = origin.FileEvidenceReceipts.ToDictionary(value => value.Id);
        var originCoordinates = targetRecords.ToDictionary(
            value => value.Id,
            value => GetOriginCoordinate(origin, value, revisionsById, artifactsById, fileEvidenceById));
        var artifactFormats = originCoordinates.Values
            .Select(value => value.ArtifactFormat)
            .Append(responseEnvelopeArtifact.FormatBinding)
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .DistinctBy(value => value.ArtifactId)
            .ToImmutableArray();
        var revisionArtifacts = artifactFormats.Select(value => value.ArtifactId).ToImmutableArray();
        var assertingRevisionId = CatalogSourceRevisionId.DeriveV2(
            providerSource.Id, providerRegistryRevision, revisionArtifacts, Descriptor.RevisionId);
        var assertingRevision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(
                assertingRevisionId, providerSource.Id, providerRegistryRevision, revisionArtifacts),
            Descriptor.RevisionId,
            sourceScope,
            artifactFormats);

        var terminology = ImmutableArray.CreateBuilder<TerminologyAssertion>();
        var classifications = ImmutableArray.CreateBuilder<CanonicalSemanticClassificationAssertion>();
        var organizations = ImmutableArray.CreateBuilder<CanonicalOrganizationalValueAssertion>();
        var fileReceipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var referenceReceipts = ImmutableArray.CreateBuilder<CatalogReferenceEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();
        var unresolvedClaims = ImmutableArray.CreateBuilder<UnresolvedCrossSourceClaimContent>();
        var unresolvedBindings = ImmutableArray.CreateBuilder<UnresolvedCrossSourceEvidenceBinding>();
        var unresolvedAssertions = ImmutableArray.CreateBuilder<UnresolvedCrossSourceAssertion>();
        var matchedProviderObjects = 0;
        var ambiguousProviderObjects = 0;
        var matchedTargetIds = new HashSet<KnowledgeRecordId>();

        foreach (var providerObject in exactObjects)
        {
            var candidates = providerObject.ExactFmnm is not null &&
                             targetsByFmnm.TryGetValue(providerObject.ExactFmnm, out var values)
                ? values
                : [];
            if (candidates.Length != 1)
            {
                if (candidates.Length > 1) ambiguousProviderObjects++;
                AddUnresolvedClaims(
                    providerSource.Id, providerObject, candidates, assertingRevisionId, responseEnvelopeArtifact,
                    capturedAtUtc, exactLanguageTag, Descriptor.RevisionId, referenceReceipts,
                    unresolvedClaims, unresolvedBindings, unresolvedAssertions);
                continue;
            }

            matchedProviderObjects++;
            var target = candidates[0];
            matchedTargetIds.Add(target.Id);
            var coordinate = originCoordinates[target.Id];
            var link = CreateTargetLink(
                target, coordinate, assertingRevisionId, responseEnvelopeArtifact,
                providerObject);
            if (!links.Any(value => value.Id == link.Id)) links.Add(link);
            var linkContent = EvidenceClaimContentId.DeriveV1(link);
            var targetReceipt = AddTargetFileReceipt(
                assertingRevisionId, target, coordinate, fileReceipts);
            var providerLinkReceipt = AddReferenceReceipt(
                providerSource.Id, assertingRevisionId, responseEnvelopeArtifact,
                providerObject, providerObject.FmnmFieldPath!, capturedAtUtc, referenceReceipts);
            var targetLinkBinding = AddBinding(
                targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink, target.Id,
                assertingRevisionId, coordinate.FieldPath, linkContent, bindings);
            var providerLinkBinding = AddBinding(
                providerLinkReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink, target.Id,
                assertingRevisionId, providerObject.FmnmFieldPath!, linkContent, bindings);

            if (providerObject.ExactTitle is not null)
            {
                var assertion = new TerminologyAssertion(
                    target.Id, assertingRevisionId, TerminologyAssertionRole.PrimaryName,
                    providerObject.ExactTitle, providerObject.TitleFieldPath!, exactLanguageTag);
                terminology.Add(assertion);
                AddResolvedEnvelope(
                    origin, target, providerSource.Id, assertingRevisionId, responseEnvelopeArtifact,
                    providerObject, assertion.SourceFieldPath, EvidenceClaimKind.Terminology,
                    CrossSourceCanonicalAssertionKind.Terminology,
                    EvidenceClaimContentId.DeriveV1(assertion), link,
                    targetReceipt, providerLinkReceipt, targetLinkBinding, providerLinkBinding,
                    [assertion.SourceFieldPath], capturedAtUtc, referenceReceipts, bindings, envelopes);
            }

            if (providerObject.IsOnline == true)
            {
                var field = providerObject.OnlineScopeFieldPath!;
                var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
                    target.Id, assertingRevisionId, CanonicalProjectionSemantics.MissionOnline,
                    "grid.missionquest-family", "1", OnlineClassificationMethodId, "1", field);
                var assertion = new CanonicalSemanticClassificationAssertion(
                    id, target.Id, assertingRevisionId, CanonicalProjectionSemantics.MissionOnline,
                    "grid.missionquest-family", "1", OnlineClassificationMethodId, "1", field);
                classifications.Add(assertion);
                AddResolvedEnvelope(
                    origin, target, providerSource.Id, assertingRevisionId, responseEnvelopeArtifact,
                    providerObject, field, EvidenceClaimKind.SemanticClassification,
                    CrossSourceCanonicalAssertionKind.SemanticClassification,
                    EvidenceClaimContentId.DeriveV1(assertion), link,
                    targetReceipt, providerLinkReceipt, targetLinkBinding, providerLinkBinding,
                    [field], capturedAtUtc, referenceReceipts, bindings, envelopes);
            }

            if (providerObject.ExactActivityFamilyIdentity is not null &&
                providerObject.ExactActivityFamilyLabel is not null)
            {
                var familyIdentity = CreateFamilyIdentity(providerObject.ExactActivityFamilyIdentity);
                // Family navigation is admitted only when the provider supplies both its exact
                // family identity and exact player-facing label. Partial family coordinates stay
                // measured as missing instead of becoming a technical selector category.
                var field = ActivityFamilyObjectFieldPath(providerObject);
                var id = CanonicalOrganizationalValueAssertionId.DeriveV1(
                    target.Id, assertingRevisionId,
                    CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
                    familyIdentity,
                    providerObject.ExactActivityFamilyLabel,
                    ActivityFamilyMethodId, "1", field);
                var assertion = new CanonicalOrganizationalValueAssertion(
                    id, target.Id, assertingRevisionId,
                    CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
                    familyIdentity,
                    providerObject.ExactActivityFamilyLabel,
                    ActivityFamilyMethodId, "1", field);
                organizations.Add(assertion);
                AddResolvedEnvelope(
                    origin, target, providerSource.Id, assertingRevisionId, responseEnvelopeArtifact,
                    providerObject, field, EvidenceClaimKind.OrganizationalValue,
                    CrossSourceCanonicalAssertionKind.OrganizationalValue,
                    EvidenceClaimContentId.DeriveV1(assertion), link,
                    targetReceipt, providerLinkReceipt, targetLinkBinding, providerLinkBinding,
                    [providerObject.ActivityFamilyIdentityFieldPath!, providerObject.ActivityFamilyLabelFieldPath!],
                    capturedAtUtc, referenceReceipts, bindings, envelopes);
            }
        }

        var titles = terminology.ToImmutable();
        var organizationValues = organizations.ToImmutable();
        var titleConflicts = titles.GroupBy(value => value.KnowledgeRecordId)
            .Count(value => value.Select(item => item.VerbatimValue).Distinct(StringComparer.Ordinal).Count() > 1);
        var familyConflicts = organizationValues.GroupBy(value => value.KnowledgeRecordId)
            .Count(value => value.Select(item => (item.ExactValueIdentity, item.VerbatimDisplayValue)).Distinct().Count() > 1);
        var batch = new GtaVSecondaryAssertionBatch(
            Descriptor,
            assertingRevision,
            [new SourceArtifactRecord(responseEnvelopeArtifact.Id, responseEnvelopeArtifact.Digest)],
            DistinctBy(titles, value => EvidenceClaimContentId.DeriveV1(value).Value),
            DistinctBy(classifications, value => value.Id.Value),
            DistinctBy(organizationValues, value => value.Id.Value),
            DistinctBy(fileReceipts, value => value.Id.Value),
            DistinctBy(bindings, value => value.Id.Value),
            DistinctBy(links, value => value.Id.Value),
            DistinctBy(envelopes, value => value.Id.Value))
        {
            AdditionalSources = [providerSource],
            ReferenceEvidenceReceipts = DistinctBy(referenceReceipts, value => value.Id.Value),
            UnresolvedCrossSourceClaimContents = DistinctBy(unresolvedClaims, value => value.Id.Value),
            UnresolvedCrossSourceEvidenceBindings = DistinctBy(unresolvedBindings, value => value.Id.Value),
            UnresolvedCrossSourceAssertions = DistinctBy(unresolvedAssertions, value => value.Id.Value),
        };
        var metrics = new GtaVCloudJobHeaderSecondaryAssertionMetrics(
            exactObjects.Length,
            matchedProviderObjects,
            matchedTargetIds.Count,
            titles.Select(value => value.KnowledgeRecordId).Distinct().Count(),
            organizationValues.Select(value => value.KnowledgeRecordId).Distinct().Count(),
            classifications.Select(value => value.KnowledgeRecordId).Distinct().Count(),
            exactObjects.Length - matchedProviderObjects - ambiguousProviderObjects,
            targetRecords.Length - matchedTargetIds.Count,
            ambiguousProviderObjects,
            snapshot.Index.EquivalentDuplicateCount,
            titleConflicts,
            familyConflicts);
        return new GtaVCloudJobHeaderSecondaryAssertionResult(batch, metrics);
    }

    private static OriginCoordinate GetOriginCoordinate(
        CanonicalCatalogPayload origin,
        CanonicalKnowledgeRecord target,
        IReadOnlyDictionary<CatalogSourceRevisionId, AdapterBoundCatalogSourceRevisionRecord> revisions,
        IReadOnlyDictionary<SourceArtifactId, SourceArtifactRecord> artifacts,
        IReadOnlyDictionary<EvidenceReceiptId, CatalogFileEvidenceReceipt> fileEvidence)
    {
        var binding = origin.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == target.Id &&
            value.SourceRevisionId == target.SourceRevisionId &&
            value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
            value.ClaimContentId is null);
        if (!fileEvidence.TryGetValue(binding.EvidenceReceiptId, out var catalogReceipt) ||
            !revisions.TryGetValue(target.SourceRevisionId, out var revision) ||
            !artifacts.TryGetValue(catalogReceipt.Receipt.SourceArtifactId, out var artifact))
            throw new InvalidDataException("An established fmnm record lacks exact FILE_VERIFIED origin closure.");
        var format = revision.ArtifactFormats.Single(value => value.ArtifactId == artifact.Id);
        return new OriginCoordinate(binding, catalogReceipt, artifact, format, catalogReceipt.Receipt.SourceFieldPath);
    }

    private static CrossSourceTargetLinkClaim CreateTargetLink(
        CanonicalKnowledgeRecord target,
        OriginCoordinate coordinate,
        CatalogSourceRevisionId assertingRevisionId,
        FrozenSourceArtifact responseArtifact,
        GtaVRockstarCloudJobObject providerObject)
    {
        if (providerObject.ExactFmnm is null || providerObject.FmnmFieldPath is null)
            throw new InvalidDataException("An exact cloud target link requires an explicit fmnm field.");
        var id = CrossSourceTargetLinkClaimId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            target.NativeIdentity, coordinate.Artifact.Id, coordinate.FieldPath,
            assertingRevisionId, providerObject.ProviderObjectIdentity,
            responseArtifact.Id, providerObject.FmnmFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            MappingMethodId, MappingMethodVersion);
        return new CrossSourceTargetLinkClaim(
            id,
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            target.NativeIdentity, coordinate.Artifact.Id, coordinate.FieldPath,
            assertingRevisionId, providerObject.ProviderObjectIdentity,
            responseArtifact.Id, providerObject.FmnmFieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            MappingMethodId, MappingMethodVersion);
    }

    private static CatalogFileEvidenceReceipt AddTargetFileReceipt(
        CatalogSourceRevisionId revisionId,
        CanonicalKnowledgeRecord target,
        OriginCoordinate coordinate,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts)
    {
        var origin = coordinate.Receipt.Receipt;
        var receipt = new FileEvidenceReceipt(
            revisionId, coordinate.Artifact.Id, coordinate.Artifact.Digest,
            GtaVUgcMissionKnowledgeAdapter.ParserIdentity,
            GtaVUgcMissionKnowledgeAdapter.ParserIdentityVersion,
            origin.NativeRecordLocator,
            coordinate.FieldPath,
            origin.ByteOffset,
            origin.ByteLength,
            origin.InterpretedBytesDigest,
            origin.ObservedAtUtc,
            target.NativeIdentity);
        var result = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(receipt), receipt);
        receipts.Add(result);
        return result;
    }

    private static CatalogReferenceEvidenceReceipt AddReferenceReceipt(
        CatalogSourceId providerSourceId,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact responseArtifact,
        GtaVRockstarCloudJobObject providerObject,
        string fieldPath,
        DateTimeOffset capturedAtUtc,
        ImmutableArray<CatalogReferenceEvidenceReceipt>.Builder receipts)
    {
        var receipt = new ReferenceEvidenceReceipt(
            providerSourceId,
            revisionId,
            responseArtifact.Id,
            responseArtifact.Digest,
            providerObject.ProviderObjectIdentity,
            providerObject.NativeRevisionIdentity,
            fieldPath,
            capturedAtUtc);
        var result = new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt);
        if (!receipts.Any(value => value.Id == result.Id)) receipts.Add(result);
        return result;
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
        if (!bindings.Any(value => value.Id == binding.Id)) bindings.Add(binding);
        return binding;
    }

    private static void AddResolvedEnvelope(
        CanonicalCatalogPayload origin,
        CanonicalKnowledgeRecord target,
        CatalogSourceId providerSourceId,
        CatalogSourceRevisionId assertingRevisionId,
        FrozenSourceArtifact responseArtifact,
        GtaVRockstarCloudJobObject providerObject,
        string claimFieldPath,
        EvidenceClaimKind claimKind,
        CrossSourceCanonicalAssertionKind assertionKind,
        EvidenceClaimContentId claimContent,
        CrossSourceTargetLinkClaim link,
        CatalogFileEvidenceReceipt targetReceipt,
        CatalogReferenceEvidenceReceipt providerLinkReceipt,
        EvidenceBinding targetLinkBinding,
        EvidenceBinding providerLinkBinding,
        ImmutableArray<string> claimEvidenceFieldPaths,
        DateTimeOffset capturedAtUtc,
        ImmutableArray<CatalogReferenceEvidenceReceipt>.Builder referenceReceipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        ImmutableArray<CrossSourceCanonicalAssertion>.Builder envelopes)
    {
        if (claimEvidenceFieldPaths.IsDefaultOrEmpty ||
            claimEvidenceFieldPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("A cloud assertion requires exact claim evidence field coordinates.");
        var claimReceiptIds = ImmutableArray.CreateBuilder<EvidenceReceiptId>();
        var claimBindingIds = ImmutableArray.CreateBuilder<EvidenceBindingId>();
        foreach (var evidenceFieldPath in claimEvidenceFieldPaths
                     .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var claimReceipt = AddReferenceReceipt(
                providerSourceId, assertingRevisionId, responseArtifact,
                providerObject, evidenceFieldPath, capturedAtUtc, referenceReceipts);
            var claimBinding = AddBinding(
                claimReceipt.Id, claimKind, target.Id, assertingRevisionId,
                evidenceFieldPath, claimContent, bindings);
            claimReceiptIds.Add(claimReceipt.Id);
            claimBindingIds.Add(claimBinding.Id);
        }
        var originBinding = origin.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == target.Id &&
            value.SourceRevisionId == target.SourceRevisionId &&
            value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
            value.ClaimContentId is null);
        var receiptIds = ImmutableArray.Create(
                originBinding.EvidenceReceiptId,
                targetReceipt.Id,
                providerLinkReceipt.Id)
            .AddRange(claimReceiptIds)
            .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var bindingIds = ImmutableArray.Create(
                originBinding.Id,
                targetLinkBinding.Id,
                providerLinkBinding.Id)
            .AddRange(claimBindingIds)
            .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var id = CrossSourceCanonicalAssertionId.DeriveV1(
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            assertingRevisionId, assertionKind, claimContent,
            link.Id, link.LinkKind, providerObject.ProviderObjectIdentity,
            MappingMethodId, MappingMethodVersion,
            receiptIds, bindingIds, [], []);
        envelopes.Add(new CrossSourceCanonicalAssertion(
            id,
            target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
            assertingRevisionId, assertionKind, claimContent,
            link.Id, link.LinkKind, providerObject.ProviderObjectIdentity,
            MappingMethodId, MappingMethodVersion,
            receiptIds, bindingIds, [], []));
    }

    private static void AddUnresolvedClaims(
        CatalogSourceId providerSourceId,
        GtaVRockstarCloudJobObject providerObject,
        ImmutableArray<CanonicalKnowledgeRecord> candidates,
        CatalogSourceRevisionId assertingRevisionId,
        FrozenSourceArtifact responseArtifact,
        DateTimeOffset capturedAtUtc,
        string exactLanguageTag,
        KnowledgeAdapterRevisionId adapterRevisionId,
        ImmutableArray<CatalogReferenceEvidenceReceipt>.Builder referenceReceipts,
        ImmutableArray<UnresolvedCrossSourceClaimContent>.Builder claims,
        ImmutableArray<UnresolvedCrossSourceEvidenceBinding>.Builder bindings,
        ImmutableArray<UnresolvedCrossSourceAssertion>.Builder assertions)
    {
        var values = ImmutableArray.CreateBuilder<(
            CrossSourceCanonicalAssertionKind Kind,
            object Body,
            string SourcePath,
            ImmutableArray<string> Paths)>();
        if (providerObject.ExactTitle is not null)
            values.Add((CrossSourceCanonicalAssertionKind.Terminology,
                new UnresolvedCrossSourceTerminologyClaim(
                    TerminologyAssertionRole.PrimaryName, providerObject.ExactTitle,
                    providerObject.TitleFieldPath!, exactLanguageTag, null),
                providerObject.TitleFieldPath!, [providerObject.TitleFieldPath!]));
        if (providerObject.IsOnline == true)
            values.Add((CrossSourceCanonicalAssertionKind.SemanticClassification,
                new UnresolvedCrossSourceSemanticClassificationClaim(
                    CanonicalProjectionSemantics.MissionOnline,
                    "grid.missionquest-family", "1",
                    OnlineClassificationMethodId, "1", providerObject.OnlineScopeFieldPath!),
                providerObject.OnlineScopeFieldPath!, [providerObject.OnlineScopeFieldPath!]));
        if (providerObject.ExactActivityFamilyIdentity is not null &&
            providerObject.ExactActivityFamilyLabel is not null)
        {
            var familyField = ActivityFamilyObjectFieldPath(providerObject);
            values.Add((CrossSourceCanonicalAssertionKind.OrganizationalValue,
                new UnresolvedCrossSourceOrganizationalValueClaim(
                    CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
                    CreateFamilyIdentity(providerObject.ExactActivityFamilyIdentity),
                    providerObject.ExactActivityFamilyLabel,
                    ActivityFamilyMethodId, "1", familyField),
                familyField,
                [providerObject.ActivityFamilyIdentityFieldPath!, providerObject.ActivityFamilyLabelFieldPath!]));
        }

        if (values.Count == 0)
            throw new InvalidDataException(
                "An unmatched provider object has no typed semantic field that schema v1 can preserve as an unresolved claim.");

        foreach (var value in values)
        {
            var claimId = UnresolvedCrossSourceClaimContentId.DeriveV1(
                assertingRevisionId, providerObject.ProviderObjectIdentity, value.Kind,
                value.Body as UnresolvedCrossSourceTerminologyClaim,
                null, null, null, null,
                value.Body as UnresolvedCrossSourceSemanticClassificationClaim,
                null,
                value.Body as UnresolvedCrossSourceOrganizationalValueClaim,
                null);
            var claim = new UnresolvedCrossSourceClaimContent(
                claimId, assertingRevisionId, providerObject.ProviderObjectIdentity, value.Kind,
                value.Body as UnresolvedCrossSourceTerminologyClaim,
                null, null, null, null,
                value.Body as UnresolvedCrossSourceSemanticClassificationClaim,
                null,
                value.Body as UnresolvedCrossSourceOrganizationalValueClaim,
                null);
            claims.Add(claim);
            var claimReceiptIds = ImmutableArray.CreateBuilder<EvidenceReceiptId>();
            var claimBindingIds = ImmutableArray.CreateBuilder<UnresolvedCrossSourceEvidenceBindingId>();
            foreach (var path in value.Paths.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
            {
                var receipt = AddReferenceReceipt(
                    providerSourceId, assertingRevisionId, responseArtifact, providerObject,
                    path, capturedAtUtc, referenceReceipts);
                var evidenceBindingId = UnresolvedCrossSourceEvidenceBindingId.DeriveV1(
                    receipt.Id, assertingRevisionId, claim.Id, null, value.Kind,
                    path, EvidenceVerificationKind.ReferenceVerified);
                var evidenceBinding = new UnresolvedCrossSourceEvidenceBinding(
                    evidenceBindingId, receipt.Id, assertingRevisionId, claim.Id, null,
                    value.Kind, path, EvidenceVerificationKind.ReferenceVerified);
                bindings.Add(evidenceBinding);
                claimReceiptIds.Add(receipt.Id);
                claimBindingIds.Add(evidenceBinding.Id);
            }
            var outcome = candidates.IsEmpty ? CorrelationOutcome.Unresolved : CorrelationOutcome.Ambiguous;
            var reason = candidates.IsEmpty
                ? "rockstar-cloud-job-registry.no-exact-fmnm-target"
                : "rockstar-cloud-job-registry.ambiguous-exact-fmnm-target";
            var candidateIds = candidates.Select(candidate => candidate.Id)
                .OrderBy(id => id.Value, StringComparer.Ordinal).ToImmutableArray();
            var receiptIds = claimReceiptIds.ToImmutable();
            var bindingIds = claimBindingIds.ToImmutable();
            var assertionId = UnresolvedCrossSourceAssertionId.DeriveV1(
                claim.Id, assertingRevisionId, adapterRevisionId,
                CrossSourceTargetLinkKind.VersionedExactMapping,
                MappingMethodId, MappingMethodVersion,
                outcome, reason, candidateIds, receiptIds, bindingIds, []);
            assertions.Add(new UnresolvedCrossSourceAssertion(
                assertionId, claim.Id, assertingRevisionId, adapterRevisionId,
                CrossSourceTargetLinkKind.VersionedExactMapping,
                MappingMethodId, MappingMethodVersion,
                outcome, reason, candidateIds, receiptIds, bindingIds, []));
        }
    }

    private static SourceNativeIdentifier CreateFamilyIdentity(string exactValue) =>
        SourceNativeIdentifier.FromExactUtf8(
            "rockstar.games.cloud.job-activity-family",
            "RockstarActivityFamily",
            exactValue,
            "grid.gta-v.rockstar-cloud-activity-family.exact-utf8",
            1);

    private static string ActivityFamilyObjectFieldPath(GtaVRockstarCloudJobObject providerObject)
    {
        var identityPath = providerObject.ActivityFamilyIdentityFieldPath ??
            throw new InvalidDataException("An activity-family identity field path is absent.");
        var labelPath = providerObject.ActivityFamilyLabelFieldPath ??
            throw new InvalidDataException("An activity-family label field path is absent.");
        var identitySeparator = identityPath.LastIndexOf('/');
        var labelSeparator = labelPath.LastIndexOf('/');
        if (identitySeparator <= 0 || labelSeparator <= 0)
            throw new InvalidDataException("Activity-family field paths have no structured parent object.");
        var identityParent = identityPath[..identitySeparator];
        var labelParent = labelPath[..labelSeparator];
        if (!string.Equals(identityParent, labelParent, StringComparison.Ordinal))
            throw new InvalidDataException("Activity-family identity and label are not fields of one exact provider object.");
        return identityParent;
    }

    private static ImmutableArray<T> DistinctBy<T>(
        IEnumerable<T> values,
        Func<T, string> identity)
    {
        var result = ImmutableArray.CreateBuilder<T>();
        var seen = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values.OrderBy(identity, StringComparer.Ordinal))
        {
            var id = identity(value);
            if (seen.TryGetValue(id, out var existing))
            {
                if (!EqualityComparer<T>.Default.Equals(existing, value))
                    throw new InvalidDataException($"Conflicting cloud assertion values share identity '{id}'.");
                continue;
            }
            seen.Add(id, value);
            result.Add(value);
        }
        return result.ToImmutable();
    }

    private sealed record OriginCoordinate(
        EvidenceBinding Binding,
        CatalogFileEvidenceReceipt Receipt,
        SourceArtifactRecord Artifact,
        SourceArtifactFormatBinding ArtifactFormat,
        string FieldPath);
}
