using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Enhanced-only preproduction adapter for exact UGC mission JSON member bytes.
/// mission.fmnm is a source-native MissionQuest identity, not display terminology.
/// </summary>
public sealed class GtaVUgcMissionKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.ugc-mission-json";
    public const string ParserIdentityVersion = "1";
    public const string FormatId = "rockstar.gta-v.ugc.mission-json";
    public const string ExactFormatVersion = "1";
    public const long MaximumArtifactBytes = 32L * 1024 * 1024;
    private const string MissingNativeIdentityReason = "rockstar.gta-v.ugc.mission-native-identity-missing";

    public GtaVUgcMissionKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVSupportedSourceCorpusIndex? corpusIndex = null)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.ugc-mission",
            "ugc-fmnm-mission-identities-v1",
            FormatId,
            "GtaVUgcMissionResource",
            ".ugc",
            KnowledgeKind.MissionQuest,
            MaximumArtifactBytes,
            maximumArtifacts: 10_000,
            maximumKnowledgeRecords: 10_000,
            corpusIndex)
    {
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.MissionQuest;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.ugc-mission";
    private protected override string RecordComparisonMethod => "grid.gta-v.ugc-fmnm.exact-utf8";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var text = IndexedStrictUtf8(artifact, MaximumArtifactBytes);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The UGC resource is not supported well-formed JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A UGC resource must have a top-level JSON object.");

            var missionProperties = document.RootElement.EnumerateObject()
                .Where(value => value.NameEquals("mission"))
                .ToArray();
            if (missionProperties.Length == 0)
                return new ParsedArtifact([], []);
            if (missionProperties.Length != 1 || missionProperties[0].Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A UGC mission resource must contain exactly one mission object.");

            var mission = missionProperties[0].Value;
            var nameProperties = mission.EnumerateObject()
                .Where(value => value.NameEquals("fmnm"))
                .ToArray();
            var coordinate = artifact.SourceCoordinate.ExactRepresentation;
            if (nameProperties.Length == 0)
            {
                var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
                    RecordNamespace,
                    "UGCMissionResource",
                    coordinate,
                    "grid.gta-v.ugc-resource-coordinate.exact-utf8",
                    1);
                var missionLocator = CreateLocator(coordinate, "/mission");
                return new ParsedArtifact(
                    [],
                    [new ParsedUnresolvedAssertion(
                        nativeIdentity,
                        KnowledgeKind.MissionQuest,
                        MissingNativeIdentityReason,
                        missionLocator,
                        missionLocator)]);
            }
            if (nameProperties.Length != 1 || nameProperties[0].Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A UGC mission fmnm must be absent or exactly one JSON string.");

            var nativeKey = nameProperties[0].Value.GetString()!;
            ValidateNativeIdentity(nativeKey);
            return new ParsedArtifact(
                [new ParsedRecord(
                    "UGCMissionFmnm",
                    nativeKey,
                    [new ParsedEvidenceLocation(
                        CreateLocator(coordinate, "/mission"),
                        CreateLocator(coordinate, "/mission/fmnm"))])],
                []);
        }
    }

    private void ValidateNativeIdentity(string value)
    {
        try
        {
            _ = SourceNativeIdentifier.FromExactUtf8(
                RecordNamespace,
                "UGCMissionFmnm",
                value,
                RecordComparisonMethod,
                1);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A mission fmnm is not a valid exact source-native identity.", exception);
        }
    }
}
