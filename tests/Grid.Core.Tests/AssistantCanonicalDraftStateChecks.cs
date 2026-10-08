using System.Collections.Immutable;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class AssistantCanonicalDraftStateChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Check failed: {message}");
            checks++;
        }

        void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                checks++;
                return;
            }

            throw new InvalidOperationException($"Check failed: {message}");
        }

        var catalog = await new MockGridCatalogService().GetCatalogAsync();
        var game = catalog.Games.Single(value => value.Id.Value == "game.skyrim-special-edition");
        var installation = game.Installations[0];
        var firstProfile = installation.Profiles[0];
        var secondProfile = installation.Profiles[1];
        var activeMod = firstProfile.Mods.First(value => value.Kind != ModEntryKind.Separator);
        var classOption = new AssistantClassOption(
            "grid.class.fixture",
            "Fixture Class",
            "grid.icon.fixture",
            true,
            SupportedGameIds: ["skyrimspecialedition"]);
        var secondClassOption = new AssistantClassOption(
            "grid.class.second",
            "Second Class",
            "grid.icon.second",
            true,
            SupportedGameIds: ["skyrimspecialedition"]);
        var integratedTool = new AssistantToolOption(
            new ToolId("grid.tool.fixture"),
            "Integrated Fixture",
            AvailabilityState.Available,
            null,
            [new AssistantToolGameCompatibility(game.Id, ["grid.fixture.collect"], "Fixture compatibility")]);

        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var installationTool = new UserToolLaunchConfiguration(
            UserToolLaunchConfiguration.CurrentSchemaVersion,
            new UserToolConfigurationId("user-tool.installation"),
            "Installation Tool",
            @"C:\Tools\InstallationTool.exe",
            @"C:\Tools",
            [],
            true,
            new UserToolScope(game.Id, installation.Id, null),
            now,
            now,
            1);
        var profileTool = installationTool with
        {
            Id = new UserToolConfigurationId("user-tool.profile"),
            Title = "Profile Tool",
            Scope = new UserToolScope(game.Id, installation.Id, firstProfile.Id),
        };
        var otherInstallationTool = installationTool with
        {
            Id = new UserToolConfigurationId("user-tool.other-installation"),
            Title = "Other Installation Tool",
            Scope = new UserToolScope(game.Id, game.Installations[1].Id, null),
        };
        var toolManager = new UserToolManagerState(
            new FixedUserToolConfigurationStore([installationTool, profileTool, otherInstallationTool]),
            new FixedInstalledToolKnowledgeStore());
        await toolManager.LoadAsync();

        var workstationContext = new ApplicationContextSnapshot(
            ApplicationSurface.GameWorkspace,
            game.Id,
            installation.Id,
            firstProfile.Id,
            [activeMod.Id],
            null, null, null, null, null, null, null, null);
        var state = new AssistantSessionState(
            workstationContext,
            catalog,
            [classOption, secondClassOption],
            [integratedTool],
            userTools: toolManager);

        var initial = state.Snapshot();
        Assert(initial.TicketDraft.GameId == game.Id &&
               initial.TicketDraft.InstallationId == installation.Id &&
               initial.TicketDraft.ProfileId == firstProfile.Id &&
               initial.TicketDraft.GameProvenance == TicketSelectionProvenance.ContextInherited &&
               initial.TicketDraft.Mods.Single().ModId == activeMod.Id,
            "An active workstation supplies exact inherited canonical context.");
        Assert(initial.TicketDraft.Class is null && initial.TicketDraft.Problem is null && initial.TicketDraft.Goal is null,
            "Context inheritance does not invent required Ticket taxonomy selections.");
        Assert(initial.Problems.IsEmpty && initial.Timings.IsEmpty && initial.Goals.IsEmpty,
            "Production-default Ticket taxonomies remain truthfully empty.");
        Assert(initial.ConfiguredTools.Select(value => value.Id).ToHashSet().SetEquals([installationTool.Id, profileTool.Id]) &&
               initial.ConfiguredTools.All(value =>
                   value.IdentityStatus == ConfiguredToolIdentityStatus.Unresolved &&
                   value.CompatibilityStatus == ConfiguredToolCompatibilityStatus.Unresolved &&
                   value.IntegrationStatus == ConfiguredToolIntegrationStatus.Unavailable &&
                   value.InvestigationCapabilityStatus == ConfiguredToolInvestigationCapabilityStatus.None),
            "The exact active workspace exposes installation- and profile-scoped configured-tool context only.");

        state.SetComposerText("Composer-only text");
        state.SetProblem("Legacy problem prose");
        state.SetExpectedBehavior("Legacy expected prose");
        state.SetReproductionOrLocation("Legacy reproduction prose");
        state.SetDesiredOutcome("Legacy desired prose");
        var prose = state.Snapshot();
        Assert(prose.ComposerText == "Composer-only text" && prose.Draft.Problem == "Composer-only text" &&
               prose.TicketDraft.Problem is null && prose.TicketDraft.Goal is null &&
               prose.TicketDraft.ReferenceContext.IsEmpty && prose.TicketDraft.UserContext.IsEmpty,
            "The visible composer is the exact request claim while canonical Problem, Goal, and reference context remain separate.");
        var legacyClaimState = new AssistantSessionState(workstationContext, catalog, [classOption, secondClassOption]);
        legacyClaimState.SetProblem("Historical caller claim");
        Assert(legacyClaimState.Snapshot().Draft.Problem == "Historical caller claim",
            "A historical caller that has never supplied composer state retains the legacy claim fallback.");

        state.SelectClass(classOption.Id);
        Assert(!state.Snapshot().TicketReadiness.CanSubmit,
            "Composer prose cannot satisfy missing canonical Problem or Goal readiness.");
        var classId = new TicketClassId(classOption.Id);
        state.SelectProblem(new TicketProblemSelection(
            new TicketProblemId("problem.fixture"), classId, "Fixture Problem", TicketSelectionProvenance.ExplicitUserSelection));
        state.SelectTiming(new TicketTimingSelection(
            new TicketTimingId("timing.fixture"), classId, "Fixture Timing", TicketSelectionProvenance.ExplicitUserSelection));
        state.SelectGoal(new TicketGoalSelection(
            new TicketGoalId("goal.fixture"), "Fixture Goal", TicketSelectionProvenance.ExplicitUserSelection));
        Assert(state.Snapshot().TicketReadiness.CanSubmit,
            "Only canonical Class, Problem, and Goal make the Ticket ready.");

        var gameOptional = new AssistantSessionState(
            ApplicationContextSnapshot.Home,
            catalog,
            [classOption, secondClassOption]);
        gameOptional.SelectClass(classOption.Id);
        gameOptional.SelectProblem(new TicketProblemSelection(
            new TicketProblemId("problem.global"), classId, "Global Problem", TicketSelectionProvenance.ExplicitUserSelection));
        gameOptional.SelectGoal(new TicketGoalSelection(
            new TicketGoalId("goal.global"), "Global Goal", TicketSelectionProvenance.ExplicitUserSelection));
        Assert(gameOptional.Snapshot().TicketDraft.GameId is null && gameOptional.Snapshot().TicketReadiness.CanSubmit,
            "Game, Installation, and Profile are not hidden Ticket readiness requirements.");

        var soleBaseProfile = firstProfile with
        {
            Mods = [],
            Plugins = [],
            EnvironmentEntries = [],
            ObservedOutputs = [],
            Observation = null,
        };
        var soleBaseInstallation = installation with
        {
            Kind = InstallationKind.External,
            Profiles = [soleBaseProfile],
            Metadata = installation.Metadata with
            {
                LocationDisplay = @"C:\Fixture\SoleBaseGame",
                Availability = InstallationAvailability.Available,
                Provenance = InstallationProvenanceKind.ConnectedReference,
                ReferenceId = new InstallationReferenceId("reference.fixture.sole-base-game"),
            },
        };
        var soleBaseGame = game with { Installations = [soleBaseInstallation] };
        var soleBaseCatalog = new GridCatalogSnapshot(
            "catalog.sole-base-profile",
            CatalogSourceKind.Adapter,
            catalog.Games.Select(value => value.Id == game.Id ? soleBaseGame : value));
        var homeWithSoleBaseProfile = new AssistantSessionState(
            ApplicationContextSnapshot.Home,
            soleBaseCatalog,
            [classOption, secondClassOption]);
        homeWithSoleBaseProfile.SelectGame(game.Id);
        var resolvedBaseProfile = homeWithSoleBaseProfile.Snapshot().TicketDraft;
        Assert(resolvedBaseProfile.GameId == game.Id &&
               resolvedBaseProfile.InstallationId == soleBaseInstallation.Id &&
               resolvedBaseProfile.ProfileId == soleBaseProfile.Id &&
               resolvedBaseProfile.InstallationProvenance == TicketSelectionProvenance.DeterministicallyResolved &&
               resolvedBaseProfile.ProfileProvenance == TicketSelectionProvenance.DeterministicallyResolved,
            "Selecting a game outside a workspace deterministically resolves its sole connected empty GRID base profile.");

        var ambiguousHome = new AssistantSessionState(
            ApplicationContextSnapshot.Home,
            catalog,
            [classOption, secondClassOption]);
        ambiguousHome.SelectGame(game.Id);
        Assert(ambiguousHome.Snapshot().TicketDraft.InstallationId is null &&
               ambiguousHome.Snapshot().TicketDraft.ProfileId is null,
            "Selecting a game with multiple connected profiles remains unselected instead of guessing applicability.");

        var moddedSoleCatalog = new GridCatalogSnapshot(
            "catalog.sole-modded-profile",
            CatalogSourceKind.Adapter,
            catalog.Games.Select(value => value.Id == game.Id
                ? game with { Installations = [soleBaseInstallation with { Profiles = [firstProfile] }] }
                : value));
        var moddedHome = new AssistantSessionState(
            ApplicationContextSnapshot.Home,
            moddedSoleCatalog,
            [classOption, secondClassOption]);
        moddedHome.SelectGame(game.Id);
        Assert(moddedHome.Snapshot().TicketDraft.InstallationId is null &&
               moddedHome.Snapshot().TicketDraft.ProfileId is null,
            "A sole modded profile is not silently treated as base-game applicability.");

        state.SelectClass(secondClassOption.Id);
        var afterClassChange = state.Snapshot().TicketDraft;
        Assert(afterClassChange.Problem is null && afterClassChange.Timing is null && afterClassChange.Goal?.Id.Value == "goal.fixture",
            "Changing Class clears only its Problem and Timing children while preserving independent Goal context.");

        state.SelectClass(classOption.Id);
        state.SetToolSelected(integratedTool.Id, true);
        state.SetConfiguredToolSelected(installationTool.Id, true);
        state.SetConfiguredToolSelected(profileTool.Id, true);
        Assert(state.Snapshot().TicketDraft is
            {
                IntegratedTools.Length: 1,
                ConfiguredToolContext.Length: 2,
            } && state.Snapshot().TicketDraft.ConfiguredToolContext.All(value =>
                value.IdentityStatus == ConfiguredToolIdentityStatus.Unresolved &&
                value.CompatibilityStatus == ConfiguredToolCompatibilityStatus.Unresolved &&
                value.IntegrationStatus == ConfiguredToolIntegrationStatus.Unavailable &&
                value.InvestigationCapabilityStatus == ConfiguredToolInvestigationCapabilityStatus.None),
            "Configured launch tools remain inert context separate from integrated ToolIDs and capabilities.");
        AssertThrows<ArgumentException>(
            () => state.SetConfiguredToolSelected(otherInstallationTool.Id, true),
            "Configured-tool selection cannot cross the exact Installation boundary.");

        var reference = new TicketReferenceContext(
            new TicketReferenceContextId("reference.fixture.location"),
            TicketReferenceContextKind.Location,
            "Fixture Location",
            [],
            game.Id,
            activeMod.Id,
            new TicketReferenceProvenance("fixture", "fixture.location", "Canonical fixture location", "Direct", true),
            TicketSelectionProvenance.ReferenceProvider);
        state.SetReferenceContextSelected(reference, true);
        state.SetOtherContextSelected(TicketReferenceContextKind.Entity, "Unresolved entity", true);
        state.SelectProfile(secondProfile.Id);
        var afterProfileChange = state.Snapshot().TicketDraft;
        Assert(afterProfileChange.Mods.IsEmpty && afterProfileChange.ReferenceContext.IsEmpty &&
               afterProfileChange.ConfiguredToolContext.Select(value => value.ConfigurationId).SequenceEqual([installationTool.Id]) &&
               afterProfileChange.UserContext.Single() is
               {
                   Kind: TicketReferenceContextKind.Entity,
                   Resolution: TicketUserContextResolution.Unresolved,
                   MatchedReferenceId: null,
               } && afterProfileChange.IntegratedTools.Single().ToolId == integratedTool.Id,
            "Profile changes prune only incompatible Mods, scoped references, and profile tools while preserving independent tools and unresolved user context.");

        state.ReplaceOtherContext(TicketReferenceContextKind.Entity, "Replacement entity");
        Assert(state.Snapshot().TicketDraft.UserContext.Count(value =>
                   value.Kind == TicketReferenceContextKind.Entity && value.Resolution == TicketUserContextResolution.Unresolved) == 1 &&
               state.Snapshot().TicketDraft.UserContext.Single(value => value.Kind == TicketReferenceContextKind.Entity).Value == "Replacement entity",
            "Each reference selector owns one replaceable unresolved Other value.");
        state.ReplaceOtherContext(TicketReferenceContextKind.Entity, "  exact Other punctuation?!  ");
        Assert(state.Snapshot().TicketDraft.UserContext.Single(value => value.Kind == TicketReferenceContextKind.Entity).Value ==
               "  exact Other punctuation?!  ",
            "Other remains exact unresolved ticket context and is not trimmed or normalized.");
        var skyrimProjectionPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(game.Id);
        var actorSelection = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Actor,
            new CatalogRevisionId("grid.catalog-revision.v5.sha256." + new string('5', 64)),
            new CatalogCompositionId("composition.assistant.fixture"),
            skyrimProjectionPolicy.Id,
            skyrimProjectionPolicy.ExactVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('6', 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('7', 64)),
            null);
        state.SetCanonicalSelectorSelection(actorSelection);
        Assert(state.Snapshot().TicketDraft.CanonicalSelections.Single() == actorSelection &&
               state.Snapshot().TicketDraft.UserContext.All(value => value.Kind != TicketReferenceContextKind.Entity),
            "Selecting a canonical record clears unresolved Other for the same selector kind.");

        var locationA = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            actorSelection.CatalogRevisionId,
            actorSelection.CatalogCompositionId,
            actorSelection.ProjectionPolicyId,
            actorSelection.ProjectionPolicyVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('b', 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('a', 64)),
            null);
        var locationB = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            actorSelection.CatalogRevisionId,
            actorSelection.CatalogCompositionId,
            actorSelection.ProjectionPolicyId,
            actorSelection.ProjectionPolicyVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('d', 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('c', 64)),
            null);
        state.ToggleCanonicalLocationSelection(locationA);
        state.ToggleCanonicalLocationSelection(locationB);
        Assert(state.Snapshot().TicketDraft.CanonicalSelections.Count(value => value.KnowledgeKind == KnowledgeKind.Location) == 2,
            "Location supports multiple canonical selections in selection order.");
        state.ToggleCanonicalLocationSelection(locationA);
        Assert(state.Snapshot().TicketDraft.CanonicalSelections.Single(value => value.KnowledgeKind == KnowledgeKind.Location) == locationB,
            "Location selections toggle off without disturbing other Location selections.");
        state.AppendLocationOtherContext("Strawberry");
        state.AppendLocationOtherContext("Davis");
        Assert(state.Snapshot().TicketDraft.UserContext.Count(value =>
                   value.Kind == TicketReferenceContextKind.Location &&
                   value.Resolution == TicketUserContextResolution.Unresolved) == 2 &&
               state.Snapshot().TicketDraft.CanonicalSelections.All(value => value.KnowledgeKind != KnowledgeKind.Location),
            "Location Other entries append to preview context and clear canonical Location selections.");
        AssertThrows<ArgumentException>(
            () => state.SetUserContextSelected(new(
                TicketReferenceContextKind.Entity,
                "conflicting unresolved actor",
                TicketUserContextResolution.Unresolved,
                TicketSelectionProvenance.ExplicitUserSelection), true),
            "Canonical selection and unresolved Other cannot coexist for one semantic kind.");
        state.ReplaceOtherContext(TicketReferenceContextKind.Entity, "unresolved replacement");
        Assert(state.Snapshot().TicketDraft.CanonicalSelections.All(value => value.KnowledgeKind != KnowledgeKind.Actor) &&
               state.Snapshot().TicketDraft.UserContext.Single(value => value.Kind == TicketReferenceContextKind.Entity).Value ==
               "unresolved replacement",
            "Entering Other clears the canonical selection for the same selector kind without promoting user text.");

        var mission = new TicketReferenceContext(
            new TicketReferenceContextId("reference.fixture.mission.cashing-out"),
            TicketReferenceContextKind.MissionOrQuest,
            "Cashing Out",
            [],
            game.Id,
            null,
            new TicketReferenceProvenance("fixture", "mission.cashing-out", "Canonical fixture mission", "Direct", true),
            TicketSelectionProvenance.ReferenceProvider);
        var taxonomy = new AssistantTicketTaxonomy(
            [new(new TicketProblemId("problem.fixture.crash"), classId, "Crash")],
            [new(new TicketTimingId("timing.fixture.repeatable"), classId, "Repeatable")],
            [new(new TicketGoalId("goal.fixture.identify"), "Identify cause")],
            [mission]);
        var previewState = new AssistantSessionState(
            workstationContext,
            catalog,
            [classOption, secondClassOption],
            [integratedTool],
            userTools: toolManager,
            taxonomy: taxonomy);
        previewState.SelectClass(classOption.Id);
        previewState.SelectProblem(new(
            taxonomy.Problems[0].Id, taxonomy.Problems[0].ClassId, taxonomy.Problems[0].DisplayName,
            TicketSelectionProvenance.ExplicitUserSelection));
        previewState.SelectGoal(new(
            taxonomy.Goals[0].Id, taxonomy.Goals[0].DisplayName,
            TicketSelectionProvenance.ExplicitUserSelection));
        previewState.SetReferenceContextSelected(mission, true);
        previewState.ReplaceOtherContext(TicketReferenceContextKind.Location, "Unresolved casino wording");
        var preview = previewState.Snapshot().TicketPreview!;
        Assert(preview.Name == "Cashing Out Mission Crash" &&
               preview.Provenance == AssistantTicketNameProvenance.DeterministicStructuredSelections &&
               preview.Contributions.Select(value => value.CanonicalId).SequenceEqual([
                   mission.Id.Value,
                   taxonomy.Problems[0].Id.Value,
               ]) &&
               !preview.Name.Contains("casino", StringComparison.OrdinalIgnoreCase),
            "Ticket naming deterministically composes salient canonical IDs and excludes unresolved Other prose.");

        var explicitContext = new AssistantSessionState(
            workstationContext,
            catalog,
            [classOption, secondClassOption]);
        explicitContext.SelectGame(game.Id);
        explicitContext.SetContext(ApplicationContextSnapshot.Home);
        Assert(explicitContext.Snapshot().TicketDraft.GameId == game.Id &&
               explicitContext.Snapshot().TicketDraft.GameProvenance == TicketSelectionProvenance.ExplicitUserSelection,
            "Leaving a workstation does not overwrite an explicit Game selection.");

        var inheritedContext = new AssistantSessionState(
            workstationContext,
            catalog,
            [classOption, secondClassOption]);
        inheritedContext.SetContext(ApplicationContextSnapshot.Home);
        Assert(inheritedContext.Snapshot().TicketDraft.GameId is null &&
               inheritedContext.Snapshot().TicketDraft.InstallationId is null &&
               inheritedContext.Snapshot().TicketDraft.ProfileId is null,
            "Inherited workstation context is removed when that active workstation no longer exists.");

        var previousInvestigationId = state.Snapshot().TicketDraft.InvestigationId;
        var previousTicketId = state.Snapshot().TicketDraft.TicketId;
        state.StartNewChat();
        var restarted = state.Snapshot();
        Assert(restarted.TicketDraft.InvestigationId != previousInvestigationId &&
               restarted.TicketDraft.TicketId != previousTicketId &&
               restarted.TicketDraft.Revision >= 1 &&
               restarted.TicketDraft.GameId == game.Id &&
               restarted.TicketDraft.ProfileId == firstProfile.Id &&
               restarted.TicketDraft.Class is null && restarted.TicketDraft.Problem is null && restarted.TicketDraft.Goal is null &&
               restarted.TicketDraft.ConfiguredToolContext.IsEmpty && restarted.TicketDraft.ReferenceContext.IsEmpty &&
               restarted.TicketDraft.UserContext.IsEmpty && restarted.ComposerText.Length == 0 &&
               restarted.Draft.Problem.Length == 0 && restarted.Draft.DesiredOutcome.Length == 0,
            "StartNewChat creates new identities, inherits only current workstation context, and leaks no prior Ticket or compatibility content.");

        Console.WriteLine($"PASS  Assistant canonical Ticket Draft state ({checks} checks).");
        return checks;
    }

    private sealed class FixedUserToolConfigurationStore(ImmutableArray<UserToolLaunchConfiguration> configurations)
        : IUserToolConfigurationStore
    {
        public Task<UserToolConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserToolConfigurationSnapshot(1, configurations, []));

        public Task<UserToolSaveResult> ReplaceAsync(
            int expectedRevision,
            ImmutableArray<UserToolLaunchConfiguration> values,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The canonical draft-state fixture is read-only.");
    }

    private sealed class FixedInstalledToolKnowledgeStore : IInstalledToolKnowledgeStore
    {
        public Task<InstalledToolKnowledgeSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(InstalledToolKnowledgeSnapshot.Empty);

        public Task<UserToolSaveResult> ReplaceAsync(
            int expectedRevision,
            ImmutableArray<InstalledToolKnowledge> resolutions,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The canonical draft-state fixture is read-only.");
    }
}
