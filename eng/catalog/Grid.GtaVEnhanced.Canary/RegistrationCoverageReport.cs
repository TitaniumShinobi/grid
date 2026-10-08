using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class RegistrationCoverageReport
{
    public static object WithPlan2(object historicalReport, GtaVRouteMetrics routes,
        GtaVItemCorpusIndex items, GtaVPresentationCorpusIndex presentation)
    {
        var report = JsonSerializer.SerializeToNode(historicalReport, CompactJson)!.AsObject();
        report.Remove("contentSha256");
        if (report["unsupportedSourceFamilies"] is System.Text.Json.Nodes.JsonArray unsupported)
            foreach (var entry in unsupported.Where(value => value?["sourceFamilyId"]?.GetValue<string>() ==
                         "rockstar.gta-v.enhanced.patch-american-localization").ToArray()) unsupported.Remove(entry);
        var labels = items.Rows.Where(row => row.LabelKey is not null)
            .Select(row => items.ResolveText(row.Source, row.LabelKey!).Select(text => text.Text).Distinct(StringComparer.Ordinal).Count()).ToArray();
        report["plan2"] = JsonSerializer.SerializeToNode(new
        {
            coverage = "partial",
            locale = "en-US",
            routes,
            itemFamilies = items.Sources.GroupBy(value => value.Family, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new
                {
                    family = group.Key,
                    sources = group.Count(),
                    parsedRows = items.Rows.Count(row => row.Source.Family == group.Key),
                }).ToArray(),
            itemDeclaredSourceGaps = items.Unresolved,
            itemTerminologyJoinOutcomes = new { exact = labels.Count(count => count == 1), unmatched = labels.Count(count => count == 0), conflicting = labels.Count(count => count > 1) },
            actorReferenceModels = presentation.Actors.Select(row => row.ModelHash).Distinct().Count(),
            generatedActivityTextDiagnostics = presentation.GeneratedTextDiagnostics,
            deferredCoverage = new[] {
                "Location: geographic parents, POIs, properties, IPL/interior and room names, effective mounted roads",
                "MissionQuest: fmnm title bridge, Story registry, full taxonomy, playlist locale, heist constituents",
                "Item: weapon category labels, wardrobe sections, armor, equipment/consumables, visible generic pickups, vehicle customization",
                "Actor: remaining/conditional models, dynamic Online actors, factions, content and mission relationships",
            },
        }, CompactJson);
        report["contentSha256"] = Sha256(JsonSerializer.SerializeToUtf8Bytes(report, CompactJson));
        return report;
    }

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public static object Create(
        CanonicalCatalogPackage package,
        RegistrationMissionQuestSourceLedger missionQuestLedger,
        string sourceManifestPath,
        string actorSourceManifestPath,
        string spatialSourceManifestPath,
        GtaRegistrationSourceRegistry registrationSourceRegistry)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(missionQuestLedger);
        ArgumentNullException.ThrowIfNull(registrationSourceRegistry);
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException(
                "Registration coverage requires a structurally verified package: " +
                string.Join("; ", verification.Issues));
        var payload = package.Payload;
        if (!File.Exists(sourceManifestPath))
            throw new InvalidDataException("The checked-in GTA source-family manifest is absent.");
        _ = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity(sourceManifestPath);
        var actorSourceManifestBytes = File.ReadAllBytes(actorSourceManifestPath);
        var actorSourceFamily = ReadActorSourceFamily(actorSourceManifestBytes, package);
        var spatialSourceManifestBytes = File.ReadAllBytes(spatialSourceManifestPath);
        var spatialSourceFamily = ReadSpatialSourceFamily(spatialSourceManifestBytes, package);

        var sourceManifestBytes = File.ReadAllBytes(sourceManifestPath);
        ImmutableArray<UnsupportedFamilyEntry> unsupportedFamilies;
        using (var manifest = JsonDocument.Parse(sourceManifestBytes))
        {
            if (!manifest.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 2)
                throw new InvalidDataException("The GTA source-family manifest must use schema version 2.");
            RequireManifestText(manifest.RootElement, "gameId", package.Manifest.GameScope.GameId.Value);
            RequireManifestText(
                manifest.RootElement,
                "steamBuildId",
                package.Manifest.GameScope.ExactGameVersion?.ExactRepresentation ?? string.Empty);
            if (!manifest.RootElement.TryGetProperty("sourceFamilies", out var families) ||
                families.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The GTA source-family manifest has no sourceFamilies array.");
            var observedByFamily = ReadManifestMemberCounts(manifest.RootElement);
            unsupportedFamilies = families.EnumerateArray()
                .Select(value => new UnsupportedFamilyEntry(
                    RequireText(value, "sourceFamilyId"),
                    RequireText(value, "status"),
                    string.Join(',', value.GetProperty("knowledgeKinds").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .OrderBy(item => item, StringComparer.Ordinal)),
                    OptionalText(value, "reasonCode"),
                    observedByFamily.GetValueOrDefault(RequireText(value, "sourceFamilyId")),
                    OptionalText(value, "potentialCoverage")))
                .Where(value => !string.Equals(value.Status, "supported", StringComparison.Ordinal))
                .Where(value => !string.Equals(
                    value.SourceFamilyId, actorSourceFamily.SourceFamilyId, StringComparison.Ordinal))
                .Where(value => !string.Equals(
                    value.SourceFamilyId,
                    "rockstar.gta-v.enhanced.mlo-interior-graph",
                    StringComparison.Ordinal))
                .Concat(missionQuestLedger.UnsupportedArtifacts
                    .GroupBy(value => value.ReasonCode, StringComparer.Ordinal)
                    .Select(value => new UnsupportedFamilyEntry(
                        "rockstar.gta-v.enhanced.ugc.unsupported-document-shapes",
                        "unsupported-observed",
                        KnowledgeKind.MissionQuest.ToString(),
                        value.Key,
                        value.Count(),
                        "Unsupported UGC document semantics")))
                .OrderBy(value => value.SourceFamilyId, StringComparer.Ordinal)
                .ThenBy(value => value.ReasonCode, StringComparer.Ordinal)
                .ToImmutableArray();
        }
        unsupportedFamilies = unsupportedFamilies
            .AddRange(spatialSourceFamily.UnsupportedFamilies)
            .OrderBy(value => value.SourceFamilyId, StringComparer.Ordinal)
            .ThenBy(value => value.ReasonCode, StringComparer.Ordinal)
            .ToImmutableArray();

        var missionRecordCount = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.MissionQuest);
        var missionUnresolvedCount = payload.UnresolvedSourceAssertions.Count(
            value => value.CandidateKind == KnowledgeKind.MissionQuest);
        if (missionRecordCount == 0 || missionUnresolvedCount == 0 ||
            missionQuestLedger.DiscoveredSourceObjects == 0)
            throw new InvalidDataException("MissionQuest coverage has an empty required accounting dimension.");

        var recordsById = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var relationshipByKind = payload.RelationshipAssertions
            .GroupBy(value => recordsById[value.SubjectKnowledgeRecordId].Kind)
            .ToDictionary(value => value.Key, value => value.ToImmutableArray());
        var classificationByKind = payload.SemanticClassificationAssertions
            .GroupBy(value => recordsById[value.KnowledgeRecordId].Kind)
            .ToDictionary(value => value.Key, value => value.ToImmutableArray());
        var organizationByKind = payload.OrganizationalValueAssertions
            .GroupBy(value => recordsById[value.KnowledgeRecordId].Kind)
            .ToDictionary(value => value.Key, value => value.ToImmutableArray());
        var receiptClass = payload.FileEvidenceReceipts
            .Select(value => (value.Id, Class: EvidenceVerificationKind.FileVerified.ToString()))
            .Concat(payload.ReferenceEvidenceReceipts.Select(value =>
                (value.Id, Class: EvidenceVerificationKind.ReferenceVerified.ToString())))
            .ToDictionary(value => value.Id, value => value.Class);

        var kinds = Enum.GetValues<KnowledgeKind>()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .Select(kind =>
            {
                var records = payload.KnowledgeRecords.Where(value => value.Kind == kind).ToImmutableArray();
                var recordIds = records.Select(value => value.Id).ToHashSet();
                var terminology = payload.TerminologyAssertions
                    .Where(value => recordIds.Contains(value.KnowledgeRecordId))
                    .ToImmutableArray();
                var classifications = classificationByKind.GetValueOrDefault(kind, []);
                var organizations = organizationByKind.GetValueOrDefault(kind, []);
                var relationships = relationshipByKind.GetValueOrDefault(kind, []);
                var bindings = payload.EvidenceBindings
                    .Where(value => recordIds.Contains(value.KnowledgeRecordId))
                    .ToImmutableArray();
                var sourceRevisionIds = records.Select(value => value.SourceRevisionId)
                    .Concat(terminology.Select(value => value.SourceRevisionId))
                    .Concat(classifications.Select(value => value.SourceRevisionId))
                    .Concat(organizations.Select(value => value.SourceRevisionId))
                    .Concat(relationships.Select(value => value.SourceRevisionId))
                    .Distinct()
                    .OrderBy(value => value.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var adapterRevisionIds = payload.SourceRevisions
                    .Where(value => sourceRevisionIds.Contains(value.Revision.Id))
                    .Select(value => value.AdapterRevisionId)
                    .Distinct()
                    .OrderBy(value => value.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var artifactIds = payload.SourceRevisions
                    .Where(value => sourceRevisionIds.Contains(value.Revision.Id))
                    .SelectMany(value => value.Revision.ArtifactIds)
                    .Distinct()
                    .OrderBy(value => value.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                var namedIds = terminology
                    .Where(value => value.Role == TerminologyAssertionRole.PrimaryName)
                    .Select(value => value.KnowledgeRecordId)
                    .Distinct()
                    .ToHashSet();
                var evidenceClasses = bindings
                    .Where(value => receiptClass.ContainsKey(value.EvidenceReceiptId))
                    .GroupBy(value => receiptClass[value.EvidenceReceiptId], StringComparer.Ordinal)
                    .OrderBy(value => value.Key, StringComparer.Ordinal)
                    .Select(value => new { evidenceClass = value.Key, receipts = value.Select(item => item.EvidenceReceiptId).Distinct().Count() })
                    .ToArray();

                return new
                {
                    knowledgeKind = kind.ToString(),
                    discoveredSourceObjects = kind == KnowledgeKind.MissionQuest
                        ? missionQuestLedger.DiscoveredSourceObjects
                        : payload.EvidenceBindings.Count(value =>
                              recordIds.Contains(value.KnowledgeRecordId) &&
                              value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity) +
                          payload.UnresolvedSourceAssertions.Count(value => value.CandidateKind == kind),
                    canonicalRecords = records.Length,
                    playerFacingTerminologyResolved = namedIds.Count,
                    presentationLocale = "en-US",
                    exactEnglishPrimaryNames = terminology
                        .Where(value => value.Role == TerminologyAssertionRole.PrimaryName && value.LanguageTag == "en-US")
                        .GroupBy(value => value.KnowledgeRecordId)
                        .Count(group => group.Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).Count() == 1),
                    explicitlyPlayerAddressableRecords = classifications
                        .Where(value => value.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable)
                        .Select(value => value.KnowledgeRecordId).Distinct().Count(),
                    identifierOnly = records.Count(value => !namedIds.Contains(value.Id)),
                    terminology = terminology
                        .GroupBy(value => new { role = value.Role.ToString(), languageTag = value.LanguageTag })
                        .OrderBy(value => value.Key.role, StringComparer.Ordinal)
                        .ThenBy(value => value.Key.languageTag, StringComparer.Ordinal)
                        .Select(value => new { value.Key.role, value.Key.languageTag, count = value.Count() })
                        .ToArray(),
                    classifiedRecords = kind == KnowledgeKind.Location
                        ? payload.LocationSemanticClassificationAssertions.Select(value => value.KnowledgeRecordId).Distinct().Count()
                        : classifications.Select(value => value.KnowledgeRecordId).Distinct().Count(),
                    classifications = (kind == KnowledgeKind.Location
                            ? payload.LocationSemanticClassificationAssertions.Select(value => value.RoleId.Value)
                            : classifications.Select(value => value.RoleId.Value))
                        .GroupBy(value => value, StringComparer.Ordinal)
                        .OrderBy(value => value.Key, StringComparer.Ordinal)
                        .Select(value => new { semanticId = value.Key, count = value.Count() })
                        .ToArray(),
                    organizedRecords = organizations.Select(value => value.KnowledgeRecordId).Distinct().Count(),
                    organizationalValues = organizations
                        .GroupBy(value => value.DimensionId.Value, StringComparer.Ordinal)
                        .OrderBy(value => value.Key, StringComparer.Ordinal)
                        .Select(value => new
                        {
                            dimensionId = value.Key,
                            assertions = value.Count(),
                            distinctValues = value.Select(item => item.ExactValueIdentity).Distinct().Count(),
                            displayBearingAssertions = value.Count(item => item.VerbatimDisplayValue is not null),
                            localeQualifiedAssertions = value.Count(item => item.LanguageTag == "en-US"),
                        }).ToArray(),
                    relationships = relationships
                        .GroupBy(value => new { semanticId = value.SemanticId.Value, resolution = value.Resolution.ToString() })
                        .OrderBy(value => value.Key.semanticId, StringComparer.Ordinal)
                        .ThenBy(value => value.Key.resolution, StringComparer.Ordinal)
                        .Select(value => new { value.Key.semanticId, value.Key.resolution, count = value.Count() })
                        .ToArray(),
                    hierarchyRelationships = kind == KnowledgeKind.Location
                        ? relationships.Count(value => IsLocationHierarchy(value.SemanticId.Value))
                        : 0,
                    unresolvedSourceAssertions = payload.UnresolvedSourceAssertions.Count(value => value.CandidateKind == kind),
                    unsupportedSourceObjects = kind == KnowledgeKind.MissionQuest
                        ? missionQuestLedger.UnsupportedArtifacts.Length
                        : kind == KnowledgeKind.Location
                            ? spatialSourceFamily.UnsupportedOutcomes
                            : 0,
                    evidence = evidenceClasses,
                    sourceRevisionIds = sourceRevisionIds.Select(value => value.Value).ToArray(),
                    artifactIds = artifactIds.Select(value => value.Value).ToArray(),
                    adapterRevisionIds = adapterRevisionIds.Select(value => value.Value).ToArray(),
                };
            }).ToArray();

        var body = new
        {
            schemaVersion = 1,
            gameId = package.Manifest.GameScope.GameId.Value,
            exactGameVersion = package.Manifest.GameScope.ExactGameVersion?.ExactRepresentation,
            packageId = package.Id.Value,
            catalogRevisionId = package.Manifest.CatalogRevisionId.Value,
            payloadDigest = package.Manifest.PayloadDigest.Value,
            effectiveCoverage = package.Manifest.EffectiveCoverage.ToString(),
            sourceFamilyManifestSha256 = Sha256(sourceManifestBytes),
            actorSourceFamilyManifestSha256 = CanonicalJsonSha256(actorSourceManifestBytes),
            spatialSourceFamilyManifestSha256 = CanonicalJsonSha256(spatialSourceManifestBytes),
            registrationSourceRegistrySchemaVersion = registrationSourceRegistry.SchemaVersion,
            registrationSourceRegistrySha256 = registrationSourceRegistry.DocumentSha256,
            actorSourceFamily,
            spatialSourceFamily,
            knowledgeKinds = kinds,
            unsupportedSourceFamilies = unsupportedFamilies,
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, CompactJson);
        return new
        {
            body.schemaVersion,
            contentSha256 = Sha256(bodyBytes),
            body.gameId,
            body.exactGameVersion,
            body.packageId,
            body.catalogRevisionId,
            body.payloadDigest,
            body.effectiveCoverage,
            body.sourceFamilyManifestSha256,
            body.actorSourceFamilyManifestSha256,
            body.spatialSourceFamilyManifestSha256,
            body.registrationSourceRegistrySchemaVersion,
            body.registrationSourceRegistrySha256,
            body.actorSourceFamily,
            body.spatialSourceFamily,
            body.knowledgeKinds,
            body.unsupportedSourceFamilies,
        };
    }

    private static ActorSourceFamilyEntry ReadActorSourceFamily(
        ReadOnlySpan<byte> manifestBytes,
        CanonicalCatalogPackage package)
    {
        using var document = JsonDocument.Parse(manifestBytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1)
            throw new InvalidDataException("The GTA Actor source-family manifest must use schema version 1.");
        RequireManifestText(root, "gameId", package.Manifest.GameScope.GameId.Value);
        RequireManifestText(
            root,
            "steamBuildId",
            package.Manifest.GameScope.ExactGameVersion?.ExactRepresentation ?? string.Empty);
        var familyId = RequireText(root, "sourceFamilyId");
        if (!string.Equals(
                familyId,
                "rockstar.gta-v.enhanced.mounted-ped-registry",
                StringComparison.Ordinal))
            throw new InvalidDataException("The GTA Actor source-family identity is unsupported.");
        var resident = root.GetProperty("residentCandidates");
        var mountGraph = root.GetProperty("mountGraph");
        if (resident.ValueKind != JsonValueKind.Array || resident.GetArrayLength() != 2 ||
            mountGraph.ValueKind != JsonValueKind.Object ||
            mountGraph.GetProperty("pedMetadataPacks").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The GTA Actor source-family closure is malformed.");
        return new ActorSourceFamilyEntry(
            familyId,
            "supported",
            KnowledgeKind.Actor.ToString(),
            resident.GetArrayLength(),
            1,
            mountGraph.GetProperty("exactPedMetadataRegistrationCount").GetInt32(),
            mountGraph.GetProperty("pedMetadataPacks").GetArrayLength(),
            "exact-mounted-ped-metadata-object");
    }

    private static bool IsLocationHierarchy(string semanticId) => semanticId is
        "grid.location.contained-by" or "grid.location.interior-of" or "grid.location.instance-of";

    private static SpatialSourceFamilyEntry ReadSpatialSourceFamily(
        ReadOnlySpan<byte> manifestBytes,
        CanonicalCatalogPackage package)
    {
        using var document = JsonDocument.Parse(manifestBytes.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1)
            throw new InvalidDataException("The GTA spatial source-family manifest must use schema version 1.");
        RequireManifestText(root, "gameId", package.Manifest.GameScope.GameId.Value);
        RequireManifestText(root, "steamBuildId",
            package.Manifest.GameScope.ExactGameVersion?.ExactRepresentation ?? string.Empty);
        var familyId = RequireText(root, "sourceFamilyId");
        if (!string.Equals(familyId,
                "rockstar.gta-v.enhanced.mounted-mlo-interior-graph", StringComparison.Ordinal))
            throw new InvalidDataException("The GTA spatial source-family identity is unsupported.");
        var closure = root.GetProperty("exactBuildClosure");
        var unsupported = root.GetProperty("unsupportedFamilies").EnumerateArray()
            .Select(value => new UnsupportedFamilyEntry(
                RequireText(value, "sourceFamilyId"),
                "unsupported-declared",
                KnowledgeKind.Location.ToString(),
                RequireText(value, "reasonCode"),
                0,
                RequireText(value, "potentialKnowledge")))
            .OrderBy(value => value.SourceFamilyId, StringComparer.Ordinal)
            .ToImmutableArray();
        return new SpatialSourceFamilyEntry(
            familyId,
            "supported",
            KnowledgeKind.Location.ToString(),
            RequireText(root, "mountScope"),
            closure.GetProperty("spatialPackCount").GetInt32(),
            closure.GetProperty("declaredSpatialRpfCount").GetInt32(),
            closure.GetProperty("nestedRpfResolvedCount").GetInt32(),
            closure.GetProperty("ytypArtifactCount").GetInt32(),
            closure.GetProperty("ymapArtifactCount").GetInt32(),
            closure.GetProperty("ymfArtifactCount").GetInt32(),
            closure.GetProperty("mloArchetypeCount").GetInt32(),
            closure.GetProperty("mloRoomCount").GetInt32(),
            closure.GetProperty("mloPortalCount").GetInt32(),
            closure.GetProperty("mloInstanceCount").GetInt32(),
            closure.GetProperty("ymfRelationshipCount").GetInt32(),
            closure.GetProperty("unsupportedOutcomeCount").GetInt32(),
            unsupported);
    }

    private static void RequireManifestText(JsonElement value, string propertyName, string expected)
    {
        var actual = RequireText(value, propertyName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"The GTA source-family manifest {propertyName} does not match the package.");
    }

    private static string RequireText(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The GTA source-family manifest {propertyName} is absent or invalid.");
        return property.GetString()!;
    }

    private static string? OptionalText(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Dictionary<string, int> ReadManifestMemberCounts(JsonElement root)
    {
        if (!root.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The GTA source-family manifest has no containers array.");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var container in containers.EnumerateArray())
        {
            if (container.TryGetProperty("members", out var members))
                foreach (var member in members.EnumerateArray())
                {
                    var family = RequireText(member, "sourceFamilyId");
                    counts[family] = counts.GetValueOrDefault(family) + 1;
                }
            if (container.TryGetProperty("dynamicMembers", out var dynamicMembers))
            {
                var family = RequireText(dynamicMembers, "sourceFamilyId");
                if (!dynamicMembers.TryGetProperty("exactCount", out var count) ||
                    count.ValueKind != JsonValueKind.Number || count.GetInt32() < 1)
                    throw new InvalidDataException("A dynamic GTA source family has no positive exactCount.");
                counts[family] = counts.GetValueOrDefault(family) + count.GetInt32();
            }
        }
        return counts;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CanonicalJsonSha256(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

internal sealed record UnsupportedFamilyEntry(
    string SourceFamilyId,
    string Status,
    string KnowledgeDomain,
    string? ReasonCode,
    int ObservedObjects,
    string? PotentialCoverage);

internal sealed record ActorSourceFamilyEntry(
    string SourceFamilyId,
    string Status,
    string KnowledgeKind,
    int ResidentCandidateArtifacts,
    int EffectiveResidentArtifacts,
    int MountedDlcPedMetadataArtifacts,
    int MountedDlcPacks,
    string SourceGrain);

internal sealed record SpatialSourceFamilyEntry(
    string SourceFamilyId,
    string Status,
    string KnowledgeKind,
    string MountScope,
    int SpatialPacks,
    int DeclaredSpatialRpfs,
    int ResolvedNestedRpfs,
    int YtypArtifacts,
    int YmapArtifacts,
    int YmfArtifacts,
    int MloArchetypes,
    int MloRooms,
    int MloPortals,
    int MloInstances,
    int YmfRelationships,
    int UnsupportedOutcomes,
    ImmutableArray<UnsupportedFamilyEntry> UnsupportedFamilies);

internal sealed record RegistrationMissionQuestSourceLedger
{
    private RegistrationMissionQuestSourceLedger(
        int discoveredSourceObjects,
        ImmutableArray<UnsupportedKnowledgeArtifact> unsupportedArtifacts)
    {
        DiscoveredSourceObjects = discoveredSourceObjects;
        UnsupportedArtifacts = unsupportedArtifacts;
    }

    public int DiscoveredSourceObjects { get; }
    public ImmutableArray<UnsupportedKnowledgeArtifact> UnsupportedArtifacts { get; }

    public static RegistrationMissionQuestSourceLedger Create(
        ImmutableArray<FrozenSourceArtifact> indexedArtifacts,
        SourceDiscoveryResult legacyMissionDiscovery,
        SourceDiscoveryResult activityRegistryDiscovery)
    {
        ArgumentNullException.ThrowIfNull(legacyMissionDiscovery);
        ArgumentNullException.ThrowIfNull(activityRegistryDiscovery);
        if (indexedArtifacts.IsDefaultOrEmpty || indexedArtifacts.Any(value => value is null))
            throw new InvalidDataException("MissionQuest coverage requires a non-empty immutable artifact index.");

        var indexedIds = indexedArtifacts.Select(value => value.Id).ToArray();
        if (indexedIds.Distinct().Count() != indexedIds.Length)
            throw new InvalidDataException("MissionQuest source-index artifacts must be distinct.");

        var unsupported = legacyMissionDiscovery.UnsupportedArtifacts
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.ReasonCode, StringComparer.Ordinal)
            .ToImmutableArray();
        ValidateDiscovery(legacyMissionDiscovery, indexedIds.ToHashSet());
        ValidateDiscovery(activityRegistryDiscovery, indexedIds.ToHashSet());
        var registryAccountedIds = activityRegistryDiscovery.SourceCandidates.SelectMany(value => value.ArtifactIds)
            .Concat(activityRegistryDiscovery.UnsupportedArtifacts.Select(value => value.ArtifactId))
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToArray();
        var expectedIds = indexedIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        if (!registryAccountedIds.SequenceEqual(expectedIds))
            throw new InvalidDataException(
                "The Online activity registry discovery does not account exactly for every indexed UGC artifact.");

        return new RegistrationMissionQuestSourceLedger(indexedIds.Length, unsupported);
    }

    private static void ValidateDiscovery(SourceDiscoveryResult discovery, HashSet<SourceArtifactId> indexedIds)
    {
        var supportedIds = discovery.SourceCandidates.SelectMany(value => value.ArtifactIds).ToArray();
        var unsupported = discovery.UnsupportedArtifacts.ToArray();
        var unsupportedIds = unsupported.Select(value => value.ArtifactId).ToArray();
        if (supportedIds.Distinct().Count() != supportedIds.Length ||
            unsupportedIds.Distinct().Count() != unsupportedIds.Length ||
            supportedIds.Intersect(unsupportedIds).Any() ||
            supportedIds.Concat(unsupportedIds).Any(value => !indexedIds.Contains(value)) ||
            unsupported.Any(value => string.IsNullOrWhiteSpace(value.ReasonCode)))
            throw new InvalidDataException("MissionQuest discovery contains duplicate, overlapping, foreign, or unreasoned artifacts.");
    }
}
