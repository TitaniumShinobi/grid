using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

public sealed class GtaVOnlineActivityOrganizationSecondaryAssertionAdapter
{
    private readonly GtaVPresentationCorpusIndex index;
    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public GtaVOnlineActivityOrganizationSecondaryAssertionAdapter(ContentDigest digest, GtaVPresentationCorpusIndex index)
    { this.index = index; Descriptor = GtaVPresentationEvidenceBuilder.DescriptorFor("online-activity", digest, KnowledgeKind.MissionQuest); }

    public GtaVSecondaryAssertionBatch Extract(CanonicalCatalogPayload origin, KnowledgeSourceScope scope)
    {
        var builder = new GtaVPresentationEvidenceBuilder(origin, scope, Descriptor, index.Artifacts.Values);
        foreach (var target in origin.KnowledgeRecords.Where(x => x.Kind == KnowledgeKind.MissionQuest &&
                     x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.online-activity-registry" &&
                     x.NativeIdentity.ObjectType == "GtaOnlineUgcGeneratedActivity"))
        {
            var coordinate = target.NativeIdentity.ExactRepresentation;
            if (!index.Artifacts.TryGetValue(coordinate, out var artifact)) continue;
            using var doc = JsonDocument.Parse(artifact.ExactBytes.AsMemory());
            var gen = doc.RootElement.GetProperty("mission").GetProperty("gen");
            foreach (var field in new[] { "type", "subtype", "nm" })
                if (gen.EnumerateObject().Count(x => x.Name == field) != 1) throw new InvalidDataException("Ambiguous generated activity fields.");
            var tuple = (gen.GetProperty("type").GetInt32(), gen.GetProperty("subtype").GetInt32());
            var key = tuple switch { (0, 0) or (0, 2) => "FMMC_RSTAR_MS", (2, 1) => "FMMC_RSTAR_LR", _ => null };
            if (key is null) continue;
            var label = index.ResolveText(key);
            if (label is null) continue;
            var map = new GtaVPresentationEvidence(index.RaceCreator, tuple.Item1 == 0 ? "func_15198/non-bookmark/mission-subtype" : "func_15196/race-subtype", true);
            var link = builder.Link(target, map);
            var tupleEvidence = new GtaVPresentationEvidence(artifact, "json:/mission/gen/type;/mission/gen/subtype", false);
            builder.Organize(target, link, CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode, key, label.Entry.Text, label.Entry.FieldLocator,
                [map, tupleEvidence, GtaVPresentationEvidence.FromText(label)]);
            var locale = index.GeneratedLocale(coordinate);
            if (locale is null) continue;
            var title = gen.GetProperty("nm").GetString();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var titleEvidence = new GtaVPresentationEvidence(artifact, "json:/mission/gen/nm", false);
            var localeEvidence = new GtaVPresentationEvidence(index.Freemode, "func_19770/GET_CURRENT_LANGUAGE/" + locale + ";func_19771", true);
            var languageEvidence = new GtaVPresentationEvidence(index.LanguageEnum, "GET_CURRENT_LANGUAGE/" + locale, true);
            GtaVPresentationEvidence[] evidence = [map, tupleEvidence, titleEvidence, localeEvidence, languageEvidence];
            builder.Title(target, link, title, titleEvidence.FieldPath, evidence, locale);
            if (locale == "en-US") builder.Classify(target, link, CanonicalProjectionSemantics.SelectorPlayerAddressable, titleEvidence.FieldPath, evidence);
        }
        AddPlaylists(origin, builder);
        return builder.Build();
    }

    private void AddPlaylists(CanonicalCatalogPayload origin, GtaVPresentationEvidenceBuilder builder)
    {
        var records = origin.KnowledgeRecords.Where(x => x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.online-activity-registry").ToArray();
        foreach (var playlist in records.Where(x => x.NativeIdentity.ObjectType == "GtaOnlineUgcPlaylist"))
        {
            var candidates = index.Artifacts.Values.Where(x => x.SourceCoordinate.ExactRepresentation.EndsWith("/" + playlist.NativeIdentity.ExactRepresentation + "_00.ugc", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) continue;
            var artifact = candidates[0];
            using var document = JsonDocument.Parse(artifact.ExactBytes.AsMemory());
            if (!document.RootElement.TryGetProperty("list", out var list) || !document.RootElement.TryGetProperty("name", out _)) continue;
            var map = new GtaVPresentationEvidence(artifact, "json:/list", false);
            var link = builder.Link(playlist, map);
            builder.Classify(playlist, link, CanonicalProjectionSemantics.MissionPlaylist, map.FieldPath, [map]);
            var ordinal = 0;
            foreach (var entry in list.EnumerateArray())
            {
                var cid = entry.GetProperty("cid").GetString();
                var targetCandidates = records.Where(x => x.NativeIdentity.ObjectType == "GtaOnlineUgcPlaylistEntry" && x.NativeIdentity.ExactRepresentation == cid &&
                    origin.EvidenceBindings.Any(b => b.KnowledgeRecordId == x.Id && b.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && b.SourceRevisionId == x.SourceRevisionId &&
                        origin.FileEvidenceReceipts.Any(r => r.Id == b.EvidenceReceiptId && r.Receipt.SourceArtifactId == artifact.Id))).ToArray();
                var field = "json:/list/" + ordinal + "/cid";
                var native = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.online-activity-registry", "GtaOnlineUgcPlaylistEntry", cid!, "grid.gta-v.online-activity-registry.exact-utf8", 1);
                var relation = new RelationshipAssertion(playlist.Id, builder.RevisionId, new RelationshipSemanticId("grid.missionquest.playlist-entry"), "RockstarPlaylistEntryOrdinal:" + ordinal, field, native, targetCandidates.Length == 1 ? targetCandidates[0].Id : null);
                builder.AddRelationship(playlist, link, relation, [new GtaVPresentationEvidence(artifact, field, false)]);
                ordinal++;
            }
        }
    }
}
