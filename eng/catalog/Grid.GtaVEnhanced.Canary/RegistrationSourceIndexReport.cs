using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class RegistrationSourceIndexReport
{
    public static object WithPlan2(object historicalReport, IEnumerable<Grid.Core.Models.FrozenSourceArtifact> artifacts)
    {
        var report = JsonSerializer.SerializeToNode(historicalReport, CompactJson)!.AsObject();
        report.Remove("contentSha256");
        report["plan2FrozenArtifacts"] = JsonSerializer.SerializeToNode(artifacts
            .DistinctBy(value => value.SourceCoordinate)
            .OrderBy(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
            .Select(value => new
            {
                sourceCoordinate = value.SourceCoordinate.ExactRepresentation,
                artifactId = value.Id.Value,
                contentSha256 = value.Digest.HexValue,
                byteLength = value.ExactBytes.Length,
                observedAtUtc = value.ObservedAtUtc,
                format = value.DeclaredFormat,
            }).ToArray(), CompactJson);
        report["contentSha256"] = Sha256(JsonSerializer.SerializeToUtf8Bytes(report, CompactJson));
        return report;
    }

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public static object Create(ValidatedGtaAcquisition acquisition, string sourceManifestPath)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        var manifestBytes = File.ReadAllBytes(sourceManifestPath);
        using var manifest = JsonDocument.Parse(manifestBytes);
        var families = manifest.RootElement.GetProperty("sourceFamilies").EnumerateArray()
            .ToDictionary(
                value => RequireText(value, "sourceFamilyId"),
                value => RequireText(value, "status"),
                StringComparer.Ordinal);
        var declarations = ReadDeclarations(manifest.RootElement);
        var bindingByArtifact = acquisition.Bindings.ToDictionary(value => value.ArtifactId);
        var entries = acquisition.Members.Values
            .OrderBy(value => value.Coordinate, StringComparer.Ordinal)
            .Select(member =>
            {
                var declaration = declarations.SingleOrDefault(value => value.Matches(member.Coordinate)) ??
                    throw new InvalidDataException($"Acquired member is undeclared by the source-family manifest: {member.Coordinate}");
                if (!bindingByArtifact.TryGetValue(member.ArtifactId, out var binding))
                    throw new InvalidDataException($"Acquired member has no immutable acquisition binding: {member.Coordinate}");
                return new
                {
                    memberCoordinate = member.Coordinate,
                    sourceFamilyId = declaration.SourceFamilyId,
                    sourceFamilyStatus = families[declaration.SourceFamilyId],
                    formatId = declaration.FormatId,
                    exactFormatVersion = declaration.FormatVersion,
                    artifactId = member.ArtifactId.Value,
                    sha256 = member.Digest.HexValue,
                    byteLength = member.ByteLength,
                    acquisitionReceiptId = binding.AcquisitionReceiptId.Value,
                };
            }).ToArray();
        var body = new
        {
            schemaVersion = 1,
            gameId = "game.grandtheftautov-enhanced",
            exactGameVersion = acquisition.GameVersion.ExactRepresentation,
            sourceFamilyManifestSha256 = Sha256(manifestBytes),
            acquisitionReceiptIds = acquisition.Receipts.Select(value => value.Id.Value)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            frozenArtifacts = entries,
        };
        var digest = Sha256(JsonSerializer.SerializeToUtf8Bytes(body, CompactJson));
        return new
        {
            body.schemaVersion,
            contentSha256 = digest,
            body.gameId,
            body.exactGameVersion,
            body.sourceFamilyManifestSha256,
            body.acquisitionReceiptIds,
            body.frozenArtifacts,
        };
    }

    private static ImmutableArray<SourceDeclaration> ReadDeclarations(JsonElement root)
    {
        var values = ImmutableArray.CreateBuilder<SourceDeclaration>();
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            var containerCoordinate = RequireText(container, "containerCoordinate");
            if (container.TryGetProperty("members", out var members))
                foreach (var member in members.EnumerateArray())
                    values.Add(new SourceDeclaration(
                        containerCoordinate + "!/" + RequireText(member, "memberPath"),
                        null,
                        RequireText(member, "sourceFamilyId"),
                        RequireText(member, "formatId"),
                        RequireText(member, "exactFormatVersion")));
            if (container.TryGetProperty("dynamicMembers", out var dynamicMembers))
                values.Add(new SourceDeclaration(
                    containerCoordinate + "!/" + RequireText(dynamicMembers, "prefix"),
                    RequireText(dynamicMembers, "suffix"),
                    RequireText(dynamicMembers, "sourceFamilyId"),
                    RequireText(dynamicMembers, "formatId"),
                    RequireText(dynamicMembers, "exactFormatVersion")));
        }
        return values.ToImmutable();
    }

    private static string RequireText(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The GTA source-family manifest {propertyName} is absent or invalid.");
        return property.GetString()!;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record SourceDeclaration(
        string CoordinateOrPrefix,
        string? Suffix,
        string SourceFamilyId,
        string FormatId,
        string FormatVersion)
    {
        public bool Matches(string coordinate) => Suffix is null
            ? string.Equals(CoordinateOrPrefix, coordinate, StringComparison.Ordinal)
            : coordinate.StartsWith(CoordinateOrPrefix, StringComparison.Ordinal) &&
              coordinate.EndsWith(Suffix, StringComparison.Ordinal);
    }
}
