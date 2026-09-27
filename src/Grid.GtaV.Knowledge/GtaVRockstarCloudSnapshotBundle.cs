using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public enum GtaVRockstarCloudSnapshotScopeMode
{
    GlobalRegistry = 0,
    ExactTargetSet = 1,
}

public sealed record GtaVRockstarCloudSnapshotResponse(
    int Ordinal,
    string RequestCoordinate,
    string? RequestedFmnm,
    int StatusCode,
    string? Cursor,
    string? NextCursor,
    SourceNativeVersion ProviderRevision,
    string? ETag,
    bool Terminal,
    long RawBodyByteLength,
    ContentDigest RawBodyDigest,
    ImmutableArray<byte> RawBodyBytes);

public sealed record GtaVRockstarCloudJobObject(
    SourceNativeIdentifier ProviderObjectIdentity,
    SourceNativeVersion? NativeRevisionIdentity,
    string? ExactFmnm,
    string? ExactTitle,
    string? ExactActivityFamilyIdentity,
    string? ExactActivityFamilyLabel,
    bool? IsOnline,
    string ObjectFieldPath,
    string ProviderObjectIdentityFieldPath,
    string? NativeRevisionFieldPath,
    string? FmnmFieldPath,
    string? TitleFieldPath,
    string? ActivityFamilyIdentityFieldPath,
    string? ActivityFamilyLabelFieldPath,
    string? OnlineScopeFieldPath,
    int ResponseOrdinal,
    string RequestCoordinate);

/// <summary>
/// Immutable, response-artifact-bound index of provider objects. It performs exact ordinal
/// lookups only; it deliberately exposes no fuzzy, normalized, or hash-derived lookup.
/// </summary>
public sealed class GtaVRockstarCloudJobSourceIndex
{
    private readonly ImmutableDictionary<string, ImmutableArray<GtaVRockstarCloudJobObject>> _byFmnm;

    internal GtaVRockstarCloudJobSourceIndex(
        ImmutableArray<GtaVRockstarCloudJobObject> objects,
        int physicalResponseParseCount,
        int equivalentDuplicateCount)
    {
        if (objects.IsDefault || objects.Any(value => value is null))
            throw new ArgumentException("Cloud job objects must be initialized and non-null.", nameof(objects));
        Objects = objects
            .OrderBy(value => value.ProviderObjectIdentity.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.NativeRevisionIdentity?.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.ObjectFieldPath, StringComparer.Ordinal)
            .ToImmutableArray();
        PhysicalResponseParseCount = physicalResponseParseCount;
        if (equivalentDuplicateCount < 0)
            throw new ArgumentOutOfRangeException(nameof(equivalentDuplicateCount));
        EquivalentDuplicateCount = equivalentDuplicateCount;
        _byFmnm = Objects
            .Where(value => value.ExactFmnm is not null)
            .GroupBy(value => value.ExactFmnm!, StringComparer.Ordinal)
            .ToImmutableDictionary(
                value => value.Key,
                value => value.ToImmutableArray(),
                StringComparer.Ordinal);
    }

    public ImmutableArray<GtaVRockstarCloudJobObject> Objects { get; }
    public int PhysicalResponseParseCount { get; }
    public int EquivalentDuplicateCount { get; }
    public int DistinctExactFmnmCount => _byFmnm.Count;

    public ImmutableArray<GtaVRockstarCloudJobObject> FindExactFmnm(string exactFmnm)
    {
        ArgumentNullException.ThrowIfNull(exactFmnm);
        return _byFmnm.TryGetValue(exactFmnm, out var values) ? values : [];
    }
}

public sealed class GtaVRockstarCloudSnapshotBundle
{
    internal GtaVRockstarCloudSnapshotBundle(
        CatalogSourceRecord providerSource,
        FrozenSourceArtifact frozenEnvelopeArtifact,
        SourceArtifactRecord envelopeArtifact,
        SourceAcquisitionReceipt acquisitionReceipt,
        SourceArtifactAcquisitionBinding acquisitionBinding,
        ContentDigest schemaDescriptorDigest,
        ContentDigest receiptDocumentDigest,
        ContentDigest requestSetDigest,
        GtaVRockstarCloudSnapshotScopeMode scopeMode,
        DateTimeOffset capturedAtUtc,
        SourceNativeVersion providerRegistryRevision,
        string platform,
        string locale,
        string visibilityScope,
        string authenticationScope,
        string endpointTemplateId,
        string endpointTemplateVersion,
        ImmutableArray<GtaVRockstarCloudSnapshotResponse> responses,
        GtaVRockstarCloudJobSourceIndex index)
    {
        ProviderSource = providerSource;
        FrozenEnvelopeArtifact = frozenEnvelopeArtifact;
        EnvelopeArtifact = envelopeArtifact;
        AcquisitionReceipt = acquisitionReceipt;
        AcquisitionBinding = acquisitionBinding;
        SchemaDescriptorDigest = schemaDescriptorDigest;
        ReceiptDocumentDigest = receiptDocumentDigest;
        RequestSetDigest = requestSetDigest;
        ScopeMode = scopeMode;
        CapturedAtUtc = capturedAtUtc;
        ProviderRegistryRevision = providerRegistryRevision;
        Platform = platform;
        Locale = locale;
        VisibilityScope = visibilityScope;
        AuthenticationScope = authenticationScope;
        EndpointTemplateId = endpointTemplateId;
        EndpointTemplateVersion = endpointTemplateVersion;
        Responses = responses;
        Index = index;
    }

    public CatalogSourceRecord ProviderSource { get; }
    public FrozenSourceArtifact FrozenEnvelopeArtifact { get; }
    public SourceArtifactRecord EnvelopeArtifact { get; }
    public SourceAcquisitionReceipt AcquisitionReceipt { get; }
    public SourceArtifactAcquisitionBinding AcquisitionBinding { get; }
    public ContentDigest SchemaDescriptorDigest { get; }
    public ContentDigest ReceiptDocumentDigest { get; }
    public ContentDigest RequestSetDigest { get; }
    public GtaVRockstarCloudSnapshotScopeMode ScopeMode { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public SourceNativeVersion ProviderRegistryRevision { get; }
    public string Platform { get; }
    public string Locale { get; }
    public string VisibilityScope { get; }
    public string AuthenticationScope { get; }
    public string EndpointTemplateId { get; }
    public string EndpointTemplateVersion { get; }
    public ImmutableArray<GtaVRockstarCloudSnapshotResponse> Responses { get; }
    public GtaVRockstarCloudJobSourceIndex Index { get; }
}

/// <summary>
/// Verifies an externally acquired, credential-free Rockstar cloud snapshot bundle. This is a
/// preproduction registration boundary; it performs no network access and accepts no secrets.
/// </summary>
public static class GtaVRockstarCloudSnapshotBundleLoader
{
    public const string EnvelopeFileName = "rockstar-cloud-job-snapshot.v1.json";
    public const string ReceiptFileName = "rockstar-cloud-job-snapshot-receipt.v1.json";
    public const string SchemaFileName = "rockstar-cloud-job-schema.v1.json";
    public const string SchemaId = "grid.gta-v.rockstar-cloud-job-header.schema";
    public const string SnapshotSchemaId = "grid.gta-v.rockstar-cloud-job-snapshot";
    public const string ReceiptSchemaId = "grid.gta-v.rockstar-cloud-job-snapshot-receipt";
    public const string SourceFamilyId = "rockstar.gta-v.enhanced.rockstar-cloud-job-registry";
    public const string SnapshotFormatId = "rockstar.gta-v.rockstar-cloud-job-snapshot-json";
    public const string SnapshotFormatVersion = "1";
    public const string ExpectedPlatform = "pc";
    public const string ExpectedLocale = "en-US";

    private const string GameIdValue = "game.grandtheftautov-enhanced";
    private const string SteamAppId = "3240220";
    private const string SteamBuildId = "25261616";
    private const string ExactComparison = "grid.exact-utf8";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ForbiddenPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "accessToken", "authorization", "bearer", "cookie", "cookies",
        "credential", "credentials", "password", "profileId", "accountId", "rockstarId",
        "refresh_token", "refreshToken", "sessionId", "email",
    };

    public static KnowledgeFormatCoordinate SnapshotFormat =>
        new(SnapshotFormatId, SnapshotFormatVersion);

    public static GtaVRockstarCloudSnapshotBundle Load(
        string bundleDirectory,
        byte[] approvedSchemaDescriptorBytes,
        ImmutableArray<string> expectedFmnmValues)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory))
            throw new ArgumentException("Bundle directory is required.", nameof(bundleDirectory));
        ArgumentNullException.ThrowIfNull(approvedSchemaDescriptorBytes);
        if (expectedFmnmValues.IsDefault || expectedFmnmValues.Any(value => value is null))
            throw new ArgumentException("Expected fmnm values must be initialized and non-null.", nameof(expectedFmnmValues));

        var directory = Path.GetFullPath(bundleDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Rockstar cloud snapshot bundle is absent: {directory}");
        var presentFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var expectedFiles = new[] { EnvelopeFileName, ReceiptFileName, SchemaFileName }
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!presentFiles.SequenceEqual(expectedFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Cloud snapshot bundle must contain exactly the three registered documents.");

        var envelopeBytes = ReadStrict(Path.Combine(directory, EnvelopeFileName));
        var receiptBytes = ReadStrict(Path.Combine(directory, ReceiptFileName));
        var schemaBytes = ReadStrict(Path.Combine(directory, SchemaFileName));
        var approvedCanonicalSchema = CanonicalJson(approvedSchemaDescriptorBytes, "approved schema descriptor");
        var bundleCanonicalSchema = CanonicalJson(schemaBytes, "bundle schema descriptor");
        if (!schemaBytes.AsSpan().SequenceEqual(bundleCanonicalSchema))
            throw new InvalidDataException("Bundle schema descriptor must use canonical compact JSON bytes.");
        if (!approvedCanonicalSchema.AsSpan().SequenceEqual(bundleCanonicalSchema))
            throw new InvalidDataException("Bundle schema descriptor does not match the approved registration manifest semantics.");

        using var schemaDocument = ParseStrict(bundleCanonicalSchema, "schema descriptor");
        var schema = ParseSchema(schemaDocument.RootElement);
        using var envelopeDocument = ParseCanonical(envelopeBytes, "snapshot envelope");
        using var receiptDocument = ParseCanonical(receiptBytes, "snapshot receipt");
        var envelope = envelopeDocument.RootElement;
        var receipt = receiptDocument.RootElement;
        RejectForbiddenProperties(envelope, "snapshot envelope");
        RejectForbiddenProperties(receipt, "snapshot receipt");

        var expectedFmnm = NormalizeExpectedFmnm(expectedFmnmValues);
        var expectedRequestSetDigest = Digest(CanonicalStringArray(expectedFmnm));
        var metadata = ParseAndMatchMetadata(envelope, receipt, expectedRequestSetDigest);
        var envelopeDigest = Digest(envelopeBytes);
        var schemaDigest = Digest(bundleCanonicalSchema);
        RequireLong(receipt, "envelopeByteLength", envelopeBytes.LongLength);
        RequireDigest(receipt, "envelopeSha256", envelopeDigest);
        RequireLong(receipt, "schemaDescriptorByteLength", bundleCanonicalSchema.LongLength);
        RequireDigest(receipt, "schemaDescriptorSha256", schemaDigest);

        var responseValues = ParseResponses(envelope, receipt, metadata, expectedFmnm);
        var index = CreateIndex(responseValues, schema);
        ValidateTargetClosure(metadata.ScopeMode, expectedFmnm, responseValues, index.Objects);

        var artifactId = SourceArtifactId.DeriveV1(envelopeDigest);
        var sourceCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.games.cloud.snapshot",
            "RockstarCloudJobSnapshot",
            $"{SourceFamilyId}/{metadata.ProviderRegistryRevision}",
            ExactComparison,
            1);
        var frozen = new FrozenSourceArtifact(
            artifactId,
            envelopeDigest,
            sourceCoordinate,
            SnapshotFormat,
            envelopeBytes.ToImmutableArray(),
            metadata.CapturedAtUtc);
        var providerIdentity = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.games.cloud",
            "OfficialJobRegistry",
            SourceFamilyId,
            ExactComparison,
            1);
        var providerSource = new CatalogSourceRecord(
            CatalogSourceId.DeriveV1(KnowledgeSourceKind.OfficialProvider, providerIdentity),
            KnowledgeSourceKind.OfficialProvider,
            providerIdentity);
        var member = new SourceAcquisitionMember(
            sourceCoordinate,
            envelopeBytes.LongLength,
            envelopeDigest,
            artifactId);
        var method = ParseAcquisitionMethod(receipt);
        var appIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam", "Application", SteamAppId, "valve.steam.app-id.exact-utf8", 1);
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id", SteamBuildId, "valve.steam.build-id.exact-utf8", 1);
        var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.games.cloud.snapshot",
            "ImmutableSnapshotEnvelope",
            $"{SnapshotSchemaId}/v1",
            ExactComparison,
            1);
        var receiptId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            appIdentity,
            gameVersion,
            containerCoordinate,
            envelopeBytes.LongLength,
            envelopeDigest,
            method,
            [member]);
        var acquisitionReceipt = new SourceAcquisitionReceipt(
            receiptId,
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            appIdentity,
            gameVersion,
            containerCoordinate,
            envelopeBytes.LongLength,
            envelopeDigest,
            method,
            [member]);
        var acquisitionBinding = new SourceArtifactAcquisitionBinding(
            artifactId,
            receiptId,
            sourceCoordinate,
            envelopeBytes.LongLength,
            envelopeDigest);

        return new GtaVRockstarCloudSnapshotBundle(
            providerSource,
            frozen,
            new SourceArtifactRecord(artifactId, envelopeDigest),
            acquisitionReceipt,
            acquisitionBinding,
            schemaDigest,
            Digest(receiptBytes),
            expectedRequestSetDigest,
            metadata.ScopeMode,
            metadata.CapturedAtUtc,
            SourceNativeVersion.FromExactUtf8(
                "rockstar.games.cloud.job-registry.revision",
                metadata.ProviderRegistryRevision,
                ExactComparison,
                1),
            metadata.Platform,
            metadata.Locale,
            metadata.VisibilityScope,
            metadata.AuthenticationScope,
            metadata.EndpointTemplateId,
            metadata.EndpointTemplateVersion,
            responseValues,
            index);
    }

    private static Descriptor ParseSchema(JsonElement root)
    {
        RequireExactProperties(root,
            ["schemaVersion", "schemaId", "formatId", "exactFormatVersion", "objectsPointer",
                "providerObjectIdentityPointer", "nativeRevisionPointer", "fmnmPointer", "titlePointer",
                "activityFamilyIdentityPointer", "activityFamilyLabelPointer", "onlineScopePointer"]);
        RequireInt(root, "schemaVersion", 1);
        RequireText(root, "schemaId", SchemaId);
        RequireText(root, "formatId", SnapshotFormatId);
        RequireText(root, "exactFormatVersion", SnapshotFormatVersion);
        var pointers = new[]
        {
            ReadPointer(root, "objectsPointer"),
            ReadPointer(root, "providerObjectIdentityPointer"),
            ReadPointer(root, "nativeRevisionPointer"),
            ReadPointer(root, "fmnmPointer"),
            ReadPointer(root, "titlePointer"),
            ReadPointer(root, "activityFamilyIdentityPointer"),
            ReadPointer(root, "activityFamilyLabelPointer"),
            ReadPointer(root, "onlineScopePointer"),
        };
        if (pointers.Distinct(StringComparer.Ordinal).Count() != pointers.Length)
            throw new InvalidDataException("Cloud snapshot schema pointers must be distinct.");
        return new Descriptor(pointers[0], pointers[1], pointers[2], pointers[3], pointers[4], pointers[5], pointers[6], pointers[7]);
    }

    private static Metadata ParseAndMatchMetadata(
        JsonElement envelope,
        JsonElement receipt,
        ContentDigest expectedRequestSetDigest)
    {
        RequireExactProperties(envelope,
            ["schemaVersion", "schemaId", "sourceFamilyId", "gameId", "steamAppId", "steamBuildId",
                "platform", "locale", "visibilityScope", "authenticationScope", "endpointTemplateId",
                "endpointTemplateVersion", "scopeMode", "capturedAtUtc", "providerRegistryRevision",
                "requestSetSha256", "responses"]);
        RequireExactProperties(receipt,
            ["schemaVersion", "schemaId", "sourceFamilyId", "gameId", "steamAppId", "steamBuildId",
                "platform", "locale", "visibilityScope", "authenticationScope", "endpointTemplateId",
                "endpointTemplateVersion", "scopeMode", "capturedAtUtc", "providerRegistryRevision",
                "requestSetSha256", "responseCount", "envelopeByteLength", "envelopeSha256",
                "schemaDescriptorByteLength", "schemaDescriptorSha256", "captureTool", "responses"]);
        RequireInt(envelope, "schemaVersion", 1);
        RequireInt(receipt, "schemaVersion", 1);
        RequireText(envelope, "schemaId", SnapshotSchemaId);
        RequireText(receipt, "schemaId", ReceiptSchemaId);
        var matchingFields = new[] { "sourceFamilyId", "gameId", "steamAppId", "steamBuildId", "platform", "locale",
            "visibilityScope", "authenticationScope", "endpointTemplateId", "endpointTemplateVersion", "scopeMode",
            "capturedAtUtc", "providerRegistryRevision", "requestSetSha256" };
        foreach (var field in matchingFields)
        {
            var left = RequireStrictText(envelope, field);
            var right = RequireStrictText(receipt, field);
            if (!string.Equals(left, right, StringComparison.Ordinal))
                throw new InvalidDataException($"Snapshot receipt metadata disagrees at {field}.");
        }
        RequireText(envelope, "sourceFamilyId", SourceFamilyId);
        RequireText(envelope, "gameId", GameIdValue);
        RequireText(envelope, "steamAppId", SteamAppId);
        RequireText(envelope, "steamBuildId", SteamBuildId);
        var platform = RequireStrictText(envelope, "platform");
        var locale = RequireStrictText(envelope, "locale");
        if (!string.Equals(platform, ExpectedPlatform, StringComparison.Ordinal) ||
            !string.Equals(locale, ExpectedLocale, StringComparison.Ordinal))
            throw new InvalidDataException("Cloud snapshot platform or locale is outside the approved source family.");
        var visibility = RequireStrictText(envelope, "visibilityScope");
        var authentication = RequireStrictText(envelope, "authenticationScope");
        if (visibility is not ("global-public" or "global-authorized-nonpersonalized") ||
            authentication is not ("none" or "nonpersonalized-provider-session"))
            throw new InvalidDataException("Personalized, favorites, entitlement-limited, or unknown provider scope is forbidden.");
        var modeText = RequireStrictText(envelope, "scopeMode");
        var mode = modeText switch
        {
            "global-registry" => GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry,
            "exact-target-set" => GtaVRockstarCloudSnapshotScopeMode.ExactTargetSet,
            _ => throw new InvalidDataException("Unsupported cloud snapshot scope mode."),
        };
        var capturedText = RequireStrictText(envelope, "capturedAtUtc");
        if (!capturedText.EndsWith('Z') || !DateTimeOffset.TryParseExact(
                capturedText,
                new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var capturedAt) || capturedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Snapshot capture time must be exact UTC text ending in Z.");
        RequireDigest(envelope, "requestSetSha256", expectedRequestSetDigest);
        return new Metadata(
            mode,
            capturedAt,
            ValidateProviderOpaqueValue(
                RequireStrictText(envelope, "providerRegistryRevision"),
                "provider registry revision")!,
            platform,
            locale,
            visibility,
            authentication,
            ValidateOpaqueCoordinate(RequireStrictText(envelope, "endpointTemplateId"), "endpoint template ID"),
            ValidateOpaqueCoordinate(RequireStrictText(envelope, "endpointTemplateVersion"), "endpoint template version"));
    }

    private static ImmutableArray<GtaVRockstarCloudSnapshotResponse> ParseResponses(
        JsonElement envelope,
        JsonElement receipt,
        Metadata metadata,
        ImmutableArray<string> expectedFmnm)
    {
        var envelopeResponses = RequireArray(envelope, "responses").EnumerateArray().ToArray();
        var receiptResponses = RequireArray(receipt, "responses").EnumerateArray().ToArray();
        RequireLong(receipt, "responseCount", envelopeResponses.Length);
        if (envelopeResponses.Length == 0 || receiptResponses.Length != envelopeResponses.Length)
            throw new InvalidDataException("Snapshot response closure is empty or inconsistent.");
        var result = ImmutableArray.CreateBuilder<GtaVRockstarCloudSnapshotResponse>(envelopeResponses.Length);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < envelopeResponses.Length; index++)
        {
            var value = envelopeResponses[index];
            var receiptValue = receiptResponses[index];
            var required = new[] { "ordinal", "requestCoordinate", "requestedFmnm", "statusCode", "cursor", "nextCursor",
                "providerRevision", "etag", "terminal", "rawBodyByteLength", "rawBodySha256" };
            RequireExactProperties(value, required.Append("rawBodyBase64").ToArray());
            RequireExactProperties(receiptValue, required);
            foreach (var property in required)
                if (!JsonElement.DeepEquals(value.GetProperty(property), receiptValue.GetProperty(property)))
                    throw new InvalidDataException($"Receipt response metadata disagrees at response {index}, field {property}.");
            RequireInt(value, "ordinal", index);
            var status = RequireInteger(value, "statusCode");
            if (status is < 200 or > 299)
                throw new InvalidDataException("Cloud snapshot includes a non-success response.");
            var requestCoordinate = ValidateOpaqueCoordinate(
                RequireStrictText(value, "requestCoordinate"), "request coordinate");
            var requestedFmnm = OptionalStrictText(value, "requestedFmnm");
            var cursor = ValidateProviderOpaqueValue(
                OptionalStrictText(value, "cursor"), "response cursor");
            var nextCursor = ValidateProviderOpaqueValue(
                OptionalStrictText(value, "nextCursor"), "response next cursor");
            if (cursor is not null && !cursors.Add(cursor))
                throw new InvalidDataException("Cloud snapshot contains a cursor loop or duplicate cursor.");
            var providerRevision = ValidateProviderOpaqueValue(
                RequireStrictText(value, "providerRevision"), "response provider revision")!;
            if (!string.Equals(providerRevision, metadata.ProviderRegistryRevision, StringComparison.Ordinal))
                throw new InvalidDataException("Provider revision changed within the immutable snapshot.");
            var terminal = RequireBoolean(value, "terminal");
            var rawText = RequireStrictText(value, "rawBodyBase64");
            byte[] rawBytes;
            try { rawBytes = Convert.FromBase64String(rawText); }
            catch (FormatException exception) { throw new InvalidDataException("Response body is not canonical base64.", exception); }
            if (!string.Equals(Convert.ToBase64String(rawBytes), rawText, StringComparison.Ordinal))
                throw new InvalidDataException("Response body base64 is noncanonical.");
            RequireLong(value, "rawBodyByteLength", rawBytes.LongLength);
            var rawDigest = Digest(rawBytes);
            RequireDigest(value, "rawBodySha256", rawDigest);
            using var body = ParseStrict(rawBytes, $"response body {index}");
            RejectForbiddenProperties(body.RootElement, $"response body {index}");
            result.Add(new GtaVRockstarCloudSnapshotResponse(
                index,
                requestCoordinate,
                requestedFmnm,
                status,
                cursor,
                nextCursor,
                SourceNativeVersion.FromExactUtf8(
                    "rockstar.games.cloud.job-registry.revision", providerRevision, ExactComparison, 1),
                ValidateProviderOpaqueValue(OptionalStrictText(value, "etag"), "response ETag"),
                terminal,
                rawBytes.LongLength,
                rawDigest,
                rawBytes.ToImmutableArray()));
        }
        var responses = result.ToImmutable();
        if (metadata.ScopeMode == GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry)
        {
            if (responses.Any(value => value.RequestedFmnm is not null) || !responses[^1].Terminal ||
                responses.Take(responses.Length - 1).Any(value => value.Terminal) ||
                responses[^1].NextCursor is not null)
                throw new InvalidDataException("Global snapshot pagination does not close at exactly one terminal response.");
            for (var index = 0; index < responses.Length - 1; index++)
                if (!string.Equals(responses[index].NextCursor, responses[index + 1].Cursor, StringComparison.Ordinal))
                    throw new InvalidDataException("Global snapshot cursor chain is incomplete or reordered.");
        }
        else if (responses.Any(value => !value.Terminal || value.Cursor is not null || value.NextCursor is not null) ||
                 expectedFmnm.IsEmpty)
        {
            throw new InvalidDataException("Exact-target snapshot responses must be terminal and unpaginated.");
        }
        return responses;
    }

    private static void ValidateTargetClosure(
        GtaVRockstarCloudSnapshotScopeMode mode,
        ImmutableArray<string> expectedFmnm,
        ImmutableArray<GtaVRockstarCloudSnapshotResponse> responses,
        ImmutableArray<GtaVRockstarCloudJobObject> objects)
    {
        if (mode == GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry) return;
        var received = responses.Select(value => value.RequestedFmnm).ToArray();
        if (received.Any(value => value is null) ||
            !received.Cast<string>().SequenceEqual(expectedFmnm, StringComparer.Ordinal))
            throw new InvalidDataException(
                "Exact-target snapshot responses must follow the exact ordinally sorted requested fmnm set.");
        var requestedByOrdinal = responses.ToDictionary(value => value.Ordinal, value => value.RequestedFmnm!);
        if (objects.Any(value => value.ExactFmnm is not null &&
                                 !string.Equals(
                                     value.ExactFmnm,
                                     requestedByOrdinal[value.ResponseOrdinal],
                                     StringComparison.Ordinal)))
            throw new InvalidDataException(
                "An exact-target response contains an explicit fmnm for a different requested target.");
    }

    private static GtaVRockstarCloudJobSourceIndex CreateIndex(
        ImmutableArray<GtaVRockstarCloudSnapshotResponse> responses,
        Descriptor schema)
    {
        var values = new Dictionary<string, GtaVRockstarCloudJobObject>(StringComparer.Ordinal);
        var parseCount = 0;
        var equivalentDuplicateCount = 0;
        foreach (var response in responses)
        {
            using var document = ParseStrict(response.RawBodyBytes.AsSpan(), $"response body {response.Ordinal}");
            parseCount++;
            var objects = ResolvePointer(document.RootElement, schema.ObjectsPointer, required: true);
            if (objects.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Approved provider objects pointer does not resolve to an array.");
            var objectIndex = 0;
            foreach (var item in objects.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Provider object array contains a non-object value.");
                var basePointer = $"/responses/{response.Ordinal}/rawBodyBase64#json{schema.ObjectsPointer}/{objectIndex}";
                var id = RequiredPointerString(item, schema.ProviderObjectIdentityPointer, "provider object identity");
                var revision = OptionalPointerString(item, schema.NativeRevisionPointer);
                var fmnm = OptionalPointerString(item, schema.FmnmPointer);
                var title = OptionalPointerString(item, schema.TitlePointer);
                var familyId = OptionalPointerString(item, schema.ActivityFamilyIdentityPointer);
                var familyLabel = OptionalPointerString(item, schema.ActivityFamilyLabelPointer);
                var online = OptionalPointerBoolean(item, schema.OnlineScopePointer);
                var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.games.cloud.job-registry",
                    "JobHeader",
                    id,
                    ExactComparison,
                    1);
                var nativeRevision = revision is null ? null : SourceNativeVersion.FromExactUtf8(
                    "rockstar.games.cloud.job.revision", revision, ExactComparison, 1);
                string? PathIfPresent(string? value, string pointer) => value is null ? null : basePointer + pointer;
                var indexed = new GtaVRockstarCloudJobObject(
                    nativeIdentity,
                    nativeRevision,
                    fmnm,
                    title,
                    familyId,
                    familyLabel,
                    online,
                    basePointer,
                    basePointer + schema.ProviderObjectIdentityPointer,
                    PathIfPresent(revision, schema.NativeRevisionPointer),
                    PathIfPresent(fmnm, schema.FmnmPointer),
                    PathIfPresent(title, schema.TitlePointer),
                    PathIfPresent(familyId, schema.ActivityFamilyIdentityPointer),
                    PathIfPresent(familyLabel, schema.ActivityFamilyLabelPointer),
                    online is null ? null : basePointer + schema.OnlineScopePointer,
                    response.Ordinal,
                    response.RequestCoordinate);
                var key = id + "\u001f" + (revision ?? string.Empty);
                if (values.TryGetValue(key, out var existing))
                {
                    if (!EquivalentProviderObject(existing, indexed))
                        throw new InvalidDataException("Duplicate provider identity/revision has conflicting content.");
                    equivalentDuplicateCount++;
                }
                else
                {
                    values.Add(key, indexed);
                }
                objectIndex++;
            }
        }
        return new GtaVRockstarCloudJobSourceIndex(
            values.Values.ToImmutableArray(), parseCount, equivalentDuplicateCount);
    }

    private static bool EquivalentProviderObject(
        GtaVRockstarCloudJobObject left,
        GtaVRockstarCloudJobObject right) =>
        left.ProviderObjectIdentity == right.ProviderObjectIdentity &&
        left.NativeRevisionIdentity == right.NativeRevisionIdentity &&
        string.Equals(left.ExactFmnm, right.ExactFmnm, StringComparison.Ordinal) &&
        string.Equals(left.ExactTitle, right.ExactTitle, StringComparison.Ordinal) &&
        string.Equals(left.ExactActivityFamilyIdentity, right.ExactActivityFamilyIdentity, StringComparison.Ordinal) &&
        string.Equals(left.ExactActivityFamilyLabel, right.ExactActivityFamilyLabel, StringComparison.Ordinal) &&
        left.IsOnline == right.IsOnline;

    private static AcquisitionMethodCoordinate ParseAcquisitionMethod(JsonElement receipt)
    {
        var tool = receipt.GetProperty("captureTool");
        RequireExactProperties(tool, ["methodId", "methodVersion", "toolId", "toolVersion", "toolArtifactSha256"]);
        return new AcquisitionMethodCoordinate(
            ValidateOpaqueCoordinate(RequireStrictText(tool, "methodId"), "capture method ID"),
            ValidateOpaqueCoordinate(RequireStrictText(tool, "methodVersion"), "capture method version"),
            ValidateOpaqueCoordinate(RequireStrictText(tool, "toolId"), "capture tool ID"),
            ValidateOpaqueCoordinate(RequireStrictText(tool, "toolVersion"), "capture tool version"),
            new ContentDigest(ContentDigest.Sha256Algorithm, RequireStrictText(tool, "toolArtifactSha256")));
    }

    private static string ValidateOpaqueCoordinate(string value, string name)
    {
        if (Path.IsPathRooted(value) || value.Contains("\\", StringComparison.Ordinal) ||
            value.Contains("://", StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) || value.Contains('=', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) ||
            new[] { "authorization", "bearer", "cookie", "password", "token", "session", "account", "profile", "email" }
                .Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Cloud snapshot {name} must be an opaque, credential-free coordinate.");
        return value;
    }

    private static string? ValidateProviderOpaqueValue(string? value, string name)
    {
        if (value is null) return null;
        if (Path.IsPathRooted(value) || value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains("://", StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) || value.Contains('&', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal) ||
            new[]
            {
                "authorization", "bearer", "cookie", "password", "token", "session", "account", "profile",
                "email", "secret", "api-key", "apikey", "access-key", "accesskey",
            }.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                $"Cloud snapshot {name} must be an opaque, credential-free provider value.");
        return value;
    }

    private static ImmutableArray<string> NormalizeExpectedFmnm(ImmutableArray<string> values)
    {
        foreach (var value in values)
            ValidateStrictText(value, "expected fmnm");
        var ordered = values.Order(StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidDataException("Expected fmnm values must be unique.");
        return ordered;
    }

    private static byte[] CanonicalStringArray(ImmutableArray<string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    private static byte[] CanonicalJson(byte[] bytes, string name)
    {
        using var document = ParseStrict(bytes, name);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return stream.ToArray();
    }

    private static JsonDocument ParseCanonical(byte[] bytes, string name)
    {
        var canonical = CanonicalJson(bytes, name);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException($"{name} must use canonical compact JSON bytes.");
        return ParseStrict(bytes, name);
    }

    private static JsonDocument ParseStrict(ReadOnlySpan<byte> bytes, string name)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            ValidateNoDuplicateProperties(document.RootElement, name);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException($"{name} is not strict UTF-8 JSON.", exception);
        }
    }

    private static byte[] ReadStrict(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Cloud snapshot bundle document is absent.", path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) throw new InvalidDataException("Cloud snapshot bundle document is empty.");
        return bytes;
    }

    private static void ValidateNoDuplicateProperties(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException($"{name} contains duplicate JSON properties.");
                ValidateNoDuplicateProperties(property.Value, name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item, name);
        }
    }

    private static void RejectForbiddenProperties(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (ForbiddenPropertyNames.Contains(property.Name))
                    throw new InvalidDataException($"{name} contains forbidden credential or account property '{property.Name}'.");
                RejectForbiddenProperties(property.Value, name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectForbiddenProperties(item, name);
        }
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
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText(), skipInputValidation: false); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Unsupported JSON value kind.");
        }
    }

    private static JsonElement ResolvePointer(JsonElement root, string pointer, bool required)
    {
        var current = root;
        if (pointer.Length == 0) return current;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
            {
                current = property;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }
            if (required) throw new InvalidDataException($"Required JSON pointer does not resolve: {pointer}");
            return default;
        }
        return current;
    }

    private static string RequiredPointerString(JsonElement root, string pointer, string name)
    {
        var value = ResolvePointer(root, pointer, required: true);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} is not exact text.");
        var text = value.GetString()!;
        ValidateStrictText(text, name);
        return text;
    }

    private static string? OptionalPointerString(JsonElement root, string pointer)
    {
        var value = ResolvePointer(root, pointer, required: false);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Optional pointer {pointer} is not exact text.");
        var text = value.GetString()!;
        ValidateStrictText(text, pointer);
        return text;
    }

    private static bool? OptionalPointerBoolean(JsonElement root, string pointer)
    {
        var value = ResolvePointer(root, pointer, required: false);
        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Optional pointer {pointer} is not Boolean."),
        };
    }

    private static string ReadPointer(JsonElement root, string propertyName)
    {
        var pointer = RequireStrictText(root, propertyName);
        if (!pointer.StartsWith('/') || pointer.EndsWith('/') || pointer.Contains("//", StringComparison.Ordinal) ||
            HasInvalidPointerEscape(pointer))
            throw new InvalidDataException($"{propertyName} must be a canonical absolute RFC 6901 JSON pointer.");
        return pointer;
    }

    private static bool HasInvalidPointerEscape(string pointer)
    {
        for (var index = 0; index < pointer.Length; index++)
        {
            if (pointer[index] != '~') continue;
            if (++index >= pointer.Length || pointer[index] is not ('0' or '1')) return true;
        }
        return false;
    }

    private static JsonElement RequireArray(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{propertyName} must be an array.");
        return value;
    }

    private static void RequireExactProperties(JsonElement value, IEnumerable<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected one JSON object.");
        var expectedValues = expected.Order(StringComparer.Ordinal).ToArray();
        var actual = value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expectedValues, StringComparer.Ordinal))
            throw new InvalidDataException("JSON object has missing or unsupported properties.");
    }

    private static void RequireText(JsonElement root, string propertyName, string expected)
    {
        var actual = RequireStrictText(root, propertyName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static string RequireStrictText(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{propertyName} must be exact text.");
        var text = value.GetString()!;
        ValidateStrictText(text, propertyName);
        return text;
    }

    private static string? OptionalStrictText(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{propertyName} must be exact text or null.");
        var text = value.GetString()!;
        ValidateStrictText(text, propertyName);
        return text;
    }

    private static void ValidateStrictText(string value, string name)
    {
        if (value.Length == 0 || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException($"{name} must be nonempty exact text without surrounding whitespace.");
        _ = StrictUtf8.GetBytes(value);
    }

    private static int RequireInteger(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"{propertyName} must be an exact 32-bit integer.");
        return result;
    }

    private static void RequireInt(JsonElement root, string propertyName, int expected)
    {
        if (RequireInteger(root, propertyName) != expected) throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static bool RequireBoolean(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{propertyName} must be Boolean."),
        };

    private static void RequireLong(JsonElement root, string propertyName, long expected)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var actual) || actual != expected)
            throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static void RequireDigest(JsonElement root, string propertyName, ContentDigest expected)
    {
        var actual = new ContentDigest(ContentDigest.Sha256Algorithm, RequireStrictText(root, propertyName));
        if (actual != expected) throw new InvalidDataException($"{propertyName} mismatch.");
    }

    private static ContentDigest Digest(ReadOnlySpan<byte> bytes) =>
        new(ContentDigest.Sha256Algorithm, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private sealed record Descriptor(
        string ObjectsPointer,
        string ProviderObjectIdentityPointer,
        string NativeRevisionPointer,
        string FmnmPointer,
        string TitlePointer,
        string ActivityFamilyIdentityPointer,
        string ActivityFamilyLabelPointer,
        string OnlineScopePointer);

    private sealed record Metadata(
        GtaVRockstarCloudSnapshotScopeMode ScopeMode,
        DateTimeOffset CapturedAtUtc,
        string ProviderRegistryRevision,
        string Platform,
        string Locale,
        string VisibilityScope,
        string AuthenticationScope,
        string EndpointTemplateId,
        string EndpointTemplateVersion);
}
