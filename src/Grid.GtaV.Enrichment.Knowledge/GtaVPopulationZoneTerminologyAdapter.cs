using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

namespace Grid.GtaV.Enrichment.Knowledge;

/// <summary>
/// Attaches exact American GXT2 text to established population-zone Locations through
/// exact NameLabel/Jenkins-hash evidence. It never derives or replaces a Location identity.
/// </summary>
public sealed class GtaVPopulationZoneTerminologyAdapter
{
    public const string ParserId = "grid.gta-v-enhanced.population-zone-gxt2-secondary";
    public const string ParserVersion = "1";
    public const string MappingMethodId = "grid.gta-v.population-zone-name-label-gxt2.joaat32";
    public const string MappingMethodVersion = "1";
    public const string BaseLanguageRpfCoordinate = "x64b.rpf!/data/lang/american_rel.rpf";
    public const string BaseGxt2Coordinate = "x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2";
    public const long MaximumGxt2Bytes = 64L * 1024 * 1024;
    private readonly GtaVEnrichmentSourceCorpusIndex? _corpusIndex;

    public GtaVPopulationZoneTerminologyAdapter(ContentDigest adapterArtifactDigest, GtaVEnrichmentSourceCorpusIndex? corpusIndex = null)
    {
        _corpusIndex = corpusIndex;
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.population-zone-gxt2-secondary"),
            "1", adapterArtifactDigest, 1, "population-zone-american-gxt2-terminology-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                new SupportedKnowledgeFormat(
                    GtaVPopulationZonesKnowledgeAdapter.FormatId,
                    GtaVPopulationZonesKnowledgeAdapter.ExactFormatVersion,
                    ["rpf7-member"], ["PopulationZoneIplSection"], [KnowledgeKind.Location],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId,
                    GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion,
                    ["rpf7-member"], ["Rpf7Container"], [],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId,
                    GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion,
                    ["nested-rpf7-member"], ["GXT2"], [KnowledgeKind.Location],
                    supportsTerminology: true, supportsRelationships: false, supportsHierarchy: false),
            ],
            new KnowledgeAdapterResourceLimits(MaximumGxt2Bytes, 3, 100_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVSecondaryAssertionBatch Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact populationZonesArtifact,
        FrozenSourceArtifact baseLanguageRpfArtifact,
        FrozenSourceArtifact baseGxt2Artifact)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(populationZonesArtifact);
        ArgumentNullException.ThrowIfNull(baseLanguageRpfArtifact);
        ArgumentNullException.ThrowIfNull(baseGxt2Artifact);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Population-zone terminology requires the exact Enhanced base-game scope.");
        GtaVEnrichmentParsing.ValidateArtifact(
            populationZonesArtifact,
            GtaVEnrichmentParsing.ResourceCoordinate(
                "PopulationZoneIplSection", GtaVPopulationZonesKnowledgeAdapter.Coordinate),
            new KnowledgeFormatCoordinate(
                GtaVPopulationZonesKnowledgeAdapter.FormatId,
                GtaVPopulationZonesKnowledgeAdapter.ExactFormatVersion));
        GtaVEnrichmentParsing.ValidateArtifact(
            baseLanguageRpfArtifact,
            SecondaryCoordinate(BaseLanguageRpfCoordinate, "Rpf7Container"),
            new KnowledgeFormatCoordinate(
                GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion));
        GtaVEnrichmentParsing.ValidateArtifact(
            baseGxt2Artifact,
            SecondaryCoordinate(BaseGxt2Coordinate, "GXT2"),
            new KnowledgeFormatCoordinate(
                GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion));

        var groups = GtaVEnrichmentParsing.ParsePopulationZones(populationZonesArtifact, _corpusIndex)
            .GroupBy(value => value.NameLabel, StringComparer.Ordinal)
            .ToDictionary(
                value => value.Key,
                value => value.OrderBy(item => item.LabelFieldLocator, StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        var gxt = GtaVEnrichmentParsing.ParseGxt2(baseGxt2Artifact, MaximumGxt2Bytes, _corpusIndex?.Sources);
        var records = origin.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId &&
                            value.Kind == KnowledgeKind.Location &&
                            string.Equals(value.NativeIdentity.Namespace,
                                "rockstar.gta-v.enhanced.population-zones", StringComparison.Ordinal) &&
                            string.Equals(value.NativeIdentity.ObjectType, "NameLabel", StringComparison.Ordinal))
            .ToDictionary(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal);
        if (records.Count == 0 || records.Count != groups.Count || groups.Keys.Any(value => !records.ContainsKey(value)))
            throw new InvalidDataException("Population-zone terminology requires the exact established Location set.");
        var originRevisions = records.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (originRevisions.Length != 1 || !origin.SourceRevisions.Any(value =>
                value.Revision.Id == originRevisions[0] &&
                value.Revision.ArtifactIds.SequenceEqual([populationZonesArtifact.Id])))
            throw new InvalidDataException("Population-zone Locations do not share the exact acquired IPL origin.");
        var originRevision = origin.SourceRevisions.Single(value => value.Revision.Id == originRevisions[0]);
        var source = origin.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var artifacts = ImmutableArray.Create(
            populationZonesArtifact.Id, baseLanguageRpfArtifact.Id, baseGxt2Artifact.Id);
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, artifacts, Descriptor.RevisionId);
        var revision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, artifacts),
            Descriptor.RevisionId, sourceScope,
            [populationZonesArtifact.FormatBinding, baseLanguageRpfArtifact.FormatBinding, baseGxt2Artifact.FormatBinding]);

        var terminology = ImmutableArray.CreateBuilder<TerminologyAssertion>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();
        foreach (var entry in groups.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var hash = GtaVEnrichmentParsing.ComputeJoaat32(entry.Key);
            if (!gxt.TryGetValue(hash, out var gxtEntry)) continue;
            var target = records[entry.Key];
            var occurrence = entry.Value[0];
            var hashIdentity = GtaVEnrichmentParsing.GxtHashIdentity(hash);
            var assertion = new TerminologyAssertion(
                target.Id, revisionId, TerminologyAssertionRole.PrimaryName,
                gxtEntry.Text, gxtEntry.FieldLocator, "en-US", hashIdentity);
            terminology.Add(assertion);
            var claimContent = EvidenceClaimContentId.DeriveV1(assertion);

            var targetReceipt = AddReceipt(
                revisionId, populationZonesArtifact, target.NativeIdentity,
                occurrence.RowLocator, occurrence.LabelFieldLocator, null, null, null, receipts);
            var textBytes = Encoding.UTF8.GetBytes(gxtEntry.Text);
            var gxtReceipt = AddReceipt(
                revisionId, baseGxt2Artifact, hashIdentity,
                hashIdentity.ExactRepresentation, gxtEntry.FieldLocator,
                gxtEntry.TextOffset, gxtEntry.TextLength,
                ContentDigest.ComputeSha256(textBytes), receipts);
            var claimBinding = AddBinding(
                gxtReceipt.Id, EvidenceClaimKind.Terminology, target.Id,
                revisionId, gxtEntry.FieldLocator, claimContent, bindings);

            var linkId = CrossSourceTargetLinkClaimId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                hashIdentity, populationZonesArtifact.Id, occurrence.LabelFieldLocator,
                revisionId, hashIdentity, baseGxt2Artifact.Id, gxtEntry.FieldLocator,
                CrossSourceTargetLinkKind.ExactLabelOrHashKey, MappingMethodId, MappingMethodVersion);
            var link = new CrossSourceTargetLinkClaim(
                linkId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                hashIdentity, populationZonesArtifact.Id, occurrence.LabelFieldLocator,
                revisionId, hashIdentity, baseGxt2Artifact.Id, gxtEntry.FieldLocator,
                CrossSourceTargetLinkKind.ExactLabelOrHashKey, MappingMethodId, MappingMethodVersion);
            links.Add(link);
            var linkContent = EvidenceClaimContentId.DeriveV1(link);
            var targetLinkBinding = AddBinding(
                targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink, target.Id,
                revisionId, occurrence.LabelFieldLocator, linkContent, bindings);
            var assertingLinkBinding = AddBinding(
                gxtReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink, target.Id,
                revisionId, gxtEntry.FieldLocator, linkContent, bindings);
            var originBinding = origin.EvidenceBindings.Single(value =>
                value.KnowledgeRecordId == target.Id &&
                value.SourceRevisionId == target.SourceRevisionId &&
                value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                value.ClaimContentId is null &&
                string.Equals(value.ClaimLocator, occurrence.LabelFieldLocator, StringComparison.Ordinal));
            var receiptIds = ImmutableArray.Create(
                originBinding.EvidenceReceiptId, targetReceipt.Id, gxtReceipt.Id);
            var bindingIds = ImmutableArray.Create(
                originBinding.Id, claimBinding.Id, targetLinkBinding.Id, assertingLinkBinding.Id);
            var envelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.Terminology,
                claimContent, link.Id, link.LinkKind, hashIdentity,
                MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []);
            envelopes.Add(new CrossSourceCanonicalAssertion(
                envelopeId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.Terminology,
                claimContent, link.Id, link.LinkKind, hashIdentity,
                MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []));
        }

        return new GtaVSecondaryAssertionBatch(
            Descriptor,
            revision,
            [
                new SourceArtifactRecord(baseLanguageRpfArtifact.Id, baseLanguageRpfArtifact.Digest),
                new SourceArtifactRecord(baseGxt2Artifact.Id, baseGxt2Artifact.Digest),
            ],
            terminology.ToImmutable(), [], [],
            receipts.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.ToImmutable(), envelopes.ToImmutable());
    }

    private static SourceNativeIdentifier SecondaryCoordinate(string coordinate, string objectType) =>
        SourceNativeIdentifier.FromExactUtf8(
            GtaVEnrichmentParsing.ResourceCoordinateNamespace,
            objectType,
            coordinate,
            GtaVEnrichmentParsing.ResourceCoordinateComparison,
            1);

    private static CatalogFileEvidenceReceipt AddReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string recordLocator,
        string fieldLocator,
        long? offset,
        int? length,
        ContentDigest? interpretedDigest,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest, ParserId, ParserVersion,
            recordLocator, fieldLocator, offset, length, interpretedDigest,
            artifact.ObservedAtUtc, nativeObject);
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
        EvidenceClaimContentId contentId,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
        var binding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, locator, contentId),
            receiptId, kind, recordId, revisionId, locator, contentId);
        bindings.Add(binding);
        return binding;
    }
}
