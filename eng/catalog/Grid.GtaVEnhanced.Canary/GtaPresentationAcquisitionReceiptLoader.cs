using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal sealed record ValidatedGtaPresentationAcquisition(SourceNativeVersion GameVersion, DateTimeOffset ObservedAtUtc,
    ImmutableArray<SourceAcquisitionReceipt> Receipts, ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
    ImmutableArray<FrozenSourceArtifact> Artifacts);

internal static class GtaPresentationAcquisitionReceiptLoader
{
    public static async Task<ValidatedGtaPresentationAcquisition> LoadAsync(string receiptPath, string manifestPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(receiptPath, cancellationToken));
        using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
        var root = document.RootElement; var manifest = manifestDocument.RootElement;
        Validate(root); Validate(manifest);
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("schemaId").GetString() == "grid.gta-v.presentation-acquisition", "Invalid presentation receipt schema.");
        Require(root.GetProperty("gameId").GetString() == ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value && root.GetProperty("gameVersion").GetString() == "25261616", "Wrong presentation game/build.");
        Require(HashJson(root, "contentSha256") == root.GetProperty("contentSha256").GetString(), "Presentation receipt digest mismatch.");
        Require(HashJson(manifest) == root.GetProperty("manifestSha256").GetString(), "Presentation manifest binding mismatch.");
        var observed = root.GetProperty("observedAtUtc").GetDateTimeOffset();
        Require(observed.Offset == TimeSpan.Zero, "Presentation observation must be UTC.");
        var expected = new Dictionary<string, (string Digest, bool Reference)>(StringComparer.Ordinal);
        var container = manifest.GetProperty("languageContainer");
        var containerCoordinate = container.GetProperty("coordinate").GetString()!;
        Require(root.GetProperty("languageContainer").GetString() == containerCoordinate && root.GetProperty("languageContainerSha256").GetString() == container.GetProperty("sha256").GetString(), "Presentation language container mismatch.");
        foreach (var member in container.GetProperty("members").EnumerateArray()) expected.Add(containerCoordinate + "!/" + member.GetProperty("path").GetString(), (member.GetProperty("sha256").GetString()!, false));
        foreach (var reference in manifest.GetProperty("references").EnumerateArray())
        {
            var coordinate = reference.TryGetProperty("url", out var url) ? url.GetString()! : "https://raw.githubusercontent.com/" + reference.GetProperty("repository").GetString() + "/" + reference.GetProperty("revision").GetString() + "/" + reference.GetProperty("path").GetString();
            expected.Add(coordinate, (reference.GetProperty("sha256").GetString()!, true));
        }
        var artifacts = ImmutableArray.CreateBuilder<FrozenSourceArtifact>();
        var members = ImmutableArray.CreateBuilder<SourceAcquisitionMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("artifacts").EnumerateArray())
        {
            var coordinate = row.GetProperty("coordinate").GetString()!;
            Require(expected.TryGetValue(coordinate, out var pin) && seen.Add(coordinate), "Unapproved/duplicate presentation artifact coordinate.");
            Require(row.GetProperty("sha256").GetString() == pin.Digest && row.GetProperty("evidenceClass").GetString() == (pin.Reference ? "REFERENCE_VERIFIED" : "FILE_VERIFIED"), "Presentation pin/provenance mismatch.");
            var relative = row.GetProperty("relativePath").GetString()!;
            Require(!Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Contains('\\') && relative.Split('/').All(x => x is not "" and not "." and not ".."), "Invalid frozen presentation path.");
            var path = Path.GetFullPath(Path.Combine(rootPath, relative));
            Require(path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Frozen presentation path escaped bundle.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var digest = ContentDigest.ComputeSha256(bytes);
            Require(bytes.LongLength == row.GetProperty("byteLength").GetInt64() && digest == new ContentDigest(ContentDigest.Sha256Algorithm, pin.Digest), "Presentation frozen bytes mismatch.");
            var format = pin.Reference ? GtaVPresentationCorpusIndex.ReferenceFormatId : GtaVPresentationCorpusIndex.GxtFormatId;
            Require(row.GetProperty("formatId").GetString() == format, "Presentation source format mismatch.");
            var native = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", pin.Reference ? "PinnedReference" : "Gxt2", coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1);
            var artifact = new FrozenSourceArtifact(SourceArtifactId.DeriveV1(digest), digest, native, new KnowledgeFormatCoordinate(format, "1"), bytes.ToImmutableArray(), observed);
            artifacts.Add(artifact);
            if (!pin.Reference) members.Add(new SourceAcquisitionMember(native, bytes.LongLength, digest, artifact.Id));
        }
        Require(seen.Count == expected.Count, "Presentation source set is incomplete.");
        var gameVersion = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "25261616", "valve.steam.build-id.exact-utf8", 1);
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220", "valve.steam.app-id.exact-utf8", 1);
        var containerNative = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", containerCoordinate, "grid.gta-v.container-coordinate.exact-utf8", 1);
        var length = root.GetProperty("languageContainerByteLength").GetInt64();
        var containerDigest = new ContentDigest(ContentDigest.Sha256Algorithm, root.GetProperty("languageContainerSha256").GetString()!);
        var method = new AcquisitionMethodCoordinate("grid.gta-v.presentation.frozen-rpf-members", "1", "grid.gta-v.presentation-acquire", "1", new ContentDigest(ContentDigest.Sha256Algorithm, root.GetProperty("acquisitionScriptSha256").GetString()!));
        var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, gameVersion, containerNative, length, containerDigest, method, members.ToImmutable());
        var receipt = new SourceAcquisitionReceipt(id, SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, gameVersion, containerNative, length, containerDigest, method, members.ToImmutable());
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>(); receipts.Add(receipt);
        var bindings = members.Select(x => new SourceArtifactAcquisitionBinding(x.ArtifactId, id, x.MemberCoordinate, x.ByteLength, x.Digest)).ToImmutableArray().ToBuilder();
        var referenceMethod = new AcquisitionMethodCoordinate("grid.gta-v.presentation.pinned-reference-snapshot", "1", "grid.gta-v.presentation-acquire", "1", new ContentDigest(ContentDigest.Sha256Algorithm, root.GetProperty("acquisitionScriptSha256").GetString()!));
        foreach (var artifact in artifacts.Where(x => x.DeclaredFormat.FormatId == GtaVPresentationCorpusIndex.ReferenceFormatId))
        {
            // Acquisition identifies captured bytes; it does not promote reference assertions to FILE_VERIFIED.
            ImmutableArray<SourceAcquisitionMember> referenceMembers = [new(artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, artifact.Id)];
            var referenceId = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, gameVersion, artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, referenceMethod, referenceMembers);
            receipts.Add(new(referenceId, SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, gameVersion, artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, referenceMethod, referenceMembers));
            bindings.Add(new(artifact.Id, referenceId, artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest));
        }
        return new(gameVersion, observed, receipts.ToImmutable(), bindings.ToImmutable(), artifacts.ToImmutable());
    }
    private static string HashJson(JsonElement element, string? exclude = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) Write(writer, element, exclude);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value, string? exclude = null)
    {
        if (value.ValueKind == JsonValueKind.Object) { writer.WriteStartObject(); foreach (var property in value.EnumerateObject().Where(x => x.Name != exclude).OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); Write(writer, property.Value); } writer.WriteEndObject(); }
        else if (value.ValueKind == JsonValueKind.Array) { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { Require(seen.Add(property.Name), "Duplicate presentation JSON property."); Validate(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Validate(item);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
