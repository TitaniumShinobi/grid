using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalSelectorAndInstructionChecks
{
    private static CanonicalTerminologyLocalePreference EnglishLocale { get; } =
        new("en-US", ["en"]);

    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }

        var fixture = Fixture.Create();
        var policy = CanonicalSelectorProjectionPolicy.V1;

        Assert(CanonicalSelectorProjectionPolicy.V1.ExactVersion == "1" &&
               CanonicalSelectorProjectionPolicy.V1.OrganizationalDefinitions.All(value =>
                   value.SemanticId != CanonicalProjectionSemantics.ItemSourceCategoryValueNode) &&
               CanonicalSelectorProjectionPolicy.V2.ExactVersion == "2" &&
               CanonicalSelectorProjectionPolicy.V2.OrganizationalDefinitions.Count(value =>
                   value.SemanticId == CanonicalProjectionSemantics.ItemSourceCategoryValueNode &&
                   value.KnowledgeKind == KnowledgeKind.Item &&
                   value.ParentSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode) == 1 &&
               CanonicalSelectorProjectionPolicy.V3.ExactVersion == "3" &&
               CanonicalSelectorProjectionPolicy.V3.OrganizationalDefinitions.Count(value =>
                   value.SemanticId == CanonicalProjectionSemantics.MissionActivityFamilyValueNode &&
                   value.KnowledgeKind == KnowledgeKind.MissionQuest &&
                   value.ParentSemanticId == CanonicalProjectionSemantics.MissionOnlineNode) == 1,
            "Projection v2 adds Item source categories and v3 adds only the game-neutral Online activity-family value path.");

        Assert(policy.Control.ClosedHeightDip == 28 &&
               policy.Control.DropdownPlacement == CanonicalSelectorDropdownPlacement.AttachedBelow &&
               policy.Control.NavigationMode == CanonicalSelectorNavigationMode.ReplaceCurrentLevel &&
               policy.Control.OverflowBehavior == CanonicalSelectorOverflowBehavior.VerticalScroll &&
               policy.Control.OtherPlacement == CanonicalSelectorOtherPlacement.RootOnly &&
               policy.Control.CurrentNodeIsDropdownHeader &&
               policy.Control.BodyContainsImmediateChildrenOnly &&
               policy.Control.BackNavigationLevelCount == 1 &&
               policy.Control.OtherSelectionClosesDropdown && policy.Control.OtherFieldIsSingleLine &&
               policy.Control.OtherFieldHeightDip == 28 && policy.Control.OtherFieldPreferredWidthDip == 84 &&
               !policy.Control.ExpandedTreeAllowed && !policy.Control.DetachedInspectorAllowed,
            "The v1 selector control contract remains a fixed attached, replace-level, root-Other projection.");
        AssertThrows<ArgumentException>(() => _ = new CanonicalSelectorControlContract(
                1, 29, CanonicalSelectorDropdownPlacement.AttachedBelow,
                CanonicalSelectorNavigationMode.ReplaceCurrentLevel,
                CanonicalSelectorOverflowBehavior.VerticalScroll,
                CanonicalSelectorOtherPlacement.RootOnly, 28, 84, false, false),
            "Selector layout metrics cannot silently drift from the versioned contract.");
        var projectionContractTypes = new[]
        {
            typeof(CanonicalSelectorQuery), typeof(CanonicalSelectorResult), typeof(CanonicalNavigationPathId),
            typeof(CanonicalOrganizationalNodeId), typeof(CanonicalSemanticClassificationAssertion),
        };
        var forbiddenScopeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "AccountId", "InstallationId", "ProfileId", "ModId", "LoadOrder",
        };
        Assert(projectionContractTypes.SelectMany(type => type.GetConstructors())
                .SelectMany(constructor => constructor.GetParameters())
                .All(parameter => !forbiddenScopeNames.Contains(parameter.ParameterType.Name)),
            "Projection identity and query contracts cannot accept account, installation, profile, local ModId, or load-order identity.");

        var locationRoot = Query(fixture, KnowledgeKind.Location, null, includeIdentifierOnly: true);
        Assert(locationRoot.CurrentPathId == locationRoot.RootPathId &&
               locationRoot.ParentPathId is null &&
               locationRoot.ImmediateChildren.All(value => value.Depth == 1),
            "A selector query exposes only the current level and pins its deterministic root path.");
        var namedParent = locationRoot.ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.LocationParent.Id);
        Assert(namedParent.DisplayAnchor == fixture.LocationParentName.VerbatimValue &&
               namedParent.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology &&
               namedParent.ExactTerminologyAssertions.Single() == fixture.LocationParentName,
            "Location navigation returns exact source terminology without normalization.");
        var locationChildLevel = Query(fixture, KnowledgeKind.Location, namedParent.PathId, includeIdentifierOnly: true);
        var identifierChild = locationChildLevel.ImmediateChildren.Single();
        Assert(identifierChild.KnowledgeRecordId == fixture.LocationChild.Id &&
               identifierChild.DisplayAnchor == fixture.LocationChild.NativeIdentity.ExactRepresentation &&
               identifierChild.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier &&
               identifierChild.ExactTerminologyAssertions.IsEmpty,
            "Nameless Location records remain identifier-only without synthesized terminology.");
        Assert(CanonicalSelectorProjectionEngine.Back(locationChildLevel) == locationRoot.RootPathId,
            "Back navigation returns the exact parent path instead of reconstructing hierarchy state.");
        var selectedLocation = CanonicalSelectorProjectionEngine.Select(locationChildLevel, identifierChild.PathId);
        Assert(selectedLocation.SelectionKind == CanonicalSelectorSelectionKind.CanonicalRecord &&
               selectedLocation.KnowledgeRecordId == fixture.LocationChild.Id &&
               selectedLocation.SelectedPathId == identifierChild.PathId &&
               selectedLocation.CatalogRevisionId == fixture.CatalogRevisionId &&
               selectedLocation.CatalogCompositionId == fixture.CompositionId,
            "Canonical selection preserves record, navigation, catalog, and composition coordinates.");

        var unresolvedNode = locationRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.LocationUnresolvedHierarchyNode);
        Assert(!unresolvedNode.IsSelectable && unresolvedNode.CanDescend &&
               unresolvedNode.DisplayKind == CanonicalNavigationDisplayKind.PolicyLabel,
            "Unresolved source hierarchy remains visible as non-selectable organization.");
        AssertThrows<ArgumentException>(() => CanonicalSelectorProjectionEngine.Select(locationRoot, unresolvedNode.PathId),
            "Organizational nodes cannot be committed as canonical records.");
        var namedOnly = Query(fixture, KnowledgeKind.Location, null, includeIdentifierOnly: false);
        var namedOnlyParent = namedOnly.ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.LocationParent.Id);
        var namedChildLevel = Query(fixture, KnowledgeKind.Location, namedOnlyParent.PathId, includeIdentifierOnly: false);
        Assert(namedChildLevel.ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.LocationChild.Id)
                   .DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier,
            "Identifier-only records remain in projection with honest native identifiers.");
        var exactSearch = Query(fixture, KnowledgeKind.Location, null, true, "  Zône—Exact  ");
        var foldedSearch = Query(fixture, KnowledgeKind.Location, null, true, "zône—exact");
        Assert(exactSearch.ImmediateChildren.Any(value => value.KnowledgeRecordId == fixture.LocationParent.Id) &&
               foldedSearch.ImmediateChildren.IsEmpty,
            "Selector search is exact ordinal and does not normalize or case-fold terminology.");

        var itemRoot = Query(fixture, KnowledgeKind.Item, null, true);
        var weapons = itemRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode);
        var weaponLevel = Query(fixture, KnowledgeKind.Item, weapons.PathId, true);
        var conflictedItem = weaponLevel.ImmediateChildren.Single();
        Assert(conflictedItem.HasTerminologyConflict &&
               conflictedItem.ExactTerminologyAssertions.Length == 2 &&
               conflictedItem.ExactTerminologyAssertions.Select(value => value.VerbatimValue)
                   .SequenceEqual(["Alpha", "Zulu"]),
            "Conflicting source-native terminology remains independently visible and deterministically ordered.");
        Assert(itemRoot.ImmediateChildren.Any(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id),
            "Unclassified Items remain visible at the root instead of receiving an invented family.");
        Assert(itemRoot.ImmediateChildren.First().OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode &&
               itemRoot.ImmediateChildren.Last().KnowledgeRecordId == fixture.UnclassifiedItem.Id,
            "Named organizational and canonical rows precede identifier-only records deterministically.");
        Assert(itemRoot.ImmediateChildren.All(value =>
                   value.OrganizationalSemanticId != CanonicalProjectionSemantics.ItemArmorNode),
            "Empty evidence-backed families are pruned from the projection.");

        var sourceCategoryIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.item-source-category", "SourceCategory", "SOURCE_CATEGORY_EXACT");
        var sourceCategoryId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            fixture.Item.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
            sourceCategoryIdentity, null, "grid.test.source-category", "1", "/item/source-category");
        var sourceCategoryAssertion = new CanonicalOrganizationalValueAssertion(
            sourceCategoryId, fixture.Item.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
            sourceCategoryIdentity, null, "grid.test.source-category", "1", "/item/source-category");
        CanonicalOrganizationalValueAssertion LocalizedCategory(string locale) => new(
            CanonicalOrganizationalValueAssertionId.DeriveV2(fixture.Item.Id, fixture.SourceRevisionId,
                CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode, sourceCategoryIdentity,
                "Exact category", "grid.test.source-category", "1", "/item/source-category", locale),
            fixture.Item.Id, fixture.SourceRevisionId, CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
            sourceCategoryIdentity, "Exact category", "grid.test.source-category", "1", "/item/source-category", locale);
        var englishCategory = LocalizedCategory("en-US");
        var frenchCategory = LocalizedCategory("fr-FR");
        Assert(englishCategory.Id != frenchCategory.Id &&
               EvidenceClaimContentId.DeriveV1(englishCategory) != EvidenceClaimContentId.DeriveV1(frenchCategory),
            "Organization locale is bound into both the versioned assertion and its evidence claim.");
        Assert(sourceCategoryAssertion.LanguageTag is null &&
               CanonicalOrganizationalValueAssertionId.DeriveV1(fixture.Item.Id, fixture.SourceRevisionId,
                   CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode, sourceCategoryIdentity, null,
                   "grid.test.source-category", "1", "/item/source-category") == sourceCategoryAssertion.Id,
            "Historical organization claims retain their V1 identities and unspecified locale.");
        AssertThrows<ArgumentException>(() => _ = LocalizedCategory("und"),
            "Unknown locale cannot become localized organization terminology.");
        var localizedFixture = (fixture with { OrganizationalValues = fixture.OrganizationalValues.Add(englishCategory) }).Repackage();
        Assert(CanonicalCatalogPackageKernel.Verify(localizedFixture.Package).IsStructurallyValid,
            "Locale-qualified organization assertions remain structurally evidence-bound.");
        var categoryFixture = (fixture with
        {
            OrganizationalValues = fixture.OrganizationalValues.Add(sourceCategoryAssertion),
        }).Repackage();
        CanonicalSelectorResult QueryV2(CanonicalNavigationPathId? path) =>
            CanonicalSelectorProjectionEngine.Query(
                categoryFixture.ToInput(CanonicalSelectorProjectionPolicy.V2),
                new CanonicalSelectorQuery(
                    categoryFixture.CatalogRevisionId, categoryFixture.CompositionId, KnowledgeKind.Item,
                    CanonicalSelectorProjectionPolicy.V2.Id, CanonicalSelectorProjectionPolicy.V2.ExactVersion,
                    path, null, true, false, EnglishLocale));
        var categoryWeapons = QueryV2(null).ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode);
        var exactCategory = QueryV2(categoryWeapons.PathId).ImmediateChildren.Single();
        var categorizedItem = QueryV2(exactCategory.PathId).ImmediateChildren.Single();
        Assert(exactCategory.DisplayAnchor == sourceCategoryIdentity.ExactRepresentation &&
               exactCategory.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier &&
               categorizedItem.KnowledgeRecordId == fixture.Item.Id,
            "Projection v2 derives a game-neutral Item source-category path from exact verified organizational values without inventing terminology.");

        var onlineClassification = Classification(
            fixture.Mission, fixture.SourceRevisionId, CanonicalProjectionSemantics.MissionOnline);
        var playlistIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.mission", "ActivityFamily", "Exact Rockstar Playlist");
        var playlistId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            fixture.Mission.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
            playlistIdentity, "Exact Rockstar Playlist", "grid.test.playlist", "1", "/playlist/name");
        var playlist = new CanonicalOrganizationalValueAssertion(
            playlistId, fixture.Mission.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
            playlistIdentity, "Exact Rockstar Playlist", "grid.test.playlist", "1", "/playlist/name");
        var missionFamilyFixture = (fixture with
        {
            Classifications = fixture.Classifications.Add(onlineClassification),
            OrganizationalValues = fixture.OrganizationalValues.Add(playlist),
        }).Repackage();
        CanonicalSelectorResult QueryV3(CanonicalNavigationPathId? path) =>
            CanonicalSelectorProjectionEngine.Query(
                missionFamilyFixture.ToInput(CanonicalSelectorProjectionPolicy.V3),
                new CanonicalSelectorQuery(
                    missionFamilyFixture.CatalogRevisionId, missionFamilyFixture.CompositionId,
                    KnowledgeKind.MissionQuest, CanonicalSelectorProjectionPolicy.V3.Id,
                    CanonicalSelectorProjectionPolicy.V3.ExactVersion, path, null, true, false, EnglishLocale));
        var online = QueryV3(null).ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode);
        var exactPlaylist = QueryV3(online.PathId).ImmediateChildren.Single();
        var playlistMission = QueryV3(exactPlaylist.PathId).ImmediateChildren.Single();
        Assert(exactPlaylist.DisplayAnchor == "Exact Rockstar Playlist" &&
               exactPlaylist.DisplayKind == CanonicalNavigationDisplayKind.SourceValue &&
               playlistMission.KnowledgeRecordId == fixture.Mission.Id,
            "Projection v3 derives Online to exact source-backed activity-family to one canonical MissionQuest ID.");
        var falseActorCategoryId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            fixture.Actor.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
            sourceCategoryIdentity, null, "grid.test.source-category", "1", "/actor/false-item-category");
        var falseActorCategory = new CanonicalOrganizationalValueAssertion(
            falseActorCategoryId, fixture.Actor.Id, fixture.SourceRevisionId,
            CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode,
            sourceCategoryIdentity, null, "grid.test.source-category", "1", "/actor/false-item-category");
        AssertThrows<InvalidDataException>(() =>
                (fixture with { OrganizationalValues = fixture.OrganizationalValues.Add(falseActorCategory) }).Repackage(),
            "The known Item source-category dimension cannot be attached to another knowledge kind.");

        var magicClassification = Classification(fixture.Item, fixture.SourceRevisionId, CanonicalProjectionSemantics.ItemMagic);
        var multiPathFixture = (fixture with { Classifications = fixture.Classifications.Add(magicClassification) }).Repackage();
        var multiRoot = Query(multiPathFixture, KnowledgeKind.Item, null, true);
        var magicPath = multiRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemMagicNode).PathId;
        var weaponsPath = multiRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode).PathId;
        var magicLevel = Query(multiPathFixture, KnowledgeKind.Item, magicPath, true);
        var weaponsLevel = Query(multiPathFixture, KnowledgeKind.Item, weaponsPath, true);
        var magicLeaf = magicLevel.ImmediateChildren.Single();
        var weaponsLeaf = weaponsLevel.ImmediateChildren.Single();
        Assert(magicLeaf.PathId != weaponsLeaf.PathId && magicLeaf.KnowledgeRecordId == fixture.Item.Id &&
               weaponsLeaf.KnowledgeRecordId == fixture.Item.Id &&
               CanonicalSelectorProjectionEngine.Select(magicLevel, magicLeaf.PathId).KnowledgeRecordId ==
               CanonicalSelectorProjectionEngine.Select(weaponsLevel, weaponsLeaf.PathId).KnowledgeRecordId,
            "Multiple evidence-backed paths retain distinct path identities while selecting one canonical record identity.");

        var locationRole = Classification(
            fixture.LocationParent, fixture.SourceRevisionId, new CanonicalSemanticRoleId("grid.location.role.business"));
        var locationWithRole = Query(
            (fixture with { Classifications = fixture.Classifications.Add(locationRole) }).Repackage(),
            KnowledgeKind.Location, null, true);
        Assert(locationWithRole.ImmediateChildren.All(value =>
                   value.OrganizationalSemanticId?.Value != "grid.location.role.business") &&
               locationWithRole.ImmediateChildren.Any(value => value.KnowledgeRecordId == fixture.LocationParent.Id),
            "Location semantic roles describe records but never manufacture navigation hierarchy.");

        var missionRoot = Query(fixture, KnowledgeKind.MissionQuest, null, true);
        var modFamily = missionRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionModNode);
        var modFamilyLevel = Query(fixture, KnowledgeKind.MissionQuest, modFamily.PathId, true);
        var exactModNode = modFamilyLevel.ImmediateChildren.Single();
        Assert(exactModNode.DisplayAnchor == fixture.ModIdentity.ExactRepresentation &&
               exactModNode.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier &&
               !exactModNode.IsSelectable,
            "Mod Mission organization explicitly exposes the registered native mod identifier, not a friendly folder name.");
        var originIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.record", "MissionQuest", "MISSION_BASE_ORIGIN");
        var originNativeId = NativeRecordIdentityId.DeriveV1(fixture.GameId, originIdentity);
        var originRecordId = KnowledgeRecordId.DeriveV1(
            fixture.GameId, fixture.GameVersion, fixture.Mission.ModVersion,
            fixture.SourceRevisionId, KnowledgeKind.MissionQuest, originNativeId);
        var originRecord = new CanonicalKnowledgeRecord(
            originRecordId, fixture.GameId, fixture.GameVersion, fixture.Mission.ModVersion,
            fixture.SourceRevisionId, KnowledgeKind.MissionQuest, originNativeId, originIdentity);
        var modifiedId = CanonicalRecordContributionAssertionId.DeriveV1(
            fixture.Mission.Id, fixture.SourceRevisionId, CanonicalRecordContributionKind.Modified,
            originRecord.Id, fixture.ModIdentity, fixture.Contributions.Single().ExactModVersion,
            "grid.test.contribution", "1", "/mission/mod");
        var modified = new CanonicalRecordContributionAssertion(
            modifiedId, fixture.Mission.Id, fixture.SourceRevisionId, CanonicalRecordContributionKind.Modified,
            originRecord.Id, fixture.ModIdentity, fixture.Contributions.Single().ExactModVersion,
            "grid.test.contribution", "1", "/mission/mod");
        var modifiedRecords = fixture.Records.Add(originRecord);
        var modifiedFixture = (fixture with
        {
            Records = modifiedRecords,
            Contributions = [modified],
            Applicability = new CanonicalApplicabilityProjection(
                fixture.CompositionId, modifiedRecords.Select(value => value.Id).ToImmutableArray(), [], "1"),
        }).Repackage();
        Assert(Query(modifiedFixture, KnowledgeKind.MissionQuest, null, true)
                   .ImmediateChildren.All(value => value.OrganizationalSemanticId != CanonicalProjectionSemantics.MissionModNode),
            "A mod modification of an existing MissionQuest does not create a second selectable mission under Mod.");

        var actorRoot = Query(fixture, KnowledgeKind.Actor, null, true);
        var npcFamily = actorRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorNpcNode);
        var npcLevel = Query(fixture, KnowledgeKind.Actor, npcFamily.PathId, true);
        var factionDimension = npcLevel.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorFactionDimensionNode);
        var factionLevel = Query(fixture, KnowledgeKind.Actor, factionDimension.PathId, true);
        Assert(factionLevel.ImmediateChildren.Single().DisplayAnchor == fixture.FactionValue &&
               factionLevel.ImmediateChildren.Single().DisplayKind == CanonicalNavigationDisplayKind.SourceValue,
            "Actor organization preserves an exact source-provided organizational value.");
        var secondFactionIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.actor", "faction", "Second Faction");
        var secondFactionId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            fixture.Actor.Id, fixture.SourceRevisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
            secondFactionIdentity, "Second Faction", "grid.test.organization", "1", "/actor/faction[1]");
        var secondFaction = new CanonicalOrganizationalValueAssertion(
            secondFactionId, fixture.Actor.Id, fixture.SourceRevisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
            secondFactionIdentity, "Second Faction", "grid.test.organization", "1", "/actor/faction[1]");
        var actorMultiFixture = (fixture with { OrganizationalValues = fixture.OrganizationalValues.Add(secondFaction) }).Repackage();
        var actorMultiRoot = Query(actorMultiFixture, KnowledgeKind.Actor, null, true);
        var actorNpc = actorMultiRoot.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorNpcNode);
        var actorNpcLevel = Query(actorMultiFixture, KnowledgeKind.Actor, actorNpc.PathId, true);
        var actorFaction = actorNpcLevel.ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorFactionDimensionNode);
        var actorFactionLevel = Query(actorMultiFixture, KnowledgeKind.Actor, actorFaction.PathId, true);
        Assert(actorFactionLevel.ImmediateChildren.Length == 2 &&
               actorFactionLevel.ImmediateChildren.Select(value => value.DisplayAnchor).SequenceEqual(
                   actorFactionLevel.ImmediateChildren.Select(value => value.DisplayAnchor).Order(StringComparer.Ordinal)),
            "An Actor may retain multiple authoritative faction paths with deterministic sibling ordering.");

        var inapplicable = (fixture with
        {
            Applicability = new CanonicalApplicabilityProjection(
                fixture.CompositionId,
                fixture.Records.Where(value => value.Id != fixture.UnclassifiedItem.Id).Select(value => value.Id).ToImmutableArray(),
                [fixture.UnclassifiedItem.Id],
                "1"),
        }).Repackage();
        Assert(Query(inapplicable, KnowledgeKind.Item, null, true).ImmediateChildren
                   .All(value => value.KnowledgeRecordId != fixture.UnclassifiedItem.Id),
            "Applicability filters visibility without changing canonical identity or terminology.");
        var inactiveMission = (fixture with
        {
            Applicability = new CanonicalApplicabilityProjection(
                fixture.CompositionId,
                fixture.Records.Where(value => value.Id != fixture.Mission.Id).Select(value => value.Id).ToImmutableArray(),
                [fixture.Mission.Id], "1"),
        }).Repackage();
        Assert(Query(inactiveMission, KnowledgeKind.MissionQuest, null, true).ImmediateChildren.IsEmpty,
            "An inactive mod-created mission prunes its now-empty Mod branch recursively.");

        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var permuted = (fixture with
            {
                Records = fixture.Records.Reverse().ToImmutableArray(),
                Terms = fixture.Terms.Reverse().ToImmutableArray(),
                Classifications = fixture.Classifications.Reverse().ToImmutableArray(),
            }).Repackage();
            var permutedResult = Query(permuted, KnowledgeKind.Item, null, true);
            Assert(itemRoot.ImmediateChildren.Select(NodeCoordinate).SequenceEqual(
                    permutedResult.ImmediateChildren.Select(NodeCoordinate)),
                "Projection ordering is independent of culture and source enumeration order.");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }

        var duplicateName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "Alpha", "/item/unclassified/name", "en");
        var identicalNames = (fixture with
        {
            Terms = fixture.Terms.Add(duplicateName),
            Classifications = fixture.Classifications
                .Where(value => value.KnowledgeRecordId != fixture.Item.Id).ToImmutableArray(),
        }).Repackage();
        var identicalRecords = Query(identicalNames, KnowledgeKind.Item, null, true).ImmediateChildren
            .Where(value => value.KnowledgeRecordId is not null).ToImmutableArray();
        Assert(identicalRecords.Length == 2 && identicalRecords.All(value => value.DisplayAnchor == "Alpha") &&
               identicalRecords.Select(value => value.KnowledgeRecordId!.Value.Value).SequenceEqual(
                   identicalRecords.Select(value => value.KnowledgeRecordId!.Value.Value).Order(StringComparer.Ordinal)),
            "Identical exact terminology is tie-broken by immutable canonical identity without renaming rows.");

        var englishName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "English Exact", "/item/multilingual/en-us", "en-US");
        var spanishName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "Español exacto", "/item/multilingual/es-es", "es-ES");
        var undeterminedName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "Aprendendo os esquemas", "/item/multilingual/und", "und");
        var sourceDefaultName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "Source default", "/item/multilingual/default");
        var multilingual = (fixture with
        {
            Terms = fixture.Terms.AddRange(ImmutableArray.Create(
                englishName, spanishName, undeterminedName, sourceDefaultName)),
        }).Repackage();
        var englishProjection = Query(multilingual, KnowledgeKind.Item, null, true)
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
        var spanishProjection = Query(
                multilingual, KnowledgeKind.Item, null, true, locale:
                new CanonicalTerminologyLocalePreference("es-ES", ["es"]))
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
        var frenchProjection = Query(
                multilingual, KnowledgeKind.Item, null, true, locale:
                new CanonicalTerminologyLocalePreference("fr-FR", ["fr"]))
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
        Assert(englishProjection.DisplayAnchor == "English Exact" &&
               spanishProjection.DisplayAnchor == "Español exacto" &&
               frenchProjection.DisplayAnchor == "Source default" &&
               englishProjection.ExactTerminologyAssertions.Contains(spanishName) &&
               englishProjection.ExactTerminologyAssertions.Contains(undeterminedName) &&
               !englishProjection.HasTerminologyConflict,
            "Projection selects exact requested locale, then approved fallback, then source default while preserving multilingual assertions and excluding undetermined language.");
        var englishFallbackName = new TerminologyAssertion(
            fixture.UnclassifiedItem.Id, fixture.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
            "English fallback", "/item/multilingual/en", "en");
        var fallbackOnly = (multilingual with
        {
            Terms = multilingual.Terms.Remove(englishName).Add(englishFallbackName),
        }).Repackage();
        var fallbackProjection = Query(
                fallbackOnly, KnowledgeKind.Item, null, true, locale:
                new CanonicalTerminologyLocalePreference("en-US", ["en"]))
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
        Assert(fallbackProjection.DisplayAnchor == "English fallback",
            "An explicitly approved language fallback wins over source-default and unrelated or undetermined terminology.");
        originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var permutedFallback = (fallbackOnly with
            {
                Terms = fallbackOnly.Terms.Reverse().ToImmutableArray(),
            }).Repackage();
            var permutedFallbackProjection = Query(
                    permutedFallback, KnowledgeKind.Item, null, true, locale:
                    new CanonicalTerminologyLocalePreference("en-US", ["en"]))
                .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
            Assert(permutedFallbackProjection.DisplayAnchor == fallbackProjection.DisplayAnchor,
                "Approved fallback selection is independent of culture and assertion enumeration order.");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
        var noDefault = (multilingual with
        {
            Terms = multilingual.Terms.Remove(sourceDefaultName).Remove(englishName),
        }).Repackage();
        var identifierFallback = Query(noDefault, KnowledgeKind.Item, null, true)
            .ImmediateChildren.Single(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id);
        Assert(identifierFallback.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier &&
               identifierFallback.DisplayAnchor == fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation,
            "An English query never substitutes unrelated Spanish or undetermined terminology when English is absent.");
        AssertThrows<ArgumentException>(() => new CanonicalTerminologyLocalePreference("und", []),
            "An undetermined language cannot be requested as a presentation locale.");
        AssertThrows<ArgumentException>(() => CanonicalSelectorProjectionEngine.Query(
                fixture.ToInput(),
                fixture.Query(KnowledgeKind.Item) with { CatalogCompositionId = new("other-composition") }),
            "Selector queries fail closed when catalog-composition coordinates differ.");
        var relabeledDefinitions = policy.OrganizationalDefinitions.Select(value =>
            value.SemanticId == CanonicalProjectionSemantics.ItemWeaponsNode
                ? new CanonicalOrganizationalDefinition(
                    value.SemanticId,
                    value.KnowledgeKind,
                    "Relabeled",
                    value.RequiredRoleId,
                    value.ParentSemanticId)
                : value).ToImmutableArray();
        var relabeledPolicy = new CanonicalSelectorProjectionPolicy(
            policy.Id, policy.ExactVersion, policy.Control, policy.OrderingPolicyId,
            policy.OrderingPolicyVersion, relabeledDefinitions);
        AssertThrows<InvalidDataException>(() => CanonicalSelectorProjectionEngine.Query(
                fixture.ToInput(relabeledPolicy), fixture.Query(KnowledgeKind.Item)),
            "A projection policy ID/version cannot be reused with different organizational vocabulary.");
        AssertThrows<InvalidDataException>(() =>
                (fixture with
                {
                    Classifications = [Classification(
                        fixture.UnknownRecord,
                        fixture.SourceRevisionId,
                        CanonicalProjectionSemantics.ItemWeapons)],
                }).Repackage(),
            "Projection assertions cannot enter a verified package when their record is outside its closure.");
        var repeatedItem = Query(fixture, KnowledgeKind.Item, null, true);
        Assert(itemRoot.RootPathId == repeatedItem.RootPathId &&
               itemRoot.CurrentPathId == repeatedItem.CurrentPathId &&
               itemRoot.ImmediateChildren.Select(NodeCoordinate)
                   .SequenceEqual(repeatedItem.ImmediateChildren.Select(NodeCoordinate)),
            "Repeated selector projection from identical inputs is deterministic.");

        var other = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.OtherContext,
            KnowledgeKind.Item,
            fixture.CatalogRevisionId,
            fixture.CompositionId,
            policy.Id,
            policy.ExactVersion,
            null,
            null,
            "ticket-other-context-1");
        Assert(other.KnowledgeRecordId is null && other.SelectedPathId is null &&
               other.UnresolvedOtherContextId == "ticket-other-context-1",
            "Other remains ticket-only unresolved context without a canonical record or path.");
        Assert(Enum.GetValues<KnowledgeKind>().All(kind =>
                !Query(fixture, kind, null, true).ImmediateChildren.Any(value =>
                    string.Equals(value.DisplayAnchor, "Other", StringComparison.Ordinal))),
            "Other never enters canonical projection data at any selector root.");
        AssertThrows<ArgumentException>(() => _ = new CanonicalSelectorSelection(
                CanonicalSelectorSelectionKind.OtherContext, KnowledgeKind.Item,
                fixture.CatalogRevisionId, fixture.CompositionId, policy.Id, policy.ExactVersion,
                identifierChild.PathId, fixture.LocationChild.Id, "ticket-other-context-1"),
            "Other context cannot masquerade as a canonical selection.");

        RunInstructionChecks(fixture, Assert, AssertThrows<ArgumentException>);
        RunResolverChecks(fixture, Assert, AssertThrows<ArgumentException>);
        checks += Plan2ProjectionChecks();
        Console.WriteLine($"PASS  Canonical selector, instruction, and resolver contracts ({checks} checks).");
        return checks;
    }

    private static void RunInstructionChecks(
        Fixture fixture,
        Action<bool, string> assert,
        Action<Action, string> assertArgument)
    {
        void Check(bool value, string message) => assert(value, message);
        void Arg(Action value, string message) => assertArgument(value, message);

        var packageId = new CatalogPackageId("grid.catalog-package.v5.sha256." + new string('7', 64));
        var first = Instruction(fixture, "  Keep THIS punctuation!\r\n", "fragment/A", packageId, fixture.Item.Id);
        var second = Instruction(fixture, "Conflicting exact source assertion", "fragment/B", packageId, fixture.Item.Id);
        var conflictId = InstructionConflictGroupId.DeriveV1([second.Id, first.Id], "grid.test.conflict", "1");
        var conflict = new InstructionConflictGroup(conflictId, [second.Id, first.Id], "grid.test.conflict", "1");
        var query = new InstructionQuery(
            fixture.CatalogRevisionId,
            fixture.CompositionId,
            [InstructionCategories.Requirements],
            fixture.Item.Id,
            fixture.SourceRevisionId,
            "en-US",
            true);
        var result = CanonicalInstructionQueryService.Query(
            query,
            [second, first],
            [conflict],
            [packageId],
            [fixture.Item.Id],
            KnowledgeCoverageState.Partial);
        Check(result.Assertions.Length == 2 &&
              result.Assertions.Any(value => value.Content.VerbatimText == "  Keep THIS punctuation!\r\n") &&
              result.ConflictGroups.Single().Id == conflict.Id &&
              result.CoverageState == KnowledgeCoverageState.Partial,
            "Instruction queries preserve exact source text, conflicts, and explicit coverage.");
        Check(result.Assertions.Select(value => value.Id.Value).SequenceEqual(
                result.Assertions.Select(value => value.Id.Value).Order(StringComparer.Ordinal)),
            "Instruction results are deterministically ordered by exact canonical coordinates.");
        var wrongLanguage = query with { ExactLanguageTag = "en-us" };
        Check(CanonicalInstructionQueryService.Query(
                wrongLanguage, [first, second], [conflict], [packageId], [fixture.Item.Id], KnowledgeCoverageState.Partial)
            .Assertions.IsEmpty,
            "Instruction language filtering is exact and never performs case normalization.");
        Check(CanonicalInstructionQueryService.Query(
                query with { IncludeConflicts = false }, [first, second], [conflict], [packageId], [fixture.Item.Id],
                KnowledgeCoverageState.Partial).ConflictGroups.IsEmpty,
            "Instruction conflict visibility is explicit and never silently chooses a winner.");
        Check(CanonicalInstructionQueryService.Query(
                query, [first], [], [], [fixture.Item.Id], KnowledgeCoverageState.Partial).Assertions.IsEmpty &&
              CanonicalInstructionQueryService.Query(
                query, [first], [], [packageId], [], KnowledgeCoverageState.Partial).Assertions.IsEmpty,
            "Package and record applicability independently filter instructions.");
        Check(Instruction(fixture, "  Keep THIS punctuation!\r\n", "fragment/A", packageId, fixture.Item.Id).Id == first.Id &&
              Instruction(fixture, "changed", "fragment/A", packageId, fixture.Item.Id).Id != first.Id &&
              Instruction(fixture, "  Keep THIS punctuation!\r\n", "fragment/changed", packageId, fixture.Item.Id).Id != first.Id,
            "Instruction identities are stable and change with exact content digest or source locator.");
        var receiptId = new EvidenceReceiptId("grid.evidence-receipt.v2.sha256." + new string('6', 64));
        var claimId = EvidenceClaimContentId.DeriveV1(first);
        var bindingId = InstructionEvidenceBindingId.DeriveV3(
            receiptId, first.Id, fixture.SourceRevisionId, first.SourceLocator.ExactFieldPathOrFragment, claimId,
            EvidenceVerificationKind.FileVerified);
        var binding = new InstructionEvidenceBinding(
            bindingId, receiptId, first.Id, fixture.SourceRevisionId,
            first.SourceLocator.ExactFieldPathOrFragment, claimId, EvidenceVerificationKind.FileVerified);
        Check(binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(first) &&
              binding.ExactLocator == first.SourceLocator.ExactFieldPathOrFragment,
            "Instruction evidence bindings identify the exact deterministic assertion content and locator.");
        Arg(() => _ = new InstructionEvidenceBinding(
                binding.Id, receiptId, first.Id, fixture.SourceRevisionId, "fragment/false", claimId,
                EvidenceVerificationKind.FileVerified),
            "Instruction evidence bindings reject a substituted source locator under the original binding identity.");
        var legacyReceiptId = new EvidenceReceiptId("grid.evidence-receipt.v1.sha256." + new string('7', 64));
        var legacyId = InstructionEvidenceBindingId.DeriveV1(
            legacyReceiptId, first.Id, fixture.SourceRevisionId,
            first.SourceLocator.ExactFieldPathOrFragment, claimId);
        var legacy = new InstructionEvidenceBinding(
            legacyId, legacyReceiptId, first.Id, fixture.SourceRevisionId,
            first.SourceLocator.ExactFieldPathOrFragment, claimId);
        Check(legacy.Id.AlgorithmVersion == InstructionEvidenceBindingId.LegacyAlgorithmVersion &&
              legacy.Verification is null,
            "Legacy v1 Instruction bindings retain their original identity and absent evidence-class field.");
        Arg(() => _ = new InstructionEvidenceBinding(
                legacyId, legacyReceiptId, first.Id, fixture.SourceRevisionId,
                first.SourceLocator.ExactFieldPathOrFragment, claimId,
                EvidenceVerificationKind.FileVerified),
            "A v1 Instruction binding ID cannot be paired with v2 evidence-class content.");
        var v2Id = InstructionEvidenceBindingId.DeriveV2(
            legacyReceiptId, first.Id, fixture.SourceRevisionId,
            first.SourceLocator.ExactFieldPathOrFragment, claimId, EvidenceVerificationKind.FileVerified);
        Arg(() => _ = new InstructionEvidenceBinding(
                v2Id, legacyReceiptId, first.Id, fixture.SourceRevisionId,
                first.SourceLocator.ExactFieldPathOrFragment, claimId),
            "A v2 Instruction binding ID cannot be paired with v1 content that omits evidence class.");
        Arg(() => _ = new InstructionContent(InstructionRetentionMode.Verbatim, null, [], ContentDigest.ComputeSha256([])),
            "Verbatim instruction retention rejects absent source text.");
        Arg(() => _ = new InstructionContent(
                InstructionRetentionMode.Verbatim, "exact", [], ContentDigest.ComputeSha256("different"u8)),
            "Retained Instruction text cannot be paired with an unrelated content digest.");
        Arg(() => _ = new InstructionContent(
                InstructionRetentionMode.ReferenceOnly, "invented", [], ContentDigest.ComputeSha256("invented"u8)),
            "Reference-only instruction retention cannot carry synthesized prose.");
        Arg(() => _ = new InstructionAssertion(
                first.Id, fixture.SourceRevisionId, first.SourceScope, first.NativeIdentity,
                InstructionCategories.Requirements, null, null, "en-US", first.Content,
                first.SourceLocator, first.ApplicableRecordIds, first.ApplicablePackageIds),
            "Classified instructions require an explicit deterministic mapping method and version.");
    }

    private static void RunResolverChecks(
        Fixture fixture,
        Action<bool, string> assert,
        Action<Action, string> assertArgument)
    {
        void Check(bool value, string message) => assert(value, message);
        void Arg(Action value, string message) => assertArgument(value, message);
        EvidenceReceiptId Receipt(KnowledgeRecordId id, string locator) => fixture.Package.Payload.EvidenceBindings
            .Single(value => value.KnowledgeRecordId == id &&
                             value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                             value.ClaimLocator == locator).EvidenceReceiptId;
        ResolverEvidenceFeature Feature(
            KnowledgeRecordId id,
            ResolverEvidenceFeatureKind kind,
            string coordinate,
            EvidenceReceiptId receipt) => new(id, kind, coordinate, [receipt]);
        DeterministicResolverInput Input(params ResolverEvidenceFeature[] features) => new(
            DeterministicResolverPolicy.V1,
            fixture.CatalogRevisionId,
            fixture.CompositionId,
            fixture.GameId,
            KnowledgeKind.Item,
            [fixture.Item.Id, fixture.UnclassifiedItem.Id, fixture.Actor.Id, fixture.UnknownRecord.Id],
            features.ToImmutableArray());

        var itemIdentityReceipt = Receipt(fixture.Item.Id, "/identity");
        var itemVersionReceipt = Receipt(fixture.Item.Id, "/version");
        var otherIdentityReceipt = Receipt(fixture.UnclassifiedItem.Id, "/identity");
        var otherVersionReceipt = Receipt(fixture.UnclassifiedItem.Id, "/version");
        var resolved = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation, otherIdentityReceipt)),
            fixture.Package, fixture.Applicability);
        Check(resolved.Outcome == DeterministicResolverOutcome.Resolved &&
              resolved.ResolvedKnowledgeRecordId == fixture.Item.Id &&
              resolved.Explanation.WinningScore == 1300 &&
              resolved.Explanation.RunnerUpScore == 1000 &&
              resolved.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.None,
            "A unique authoritative candidate resolves only after meeting score, strong-evidence, and margin policy.");

        var duplicateFeature = Feature(
            fixture.Item.Id,
            ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
            fixture.Item.NativeIdentity.ExactRepresentation,
            itemIdentityReceipt);
        var deduplicated = DeterministicCanonicalResolver.Resolve(Input(
            duplicateFeature, duplicateFeature,
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt)), fixture.Package, fixture.Applicability);
        Check(deduplicated.Explanation.WinningScore == 1300 &&
              deduplicated.Explanation.CandidateScores.Single(value => value.KnowledgeRecordId == fixture.Item.Id)
                  .CountedFeatures.Length == 2,
            "Duplicate resolver features cannot inflate a candidate score.");

        var tie = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation, otherIdentityReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, otherVersionReceipt)), fixture.Package, fixture.Applicability);
        Check(tie.Outcome == DeterministicResolverOutcome.Unresolved &&
              tie.ResolvedKnowledgeRecordId is null &&
              tie.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.Tie,
            "Equal authoritative candidates remain unresolved.");

        var below = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactPrimaryTerminology,
                "not-a-registered-name", itemIdentityReceipt)), fixture.Package, fixture.Applicability);
        Check(below.Outcome == DeterministicResolverOutcome.Unresolved &&
              below.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.BelowThreshold,
            "Name-only similarity cannot promote a canonical resolution.");

        var weak = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt)), fixture.Package, fixture.Applicability);
        Check(weak.Outcome == DeterministicResolverOutcome.Unresolved &&
              weak.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.BelowThreshold,
            "Accumulated weak context cannot replace a strong authoritative identity feature.");

        var margin = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation, otherIdentityReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.FileVerifiedIdentity,
                otherVersionReceipt.Value, otherVersionReceipt)), fixture.Package, fixture.Applicability);
        Check(margin.Outcome == DeterministicResolverOutcome.Unresolved &&
              margin.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.InsufficientMargin,
            "A winning score without the required deterministic margin remains unresolved.");

        var replay = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation, otherIdentityReceipt)),
            fixture.Package, fixture.Applicability);
        Check(replay.Outcome == resolved.Outcome &&
              replay.ResolvedKnowledgeRecordId == resolved.ResolvedKnowledgeRecordId &&
              replay.Explanation.DeterministicDigest == resolved.Explanation.DeterministicDigest,
            "Resolver explanation and outcome are invariant to feature enumeration order.");
        Check(resolved.Explanation.CandidateScores.All(value =>
                  value.KnowledgeRecordId != fixture.Actor.Id && value.KnowledgeRecordId != fixture.UnknownRecord.Id),
            "Resolver candidates are constrained to already registered records of the required game and knowledge kind.");

        var proposal = new ModelCanonicalProposal(
            new ModelCanonicalProposalId("proposal-1"), KnowledgeKind.Item, ["span:1"],
            [fixture.Item.Id, fixture.Actor.Id, fixture.UnknownRecord.Id], "model-run-1");
        var validated = CanonicalModelProposalValidator.Validate(proposal, fixture.Records);
        Check(validated.ValidExistingCandidateIds.SequenceEqual([fixture.Item.Id]) &&
              validated.RejectedCandidateIds.Contains(fixture.Actor.Id) &&
              validated.RejectedCandidateIds.Contains(fixture.UnknownRecord.Id),
            "Model proposals can reference only preexisting canonical records of the requested kind.");
        Check(typeof(ModelCanonicalProposal).GetProperties().All(property =>
                !property.Name.Contains("Evidence", StringComparison.Ordinal) &&
                !property.Name.Contains("Terminology", StringComparison.Ordinal) &&
                !property.Name.Contains("Alias", StringComparison.Ordinal)),
            "The model boundary cannot carry newly invented evidence, terminology, or aliases.");
        Arg(() => DeterministicCanonicalResolver.Resolve(new(
                DeterministicResolverPolicy.V1, fixture.CatalogRevisionId, fixture.CompositionId,
                fixture.GameId, KnowledgeKind.Item, [fixture.Item.Id],
                [Feature(fixture.UnclassifiedItem.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                    fixture.UnclassifiedItem.NativeIdentity.ExactRepresentation, otherIdentityReceipt)]),
                fixture.Package, fixture.Applicability),
            "Resolver evidence cannot silently introduce a candidate outside the proposed canonical ID set.");
        Arg(() => _ = new ResolverEvidenceFeature(
                fixture.Item.Id, (ResolverEvidenceFeatureKind)999, "bad", []),
            "Unknown resolver evidence feature kinds fail closed.");
        Arg(() => _ = new ResolverEvidenceFeature(
                fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, []),
            "Resolver features cannot exist without supporting evidence receipts.");
        Arg(() => DeterministicCanonicalResolver.Resolve(
                Input() with
                {
                    Policy = new DeterministicResolverPolicy("grid.canonical-resolver", "1", 0, 0, 0),
                },
                fixture.Package,
                fixture.Applicability),
            "Resolver callers cannot weaken the immutable v1 thresholds.");
        var zeroFeature = DeterministicCanonicalResolver.Resolve(Input(), fixture.Package, fixture.Applicability);
        Check(zeroFeature.Outcome == DeterministicResolverOutcome.Unresolved &&
              zeroFeature.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.BelowThreshold,
            "A candidate with zero verified features cannot resolve.");
        var nonexistentReceipt = new EvidenceReceiptId(
            "grid.evidence-receipt.v1.sha256." + new string('8', 64));
        var nonexistent = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, nonexistentReceipt)),
            fixture.Package,
            fixture.Applicability);
        Check(nonexistent.Outcome == DeterministicResolverOutcome.Unresolved &&
              nonexistent.Explanation.CandidateScores.Single(value => value.KnowledgeRecordId == fixture.Item.Id)
                  .RejectedFeatures.Single().RejectionReason == ResolverFeatureRejectionReason.EvidenceReceiptNotFound,
            "Resolver features backed by nonexistent receipts are rejected and explained.");
        var contradictory = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                "CONTRADICTORY_NATIVE_IDENTITY", itemIdentityReceipt)),
            fixture.Package,
            fixture.Applicability);
        Check(contradictory.Outcome == DeterministicResolverOutcome.Unresolved &&
              contradictory.Explanation.UnresolvedReason ==
                  DeterministicResolverUnresolvedReason.AuthoritativeIdentityConflict &&
              contradictory.Explanation.AuthoritativeConflictCoordinates.Any(value =>
                  value.Contains("domain:ExactSourceNativeIdentity:", StringComparison.Ordinal)),
            "One receipt cannot score contradictory exact identity coordinates and the conflict is explicit.");
        var identityReceipt = fixture.Package.Payload.FileEvidenceReceipts.Single(value =>
            value.Id == itemIdentityReceipt);
        var heterogeneous = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactArtifactRecordLocator,
                identityReceipt.Receipt.NativeRecordLocator, itemIdentityReceipt)),
            fixture.Package,
            fixture.Applicability);
        Check(heterogeneous.Explanation.UnresolvedReason !=
                  DeterministicResolverUnresolvedReason.AuthoritativeIdentityConflict &&
              heterogeneous.Explanation.AuthoritativeConflictCoordinates.IsEmpty &&
              heterogeneous.Explanation.CandidateScores.Single(value => value.KnowledgeRecordId == fixture.Item.Id)
                  .RejectedFeatures.Any(value => value.RejectionReason == ResolverFeatureRejectionReason.DuplicateEvidence),
            "One receipt may prove heterogeneous native-identity and record-locator coordinates without a false conflict or duplicate score.");
        var actorIdentityReceipt = Receipt(fixture.Actor.Id, "/identity");
        var wrongKindPoison = DeterministicCanonicalResolver.Resolve(Input(
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Item.NativeIdentity.ExactRepresentation, itemIdentityReceipt),
            Feature(fixture.Item.Id, ResolverEvidenceFeatureKind.ExactGameVersion,
                fixture.GameVersion.ExactRepresentation, itemVersionReceipt),
            Feature(fixture.Actor.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                fixture.Actor.NativeIdentity.ExactRepresentation, actorIdentityReceipt),
            Feature(fixture.Actor.Id, ResolverEvidenceFeatureKind.ExactSourceNativeIdentity,
                "CONTRADICTORY_WRONG_KIND_IDENTITY", actorIdentityReceipt)),
            fixture.Package,
            fixture.Applicability);
        Check(wrongKindPoison.Outcome == DeterministicResolverOutcome.Resolved &&
              wrongKindPoison.ResolvedKnowledgeRecordId == fixture.Item.Id &&
              wrongKindPoison.Explanation.UnresolvedReason == DeterministicResolverUnresolvedReason.None &&
              wrongKindPoison.Explanation.AuthoritativeConflictCoordinates.IsEmpty,
            "A verified record outside the required knowledge kind cannot poison eligible-candidate conflict detection.");
    }

    private static CanonicalSelectorResult Query(
        Fixture fixture,
        KnowledgeKind kind,
        CanonicalNavigationPathId? path,
        bool includeIdentifierOnly,
        string? search = null,
        CanonicalTerminologyLocalePreference? locale = null) => CanonicalSelectorProjectionEngine.Query(
        fixture.ToInput(),
        fixture.Query(kind) with
        {
            CurrentPathId = path,
            IncludeIdentifierOnly = includeIdentifierOnly,
            SearchText = search,
            TerminologyLocale = locale ?? EnglishLocale,
        });

    private static string NodeCoordinate(CanonicalNavigationNode value) => string.Join(
        "\u001f",
        value.NodeId.Value,
        value.PathId.Value,
        value.ParentPathId?.Value ?? string.Empty,
        value.DisplayAnchor,
        ((int)value.DisplayKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
        value.KnowledgeRecordId?.Value ?? string.Empty);

    private static bool ContainsKnowledgeRecordInSelector(
        Fixture fixture,
        KnowledgeKind kind,
        KnowledgeRecordId recordId,
        CanonicalSelectorProjectionPolicy policy,
        bool inspectionMode = false)
    {
        CanonicalSelectorResult Query(CanonicalNavigationPathId? path) =>
            CanonicalSelectorProjectionEngine.Query(
                fixture.ToInput(policy),
                new(
                    fixture.CatalogRevisionId,
                    fixture.CompositionId,
                    kind,
                    policy.Id,
                    policy.ExactVersion,
                    path,
                    null,
                    true,
                    inspectionMode,
                    new("en-US", [])));

        bool Walk(CanonicalNavigationPathId? path)
        {
            foreach (var child in Query(path).ImmediateChildren)
            {
                if (child.KnowledgeRecordId == recordId)
                    return true;

                if (Walk(child.PathId))
                    return true;
            }

            return false;
        }

        return Walk(null);
    }

    private static int Plan2ProjectionChecks()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var seed = Fixture.Create(enhanced: true);
        CanonicalOrganizationalValueAssertion Organization(CanonicalKnowledgeRecord record,
            CanonicalOrganizationalSemanticId dimension, string key, string text, string locale = "en-US")
        {
            var native = SourceNativeIdentifier.FromExactUtf8("grid.test.organization", "Value", key);
            var id = CanonicalOrganizationalValueAssertionId.DeriveV2(record.Id, seed.SourceRevisionId,
                dimension, native, text, "grid.test.organization", "1", "/organization", locale);
            return new(id, record.Id, seed.SourceRevisionId, dimension, native, text,
                "grid.test.organization", "1", "/organization", locale);
        }
        var fixture = (seed with
        {
            Terms = seed.Terms.AddRange(new TerminologyAssertion[] {
                new(seed.Item.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact Vehicle", "/vehicle/name", "en-US"),
                new(seed.Actor.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact Actor Type", "/actor/name", "en-US"),
            }),
            Classifications = seed.Classifications.Where(value => value.KnowledgeRecordId != seed.Item.Id)
                .Concat(new[] {
                    Classification(seed.Item, seed.SourceRevisionId, CanonicalProjectionSemantics.ItemVehicles),
                    Classification(seed.Item, seed.SourceRevisionId, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                    Classification(seed.Actor, seed.SourceRevisionId, CanonicalProjectionSemantics.ActorGenericType),
                    Classification(seed.Actor, seed.SourceRevisionId, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                }).ToImmutableArray(),
            OrganizationalValues = seed.OrganizationalValues.AddRange(new[] {
                Organization(seed.Item, CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, "CLASS_1", "Exact Class"),
                Organization(seed.Actor, CanonicalProjectionSemantics.ActorSourceCategoryDimensionNode, "CATEGORY_1", "Exact Category"),
            }),
        }).Repackage();
        var gtaPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(seed.GameId);
        CanonicalSelectorResult QueryGta(Fixture value, KnowledgeKind kind, CanonicalNavigationPathId? path = null, bool inspection = false) =>
            CanonicalSelectorProjectionEngine.Query(value.ToInput(gtaPolicy),
                new(value.CatalogRevisionId, value.CompositionId, kind, gtaPolicy.Id, gtaPolicy.ExactVersion,
                    path, null, true, inspection, new("en-US", [])));
        var vehicles = QueryGta(fixture, KnowledgeKind.Item).ImmediateChildren.Single();
        Assert(vehicles.OrganizationalSemanticId == CanonicalProjectionSemantics.ItemVehiclesNode && !vehicles.IsSelectable,
            "Vehicles is an organizational node; technical Items remain absent from normal Enhanced presentation.");
        var vehicleClass = QueryGta(fixture, KnowledgeKind.Item, vehicles.PathId).ImmediateChildren.Single();
        var vehicle = QueryGta(fixture, KnowledgeKind.Item, vehicleClass.PathId).ImmediateChildren.Single();
        Assert(vehicleClass.DisplayAnchor == "Exact Class" && vehicle.DisplayAnchor == "Exact Vehicle" && vehicle.KnowledgeRecordId == fixture.Item.Id,
            "Vehicles use exact localized source class and title without changing canonical identity.");
        var npc = QueryGta(fixture, KnowledgeKind.Actor).ImmediateChildren.Single();
        var generic = QueryGta(fixture, KnowledgeKind.Actor, npc.PathId).ImmediateChildren.Single(value =>
            value.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorGenericTypeNode);
        var category = QueryGta(fixture, KnowledgeKind.Actor, generic.PathId).ImmediateChildren.Single();
        Assert(generic.OrganizationalSemanticId == CanonicalProjectionSemantics.ActorGenericTypeNode && category.DisplayAnchor == "Exact Category" &&
               QueryGta(fixture, KnowledgeKind.Actor, category.PathId).ImmediateChildren.Single().DisplayAnchor == "Exact Actor Type",
            "Generic NPC types retain the frozen NPC/type/source-category path.");
        var conflicting = (fixture with { Terms = fixture.Terms.Add(new(fixture.Item.Id, fixture.SourceRevisionId,
            TerminologyAssertionRole.PrimaryName, "Conflicting Vehicle", "/vehicle/other-name", "en-US")) }).Repackage();
        Assert(!ContainsKnowledgeRecordInSelector(conflicting, KnowledgeKind.Item, fixture.Item.Id, gtaPolicy),
            "Conflicting exact-locale primary names do not become normal selectable options.");
        var untranslated = (fixture with { Terms = fixture.Terms.Where(value => value.LanguageTag != "en-US").ToImmutableArray() }).Repackage();
        Assert(QueryGta(untranslated, KnowledgeKind.Item).ImmediateChildren.IsEmpty,
            "Enhanced presentation does not silently fall back to unapproved source languages.");
        var unknownClass = (fixture with { OrganizationalValues = fixture.OrganizationalValues
            .Where(value => value.DimensionId != CanonicalProjectionSemantics.ItemVehicleClassDimensionNode).ToImmutableArray() }).Repackage();
        Assert(QueryGta(unknownClass, KnowledgeKind.Item).ImmediateChildren.IsEmpty,
            "A vehicle without a resolved localized source class does not receive invented navigation.");
        var conflictingClass = (fixture with { OrganizationalValues = fixture.OrganizationalValues.Add(
            Organization(fixture.Item, CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, "CLASS_1", "Conflicting Class")) }).Repackage();
        Assert(QueryGta(conflictingClass, KnowledgeKind.Item).ImmediateChildren.IsEmpty,
            "Conflicting class terminology cannot be silently selected for presentation.");
        var sameName = (fixture with
        {
            Terms = fixture.Terms.Add(new(fixture.UnclassifiedItem.Id, fixture.SourceRevisionId,
                TerminologyAssertionRole.PrimaryName, "Exact Vehicle", "/vehicle/name", "en-US")),
            Classifications = fixture.Classifications.AddRange(new[] {
                Classification(fixture.UnclassifiedItem, fixture.SourceRevisionId, CanonicalProjectionSemantics.ItemVehicles),
                Classification(fixture.UnclassifiedItem, fixture.SourceRevisionId, CanonicalProjectionSemantics.SelectorPlayerAddressable),
            }),
            OrganizationalValues = fixture.OrganizationalValues.Add(Organization(fixture.UnclassifiedItem,
                CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, "CLASS_1", "Exact Class")),
        }).Repackage();
        var sameNameRoot = QueryGta(sameName, KnowledgeKind.Item).ImmediateChildren.Single();
        var sameNameClass = QueryGta(sameName, KnowledgeKind.Item, sameNameRoot.PathId).ImmediateChildren.Single();
        var sameNameLeaves = QueryGta(sameName, KnowledgeKind.Item, sameNameClass.PathId).ImmediateChildren;
        Assert(sameNameLeaves.Length == 2 && sameNameLeaves.Select(value => value.KnowledgeRecordId).Distinct().Count() == 2 &&
               sameNameLeaves.All(value => value.DisplayAnchor == "Exact Vehicle"),
            "Equal vehicle display names retain distinct selectable model identities.");
        Assert(QueryGta(fixture, KnowledgeKind.Item, inspection: true).ImmediateChildren.Any(value => value.KnowledgeRecordId == fixture.UnclassifiedItem.Id),
            "Identifier-only inspection remains available independently from normal eligibility.");
        return checks;
    }

    private static CanonicalSemanticClassificationAssertion Classification(
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId revisionId,
        CanonicalSemanticRoleId role)
    {
        var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
            record.Id, revisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
        return new(id, record.Id, revisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
    }

    private static InstructionAssertion Instruction(
        Fixture fixture,
        string text,
        string locator,
        CatalogPackageId packageId,
        KnowledgeRecordId recordId)
    {
        var content = new InstructionContent(
            InstructionRetentionMode.Verbatim,
            text,
            [],
            ContentDigest.ComputeSha256(System.Text.Encoding.UTF8.GetBytes(text)));
        var native = SourceNativeIdentifier.FromExactUtf8("grid.test.instructions", "instruction", locator);
        var sourceLocator = new InstructionSourceLocator(
            fixture.ArtifactId,
            native,
            locator,
            null);
        var scope = KnowledgeSourceScope.BaseGame(fixture.GameId, fixture.GameVersion);
        var id = InstructionAssertionId.DeriveV1(
            fixture.SourceRevisionId,
            scope,
            native,
            InstructionCategories.Requirements,
            content.RetentionMode,
            content.ContentDigest,
            sourceLocator,
            "en-US",
            "grid.test.instruction-category",
            "1",
            [recordId],
            [packageId]);
        return new(
            id,
            fixture.SourceRevisionId,
            scope,
            native,
            InstructionCategories.Requirements,
            "grid.test.instruction-category",
            "1",
            "en-US",
            content,
            sourceLocator,
            [recordId],
            [packageId]);
    }

    internal sealed record Fixture(
        GameId GameId,
        SourceNativeVersion GameVersion,
        CatalogRevisionId CatalogRevisionId,
        CatalogCompositionId CompositionId,
        CatalogSourceRevisionId SourceRevisionId,
        SourceArtifactId ArtifactId,
        ImmutableArray<CanonicalKnowledgeRecord> Records,
        ImmutableArray<TerminologyAssertion> Terms,
        ImmutableArray<RelationshipAssertion> Relationships,
        ImmutableArray<CanonicalSemanticClassificationAssertion> Classifications,
        ImmutableArray<CanonicalRecordContributionAssertion> Contributions,
        ImmutableArray<CanonicalOrganizationalValueAssertion> OrganizationalValues,
        CanonicalApplicabilityProjection Applicability,
        CanonicalKnowledgeRecord LocationParent,
        CanonicalKnowledgeRecord LocationChild,
        CanonicalKnowledgeRecord LocationUnresolved,
        CanonicalKnowledgeRecord Mission,
        CanonicalKnowledgeRecord Item,
        CanonicalKnowledgeRecord UnclassifiedItem,
        CanonicalKnowledgeRecord Actor,
        CanonicalKnowledgeRecord Player,
        CanonicalKnowledgeRecord UnknownRecord,
        TerminologyAssertion LocationParentName,
        SourceNativeIdentifier ModIdentity,
        string FactionValue)
    {
        public CanonicalCatalogPackage Package { get; init; } = null!;

        public CanonicalSelectorProjectionInput ToInput(
            CanonicalSelectorProjectionPolicy? policy = null) =>
            CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                Package,
                CompositionId,
                policy ?? CanonicalSelectorProjectionPolicy.V1,
                Applicability);

        public Fixture Repackage()
        {
            var package = CreateVerifiedPackage(this);
            return this with { Package = package, CatalogRevisionId = package.Manifest.CatalogRevisionId };
        }

        public CanonicalSelectorQuery Query(KnowledgeKind kind) => new(
            CatalogRevisionId,
            CompositionId,
            kind,
            CanonicalSelectorProjectionPolicy.V1.Id,
            CanonicalSelectorProjectionPolicy.V1.ExactVersion,
            null,
            null,
            true,
            false,
            EnglishLocale);

        public static Fixture Create(bool enhanced = false)
        {
            var gameId = enhanced ? ProductionGridCatalogService.GrandTheftAutoVEnhancedId : new GameId("game.selector-fixture");
            var gameVersion = SourceNativeVersion.FromExactUtf8("grid.test.game", "build-1");
            var artifactDigest = ContentDigest.ComputeSha256("selector-fixture"u8);
            var artifactId = SourceArtifactId.DeriveV1(artifactDigest);
            var sourceNative = SourceNativeIdentifier.FromExactUtf8("grid.test.source", "catalog", "selector-fixture");
            var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, sourceNative);
            var descriptor = CreateAdapterDescriptor(gameId);
            var revisionId = CatalogSourceRevisionId.DeriveV2(sourceId, gameVersion, [artifactId], descriptor.RevisionId);
            var modIdentity = SourceNativeIdentifier.FromExactUtf8("grid.test.mod", "mod", "Exact.Mod.Identity");
            var modVersion = SourceNativeVersion.FromExactUtf8("grid.test.mod", "2.0");
            CanonicalKnowledgeRecord Record(KnowledgeKind kind, string native)
            {
                var identity = SourceNativeIdentifier.FromExactUtf8("grid.test.record", kind.ToString(), native);
                var nativeId = NativeRecordIdentityId.DeriveV1(gameId, identity);
                var id = KnowledgeRecordId.DeriveV1(gameId, gameVersion, modVersion, revisionId, kind, nativeId);
                return new(id, gameId, gameVersion, modVersion, revisionId, kind, nativeId, identity);
            }

            var locationParent = Record(KnowledgeKind.Location, "LOC_PARENT");
            var locationChild = Record(KnowledgeKind.Location, "LOC_CHILD_NATIVE");
            var locationUnresolved = Record(KnowledgeKind.Location, "LOC_UNRESOLVED");
            var mission = Record(KnowledgeKind.MissionQuest, "MISSION_MOD_1");
            var item = Record(KnowledgeKind.Item, "ITEM_WEAPON_1");
            var unclassifiedItem = Record(KnowledgeKind.Item, "ITEM_NATIVE_ONLY");
            var actor = Record(KnowledgeKind.Actor, "ACTOR_NPC_1");
            var player = Record(KnowledgeKind.Actor, "ACTOR_PLAYER_1");
            var otherGame = new GameId("game.other");
            var unknownNative = SourceNativeIdentifier.FromExactUtf8("grid.test.record", "Item", "UNKNOWN");
            var unknownNativeId = NativeRecordIdentityId.DeriveV1(otherGame, unknownNative);
            var unknownId = KnowledgeRecordId.DeriveV1(otherGame, gameVersion, null, revisionId, KnowledgeKind.Item, unknownNativeId);
            var unknown = new CanonicalKnowledgeRecord(
                unknownId, otherGame, gameVersion, null, revisionId, KnowledgeKind.Item, unknownNativeId, unknownNative);

            var locationName = new TerminologyAssertion(
                locationParent.Id, revisionId, TerminologyAssertionRole.PrimaryName,
                "  Zône—Exact  ", "/location/name", "en");
            var itemAlpha = new TerminologyAssertion(
                item.Id, revisionId, TerminologyAssertionRole.PrimaryName, "Alpha", "/item/name/a", "en");
            var itemZulu = new TerminologyAssertion(
                item.Id, revisionId, TerminologyAssertionRole.PrimaryName, "Zulu", "/item/name/z", "en");
            var missionName = new TerminologyAssertion(
                mission.Id, revisionId, TerminologyAssertionRole.PrimaryName, "Mission Exact", "/mission/name", "en");
            var actorName = new TerminologyAssertion(
                actor.Id, revisionId, TerminologyAssertionRole.PrimaryName, "Actor Exact", "/actor/name", "en");
            var playerName = new TerminologyAssertion(
                player.Id, revisionId, TerminologyAssertionRole.PrimaryName, "Player Exact", "/actor/name", "en");

            var parentTarget = locationParent.NativeIdentity;
            var resolved = new RelationshipAssertion(
                locationChild.Id, revisionId, LocationRelationshipSemantics.ContainedBy,
                "contained-by", "/location/parent", parentTarget, locationParent.Id);
            var unresolvedTarget = SourceNativeIdentifier.FromExactUtf8(
                "grid.test.record", "Location", "MISSING_PARENT");
            var unresolved = new RelationshipAssertion(
                locationUnresolved.Id, revisionId, LocationRelationshipSemantics.ContainedBy,
                "contained-by", "/location/parent", unresolvedTarget);

            var classifications = ImmutableArray.Create(
                Classification(mission, revisionId, CanonicalProjectionSemantics.MissionMod),
                Classification(item, revisionId, CanonicalProjectionSemantics.ItemWeapons),
                Classification(actor, revisionId, CanonicalProjectionSemantics.ActorNpc),
                Classification(player, revisionId, CanonicalProjectionSemantics.ActorPlayerCharacter));
            var contributionId = CanonicalRecordContributionAssertionId.DeriveV1(
                mission.Id, revisionId, CanonicalRecordContributionKind.Introduced, null,
                modIdentity, modVersion, "grid.test.contribution", "1", "/mission/mod");
            var contribution = new CanonicalRecordContributionAssertion(
                contributionId, mission.Id, revisionId, CanonicalRecordContributionKind.Introduced, null,
                modIdentity, modVersion, "grid.test.contribution", "1", "/mission/mod");
            const string factionValue = "  Faction—Exact  ";
            var factionIdentity = SourceNativeIdentifier.FromExactUtf8("grid.test.actor", "faction", factionValue);
            var organizationalId = CanonicalOrganizationalValueAssertionId.DeriveV1(
                actor.Id, revisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
                factionIdentity, factionValue, "grid.test.organization", "1", "/actor/faction");
            var organizational = new CanonicalOrganizationalValueAssertion(
                organizationalId, actor.Id, revisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
                factionIdentity, factionValue, "grid.test.organization", "1", "/actor/faction");

            var records = ImmutableArray.Create(
                locationParent, locationChild, locationUnresolved, mission, item, unclassifiedItem, actor, player);
            var revision = new CatalogRevisionId("grid.catalog-revision.v5.sha256." + new string('5', 64));
            var composition = new CatalogCompositionId("composition.selector-fixture");
            var applicability = new CanonicalApplicabilityProjection(
                composition, records.Select(value => value.Id).ToImmutableArray(), [], "1");
            return new Fixture(
                gameId, gameVersion, revision, composition, revisionId, artifactId, records,
                [locationName, itemZulu, itemAlpha, missionName, actorName, playerName],
                [resolved, unresolved], classifications, [contribution], [organizational], applicability,
                locationParent, locationChild, locationUnresolved, mission, item, unclassifiedItem,
                actor, player, unknown, locationName, modIdentity, factionValue).Repackage();
        }

        private static GameKnowledgeAdapterDescriptor CreateAdapterDescriptor(GameId gameId)
        {
            var format = new SupportedKnowledgeFormat(
                "grid.test.selector-format", "1", ["frozen-member"], ["selector-records"],
                Enum.GetValues<KnowledgeKind>().ToImmutableArray(), true, true, true);
            return new(
                new KnowledgeAdapterId("grid.test.selector-adapter"),
                "1",
                ContentDigest.ComputeSha256("selector-adapter"u8),
                1,
                "1",
                [gameId],
                [format],
                new KnowledgeAdapterResourceLimits(1_000_000, 100, 100, 8));
        }

        private static CanonicalCatalogPackage CreateVerifiedPackage(Fixture fixture)
        {
            var descriptor = CreateAdapterDescriptor(fixture.GameId);
            var artifactDigest = ContentDigest.ComputeSha256("selector-fixture"u8);
            var artifact = new SourceArtifactRecord(fixture.ArtifactId, artifactDigest);
            var sourceNative = SourceNativeIdentifier.FromExactUtf8(
                "grid.test.source", "catalog", "selector-fixture");
            var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, sourceNative);
            var source = new CatalogSourceRecord(sourceId, KnowledgeSourceKind.LocalGameDistribution, sourceNative);
            var sourceRevision = new CatalogSourceRevisionRecord(
                fixture.SourceRevisionId, sourceId, fixture.GameVersion, [fixture.ArtifactId]);
            var modVersion = fixture.Contributions.FirstOrDefault()?.ExactModVersion ?? fixture.Records[0].ModVersion!;
            var scope = KnowledgeSourceScope.ModExtension(
                fixture.GameId, fixture.GameVersion, fixture.ModIdentity, modVersion);
            var format = descriptor.SupportedFormats.Single();
            var boundRevision = new AdapterBoundCatalogSourceRevisionRecord(
                sourceRevision,
                descriptor.RevisionId,
                scope,
                [new SourceArtifactFormatBinding(
                    fixture.ArtifactId,
                    new KnowledgeFormatCoordinate(format.FormatId, format.ExactFormatVersion))]);

            var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
            var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
            CatalogFileEvidenceReceipt Receipt(CanonicalKnowledgeRecord record, string path)
            {
                var value = new FileEvidenceReceipt(
                    fixture.SourceRevisionId,
                    fixture.ArtifactId,
                    artifactDigest,
                    "grid.test.selector-parser",
                    "1",
                    record.NativeIdentity.ExactRepresentation,
                    path,
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
                var catalog = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV1(value), value);
                if (receipts.All(existing => existing.Id != catalog.Id)) receipts.Add(catalog);
                return catalog;
            }
            void Bind(
                CanonicalKnowledgeRecord record,
                string path,
                EvidenceClaimKind kind,
                EvidenceClaimContentId? claim)
            {
                var receipt = Receipt(record, path);
                var binding = new EvidenceBinding(
                    EvidenceBindingId.DeriveV2(
                        receipt.Id, kind, record.Id, fixture.SourceRevisionId, path, claim),
                    receipt.Id, kind, record.Id, fixture.SourceRevisionId, path, claim);
                if (bindings.All(existing => existing.Id != binding.Id)) bindings.Add(binding);
            }

            CanonicalKnowledgeRecord RecordFor(KnowledgeRecordId id) =>
                fixture.Records.FirstOrDefault(value => value.Id == id) ??
                throw new InvalidDataException("Projection assertion refers outside the verified package record set.");

            foreach (var record in fixture.Records)
            {
                Bind(record, "/identity", EvidenceClaimKind.KnowledgeIdentity, null);
                Bind(record, "/version", EvidenceClaimKind.KnowledgeIdentity, null);
            }
            foreach (var term in fixture.Terms)
                Bind(RecordFor(term.KnowledgeRecordId), term.SourceFieldPath,
                    EvidenceClaimKind.Terminology, EvidenceClaimContentId.DeriveV1(term));
            foreach (var relationship in fixture.Relationships)
                Bind(RecordFor(relationship.SubjectKnowledgeRecordId), relationship.SourceFieldPath,
                    EvidenceClaimKind.Relationship, EvidenceClaimContentId.DeriveV1(relationship));
            foreach (var classification in fixture.Classifications)
                Bind(RecordFor(classification.KnowledgeRecordId), classification.SourceFieldPath,
                    EvidenceClaimKind.SemanticClassification, EvidenceClaimContentId.DeriveV1(classification));
            foreach (var contribution in fixture.Contributions)
                Bind(RecordFor(contribution.KnowledgeRecordId), contribution.SourceFieldPath,
                    EvidenceClaimKind.RecordContribution, EvidenceClaimContentId.DeriveV1(contribution));
            foreach (var organizational in fixture.OrganizationalValues)
                Bind(RecordFor(organizational.KnowledgeRecordId), organizational.SourceFieldPath,
                    EvidenceClaimKind.OrganizationalValue, EvidenceClaimContentId.DeriveV1(organizational));

            var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "grid.test.selector", "member", "selector.container!/records.bin");
            var member = new SourceAcquisitionMember(memberCoordinate, 16, artifactDigest, fixture.ArtifactId);
            var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "grid.test.selector", "container", "selector.container");
            var method = new AcquisitionMethodCoordinate(
                "grid.test.acquire", "1", "grid.test.acquire-tool", "1",
                ContentDigest.ComputeSha256("selector-acquire"u8));
            var containerDigest = ContentDigest.ComputeSha256("selector-container"u8);
            var acquisitionId = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                fixture.GameId,
                null,
                null,
                containerCoordinate,
                64,
                containerDigest,
                method,
                [member]);
            var acquisition = new SourceAcquisitionReceipt(
                acquisitionId,
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                fixture.GameId,
                null,
                null,
                containerCoordinate,
                64,
                containerDigest,
                method,
                [member]);
            var acquisitionBinding = new SourceArtifactAcquisitionBinding(
                fixture.ArtifactId, acquisition.Id, memberCoordinate, 16, artifactDigest);

            var validation = new CatalogValidationSummary(
                CatalogValidationStatus.Candidate,
                "grid.test.selector-validation",
                "1",
                ContentDigest.ComputeSha256("selector-validation"u8));
            var family = new LocationSourceFamilyDeclaration(
                new LocationSourceFamilyId("grid.test.selector-locations"),
                new KnowledgeFormatCoordinate(format.FormatId, format.ExactFormatVersion),
                descriptor.RevisionId,
                true,
                [fixture.ArtifactId],
                [fixture.SourceRevisionId],
                []);
            var manifestId = LocationCoverageManifestId.DeriveV1(scope, "1", false, validation, [family]);
            var locationManifest = new LocationCoverageManifest(
                manifestId, scope, "1", false, validation, [family]);
            var locationIds = fixture.Records.Where(value => value.Kind == KnowledgeKind.Location)
                .Select(value => value.Id).ToImmutableArray();
            var familyCoverage = new LocationSourceFamilyCoverage(
                family.SourceFamilyId,
                [fixture.ArtifactId],
                [fixture.SourceRevisionId],
                locationIds.Length,
                locationIds.Length,
                locationIds.Length,
                locationIds,
                [],
                [],
                0,
                0,
                0,
                0);
            var locationReport = LocationCoverageReport.Create(
                locationManifest,
                [familyCoverage],
                [new LocationSemanticCategoryCoverage(null, null, locationIds, locationIds.Length)],
                new LocationTerminologyCoverage(locationIds.Length,
                    locationIds.Count(id => fixture.Terms.Any(value => value.KnowledgeRecordId == id && value.Role == TerminologyAssertionRole.PrimaryName)),
                    fixture.Terms.Count(value => locationIds.Contains(value.KnowledgeRecordId) && value.Role == TerminologyAssertionRole.Alias),
                    locationIds.Count(id => !fixture.Terms.Any(value => value.KnowledgeRecordId == id && value.Role == TerminologyAssertionRole.PrimaryName)),
                    locationIds.Count(id => fixture.Terms.Where(value => value.KnowledgeRecordId == id && value.Role == TerminologyAssertionRole.PrimaryName)
                        .Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).Skip(1).Any())),
                new LocationHierarchyCoverage(2, 1, 1, 0, false),
                [new LocationRelationshipCoverage(
                    LocationRelationshipSemantics.ContainedBy, 2, 1, 1, 2, 0, 0)],
                []);

            var payload = new CanonicalCatalogPayload(
                KnowledgeCoverageState.Partial,
                [descriptor],
                [source],
                [artifact],
                [boundRevision],
                fixture.Records,
                fixture.Terms,
                fixture.Relationships,
                receipts.ToImmutable(),
                [],
                bindings.ToImmutable(),
                [],
                [],
                [acquisition],
                [acquisitionBinding])
            {
                LocationCoverageReports = [locationReport],
                SemanticClassificationAssertions = fixture.Classifications,
                RecordContributionAssertions = fixture.Contributions,
                OrganizationalValueAssertions = fixture.OrganizationalValues,
            };
            var provenance = new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.test.selector-builder",
                "1",
                new string('a', 40),
                [new CatalogCommittedBuildInput("tests/Grid.Core.Tests/selector", new string('b', 40))]);
            return CanonicalCatalogPackageKernel.CreateV5(
                CatalogPackageKind.ModCatalogExtension,
                new CatalogGameScope(fixture.GameId, fixture.GameVersion, [fixture.ArtifactId]),
                new CatalogModScope(fixture.ModIdentity, modVersion, [fixture.ArtifactId]),
                [new CatalogPackageId("grid.catalog-package.v5.sha256." + new string('c', 64))],
                "1",
                payload,
                validation,
                provenance);
        }
    }
}
