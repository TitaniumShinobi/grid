using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

internal static class GtaVLocationHierarchyRegistrationProofFixtures
{
    public static (CanonicalCatalogPackage Package, GtaVLocationHierarchyCorpusIndex Index) CreateImportedBaselineWithoutHierarchy(
        string repositoryRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        var references = Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references");
        var index = GtaVLocationHierarchyCorpusIndex.LoadFromRepository(references);
        var names = index.RequiredEnglishNames.ToArray();
        var rows = names.Select((name, i) => $"ZONE_{i}, 0, 0, 0, 1, 1, 1, {index.NameCodes[GtaVLocationHierarchyCorpusIndex.ResolveNativeBridgeEnglishName(name)]}, 0").ToList();
        rows.Add($"ZONE_REPEAT, 1, 1, 1, 2, 2, 2, {index.NameCodes[index.ChildNames[0]]}, 0");
        var bytes = Encoding.UTF8.GetBytes("zone\n" + string.Join("\n", rows) + "\nend\n");
        var digest = ContentDigest.ComputeSha256(bytes);
        var artifact = new FrozenSourceArtifact(SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", "PopulationZoneIplSection",
                GtaVPopulationZonesKnowledgeAdapter.Coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1),
            new KnowledgeFormatCoordinate(GtaVPopulationZonesKnowledgeAdapter.FormatId, "1"), bytes.ToImmutableArray(),
            DateTimeOffset.UnixEpoch);
        var nativeAdapter = new GtaVPopulationZonesKnowledgeAdapter(ContentDigest.ComputeSha256("hierarchy-proof"u8));
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture");
        var extraction = nativeAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, nativeAdapter.Descriptor, [artifact]))
            .GetAwaiter().GetResult();
        var scope = KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);
        var language = Frozen(GtaVPopulationZoneTerminologyAdapter.BaseLanguageRpfCoordinate, "Rpf7Container",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId, "1"), "proof-language"u8.ToArray());
        var gxt = Frozen(GtaVPopulationZoneTerminologyAdapter.BaseGxt2Coordinate, "GXT2",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId, "1"),
            CreateGxt2(names.Select(n =>
            {
                var bridge = GtaVLocationHierarchyCorpusIndex.ResolveNativeBridgeEnglishName(n);
                return (GtaVPresentationCorpusIndex.Hash(index.NameCodes[bridge]), bridge);
            }).ToArray()));
        var acquisition = Acquisition(version, [artifact, language, gxt, .. index.Artifacts]);
        var origin = GtaVKnowledgePackageProjection.CreatePayload([extraction], acquisition.Receipts, acquisition.Bindings);
        var terms = new GtaVPopulationZoneTerminologyAdapter(ContentDigest.ComputeSha256("hierarchy-proof"u8))
            .Extract(origin, scope, artifact, language, gxt);
        origin = terms.ApplyTo(origin, acquisition.Receipts, acquisition.Bindings);
        var package = CanonicalCatalogPackageKernel.CreateV6(CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(scope.GameId, version, origin.Artifacts.Select(a => a.Id).ToImmutableArray()), null, [],
            "grid.catalog-composition.v1", origin,
            new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.location-hierarchy.proof", "1",
                ContentDigest.ComputeSha256("proof"u8)),
            new CatalogBuildProvenance(CatalogBuildProvenance.CurrentSchemaVersion, "grid.location-hierarchy.proof", "1",
                new string('c', 40),
                [new CatalogCommittedBuildInput("tests/Grid.Core.Tests/GtaVLocationHierarchyRegistrationProofFixtures.cs",
                    new string('b', 40))]));
        if (!CanonicalCatalogPackageKernel.Verify(package).IsStructurallyValid)
            throw new InvalidDataException("Hierarchy proof baseline package failed verification.");
        return (package, index);
    }

    private static FrozenSourceArtifact Frozen(string coordinate, string type, KnowledgeFormatCoordinate format, byte[] bytes)
    {
        var artifactDigest = ContentDigest.ComputeSha256(bytes);
        return new(SourceArtifactId.DeriveV1(artifactDigest), artifactDigest,
            SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", type, coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8", 1),
            format, bytes.ToImmutableArray(), DateTimeOffset.UnixEpoch);
    }

    private static (ImmutableArray<SourceAcquisitionReceipt> Receipts, ImmutableArray<SourceArtifactAcquisitionBinding> Bindings)
        Acquisition(SourceNativeVersion version, FrozenSourceArtifact[] artifacts)
    {
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220");
        var method = new AcquisitionMethodCoordinate("grid.fixture.acquisition", "1", "grid.fixture", "1",
            ContentDigest.ComputeSha256("fixture"u8));
        var receipts = artifacts.Select(a =>
        {
            ImmutableArray<SourceAcquisitionMember> members =
                [new(a.SourceCoordinate, a.ExactBytes.Length, a.Digest, a.Id)];
            var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, a.SourceCoordinate,
                a.ExactBytes.Length, a.Digest, method, members);
            return new SourceAcquisitionReceipt(id, SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, a.SourceCoordinate,
                a.ExactBytes.Length, a.Digest, method, members);
        }).ToImmutableArray();
        return (receipts, receipts.SelectMany(r => r.Members.Select(m =>
            new SourceArtifactAcquisitionBinding(m.ArtifactId, r.Id, m.MemberCoordinate, m.ByteLength, m.Digest))).ToImmutableArray());
    }

    private static byte[] CreateGxt2(params (uint Hash, string Text)[] entries)
    {
        var ordered = entries.OrderBy(e => e.Hash).ToArray();
        var encoded = ordered.Select(e => Encoding.UTF8.GetBytes(e.Text)).ToArray();
        var tableEnd = 8 + ordered.Length * 8;
        var cursor = tableEnd + 8;
        var result = new byte[cursor + encoded.Sum(e => e.Length + 1)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + i * 8), ordered[i].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12 + i * 8), (uint)cursor);
            encoded[i].CopyTo(result.AsSpan(cursor));
            cursor += encoded[i].Length + 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd), 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd + 4), (uint)cursor);
        return result;
    }
}
