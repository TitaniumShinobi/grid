using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class InvestigationTicketDraftChecks
{
    public static int Run()
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

        var classId = new TicketClassId("class.crash-freeze");
        var problemId = new TicketProblemId("problem.unexpected-termination");
        var classSelection = new TicketClassSelection(
            classId,
            "Crash & Freeze",
            "class.crash-freeze",
            TicketSelectionProvenance.ExplicitUserSelection);
        var problemSelection = new TicketProblemSelection(
            problemId,
            classId,
            "Unexpected termination",
            TicketSelectionProvenance.ExplicitUserSelection);
        var goalSelection = new TicketGoalSelection(
            new TicketGoalId("goal.restore-expected-operation"),
            "Restore expected operation",
            TicketSelectionProvenance.ExplicitUserSelection);

        InvestigationTicketDraft CreateDraft(
            bool includeClass = true,
            bool includeProblem = true,
            bool includeGoal = true) => new(
                InvestigationTicketDraft.CurrentSchemaVersion,
                new InvestigationId("investigation.fixture"),
                new TicketId("ticket.fixture"),
                1,
                includeClass ? classSelection : null,
                includeProblem ? problemSelection : null,
                includeGoal ? goalSelection : null);

        Assert(InvestigationTicketDraft.CurrentSchemaVersion == 2,
            "Ticket Draft schema version records canonical selector identity without reinterpreting version one.");
        AssertThrows<ArgumentException>(() => _ = new InvestigationId(" "),
            "Investigation identities reject blank values.");
        AssertThrows<ArgumentException>(() => _ = new TicketId(new string('x', 513)),
            "Ticket identities reject overlong values.");
        Assert(new InvestigationId("  investigation.trimmed  ").Value == "investigation.trimmed",
            "Ticket identities normalize surrounding whitespace deterministically.");

        for (var mask = 0; mask < 8; mask++)
        {
            var hasClass = (mask & 1) != 0;
            var hasProblem = (mask & 2) != 0;
            var hasGoal = (mask & 4) != 0;
            var readiness = InvestigationTicketDraftPolicy.Evaluate(CreateDraft(hasClass, hasProblem, hasGoal));
            Assert(readiness.CanSubmit == (hasClass && hasProblem && hasGoal),
                $"CanSubmit follows only the Class/Problem/Goal contract for presence mask {mask}.");
        }

        var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId;
        var installationId = new InstallationId("installation.gta-enhanced.fixture");
        var profileId = new ProfileId("profile.gta-enhanced.fixture");
        var configuredId = new UserToolConfigurationId("tool-config.cherax.fixture");
        var fullDraft = CreateDraft() with
        {
            Revision = 2,
            Timing = new TicketTimingSelection(
                new TicketTimingId("timing.during-gameplay"),
                classId,
                "During gameplay",
                TicketSelectionProvenance.ExplicitUserSelection),
            GameId = gameId,
            InstallationId = installationId,
            ProfileId = profileId,
            GameProvenance = TicketSelectionProvenance.ContextInherited,
            InstallationProvenance = TicketSelectionProvenance.DeterministicallyResolved,
            ProfileProvenance = TicketSelectionProvenance.ContextInherited,
            VvaultInstanceId = new VvaultGridInstanceId("opaque-grid-instance-fixture"),
            Mods = [new(new ModId("mod.fixture"), "Fixture mod", TicketSelectionProvenance.ExplicitUserSelection)],
            IntegratedTools = [new(new ToolId("grid.tool.fixture"), "Integrated fixture", TicketSelectionProvenance.ExplicitUserSelection)],
            ConfiguredToolContext =
            [
                new(
                    configuredId,
                    "Configured fixture",
                    new UserToolScope(gameId, installationId, null),
                    "opaque-executable-reference:does-not-exist",
                    "configuration-fingerprint-fixture",
                    true,
                    ConfiguredToolIdentityStatus.Unresolved,
                    ConfiguredToolCompatibilityStatus.Unresolved,
                    ConfiguredToolIntegrationStatus.Unavailable,
                    ConfiguredToolInvestigationCapabilityStatus.None,
                    TicketSelectionProvenance.ExplicitUserSelection),
            ],
            ReferenceContext =
            [
                new(
                    new TicketReferenceContextId("context.location.fixture"),
                    TicketReferenceContextKind.Location,
                    "Fixture location",
                    ["Fixture alias"],
                    gameId,
                    null,
                    new TicketReferenceProvenance(
                        "Fixture",
                        "reference.fixture",
                        "The reference identifies a canonical location.",
                        "SufficientForIdentity",
                        true),
                    TicketSelectionProvenance.ReferenceProvider),
            ],
            UserContext =
            [
                new(
                    TicketReferenceContextKind.Entity,
                    "Unmatched user entity",
                    TicketUserContextResolution.Unresolved,
                    TicketSelectionProvenance.ExplicitUserSelection),
            ],
            Attachments =
            [
                new(
                    new TicketAttachmentId("attachment.fixture"),
                    "fixture.png",
                    "opaque-resource-reference:does-not-exist",
                    "image/png",
                    123,
                    TicketSelectionProvenance.ExplicitUserSelection),
            ],
        };

        var fullReadiness = InvestigationTicketDraftPolicy.Evaluate(fullDraft);
        Assert(fullReadiness.Status == InvestigationTicketReadinessStatus.Ready && fullReadiness.CanSubmit,
            "Optional Game/Profile, Timing, Mods, Tools, context, attachments, and VVAULT identity do not add readiness requirements.");
        Assert(fullReadiness.Issues.IsEmpty,
            "A coherent fully contextualized Ticket Draft has no validation issues.");

        var gameOnly = CreateDraft() with
        {
            GameId = gameId,
            GameProvenance = TicketSelectionProvenance.ExplicitUserSelection,
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(gameOnly).CanSubmit,
            "Game-only optional context remains a valid, submittable Ticket Draft.");

        var installationWithoutGame = CreateDraft() with
        {
            InstallationId = installationId,
            InstallationProvenance = TicketSelectionProvenance.DeterministicallyResolved,
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(installationWithoutGame).Status == InvestigationTicketReadinessStatus.Invalid,
            "Installation without Game fails closed.");
        AssertThrows<ArgumentException>(() => InvestigationTicketDraftPolicy.Validate(installationWithoutGame),
            "Strict validation rejects Installation without Game.");

        var profileWithoutInstallation = CreateDraft() with
        {
            GameId = gameId,
            ProfileId = profileId,
            GameProvenance = TicketSelectionProvenance.ExplicitUserSelection,
            ProfileProvenance = TicketSelectionProvenance.ExplicitUserSelection,
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(profileWithoutInstallation).Status == InvestigationTicketReadinessStatus.Invalid,
            "Profile without exact Game and Installation fails closed.");

        Assert(!typeof(TicketConfiguredToolContext).GetProperties().Any(property =>
                property.PropertyType == typeof(ToolId) ||
                property.Name.Contains("CapabilityId", StringComparison.Ordinal)),
            "Configured-tool context cannot contain ToolID or capability identity by construction.");
        Assert(fullDraft.ConfiguredToolContext.Single().Scope == new UserToolScope(gameId, installationId, null),
            "Configured-tool context preserves its exact installation scope.");
        Assert(fullDraft.ConfiguredToolContext.Single().IdentityStatus == ConfiguredToolIdentityStatus.Unresolved &&
               fullDraft.ConfiguredToolContext.Single().IntegrationStatus == ConfiguredToolIntegrationStatus.Unavailable &&
               fullDraft.ConfiguredToolContext.Single().InvestigationCapabilityStatus == ConfiguredToolInvestigationCapabilityStatus.None,
            "Configured-tool identity, integration, and investigation capability remain independent unresolved states.");

        var crossInstallationTool = fullDraft with
        {
            ConfiguredToolContext =
            [
                fullDraft.ConfiguredToolContext.Single() with
                {
                    Scope = new UserToolScope(gameId, new InstallationId("installation.other"), null),
                },
            ],
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(crossInstallationTool).Status == InvestigationTicketReadinessStatus.Invalid,
            "Configured tools cannot cross canonical Installation boundaries.");

        var otherProfileTool = fullDraft with
        {
            ConfiguredToolContext =
            [
                fullDraft.ConfiguredToolContext.Single() with
                {
                    Scope = new UserToolScope(gameId, installationId, new ProfileId("profile.other")),
                },
            ],
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(otherProfileTool).Status == InvestigationTicketReadinessStatus.Invalid,
            "Profile-scoped configured tools cannot cross canonical Profile boundaries.");

        Assert(fullDraft.ReferenceContext.Single().SelectionProvenance == TicketSelectionProvenance.ReferenceProvider &&
               fullDraft.ReferenceContext.Single().ReferenceProvenance.IsSufficient,
            "Reference-backed context preserves provider provenance and identity sufficiency.");
        Assert(fullDraft.UserContext.Single().Resolution == TicketUserContextResolution.Unresolved &&
               fullDraft.UserContext.Single().MatchedReferenceId is null,
            "Other text remains unresolved user context rather than reference knowledge or evidence.");

        var duplicateTools = fullDraft with
        {
            IntegratedTools = [fullDraft.IntegratedTools.Single(), fullDraft.IntegratedTools.Single()],
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(duplicateTools).Status == InvestigationTicketReadinessStatus.Invalid,
            "Duplicate canonical selections fail closed.");

        var wrongProblemClass = fullDraft with
        {
            Problem = fullDraft.Problem! with { ClassId = new TicketClassId("class.other") },
        };
        Assert(InvestigationTicketDraftPolicy.Evaluate(wrongProblemClass).Status == InvestigationTicketReadinessStatus.Invalid,
            "Problem selections cannot cross their canonical Class boundary.");

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(fullDraft, options);
        var restored = JsonSerializer.Deserialize<InvestigationTicketDraft>(json, options);
        Assert(restored is not null &&
               restored.SchemaVersion == InvestigationTicketDraft.CurrentSchemaVersion &&
               restored.InvestigationId == fullDraft.InvestigationId &&
               restored.TicketId == fullDraft.TicketId &&
               restored.Revision == fullDraft.Revision,
            "Versioned Ticket Draft identity round-trips through JSON.");
        Assert(restored!.Class?.Provenance == TicketSelectionProvenance.ExplicitUserSelection &&
               restored.GameProvenance == TicketSelectionProvenance.ContextInherited &&
               restored.ConfiguredToolContext.Single().Provenance == TicketSelectionProvenance.ExplicitUserSelection,
            "Selection provenance survives serialization.");
        Assert(restored.ReferenceContext.Single().ReferenceProvenance.SourceIdentifier == "reference.fixture" &&
               restored.UserContext.Single().Resolution == TicketUserContextResolution.Unresolved,
            "Reference knowledge and unresolved user context remain distinct after serialization.");

        var gtaProjectionPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(gameId);
        var canonicalSelection = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            new CatalogRevisionId("grid.catalog-revision.v5.sha256." + new string('5', 64)),
            new CatalogCompositionId("composition.ticket.fixture"),
            gtaProjectionPolicy.Id,
            gtaProjectionPolicy.ExactVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('6', 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('7', 64)),
            null);
        var canonicalDraft = fullDraft with { CanonicalSelections = [canonicalSelection] };
        InvestigationTicketDraftPolicy.Validate(canonicalDraft);
        AssertThrows<ArgumentException>(() => InvestigationTicketDraftPolicy.Validate(CreateDraft() with
        {
            CanonicalSelections = [canonicalSelection],
        }), "Canonical selector selections without a canonical Game fail closed.");
        Assert(InvestigationTicketDraftPolicy.Evaluate(CreateDraft()).CanSubmit,
            "Tickets without canonical selections remain valid without a Game.");
        var staleGtaSelection = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            canonicalSelection.CatalogRevisionId,
            canonicalSelection.CatalogCompositionId,
            CanonicalSelectorProjectionPolicy.V4.Id,
            CanonicalSelectorProjectionPolicy.V4.ExactVersion,
            canonicalSelection.SelectedPathId,
            canonicalSelection.KnowledgeRecordId,
            null);
        AssertThrows<ArgumentException>(() => InvestigationTicketDraftPolicy.Validate(canonicalDraft with
        {
            CanonicalSelections = [staleGtaSelection],
        }), "GTA ticket drafts reject stale cross-game projection coordinates.");
        var skyrimGameId = new GameId("game.skyrim");
        var skyrimPolicy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(skyrimGameId);
        var gtaOnlySelection = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            canonicalSelection.CatalogRevisionId,
            canonicalSelection.CatalogCompositionId,
            gtaProjectionPolicy.Id,
            gtaProjectionPolicy.ExactVersion,
            canonicalSelection.SelectedPathId,
            canonicalSelection.KnowledgeRecordId,
            null);
        AssertThrows<ArgumentException>(() => InvestigationTicketDraftPolicy.Validate(fullDraft with
        {
            GameId = skyrimGameId,
            CanonicalSelections = [gtaOnlySelection],
        }), "Non-GTA ticket drafts reject GTA-only projection coordinates.");
        Assert(CanonicalSelectorProjectionPolicyResolver.MatchesGamePolicy(
                   skyrimGameId, skyrimPolicy.Id, skyrimPolicy.ExactVersion),
            "Skyrim resolves to cross-game v4 projection policy.");
        var canonicalJson = JsonSerializer.Serialize(canonicalDraft, options);
        var canonicalRestored = JsonSerializer.Deserialize<InvestigationTicketDraft>(canonicalJson, options);
        Assert(canonicalRestored?.CanonicalSelections.Single() == canonicalSelection,
            "Ticket persistence retains the selected canonical record, navigation path, catalog revision, composition, and policy coordinates exactly.");

        var revised = fullDraft with { Revision = 3, Goal = goalSelection with { DisplayName = "Restore operation safely" } };
        Assert(revised.Class?.Provenance == fullDraft.Class?.Provenance &&
               revised.GameProvenance == fullDraft.GameProvenance &&
               revised.ConfiguredToolContext.Single().Provenance == fullDraft.ConfiguredToolContext.Single().Provenance,
            "Immutable Ticket Draft revisions preserve selection provenance.");

        Assert(InvestigationTicketDraftPolicy.Evaluate(fullDraft with
            {
                Attachments =
                [
                    fullDraft.Attachments.Single() with
                    {
                        ResourceReference = "opaque-resource-reference:still-does-not-exist",
                    },
                ],
            }).CanSubmit,
            "Readiness evaluates opaque resource references without probing external files.");

        Console.WriteLine($"PASS  Canonical Ticket Draft foundation ({checks} checks).");
        return checks;
    }
}
