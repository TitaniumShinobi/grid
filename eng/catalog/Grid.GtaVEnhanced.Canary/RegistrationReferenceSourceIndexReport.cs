using System.Security.Cryptography;
using System.Text.Json;
using Grid.GtaV.Knowledge;

internal static class RegistrationReferenceSourceIndexReport
{
    public static object WithPresentation(object? cloudReport, GtaVPresentationCorpusIndex index)
    {
        var body = new
        {
            schemaVersion = 2,
            cloud = cloudReport,
            pinnedPresentationReferences = index.Artifacts.Values
                .Where(value => value.DeclaredFormat.FormatId == GtaVPresentationCorpusIndex.ReferenceFormatId)
                .OrderBy(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .Select(value => new
                {
                    sourceCoordinate = value.SourceCoordinate.ExactRepresentation,
                    contentSha256 = value.Digest.HexValue,
                    artifactId = value.Id.Value,
                    provenance = "REFERENCE_VERIFIED",
                    observedAtUtc = value.ObservedAtUtc,
                }).ToArray(),
        };
        var report = JsonSerializer.SerializeToNode(body, CompactJson)!.AsObject();
        report["contentSha256"] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, CompactJson)));
        return report;
    }

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public static object Create(
        GtaVRockstarCloudSnapshotBundle bundle,
        GtaRegistrationSourceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(registry);
        if (bundle.Index.PhysicalResponseParseCount != bundle.Responses.Length)
            throw new InvalidDataException("Every frozen cloud response must be parsed exactly once into the immutable index.");
        if (!string.Equals(bundle.ProviderSource.NativeIdentity.ExactRepresentation,
                registry.RockstarCloudJobs.ProviderSourceId, StringComparison.Ordinal) ||
            !string.Equals(bundle.EndpointTemplateId,
                registry.RockstarCloudJobs.EndpointTemplateId, StringComparison.Ordinal) ||
            !string.Equals(bundle.EndpointTemplateVersion,
                registry.RockstarCloudJobs.ExactEndpointTemplateVersion, StringComparison.Ordinal) ||
            !string.Equals(bundle.Platform, registry.RockstarCloudJobs.Platform, StringComparison.Ordinal) ||
            !string.Equals(bundle.Locale, registry.RockstarCloudJobs.Locale, StringComparison.Ordinal) ||
            !registry.RockstarCloudJobs.VisibilityScopes.Contains(bundle.VisibilityScope, StringComparer.Ordinal) ||
            !registry.RockstarCloudJobs.AuthenticationScopes.Contains(bundle.AuthenticationScope, StringComparer.Ordinal) ||
            bundle.SchemaDescriptorDigest !=
                Grid.Core.Models.ContentDigest.ComputeSha256(
                    CanonicalJson(registry.RockstarCloudJobs.ApprovedSchemaDescriptorBytes)))
            throw new InvalidDataException("The cloud snapshot does not match the approved registration source declaration.");

        var body = new
        {
            schemaVersion = 1,
            gameId = "game.grandtheftautov-enhanced",
            exactGameVersion = "25261616",
            registrationSourceRegistrySha256 = registry.DocumentSha256,
            sourceFamilyId = registry.RockstarCloudJobs.SourceFamilyId,
            providerCatalogSourceId = bundle.ProviderSource.Id.Value,
            responseArtifactId = bundle.EnvelopeArtifact.Id.Value,
            responseContentSha256 = bundle.EnvelopeArtifact.Digest.HexValue,
            schemaDescriptorSha256 = bundle.SchemaDescriptorDigest.HexValue,
            receiptDocumentSha256 = bundle.ReceiptDocumentDigest.HexValue,
            requestSetSha256 = bundle.RequestSetDigest.HexValue,
            acquisitionReceiptId = bundle.AcquisitionReceipt.Id.Value,
            captureMethod = new
            {
                methodId = bundle.AcquisitionReceipt.AcquisitionMethod.MethodId,
                exactMethodVersion = bundle.AcquisitionReceipt.AcquisitionMethod.ExactMethodVersion,
                toolId = bundle.AcquisitionReceipt.AcquisitionMethod.ToolId,
                exactToolVersion = bundle.AcquisitionReceipt.AcquisitionMethod.ExactToolVersion,
                toolArtifactSha256 = bundle.AcquisitionReceipt.AcquisitionMethod.ToolArtifactDigest.HexValue,
            },
            scopeMode = bundle.ScopeMode == GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry
                ? "global-registry"
                : "exact-target-set",
            capturedAtUtc = bundle.CapturedAtUtc,
            providerRegistryRevision = bundle.ProviderRegistryRevision.ExactRepresentation,
            bundle.Platform,
            bundle.Locale,
            bundle.VisibilityScope,
            bundle.AuthenticationScope,
            endpointTemplateId = bundle.EndpointTemplateId,
            exactEndpointTemplateVersion = bundle.EndpointTemplateVersion,
            responses = new
            {
                count = bundle.Responses.Length,
                successful = bundle.Responses.Count(value => value.StatusCode is >= 200 and <= 299),
                terminal = bundle.Responses.Count(value => value.Terminal),
                providerRevisions = bundle.Responses.Select(value => value.ProviderRevision.ExactRepresentation)
                    .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            },
            providerObjects = new
            {
                count = bundle.Index.Objects.Length,
                distinctExactFmnm = bundle.Index.DistinctExactFmnmCount,
                physicalResponseParseCount = bundle.Index.PhysicalResponseParseCount,
            },
        };
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, CompactJson);
        return new
        {
            body.schemaVersion,
            contentSha256 = Convert.ToHexStringLower(SHA256.HashData(bodyBytes)),
            body.gameId,
            body.exactGameVersion,
            body.registrationSourceRegistrySha256,
            body.sourceFamilyId,
            body.providerCatalogSourceId,
            body.responseArtifactId,
            body.responseContentSha256,
            body.schemaDescriptorSha256,
            body.receiptDocumentSha256,
            body.requestSetSha256,
            body.acquisitionReceiptId,
            body.captureMethod,
            body.scopeMode,
            body.capturedAtUtc,
            body.providerRegistryRevision,
            body.Platform,
            body.Locale,
            body.VisibilityScope,
            body.AuthenticationScope,
            body.endpointTemplateId,
            body.exactEndpointTemplateVersion,
            body.responses,
            body.providerObjects,
        };
    }

    private static byte[] CanonicalJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return stream.ToArray();
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
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("The approved cloud schema descriptor contains unsupported JSON.");
        }
    }
}
