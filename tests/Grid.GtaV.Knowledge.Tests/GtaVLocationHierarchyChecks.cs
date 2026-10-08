using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

internal static class GtaVLocationHierarchyChecks
{
    public static int Run()
    {
        var root = GitBuildProvenanceResolver.FindRepositoryRoot();
        var references = Path.Combine(root, "scripts/games/grandtheftautov/catalog/references");
        var index = GtaVLocationHierarchyCorpusIndex.LoadFromRepository(references);
        Require(index.ExpectedRelationshipCount == 36, "Pinned v2 catalog registration relationship count");
        Require(index.ExpectedPackageAssertionRelationshipCount == 36, "Pinned v2 package assertion relationship count");
        Require(index.ParentName == "Vinewood" && index.ChildNames.Length == 4, "Pinned enumeration");
        Require(index.NameCodes[index.ParentName] == "VINE", "Native parent bridge");
        Require(index.ChildNames.Select(x => index.NameCodes[x]).Order().SequenceEqual(new[] { "CHIL", "DTVINE", "EAST_V", "WVINE" }), "Four unique native children");
        var wiki = File.ReadAllBytes(Path.Combine(references, "vinewood-411759.normalized.txt"));
        var table = File.ReadAllBytes(Path.Combine(references, "cfx-zones-ad60ae80.md"));
        var losSantos = File.ReadAllBytes(Path.Combine(references, "los-santos-419777.normalized.txt"));
        var grandSenora = File.ReadAllBytes(Path.Combine(references, "grand-senora-desert-395939.normalized.txt"));
        var eastLosSantos = File.ReadAllBytes(Path.Combine(references, "east-los-santos-409804.normalized.txt"));
        var blaineCounty = File.ReadAllBytes(Path.Combine(references, "blaine-county-406414.normalized.txt"));
        var sanAndreas = File.ReadAllBytes(Path.Combine(references, "san-andreas-416219.normalized.txt"));
        var vespucci = File.ReadAllBytes(Path.Combine(references, "vespucci-396784.normalized.txt"));
        var joinIndex = File.ReadAllBytes(Path.Combine(references, "geography-containment.v1.normalized.txt"));
        Reject(() => new GtaVLocationHierarchyCorpusIndex(wiki.Concat(new byte[] { 32 }).ToArray(), table, losSantos, grandSenora, eastLosSantos, blaineCounty, sanAndreas, vespucci, joinIndex, 12));
        Reject(() => new GtaVLocationHierarchyCorpusIndex(wiki, table.Concat(new byte[] { 32 }).ToArray(), losSantos, grandSenora, eastLosSantos, blaineCounty, sanAndreas, vespucci, joinIndex, 12));
        Reject(() => new GtaVLocationHierarchyCorpusIndex(wiki, table, losSantos, grandSenora, eastLosSantos, blaineCounty, sanAndreas, vespucci.Concat(new byte[] { 32 }).ToArray(), joinIndex, 36));
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(vespucci, "Vespucci", "neighborhood-in-city", "Los Santos", out var vespucciChild)
                && vespucciChild.SequenceEqual(ImmutableArray.Create("Vespucci"), StringComparer.Ordinal)
                && !GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(vespucci, "Vespucci", "neighborhood-in-city", "San Andreas", out _),
            "Vespucci neighborhood lead sentence names Los Santos as its only container");
        Reject(() => GtaVLocationHierarchyCorpusIndex.ParseEnumeration("No enumerative evidence."));
        var sentence = "North , South , East , and West are the neighbourhoods that comprise of the larger Central district.";
        Reject(() => GtaVLocationHierarchyCorpusIndex.ParseEnumeration(sentence + "\n" + sentence));
        Reject(() => GtaVLocationHierarchyCorpusIndex.ParseEnumeration("North , North , East , and West are the neighbourhoods that comprise of the larger Central district."));
        Require(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantos, "Downtown Los Santos", "divided-neighbourhoods", "Vespucci", out _),
            "False join-index parent Vespucci rejected for Downtown Los Santos upstream section");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantos, "Downtown Los Santos", "divided-neighbourhoods", "Downtown", out var downtownChildren)
                && downtownChildren.SequenceEqual(new[] { "Pillbox Hill", "Mission Row", "Textile City", "Legion Square" }, StringComparer.Ordinal),
            "Downtown divided-neighbourhoods upstream establishes parent against section subject and child list");
        Require(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenora, "Grand Senora Desert", "town-located-inside-desert", "Grand Senora", out _),
            "Shortened desert parent Grand Senora rejected for town-located-inside-desert grammar");
        Require(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenora, "Grand Senora Desert", "town-located-inside-desert", "Grand", out _),
            "Prefix desert parent Grand rejected for town-located-inside-desert grammar");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenora, "Grand Senora Desert", "town-located-inside-desert", "Grand Senora Desert", out var sandyShoresChild)
                && sandyShoresChild.SequenceEqual(ImmutableArray.Create("Sandy Shores"), StringComparer.Ordinal),
            "Complete desert parent Grand Senora Desert locates Sandy Shores upstream child");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantos, "Los Santos", "city-located-within-municipality", "Los Santos", out var davisChild)
                && davisChild.SequenceEqual(ImmutableArray.Create("Davis"), StringComparer.Ordinal),
            "Los Santos intro locates Davis city-located-within-municipality upstream child");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(eastLosSantos, "Neighbourhoods", "page-section-member-list", "East Los Santos", out var eastLosChildren)
                && eastLosChildren.Length == 7,
            "East Los Santos Neighbourhoods section locates seven upstream members");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(blaineCounty, "Settlements", "page-section-member-list", "Blaine County", out var blaineSettlements)
                && blaineSettlements.Length == 8,
            "Blaine County Settlements section locates eight upstream members");
        Require(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(sanAndreas, "Southern San Andreas / Los Santos County", "county-section-member-list", "Los Santos County", out var losSantosCountyChildren)
                && losSantosCountyChildren.SequenceEqual(new[] { "Los Santos", "City of Davis", "Chumash" }, StringComparer.Ordinal),
            "San Andreas Los Santos County section locates county member list");
        Require(index.GeographyPublicationReady && index.GeographyBridgeBlockedRows.IsEmpty,
            "Reference geography join-index rows correlate without ambiguous native bridge blockers");
        Require(GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision(GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate) == "395939",
            "Grand Senora Desert pinned reference revision");
        Require(GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision(GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate) == "409804",
            "East Los Santos pinned reference revision");
        Require(GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision(GtaVLocationHierarchyCorpusIndex.BlaineCountyCoordinate) == "406414",
            "Blaine County pinned reference revision");
        Require(GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision(GtaVLocationHierarchyCorpusIndex.SanAndreasCoordinate) == "416219",
            "San Andreas pinned reference revision");
        Require(GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision(GtaVLocationHierarchyCorpusIndex.VespucciCoordinate) == "396784",
            "Vespucci pinned reference revision");
        Reject(() => GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision("https://example.com/not-pinned"));
        var ambiguous = GtaVLocationHierarchyCorpusIndex.ParseNativeTable("| 1 | Z1 | A | Same |\n| 2 | Z2 | B | Same |\n| 3 | Z3 | C | Unique |\n");
        Require(!ambiguous.ContainsKey("Same") && ambiguous["Unique"] == "C", "Ambiguous name join excluded");
        var names = index.RequiredEnglishNames.ToArray();
        var rows = names.Select((name, i) => $"ZONE_{i}, 0, 0, 0, 1, 1, 1, {index.NameCodes[GtaVLocationHierarchyCorpusIndex.ResolveNativeBridgeEnglishName(name)]}, 0").ToList();
        rows.Add($"ZONE_REPEAT, 1, 1, 1, 2, 2, 2, {index.NameCodes[index.ChildNames[0]]}, 0");
        var bytes = Encoding.UTF8.GetBytes("zone\n" + string.Join("\n", rows) + "\nend\n");
        var digest = ContentDigest.ComputeSha256(bytes);
        var artifact = new FrozenSourceArtifact(SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", "PopulationZoneIplSection", GtaVPopulationZonesKnowledgeAdapter.Coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1),
            new KnowledgeFormatCoordinate(GtaVPopulationZonesKnowledgeAdapter.FormatId, "1"), bytes.ToImmutableArray(), DateTimeOffset.UnixEpoch);
        var nativeAdapter = new GtaVPopulationZonesKnowledgeAdapter(ContentDigest.ComputeSha256("hierarchy-fixture"u8));
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture");
        var extraction = nativeAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, nativeAdapter.Descriptor, [artifact])).GetAwaiter().GetResult();
        var scope = KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);
        var language = Frozen(GtaVPopulationZoneTerminologyAdapter.BaseLanguageRpfCoordinate, "Rpf7Container",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId, "1"), "fixture-language-container"u8.ToArray());
        var gxt = Frozen(GtaVPopulationZoneTerminologyAdapter.BaseGxt2Coordinate, "GXT2",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId, "1"),
            CreateGxt2(names.Select(n =>
            {
                var bridge = GtaVLocationHierarchyCorpusIndex.ResolveNativeBridgeEnglishName(n);
                return (GtaVPresentationCorpusIndex.Hash(index.NameCodes[bridge]), bridge);
            }).ToArray()));
        var acquisition = Acquisition(version, [artifact, language, gxt, .. index.Artifacts]);
        var origin = GtaVKnowledgePackageProjection.CreatePayload([extraction], acquisition.Receipts, acquisition.Bindings);
        var terms = new GtaVPopulationZoneTerminologyAdapter(ContentDigest.ComputeSha256("hierarchy-fixture"u8)).Extract(origin, scope, artifact, language, gxt);
        origin = terms.ApplyTo(origin, acquisition.Receipts, acquisition.Bindings);
        var batch = new GtaVLocationHierarchySecondaryAssertionAdapter(ContentDigest.ComputeSha256("hierarchy-fixture"u8), index)
            .Extract(origin, KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version));
        Require(batch.RelationshipAssertions.Length == index.ExpectedPackageAssertionRelationshipCount, "Full hierarchy package relationships");
        Require(batch.TerminologyAssertions.Length == batch.AdditionalKnowledgeRecords.Length, "Reference geography terminology accompanies materialized records");
        Require(batch.AdditionalKnowledgeRecords.All(r =>
                r.NativeIdentity.Namespace == GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace &&
                r.NativeIdentity.ObjectType == GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeObjectType &&
                batch.TerminologyAssertions.Any(t => t.KnowledgeRecordId == r.Id && t.VerbatimValue == r.NativeIdentity.ExactRepresentation)),
            "Reference geography identities use EnglishPrimaryName exact representation");
        var repeated = origin.KnowledgeRecords.Single(r => r.NativeIdentity.ExactRepresentation == index.NameCodes[index.ChildNames[0]]);
        var identities = origin.EvidenceBindings.Where(b => b.KnowledgeRecordId == repeated.Id && b.ClaimKind == EvidenceClaimKind.KnowledgeIdentity).ToArray();
        var assertion = batch.CrossSourceAssertions.Single(a => a.TargetKnowledgeRecordId == repeated.Id);
        Require(identities.Length == 2 && identities.All(b => assertion.SupportingEvidenceBindingIds.Contains(b.Id)), "All repeated native occurrence receipts remain in relationship closure");
        Require(batch.EvidenceBindings.Where(b => b.ClaimKind == EvidenceClaimKind.Relationship).All(b => batch.ReferenceEvidenceReceipts.Any(r => r.Id == b.EvidenceReceiptId)), "Reference hierarchy is not promoted to file verified");
        Require(batch.TargetLinkClaims.All(link => batch.CrossSourceAssertions.Any(a => a.TargetLinkClaimId == link.Id)), "No orphan parent link");
        Require(batch.ReferenceEvidenceReceipts.Where(r => r.Receipt.ResponseArtifactId == index.NativeTable.Id).All(r => r.Receipt.ResponseFieldPath == "Zones"), "Truthful source-owned native table locator");
        var relationshipContents = batch.RelationshipAssertions.Select(r => EvidenceClaimContentId.DeriveV1(r).Value).ToHashSet(StringComparer.Ordinal);
        var nativeRelationshipAssertions = batch.CrossSourceAssertions
            .Where(a => relationshipContents.Contains(a.UnderlyingClaimContentId.Value) &&
                        batch.RelationshipAssertions.Any(r => EvidenceClaimContentId.DeriveV1(r) == a.UnderlyingClaimContentId &&
                            origin.KnowledgeRecords.Any(k => k.Id == r.SubjectKnowledgeRecordId)))
            .ToArray();
        Require(nativeRelationshipAssertions.All(a => a.SupportingEvidenceReceiptIds.Any(id => batch.ReferenceEvidenceReceipts.Any(r => r.Id == id && r.Receipt.ResponseArtifactId == index.NativeTable.Id)) &&
            a.SupportingEvidenceReceiptIds.Any(id => batch.ReferenceEvidenceReceipts.Any(r => r.Id == id && (r.Receipt.ResponseArtifactId == index.Wiki.Id || r.Receipt.ResponseArtifactId == index.LosSantosWiki.Id || r.Receipt.ResponseArtifactId == index.GrandSenoraWiki.Id || r.Receipt.ResponseArtifactId == index.EastLosSantosWiki.Id || r.Receipt.ResponseArtifactId == index.BlaineCountyWiki.Id || r.Receipt.ResponseArtifactId == index.SanAndreasWiki.Id || r.Receipt.ResponseArtifactId == index.VespucciWiki.Id)))),
            "Native-bridged relationship assertions retain both native bridge and containment reference legs");
        var payload = batch.ApplyTo(origin, acquisition.Receipts, acquisition.Bindings);
        var package = CanonicalCatalogPackageKernel.CreateV6(CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(scope.GameId, version, payload.Artifacts.Select(a => a.Id).ToImmutableArray()), null, [], "grid.catalog-composition.v1", payload,
            new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.location-hierarchy.fixture", "1", ContentDigest.ComputeSha256("fixture"u8)),
            new CatalogBuildProvenance(CatalogBuildProvenance.CurrentSchemaVersion, "grid.location-hierarchy.tests", "1", new string('a', 40),
                [new CatalogCommittedBuildInput("src/Grid.GtaV.Knowledge/GtaVLocationHierarchySecondaryAssertionAdapter.cs", new string('b', 40))]));
        Require(CanonicalCatalogPackageKernel.Verify(package).IsStructurallyValid, "Full hierarchy package/evidence verification");
        var referenceRecord = payload.KnowledgeRecords.First(r => KnowledgeRecordId.IsRegistrationEntityBacked(r.Id.Value));
        Require(string.Equals(
                referenceRecord.Id.Value,
                KnowledgeRecordId.DeriveRegistrationBackedLocationEntityId(referenceRecord),
                StringComparison.Ordinal),
            "Registration-backed location record id matches canonical identity rederivation");
        Require(!KnowledgeRecordId.MatchesPackageIdentity(referenceRecord with
        {
            Id = new KnowledgeRecordId(KnowledgeRecordId.RegistrationEntityPrefix + new string('f', 64)),
        }), "Adversarial registration entity id with valid prefix is rejected");
        Require(origin.KnowledgeRecords.All(r => payload.KnowledgeRecords.Contains(r)), "Established native identities preserved");
        Require(origin.TerminologyAssertions.All(t => payload.TerminologyAssertions.Contains(t)), "Established terminology preserved");
        Require(payload.KnowledgeRecords.Length == origin.KnowledgeRecords.Length + batch.AdditionalKnowledgeRecords.Length, "Reference geography records merged into payload");
        Require(payload.RelationshipAssertions.Count(r => r.SemanticId == LocationRelationshipSemantics.ContainedBy && r.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal)) == 36, "Importable package carries full hierarchy relationship set");
        var fixtureRoot = Path.Combine(root, ".tmp", "grid-location-prepared-20261002", "hierarchy-import-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var store = new JsonCanonicalKnowledgeCatalogStore(Path.Combine(fixtureRoot, "catalog.json"));
        var imported = store.ImportPackageAsync(0, package).GetAwaiter().GetResult();
        Require(imported.Status == CanonicalCatalogImportStatus.Imported, "Full fixture store import: " + imported.Detail);
        var reloaded = store.LoadAsync().GetAwaiter().GetResult();
        Require(reloaded.IsValid && reloaded.Snapshot.FindImportedPackage(package.Id) is not null, "Reload verified hierarchy package");
        Console.WriteLine("PASS: 40 focused hierarchy checks including full package, store import and reload verification.");
        return 40;
    }
    private static FrozenSourceArtifact Frozen(string coordinate, string type, KnowledgeFormatCoordinate format, byte[] bytes)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new(SourceArtifactId.DeriveV1(digest), digest, SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", type, coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1),
            format, bytes.ToImmutableArray(), DateTimeOffset.UnixEpoch);
    }
    private static (ImmutableArray<SourceAcquisitionReceipt> Receipts, ImmutableArray<SourceArtifactAcquisitionBinding> Bindings) Acquisition(SourceNativeVersion version, FrozenSourceArtifact[] artifacts)
    {
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220");
        var method = new AcquisitionMethodCoordinate("grid.fixture.acquisition", "1", "grid.fixture", "1", ContentDigest.ComputeSha256("fixture"u8));
        var receipts = artifacts.Select(a =>
        {
            ImmutableArray<SourceAcquisitionMember> members = [new(a.SourceCoordinate, a.ExactBytes.Length, a.Digest, a.Id)];
            var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, a.SourceCoordinate, a.ExactBytes.Length, a.Digest, method, members);
            return new SourceAcquisitionReceipt(id, SourceAcquisitionReceipt.CurrentSchemaVersion, ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, a.SourceCoordinate, a.ExactBytes.Length, a.Digest, method, members);
        }).ToImmutableArray();
        return (receipts, receipts.SelectMany(r => r.Members.Select(m => new SourceArtifactAcquisitionBinding(m.ArtifactId, r.Id, m.MemberCoordinate, m.ByteLength, m.Digest))).ToImmutableArray());
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
            encoded[i].CopyTo(result.AsSpan(cursor)); cursor += encoded[i].Length + 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd), 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd + 4), (uint)cursor);
        return result;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Expected fail-closed rejection."); }
}
