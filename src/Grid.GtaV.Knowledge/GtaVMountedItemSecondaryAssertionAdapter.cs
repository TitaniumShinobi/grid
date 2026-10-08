using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Exact XML identities, mounted localization and typed references; no model-name display guesses.</summary>
public sealed class GtaVMountedItemSecondaryAssertionAdapter
{
    private readonly GtaVItemCorpusIndex index;
    private readonly GtaVPresentationCorpusIndex? presentation;
    private readonly ContentDigest digest;
    public GtaVMountedItemSecondaryAssertionAdapter(ContentDigest digest, GtaVItemCorpusIndex index, GtaVPresentationCorpusIndex? presentation = null)
    { this.digest = digest; this.index = index; this.presentation = presentation; }

    public ImmutableArray<GtaVSecondaryAssertionBatch> Extract(CanonicalCatalogPayload origin, KnowledgeSourceScope scope)
    {
        var referenceArtifacts = presentation?.Artifacts.Values.ToArray() ?? [];
        var formats = index.Sources.Select(x => x.Artifact.DeclaredFormat).Concat(referenceArtifacts.Select(x => x.DeclaredFormat))
            .Concat(origin.SourceRevisions.SelectMany(x => x.ArtifactFormats).Select(x => x.Format)).Distinct()
            .Select(x => new SupportedKnowledgeFormat(x.FormatId, x.ExactFormatVersion, ["rpf7-member", "reference-snapshot"],
                [GtaVMountedItemKnowledgeAdapter.SourceObjectType(x.FormatId)], [KnowledgeKind.Item], true, true, false)).ToImmutableArray();
        var descriptor = new GameKnowledgeAdapterDescriptor(new KnowledgeAdapterId("grid.gta-v.enhanced.mounted-item-secondary"),
            "1", digest, 1, "mounted-item-exact-xml-gxt-v1", [ProductionGridCatalogService.GrandTheftAutoVEnhancedId], formats,
            new KnowledgeAdapterResourceLimits(128L * 1024 * 1024, 20_000, 500_000, 500_000), KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
        var builder = new GtaVPresentationEvidenceBuilder(origin, scope, descriptor,
            index.Sources.Select(x => x.Artifact).Concat(referenceArtifacts));
        var targets = origin.KnowledgeRecords.Where(x => x.Kind == KnowledgeKind.Item)
            .GroupBy(x => (x.NativeIdentity.ObjectType, x.NativeIdentity.ExactRepresentation))
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var apparelTargets = index.Rows.Where(x => x.Source.Family == "apparel")
            .GroupBy(x => (x.Fields["pedName"], x.Fields["uniqueNameHash"]))
            .ToDictionary(g => g.Key, g => g.Select(x => targets.GetValueOrDefault((x.ObjectType, x.NativeKey)))
                .OfType<CanonicalKnowledgeRecord>().DistinctBy(x => x.Id).ToArray());
        foreach (var row in index.Rows)
        {
            var target = FindTarget(row, targets);
            if (target is null) continue;
            var raw = new GtaVPresentationEvidence(row.Source.Artifact, row.IdentityField, false);
            var link = builder.Link(target, raw);
            var names = row.LabelKey is null ? [] : index.ResolveText(row.Source, row.LabelKey);
            foreach (var text in names.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
                builder.Title(target, link, text.Text, text.FieldLocator,
                    [raw, new(row.Source.Artifact, row.Locator + "/" + row.LabelField + "[1]", false), TextEvidence(text)]);
            var family = row.Source.Family;
            CanonicalSemanticRoleId? role = family switch { "vehicles" or "vehicle-shop" => CanonicalProjectionSemantics.ItemVehicles,
                "apparel" => CanonicalProjectionSemantics.ItemClothing,
                "components" or "weapon-shop" => CanonicalProjectionSemantics.ItemWeapons,
                "weapons" when row.ObjectType == "CWeaponInfo" => CanonicalProjectionSemantics.ItemWeapons, _ => null };
            if (role is not null) builder.Classify(target, link, role.Value, row.IdentityField, [raw]);
            if (role is not null && names.Any(x => !string.IsNullOrWhiteSpace(x.Text)) && IsPlayerAddressable(row))
                builder.Classify(target, link, CanonicalProjectionSemantics.SelectorPlayerAddressable, row.IdentityField, [raw]);
            if (family == "vehicles" && row.Fields.TryGetValue("vehicleMakeName", out var make))
                foreach (var text in index.ResolveText(row.Source, make))
                    builder.Organize(target, link, new CanonicalOrganizationalSemanticId("rockstar.gta-v.item.dimension.manufacturer"),
                        make, text.Text, text.FieldLocator, [raw, new(row.Source.Artifact, row.Locator + "/vehicleMakeName[1]", false), TextEvidence(text)]);
            if (row.Source.Declaration is { } declaration)
            {
                var marker = declaration.IndexOf('#');
                var coordinate = marker < 0 ? "" : declaration["rpf7-member:".Length..marker];
                var content = index.Sources.SingleOrDefault(x => x.Coordinate == coordinate);
                if (content is null) throw new InvalidDataException("Mounted Item declaration evidence is absent.");
                var contentNative = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.item.content", "ContentDeclaration", declaration, "grid.exact-utf8", 1);
                builder.AddRelationship(target, link, new RelationshipAssertion(target.Id, builder.RevisionId,
                    new RelationshipSemanticId("rockstar.gta-v.item.declared-by-content"), "dataFiles.Item", declaration, contentNative),
                    [raw, new(content.Artifact, declaration, false)]);
            }
            if (family == "apparel")
                foreach (var key in new[] { "pedName", "fullDlcName", "eCharacter" })
                {
                    var field = "rpf7-member:" + row.Source.Coordinate + "#/ShopPedApparel[1]/" + key + "[1]";
                    var native = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.item.apparel-applicability", key, row.Fields[key], "grid.exact-utf8", 1);
                    builder.AddRelationship(target, link, new RelationshipAssertion(target.Id, builder.RevisionId,
                        new RelationshipSemanticId("rockstar.gta-v.item.apparel." + key.ToLowerInvariant()), key, field, native),
                        [raw, new(row.Source.Artifact, field, false)]);
                }
            if (family == "vehicles" && presentation is not null && row.Fields.TryGetValue("vehicleClass", out var className) && presentation.VehicleClasses.TryGetValue(className, out var vehicleClass))
            {
                var table = new GtaVPresentationEvidence(presentation.VehicleClassTable, "vehicleClass/" + className + "/" + vehicleClass.Index, true);
                var native = new GtaVPresentationEvidence(presentation.VehicleClassNative, "GetVehicleClass/VEH_CLASS_" + vehicleClass.Index, true);
                var classLink = builder.Link(target, table);
                foreach (var text in index.ResolveText(row.Source, vehicleClass.LabelKey))
                    builder.Organize(target, classLink, CanonicalProjectionSemantics.ItemVehicleClassDimensionNode,
                        className, text.Text, text.FieldLocator,
                        [raw, new(row.Source.Artifact, row.Locator + "/vehicleClass[1]", false), table, native, TextEvidence(text)]);
            }
            foreach (var reference in row.References)
            {
                var targetType = reference.Kind switch { "AmmoInfo" or "AmmoRef" => "CAmmoInfo", "WeaponRef" => "CWeaponInfo", "componentName" or "Name" => "CWeaponComponentInfo",
                    "trailers" or "additionalTrailers" => "CVehicleModelInfo.modelName",
                    "Actions" or "OnFootPickupActions" or "InCarPickupActions" or "OnShotPickupActions" => "CPickupDataManager.actionData",
                    "Rewards" => "CPickupDataManager.rewardData", _ => reference.Kind };
                targets.TryGetValue((targetType, reference.NativeKey), out var related);
                if (related is null && family == "apparel" && reference.Kind == "nameHash" &&
                    apparelTargets.TryGetValue((row.Fields["pedName"], reference.NativeKey), out var apparelMatches) && apparelMatches.Length == 1)
                    related = apparelMatches[0];
                if (related is null && reference.Kind == "driverName")
                {
                    var hash = $"0x{GtaVItemCorpusIndex.Joaat(reference.NativeKey):X8}";
                    var actors = origin.KnowledgeRecords.Where(x => x.Kind == KnowledgeKind.Actor &&
                        (x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.resident-ped-model" && x.NativeIdentity.ExactRepresentation == hash ||
                         x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.mounted-dlc-ped-model" && x.NativeIdentity.ExactRepresentation == reference.NativeKey)).ToArray();
                    if (actors.Length == 1) related = actors[0];
                }
                var identity = related?.NativeIdentity ?? SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.item.reference", targetType, reference.NativeKey, "grid.exact-utf8", 1);
                var relationship = new RelationshipAssertion(target.Id, builder.RevisionId,
                    new RelationshipSemanticId("rockstar.gta-v.item." + reference.Kind.ToLowerInvariant()), reference.Kind,
                    reference.FieldLocator, identity, related?.Id);
                builder.AddRelationship(target, link, relationship, [raw, new(row.Source.Artifact, reference.FieldLocator, false)]);
            }
        }
        return [builder.Build()];
    }

    private static GtaVPresentationEvidence TextEvidence(GtaVItemText text) =>
        new(text.Source.Artifact, text.FieldLocator, false, text.TextOffset, text.ByteLength);

    private static CanonicalKnowledgeRecord? FindTarget(GtaVItemRow row,
        Dictionary<(string, string), CanonicalKnowledgeRecord> targets)
    {
        if (row.Source.Family is not ("vehicle-shop" or "weapon-shop")) return targets.GetValueOrDefault((row.ObjectType, row.NativeKey));
        var type = row.Source.Family == "vehicle-shop" ? "CVehicleModelInfo.modelName" : "CWeaponInfo";
        var exact = targets.GetValueOrDefault((type, row.NativeKey));
        if (exact is not null) return exact;
        // Shop hashes are correlation keys, never new canonical identities.
        var hash = row.NativeKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(row.NativeKey[2..], System.Globalization.NumberStyles.HexNumber, null, out var numeric)
            ? numeric : GtaVItemCorpusIndex.Joaat(row.NativeKey);
        var matches = targets.Where(x => x.Key.Item1 == type && GtaVItemCorpusIndex.Joaat(x.Key.Item2) == hash).Select(x => x.Value).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsPlayerAddressable(GtaVItemRow row)
    {
        if (row.Source.Family is "vehicles" or "vehicle-shop" or "apparel" or "components" or "weapon-shop") return true;
        if (row.ObjectType != "CWeaponInfo") return false;
        var flags = row.Fields.GetValueOrDefault("WeaponFlags", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return (flags.Contains("UsableOnFoot") || flags.Contains("CarriedInHand")) &&
            !flags.Contains("HiddenFromWeaponWheel") && !flags.Contains("NotAWeapon");
    }
}
