using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

internal sealed record SemanticAcquisitionProjection(
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);

internal static class GtaAcquisitionSemanticProjection
{
    public static SemanticAcquisitionProjection Create(
        ValidatedGtaAcquisition acquisition,
        string sourceManifestPath)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        _ = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity(sourceManifestPath);
        var diagnosticCoordinates = ReadDiagnosticCoordinates(sourceManifestPath);
        var diagnosticArtifactIds = acquisition.Members.Values
            .Where(value => diagnosticCoordinates.Contains(value.Coordinate))
            .Select(value => value.ArtifactId)
            .ToHashSet();
        if (diagnosticArtifactIds.Count != diagnosticCoordinates.Count)
            throw new InvalidDataException("The acquired diagnostic-source closure does not match the source-family manifest.");

        var replacementIds = new Dictionary<SourceAcquisitionReceiptId, SourceAcquisitionReceiptId>();
        var receipts = acquisition.Receipts.Select(receipt =>
        {
            var members = receipt.Members
                .Where(value => !diagnosticArtifactIds.Contains(value.ArtifactId))
                .ToImmutableArray();
            if (members.Length == receipt.Members.Length)
            {
                replacementIds.Add(receipt.Id, receipt.Id);
                return receipt;
            }
            if (members.IsEmpty)
                throw new InvalidDataException("A diagnostic-only acquisition container cannot enter the semantic package boundary.");
            var id = SourceAcquisitionReceiptId.DeriveV1(
                receipt.ReceiptSchemaVersion,
                receipt.GameId,
                receipt.DistributionApplicationIdentity,
                receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate,
                receipt.ContainerByteLength,
                receipt.ContainerDigest,
                receipt.AcquisitionMethod,
                members);
            replacementIds.Add(receipt.Id, id);
            return new SourceAcquisitionReceipt(
                id,
                receipt.ReceiptSchemaVersion,
                receipt.GameId,
                receipt.DistributionApplicationIdentity,
                receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate,
                receipt.ContainerByteLength,
                receipt.ContainerDigest,
                receipt.AcquisitionMethod,
                members);
        }).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();

        var bindings = acquisition.Bindings
            .Where(value => !diagnosticArtifactIds.Contains(value.ArtifactId))
            .Select(value => new SourceArtifactAcquisitionBinding(
                value.ArtifactId,
                replacementIds[value.AcquisitionReceiptId],
                value.MemberCoordinate,
                value.MemberByteLength,
                value.MemberDigest))
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new SemanticAcquisitionProjection(receipts, bindings);
    }

    private static HashSet<string> ReadDiagnosticCoordinates(string sourceManifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(sourceManifestPath));
        var diagnosticFamilies = document.RootElement.GetProperty("sourceFamilies").EnumerateArray()
            .Where(value => string.Equals(
                value.GetProperty("status").GetString(), "diagnostic", StringComparison.Ordinal))
            .Select(value => value.GetProperty("sourceFamilyId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in document.RootElement.GetProperty("containers").EnumerateArray())
        {
            var containerCoordinate = container.GetProperty("containerCoordinate").GetString()!;
            if (!container.TryGetProperty("members", out var members)) continue;
            foreach (var member in members.EnumerateArray())
                if (diagnosticFamilies.Contains(member.GetProperty("sourceFamilyId").GetString()!))
                    coordinates.Add(containerCoordinate + "!/" + member.GetProperty("memberPath").GetString());
        }
        if (coordinates.Count == 0)
            throw new InvalidDataException("The GTA source-family manifest declares no bounded diagnostic members.");
        return coordinates;
    }
}
