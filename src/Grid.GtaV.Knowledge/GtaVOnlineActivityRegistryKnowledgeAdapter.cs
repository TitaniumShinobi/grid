using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Registers the exact player-facing activities and playlists carried by Rockstar's
/// shipped Online UGC registry documents. It deliberately does not correlate these
/// objects to legacy fmnm identities unless the source bytes provide an exact join.
/// </summary>
public sealed class GtaVOnlineActivityRegistryKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.online-activity-registry-json";
    public const string ParserIdentityVersion = "1";
    public const long MaximumArtifactBytes = GtaVUgcMissionKnowledgeAdapter.MaximumArtifactBytes;

    public GtaVOnlineActivityRegistryKnowledgeAdapter(
        ContentDigest adapterArtifactDigest,
        GtaVSupportedSourceCorpusIndex? corpusIndex = null)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.online-activity-registry",
            "online-activity-registry-v1",
            GtaVUgcMissionKnowledgeAdapter.FormatId,
            "GtaVUgcMissionResource",
            ".ugc",
            KnowledgeKind.MissionQuest,
            MaximumArtifactBytes,
            maximumArtifacts: 10_000,
            maximumKnowledgeRecords: 10_000,
            corpusIndex,
            supportsTerminology: true)
    {
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.MissionQuest;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.online-activity-registry";
    private protected override string RecordComparisonMethod => "grid.gta-v.online-activity-registry.exact-utf8";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(IndexedStrictUtf8(artifact, MaximumArtifactBytes), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Online activity registry resource is not supported JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("An Online activity registry resource must be a JSON object.");

            var root = document.RootElement;
            var hasMission = TrySingle(root, "mission", out var mission);
            var hasList = TrySingle(root, "list", out var list);
            if (hasMission && hasList)
                throw new InvalidDataException("An Online activity registry resource has ambiguous shapes.");
            if (hasMission)
                return ParseGeneratedActivity(artifact, mission);
            if (hasList)
                return ParsePlaylistRegistry(artifact, root, list);
            return new ParsedArtifact([], []);
        }
    }

    private ParsedArtifact ParseGeneratedActivity(FrozenSourceArtifact artifact, JsonElement mission)
    {
        if (mission.ValueKind != JsonValueKind.Object ||
            !TrySingle(mission, "gen", out var generated) ||
            generated.ValueKind != JsonValueKind.Object ||
            !TrySingle(generated, "nm", out var name) ||
            name.ValueKind != JsonValueKind.String)
            return new ParsedArtifact([], []);

        var title = RequireExactText(name.GetString(), "generated activity title");
        var coordinate = artifact.SourceCoordinate.ExactRepresentation;
        var recordLocator = CreateLocator(coordinate, "/mission/gen");
        var titleLocator = CreateLocator(coordinate, "/mission/gen/nm");
        return new ParsedArtifact(
            [CreateRecord(
                "GtaOnlineUgcGeneratedActivity",
                coordinate,
                recordLocator,
                recordLocator,
                titleLocator,
                title,
                null,
                null)],
            []);
    }

    private ParsedArtifact ParsePlaylistRegistry(
        FrozenSourceArtifact artifact,
        JsonElement root,
        JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("An Online playlist list must be an array.");

        var coordinate = artifact.SourceCoordinate.ExactRepresentation;
        var hasParentName = TrySingle(root, "name", out var parentNameElement);
        string? parentName = null;
        if (hasParentName)
        {
            if (parentNameElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("An Online playlist name must be one exact string.");
            parentName = RequireExactText(parentNameElement.GetString(), "playlist title");
        }

        var records = ImmutableArray.CreateBuilder<ParsedRecord>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !TrySingle(entry, "cid", out var cidElement) || cidElement.ValueKind != JsonValueKind.String ||
                !TrySingle(entry, "name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("An Online playlist entry requires exact cid and name fields.");

            var cid = RequireExactText(cidElement.GetString(), "playlist entry cid");
            var title = RequireExactText(nameElement.GetString(), "playlist entry title");
            if (!identities.Add(cid))
                throw new InvalidDataException("An Online playlist contains a duplicate cid.");
            var entryPath = $"/list/{index}";
            var recordLocator = CreateLocator(coordinate, entryPath);
            var titleLocator = CreateLocator(coordinate, entryPath + "/name");
            records.Add(CreateRecord(
                parentName is null ? "GtaOnlineUgcPlaylist" : "GtaOnlineUgcPlaylistEntry",
                cid,
                recordLocator,
                CreateLocator(coordinate, entryPath + "/cid"),
                titleLocator,
                title,
                parentName,
                parentName is null ? null : CreateLocator(coordinate, "/name")));
            index++;
        }
        return new ParsedArtifact(records.ToImmutable(), []);
    }

    private ParsedRecord CreateRecord(
        string objectType,
        string nativeKey,
        string recordLocator,
        string identityLocator,
        string titleLocator,
        string title,
        string? playlistTitle,
        string? playlistTitleLocator)
    {
        var classifications = ImmutableArray.Create(new ParsedSemanticClassificationFact(
            CanonicalProjectionSemantics.MissionOnline,
            "grid.missionquest-family",
            "1",
            "grid.gta-v.online-ugc-registry-source-family",
            "1",
            recordLocator,
            recordLocator));
        var organization = playlistTitle is null
            ? ImmutableArray<ParsedOrganizationalValueFact>.Empty
            : ImmutableArray.Create(new ParsedOrganizationalValueFact(
                CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
                SourceNativeIdentifier.FromExactUtf8(
                    RecordNamespace,
                    "RockstarPlaylistTitle",
                    playlistTitle,
                    "grid.gta-v.playlist-title.exact-utf8",
                    1),
                playlistTitle,
                "grid.gta-v.online-playlist-membership",
                "1",
                playlistTitleLocator!,
                recordLocator));
        return new ParsedRecord(
            objectType,
            nativeKey,
            [new ParsedEvidenceLocation(recordLocator, identityLocator)],
            [new ParsedTerminologyFact(
                TerminologyAssertionRole.PrimaryName,
                title,
                titleLocator,
                recordLocator,
                "und")],
            classifications,
            organization);
    }

    private static bool TrySingle(JsonElement value, string propertyName, out JsonElement result)
    {
        var matches = value.EnumerateObject().Where(property => property.NameEquals(propertyName)).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"The JSON property {propertyName} is duplicated.");
        result = matches.Length == 1 ? matches[0].Value : default;
        return matches.Length == 1;
    }

    private static string RequireExactText(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException($"The {name} is absent or empty.");
        _ = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.online-activity-registry.text",
            "VerbatimText",
            value,
            "grid.gta-v.verbatim-text.exact-utf8",
            1);
        return value;
    }
}
