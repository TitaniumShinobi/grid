using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal sealed record ValidatedGtaItemAcquisition(
    SourceNativeVersion GameVersion,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
    ImmutableDictionary<string, FrozenSourceArtifact> Artifacts,
    ImmutableArray<FrozenSourceArtifact> CorpusArtifacts,
    ImmutableArray<byte> CorpusIndexBytes);

internal static class GtaItemAcquisitionReceiptLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ValidatedGtaItemAcquisition> LoadAsync(
        string receiptPath,
        string corpusIndexPath,
        string sourceFamilyManifestPath,
        CancellationToken cancellationToken = default)
    {
        var receiptFullPath = Path.GetFullPath(receiptPath);
        var indexFullPath = Path.GetFullPath(corpusIndexPath);
        var manifestFullPath = Path.GetFullPath(sourceFamilyManifestPath);
        if (!File.Exists(receiptFullPath) || !File.Exists(indexFullPath) || !File.Exists(manifestFullPath))
            throw new FileNotFoundException("The mounted Item receipt or corpus index is absent.");
        var rootDirectory = Path.GetDirectoryName(receiptFullPath)!;
        if (!string.Equals(
                Path.GetDirectoryName(indexFullPath), rootDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The mounted Item receipt and corpus index must share one immutable bundle root.");

        var receiptBytes = await File.ReadAllBytesAsync(receiptFullPath, cancellationToken).ConfigureAwait(false);
        _ = StrictUtf8.GetString(receiptBytes);
        using var document = JsonDocument.Parse(receiptBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        ValidateNoDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1, "Unsupported mounted Item receipt schema.");
        Require(string.Equals(root.GetProperty("gameId").GetString(),
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value, StringComparison.Ordinal),
            "Mounted Item receipt GameId mismatch.");
        var build = RequireText(root, "gameVersion");
        Require(string.Equals(build, "25261616", StringComparison.Ordinal),
            "Mounted Item receipt Steam build mismatch.");
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            RequireText(root, "gameVersionNamespace"), build,
            "valve.steam.build-id.exact-utf8", 1);
        var observedAt = root.GetProperty("observedAtUtc").GetDateTimeOffset();
        Require(observedAt.Offset == TimeSpan.Zero, "Mounted Item observation must be UTC.");
        VerifyDocumentDigest(receiptBytes, root);
        var manifestBinding = root.GetProperty("sourceFamilyManifest");
        Require(string.Equals(RequireText(manifestBinding, "manifestId"),
                    "grid.gta-v-enhanced.item-source-families", StringComparison.Ordinal) &&
                manifestBinding.GetProperty("schemaVersion").GetInt32() == 1,
            "Mounted Item source-family manifest identity mismatch.");
        var manifestDigest = CanonicalJsonSha256(
            await File.ReadAllBytesAsync(manifestFullPath, cancellationToken).ConfigureAwait(false));
        Require(string.Equals(
                RequireText(manifestBinding, "documentSha256"), manifestDigest, StringComparison.Ordinal),
            "Mounted Item source-family manifest digest mismatch.");

        var tool = root.GetProperty("acquisitionTool");
        Require(tool.GetProperty("receiptSchemaVersion").GetInt32() == 1,
            "Mounted Item acquisition tool receipt schema mismatch.");
        Require(RequireText(tool, "methodId") == "grid.gta-v-enhanced.item-mounted-acquisition" &&
                RequireText(tool, "methodVersion") == "1" && RequireText(tool, "toolId") == "fivefury.rpf-read" &&
                RequireText(tool, "exactVersion") == GtaVItemCorpusIndex.DecoderVersion &&
                RequireText(tool, "artifactSha256") == GtaVItemCorpusIndex.ApprovedDecoderArtifactSha256,
            "Mounted Item acquisition decoder differs from the pinned approved tool.");
        var method = new AcquisitionMethodCoordinate(
            RequireText(tool, "methodId"),
            RequireText(tool, "methodVersion"),
            RequireText(tool, "toolId"),
            RequireText(tool, "exactVersion"),
            new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(tool, "artifactSha256")));
        var appIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam", "Application", "3240220",
            "valve.steam.app-id.exact-utf8", 1);

        var indexBindingElement = root.GetProperty("itemCorpusIndex");
        Require(RequireText(indexBindingElement, "fileName") == "item-corpus-index.v1.json" &&
            Path.GetFileName(indexFullPath) == "item-corpus-index.v1.json", "Item corpus filename mismatch.");
        var indexLength = indexBindingElement.GetProperty("documentByteLength").GetInt64();
        var indexDigest = new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(indexBindingElement, "documentSha256"));

        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        var artifacts = ImmutableDictionary.CreateBuilder<string, FrozenSourceArtifact>(StringComparer.Ordinal);
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containerText = RequireText(container, "containerCoordinate");
            var containerLength = container.GetProperty("byteLength").GetInt64();
            var containerDigest = new ContentDigest(
                ContentDigest.Sha256Algorithm, RequireText(container, "sha256"));
            var members = ImmutableArray.CreateBuilder<SourceAcquisitionMember>();
            foreach (var artifact in container.GetProperty("artifacts").EnumerateArray())
            {
                var coordinate = RequireText(artifact, "sourceCoordinate");
                Require(coordinate.StartsWith(containerText + "!/", StringComparison.Ordinal),
                    "Mounted Item artifact does not bind its container coordinate.");
                var relativePath = RequireText(artifact, "frozenRelativePath");
                var frozenPath = ResolveWithin(rootDirectory, relativePath);
                var exactBytes = await File.ReadAllBytesAsync(frozenPath, cancellationToken).ConfigureAwait(false);
                var byteLength = artifact.GetProperty("byteLength").GetInt64();
                var digest = new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(artifact, "sha256"));
                Require(exactBytes.LongLength == byteLength &&
                        ContentDigest.ComputeSha256(exactBytes) == digest,
                    "Mounted Item frozen artifact digest or length mismatch.");
                var artifactId = SourceArtifactId.DeriveV1(digest);
                var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.resource-coordinate", "Rpf7Member", coordinate,
                    "grid.gta-v.resource-coordinate.exact-utf8", 1);
                members.Add(new SourceAcquisitionMember(memberCoordinate, byteLength, digest, artifactId));
                var format = new KnowledgeFormatCoordinate(
                    RequireText(artifact, "formatId"), RequireText(artifact, "formatVersion"));
                var sourceObjectType = GtaVMountedItemKnowledgeAdapter.SourceObjectType(format.FormatId);
                var sourceCoordinate = SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.resource-coordinate", sourceObjectType, coordinate,
                    "grid.gta-v.resource-coordinate.exact-utf8", 1);
                Require(artifacts.TryAdd(coordinate, new FrozenSourceArtifact(
                    artifactId, digest, sourceCoordinate, format,
                    exactBytes.ToImmutableArray(), observedAt)),
                    "Mounted Item artifact coordinate is duplicated.");
            }
            var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", containerText,
                "grid.gta-v.container-coordinate.exact-utf8", 1);
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
            foreach (var member in receipt.Members)
                bindings.Add(new SourceArtifactAcquisitionBinding(
                    member.ArtifactId, receipt.Id, member.MemberCoordinate,
                    member.ByteLength, member.Digest));
        }
        var indexBytes = await File.ReadAllBytesAsync(indexFullPath, cancellationToken).ConfigureAwait(false);
        _ = StrictUtf8.GetString(indexBytes);
        Require(indexBytes.LongLength == indexLength &&
                ContentDigest.ComputeSha256(indexBytes) == indexDigest,
            "Mounted Item corpus index differs from its acquisition receipt binding.");
        var corpusArtifacts = SelectCorpusArtifacts(indexBytes, artifacts);
        return new ValidatedGtaItemAcquisition(
            gameVersion,
            observedAt,
            receipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray(),
            artifacts.ToImmutable(),
            corpusArtifacts,
            indexBytes.ToImmutableArray());
    }

    private static ImmutableArray<FrozenSourceArtifact> SelectCorpusArtifacts(
        ReadOnlySpan<byte> indexBytes,
        ImmutableDictionary<string, FrozenSourceArtifact>.Builder artifacts)
    {
        using var document = JsonDocument.Parse(indexBytes.ToArray());
        var distinct = document.RootElement.GetProperty("sources").EnumerateArray()
            .Select(value => RequireText(value, "sourceCoordinate")).ToArray();
        Require(distinct.Distinct(StringComparer.Ordinal).Count() == distinct.Length && distinct.Length == artifacts.Count,
            "Item corpus source closure mismatch.");
        var values = distinct.Select(value => artifacts.TryGetValue(value, out var artifact) ? artifact :
            throw new InvalidDataException("Item corpus source is not acquired.")).ToImmutableArray();
        return values;
    }

    public static SemanticAcquisitionProjection NarrowTo(
        ValidatedGtaItemAcquisition acquisition,
        IEnumerable<SourceArtifactId> retainedArtifactIds)
    {
        var retained = retainedArtifactIds.ToHashSet();
        var replacementIds = new Dictionary<SourceAcquisitionReceiptId, SourceAcquisitionReceiptId>();
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        foreach (var receipt in acquisition.Receipts)
        {
            var members = receipt.Members.Where(value => retained.Contains(value.ArtifactId)).ToImmutableArray();
            if (members.IsEmpty) continue;
            var id = SourceAcquisitionReceiptId.DeriveV1(
                receipt.ReceiptSchemaVersion, receipt.GameId,
                receipt.DistributionApplicationIdentity, receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate, receipt.ContainerByteLength, receipt.ContainerDigest,
                receipt.AcquisitionMethod, members);
            replacementIds.Add(receipt.Id, id);
            receipts.Add(new SourceAcquisitionReceipt(
                id, receipt.ReceiptSchemaVersion, receipt.GameId,
                receipt.DistributionApplicationIdentity, receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate, receipt.ContainerByteLength, receipt.ContainerDigest,
                receipt.AcquisitionMethod, members));
        }
        var bindings = acquisition.Bindings
            .Where(value => retained.Contains(value.ArtifactId))
            .Select(value => new SourceArtifactAcquisitionBinding(
                value.ArtifactId, replacementIds[value.AcquisitionReceiptId], value.MemberCoordinate,
                value.MemberByteLength, value.MemberDigest))
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(bindings.Select(x => x.ArtifactId).Distinct().Count() == retained.Count && receipts.Count > 0,
            "Mounted Item semantic acquisition closure is incomplete.");
        return new SemanticAcquisitionProjection(
            receipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(), bindings);
    }

    private static void VerifyDocumentDigest(ReadOnlySpan<byte> receiptBytes, JsonElement root)
    {
        var expected = RequireText(root, "receiptDocumentSha256");
        Require(expected.Length == 64 && expected.All(value => char.IsAsciiHexDigit(value)) &&
                string.Equals(expected, expected.ToLowerInvariant(), StringComparison.Ordinal),
            "Mounted Item receipt document digest is not lowercase SHA-256 data.");

        // The acquisition writer hashes its canonical JSON object before adding this one
        // root property, then writes the resulting canonical object plus a single LF. Remove
        // that exact property from the persisted canonical bytes so verification uses the
        // acquisition algorithm byte-for-byte rather than a second JSON encoder's escaping.
        var documentBytes = receiptBytes;
        if (documentBytes.Length > 0 && documentBytes[^1] == (byte)'\n')
            documentBytes = documentBytes[..^1];
        var encodedProperty = Encoding.UTF8.GetBytes($"\"receiptDocumentSha256\":\"{expected}\",");
        var propertyOffset = documentBytes.IndexOf(encodedProperty);
        Require(propertyOffset >= 0 && documentBytes[(propertyOffset + encodedProperty.Length)..].IndexOf(encodedProperty) < 0,
            "Mounted Item receipt digest property is not in canonical root-object form.");
        var unsignedDocument = new byte[documentBytes.Length - encodedProperty.Length];
        documentBytes[..propertyOffset].CopyTo(unsignedDocument);
        documentBytes[(propertyOffset + encodedProperty.Length)..].CopyTo(unsignedDocument.AsSpan(propertyOffset));
        var actual = Convert.ToHexString(SHA256.HashData(unsignedDocument)).ToLowerInvariant();
        Require(string.Equals(expected, actual, StringComparison.Ordinal),
            "Mounted Item receipt document digest mismatch.");
    }

    private static string CanonicalJsonSha256(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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

    private static string ResolveWithin(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(value => value is "" or "." or ".."))
            throw new InvalidDataException("Mounted Item frozen artifact path is not canonical relative data.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Mounted Item frozen artifact escapes its immutable bundle.");
        return result;
    }

    private static string RequireText(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"Mounted Item receipt {propertyName} is absent or invalid.");
        return property.GetString()!;
    }

    private static void ValidateNoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(seen.Add(property.Name), "Mounted Item receipt contains duplicate JSON properties.");
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateNoDuplicateProperties(item);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
