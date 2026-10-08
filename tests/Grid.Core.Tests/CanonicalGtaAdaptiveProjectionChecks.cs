using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalGtaAdaptiveProjectionChecks
{
    private static CanonicalTerminologyLocalePreference EnglishLocale { get; } = new("en-US", ["en"]);

    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var gtaPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId);
        var legacyPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            ProductionGridCatalogService.GrandTheftAutoVLegacyId);
        var skyrimPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            new GameId("game.skyrim"));
        Assert(gtaPolicy.ExactVersion == "5" && legacyPolicy.ExactVersion == "5" && skyrimPolicy.ExactVersion == "4",
            "GTA Enhanced and Legacy select v5; representative non-GTA selects v4.");
        Assert(!gtaPolicy.OrganizationalDefinitions.Any(value => value.SemanticId == CanonicalProjectionSemantics.ItemArmorNode) &&
               skyrimPolicy.OrganizationalDefinitions.Any(value => value.SemanticId == CanonicalProjectionSemantics.ItemArmorNode),
            "GTA policy omits Skyrim-only families; cross-game v4 retains them.");

        var enhancedFixture = CanonicalSelectorRuntimeChecks.CreateAllSelectorEnhancedFixture();
        var gtaInput = enhancedFixture.ToInput(gtaPolicy);
        var missionRoot = Query(gtaInput, KnowledgeKind.MissionQuest, gtaPolicy, null);
        Assert(missionRoot.ImmediateChildren.Any(value =>
                   value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode),
            "GTA mission projection exposes Online organization.");
        var online = missionRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode);
        var activityFamily = Query(gtaInput, KnowledgeKind.MissionQuest, gtaPolicy, online.PathId)
            .ImmediateChildren.Single(value => value.DisplayAnchor == "Exact Activity Family");
        var missionLeaf = Query(gtaInput, KnowledgeKind.MissionQuest, gtaPolicy, activityFamily.PathId)
            .ImmediateChildren.Single();
        Assert(missionLeaf.DisplayAnchor == "Exact English Activity" &&
               missionLeaf.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology,
            "Resolved mission titles win over native identifiers.");

        var itemRoot = Query(gtaInput, KnowledgeKind.Item, gtaPolicy, null);
        var vehicles = itemRoot.ImmediateChildren.Single();
        Assert(vehicles.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemVehiclesNode,
            "GTA item projection exposes Vehicles when vehicle knowledge exists.");
        var vehicleLeaf = Query(gtaInput, KnowledgeKind.Item, gtaPolicy,
            Query(gtaInput, KnowledgeKind.Item, gtaPolicy, vehicles.PathId).ImmediateChildren.Single().PathId)
            .ImmediateChildren.Single();
        Assert(vehicleLeaf.DisplayAnchor == "Exact Vehicle",
            "GTA vehicles project under vehicle class with localized titles.");

        var technicalMission = (enhancedFixture with
        {
            Terms = enhancedFixture.Terms
                .Where(value => value.KnowledgeRecordId != enhancedFixture.Mission.Id)
                .ToImmutableArray(),
        }).Repackage();
        var technicalInput = technicalMission.ToInput(gtaPolicy);
        var technicalOnline = Query(technicalInput, KnowledgeKind.MissionQuest, gtaPolicy, null)
            .ImmediateChildren.Single(value => value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode);
        var technicalFamily = Query(technicalInput, KnowledgeKind.MissionQuest, gtaPolicy, technicalOnline.PathId)
            .ImmediateChildren.Single(value => value.DisplayAnchor == "Exact Activity Family");
        var unresolvedMission = Query(technicalInput, KnowledgeKind.MissionQuest, gtaPolicy, technicalFamily.PathId)
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == technicalMission.Mission.Id);
        Assert(unresolvedMission is not null &&
               unresolvedMission.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier,
            "Unresolved GTA missions remain visible with native identifiers instead of being dropped.");

        var fallbackFixture = (enhancedFixture with
        {
            Terms = enhancedFixture.Terms
                .Where(value => value.KnowledgeRecordId != enhancedFixture.Mission.Id)
                .Concat(new[]
                {
                    new TerminologyAssertion(
                        enhancedFixture.Mission.Id,
                        enhancedFixture.SourceRevisionId,
                        TerminologyAssertionRole.PrimaryName,
                        "Exact French Activity",
                        "/activity/name",
                        "fr-FR"),
                })
                .ToImmutableArray(),
        }).Repackage();
        var fallbackInput = fallbackFixture.ToInput(gtaPolicy);
        var fallbackLocale = new CanonicalTerminologyLocalePreference("en-US", ["fr-FR"]);
        var fallbackMission = Query(fallbackInput, KnowledgeKind.MissionQuest, gtaPolicy,
            Query(fallbackInput, KnowledgeKind.MissionQuest, gtaPolicy,
                Query(fallbackInput, KnowledgeKind.MissionQuest, gtaPolicy, null, fallbackLocale)
                    .ImmediateChildren.Single(value => value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode).PathId,
                fallbackLocale)
                .ImmediateChildren.Single(value => value.DisplayAnchor == "Exact Activity Family").PathId,
            fallbackLocale)
            .ImmediateChildren.Single();
        Assert(fallbackMission.DisplayAnchor == "Exact French Activity",
            "Approved locale fallback terminology survives GTA admission.");

        var locationSeed = CanonicalSelectorAndInstructionChecks.Fixture.Create(enhanced: true);
        CanonicalSemanticClassificationAssertion LocationRole(CanonicalKnowledgeRecord record, CanonicalSemanticRoleId role)
        {
            var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
                record.Id, locationSeed.SourceRevisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
            return new(id, record.Id, locationSeed.SourceRevisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
        }

        var locationFixture = (locationSeed with
        {
            Classifications = locationSeed.Classifications.AddRange(new[]
            {
                LocationRole(locationSeed.LocationParent, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                LocationRole(locationSeed.LocationChild, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                LocationRole(locationSeed.LocationUnresolved, CanonicalProjectionSemantics.SelectorPlayerAddressable),
            }),
        }).Repackage();
        var locationInput = locationFixture.ToInput(gtaPolicy);
        var locationRoot = Query(locationInput, KnowledgeKind.Location, gtaPolicy, null);
        Assert(locationRoot.ImmediateChildren.Any(value => value.KnowledgeRecordId == locationFixture.LocationParent.Id),
            "GTA Location projection retains resolved route labels at root.");
        var unresolvedBucket = locationRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.LocationUnresolvedHierarchyNode);
        var unresolvedChild = Query(locationInput, KnowledgeKind.Location, gtaPolicy, unresolvedBucket.PathId)
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == locationFixture.LocationUnresolved.Id);
        Assert(unresolvedChild.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier,
            "Unresolved Location records remain honestly represented under the unresolved hierarchy bucket.");

        var namedPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, KnowledgeKind.Location);
        var namedFixture = (locationFixture with { Terms = locationFixture.Terms.Add(new(
            locationFixture.LocationChild.Id, locationFixture.SourceRevisionId,
            TerminologyAssertionRole.PrimaryName, "Exact child", "/location/child-name", "en-US")) }).Repackage();
        var namedInput = namedFixture.ToInput(namedPolicy);
        var namedRoot = Query(namedInput, KnowledgeKind.Location, namedPolicy, null);
        Assert(namedPolicy.ExactVersion == "6" && namedRoot.ImmediateChildren.All(value =>
                value.DisplayKind != CanonicalNavigationDisplayKind.NativeIdentifier),
            "Location v6 hides unresolved identifiers without changing historical v5.");
        var namedParent = namedRoot.ImmediateChildren.Single(value => value.KnowledgeRecordId == locationFixture.LocationParent.Id);
        Assert(namedParent.IsSelectable && namedParent.CanDescend &&
            Query(namedInput, KnowledgeKind.Location, namedPolicy, namedParent.PathId).ImmediateChildren.Any(value =>
                value.KnowledgeRecordId == locationFixture.LocationChild.Id),
            "Named parents remain selectable and expose evidence-backed child paths.");
        Assert(CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, KnowledgeKind.Item).ExactVersion == "5",
            "Location policy revision does not activate changes for other selectors.");
        Assert(CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
            ProductionGridCatalogService.GrandTheftAutoVLegacyId, KnowledgeKind.Location).ExactVersion == "5",
            "Enhanced Location activation does not change the Legacy game's policy.");

        checks += ActorMultiPathChecks(gtaPolicy, enhancedFixture);

        Assert(CanonicalSelectorProjectionPolicyResolver.MatchesGamePolicy(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, gtaPolicy.Id, gtaPolicy.ExactVersion),
            "Ticket validation coordinates align with GTA adaptive projection.");

        WriteTreePreview(enhancedFixture, gtaPolicy);
        Console.WriteLine($"PASS  GTA adaptive fourth-row projection ({checks} checks).");
        return checks;
    }

    private static int ActorMultiPathChecks(
        CanonicalSelectorProjectionPolicy gtaPolicy,
        CanonicalSelectorAndInstructionChecks.Fixture seed)
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var dlcNative = SourceNativeIdentifier.FromExactUtf8("grid.test.organization", "Value", "DLC_PACK");
        var dlcOrg = new CanonicalOrganizationalValueAssertion(
            CanonicalOrganizationalValueAssertionId.DeriveV2(
                seed.Actor.Id, seed.SourceRevisionId,
                CanonicalProjectionSemantics.ActorDlcDimensionNode, dlcNative, "Exact DLC Pack",
                "grid.test.organization", "1", "/organization", "en-US"),
            seed.Actor.Id, seed.SourceRevisionId,
            CanonicalProjectionSemantics.ActorDlcDimensionNode, dlcNative, "Exact DLC Pack",
            "grid.test.organization", "1", "/organization", "en-US");
        var fixture = (seed with
        {
            OrganizationalValues = seed.OrganizationalValues.Add(dlcOrg),
        }).Repackage();
        var input = fixture.ToInput(gtaPolicy);
        var npc = Query(input, KnowledgeKind.Actor, gtaPolicy, null)
            .ImmediateChildren.Single(value => value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorNpcNode);
        var generic = Query(input, KnowledgeKind.Actor, gtaPolicy, npc.PathId)
            .ImmediateChildren.Single(value => value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorGenericTypeNode);
        var categoryLeaf = Query(input, KnowledgeKind.Actor, gtaPolicy,
            Query(input, KnowledgeKind.Actor, gtaPolicy, generic.PathId)
                .ImmediateChildren.Single(value => value.DisplayAnchor == "Exact Category").PathId)
            .ImmediateChildren.Single();
        Assert(categoryLeaf.KnowledgeRecordId == fixture.Actor.Id,
            "Actor retains Named/Generic source-category placement.");
        var dlcDimension = Query(input, KnowledgeKind.Actor, gtaPolicy, npc.PathId)
            .ImmediateChildren.Single(value => value.DisplayAnchor == "DLC");
        var dlcLeaf = Query(input, KnowledgeKind.Actor, gtaPolicy,
            Query(input, KnowledgeKind.Actor, gtaPolicy, dlcDimension.PathId)
                .ImmediateChildren.Single(value => value.DisplayAnchor == "Exact DLC Pack").PathId)
            .ImmediateChildren.Single();
        Assert(dlcLeaf.KnowledgeRecordId == fixture.Actor.Id,
            "The same actor also retains DLC organization when both evidence paths exist.");
        return checks;
    }

    private static CanonicalSelectorResult Query(
        CanonicalSelectorProjectionInput input,
        KnowledgeKind kind,
        CanonicalSelectorProjectionPolicy policy,
        CanonicalNavigationPathId? path,
        CanonicalTerminologyLocalePreference? locale = null,
        bool includeIdentifierOnly = true) =>
        CanonicalSelectorProjectionEngine.Query(input, new(
            input.CatalogRevisionId,
            input.CatalogCompositionId,
            kind,
            policy.Id,
            policy.ExactVersion,
            path,
            null,
            includeIdentifierOnly,
            false,
            locale ?? EnglishLocale));

    private static void WriteTreePreview(
        CanonicalSelectorAndInstructionChecks.Fixture fixture,
        CanonicalSelectorProjectionPolicy policy)
    {
        var input = fixture.ToInput(policy);
        var repoRoot = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(repoRoot); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GRID.md")))
            {
                repoRoot = dir.FullName;
                break;
            }
        }

        var outputDir = Path.Combine(repoRoot, "artifacts", "selector-tree-preview");
        Directory.CreateDirectory(outputDir);
        foreach (var kind in Enum.GetValues<KnowledgeKind>())
        {
            var builder = new StringBuilder();
            DumpLevel(input, policy, kind, null, builder, 0);
            File.WriteAllText(
                Path.Combine(outputDir, $"GtaAdaptive-{kind}-False-en-US.txt"),
                builder.ToString(),
                Encoding.UTF8);
        }
    }

    private static void DumpLevel(
        CanonicalSelectorProjectionInput input,
        CanonicalSelectorProjectionPolicy policy,
        KnowledgeKind kind,
        CanonicalNavigationPathId? path,
        StringBuilder builder,
        int depth)
    {
        var result = Query(input, kind, policy, path);
        var indent = new string(' ', depth * 2);
        foreach (var child in result.ImmediateChildren)
        {
            var kindLabel = child.IsSelectable ? "selectable" : "folder";
            builder.AppendLine($"{indent}{child.DisplayAnchor} [{kindLabel}]");
            if (child.CanDescend)
                DumpLevel(input, policy, kind, child.PathId, builder, depth + 1);
        }
    }
}
