using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal sealed record ValidatedGtaRouteAcquisition(
    SourceNativeVersion GameVersion,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
    ImmutableDictionary<string, FrozenSourceArtifact> Artifacts,
    ImmutableArray<FrozenSourceArtifact> CorpusArtifacts,
    ImmutableArray<FrozenSourceArtifact> SemanticArtifacts,
    GtaVRouteCorpusIndexReceiptBinding CorpusIndexReceiptBinding,
    ImmutableArray<byte> CorpusIndexBytes);

internal static class GtaRouteAcquisitionReceiptLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ValidatedGtaRouteAcquisition> LoadAsync(
        string receiptPath,
        string corpusIndexPath,
        string sourceFamilyManifestPath,
        CancellationToken cancellationToken = default)
    {
        var receiptFullPath = Path.GetFullPath(receiptPath);
        var indexFullPath = Path.GetFullPath(corpusIndexPath);
        var manifestFullPath = Path.GetFullPath(sourceFamilyManifestPath);
        if (!File.Exists(receiptFullPath) || !File.Exists(indexFullPath) || !File.Exists(manifestFullPath))
            throw new FileNotFoundException("The route receipt, corpus index, or source manifest is absent.");
        var rootDirectory = Path.GetDirectoryName(receiptFullPath)!;
        if (!string.Equals(Path.GetDirectoryName(indexFullPath), rootDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The route receipt and index must share one immutable bundle root.");

        var receiptBytes = await File.ReadAllBytesAsync(receiptFullPath, cancellationToken).ConfigureAwait(false);
        _ = StrictUtf8.GetString(receiptBytes);
        using var document = JsonDocument.Parse(receiptBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 96,
        });
        ValidateNoDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1,
            "Unsupported route receipt schema.");
        Require(string.Equals(RequireText(root, "gameId"),
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value, StringComparison.Ordinal),
            "Route receipt GameId mismatch.");
        var build = RequireText(root, "gameVersion");
        Require(string.Equals(build, "25261616", StringComparison.Ordinal),
            "Route receipt Steam build mismatch.");
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            RequireText(root, "gameVersionNamespace"), build,
            "valve.steam.build-id.exact-utf8", 1);
        var observedAt = root.GetProperty("observedAtUtc").GetDateTimeOffset();
        Require(observedAt.Offset == TimeSpan.Zero, "Route observation must be UTC.");
        VerifyDocumentDigest(receiptBytes, root);

        var manifestBinding = root.GetProperty("sourceFamilyManifest");
        Require(string.Equals(RequireText(manifestBinding, "manifestId"),
                    "grid.gta-v-enhanced.route-source-families", StringComparison.Ordinal) &&
                manifestBinding.GetProperty("schemaVersion").GetInt32() == 1,
            "Route source-family manifest identity mismatch.");
        var manifestDigest = CanonicalJsonSha256(
            await File.ReadAllBytesAsync(manifestFullPath, cancellationToken).ConfigureAwait(false));
        Require(string.Equals(RequireText(manifestBinding, "documentSha256"), manifestDigest,
                StringComparison.Ordinal),
            "Route source-family manifest digest mismatch.");

        var tool = root.GetProperty("acquisitionTool");
        Require(tool.GetProperty("receiptSchemaVersion").GetInt32() == 1,
            "Route acquisition tool receipt schema mismatch.");
        Require(RequireText(tool, "methodId") == "grid.gta-v-enhanced.route-acquisition" &&
                RequireText(tool, "methodVersion") == "1" && RequireText(tool, "toolId") == "fivefury.rpf+ynd+gxt2-read" &&
                RequireText(tool, "exactVersion") == GtaVRouteCorpusIndex.DecoderVersion &&
                RequireText(tool, "artifactSha256") == GtaVRouteCorpusIndex.DecoderArtifactSha256 &&
                RequireText(tool, "sourceRevision") == "75bd2b8b99838bf8ec49afa2a82ba03f557cd584",
            "Route acquisition decoder differs from the pinned approved tool.");
        var method = new AcquisitionMethodCoordinate(
            RequireText(tool, "methodId"),
            RequireText(tool, "methodVersion"),
            RequireText(tool, "toolId"),
            RequireText(tool, "exactVersion"),
            new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(tool, "artifactSha256")));
        var appIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam", "Application", "3240220",
            "valve.steam.app-id.exact-utf8", 1);

        var indexBindingElement = root.GetProperty("routeCorpusIndex");
        Require(string.Equals(RequireText(indexBindingElement, "fileName"),
                    "route-corpus-index.v1.json", StringComparison.Ordinal) &&
                string.Equals(Path.GetFileName(indexFullPath), "route-corpus-index.v1.json", StringComparison.Ordinal),
            "Route corpus-index filename mismatch.");
        var indexBinding = new GtaVRouteCorpusIndexReceiptBinding(
            RequireText(indexBindingElement, "schemaId"),
            indexBindingElement.GetProperty("schemaVersion").GetInt32(),
            RequireText(indexBindingElement, "decoderMethodId"),
            RequireText(indexBindingElement, "decoderExactVersion"),
            RequireText(indexBindingElement, "decoderArtifactSha256"),
            indexBindingElement.GetProperty("documentByteLength").GetInt64(),
            new ContentDigest(ContentDigest.Sha256Algorithm,
                RequireText(indexBindingElement, "documentSha256")));

        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        var artifacts = ImmutableDictionary.CreateBuilder<string, FrozenSourceArtifact>(StringComparer.Ordinal);
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containerText = RequireText(container, "containerCoordinate");
            var containerLength = container.GetProperty("byteLength").GetInt64();
            var containerDigest = new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(container, "sha256"));
            var members = ImmutableArray.CreateBuilder<SourceAcquisitionMember>();
            foreach (var artifact in container.GetProperty("artifacts").EnumerateArray())
            {
                var coordinate = RequireText(artifact, "sourceCoordinate");
                Require(coordinate.StartsWith(containerText + "!/", StringComparison.Ordinal),
                    "Route artifact does not bind its container coordinate.");
                var frozenPath = ResolveWithin(rootDirectory, RequireText(artifact, "frozenRelativePath"));
                var exactBytes = await File.ReadAllBytesAsync(frozenPath, cancellationToken).ConfigureAwait(false);
                var byteLength = artifact.GetProperty("byteLength").GetInt64();
                var digest = new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(artifact, "sha256"));
                Require(exactBytes.LongLength == byteLength && ContentDigest.ComputeSha256(exactBytes) == digest,
                    "Route frozen artifact digest or length mismatch.");
                var artifactId = SourceArtifactId.DeriveV1(digest);
                var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.resource-coordinate", "Rpf7Member", coordinate,
                    "grid.gta-v.resource-coordinate.exact-utf8", 1);
                members.Add(new SourceAcquisitionMember(memberCoordinate, byteLength, digest, artifactId));
                var format = new KnowledgeFormatCoordinate(
                    RequireText(artifact, "formatId"), RequireText(artifact, "formatVersion"));
                var objectType = format.FormatId switch
                {
                    GtaVRouteCorpusIndex.YndFormatId => "YndStreetNameHash",
                    GtaVRouteCorpusIndex.GxtFormatId => "GXT2",
                    GtaVRouteCorpusIndex.RpfFormatId => "Rpf7Container",
                    _ => "MountedRouteSource",
                };
                var sourceCoordinate = SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.resource-coordinate", objectType, coordinate,
                    "grid.gta-v.resource-coordinate.exact-utf8", 1);
                Require(artifacts.TryAdd(coordinate, new FrozenSourceArtifact(
                        artifactId, digest, sourceCoordinate, format,
                        exactBytes.ToImmutableArray(), observedAt)),
                    "Route artifact coordinate is duplicated.");
            }

            var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", containerText,
                "grid.gta-v.container-coordinate.exact-utf8", 1);
            var receiptId = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity, gameVersion, containerCoordinate, containerLength, containerDigest, method,
                members.ToImmutable());
            var receipt = new SourceAcquisitionReceipt(
                receiptId, SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity, gameVersion, containerCoordinate, containerLength, containerDigest, method,
                members.ToImmutable());
            receipts.Add(receipt);
            foreach (var member in receipt.Members)
                bindings.Add(new SourceArtifactAcquisitionBinding(
                    member.ArtifactId, receipt.Id, member.MemberCoordinate, member.ByteLength, member.Digest));
        }

        var indexBytes = await File.ReadAllBytesAsync(indexFullPath, cancellationToken).ConfigureAwait(false);
        _ = StrictUtf8.GetString(indexBytes);
        Require(indexBytes.LongLength == indexBinding.ByteLength &&
                ContentDigest.ComputeSha256(indexBytes) == indexBinding.Digest,
            "Route corpus index differs from its acquisition receipt binding.");
        var (corpusArtifacts, semanticArtifacts) = SelectCorpusArtifacts(indexBytes, artifacts);
        var index = GtaVRouteCorpusIndex.Load(indexBytes, semanticArtifacts, indexBinding);
        using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestFullPath, cancellationToken).ConfigureAwait(false));
        var manifest = manifestDocument.RootElement;
        var counts = manifest.GetProperty("exactBuildClosure");
        Require(index.Metrics.YndArtifactCount == counts.GetProperty("yndArtifactCount").GetInt32() &&
                index.Metrics.NodeCount == counts.GetProperty("nodeCount").GetInt32() &&
                index.Metrics.DistinctNonzeroHashes == counts.GetProperty("distinctNonzeroHashes").GetInt32() &&
                index.Metrics.NamedRouteCount == counts.GetProperty("namedRouteCount").GetInt32() &&
                index.Metrics.UnmatchedHashCount == counts.GetProperty("unmatchedHashCount").GetInt32(),
            "Route acquisition differs from the exact-build source-family closure.");
        Require(artifacts[GtaVRouteCorpusIndex.PathsCoordinate].Digest == new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(manifest, "pathsSha256")) &&
                artifacts[GtaVRouteCorpusIndex.PathsCoordinate].ExactBytes.Length == manifest.GetProperty("pathsByteLength").GetInt64() &&
                artifacts[GtaVRouteCorpusIndex.GxtCoordinate].Digest == new ContentDigest(ContentDigest.Sha256Algorithm, RequireText(manifest, "gxtSha256")),
            "Route parent archive or localization differs from the admitted source.");
        Require(artifacts.Count == corpusArtifacts.Length, "Route acquisition contains undeclared extra artifacts.");
        return new ValidatedGtaRouteAcquisition(
            gameVersion,
            observedAt,
            receipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray(),
            artifacts.ToImmutable(),
            corpusArtifacts,
            semanticArtifacts,
            indexBinding,
            indexBytes.ToImmutableArray());
    }

    private static (ImmutableArray<FrozenSourceArtifact> Corpus, ImmutableArray<FrozenSourceArtifact> Semantic)
        SelectCorpusArtifacts(
            ReadOnlySpan<byte> indexBytes,
            ImmutableDictionary<string, FrozenSourceArtifact>.Builder artifacts)
    {
        using var document = JsonDocument.Parse(indexBytes.ToArray());
        var root = document.RootElement;
        var allCoordinates = ImmutableArray.CreateBuilder<string>();
        var semanticCoordinates = ImmutableArray.CreateBuilder<string>();
        foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
        {
            var coordinate = RequireText(artifact, "sourceCoordinate");
            allCoordinates.Add(coordinate);
            semanticCoordinates.Add(coordinate);
        }
        static ImmutableArray<FrozenSourceArtifact> Resolve(
            IEnumerable<string> coordinates,
            ImmutableDictionary<string, FrozenSourceArtifact>.Builder artifacts,
            string description) => coordinates
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(value => artifacts.TryGetValue(value, out var artifact)
                ? artifact
                : throw new InvalidDataException($"The route index references an unacquired {description} artifact."))
            .ToImmutableArray();

        var corpus = Resolve(allCoordinates, artifacts, "corpus");
        var semantic = Resolve(semanticCoordinates, artifacts, "semantic");
        Require(!corpus.IsEmpty && !semantic.IsEmpty,
            "The route index contains no source or semantic artifacts.");
        Require(corpus.Select(value => value.Id).Distinct().Count() == corpus.Length,
            "The route corpus contains digest-identical artifacts at unequal coordinates.");
        return (corpus, semantic);
    }

    public static SemanticAcquisitionProjection NarrowTo(
        ValidatedGtaRouteAcquisition acquisition,
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
        Require(bindings.Length == retained.Count && receipts.Count > 0,
            "Route semantic acquisition closure is incomplete.");
        return new SemanticAcquisitionProjection(
            receipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(), bindings);
    }

    private static void VerifyDocumentDigest(ReadOnlySpan<byte> receiptBytes, JsonElement root)
    {
        var expected = RequireText(root, "receiptDocumentSha256");
        Require(expected.Length == 64 && expected.All(char.IsAsciiHexDigit) &&
                string.Equals(expected, expected.ToLowerInvariant(), StringComparison.Ordinal),
            "Route receipt document digest is not lowercase SHA-256 data.");
        var documentBytes = receiptBytes;
        if (documentBytes.Length > 0 && documentBytes[^1] == (byte)'\n') documentBytes = documentBytes[..^1];
        var encodedProperty = Encoding.UTF8.GetBytes($"\"receiptDocumentSha256\":\"{expected}\",");
        var propertyOffset = documentBytes.IndexOf(encodedProperty);
        Require(propertyOffset >= 0 &&
                documentBytes[(propertyOffset + encodedProperty.Length)..].IndexOf(encodedProperty) < 0,
            "Route receipt digest property is not in canonical root-object form.");
        var unsignedDocument = new byte[documentBytes.Length - encodedProperty.Length];
        documentBytes[..propertyOffset].CopyTo(unsignedDocument);
        documentBytes[(propertyOffset + encodedProperty.Length)..].CopyTo(unsignedDocument.AsSpan(propertyOffset));
        var actual = Convert.ToHexStringLower(SHA256.HashData(unsignedDocument));
        Require(string.Equals(expected, actual, StringComparison.Ordinal),
            "Route receipt document digest mismatch.");
    }

    private static string CanonicalJsonSha256(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
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

    private static string ResolveWithin(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(value => value is "" or "." or ".."))
            throw new InvalidDataException("Route frozen artifact path is not canonical relative data.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Route frozen artifact escapes its immutable bundle.");
        return result;
    }

    private static string RequireText(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"Route receipt {propertyName} is absent or invalid.");
        return property.GetString()!;
    }

    private static void ValidateNoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(seen.Add(property.Name), "Route receipt contains duplicate JSON properties.");
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
