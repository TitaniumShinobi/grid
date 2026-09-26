using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal sealed record ValidatedGtaAcquisition(
    SourceNativeVersion GameVersion,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
    ImmutableDictionary<string, FrozenMember> Members,
    ImmutableArray<SourceArtifactId> ContainerFingerprints);

internal sealed record FrozenMember(
    string Coordinate,
    ImmutableArray<byte> Bytes,
    ContentDigest Digest,
    SourceArtifactId ArtifactId,
    long ByteLength);

internal static class GtaAcquisitionReceiptLoader
{
    private const string ExpectedGameId = "game.grandtheftautov-enhanced";
    private const string ExpectedAppId = "3240220";
    private const string ExpectedToolVersion = "0.5.1";
    private const string ExpectedToolDigest = "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c";
    private const string ExpectedToolRevision = "75bd2b8b99838bf8ec49afa2a82ba03f557cd584";
    private const string ExpectedMethodId = "grid.gta-v-enhanced.rpf-member-acquisition";
    private const string LegacyReceiptV1MethodVersion = "2";
    private const string ExpectedMethodVersion = "4";

    public static async Task<ValidatedGtaAcquisition> LoadAsync(
        string receiptPath,
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        receiptPath = Path.GetFullPath(receiptPath);
        gameRoot = Path.GetFullPath(gameRoot);
        var bytes = await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        var receiptSchemaVersion = root.GetProperty("schemaVersion").GetInt32();
        Require(receiptSchemaVersion is 1 or 2, "Unsupported acquisition receipt schema.");
        Require(root.GetProperty("gameId").GetString() == ExpectedGameId, "Acquisition receipt GameId mismatch.");
        VerifyDocumentDigest(root);

        var platform = root.GetProperty("platformObservation");
        Require(platform.GetProperty("provider").GetString() == "valve.steam", "Acquisition provider mismatch.");
        Require(platform.GetProperty("appId").GetString() == ExpectedAppId, "Steam app identity mismatch.");
        var buildId = RequireText(platform.GetProperty("buildId"), "Steam build ID");
        Require(root.GetProperty("gameVersion").GetString() == buildId, "Receipt build coordinates disagree.");
        var observedAtText = RequireText(root.GetProperty("observedAtUtc"), "observation time");
        var hasObservedAt = DateTimeOffset.TryParse(
            observedAtText,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var observedAt);
        Require(observedAtText.EndsWith('Z') && hasObservedAt && observedAt.Offset == TimeSpan.Zero,
            "Acquisition observation time must be valid round-trip UTC text.");
        VerifySteamManifest(platform, gameRoot, receiptSchemaVersion);

        var tool = root.GetProperty("acquisitionTool");
        Require(tool.GetProperty("toolId").GetString() == "fivefury.rpf-read", "Acquisition tool ID mismatch.");
        Require(tool.GetProperty("exactVersion").GetString() == ExpectedToolVersion, "FiveFury version mismatch.");
        Require(tool.GetProperty("artifactSha256").GetString() == ExpectedToolDigest, "FiveFury artifact digest mismatch.");
        Require(tool.GetProperty("sourceRevision").GetString() == ExpectedToolRevision, "FiveFury source revision mismatch.");
        Require(tool.GetProperty("methodId").GetString() == ExpectedMethodId, "Acquisition method mismatch.");
        var methodVersion = receiptSchemaVersion == 1
            ? LegacyReceiptV1MethodVersion
            : ExpectedMethodVersion;
        Require(tool.GetProperty("methodVersion").GetString() == methodVersion, "Acquisition method version mismatch.");
        Require(tool.GetProperty("receiptSchemaVersion").GetInt32() == receiptSchemaVersion,
            "Acquisition tool receipt schema mismatch.");

        var method = new AcquisitionMethodCoordinate(
            ExpectedMethodId,
            methodVersion,
            "fivefury.rpf-read",
            ExpectedToolVersion,
            new ContentDigest(ContentDigest.Sha256Algorithm, ExpectedToolDigest));
        var appIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam",
            "Application",
            ExpectedAppId,
            "valve.steam.app-id.exact-utf8",
            1);
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id",
            buildId,
            "valve.steam.build-id.exact-utf8",
            1);

        var receiptDirectory = Path.GetDirectoryName(receiptPath) ??
            throw new InvalidDataException("Acquisition receipt has no directory.");
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        var frozenMembers = ImmutableDictionary.CreateBuilder<string, FrozenMember>(StringComparer.Ordinal);
        var fingerprints = ImmutableArray.CreateBuilder<SourceArtifactId>();
        var seenContainers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinate = RequireText(container.GetProperty("containerCoordinate"), "container coordinate");
            Require(seenContainers.Add(coordinate), "Duplicate acquisition container coordinate.");
            ValidateRelativeCoordinate(coordinate);
            var containerLength = container.GetProperty("byteLength").GetInt64();
            var containerDigest = new ContentDigest(
                ContentDigest.Sha256Algorithm,
                RequireText(container.GetProperty("sha256"), "container digest"));
            var containerPath = ResolveWithin(gameRoot, coordinate);
            var actualContainer = await ComputeDigestAsync(containerPath, cancellationToken).ConfigureAwait(false);
            Require(actualContainer.Length == containerLength && actualContainer.Digest == containerDigest,
                $"Container digest or length mismatch: {coordinate}");
            fingerprints.Add(SourceArtifactId.DeriveV1(containerDigest));

            var members = ImmutableArray.CreateBuilder<SourceAcquisitionMember>();
            foreach (var member in container.GetProperty("members").EnumerateArray())
            {
                var memberPath = RequireText(member.GetProperty("memberPath"), "member path");
                ValidateRelativeCoordinate(memberPath);
                var memberCoordinateText = RequireText(member.GetProperty("memberCoordinate"), "member coordinate");
                Require(memberCoordinateText == $"{coordinate}!/{memberPath}", "Member coordinate does not bind its container.");
                var memberLength = member.GetProperty("byteLength").GetInt64();
                var memberDigest = new ContentDigest(
                    ContentDigest.Sha256Algorithm,
                    RequireText(member.GetProperty("sha256"), "member digest"));
                var artifactId = SourceArtifactId.DeriveV1(memberDigest);
                var frozenPath = ResolveWithin(
                    receiptDirectory,
                    "members/" + coordinate + "/" + memberPath);
                var exactBytes = (await File.ReadAllBytesAsync(frozenPath, cancellationToken).ConfigureAwait(false)).ToImmutableArray();
                Require(exactBytes.Length == memberLength && ContentDigest.ComputeSha256(exactBytes.AsSpan()) == memberDigest,
                    $"Frozen member digest or length mismatch: {memberCoordinateText}");
                Require(frozenMembers.TryAdd(
                    memberCoordinateText,
                    new FrozenMember(memberCoordinateText, exactBytes, memberDigest, artifactId, memberLength)),
                    "Duplicate acquisition member coordinate.");
                var memberCoordinate = CreateMemberCoordinate(memberCoordinateText);
                members.Add(new SourceAcquisitionMember(memberCoordinate, memberLength, memberDigest, artifactId));
            }

            var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate",
                "Rpf7Container",
                coordinate,
                "grid.gta-v.container-coordinate.exact-utf8",
                1);
            var receiptId = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity,
                gameVersion,
                containerCoordinate,
                containerLength,
                containerDigest,
                method,
                members.ToImmutable());
            var receipt = new SourceAcquisitionReceipt(
                receiptId,
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity,
                gameVersion,
                containerCoordinate,
                containerLength,
                containerDigest,
                method,
                members.ToImmutable());
            receipts.Add(receipt);
            foreach (var memberValue in receipt.Members)
                bindings.Add(new SourceArtifactAcquisitionBinding(
                    memberValue.ArtifactId,
                    receipt.Id,
                    memberValue.MemberCoordinate,
                    memberValue.ByteLength,
                    memberValue.Digest));
        }

        Require(receipts.Count == 4 && frozenMembers.Count == 1061,
            "The Enhanced canary acquisition must contain exactly four containers and 1,061 members.");
        return new ValidatedGtaAcquisition(
            gameVersion,
            observedAt,
            receipts.ToImmutable(),
            bindings.ToImmutable(),
            frozenMembers.ToImmutable(),
            fingerprints.Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static void VerifySteamManifest(JsonElement expected, string gameRoot, int receiptSchemaVersion)
    {
        var manifestCoordinate = RequireText(expected.GetProperty("manifestCoordinate"), "Steam manifest coordinate");
        Require(manifestCoordinate == $"steamapps/appmanifest_{ExpectedAppId}.acf", "Steam manifest coordinate mismatch.");
        var manifest = Path.GetFullPath(Path.Combine(Path.GetFullPath(gameRoot), "..", "..", $"appmanifest_{ExpectedAppId}.acf"));
        var bytes = File.ReadAllBytes(manifest);
        var historicalLength = expected.GetProperty("manifestByteLength").GetInt64();
        var historicalDigest = RequireText(expected.GetProperty("manifestSha256"), "Steam manifest digest");
        Require(historicalLength > 0 && Regex.IsMatch(historicalDigest, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
            "Historical Steam manifest observation is invalid.");
        if (receiptSchemaVersion == 1)
        {
            Require(bytes.LongLength == historicalLength, "Steam manifest length mismatch.");
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == historicalDigest,
                "Steam manifest digest mismatch.");
            return;
        }

        Require(RequireText(expected.GetProperty("provider"), "Steam provider") == "valve.steam",
            "Acquisition provider mismatch.");
        Require(RequireText(expected.GetProperty("appIdFieldPath"), "Steam app ID field path") == "/AppState/appid" &&
                RequireText(expected.GetProperty("buildIdFieldPath"), "Steam build ID field path") == "/AppState/buildid" &&
                RequireText(expected.GetProperty("installDirFieldPath"), "Steam install directory field path") ==
                "/AppState/installdir",
            "Stable Steam manifest field coordinates mismatch.");
        var fields = ParseSteamManifestStableFields(bytes);
        Require(fields.AppId == RequireText(expected.GetProperty("appId"), "Steam app ID") &&
                fields.BuildId == RequireText(expected.GetProperty("buildId"), "Steam build ID") &&
                fields.InstallDir == RequireText(expected.GetProperty("installDir"), "Steam install directory"),
            "Stable Steam app/build/install observation does not match the local app manifest.");
        Require(fields.AppId == ExpectedAppId &&
                fields.InstallDir == Path.GetFileName(Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar)),
            "Steam manifest app or installation identity mismatch.");
    }

    private static (string AppId, string BuildId, string InstallDir) ParseSteamManifestStableFields(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Steam manifest is not strict UTF-8.", exception);
        }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length != 0)
            .ToArray();
        if (lines.Length < 3 || lines[0] != "\"AppState\"" || lines[1] != "{")
            throw new InvalidDataException("Steam manifest requires one exact AppState root object.");

        var stableNames = new[] { "appid", "buildid", "installdir" };
        var stable = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = 1;
        var expectObjectOpen = false;
        var rootClosed = false;
        for (var index = 2; index < lines.Length; index++)
        {
            var line = lines[index];
            if (rootClosed)
                throw new InvalidDataException("Steam manifest has content after the AppState root.");
            if (line == "{")
            {
                if (!expectObjectOpen)
                    throw new InvalidDataException("Steam manifest has an unbound object opening.");
                depth++;
                expectObjectOpen = false;
                continue;
            }
            if (expectObjectOpen)
                throw new InvalidDataException("Steam manifest object name is not followed by an opening brace.");
            if (line == "}")
            {
                depth--;
                if (depth < 0) throw new InvalidDataException("Steam manifest has an unmatched closing brace.");
                if (depth == 0) rootClosed = true;
                continue;
            }

            var pair = Regex.Match(line, "\\A\"([^\"\\r\\n]+)\"\\s+\"([^\"\\r\\n]*)\"\\z",
                RegexOptions.CultureInvariant);
            if (pair.Success)
            {
                var name = pair.Groups[1].Value;
                var stableName = stableNames.SingleOrDefault(value =>
                    string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                if (stableName is not null &&
                    (!string.Equals(name, stableName, StringComparison.Ordinal) ||
                     depth != 1 || stable.ContainsKey(stableName) || pair.Groups[2].Value.Length == 0))
                    throw new InvalidDataException(
                        $"Steam manifest requires exactly one nonempty direct exact-case {stableName} field.");
                if (stableName is not null) stable.Add(stableName, pair.Groups[2].Value);
                continue;
            }

            var objectName = Regex.Match(line, "\\A\"([^\"\\r\\n]+)\"\\z", RegexOptions.CultureInvariant);
            if (!objectName.Success)
                throw new InvalidDataException($"Steam manifest has unsupported structure at line {index + 1}.");
            if (stableNames.Any(value =>
                    string.Equals(value, objectName.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A stable Steam identity field cannot be an object.");
            expectObjectOpen = true;
        }
        if (expectObjectOpen || depth != 0 || !rootClosed)
            throw new InvalidDataException("Steam manifest AppState object is not closed.");
        if (stable.Count != stableNames.Length)
            throw new InvalidDataException("Steam manifest is missing a stable AppState identity field.");
        return (stable["appid"], stable["buildid"], stable["installdir"]);
    }

    private static void VerifyDocumentDigest(JsonElement root)
    {
        var expected = RequireText(root.GetProperty("receiptDocumentSha256"), "receipt document digest");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, root, skipReceiptDigest: true);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        Require(actual == expected, "Acquisition receipt document digest mismatch.");
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool skipReceiptDigest = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (skipReceiptDigest && property.NameEquals("receiptDocumentSha256")) continue;
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

    private static SourceNativeIdentifier CreateMemberCoordinate(string value) =>
        SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.rpf-member-coordinate",
            "Rpf7Member",
            value,
            "grid.gta-v.rpf-member-coordinate.exact-utf8",
            1);

    private static string ResolveWithin(string root, string relative)
    {
        ValidateRelativeCoordinate(relative);
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Acquisition coordinate escaped its bounded root.");
        return result;
    }

    private static void ValidateRelativeCoordinate(string value)
    {
        Require(!string.IsNullOrEmpty(value) && !value.Contains('\\') && !value.StartsWith("/", StringComparison.Ordinal),
            "Acquisition coordinates must be normalized relative paths.");
        Require(!value.Split('/').Any(segment => segment is "" or "." or ".."),
            "Acquisition coordinates cannot contain empty or traversal segments.");
    }

    private static async Task<(long Length, ContentDigest Digest)> ComputeDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            length += read;
            hash.AppendData(buffer, 0, read);
        }
        return (length, new ContentDigest(ContentDigest.Sha256Algorithm, Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    private static string RequireText(JsonElement value, string name)
    {
        var result = value.GetString();
        if (string.IsNullOrEmpty(result) || result.Trim() != result)
            throw new InvalidDataException($"{name} is missing or noncanonical.");
        _ = new UTF8Encoding(false, true).GetBytes(result);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
