using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;
using Grid.GtaV.Knowledge;
using Grid.GtaV.Enrichment.Knowledge;

internal sealed record SemanticAcquisitionProjection(
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);

internal static class GtaAcquisitionSemanticProjection
{
    /// <summary>Adds the bounded named-route ledger without changing the existing spatial consolidation.</summary>
    public static CanonicalCatalogPayload ApplyPlan2LocationCoverage(CanonicalCatalogPayload payload, KnowledgeSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(scope);
        var routeFamily = new LocationSourceFamilyId(GtaVRouteKnowledgeAdapter.FamilyId);
        if (payload.LocationCoverageReports.Length != 4 ||
            payload.LocationCoverageReports.Any(report => report.Manifest.SourceScope != scope))
            throw new InvalidDataException("Plan 2 Location coverage requires exactly the three established reports and the bounded route report.");
        var routeReports = payload.LocationCoverageReports.Where(report =>
            report.SourceFamilies.Any(family => family.SourceFamilyId == routeFamily)).ToImmutableArray();
        if (routeReports.Length != 1 || routeReports[0].SourceFamilies.Length != 1 ||
            routeReports[0].Manifest.SourceFamilies.Length != 1)
            throw new InvalidDataException("The Plan 2 route coverage family must have one distinct report.");
        var routes = routeReports[0];
        var routeIds = routes.SourceFamilies[0].EmittedLocationRecordIds.ToHashSet();
        if (routes.Hierarchy.SourceProvidedEdgeCount != 0 || !routes.Relationships.IsEmpty ||
            payload.RelationshipAssertions.Any(assertion => routeIds.Contains(assertion.SubjectKnowledgeRecordId)))
            throw new InvalidDataException("The admitted route-label source supplies no geographic or connection relationships.");
        var basePayload = payload with
        {
            KnowledgeRecords = payload.KnowledgeRecords.Where(record => !routeIds.Contains(record.Id)).ToImmutableArray(),
            LocationCoverageReports = payload.LocationCoverageReports.Where(report => report.Id != routes.Id).ToImmutableArray(),
        };
        // This existing helper attaches the population-zone terminology revision, recomputes
        // source-backed MLO edge metrics, and validates exact coverage of every old Location.
        var established = GtaVEnrichmentCoverageProjection.CreateConsolidatedLocationCoverage(basePayload, scope);
        return payload with { LocationCoverageReports = [CombinePlan2LocationReports(payload, scope, established, routes)] };
    }

    internal static LocationCoverageReport CombinePlan2LocationReports(CanonicalCatalogPayload payload,
        KnowledgeSourceScope scope, LocationCoverageReport established, LocationCoverageReport routes)
    {
        if (established.Manifest.SourceScope != scope || routes.Manifest.SourceScope != scope ||
            routes.Hierarchy.SourceProvidedEdgeCount != 0 || !routes.Relationships.IsEmpty)
            throw new InvalidDataException("Plan 2 Location report scope or route relationship contract differs.");
        var declarations = established.Manifest.SourceFamilies.AddRange(routes.Manifest.SourceFamilies);
        var families = established.SourceFamilies.AddRange(routes.SourceFamilies);
        if (families.Select(family => family.SourceFamilyId).Distinct().Count() != families.Length)
            throw new InvalidDataException("Plan 2 Location coverage repeats a source family.");
        var ids = families.SelectMany(family => family.EmittedLocationRecordIds).ToHashSet();
        if (!ids.SetEquals(payload.KnowledgeRecords.Where(record => record.Kind == KnowledgeKind.Location && record.GameId == scope.GameId)
            .Select(record => record.Id)))
            throw new InvalidDataException("Plan 2 Location coverage must account for every canonical Location.");
        var terms = payload.TerminologyAssertions.Where(term => ids.Contains(term.KnowledgeRecordId)).ToImmutableArray();
        var primary = terms.Where(term => term.Role == TerminologyAssertionRole.PrimaryName).ToImmutableArray();
        var named = primary.Select(term => term.KnowledgeRecordId).Distinct().Count();
        var conflicts = primary.GroupBy(term => (term.KnowledgeRecordId, term.LanguageTag))
            .Where(group => group.Select(term => term.VerbatimValue).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key.KnowledgeRecordId).Distinct().Count();
        const string version = "gta-v-enhanced-mapzones-population-mlo-named-routes-v1";
        var validation = established.Manifest.QcsValidation;
        var manifest = new LocationCoverageManifest(
            LocationCoverageManifestId.DeriveV1(scope, version, false, validation, declarations),
            scope, version, false, validation, declarations);
        return LocationCoverageReport.Create(manifest, families,
            established.SemanticCategories.AddRange(routes.SemanticCategories),
            new LocationTerminologyCoverage(ids.Count, named, terms.Count(term => term.Role == TerminologyAssertionRole.Alias), ids.Count - named, conflicts),
            established.Hierarchy, established.Relationships, established.Unresolved.AddRange(routes.Unresolved));
    }

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
