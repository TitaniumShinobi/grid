using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record GtaRegistrationReferenceSource(
    string SourceFamilyId,
    string ProviderSourceId,
    string EndpointTemplateId,
    string ExactEndpointTemplateVersion,
    string Platform,
    string Locale,
    ImmutableArray<string> VisibilityScopes,
    ImmutableArray<string> AuthenticationScopes,
    string SnapshotSchemaId,
    string ReceiptSchemaId,
    ImmutableArray<string> ScopeModes,
    byte[] ApprovedSchemaDescriptorBytes);

internal sealed record GtaRegistrationSourceRegistry(
    string ManifestId,
    int SchemaVersion,
    string DocumentSha256,
    string LocalSourceFamilyManifestPath,
    string ActorSourceFamilyManifestPath,
    string SpatialSourceFamilyManifestPath,
    GtaRegistrationReferenceSource RockstarCloudJobs)
{
    private const string ExpectedLocalManifestDigest =
        "a477091b010d4a80958a77a35247a520cf98b8df4d8b8c27c76c979e8be03338";
    private const string ExpectedActorManifestDigest =
        "63fe8c6a97c011cb70a62e0f88e22bcb1e42fb70fcac86dbb7d450aebc3c50ce";
    private const string ExpectedSpatialManifestDigest =
        "43c1792a9d1d4c252e0a1eee60113bfc55eb35de38ccc719ce6f2e1fbb789cb1";

    public static GtaRegistrationSourceRegistry Load(
        string registryPath,
        string localManifestPath,
        string actorManifestPath,
        string spatialManifestPath)
    {
        registryPath = Path.GetFullPath(registryPath);
        localManifestPath = Path.GetFullPath(localManifestPath);
        actorManifestPath = Path.GetFullPath(actorManifestPath);
        spatialManifestPath = Path.GetFullPath(spatialManifestPath);
        var bytes = File.ReadAllBytes(registryPath);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        var registryVersion = root.GetProperty("schemaVersion").GetInt32();
        var plan2 = registryVersion == 6;
        RequireExactProperties(root,
            new[] { "schemaVersion", "manifestId", "gameId", "steamAppId", "steamBuildId",
                "localSourceFamilyManifest", "actorSourceFamilyManifest", "spatialSourceFamilyManifest",
                "referenceSources" }.Concat(plan2 ? ["routeSourceFamilyManifest", "itemSourceFamilyManifest", "presentationSourceFamilyManifest"] : Array.Empty<string>()).ToArray());
        Require(registryVersion is 5 or 6 &&
                RequireText(root, "manifestId") == "grid.gta-v-enhanced.registration-sources" &&
                RequireText(root, "gameId") == "game.grandtheftautov-enhanced" &&
                RequireText(root, "steamAppId") == "3240220" &&
                RequireText(root, "steamBuildId") == "25261616",
            "GTA registration source-registry identity mismatch.");

        var local = root.GetProperty("localSourceFamilyManifest");
        RequireExactProperties(local, ["manifestId", "schemaVersion", "documentSha256"]);
        Require(RequireText(local, "manifestId") == "grid.gta-v-enhanced.source-families" &&
                local.GetProperty("schemaVersion").GetInt32() == 2 &&
                RequireText(local, "documentSha256") == ExpectedLocalManifestDigest,
            "The registration registry does not pin the approved local source-family manifest.");
        var localIdentity = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity(localManifestPath);
        Require(localIdentity == ("grid.gta-v-enhanced.source-families", 2, ExpectedLocalManifestDigest),
            "The checked-in local source-family manifest does not match the registration registry.");

        var actor = root.GetProperty("actorSourceFamilyManifest");
        RequireExactProperties(actor, ["manifestId", "schemaVersion", "documentSha256"]);
        Require(RequireText(actor, "manifestId") == "grid.gta-v-enhanced.actor-source-families" &&
                actor.GetProperty("schemaVersion").GetInt32() == 1 &&
                RequireText(actor, "documentSha256") == ExpectedActorManifestDigest,
            "The registration registry does not pin the approved Actor source-family manifest.");
        var actorManifestDigest = CanonicalJsonSha256(File.ReadAllBytes(actorManifestPath));
        Require(string.Equals(actorManifestDigest, ExpectedActorManifestDigest, StringComparison.Ordinal),
            "The checked-in Actor source-family manifest does not match the registration registry.");

        var spatial = root.GetProperty("spatialSourceFamilyManifest");
        RequireExactProperties(spatial, ["manifestId", "schemaVersion", "documentSha256"]);
        Require(RequireText(spatial, "manifestId") == "grid.gta-v-enhanced.spatial-source-families" &&
                spatial.GetProperty("schemaVersion").GetInt32() == 1 &&
                RequireText(spatial, "documentSha256") == ExpectedSpatialManifestDigest,
            "The registration registry does not pin the approved spatial source-family manifest.");
        var spatialManifestDigest = CanonicalJsonSha256(File.ReadAllBytes(spatialManifestPath));
        Require(string.Equals(spatialManifestDigest, ExpectedSpatialManifestDigest, StringComparison.Ordinal),
            "The checked-in spatial source-family manifest does not match the registration registry.");

        if (plan2)
            foreach (var family in new[] { "route", "item", "presentation" })
            {
                var pin = root.GetProperty(family + "SourceFamilyManifest");
                RequireExactProperties(pin, ["manifestId", "schemaVersion", "documentSha256"]);
                var path = Path.Combine(Path.GetDirectoryName(registryPath)!, $"gta_v_enhanced_{family}_source_families.v1.json");
                using var familyDocument = JsonDocument.Parse(File.ReadAllBytes(path));
                Require(pin.GetProperty("schemaVersion").GetInt32() == 1 &&
                        RequireText(pin, "manifestId") == RequireText(familyDocument.RootElement, "manifestId") &&
                        RequireText(pin, "documentSha256") == CanonicalJsonSha256(File.ReadAllBytes(path), relaxed: true),
                    $"Plan 2 {family} source manifest does not match its registered digest.");
            }

        var references = root.GetProperty("referenceSources");
        Require(references.ValueKind == JsonValueKind.Array && references.GetArrayLength() == 1,
            "The GTA registration registry must declare exactly one bounded reference source.");
        var reference = references[0];
        RequireExactProperties(reference,
            ["sourceFamilyId", "status", "knowledgeKinds", "providerSourceId", "endpointTemplateId",
                "exactEndpointTemplateVersion", "platform", "locale", "visibilityScopes", "authenticationScopes",
                "snapshotSchemaId", "receiptSchemaId", "scopeModes",
                "schemaDescriptor"]);
        Require(RequireText(reference, "sourceFamilyId") ==
                    "rockstar.gta-v.enhanced.rockstar-cloud-job-registry" &&
                RequireText(reference, "status") == "supported-optional" &&
                ReadOrderedTextArray(reference, "knowledgeKinds").SequenceEqual(["MissionQuest"]) &&
                RequireText(reference, "providerSourceId") ==
                    "rockstar.gta-v.enhanced.rockstar-cloud-job-registry" &&
                RequireText(reference, "endpointTemplateId") == "rockstar.gta-online.job-header-registry" &&
                RequireText(reference, "exactEndpointTemplateVersion") == "1" &&
                RequireText(reference, "platform") == "pc" &&
                RequireText(reference, "locale") == "en-US" &&
                RequireText(reference, "snapshotSchemaId") == "grid.gta-v.rockstar-cloud-job-snapshot" &&
                RequireText(reference, "receiptSchemaId") == "grid.gta-v.rockstar-cloud-job-snapshot-receipt",
            "The Rockstar cloud-job reference-source declaration is not approved.");
        var scopeModes = ReadOrderedTextArray(reference, "scopeModes");
        var visibilityScopes = ReadOrderedTextArray(reference, "visibilityScopes");
        var authenticationScopes = ReadOrderedTextArray(reference, "authenticationScopes");
        Require(scopeModes.SequenceEqual(["exact-target-set", "global-registry"]),
            "The Rockstar cloud-job scope modes are not exact and approved.");
        Require(visibilityScopes.SequenceEqual(["global-authorized-nonpersonalized", "global-public"]) &&
                authenticationScopes.SequenceEqual(["none", "nonpersonalized-provider-session"]),
            "The Rockstar cloud-job visibility or authentication scope is not approved.");

        var descriptor = reference.GetProperty("schemaDescriptor");
        RequireExactProperties(descriptor,
            ["schemaVersion", "schemaId", "formatId", "exactFormatVersion", "objectsPointer",
                "providerObjectIdentityPointer", "nativeRevisionPointer", "fmnmPointer", "titlePointer",
                "activityFamilyIdentityPointer", "activityFamilyLabelPointer", "onlineScopePointer"]);
        Require(descriptor.GetProperty("schemaVersion").GetInt32() == 1 &&
                RequireText(descriptor, "schemaId") == "grid.gta-v.rockstar-cloud-job-header.schema" &&
                RequireText(descriptor, "formatId") == "rockstar.gta-v.rockstar-cloud-job-snapshot-json" &&
                RequireText(descriptor, "exactFormatVersion") == "1",
            "The Rockstar cloud-job schema descriptor identity is not approved.");
        var pointers = new[]
        {
            "objectsPointer", "providerObjectIdentityPointer", "nativeRevisionPointer", "fmnmPointer",
            "titlePointer", "activityFamilyIdentityPointer", "activityFamilyLabelPointer", "onlineScopePointer",
        }.Select(name => RequireText(descriptor, name)).ToArray();
        Require(pointers.All(value => value.StartsWith("/", StringComparison.Ordinal)) &&
                pointers.Distinct(StringComparer.Ordinal).Count() == pointers.Length,
            "The Rockstar cloud-job schema pointers must be distinct absolute RFC 6901 pointers.");

        return new GtaRegistrationSourceRegistry(
            "grid.gta-v-enhanced.registration-sources",
            registryVersion,
            CanonicalJsonSha256(bytes),
            localManifestPath,
            actorManifestPath,
            spatialManifestPath,
            new GtaRegistrationReferenceSource(
                RequireText(reference, "sourceFamilyId"),
                RequireText(reference, "providerSourceId"),
                RequireText(reference, "endpointTemplateId"),
                RequireText(reference, "exactEndpointTemplateVersion"),
                RequireText(reference, "platform"),
                RequireText(reference, "locale"),
                visibilityScopes,
                authenticationScopes,
                RequireText(reference, "snapshotSchemaId"),
                RequireText(reference, "receiptSchemaId"),
                scopeModes,
                Encoding.UTF8.GetBytes(descriptor.GetRawText())));
    }

    private static string CanonicalJsonSha256(ReadOnlySpan<byte> bytes, bool relaxed = false)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false,
            Encoder = relaxed ? System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping : null }))
            WriteCanonical(writer, document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
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

    private static ImmutableArray<string> ReadOrderedTextArray(JsonElement parent, string name)
    {
        var property = parent.GetProperty(name);
        Require(property.ValueKind == JsonValueKind.Array, $"{name} must be an array.");
        var values = property.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidDataException($"{name} contains an invalid value."))
            .ToImmutableArray();
        Require(!values.IsEmpty &&
                values.SequenceEqual(values.OrderBy(value => value, StringComparer.Ordinal)) &&
                values.Distinct(StringComparer.Ordinal).Count() == values.Length,
            $"{name} must be nonempty, unique, and ordinally sorted.");
        return values;
    }

    private static string RequireText(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The GTA registration registry {propertyName} is absent or invalid.");
        return property.GetString()!;
    }

    private static void RequireExactProperties(JsonElement value, IReadOnlyCollection<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A GTA registration registry value is not an object.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any() || expected.Except(actual, StringComparer.Ordinal).Any())
            throw new InvalidDataException("A GTA registration registry object has missing, duplicate, or unsupported fields.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
