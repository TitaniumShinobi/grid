using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVSecondaryAssertionChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var observed = new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero);
        var digest = ContentDigest.ComputeSha256("gta-secondary-adapter-fixture"u8);
        var originAdapter = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, digest);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture-build");
        var weapons = Frozen(
            "common.rpf!/data/ai/weapons.meta",
            "CWeaponInfoBlob",
            GtaVWeaponsMetaKnowledgeAdapter.Format,
            Encoding.UTF8.GetBytes("""
                <CWeaponInfoBlob>
                <SlotNavigateOrder><Item><WeaponSlots>
                  <Item><OrderNumber value="10"/><Entry>SLOT_PISTOL</Entry></Item>
                  <Item><OrderNumber value="20"/><Entry>SLOT_NAMELESS</Entry></Item>
                </WeaponSlots></Item></SlotNavigateOrder>
                <SlotBestOrder><WeaponSlots>
                  <Item><OrderNumber value="30"/><Entry>SLOT_PISTOL</Entry></Item>
                </WeaponSlots></SlotBestOrder>
                <Infos>
                  <Item type="CWeaponInfo"><Name>WEAPON_PISTOL</Name><HumanNameHash>WT_PIST</HumanNameHash><WheelSlot>WHEEL_PISTOL</WheelSlot><Group>GROUP_PISTOL</Group><Slot>SLOT_PISTOL</Slot><AmmoInfo ref="AMMO_PISTOL" /></Item>
                  <Item type="CWeaponInfo"><Name>WEAPON_NAMELESS</Name><HumanNameHash>WT_INVALID</HumanNameHash><WheelSlot>WHEEL_PISTOL</WheelSlot><Group>GROUP_PISTOL</Group><Slot>SLOT_NAMELESS</Slot><AmmoInfo ref="AMMO_PISTOL" /></Item>
                  <Item type="CAmmoInfo"><Name>AMMO_PISTOL</Name></Item>
                </Infos></CWeaponInfoBlob>
                """), observed);
        var extraction = await originAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, originAdapter.Descriptor, [weapons]));
        var actorArtifact = Frozen(
            "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta",
            "Gen9ExclusiveAssetsDataPeds",
            new KnowledgeFormatCoordinate(
                GtaVGen9PedsKnowledgeAdapter.FormatId,
                GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion),
            Encoding.UTF8.GetBytes("""
                <Gen9ExclusiveAssetsDataPeds><PedData>
                  <Item><PedModelName>mp_m_freemode_01</PedModelName><DLCData>
                    <Item><dlcName>mp_m_g9ec</dlcName></Item><Item><dlcName>mp_m_2025_02</dlcName></Item>
                  </DLCData></Item>
                  <Item><PedModelName>mp_f_freemode_01</PedModelName><DLCData>
                    <Item><dlcName>mp_f_g9ec</dlcName></Item><Item><dlcName>mp_f_2025_02</dlcName></Item>
                  </DLCData></Item>
                </PedData></Gen9ExclusiveAssetsDataPeds>
                """), observed);
        var actorAdapter = new GtaVGen9PedsKnowledgeAdapter(digest);
        var actorExtraction = await actorAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, actorAdapter.Descriptor, [actorArtifact]));

        var languageRpf = Frozen(
            "x64b.rpf!/data/lang/american_rel.rpf", "Rpf7Container",
            new KnowledgeFormatCoordinate(
                GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion),
            "exact language rpf fixture bytes"u8.ToArray(), observed);
        var gxt = Frozen(
            "x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2", "GXT2",
            new KnowledgeFormatCoordinate(
                GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion),
            CreateGxt2((Joaat("WT_PIST"), "Pistol")), observed);
        var acquisition = CreateAcquisition(version, weapons, actorArtifact, languageRpf, gxt);
        var origin = GtaVKnowledgePackageProjection.CreatePayload(
            [extraction, actorExtraction], acquisition.Receipts, acquisition.Bindings);
        var originIds = origin.KnowledgeRecords.Select(value => value.Id).ToImmutableArray();
        var adapter = new GtaVWeaponsSecondaryAssertionAdapter(digest);
        var secondary = adapter.Extract(
            origin,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            weapons, languageRpf, gxt);
        var weaponPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            origin, secondary, acquisition.Receipts, acquisition.Bindings);
        var organizationAdapter = new GtaVWeaponsOrganizationSecondaryAssertionAdapter(digest);
        var organization = organizationAdapter.Extract(
            weaponPayload,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            weapons);
        var organizationReplay = organizationAdapter.Extract(
            weaponPayload,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            weapons);
        Assert(organization.AdapterDescriptor.RevisionId == organizationReplay.AdapterDescriptor.RevisionId &&
               organization.SourceRevision.Revision.Id == organizationReplay.SourceRevision.Revision.Id &&
               organization.OrganizationalValues.Select(value => value.Id)
                   .SequenceEqual(organizationReplay.OrganizationalValues.Select(value => value.Id)) &&
               organization.FileEvidenceReceipts.Select(value => value.Id)
                   .SequenceEqual(organizationReplay.FileEvidenceReceipts.Select(value => value.Id)) &&
               organization.EvidenceBindings.Select(value => value.Id)
                   .SequenceEqual(organizationReplay.EvidenceBindings.Select(value => value.Id)) &&
               organization.TargetLinkClaims.Select(value => value.Id)
                   .SequenceEqual(organizationReplay.TargetLinkClaims.Select(value => value.Id)) &&
               organization.CrossSourceAssertions.Select(value => value.Id)
                   .SequenceEqual(organizationReplay.CrossSourceAssertions.Select(value => value.Id)),
            "Repeated weapon-organization extraction from identical frozen bytes is byte-semantic deterministic.");
        var organizedWeaponPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            weaponPayload, organization, acquisition.Receipts, acquisition.Bindings);
        var actorSecondaryAdapter = new GtaVActorDlcSecondaryAssertionAdapter(digest);
        var actorSecondary = actorSecondaryAdapter.Extract(
            organizedWeaponPayload,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            actorArtifact);
        var payload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            organizedWeaponPayload, actorSecondary, acquisition.Receipts, acquisition.Bindings);

        Assert(payload.KnowledgeRecords.Select(value => value.Id).SequenceEqual(originIds),
            "Secondary facts preserve every established KnowledgeRecordId.");
        Assert(payload.TerminologyAssertions is [{ VerbatimValue: "Pistol", LanguageTag: "en-US" }] &&
               payload.TerminologyAssertions[0].NativeStringIdentifier?.ExactRepresentation ==
                   $"0x{Joaat("WT_PIST"):X8}",
            "Only the exact GXT2-linked source text becomes terminology; WT_INVALID remains nameless.");
        Assert(payload.SemanticClassificationAssertions.Length == 2 &&
               payload.SemanticClassificationAssertions.All(value =>
                   value.RoleId == CanonicalProjectionSemantics.ItemWeapons),
            "Only exact CWeaponInfo records receive the Weapons classification.");
        Assert(payload.OrganizationalValueAssertions.Length == 16 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode) == 4 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode) == 2 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.GroupDimension) == 2 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.SlotDimension) == 2 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.NavigateOrderEntryDimension) == 2 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.NavigateOrderNumberDimension) == 2 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.BestOrderEntryDimension) == 1 &&
               payload.OrganizationalValueAssertions.Count(value =>
                   value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.BestOrderNumberDimension) == 1 &&
               payload.OrganizationalValueAssertions.All(value => value.VerbatimDisplayValue is null ||
                   value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode),
            "Exact weapon organization and nested Actor DLC facts remain attached to established records without invented labels.");
        var organizationReceipts = payload.FileEvidenceReceipts
            .Where(value => value.Receipt.SourceRevisionId == organization.SourceRevision.Revision.Id)
            .ToDictionary(value => value.Id);
        var organizationBindings = payload.EvidenceBindings.Where(value =>
            value.SourceRevisionId == organization.SourceRevision.Revision.Id &&
            value.ClaimKind == EvidenceClaimKind.OrganizationalValue).ToImmutableArray();
        Assert(organization.OrganizationalValues.All(assertion =>
                   organizationBindings.Any(binding =>
                       binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
                       binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                       binding.ClaimLocator == assertion.SourceFieldPath &&
                       organizationReceipts.TryGetValue(binding.EvidenceReceiptId, out var receipt) &&
                       receipt.Receipt.SourceArtifactId == weapons.Id &&
                       receipt.Receipt.SourceFieldPath == assertion.SourceFieldPath &&
                       receipt.Receipt.Verification == EvidenceVerificationKind.FileVerified &&
                       receipt.Receipt.NativeObjectIdentity is not null)),
            "Every exact weapon organization field has its own content-bound FILE_VERIFIED receipt and binding.");
        Assert(payload.CrossSourceAssertions.Length == 19 &&
               payload.CrossSourceTargetLinkClaims.Length == 10 &&
               payload.CrossSourceAssertions.All(value =>
                   value.TargetOriginSourceRevisionId != value.AssertingSourceRevisionId),
            "Every secondary claim carries a distinct exact cross-source target-link envelope.");
        Assert(payload.FileEvidenceReceipts.Where(value =>
                   value.Receipt.SourceRevisionId == secondary.SourceRevision.Revision.Id).All(value =>
                   value.Receipt.Verification == EvidenceVerificationKind.FileVerified) &&
               payload.ReferenceEvidenceReceipts.IsEmpty,
            "Secondary source facts remain exact FILE_VERIFIED evidence only.");
        Assert(payload.SourceRevisions.Single(value =>
                   value.Revision.Id == secondary.SourceRevision.Revision.Id).Revision.ArtifactIds
                   .OrderBy(value => value.Value, StringComparer.Ordinal)
                   .SequenceEqual(new[] { weapons.Id, languageRpf.Id, gxt.Id }
                       .OrderBy(value => value.Value, StringComparer.Ordinal)),
            "The asserting revision retains weapons, intermediate language RPF, and nested GXT2 artifacts.");

        var package = CanonicalCatalogPackageKernel.CreateV6(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version,
                payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
            null, [], "grid.catalog-composition.v1", payload,
            new CatalogValidationSummary(
                CatalogValidationStatus.Candidate,
                "grid.gta-v.secondary.structural", "1",
                ContentDigest.ComputeSha256("candidate-secondary"u8)),
            new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.gta-v.secondary.tests", "1", new string('a', 40),
                [new CatalogCommittedBuildInput("src/Grid.GtaV.Knowledge/GtaVWeaponsSecondaryAssertionAdapter.cs", new string('b', 40))]));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        Assert(package.Manifest.PackageSchemaVersion == CatalogPackageManifest.CrossSourceAssertionSchemaVersion &&
               verification.IsStructurallyValid,
            "The secondary assertions produce a structurally valid schema-v6 Candidate package.");
        var composition = new CatalogCompositionId("grid.test.gta-secondary-composition");
        var applicability = new CanonicalApplicabilityProjection(
            composition, payload.KnowledgeRecords.Select(value => value.Id).ToImmutableArray(), [], "1");
        var projection = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
            package, composition, CanonicalSelectorProjectionPolicy.V1, applicability);
        CanonicalSelectorResult Query(KnowledgeKind kind, CanonicalNavigationPathId? path = null) =>
            CanonicalSelectorProjectionEngine.Query(projection, new CanonicalSelectorQuery(
                package.Manifest.CatalogRevisionId, composition, kind,
                CanonicalSelectorProjectionPolicy.V1.Id, CanonicalSelectorProjectionPolicy.V1.ExactVersion,
                path, null, IncludeIdentifierOnly: true, InspectionMode: false));
        var itemRoot = Query(KnowledgeKind.Item);
        var weaponsNode = itemRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode);
        var weaponRows = Query(KnowledgeKind.Item, weaponsNode.PathId).ImmediateChildren;
        Assert(weaponRows.Length == 2 &&
               weaponRows.Any(value => value.DisplayAnchor == "Pistol" &&
                   value.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology) &&
               weaponRows.Any(value => value.DisplayAnchor == "WEAPON_NAMELESS" &&
                   value.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier),
            "Verified schema-v6 facts project exact terminology and truthful identifier-only weapons.");
        var actorRoot = Query(KnowledgeKind.Actor);
        Assert(actorRoot.ImmediateChildren.All(value =>
                value.OrganizationalSemanticId != CanonicalProjectionSemantics.ActorNpcNode),
            "DLC membership alone cannot invent the still-unproven NPC classification.");

        var projectionV2 = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
            package, composition, CanonicalSelectorProjectionPolicy.V2, applicability);
        CanonicalSelectorResult QueryV2(KnowledgeKind kind, CanonicalNavigationPathId? path = null) =>
            CanonicalSelectorProjectionEngine.Query(projectionV2, new CanonicalSelectorQuery(
                package.Manifest.CatalogRevisionId, composition, kind,
                CanonicalSelectorProjectionPolicy.V2.Id, CanonicalSelectorProjectionPolicy.V2.ExactVersion,
                path, null, IncludeIdentifierOnly: true, InspectionMode: false));
        var v2Weapons = QueryV2(KnowledgeKind.Item).ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode);
        var pistolCategory = QueryV2(KnowledgeKind.Item, v2Weapons.PathId).ImmediateChildren.Single();
        var categorizedWeapons = QueryV2(KnowledgeKind.Item, pistolCategory.PathId).ImmediateChildren;
        Assert(pistolCategory.DisplayAnchor == "WHEEL_PISTOL" &&
               pistolCategory.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier &&
               categorizedWeapons.Length == 2 &&
               categorizedWeapons.All(value => value.KnowledgeRecordId is not null),
            "Projection v2 exposes exact identifier-only WheelSlot organization below Weapons without changing record identity.");

        var storeRoot = Path.Combine(Path.GetTempPath(), "grid-gta-secondary-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);
        try
        {
            var store = new JsonCanonicalKnowledgeCatalogStore(Path.Combine(storeRoot, "catalog.v5.json"));
            var imported = await store.ImportPackageAsync(0, package);
            var retry = await store.ImportPackageAsync(imported.Revision, package);
            var reloaded = await store.LoadAsync();
            Assert(imported.Status == CanonicalCatalogImportStatus.Imported &&
                   retry.Status == CanonicalCatalogImportStatus.Unchanged &&
                   reloaded.IsValid &&
                   reloaded.Snapshot.KnowledgeRecords.Select(value => value.Id).SequenceEqual(originIds) &&
                   reloaded.Snapshot.TerminologyAssertions.Any(value => value.VerbatimValue == "Pistol") &&
                   reloaded.Snapshot.SemanticClassificationAssertions.Length == 2 &&
                   reloaded.Snapshot.OrganizationalValueAssertions.Length == 16 &&
                   reloaded.Snapshot.CrossSourceAssertions.Length == 19,
                "Atomic schema-v5 import, idempotent retry, and reload preserve secondary facts and origin IDs.");
        }
        finally
        {
            Directory.Delete(storeRoot, recursive: true);
        }

        var changedGxt = Frozen(
            gxt.SourceCoordinate.ExactRepresentation, "GXT2", gxt.DeclaredFormat,
            CreateGxt2((Joaat("WT_PIST"), "PISTOL!")), observed);
        var changedSecondary = adapter.Extract(
            origin,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            weapons, languageRpf, changedGxt);
        var changedAcquisition = CreateAcquisition(version, weapons, actorArtifact, languageRpf, changedGxt);
        var changedWeaponPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            origin, changedSecondary, changedAcquisition.Receipts, changedAcquisition.Bindings);
        var changedOrganization = organizationAdapter.Extract(
            changedWeaponPayload,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            weapons);
        var changedOrganizedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            changedWeaponPayload, changedOrganization, changedAcquisition.Receipts, changedAcquisition.Bindings);
        var changedActorSecondary = actorSecondaryAdapter.Extract(
            changedOrganizedPayload,
            KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            actorArtifact);
        var changedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            changedOrganizedPayload, changedActorSecondary,
            changedAcquisition.Receipts, changedAcquisition.Bindings);
        Assert(changedPayload.KnowledgeRecords.Select(value => value.Id).SequenceEqual(originIds) &&
               CanonicalCatalogPackageKernel.ComputePayloadDigestV6(payload) !=
                   CanonicalCatalogPackageKernel.ComputePayloadDigestV6(changedPayload),
            "Changed authoritative terminology changes package semantics without regenerating record identities.");

        Console.WriteLine("PASS  GTA V Enhanced cross-source secondary assertions.");
        return checks;
    }

    private static AcquisitionFixture CreateAcquisition(
        SourceNativeVersion version,
        FrozenSourceArtifact weapons,
        FrozenSourceArtifact actor,
        FrozenSourceArtifact languageRpf,
        FrozenSourceArtifact gxt)
    {
        var method = new AcquisitionMethodCoordinate(
            "grid.gta-v-enhanced.rpf-member-acquisition", "1",
            "fivefury.rpf-read", "0.5.1",
            ContentDigest.ComputeSha256("pinned-fivefury-fixture"u8));
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220");
        SourceAcquisitionReceipt Receipt(string container, params FrozenSourceArtifact[] members)
        {
            var coordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", container);
            var digest = ContentDigest.ComputeSha256(Encoding.UTF8.GetBytes("container:" + container));
            var values = members.Select(value => new SourceAcquisitionMember(
                value.SourceCoordinate, value.ExactBytes.Length, value.Digest, value.Id)).ToImmutableArray();
            var id = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, version, coordinate, 1024, digest, method, values);
            return new SourceAcquisitionReceipt(
                id, SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, version, coordinate, 1024, digest, method, values);
        }
        var receipts = ImmutableArray.Create(
            Receipt("common.rpf", weapons),
            Receipt("update/update.rpf", actor),
            Receipt("x64b.rpf", languageRpf, gxt));
        var bindings = receipts.SelectMany(receipt => receipt.Members.Select(member =>
            new SourceArtifactAcquisitionBinding(
                member.ArtifactId, receipt.Id, member.MemberCoordinate, member.ByteLength, member.Digest)))
            .ToImmutableArray();
        var fingerprints = receipts.Select(value => SourceArtifactId.DeriveV1(value.ContainerDigest))
            .ToImmutableArray();
        return new AcquisitionFixture(receipts, bindings, fingerprints);
    }

    private static FrozenSourceArtifact Frozen(
        string coordinate,
        string objectType,
        KnowledgeFormatCoordinate format,
        byte[] bytes,
        DateTimeOffset observed)
    {
        var exact = bytes.ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(exact.AsSpan());
        return new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.resource-coordinate", objectType, coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8", 1),
            format, exact, observed);
    }

    private static byte[] CreateGxt2(params (uint Hash, string Text)[] entries)
    {
        var ordered = entries.OrderBy(value => value.Hash).ToArray();
        var encoded = ordered.Select(value => Encoding.UTF8.GetBytes(value.Text)).ToArray();
        var tableEnd = 8 + ordered.Length * 8;
        var cursor = tableEnd + 8;
        var result = new byte[cursor + encoded.Sum(value => value.Length + 1)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + index * 8), ordered[index].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12 + index * 8), (uint)cursor);
            encoded[index].CopyTo(result.AsSpan(cursor));
            cursor += encoded[index].Length + 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd), 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd + 4), (uint)cursor);
        return result;
    }

    private static uint Joaat(string value)
    {
        uint hash = 0;
        foreach (var character in value)
        {
            var lowered = character is >= 'A' and <= 'Z' ? (byte)(character + 32) : (byte)character;
            hash += lowered;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    private sealed record AcquisitionFixture(
        ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
        ImmutableArray<SourceArtifactId> ContainerFingerprints);
}
