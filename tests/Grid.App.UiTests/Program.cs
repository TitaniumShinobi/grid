using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Grid.App.Composition;
using Grid.App.Controls;
using Grid.App.Services;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.UiTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var configuration = args.Contains("--debug", StringComparer.Ordinal) ||
                            AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Any(segment => segment.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            ? "Debug"
            : "Release";
        try
        {
            if (args.Length > 0 && args[0].Equals("--dif-layout-cleanup-contracts", StringComparison.Ordinal))
            {
                if (args.Length != 1) throw new ArgumentException("Usage: --dif-layout-cleanup-contracts");
                VerifyDifLayoutCleanupContracts();
                return 0;
            }
            if (args.Length > 0 && args[0].Equals("--attach-dif-layout-cleanup", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var cleanupProcessId))
                    throw new ArgumentException("Usage: --attach-dif-layout-cleanup <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                using var live = UiAutomationDriver.Attach(cleanupProcessId);
                return live.VerifyDifLayoutCleanupAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--selector-presentation-contracts", StringComparison.Ordinal))
            {
                if (args.Length != 1) throw new ArgumentException("Usage: --selector-presentation-contracts");
                VerifySelectorPresentationContracts();
                return 0;
            }
            if (args.Length > 0 && args[0].Equals("--attach-selector-presentation", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var presentationProcessId))
                    throw new ArgumentException("Usage: --attach-selector-presentation <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                using var live = UiAutomationDriver.Attach(presentationProcessId);
                return live.VerifySelectorPresentationAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && (args[0].Equals("--location-scaffold-contracts", StringComparison.Ordinal) ||
                                   args[0].Equals("--dif-scaffold-contracts", StringComparison.Ordinal)))
            {
                if (args.Length != 1) throw new ArgumentException("Usage: --dif-scaffold-contracts");
                await VerifyLocationScaffoldContractsAsync();
                return 0;
            }
            if (args.Length > 0 && (args[0].Equals("--attach-dif-scaffolds", StringComparison.Ordinal) ||
                args[0].Equals("--attach-dif-scaffolds-remaining", StringComparison.Ordinal)))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var scaffoldProcessId))
                    throw new ArgumentException("Usage: --attach-dif-scaffolds <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                using var live = UiAutomationDriver.Attach(scaffoldProcessId);
                return live.VerifyDifScaffoldsAttached(reportPath,
                    remainingItemActorOnly: args[0].Equals("--attach-dif-scaffolds-remaining", StringComparison.Ordinal)) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--attach-location-scaffold", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var scaffoldProcessId))
                    throw new ArgumentException("Usage: --attach-location-scaffold <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                using var live = UiAutomationDriver.Attach(scaffoldProcessId);
                return live.VerifyLocationScaffoldAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--canonical-binding-only", StringComparison.Ordinal))
            {
                if (args.Length != 1)
                    throw new ArgumentException("Usage: --canonical-binding-only");
                CanonicalCatalogRuntimeBindingStoreChecks.Run();
                Console.WriteLine("Canonical runtime binding checks passed without launching GRID.");
                return 0;
            }
            if (args.Length > 0 && args[0].Equals("--measure-location-2a", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var measuredProcessId))
                    throw new ArgumentException("Usage: --measure-location-2a <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                Environment.SetEnvironmentVariable("GRID_UI_CAPTURE_ROOT", Path.GetDirectoryName(reportPath));
                using var live = UiAutomationDriver.Attach(measuredProcessId);
                return live.MeasureLocation2AAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--attach-location-2a-remaining", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var remainingProcessId))
                    throw new ArgumentException("Usage: --attach-location-2a-remaining <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                Environment.SetEnvironmentVariable("GRID_UI_CAPTURE_ROOT", Path.GetDirectoryName(reportPath));
                using var live = UiAutomationDriver.Attach(remainingProcessId);
                return live.CompleteLocation2ARemainingAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--direct-knowledge-rebuild", StringComparison.Ordinal))
            {
                if (args.Length != 2)
                    throw new ArgumentException("Usage: --direct-knowledge-rebuild <report.json>");
                return await DirectKnowledgeRebuildAsync(Path.GetFullPath(args[1]));
            }
            if (args.Length > 0 && args[0].Equals("--attach-workstation-registration-refresh", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var refreshProcessId))
                    throw new ArgumentException("Usage: --attach-workstation-registration-refresh <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                using var live = UiAutomationDriver.Attach(refreshProcessId);
                return live.ExerciseWorkstationRegistrationRefreshAttached(reportPath) ? 0 : 1;
            }
            if (args.Length > 0 && args[0].Equals("--attach-location-2a", StringComparison.Ordinal))
            {
                if (args.Length != 3 || !int.TryParse(args[1], out var locationProcessId))
                    throw new ArgumentException("Usage: --attach-location-2a <existing-grid-pid> <report.json>");
                var reportPath = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                Environment.SetEnvironmentVariable("GRID_UI_CAPTURE_ROOT", Path.GetDirectoryName(reportPath));
                using var live = UiAutomationDriver.Attach(locationProcessId);
                return live.VerifyLocation2AAttached(reportPath) ? 0 : 1;
            }
            if (args.Length == 7 && args[0].Equals("--canonical-runtime-benchmark", StringComparison.Ordinal))
            {
                await CanonicalRuntimePerformanceChecks.RunAsync(
                    args[1], args[2], args[3],
                    args[4].Equals("--allow-candidate", StringComparison.Ordinal),
                    new CatalogPackageId(args[5]), args[6]);
                return 0;
            }
            if (args.Length is 5 or 6 && args[0].Equals("--canonical-runtime-probe", StringComparison.Ordinal))
            {
                Console.WriteLine(await ProbeCanonicalRuntimeAsync(
                    args[1], args[2], args[3],
                    args[4].Equals("--allow-candidate", StringComparison.Ordinal),
                    args.Length == 6 ? new CatalogPackageId(args[5]) : null));
                return 0;
            }
            if (args.Length == 6 && args[0].Equals("--canonical-locale-probe", StringComparison.Ordinal))
            {
                Console.WriteLine(await ProbeCanonicalLocaleAsync(
                    args[1], args[2], args[3],
                    args[4].Equals("--allow-candidate", StringComparison.Ordinal),
                    new CatalogPackageId(args[5])));
                return 0;
            }
            if (args.Length == 2 && args[0].Equals("--canonical-live-dif", StringComparison.Ordinal))
            {
                var liveExecutable = UiAutomationDriver.ResolveExecutable(configuration);
                using var live = await UiAutomationDriver.StartAsync(liveExecutable, demo: false, args[1]);
                live.VerifyCanonicalGtaLiveDif();
                VerifyRequestExecutionResponseContract();
                Console.WriteLine("Grid live canonical GTA DIF navigation passed.");
                return 0;
            }
            if (args.Length == 2 &&
                args[0].Equals("--attach-canonical-live-dif", StringComparison.Ordinal) &&
                int.TryParse(args[1], out var liveProcessId))
            {
                using var live = UiAutomationDriver.Attach(liveProcessId);
                live.VerifyCanonicalGtaLiveDif(completeLifecycle: true);
                VerifyRequestExecutionResponseContract();
                Console.WriteLine("Attached Grid canonical GTA DIF navigation passed.");
                return 0;
            }
            if (args.Length == 2 &&
                args[0].Equals("--attach-canonical-selectors-only", StringComparison.Ordinal) &&
                int.TryParse(args[1], out var selectorProcessId))
            {
                using var live = UiAutomationDriver.Attach(selectorProcessId);
                live.VerifyCanonicalGtaLiveDif();
                Console.WriteLine("Attached Grid canonical GTA selectors passed without submitting a ticket.");
                return 0;
            }
            if (args.Length == 2 &&
                args[0].Equals("--attach-normal-home-canonical-selectors", StringComparison.Ordinal) &&
                int.TryParse(args[1], out var normalHomeProcessId))
            {
                using var live = UiAutomationDriver.Attach(normalHomeProcessId);
                live.VerifyCanonicalGtaLiveDif(selectProfile: false);
                Console.WriteLine(
                    "Attached normal-start Grid canonical GTA selectors passed with the sole base profile resolved by product state.");
                return 0;
            }
            if (args.Length == 2 &&
                args[0].Equals("--attach-gta-casino-canary", StringComparison.Ordinal) &&
                int.TryParse(args[1], out var casinoProcessId))
            {
                using var live = UiAutomationDriver.Attach(casinoProcessId);
                live.VerifyCanonicalGtaCasinoCanary();
                VerifyRequestExecutionResponseContract();
                Console.WriteLine("Attached Grid GTA Casino Heist unresolved-context canary passed.");
                return 0;
            }
            if (args.Length == 2 && args[0].Equals("--inspect-add-game-profiles-read-only", StringComparison.Ordinal))
            {
                Console.WriteLine(await InspectAddGameProfilesReadOnlyAsync(args[1]));
                return 0;
            }
            if (args.Contains("--add-profile-contract-only", StringComparer.Ordinal))
            {
                VerifyAddProfileResolutionContract();
                Console.WriteLine("Grid Add Profile contract checks passed.");
                return 0;
            }
            VerifyPurePolicies();
            CanonicalCatalogRuntimeBindingStoreChecks.Run();
            VerifyFirstRunStore();
            VerifyAddProfileResolutionContract();
            await VerifyAccountScopedFirstRunAndConnectionStateAsync();
            await VerifyGameCatalogConnectionEngineAsync();
            VerifyOfflineAlertIndexStore();
            VerifyWorkspacePresentationStore();
            VerifySourceAcquisitionPreferencesStore();
            VerifyNexusSourceRoutingPolicy();
            VerifyWorkspaceColumnContract();
            VerifyShellPanelContract();
            VerifyCanonicalCatalogInspectionContract();
            VerifyCanonicalRuntimeSelectorContract();
            VerifyPreproductionAcquisitionPackagingBoundary();
            VerifyUserToolManagerContract();
            await VerifyInstalledToolIdentityServiceAsync();
            await VerifyWindowsUserToolLaunchServiceAsync();
            VerifyStartupAuthGateContract();
            VerifyAuthenticatedAvatarContract();
            VerifyLegacyTaskHistoryCompatibility();
            VerifyRequestExecutionResponseContract();
            VerifyRequestExecutionFailureContract();
            VerifyTaskboardProjection();
            VerifyInvestigationSnapshotContract();
            VerifyInvestigationActionMarkup();
            VerifyAssistantToolCatalogContract();
            AssistantTicketTaxonomyLoaderChecks.Run();
            await VerifyHistoryStoreAsync();
            if (args.Contains("--contract-only", StringComparer.Ordinal))
            {
                Console.WriteLine("Grid desktop contract checks passed without launching the application.");
                return 0;
            }
            var executable = UiAutomationDriver.ResolveExecutable(configuration);
            Console.WriteLine($"Grid UI executable: {executable}");
            if (args.Contains("--assistant-dif-only", StringComparer.Ordinal))
            {
                using var dif = await UiAutomationDriver.StartAsync(executable, demo: true);
                dif.VerifyCanonicalDifOnly();
                Console.WriteLine("Grid canonical Ticket DIF UI automation passed.");
                return 0;
            }
            var attachGeometryIndex = Array.FindIndex(args, argument =>
                argument.Equals("--attach-workstation-geometry", StringComparison.Ordinal));
            if (attachGeometryIndex >= 0)
            {
                if (attachGeometryIndex + 1 >= args.Length ||
                    !int.TryParse(args[attachGeometryIndex + 1], out var processId))
                {
                    throw new InvalidOperationException("--attach-workstation-geometry requires the installed GRID PID.");
                }

                using var installedGeometry = UiAutomationDriver.Attach(processId);
                installedGeometry.VerifyPanelToggles();
                installedGeometry.VerifyConnectedWorkspaceGeometry();
                Console.WriteLine("Grid installed workstation and shared shell geometry checks passed.");
                return 0;
            }
            if (args.Contains("--workstation-geometry-only", StringComparer.Ordinal))
            {
                using var geometry = await UiAutomationDriver.StartAsync(executable, demo: true);
                geometry.VerifyDemoIsExplicit();
                geometry.VerifyPanelToggles();
                geometry.VerifyDemoWorkspaceAndAssistant();
                Console.WriteLine("Grid installed workstation and shared shell geometry checks passed.");
                return 0;
            }
            var configuredDeviceRoot = Environment.GetEnvironmentVariable("GRID_UI_DATA_ROOT");
            var ownsSharedDeviceRoot = string.IsNullOrWhiteSpace(configuredDeviceRoot);
            var sharedDeviceRoot = ownsSharedDeviceRoot
                ? Path.Combine(Path.GetTempPath(), $"grid-ui-device-{Guid.NewGuid():N}")
                : Path.GetFullPath(configuredDeviceRoot!);
            Directory.CreateDirectory(sharedDeviceRoot);
            try
            {
                using (var driver = await UiAutomationDriver.StartAsync(executable, demo: false, sharedDeviceRoot))
                {
                    driver.VerifyProductionIsEmpty();
                    driver.VerifyAuthenticatedAccountAvatar();
                    driver.VerifyEditorShell();
                    driver.VerifyFirstRunWelcomeAndTabs();
                    driver.VerifyGameCatalogContract();
                    driver.VerifyActivityTaskboard();
                    driver.VerifyPanelToggles();
                    driver.VerifyShellCommandSeparation();
                    driver.VerifyHomeAssistant();
                    driver.VerifyGlobalNavigation();
                    driver.VerifyCanonicalCatalogInspection();
                    driver.VerifyResponsiveMatrix();
                    driver.VerifyWindowStateTransitions();
                }

                // Entering a normal product surface completes first-run for
                // this authenticated account/device without creating a connection.
                using (var relaunch = await UiAutomationDriver.StartAsync(executable, demo: false, sharedDeviceRoot))
                {
                    relaunch.VerifyReturningAccountHome();
                    relaunch.VerifyAuthenticatedAccountAvatar();
                }

                var accountsRoot = Path.Combine(sharedDeviceRoot, "accounts", "v1");
                var accountRoots = Directory.Exists(accountsRoot)
                    ? Directory.GetDirectories(accountsRoot)
                    : [];
                if (accountRoots.Length != 1 || Path.GetFileName(accountRoots[0]).Length != 64)
                {
                    throw new InvalidOperationException(
                        $"Expected one opaque authenticated account scope, found {accountRoots.Length}.");
                }
                var completedStore = new LocalFirstRunStateStore(Path.Combine(accountRoots[0], "setup", "first-run.v1.json"));
                if (!completedStore.IsComplete()) throw new InvalidOperationException("Entering Home did not persist account-scoped first-run completion.");
            }
            finally
            {
                if (ownsSharedDeviceRoot)
                {
                    try { Directory.Delete(sharedDeviceRoot, recursive: true); } catch { }
                }
            }

            using (var demo = await UiAutomationDriver.StartAsync(executable, demo: true))
            {
                demo.VerifyDemoIsExplicit();
                demo.VerifyEditorShell();
                demo.VerifyPanelToggles();
                demo.VerifyDemoWorkspaceAndAssistant();
                demo.VerifyResponsiveMatrix();
                demo.VerifyResponsiveMatrixWithAssistant();
            }
            Console.WriteLine("Grid UI checks passed: production empty, New Investigation intake, Demo isolation, and responsive bounds verified.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyPreproductionAcquisitionPackagingBoundary()
    {
        var scriptsRoot = Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts");
        if (!Directory.Exists(scriptsRoot))
            throw new InvalidOperationException("The runtime request-engine scripts were not packaged for inspection.");
        var forbidden = Directory.EnumerateFiles(scriptsRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(scriptsRoot, path))
            .Where(relative =>
            {
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return segments.Any(segment =>
                           segment.Equals("catalog", StringComparison.OrdinalIgnoreCase) ||
                           segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                           segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) ||
                       relative.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (forbidden.Length != 0)
            throw new InvalidOperationException(
                "Preproduction catalog/acquisition or test artifacts entered the runtime package: " +
                string.Join(", ", forbidden));
    }

    private static async Task<string> ProbeCanonicalLocaleAsync(
        string canonicalStorePath,
        string registrationStorePath,
        string gameIdValue,
        bool allowCandidate,
        CatalogPackageId pinnedPackageId)
    {
        var catalogService = new RegisteredGameCatalogService(
            new ProductionGridCatalogService(),
            new JsonGameInstallationRegistrationStore(registrationStorePath));
        var catalog = await catalogService.GetCatalogAsync();
        var gameId = new GameId(gameIdValue);
        var game = catalog.Games.Single(value => value.Id == gameId);
        var installation = game.Installations.Single(value =>
            value.Metadata.Availability == InstallationAvailability.Available);
        var profile = installation.Profiles.Single();
        var locale = new CanonicalTerminologyLocalePreference("en-US", ["en"]);
        var runtime = new CanonicalCatalogRuntimeService(
            canonicalStorePath, locale, allowCandidate, pinnedPackageId, usePreparedNavigation: false);
        await runtime.LoadAsync(catalog);
        var match = runtime.Match(gameId, installation.Id, profile.Id);
        if (!match.IsExact || match.ProjectionInput is null)
            throw new InvalidOperationException($"Locale probe runtime match failed: {match.State}: {match.Detail}");

        var payload = match.ProjectionInput.VerifiedPackage.Payload;
        var records = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var localeCounts = payload.TerminologyAssertions
            .GroupBy(value => new
            {
                kind = records[value.KnowledgeRecordId].Kind.ToString(),
                languageTag = value.LanguageTag ?? "(source-default)",
            })
            .OrderBy(value => value.Key.kind, StringComparer.Ordinal)
            .ThenBy(value => value.Key.languageTag, StringComparer.Ordinal)
            .Select(value => new
            {
                value.Key.kind,
                value.Key.languageTag,
                count = value.Count(),
            })
            .ToArray();

        TerminologyAssertion? Preferred(IEnumerable<TerminologyAssertion> values)
        {
            var terms = values.OrderBy(value => value.VerbatimValue, StringComparer.Ordinal)
                .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
                .ToArray();
            return terms.FirstOrDefault(value => string.Equals(
                       value.LanguageTag, locale.RequestedLanguageTag, StringComparison.Ordinal)) ??
                   locale.ApprovedLanguageFallbackTags
                       .Select(fallback => terms.FirstOrDefault(value =>
                           string.Equals(value.LanguageTag, fallback, StringComparison.Ordinal)))
                       .FirstOrDefault(value => value is not null) ??
                   terms.FirstOrDefault(value => value.LanguageTag is null);
        }

        var eligibleCounts = payload.KnowledgeRecords
            .GroupBy(value => value.Kind)
            .OrderBy(value => value.Key)
            .Select(group => new
            {
                kind = group.Key.ToString(),
                records = group.Count(),
                eligiblePrimaryNames = group.Count(record => Preferred(payload.TerminologyAssertions.Where(value =>
                    value.KnowledgeRecordId == record.Id &&
                    value.Role == TerminologyAssertionRole.PrimaryName)) is not null),
            })
            .ToArray();

        var leakedMissionTerms = payload.KnowledgeRecords
            .Where(value => value.Kind == KnowledgeKind.MissionQuest)
            .Select(record => Preferred(payload.TerminologyAssertions.Where(value =>
                value.KnowledgeRecordId == record.Id && value.Role == TerminologyAssertionRole.PrimaryName)))
            .Where(value => value is not null &&
                            value.LanguageTag is not null &&
                            !string.Equals(value.LanguageTag, "en-US", StringComparison.Ordinal) &&
                            !string.Equals(value.LanguageTag, "en", StringComparison.Ordinal))
            .ToArray();
        if (leakedMissionTerms.Length != 0)
            throw new InvalidOperationException("The English Mission projection admitted another locale.");

        var projectionRoots = Enum.GetValues<KnowledgeKind>()
            .Select(kind =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = runtime.Query(match, kind);
                stopwatch.Stop();
                if (kind == KnowledgeKind.MissionQuest && result.ImmediateChildren.Any(value =>
                        value.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology))
                    throw new InvalidOperationException(
                        "The English Mission root exposed unqualified canonical terminology.");
                return new
                {
                    kind = kind.ToString(),
                    immediateChildren = result.ImmediateChildren.Length,
                    canonicalTerminologyChildren = result.ImmediateChildren.Count(value =>
                        value.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology),
                    identifierChildren = result.ImmediateChildren.Count(value =>
                        value.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier),
                    elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                };
            })
            .ToArray();

        var wrong = payload.TerminologyAssertions.Single(value =>
            value.VerbatimValue == "Aprendendo os esquemas" && value.LanguageTag == "und");
        var claimId = EvidenceClaimContentId.DeriveV1(wrong);
        var binding = payload.EvidenceBindings.Single(value =>
            value.ClaimKind == EvidenceClaimKind.Terminology && value.ClaimContentId == claimId);
        var catalogReceipt = payload.FileEvidenceReceipts.Single(value => value.Id == binding.EvidenceReceiptId);
        var receipt = catalogReceipt.Receipt;
        var sourceRevision = payload.SourceRevisions.Single(value => value.Revision.Id == wrong.SourceRevisionId);
        var acquisitionBinding = payload.ArtifactAcquisitionBindings.Single(value =>
            value.ArtifactId == receipt.SourceArtifactId &&
            receipt.SourceFieldPath.StartsWith(
                $"rpf7-member:{value.MemberCoordinate.ExactRepresentation}#", StringComparison.Ordinal));
        var acquisitionReceipt = payload.AcquisitionReceipts.Single(value =>
            value.Id == acquisitionBinding.AcquisitionReceiptId);
        var acquisitionMember = acquisitionReceipt.Members.Single(value =>
            value.ArtifactId == receipt.SourceArtifactId &&
            value.MemberCoordinate == acquisitionBinding.MemberCoordinate);

        return JsonSerializer.Serialize(new
        {
            state = match.State.ToString(),
            packageId = match.PackageId?.Value,
            catalogRevisionId = match.CatalogRevisionId?.Value,
            sharedLibraryRevision = match.SharedLibraryRevision,
            requestedLanguageTag = locale.RequestedLanguageTag,
            approvedLanguageFallbackTags = locale.ApprovedLanguageFallbackTags,
            localeCounts,
            eligibleCounts,
            projectionRoots,
            leakedMissionTerms = leakedMissionTerms.Length,
            evidenceChain = new
            {
                knowledgeRecordId = wrong.KnowledgeRecordId.Value,
                sourceRevisionId = wrong.SourceRevisionId.Value,
                verbatimValue = wrong.VerbatimValue,
                languageTag = wrong.LanguageTag,
                sourceFieldPath = wrong.SourceFieldPath,
                claimContentId = claimId.Value,
                evidenceBindingId = binding.Id.Value,
                evidenceReceiptId = catalogReceipt.Id.Value,
                evidenceClass = receipt.Verification.ToString(),
                sourceArtifactId = receipt.SourceArtifactId.Value,
                artifactDigest = receipt.ArtifactDigest.HexValue,
                receipt.ParserId,
                receipt.ParserVersion,
                receipt.NativeRecordLocator,
                sourceId = sourceRevision.Revision.SourceId.Value,
                acquisitionMember = acquisitionBinding.MemberCoordinate.ExactRepresentation,
                acquisitionDigest = acquisitionMember.Digest.HexValue,
                acquisitionReceiptId = acquisitionReceipt.Id.Value,
            },
        });
    }

    private static async Task<string> ProbeCanonicalRuntimeAsync(
        string canonicalStorePath,
        string registrationStorePath,
        string gameIdValue,
        bool allowCandidate,
        CatalogPackageId? pinnedPackageId)
    {
        var catalogService = new RegisteredGameCatalogService(
            new ProductionGridCatalogService(),
            new JsonGameInstallationRegistrationStore(registrationStorePath));
        var catalog = await catalogService.GetCatalogAsync();
        var gameId = new GameId(gameIdValue);
        var game = catalog.Games.Single(value => value.Id == gameId);
        var installation = game.Installations.Single(value =>
            value.Metadata.Availability == InstallationAvailability.Available);
        var profile = installation.Profiles.Single();
        var runtime = new CanonicalCatalogRuntimeService(
            canonicalStorePath,
            new CanonicalTerminologyLocalePreference("en-US", ["en"]),
            allowCandidate,
            pinnedPackageId,
            usePreparedNavigation: false);
        var loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await runtime.LoadAsync(catalog);
        var loadElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(loadStarted);
        var coldMatchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var match = runtime.Match(gameId, installation.Id, profile.Id);
        var coldMatchElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(coldMatchStarted);
        if (!match.IsExact)
            return JsonSerializer.Serialize(new
            {
                state = match.State.ToString(),
                detail = match.Detail,
                allowsCandidate = runtime.AllowsCandidatePackages,
                pinnedPackageId = runtime.PinnedPackageId?.Value,
            });

        var cachedMatchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var repeatedMatch = runtime.Match(gameId, installation.Id, profile.Id);
        var cachedMatchElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(cachedMatchStarted);
        if (!repeatedMatch.IsExact ||
            !ReferenceEquals(match, repeatedMatch) ||
            !ReferenceEquals(match.ProjectionInput, repeatedMatch.ProjectionInput))
            throw new InvalidOperationException(
                "Repeated exact matching did not reuse the already-verified immutable runtime match.");

        var counts = new Dictionary<string, object>(StringComparer.Ordinal);
        CanonicalSelectorSelection? selectionForStaleMatchProbe = null;
        var coldQueryElapsed = TimeSpan.Zero;
        var cachedQueryElapsed = TimeSpan.Zero;
        var queryCount = 0;
        foreach (var kind in Enum.GetValues<KnowledgeKind>())
        {
            var pending = new Stack<CanonicalNavigationPathId?>();
            pending.Push(null);
            var visitedPaths = new HashSet<CanonicalNavigationPathId>();
            var records = new HashSet<KnowledgeRecordId>();
            var named = new HashSet<KnowledgeRecordId>();
            CanonicalSelectorSelection? emittedSelection = null;
            while (pending.Count > 0)
            {
                var path = pending.Pop();
                var coldQueryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = runtime.Query(match, kind, path);
                coldQueryElapsed += System.Diagnostics.Stopwatch.GetElapsedTime(coldQueryStarted);
                var cachedQueryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var cachedResult = runtime.Query(repeatedMatch, kind, path);
                cachedQueryElapsed += System.Diagnostics.Stopwatch.GetElapsedTime(cachedQueryStarted);
                queryCount++;
                if (!ReferenceEquals(result, cachedResult))
                    throw new InvalidOperationException(
                        "Repeated selector queries did not reuse the verified immutable result.");
                if (!visitedPaths.Add(result.CurrentPathId)) continue;
                foreach (var child in result.ImmediateChildren)
                {
                    if (child.CanDescend) pending.Push(child.PathId);
                    if (child.KnowledgeRecordId is not KnowledgeRecordId recordId) continue;
                    if (emittedSelection is null)
                    {
                        emittedSelection = CanonicalSelectorProjectionEngine.Select(result, child.PathId);
                        selectionForStaleMatchProbe ??= emittedSelection;
                        if (!runtime.ValidateSelection(match, emittedSelection))
                            throw new InvalidOperationException(
                                "The runtime rejected a selection emitted by its current verified projection.");
                    }
                    records.Add(recordId);
                    if (child.DisplayKind == CanonicalNavigationDisplayKind.CanonicalTerminology) named.Add(recordId);
                }
            }
            if (emittedSelection is null)
                throw new InvalidOperationException("The verified projection unexpectedly contained no selectable record.");
            var alteredSelection = new CanonicalSelectorSelection(
                CanonicalSelectorSelectionKind.CanonicalRecord,
                emittedSelection.KnowledgeKind,
                emittedSelection.CatalogRevisionId,
                emittedSelection.CatalogCompositionId,
                emittedSelection.ProjectionPolicyId,
                emittedSelection.ProjectionPolicyVersion,
                emittedSelection.SelectedPathId,
                new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('0', 64)),
                null);
            if (runtime.ValidateSelection(match, alteredSelection))
                throw new InvalidOperationException(
                    "The runtime accepted a record ID that was not bound to the selected projected path.");
            counts[kind.ToString()] = new { records = records.Count, playerFacingTerminology = named.Count };
        }
        var actorWithoutIdentifiers = runtime.Query(match, KnowledgeKind.Actor, includeIdentifierOnly: false);
        if (!ReferenceEquals(actorWithoutIdentifiers,
                runtime.Query(repeatedMatch, KnowledgeKind.Actor, includeIdentifierOnly: false)) ||
            ReferenceEquals(actorWithoutIdentifiers,
                runtime.Query(match, KnowledgeKind.Actor, includeIdentifierOnly: true)))
            throw new InvalidOperationException(
                "Selector cache keys did not isolate the identifier-only projection mode.");
        if (selectionForStaleMatchProbe is null)
            throw new InvalidOperationException("The runtime probe did not retain a projected selection for reload validation.");
        var moddedProfile = profile with
        {
            Mods =
            [
                new ModEntry(
                    new ModId("runtime-probe-mod"), "Runtime probe mod", "1", "probe", true, 0,
                    HealthLevel.Healthy),
            ],
        };
        var moddedInstallation = installation with
        {
            Profiles = installation.Profiles
                .Select(value => value.Id == profile.Id ? moddedProfile : value)
                .ToImmutableArray(),
        };
        var moddedGame = game with
        {
            Installations = game.Installations
                .Select(value => value.Id == installation.Id ? moddedInstallation : value)
                .ToImmutableArray(),
        };
        var moddedCatalog = new GridCatalogSnapshot(
            catalog.Revision + ".runtime-probe-modded",
            catalog.SourceKind,
            catalog.Games.Select(value => value.Id == game.Id ? moddedGame : value));
        await runtime.LoadAsync(moddedCatalog);
        var moddedMatch = runtime.Match(gameId, installation.Id, profile.Id);
        if (moddedMatch.State != CanonicalRuntimeMatchState.InsufficientEvidence ||
            runtime.ValidateSelection(match, selectionForStaleMatchProbe))
            throw new InvalidOperationException(
                "A modded profile did not fail closed or an earlier projection selection survived catalog drift.");
        try
        {
            _ = runtime.Query(match, selectionForStaleMatchProbe.KnowledgeKind);
            throw new InvalidOperationException("A stale runtime match remained queryable after catalog reload.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("current exact verified runtime match", StringComparison.Ordinal))
        {
        }

        await runtime.LoadAsync(catalog);
        var reloadedMatch = runtime.Match(gameId, installation.Id, profile.Id);
        if (!reloadedMatch.IsExact || ReferenceEquals(match, reloadedMatch) ||
            ReferenceEquals(match.ProjectionInput, reloadedMatch.ProjectionInput) ||
            ReferenceEquals(actorWithoutIdentifiers,
                runtime.Query(reloadedMatch, KnowledgeKind.Actor, includeIdentifierOnly: false)))
            throw new InvalidOperationException(
                "Reloading the observed catalog did not invalidate runtime match and projection caches.");
        return JsonSerializer.Serialize(new
        {
            state = match.State.ToString(),
            packageId = match.PackageId?.Value,
            catalogRevisionId = match.CatalogRevisionId?.Value,
            sharedLibraryRevision = match.SharedLibraryRevision,
            compositionId = match.CatalogCompositionId?.Value,
            validationStatus = match.ValidationStatus?.ToString(),
            requestedLanguageTag = runtime.TerminologyLocale.RequestedLanguageTag,
            approvedLanguageFallbackTags = runtime.TerminologyLocale.ApprovedLanguageFallbackTags,
            knowledge = counts,
            cacheBenchmark = new
            {
                loadMilliseconds = loadElapsed.TotalMilliseconds,
                coldMatchMilliseconds = coldMatchElapsed.TotalMilliseconds,
                cachedMatchMilliseconds = cachedMatchElapsed.TotalMilliseconds,
                queryCount,
                coldQueryMilliseconds = coldQueryElapsed.TotalMilliseconds,
                cachedQueryMilliseconds = cachedQueryElapsed.TotalMilliseconds,
            },
        });
    }

    private static async Task<string> InspectAddGameProfilesReadOnlyAsync(string gameDirectory)
    {
        var priorDataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"grid-add-game-profile-inspect-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable("GRID_DATA_ROOT", isolatedRoot);
            var composition = GridCompositionRoot.CreateProduction("read-only-add-game-acceptance");
            var catalog = await composition.CatalogService.GetCatalogAsync();
            var game = catalog.Games.Single(candidate => candidate.Id == ProductionGridCatalogService.SkyrimSpecialEditionId);
            var service = composition.ExistingProfileDiscoveryService
                ?? throw new InvalidOperationException("Existing-profile discovery was not composed.");
            var result = await service.DiscoverAsync(
                new(game.Id, game.Name, Path.GetFullPath(gameDirectory), []),
                catalog);
            var filesWritten = Directory.Exists(isolatedRoot)
                ? Directory.EnumerateFiles(isolatedRoot, "*", SearchOption.AllDirectories).ToArray()
                : [];
            return JsonSerializer.Serialize(new
            {
                game.Id,
                InstallationRoot = Path.GetFullPath(gameDirectory),
                Profiles = result.Profiles.Select(profile => new
                {
                    profile.ProfileName,
                    profile.ManagerName,
                    profile.GameDirectory,
                    profile.EnabledModCount,
                    profile.ActivePluginCount,
                }),
                Issues = result.Issues,
                FilesWritten = filesWritten,
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRID_DATA_ROOT", priorDataRoot);
            try { if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, recursive: true); } catch { }
        }
    }

    private static void VerifyLegacyTaskHistoryCompatibility()
    {
        const string json = """
            {
              "taskId": "legacy-empty-repair",
              "createdAt": "2026-09-01T00:00:00Z",
              "classId": "grid.class.installation-integrity",
              "terminalState": "RepairPlanned",
              "rawPrompt": "Apply a sealed repair.",
              "result": {
                "affectedMods": {},
                "modRoles": [],
                "finding": "A sealed repair is available.",
                "solution": "Review the repair.",
                "evidenceToolIds": []
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var document = JsonDocument.Parse(json);
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var parseTask = serviceType.GetMethod("ParseTask", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Task-history parser was not found.");
        try
        {
            _ = parseTask.Invoke(null, [document.RootElement]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException("Legacy empty repair history must remain loadable.", exception.InnerException);
        }

        const string capabilityJson = """
            {
              "taskId": "capability-assessment",
              "createdAt": "2026-09-11T16:00:00Z",
              "classId": "grid.class.outfits-bodies-physics",
              "terminalState": "EvidenceComplete",
              "result": {
                "affectedMods": [],
                "modRoles": [],
                "finding": "No installed mod in the captured profile satisfies this capability.",
                "solution": "Review the compatible provider candidate.",
                "evidenceToolIds": [],
                "capabilityAssessment": {
                  "schemaVersion": 1,
                  "capabilityId": "grid.capability.equipment.multiple-rings",
                  "displayName": "Wear rings on multiple fingers",
                  "installedStatus": "Absent",
                  "installedProviders": [],
                  "discoveryStatus": "Current",
                  "observedAtUtc": "2026-09-11T16:00:00Z",
                  "communityCandidates": [{
                    "name": "Fixture Multi-Ring Provider",
                    "provider": "Nexus",
                    "uri": "https://www.nexusmods.com/skyrimspecialedition/mods/12345",
                    "version": "2.0.0",
                    "compatibilityStatus": "Compatible",
                    "compatibilityDetail": "The fixture profile satisfies its declared requirements.",
                    "requiredPatches": [],
                    "evidenceIds": ["provider-evidence.fixture"]
                  }],
                  "evidenceIds": ["profile-evidence.fixture", "provider-evidence.fixture"]
                }
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var capabilityDocument = JsonDocument.Parse(capabilityJson);
        var parsed = (AssistantTaskRecord?)parseTask.Invoke(null, [capabilityDocument.RootElement]);
        if (parsed?.Finding?.CapabilityAssessment is not
            {
                InstalledStatus: AssistantInstalledCapabilityStatus.Absent,
                DiscoveryStatus: AssistantProviderDiscoveryStatus.Current,
                CommunityCandidates.Length: 1,
            } assessment ||
            assessment.CommunityCandidates[0].CompatibilityStatus != AssistantCandidateCompatibilityStatus.Compatible)
        {
            throw new InvalidOperationException("Capability assessment history did not preserve installed coverage, discovery freshness, and compatibility.");
        }

        const string detailedTaskJson = """
            {
              "taskId": "detailed-intake",
              "createdAt": "2026-09-11T16:00:00Z",
              "classId": "grid.class.installation-integrity",
              "terminalState": "EvidenceComplete",
              "caseId": "case-detailed-intake",
              "rawPrompt": "Audit the complete profile.",
              "intake": {
                "expectedBehavior": "All enabled components are complete and compatible.",
                "reproductionLocation": "The selected installation and profile.",
                "desiredOutcome": "Produce an evidence-backed repair plan."
              },
              "result": {
                "affectedMods": [],
                "modRoles": [],
                "finding": "Fixture finding.",
                "solution": "Fixture solution.",
                "evidenceToolIds": []
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var detailedTaskDocument = JsonDocument.Parse(detailedTaskJson);
        var detailedTask = (AssistantTaskRecord?)parseTask.Invoke(null, [detailedTaskDocument.RootElement]);
        var detailedClaim = detailedTask?.Transcript.Single(entry => entry.Kind == AssistantTranscriptKind.UserClaim).Text;
        if (detailedClaim is null ||
            !detailedClaim.Contains("Expected behavior: All enabled components are complete and compatible.", StringComparison.Ordinal) ||
            !detailedClaim.Contains("Reproduction or location: The selected installation and profile.", StringComparison.Ordinal) ||
            !detailedClaim.Contains("Desired outcome: Produce an evidence-backed repair plan.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted task history did not render every structured investigation instruction.");
        }
    }

    private static void VerifyRequestExecutionResponseContract()
    {
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var createInput = serviceType.GetMethod("CreateInput", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The deterministic bridge input projection was not found.");
        var bridge = Activator.CreateInstance(serviceType, [FindRepositoryRoot(), Path.GetTempPath()])
            ?? throw new InvalidOperationException("The deterministic request bridge could not be constructed for contract inspection.");
        var canonicalSelection = new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            KnowledgeKind.Location,
            new CatalogRevisionId("grid.catalog-revision.v5.sha256." + new string('5', 64)),
            new CatalogCompositionId("composition.bridge.fixture"),
            CanonicalSelectorProjectionPolicy.V1.Id,
            CanonicalSelectorProjectionPolicy.V1.ExactVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('6', 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('7', 64)),
            null);
        var bridgeDraft = new AssistantRequestDraft(
            new GameId("game.grandtheftautov-enhanced"), new InstallationId("installation.fixture"),
            new ProfileId("profile.fixture"), [], [], "grid.class.fixture", "1", "Fixture claim", "Fixture",
            CanonicalSelections: [canonicalSelection],
            UnresolvedUserContext:
            [
                new(TicketReferenceContextKind.Entity, "  exact unresolved actor  ",
                    TicketUserContextResolution.Unresolved, TicketSelectionProvenance.ExplicitUserSelection),
            ],
            ProblemSelection: new(new TicketProblemId("grid.problem.crash"), new TicketClassId("grid.class.fixture"),
                "Crash", TicketSelectionProvenance.ExplicitUserSelection),
            TimingSelection: new(new TicketTimingId("grid.timing.after-leaving-activity"), new TicketClassId("grid.class.fixture"),
                "After leaving an activity", TicketSelectionProvenance.ExplicitUserSelection),
            GoalSelection: new(new TicketGoalId("grid.goal.identify-evidence-backed-cause"),
                "Identify evidence-backed cause", TicketSelectionProvenance.ExplicitUserSelection));
        var bridgeInput = createInput.Invoke(bridge, ["Prepare", bridgeDraft, "submission.fixture", null]);
        using var bridgeInputDocument = JsonDocument.Parse(JsonSerializer.Serialize(bridgeInput));
        var bridgeInputRoot = bridgeInputDocument.RootElement;
        if (bridgeInputRoot.GetProperty("canonicalSelections")[0].GetProperty("knowledgeRecordId").GetString() !=
                canonicalSelection.KnowledgeRecordId?.Value ||
            bridgeInputRoot.GetProperty("canonicalSelections")[0].GetProperty("selectedPathId").GetString() !=
                canonicalSelection.SelectedPathId?.Value ||
            bridgeInputRoot.GetProperty("unresolvedUserContext")[0].GetProperty("value").GetString() !=
                "  exact unresolved actor  " ||
            bridgeInputRoot.GetProperty("problemSelection").GetProperty("id").GetString() != "grid.problem.crash" ||
            bridgeInputRoot.GetProperty("timingSelection").GetProperty("displayName").GetString() != "After leaving an activity" ||
            bridgeInputRoot.GetProperty("goalSelection").GetProperty("id").GetString() != "grid.goal.identify-evidence-backed-cause")
        {
            throw new InvalidOperationException(
                "The deterministic request bridge dropped or rewrote canonical selector or unresolved Other coordinates.");
        }
        var verifyPreparedIntake = serviceType.GetMethod("VerifyPreparedIntake", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Prepared-intake integrity verification was not found.");
        var exactIntakeNode = System.Text.Json.Nodes.JsonNode.Parse(bridgeInputRoot.GetRawText())!.AsObject();
        exactIntakeNode["schemaVersion"] = 3;
        exactIntakeNode["taxonomyId"] = "grid.investigation-taxonomy.minimal-canary";
        exactIntakeNode["taxonomyVersion"] = "1.0.0";
        var exactIntakeJson = exactIntakeNode.ToJsonString();
        using (var exactIntakeDocument = JsonDocument.Parse(exactIntakeJson))
            verifyPreparedIntake.Invoke(null, [exactIntakeDocument.RootElement, bridgeDraft]);
        foreach (var (original, replacement) in new (string Original, string Replacement)[]
                 {
                     (canonicalSelection.KnowledgeRecordId!.Value.Value, "grid.knowledge-record.v1.sha256." + new string('8', 64)),
                     (canonicalSelection.SelectedPathId!.Value.Value, "grid.canonical-navigation-path.v1.sha256." + new string('9', 64)),
                     (canonicalSelection.CatalogRevisionId.Value, "grid.catalog-revision.v5.sha256." + new string('4', 64)),
                     (canonicalSelection.CatalogCompositionId.Value, "composition.bridge.changed"),
                     ("  exact unresolved actor  ", "changed unresolved actor"),
                     ("grid.problem.crash", "grid.problem.changed"),
                     ("After leaving an activity", "Changed timing"),
                     ("grid.goal.identify-evidence-backed-cause", "grid.goal.changed"),
                 })
        {
            AssertPreparedIntakeRejected(exactIntakeJson.Replace(original, replacement, StringComparison.Ordinal));
        }
        var droppedCanonical = System.Text.Json.Nodes.JsonNode.Parse(exactIntakeJson)!.AsObject();
        droppedCanonical["canonicalSelections"] = new System.Text.Json.Nodes.JsonArray();
        AssertPreparedIntakeRejected(droppedCanonical.ToJsonString());
        var droppedOther = System.Text.Json.Nodes.JsonNode.Parse(exactIntakeJson)!.AsObject();
        droppedOther["unresolvedUserContext"] = new System.Text.Json.Nodes.JsonArray();
        AssertPreparedIntakeRejected(droppedOther.ToJsonString());

        void AssertPreparedIntakeRejected(string json)
        {
            using var document = JsonDocument.Parse(json);
            try
            {
                _ = verifyPreparedIntake.Invoke(null, [document.RootElement, bridgeDraft]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException(
                "The request bridge accepted changed or dropped canonical selector coordinates or unresolved Other context.");
        }

        var parseHistory = serviceType.GetMethod("ParseTaskHistoryResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Task-history response parser was not found.");
        using var emptyHistoryDocument = JsonDocument.Parse("""{ "tasks": [] }""");
        var emptyHistory = (System.Collections.IEnumerable?)parseHistory.Invoke(null, [emptyHistoryDocument.RootElement])
            ?? throw new InvalidOperationException("The task-history parser returned no collection.");
        if (emptyHistory.Cast<object>().Any())
            throw new InvalidOperationException("An explicit empty task-history array did not remain empty.");
        foreach (var invalidHistory in new[] { "{}", "{ \"tasks\": {} }" })
        {
            using var invalidHistoryDocument = JsonDocument.Parse(invalidHistory);
            try
            {
                _ = parseHistory.Invoke(null, [invalidHistoryDocument.RootElement]);
                throw new InvalidOperationException("An invalid history response was silently treated as a normal empty task list.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
            }
        }
        var parseResponse = serviceType.GetMethod("ParseExecuteResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Authorized-execution response parser was not found.");

        const string result = """
            {
              "affectedMods": [],
              "modRoles": [],
              "finding": "Fixture evidence was collected.",
              "solution": "Review the sealed fixture evidence.",
              "evidenceToolIds": [],
              "evidenceIds": [],
              "repairState": {
                "specificationAvailable": false,
                "applyEnabled": false,
                "rollbackAvailable": false
              },
              "mutationAuthorized": false,
              "terminalState": "EvidenceComplete"
            }
            """;
        using var normalDocument = JsonDocument.Parse($$"""
            {
              "tasks": [],
              "execution": {
                "CaseId": "case-normal",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        var normal = Invoke(normalDocument.RootElement, "submission-normal");
        if (normal.TaskId != "submission-normal" || normal.CaseId != "case-normal" || normal.TerminalState != "EvidenceComplete")
            throw new InvalidOperationException("A normal authorized execution with tasks: [] was not parsed as its execution result.");

        using var omittedTasksDocument = JsonDocument.Parse($$"""
            {
              "execution": {
                "CaseId": "case-no-tasks-property",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        if (Invoke(omittedTasksDocument.RootElement, "submission-no-tasks").CaseId != "case-no-tasks-property")
            throw new InvalidOperationException("A normal authorized execution without a tasks property was not parsed.");

        using var recoveredDocument = JsonDocument.Parse($$"""
            {
              "tasks": [{
                "taskId": "case-recovered",
                "createdAt": "2026-09-11T16:00:00Z",
                "classId": "grid.class.installation-integrity",
                "terminalState": "EvidenceComplete",
                "caseId": "case-recovered",
                "rawPrompt": "Recover this sealed task.",
                "result": {{result}},
                "toolReceipts": [],
                "repairState": {
                  "specificationAvailable": false,
                  "applyEnabled": false,
                  "rollbackAvailable": false
                },
                "resumable": false
              }],
              "execution": null
            }
            """);
        var recovered = Invoke(recoveredDocument.RootElement, "ignored-for-recovered-task");
        if (recovered.TaskId != "case-recovered" || recovered.CaseId != "case-recovered")
            throw new InvalidOperationException("A one-task recovery response did not preserve its sealed task identity.");

        using var multipleTasksDocument = JsonDocument.Parse($$"""
            {
              "tasks": [
                { "taskId": "one" },
                { "taskId": "two" }
              ],
              "execution": null
            }
            """);
        AssertInvalid(multipleTasksDocument.RootElement, "Multiple returned tasks must fail closed.");

        using var invalidTasksDocument = JsonDocument.Parse($$"""
            {
              "tasks": {},
              "execution": {
                "CaseId": "must-not-fall-through",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        AssertInvalid(invalidTasksDocument.RootElement, "A non-array tasks property must fail closed.");

        var resolveDirectAction = serviceType.GetMethod("ResolveDirectActionOperation", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Direct-action routing policy was not found.");
        if (!string.Equals((string?)resolveDirectAction.Invoke(null, [AssistantCaseAction.Diagnose]), "Diagnose", StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnose must retain its deterministic direct-action route.");
        foreach (var authorizedAction in new[]
                 {
                     AssistantCaseAction.AttachEvidence,
                     AssistantCaseAction.CaptureCurrentState,
                     AssistantCaseAction.RefreshRecoverySources,
                     AssistantCaseAction.ApplyRepair,
                     AssistantCaseAction.RollBack,
                 })
        {
            try
            {
                _ = resolveDirectAction.Invoke(null, [authorizedAction]);
                throw new InvalidOperationException($"{authorizedAction} bypassed preparation and explicit authorization.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
            }
        }

        var parsePreparedDraft = serviceType.GetMethod("ParsePreparedDraft", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Prepared successor parser was not found.");
        using var envelopeDocument = JsonDocument.Parse("""
            {
              "context": {
                "gameId": "skyrimspecialedition",
                "installationId": "installation.fixture",
                "profileId": "profile.fixture"
              },
              "class": {
                "classId": "grid.class.asset-mismatch",
                "recipeVersion": "1.0.0"
              },
              "selections": {
                "mods": [],
                "tools": [],
                "capabilities": []
              },
              "claims": { "text": "Inspect attached evidence." }
            }
            """);
        using var intakeDocument = JsonDocument.Parse("""
            {
              "expectedBehavior": "Evidence remains bound to the successor.",
              "reproductionLocation": "Fixture profile.",
              "desiredOutcome": "Preserve the selected attachment.",
              "problemSelection": {
                "id": "grid.problem.crash",
                "classId": "grid.class.asset-mismatch",
                "displayName": "Crash",
                "provenance": "ExplicitUserSelection"
              },
              "timingSelection": {
                "id": "grid.timing.after-leaving-activity",
                "classId": "grid.class.asset-mismatch",
                "displayName": "After leaving an activity",
                "provenance": "ExplicitUserSelection"
              },
              "goalSelection": {
                "id": "grid.goal.identify-evidence-backed-cause",
                "displayName": "Identify evidence-backed cause",
                "provenance": "ExplicitUserSelection"
              },
              "canonicalSelections": [{
                "selectionKind": "CanonicalRecord",
                "knowledgeKind": "Location",
                "catalogRevisionId": "grid.catalog-revision.v5.sha256.5555555555555555555555555555555555555555555555555555555555555555",
                "catalogCompositionId": "composition.bridge.fixture",
                "projectionPolicyId": "grid.canonical-selector-projection",
                "projectionPolicyVersion": "1",
                "selectedPathId": "grid.canonical-navigation-path.v1.sha256.6666666666666666666666666666666666666666666666666666666666666666",
                "knowledgeRecordId": "grid.knowledge-record.v1.sha256.7777777777777777777777777777777777777777777777777777777777777777",
                "unresolvedOtherContextId": null
              }],
              "unresolvedUserContext": [{
                "kind": "Entity",
                "value": "  exact unresolved actor  ",
                "resolution": "Unresolved",
                "provenance": "ExplicitUserSelection",
                "matchedReferenceId": null
              }],
              "attachments": [{
                "path": "C:\\fixture\\evidence.png",
                "mediaType": "image/png"
              }]
            }
            """);
        AssistantRequestDraft successorDraft;
        try
        {
            successorDraft = (AssistantRequestDraft?)parsePreparedDraft.Invoke(
                null,
                [envelopeDocument.RootElement, intakeDocument.RootElement, AssistantCaseAction.CaptureCurrentState])
                ?? throw new InvalidOperationException("The prepared successor parser returned no draft.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
        if (successorDraft.Attachments is not [{ OriginalName: "evidence.png", MediaType: "image/png" }])
            throw new InvalidOperationException("A reopened Attach Evidence successor did not preserve its authorized attachment in session state.");
        if (!string.Equals(successorDraft.DisplayTitle, "Capture current state", StringComparison.Ordinal))
            throw new InvalidOperationException("A reopened Capture Current State successor did not preserve its action-specific title.");
        if (successorDraft.CanonicalSelections.Single() is not
            {
                KnowledgeKind: KnowledgeKind.Location,
                KnowledgeRecordId.Value: "grid.knowledge-record.v1.sha256.7777777777777777777777777777777777777777777777777777777777777777",
                SelectedPathId.Value: "grid.canonical-navigation-path.v1.sha256.6666666666666666666666666666666666666666666666666666666666666666",
            } || successorDraft.UnresolvedUserContext.Single().Value != "  exact unresolved actor  ")
            throw new InvalidOperationException(
                "The legacy successor bridge dropped or rewrote canonical selector coordinates or exact unresolved Other context.");
        if (successorDraft.ProblemSelection?.Id.Value != "grid.problem.crash" ||
            successorDraft.TimingSelection?.Id.Value != "grid.timing.after-leaving-activity" ||
            successorDraft.GoalSelection?.Id.Value != "grid.goal.identify-evidence-backed-cause")
            throw new InvalidOperationException("The successor bridge dropped structured Problem, Timing, or Goal identity.");
        using var legacyIntakeDocument = JsonDocument.Parse("""
            {
              "schemaVersion": 2,
              "expectedBehavior": "Historical expectation.",
              "reproductionLocation": "Historical location.",
              "desiredOutcome": "Historical outcome.",
              "canonicalSelections": [],
              "unresolvedUserContext": [],
              "attachments": []
            }
            """);
        var legacyDraft = (AssistantRequestDraft?)parsePreparedDraft.Invoke(
            null,
            [envelopeDocument.RootElement, legacyIntakeDocument.RootElement, AssistantCaseAction.CaptureCurrentState])
            ?? throw new InvalidOperationException("The historical v2 intake parser returned no draft.");
        if (legacyDraft.ProblemSelection is not null || legacyDraft.TimingSelection is not null || legacyDraft.GoalSelection is not null)
            throw new InvalidOperationException("A historical v2 intake was silently reinterpreted as structured taxonomy.");

        AssistantExecutionResult Invoke(JsonElement response, string taskId)
        {
            try
            {
                return (AssistantExecutionResult?)parseResponse.Invoke(null, [taskId, response])
                    ?? throw new InvalidOperationException("The execution response parser returned no result.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        void AssertInvalid(JsonElement response, string message)
        {
            try
            {
                _ = Invoke(response, "invalid-response");
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
    }

    private static void VerifyRequestExecutionFailureContract()
    {
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var sanitizeError = serviceType.GetMethod("SanitizeError", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The request-bridge failure sanitizer was not found.");

        const string bridgeOutput = """
            {
              "errorCode": "RequestSubmissionFailed",
              "message": "The property 'proposalId' cannot be found on this object."
            }
            """;
        var visible = (string?)sanitizeError.Invoke(null, [bridgeOutput, string.Empty]);
        if (visible is null ||
            !visible.Contains("RequestSubmissionFailed", StringComparison.Ordinal) ||
            !visible.Contains("proposalId", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A structured bridge failure discarded its bounded diagnostic message.");
        }

        const string secretOutput = """
            {
              "errorCode": "RequestSubmissionFailed",
              "message": "authorizationSecret=never-show-this"
            }
            """;
        var redacted = (string?)sanitizeError.Invoke(null, [secretOutput, string.Empty]);
        if (redacted is null || redacted.Contains("never-show-this", StringComparison.Ordinal) ||
            !redacted.Contains("[REDACTED]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bridge failure sanitizer did not redact a credential-like value.");
        }
    }

    private static void VerifyWorkspacePresentationStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-presentation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "presentation.v1.json");
            var store = new LocalWorkspacePresentationStore(path);
            var expected = new WorkspacePresentationState(
                "above",
                "skyrim",
                EnvironmentTabCapability.Plugins.ToString(),
                ["separator.weather", "separator.interface"],
                612,
                [76, 56, 72, 72, 86],
                [76, 62, 76, 0, 0],
                3);
            if (!store.Save("profile.one", expected) || store.Load("profile.one") is not { } observed ||
                observed.ModSearch != expected.ModSearch ||
                observed.EnvironmentSearch != expected.EnvironmentSearch ||
                observed.LeftPaneWidth != expected.LeftPaneWidth ||
                observed.ModColumnLayoutVersion != expected.ModColumnLayoutVersion ||
                !observed.CollapsedSeparatorIds.SequenceEqual(expected.CollapsedSeparatorIds) ||
                !observed.ModColumnWidths.SequenceEqual(expected.ModColumnWidths) ||
                !observed.EnvironmentColumnWidths.SequenceEqual(expected.EnvironmentColumnWidths))
            {
                throw new InvalidOperationException("Workspace presentation state did not round-trip exactly.");
            }

            if (store.Load("profile.two") is not null)
            {
                throw new InvalidOperationException("Workspace presentation state leaked between profiles.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySourceAcquisitionPreferencesStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "grid-source-acquisition-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "source-acquisition.v1.json");
            var store = new LocalSourceAcquisitionPreferencesStore(path);
            if (store.Load().AutomaticallyDownloadVerifiedSources)
                throw new InvalidOperationException("Automatic source downloads must default off.");
            if (!store.Save(new SourceAcquisitionPreferences(true)) || !store.Load().AutomaticallyDownloadVerifiedSources)
                throw new InvalidOperationException("Automatic source download preference did not round-trip.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyNexusSourceRoutingPolicy()
    {
        if (!NexusRecoverySourceDownloader.TryParseOfficialFilesUri(
                "https://www.nexusmods.com/skyrimspecialedition/mods/29194?tab=files", out var game, out var modId) ||
            game != "skyrimspecialedition" || modId != 29194)
            throw new InvalidOperationException("An exact official Nexus Files route was not accepted.");
        foreach (var refused in new[]
        {
            "http://www.nexusmods.com/skyrimspecialedition/mods/29194?tab=files",
            "https://evil.example/skyrimspecialedition/mods/29194?tab=files",
            "https://www.nexusmods.com/skyrimspecialedition/mods/not-a-number?tab=files",
            "https://www.nexusmods.com/skyrimspecialedition/mods/29194/files/12",
        })
        {
            if (NexusRecoverySourceDownloader.TryParseOfficialFilesUri(refused, out _, out _))
                throw new InvalidOperationException($"An unsafe or inexact Nexus route was accepted: {refused}");
        }
    }

    private static void VerifyWorkspaceColumnContract()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml"));
        const string pluginHeaders =
            "EnvironmentTabCapability.Plugins => (\"NAME\", \"FLAGS\", \"PRIORITY\", \"MOD INDEX\", \"\")";
        if (!source.Contains(pluginHeaders, StringComparison.Ordinal) ||
            !source.Contains("DefaultModColumnWidths = [76, 56, 72, 72, 86]", StringComparison.Ordinal) ||
            !source.Contains("CenterColumn4: true", StringComparison.Ordinal) ||
            !source.Contains("Header4Alignment = Capability == EnvironmentTabCapability.Plugins", StringComparison.Ordinal) ||
            !xaml.Contains("<ColumnDefinition Width=\"30\" />", StringComparison.Ordinal) ||
            !xaml.Contains("<ScaleTransform ScaleX=\"0.75\" ScaleY=\"0.75\" />", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ModFlagsHeaderColumn\" Width=\"56\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ModPriorityHeaderColumn\" Width=\"72\"", StringComparison.Ordinal) ||
            xaml.Contains("Margin=\"{Binding NameMargin}\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"PRIORITY\" TextAlignment=\"Center\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"CATEGORY\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"{Binding Header4}\" TextAlignment=\"{Binding Header4Alignment}\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The workstation tables must preserve their checkbox inset, mod-column order, readable Flags width, and centered plugin Mod Index contract.");
        }
    }

    private static void VerifyCanonicalCatalogInspectionContract()
    {
        var options = DeveloperCatalogInspectionFixture.CreateOptions();
        if (options.Length != 2)
            throw new InvalidOperationException("The catalog reviewer fixture must expose valid and structurally-invalid inspection states.");

        var inspector = new CanonicalCatalogInspector();
        var valid = inspector.Inspect(options[0].Package);
        var invalid = inspector.Inspect(options[1].Package);
        const string exactTerminology = "  Café—North  \r\n";
        if (!valid.Summary.IsStructurallyValid || invalid.Summary.IsStructurallyValid ||
            valid.Summary.QcsStatus != CatalogValidationStatus.Rejected ||
            valid.PackageValidation.QcsStatus != CatalogValidationStatus.Rejected)
        {
            throw new InvalidOperationException(
                "Catalog inspection must distinguish structural validity, structural invalidity, and Rejected QCS status.");
        }
        if (!valid.Records.SelectMany(value => value.TerminologyAssertions)
                .Any(value => value.VerbatimValue == exactTerminology) ||
            valid.Records.SelectMany(value => value.TerminologyAssertions)
                .Any(value => value.VerbatimValue == exactTerminology.Trim()))
        {
            throw new InvalidOperationException("The catalog reviewer did not preserve exact canonical terminology.");
        }
        var nameless = valid.Records.Single(value =>
            value.Record.NativeIdentity.ExactRepresentation == "parent");
        if (!nameless.IsTerminologyUnresolved || !nameless.TerminologyAssertions.IsEmpty ||
            nameless.Record.NativeIdentity.ExactRepresentation != "parent")
        {
            throw new InvalidOperationException("A nameless record acquired synthesized terminology instead of its native identity.");
        }
        if (!valid.Evidence.Any(value => value.Verification == EvidenceVerificationKind.FileVerified) ||
            !valid.Evidence.Any(value => value.Verification == EvidenceVerificationKind.ReferenceVerified) ||
            valid.Evidence.Any(value => (value.FileEvidence is null) == (value.ReferenceEvidence is null)) ||
            valid.Evidence.Length != options[0].Package.Payload.FileEvidenceReceipts.Length +
                                     options[0].Package.Payload.ReferenceEvidenceReceipts.Length)
        {
            throw new InvalidOperationException("FILE_VERIFIED and REFERENCE_VERIFIED evidence are not structurally distinct in inspection.");
        }
        var fileTrace = valid.Evidence.First(value => value.FileEvidence is not null && !value.EvidenceBindings.IsEmpty);
        if (fileTrace.SourceRevision.Revision.Id != fileTrace.FileEvidence!.Receipt.SourceRevisionId ||
            fileTrace.AdapterDescriptor.RevisionId != fileTrace.SourceRevision.AdapterRevisionId ||
            !fileTrace.Artifacts.Any(value => value.Id == fileTrace.FileEvidence.Receipt.SourceArtifactId) ||
            fileTrace.FileEvidence.Receipt.ParserId != "grid.conformance.fixture-parser" ||
            fileTrace.EvidenceBindings.Any(value => value.ClaimLocator != fileTrace.FileEvidence.Receipt.SourceFieldPath) ||
            !valid.Evidence.Any(value => !value.UnresolvedAssertionIds.IsEmpty) ||
            !valid.Evidence.Any(value => !value.CorrelationIds.IsEmpty))
        {
            throw new InvalidOperationException("The inspection projection did not preserve the complete provenance drill-down chain.");
        }
        if (!valid.Unresolved.Any(value => value.Kind == CatalogInspectionUnresolvedKind.SourceAssertion) ||
            !valid.Unresolved.Any(value => value.Kind == CatalogInspectionUnresolvedKind.Relationship) ||
            !valid.Unresolved.Any(value => value.Kind == CatalogInspectionUnresolvedKind.MissingTerminology) ||
            !valid.Conflicts.Any(value => value.Kind == CatalogInspectionConflictKind.TerminologyDisagreement) ||
            !valid.Conflicts.Any(value => value.Kind == CatalogInspectionConflictKind.RelationshipDisagreement) ||
            !valid.Conflicts.Any(value => value.Kind == CatalogInspectionConflictKind.AmbiguousCorrelation))
        {
            throw new InvalidOperationException("The catalog reviewer hid unresolved or conflicting source assertions.");
        }
        if (valid.Relationships.Any(value => value.EvidenceBindingIds.IsEmpty) ||
            valid.Relationships.Any(value => value.ClaimContentId != EvidenceClaimContentId.DeriveV1(value.Assertion)))
        {
            throw new InvalidOperationException("Relationship inspection lost exact claim-level evidence identity.");
        }
        if (valid.Summary.PackageId != options[0].Package.Id ||
            valid.Summary.CatalogRevisionId != options[0].Package.Manifest.CatalogRevisionId ||
            !valid.Sources.SelectMany(value => value.Revisions).Select(value => value.Revision.Id)
                .SequenceEqual(options[0].Package.Payload.SourceRevisions.Select(value => value.Revision.Id)))
        {
            throw new InvalidOperationException("Catalog package/source identities changed in the inspection projection.");
        }

        var publicMethods = typeof(CanonicalCatalogInspector).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (publicMethods.Length != 1 || publicMethods[0].Name != nameof(CanonicalCatalogInspector.Inspect) ||
            publicMethods[0].GetParameters().Select(value => value.ParameterType).Any(type =>
                type == typeof(InstallationId) || type == typeof(ProfileId) || type == typeof(ModId) ||
                type.Name.Contains("Account", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The read-only inspector exposes mutation authority or account/profile inputs.");
        }
        foreach (var type in new[]
        {
            typeof(CanonicalCatalogInspectionReport), typeof(CatalogInspectionRecord),
            typeof(CatalogInspectionEvidenceTrace), typeof(CatalogInspectionPackageValidation),
        })
        {
            if (type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(method =>
                    method.Name.StartsWith("Set", StringComparison.Ordinal) ||
                    method.Name.StartsWith("Update", StringComparison.Ordinal) ||
                    method.Name.StartsWith("Delete", StringComparison.Ordinal) ||
                    method.Name.StartsWith("Approve", StringComparison.Ordinal) ||
                    method.Name.StartsWith("Publish", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Inspection type {type.Name} exposes a mutation/publication command.");
        }
        if (typeof(CatalogPackageVerificationResult).GetProperty("IsApproved") is not null ||
            typeof(CatalogPackageVerificationResult).GetProperty("IsPublishable") is not null ||
            typeof(CatalogInspectionPackageValidation).GetProperty("IsApproved") is not null ||
            typeof(CatalogInspectionPackageValidation).GetProperty("IsPublishable") is not null)
        {
            throw new InvalidOperationException("Structural validation accidentally acquired approval/publication semantics.");
        }

        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "CatalogInspectionPage.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "CatalogInspectionPage.xaml.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml"));
        foreach (var required in new[]
        {
            "Header=\"Summary\"", "Header=\"Records\"", "Header=\"Relationships\"", "Header=\"Evidence\"",
            "Header=\"Unresolved\"", "Header=\"Conflicts\"", "Header=\"Sources / Revisions\"",
            "Header=\"Package / Validation\"", "Text=\"{Binding VerbatimValue}\"",
            "Source-native identifier (not a display name)", "Structural validity is not QCS approval",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"The catalog review surface is missing '{required}'.");
        }
        if (!shell.Contains("Text=\"Catalog Review\" Click=\"OnCatalogReviewClicked\"", StringComparison.Ordinal) ||
            xaml.Contains("<Button", StringComparison.Ordinal) ||
            source.Contains("UnresolvedOtherContext", StringComparison.Ordinal) ||
            source.Contains("OtherResolutionRecord", StringComparison.Ordinal) ||
            xaml.Contains("ticket Other", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalog Review lacks its entry point, exposes an action button, or admits ticket Other data.");
        }

        var replay = inspector.Inspect(options[0].Package);
        if (replay.Summary.PackageId != valid.Summary.PackageId ||
            !replay.Records.Select(value => value.Record.Id).SequenceEqual(valid.Records.Select(value => value.Record.Id)) ||
            !replay.Records.SelectMany(value => value.TerminologyAssertions).Select(value => value.VerbatimValue)
                .SequenceEqual(valid.Records.SelectMany(value => value.TerminologyAssertions).Select(value => value.VerbatimValue), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Repeated account-independent inspection changed canonical data.");
        }
    }

    private static void VerifyShellPanelContract()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "App.xaml"));
        var assistant = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml"));
        var workspace = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml"));
        var workspaceSource = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml.cs"));
        var lcdSource = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Controls", "SevenSegmentDisplay.cs"));
        var tabGeometrySource = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Controls", "ShellTabGeometry.cs"));
        var selectionPresentationStart = workspaceSource.IndexOf("private void UpdateSelectionPresentation()", StringComparison.Ordinal);
        var resolvedEnvironmentRefreshStart = workspaceSource.IndexOf("private async Task RefreshResolvedEnvironmentTabsAsync()", StringComparison.Ordinal);
        foreach (var required in new[]
        {
            "x:Name=\"MainMenuBar\"",
            "x:Name=\"CompactMenuButton\"",
              "x:Name=\"GlobalSearchPanel\" Width=\"300\" Height=\"24\"",
              "x:Name=\"GlobalSearchBox\" MinHeight=\"0\" Padding=\"6,0\"",
            "x:Name=\"OpenInButton\"",
            "x:Name=\"OpenInLabel\"",
              "x:Name=\"LeftSidebar\" Grid.Column=\"2\" Margin=\"0,3.25\"",
              "x:Name=\"MainWorkspacePanel\" Grid.Column=\"4\" Margin=\"0,3.25\"",
              "x:Name=\"MainContentPanelOutline\" Grid.Row=\"0\" Grid.RowSpan=\"3\"",
              "x:Name=\"AssistantPanelShell\" Grid.Column=\"6\" AutomationProperties.Name=\"Grid Assistant panel\" Margin=\"0,3.25\"",
              "x:Name=\"BottomPanel\" Grid.Row=\"4\" AutomationProperties.Name=\"Bottom tool panel\"",
              "x:Name=\"EditorMoreActionsButton\"",
              "x:Name=\"ContextBar\" Grid.Row=\"1\" Padding=\"{StaticResource ShellPanelContentInset}\" HorizontalAlignment=\"Right\"",
              "x:Name=\"LeftSplitter\" Grid.Column=\"3\" Style=\"{StaticResource VerticalPanelSplitterStyle}\"",
              "x:Name=\"BottomSplitter\" Grid.Row=\"3\" Style=\"{StaticResource HorizontalPanelSplitterStyle}\"",
              "x:Name=\"AssistantSplitter\" Grid.Column=\"5\" Style=\"{StaticResource VerticalPanelSplitterStyle}\"",
              "AutomationProperties.Name=\"Application menu\"",
            "AutomationProperties.Name=\"Toggle left sidebar\"",
            "AutomationProperties.Name=\"Toggle bottom panel\"",
            "ToolTipService.ToolTip=\"Toggle right panel\"",
            "x:Name=\"LeftPanelToggleFill\"",
            "x:Name=\"BottomPanelToggleFill\"",
            "x:Name=\"AssistantToggleFill\"",
            "Grid.Column=\"3\" Orientation=\"Horizontal\" Margin=\"0,0,4,0\"",
            "Grid.Column=\"1\" Grid.ColumnSpan=\"4\" Orientation=\"Horizontal\" HorizontalAlignment=\"Left\"",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"The canonical shell panel contract is missing '{required}'.");
        }
        if (!source.Contains("e.NewSize.Width <= e.NewSize.Height", StringComparison.Ordinal) ||
            !source.Contains("OpenInLabel.Visibility = compactMenu ? Visibility.Collapsed : Visibility.Visible", StringComparison.Ordinal) ||
              !source.Contains("GlobalSearchPanel.Width = e.NewSize.Width < 820 ? 220 : compactMenu ? 280 : 300", StringComparison.Ordinal) ||
            !source.Contains("AssistantPresentationMode.Solo", StringComparison.Ordinal) ||
            source.Contains("band != ResponsiveLayoutBand.Narrow", StringComparison.Ordinal) ||
            source.Contains("AssistantDrawerLayer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The shell must use a square-width application menu and complete docked-or-solo workbench panels without a drawer.");
        }
        if (!resources.Contains("x:Key=\"ShellTabViewItemStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellInteriorHeaderInset\">3,3,3,0</Thickness>", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Margin\" Value=\"0,0,3,0\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"TabViewHeaderPadding\">0</Thickness>", StringComparison.Ordinal) ||
            !resources.Contains("x:Name=\"TabCard\"", StringComparison.Ordinal) ||
            !resources.Contains("Content=\"{TemplateBinding Header}\"", StringComparison.Ordinal) ||
            !resources.Contains("VerticalAlignment=\"Stretch\"", StringComparison.Ordinal) ||
            !resources.Contains("VerticalContentAlignment=\"Center\"", StringComparison.Ordinal) ||
            !resources.Contains("Margin=\"0,-2,0,2\"", StringComparison.Ordinal) ||
            !resources.Contains("Target=\"TabCard.Background\" Value=\"#2D2D30\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Height\" Value=\"24\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Padding\" Value=\"6,0\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellWorkbenchBrush\" Color=\"#111111\"", StringComparison.Ordinal) ||
            !resources.Contains("FontSize=\"13\"", StringComparison.Ordinal) ||
            !resources.Contains("BorderThickness=\"0\"", StringComparison.Ordinal) ||
            xaml.Contains("Margin=\"-4,0,33,0\"", StringComparison.Ordinal) ||
            !xaml.Contains("Margin=\"0,0,34,0\"", StringComparison.Ordinal) ||
            !xaml.Contains("Margin=\"0,4,4,2\"", StringComparison.Ordinal) ||
            !xaml.Contains("AutomationProperties.Name=\"Console panel header row\"", StringComparison.Ordinal) ||
            !xaml.Contains("AutomationProperties.Name=\"Maximized Console panel header row\"", StringComparison.Ordinal) ||
            !assistant.Contains("AutomationProperties.Name=\"Chat panel header row\"", StringComparison.Ordinal) ||
            assistant.Contains("Padding=\"14,3\"", StringComparison.Ordinal) ||
            !tabGeometrySource.Contains("new(2, 4, 4, 2)", StringComparison.Ordinal) ||
            !tabGeometrySource.Contains("tabList.Padding = new Thickness(0);", StringComparison.Ordinal) ||
            !tabGeometrySource.Contains("ScrollDecreaseButtonContainer", StringComparison.Ordinal) ||
            !tabGeometrySource.Contains("scrollViewer.HorizontalOffset <= 0.5", StringComparison.Ordinal) ||
            !workspace.Contains("Style=\"{StaticResource ShellTabViewItemStyle}\"", StringComparison.Ordinal) ||
            !workspace.Contains("TabWidthMode=\"SizeToContent\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"ModWindow\"", StringComparison.Ordinal) ||
            !workspace.Contains("Click=\"OnModEnabledCheckBoxClicked\"", StringComparison.Ordinal) ||
            !workspace.Contains("<ListView.ItemContainerTransitions>", StringComparison.Ordinal) ||
            !workspace.Contains("<TransitionCollection />", StringComparison.Ordinal) ||
            workspace.Contains("IsChecked=\"{Binding IsEnabled}\" IsHitTestVisible=\"False\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentWindow\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentWindowOutline\"", StringComparison.Ordinal) ||
            workspace.Contains("Margin=\"0,30,0,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("AutomationProperties.Name=\"Plugin pane outline\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentTabHost\" Background=\"Transparent\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellPanelContentInset\">5</Thickness>", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableHeaderTextStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableSectionButtonStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableHeaderGridLineBrush\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableBodyGridLineBrush\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"TextAlignment\" Value=\"Left\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"VerticalAlignment\" Value=\"Center\"", StringComparison.Ordinal) ||
            !workspace.Contains("AutomationProperties.Name=\"Workstation layout root\"", StringComparison.Ordinal) ||
            !workspace.Contains("AutomationProperties.Name=\"Workstation content\"", StringComparison.Ordinal) ||
            workspace.Split("<ColumnDefinition Width=\"5\" />", StringSplitOptions.None).Length < 3 ||
            workspace.Contains("Margin=\"-9,0,0,0\"", StringComparison.Ordinal) ||
            workspace.Contains("Margin=\"9,0,0,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("Style=\"{StaticResource ShellTableSectionButtonStyle}\"", StringComparison.Ordinal) ||
            !workspace.Contains("Background=\"{ThemeResource ShellWorkbenchBrush}\"", StringComparison.Ordinal) ||
            !workspace.Contains("<Grid MinHeight=\"28\">", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding Priority}\" TextAlignment=\"{Binding PriorityAlignment}\"", StringComparison.Ordinal) ||
            !workspace.Contains("TextAlignment=\"{Binding Column3Alignment}\"", StringComparison.Ordinal) ||
            !workspaceSource.Contains("private const string NullCellValue = \"—\";", StringComparison.Ordinal) ||
            !workspaceSource.Contains("return TextAlignment.Center;", StringComparison.Ordinal) ||
            !workspaceSource.Contains("CenterColumn2 ? TextAlignment.Center", StringComparison.Ordinal) ||
            !workspaceSource.Contains("preserveWorkstationPosition: true", StringComparison.Ordinal) ||
            !workspaceSource.Contains("CaptureScrollPosition(ModsList)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("RestoreScrollPosition(ModsList, modScrollPosition)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ObservableCollection<ModRowViewModel> _modRows", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ReconcileModRows(rows)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("SynchronizePresentation(ModRowViewModel source)", StringComparison.Ordinal) ||
            selectionPresentationStart < 0 ||
            resolvedEnvironmentRefreshStart <= selectionPresentationStart ||
            workspaceSource[selectionPresentationStart..resolvedEnvironmentRefreshStart].Contains("RefreshEnvironmentTabs();", StringComparison.Ordinal) ||
            !source.Contains("UpdateLayoutToggleGlyphs()", StringComparison.Ordinal) ||
            !source.Contains("Application.Current.Resources[\"ShellSuccessBrush\"]", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding FlagsLabel}\" TextAlignment=\"Center\"", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding Header2}\" TextAlignment=\"{Binding Header2Alignment}\"", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ResolveCellAlignment", StringComparison.Ordinal) ||
            !source.Contains("MainWorkspacePanel.SizeChanged += OnMainWorkspacePanelSizeChanged", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ContextBar\"", StringComparison.Ordinal) ||
            !xaml.Contains("BorderThickness=\"0\" Visibility=\"Collapsed\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellSearchTextBoxStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"MinHeight\" Value=\"0\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Padding\" Value=\"6,0\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"VerticalContentAlignment\" Value=\"Center\"", StringComparison.Ordinal) ||
            !xaml.Contains("Background=\"{StaticResource ShellSurfaceBrush}\"", StringComparison.Ordinal) ||
            !xaml.Contains("TextBoxStyle=\"{StaticResource ShellSearchTextBoxStyle}\"", StringComparison.Ordinal) ||
            xaml.Contains("QueryIcon=\"Find\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"LaunchTargetFlyout\" Placement=\"BottomEdgeAlignedLeft\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"RefreshEnvironmentButton\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"LaunchTargetSelector\" Grid.Column=\"1\"", StringComparison.Ordinal) ||
            !source.Contains("await workspace.RefreshEnvironmentAsync();", StringComparison.Ordinal) ||
            !workspaceSource.Contains("public async Task RefreshEnvironmentAsync()", StringComparison.Ordinal) ||
            workspace.Split("Grid.Column=\"1\" Width=\"280\" Height=\"24\" HorizontalAlignment=\"Right\"", StringSplitOptions.None).Length < 3 ||
            workspace.Split("MaxWidth=\"280\"", StringSplitOptions.None).Length < 3 ||
            !workspace.Contains("PlaceholderText=\"Search mods\"", StringComparison.Ordinal) ||
            !workspace.Contains("PlaceholderText=\"Search plugins\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"ActiveModCountDisplay\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"ActivePluginCountDisplay\"", StringComparison.Ordinal) ||
            !workspace.Contains("DigitCount=\"5\"", StringComparison.Ordinal) ||
            !workspace.Contains("DigitCount=\"4\"", StringComparison.Ordinal) ||
            !workspace.Contains("DigitWidth=\"7.5\"", StringComparison.Ordinal) ||
            !workspace.Contains("DigitHeight=\"15\"", StringComparison.Ordinal) ||
            !workspace.Contains("DigitSpacing=\"3\"", StringComparison.Ordinal) ||
            workspace.Contains("Text=\"Active mods\"", StringComparison.Ordinal) ||
            workspace.Contains("Text=\"Active plugins\"", StringComparison.Ordinal) ||
            !lcdSource.Contains("nameof(DigitCount)", StringComparison.Ordinal) ||
            !lcdSource.Contains("nameof(DigitSpacing)", StringComparison.Ordinal) ||
            !lcdSource.Contains("numericText.PadLeft(capacity, '0')", StringComparison.Ordinal) ||
            !workspaceSource.Contains("private const double PageHorizontalReservedSpace = 10;", StringComparison.Ordinal) ||
            !xaml.Contains("<ColumnDefinition Width=\"8\" />", StringComparison.Ordinal) ||
            !source.Contains("ApplyWorkstationFramePlacement", StringComparison.Ordinal) ||
            !workspaceSource.Contains("SupportsElevatedTopBaseline", StringComparison.Ordinal) ||
            !workspaceSource.Contains("new Thickness(0, 33, 0, 0)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ApplyCompactFilterWidths", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ShellTabGeometry.ApplyInteriorHeader(EnvironmentTabs, \"Plugin panel tab strip\")", StringComparison.Ordinal) ||
            workspaceSource.Contains("new Thickness(-8, 0, 8, 0)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("mod.Kind == ModEntryKind.Mod && mod.IsEnabled", StringComparison.Ordinal) ||
            !workspaceSource.Contains("profile?.Plugins.Count(plugin => plugin.IsEnabled)", StringComparison.Ordinal) ||
            !workspace.Contains("<AutoSuggestBox x:Name=\"ModSearchBox\"", StringComparison.Ordinal) ||
            !workspace.Contains("<AutoSuggestBox x:Name=\"EnvironmentSearchBox\"", StringComparison.Ordinal) ||
            workspace.Split("TextBoxStyle=\"{StaticResource ShellSearchTextBoxStyle}\"", StringSplitOptions.None).Length < 3 ||
            !workspaceSource.Contains("EnvironmentPane.Margin = elevatedTopBaseline", StringComparison.Ordinal) ||
            !source.Contains("ToolTargetPresentation.ForManagement()", StringComparison.Ordinal) ||
            !source.Contains("DispatcherQueue.TryEnqueue(() =>", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"SnapshotOpenWorkspaceButton\"", StringComparison.Ordinal) ||
            !xaml.Contains("Click=\"OnOpenSnapshotWorkspaceClicked\"", StringComparison.Ordinal) ||
            !source.Contains("OpenGameSnapshot(id);", StringComparison.Ordinal) ||
            !source.Contains("home.BindContext(session.Catalog, composition.IsDemo, OpenGameWorkspace", StringComparison.Ordinal) ||
            !source.Contains("private void OpenGameWorkspace(GameId id)", StringComparison.Ordinal) ||
            !source.Contains("private void OpenGameSnapshot(GameId id)", StringComparison.Ordinal) ||
            source.Contains("ManageGameButton", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shell tabs must float on the canonical 3 DIP inset, and Manage must be the first launch-selector action.");
        }
    }

    private static void VerifyStartupAuthGateContract()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var authService = File.ReadAllText(Path.Combine(root, "core", "features", "users", "auth", "GridAuthService.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "core", "features", "users", "auth", "ui", "ShellAuthController.cs"));

        foreach (var required in new[]
        {
            "x:Name=\"AuthResolvingPanel\"",
            "AutomationProperties.Name=\"Resolving GRID authentication\"",
            "Text=\"Restoring your session…\"",
            "x:Name=\"SignInPanel\"",
            "Visibility=\"Collapsed\"",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"The startup auth gate is missing '{required}'.");
        }

        if (!source.Contains("ShowAuthResolvingShell();", StringComparison.Ordinal) ||
            !source.Contains("snapshot.Phase == AuthPhase.SessionRestore", StringComparison.Ordinal) ||
            !source.Contains("AuthResolvingPanel.Visibility = Visibility.Collapsed", StringComparison.Ordinal) ||
            !authService.Contains("BeginSessionRestore();", StringComparison.Ordinal) ||
            !authService.Contains("Phase = AuthPhase.SessionRestore", StringComparison.Ordinal) ||
            !controller.Contains("_service.BeginSessionRestore();", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Startup must remain on the neutral auth-resolving surface until persisted session restoration reaches a terminal state.");
        }
    }

    private static void VerifyUserToolManagerContract()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var dialog = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "ToolConfigurationDialog.xaml"));
        var dialogSource = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "ToolConfigurationDialog.xaml.cs"));
        var launch = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "WindowsUserToolLaunchService.cs"));
        var assistant = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "AssistantToolCatalogLoader.cs"));

        foreach (var required in new[]
        {
            "Content=\"Add from file\"",
            "Content=\"Add empty\"",
            "AutomationProperties.Name=\"Tool title\"",
            "AutomationProperties.Name=\"Tool binary\"",
            "AutomationProperties.Name=\"Tool working directory\"",
            "AutomationProperties.Name=\"Tool arguments\"",
            "Content=\"Apply\"",
            "Content=\"OK\"",
            "Content=\"Cancel\"",
        })
            if (!dialog.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"Tool Manager is missing '{required}'.");

        if (!main.Contains("session.UserTools.ForContext(context)", StringComparison.Ordinal) ||
            !main.Contains("await session.UserToolLaunch.LaunchAsync(configuration, context", StringComparison.Ordinal) ||
            main.Contains("Launching is not implemented in this shell stage", StringComparison.Ordinal) ||
            !dialog.Contains("Content=\"This game installation\"", StringComparison.Ordinal) && !dialogSource.Contains("ScopeSelector.Items.Add(\"This game installation\")", StringComparison.Ordinal) ||
            !dialogSource.Contains("new(currentContext.GameId, currentContext.InstallationId, null)", StringComparison.Ordinal) ||
            !dialogSource.Contains("state.ForContext(currentContext, includeDisabled: true)", StringComparison.Ordinal) ||
            !dialogSource.Contains("This does not prevent manual launching", StringComparison.Ordinal) ||
            !dialogSource.Contains("Compatibility: unresolved", StringComparison.Ordinal) ||
            !launch.Contains("UseShellExecute = true", StringComparison.Ordinal) ||
            !launch.Contains("start.ArgumentList.Add(argument)", StringComparison.Ordinal) ||
            assistant.Contains("UserToolLaunchConfiguration", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Manual launch configurations must remain workspace-scoped, directly runnable, and separate from canonical investigation-tool admission.");
        }
    }

    private static async Task VerifyInstalledToolIdentityServiceAsync()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "scripts", "health", "Get-GridInstalledToolIdentity.ps1");
        var executable = Path.Combine(AppContext.BaseDirectory, "Grid.App.UiTests.exe");
        if (!File.Exists(executable)) executable = Environment.ProcessPath ?? throw new InvalidOperationException("No exact executable was available for the identity fixture.");
        var service = new PowerShellInstalledToolIdentityService(script);
        var result = await service.ResolveAsync(executable);
        if (!StringComparer.OrdinalIgnoreCase.Equals(result.BinaryPath, Path.GetFullPath(executable)) ||
            result.Sha256.Length != 64 || result.Evidence.Length < 5 || result.CanonicalToolId is not null || !result.Compatibility.IsEmpty)
        {
            throw new InvalidOperationException("Exact-file identity observation must preserve provenance without inventing ToolID or GameID compatibility.");
        }
    }

    private static async Task VerifyWindowsUserToolLaunchServiceAsync()
    {
        var binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var scope = new UserToolScope(new("game.fixture"), new("installation.fixture"), new("profile.fixture"));
        var configuration = new UserToolLaunchConfiguration(
            UserToolLaunchConfiguration.CurrentSchemaVersion,
            new("user-tool.launch-fixture"),
            "Launch fixture",
            binary,
            Path.GetDirectoryName(binary),
            ["-NoProfile", "-Command", "exit 0"],
            true,
            scope,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1);
        var service = new WindowsUserToolLaunchService();
        var result = await service.LaunchAsync(configuration, new(configuration.Id, scope, configuration.Fingerprint));
        if (!result.Succeeded || result.ProcessId is null)
            throw new InvalidOperationException($"Direct user-tool launch fixture failed: {result.Detail}");
    }

    private static void VerifyAuthenticatedAvatarContract()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "core", "features", "users", "auth", "ui", "ShellAuthController.cs"));
        var sessionUser = File.ReadAllText(Path.Combine(root, "core", "features", "users", "auth", "SessionUser.cs"));
        var authApi = File.ReadAllText(Path.Combine(root, "core", "api", "Auth", "GridAuthHttpClient.cs"));

        foreach (var required in new[]
        {
            "x:Name=\"AccountArea\"",
            "x:Name=\"AccountAvatar\"",
            "<PersonPicture",
            "AutomationProperties.Name=\"Authenticated account avatar\"",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"The authenticated account area is missing '{required}'.");
        }

        if (!source.Contains("BindAuthenticatedUser(snapshot.User!, avatar);", StringComparison.Ordinal) ||
            !source.Contains("AccountAvatar.ProfilePicture = avatar;", StringComparison.Ordinal) ||
            !source.Contains("AccountAvatar.ProfilePicture = null;", StringComparison.Ordinal) ||
            !source.Contains("AccountArea.Visibility = Visibility.Collapsed;", StringComparison.Ordinal) ||
            !controller.Contains("LoadAvatarAsync(snapshot.User?.Picture)", StringComparison.Ordinal) ||
            !sessionUser.Contains("public string? Picture { get; init; }", StringComparison.Ordinal) ||
            !authApi.Contains("u.Deserialize<Grid.Auth.Features.SessionUser>(_json)", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The account thumbnail must project /api/me user.picture and clear it at every unauthenticated boundary.");
        }
    }

    private static void VerifyAddProfileResolutionContract()
    {
        var single = AddProfileReviewState<string>.FromChoices(["Only"]);
        if (!single.IsVisible || single.RequiresSelection || !single.CanConnect || single.Selected != "Only")
            throw new InvalidOperationException("A single resolved profile did not advance directly to review.");

        var multiple = AddProfileReviewState<string>.FromChoices(["First", "Second"]);
        if (!multiple.IsVisible || !multiple.RequiresSelection || multiple.CanConnect || multiple.Selected is not null)
            throw new InvalidOperationException("Multiple resolved profiles were not gated behind an explicit selection.");
        multiple = multiple.Select("Second");
        if (!multiple.CanConnect || multiple.Selected != "Second")
            throw new InvalidOperationException("An explicit resolved-profile selection did not enable Connect.");
        try
        {
            _ = multiple.Select("Unresolved");
            throw new InvalidOperationException("Review state accepted a profile outside the resolved choices.");
        }
        catch (ArgumentException)
        {
            // Expected: the review cannot select evidence the resolver did not return.
        }
        multiple = multiple.Clear();
        if (multiple.IsVisible || multiple.CanConnect || !multiple.Choices.IsEmpty || multiple.Selected is not null)
            throw new InvalidOperationException("Browse again did not clear stale Add Profile review state.");

        var root = FindRepositoryRoot();
        var resolver = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "GridProfileEnvironmentResolver.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AddProfilePage.xaml"));
        var page = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AddProfilePage.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var composition = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Composition", "GridCompositionRoot.cs"));
        var addGameDialog = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameRegistrationDialog.xaml.cs"));
        var addGameMarkup = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameRegistrationDialog.xaml"));
        var discoveryAdapter = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "Mo2ExistingProfileDiscoveryAdapter.cs"));

        foreach (var required in new[]
        {
            "AutomationProperties.Name=\"Resolved profile selection\"",
            "DisplayMemberPath=\"ProfileName\"",
            "x:Name=\"ApplicationText\"",
            "x:Name=\"EnvironmentText\"",
            "x:Name=\"ModsText\"",
            "x:Name=\"PluginsText\"",
            "x:Name=\"ConfigurationText\"",
            "x:Name=\"ObservationText\"",
            "x:Name=\"WarningsList\"",
            "x:Name=\"ConnectButton\" IsEnabled=\"False\"",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"Add Profile review is missing '{required}'.");
        }

        if (!resolver.Contains("Mo2ProfileEnvironmentResolutionService mo2Resolver", StringComparison.Ordinal) ||
            !resolver.Contains("catalog.Games", StringComparison.Ordinal) ||
            !resolver.Contains("active |", StringComparison.Ordinal) ||
            !resolver.Contains("implicit |", StringComparison.Ordinal) ||
            !resolver.Contains("observed total", StringComparison.Ordinal) ||
            resolver.Contains("FindMo2InstanceDirectory", StringComparison.Ordinal) ||
            !composition.Contains("new Mo2ProfileEnvironmentResolutionService(", StringComparison.Ordinal) ||
            !page.Contains("ProfileSelector.SelectedItem is ResolvedProfileEnvironment", StringComparison.Ordinal) ||
            !page.Contains("reviewState = AddProfileReviewState<ResolvedProfileEnvironment>.FromChoices", StringComparison.Ordinal) ||
            !page.Contains("ProfileSelectionPanel.Visibility = reviewState.RequiresSelection", StringComparison.Ordinal) ||
            !page.Contains("ClearResolution();", StringComparison.Ordinal) ||
            !page.Contains("ProfileText.Text = string.Empty", StringComparison.Ordinal) ||
            !page.Contains("EvidenceText.Text = string.Empty", StringComparison.Ordinal) ||
            !page.Contains("ConnectButton.IsEnabled = !value && reviewState.CanConnect", StringComparison.Ordinal) ||
            !window.Contains("ConnectResolvedProfileAsync", StringComparison.Ordinal) ||
            !window.Contains("Mo2OnboardingCoordinator.ConnectAsync", StringComparison.Ordinal) ||
            !composition.Contains("GridExistingProfileDiscoveryService", StringComparison.Ordinal) ||
            !addGameMarkup.Contains("PrimaryButtonText=\"Create new profile\"", StringComparison.Ordinal) ||
            !addGameMarkup.Contains("AutomationProperties.Name=\"Existing profiles for selected game installation\"", StringComparison.Ordinal) ||
            !addGameDialog.Contains("UseExistingGameProfileDecision", StringComparison.Ordinal) ||
            !addGameDialog.Contains("CreateNewGameProfileDecision", StringComparison.Ordinal) ||
            addGameDialog.Contains("IGameInstallationRegistrationStore", StringComparison.Ordinal) ||
            !discoveryAdapter.Contains("profile.GameId == target.GameId", StringComparison.Ordinal) ||
            !discoveryAdapter.Contains("paths.Equals(profile.GameDirectory, target.InstallationRoot)", StringComparison.Ordinal) ||
            !window.Contains("composition.GameRegistrationStore.RegisterAsync", StringComparison.Ordinal) ||
            !window.Contains("ConnectResolvedProfileAsync(useExisting.Profile", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Add Profile and Add Game profile discovery must delegate interpretation to manager adapters and preserve explicit connection write boundaries.");
        }
    }

    private static void VerifyCanonicalRuntimeSelectorContract()
    {
        var root = FindRepositoryRoot();
        var runtime = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "CanonicalCatalogRuntimeService.cs"));
        var runtimeBinding = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Services", "CanonicalCatalogRuntimeBindingStore.cs"));
        var composition = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Composition", "GridCompositionRoot.cs"));
        var assistantPanel = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        if (!runtime.Contains("JsonCanonicalKnowledgeCatalogStore", StringComparison.Ordinal) ||
            !runtime.Contains("CanonicalSelectorProjectionPolicyResolver.ResolveForCatalogPackage", StringComparison.Ordinal) ||
            !runtime.Contains("CanonicalSelectorProjectionEngine.CreateVerifiedInput(", StringComparison.Ordinal) ||
            !runtime.Contains("CanonicalSelectorProjectionEngine.Query", StringComparison.Ordinal) ||
            !runtime.Contains("CanonicalTerminologyLocalePreference", StringComparison.Ordinal) ||
            runtime.Contains("IGameKnowledgeAdapter", StringComparison.Ordinal) ||
            runtime.Contains("ImportPackageAsync", StringComparison.Ordinal) ||
            runtime.Contains("HttpClient", StringComparison.Ordinal) ||
            !composition.Contains("GRID_CANONICAL_CATALOG_PATH", StringComparison.Ordinal) ||
            !composition.Contains("GRID_CANONICAL_ALLOW_CANDIDATE", StringComparison.Ordinal) ||
            !composition.Contains("GRID_CANONICAL_PACKAGE_ID", StringComparison.Ordinal) ||
            !composition.Contains("CultureInfo.CurrentUICulture", StringComparison.Ordinal) ||
            !runtime.Contains("PinnedPackageId", StringComparison.Ordinal) ||
            !runtime.Contains("SharedLibraryRevision", StringComparison.Ordinal) ||
            !runtime.Contains("ValidateSelection", StringComparison.Ordinal) ||
            !runtime.Contains("currentExactMatches", StringComparison.Ordinal) ||
            !runtime.Contains("var loadedCatalog = await", StringComparison.Ordinal) ||
            !runtime.Contains("runtimeCatalog = observedCatalog;", StringComparison.Ordinal) ||
            !runtime.Contains("catalog = loadedCatalog;", StringComparison.Ordinal) ||
            !runtime.Contains("became stale during catalog reload", StringComparison.Ordinal) ||
            !assistantPanel.Contains("canonicalCatalogRuntimeService.ValidateSelection(canonicalRuntimeMatch, selection)", StringComparison.Ordinal) ||
            !assistantPanel.Contains("PruneInvalidCanonicalSelections", StringComparison.Ordinal) ||
            !assistantPanel.Contains("{match.State} ({status})", StringComparison.Ordinal) ||
            !mainWindow.Contains("ReplaceObservedCatalogAsync", StringComparison.Ordinal) ||
            mainWindow.Contains("session.Shell.ReplaceCatalog(catalog);", StringComparison.Ordinal) ||
            mainWindow.Contains("refreshSession.Shell.ReplaceCatalog(catalog);", StringComparison.Ordinal) ||
            !composition.Contains("CanonicalCatalogRuntimeBindingStore", StringComparison.Ordinal) ||
            !runtimeBinding.Contains("shared-canonical-library.v5.json", StringComparison.Ordinal) ||
            runtimeBinding.Contains("WriteAllText", StringComparison.Ordinal) ||
            runtimeBinding.Contains("ImportPackageAsync", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Runtime selectors must consume the shared persisted verified catalog and may not extract, import, or discover knowledge over the web.");
        }

        var method = typeof(CanonicalCatalogRuntimeService).GetMethod(
            "TryReadSteamManifest", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The bounded Steam build observer was not found.");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"grid-runtime-match-{Guid.NewGuid():N}");
        var install = Path.Combine(fixtureRoot, "steamapps", "common", "Fixture Game");
        var manifest = Path.Combine(fixtureRoot, "steamapps", "appmanifest_3240220.acf");
        Directory.CreateDirectory(install);
        try
        {
            const string valid = "\"AppState\"\n{\n  \"appid\" \"3240220\"\n  \"buildid\" \"25261616\"\n  \"installdir\" \"Fixture Game\"\n}\n";
            File.WriteAllText(manifest, valid,
                new UTF8Encoding(false));
            var observed = method.Invoke(null, [install, "3240220"]);
            if (!string.Equals((string?)observed?.GetType().GetProperty("ApplicationId")?.GetValue(observed), "3240220", StringComparison.Ordinal) ||
                !string.Equals((string?)observed?.GetType().GetProperty("BuildId")?.GetValue(observed), "25261616", StringComparison.Ordinal) ||
                !string.Equals((string?)observed?.GetType().GetProperty("InstallDirectory")?.GetValue(observed), "Fixture Game", StringComparison.Ordinal))
                throw new InvalidOperationException("The runtime matcher did not retain the complete exact local provider manifest coordinate.");

            foreach (var invalid in new[]
            {
                valid.Replace("  \"appid\" \"3240220\"\n", string.Empty, StringComparison.Ordinal),
                valid.Replace("\"appid\" \"3240220\"", "\"appid\" \"999999\"", StringComparison.Ordinal),
                valid.Replace("  \"appid\" \"3240220\"\n", "  \"appid\" \"3240220\"\n  \"appid\" \"3240220\"\n", StringComparison.Ordinal),
                valid.Replace("  \"installdir\" \"Fixture Game\"\n", string.Empty, StringComparison.Ordinal),
                valid.Replace("\"installdir\" \"Fixture Game\"", "\"installdir\" \"Different Game\"", StringComparison.Ordinal),
                valid.Replace("  \"installdir\" \"Fixture Game\"\n", "  \"installdir\" \"Fixture Game\"\n  \"installdir\" \"Fixture Game\"\n", StringComparison.Ordinal),
                valid.Replace("  \"buildid\" \"25261616\"\n", "  \"buildid\" \"25261616\"\n  \"buildid\" \"25261616\"\n", StringComparison.Ordinal),
            })
            {
                File.WriteAllText(manifest, invalid, new UTF8Encoding(false));
                if (method.Invoke(null, [install, "3240220"]) is not null)
                    throw new InvalidOperationException("Incomplete, mismatched, or duplicate Steam manifest evidence did not fail closed.");
            }
            File.WriteAllBytes(manifest, [0xff, 0xfe, 0xfd]);
            if (method.Invoke(null, [install, "3240220"]) is not null)
                throw new InvalidOperationException("Malformed UTF-8 Steam manifest evidence did not fail closed.");
        }
        finally
        {
            Directory.Delete(fixtureRoot, true);
        }
    }

    private static void VerifyInvestigationActionMarkup()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml.cs"));
        var selectorControl = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Controls", "CanonicalSelectorNavigationControl.cs"));
        foreach (var chatContract in new[]
        {
            "AutomationProperties.Name=\"Grid message composer\"",
            "CornerRadius=\"20\"",
            "PlaceholderText=\"Message Grid\"",
            "AutomationProperties.Name=\"Conversation messages\"",
            "Header=\"Details and evidence\"",
        })
        {
            if (!xaml.Contains(chatContract, StringComparison.Ordinal))
                throw new InvalidOperationException($"Grid chat markup is missing its Codex-style contract: {chatContract}.");
        }
        if (!source.Contains("CreateTranscriptMessage", StringComparison.Ordinal) ||
            !source.Contains("HorizontalAlignment = HorizontalAlignment.Right", StringComparison.Ordinal) ||
            !source.Contains("entry.Kind == AssistantTranscriptKind.Progress", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Grid chat must render right-aligned user bubbles and reduce repeated progress receipts to one live message.");
        }
        if (!source.Contains("state.SetComposerText(ComposerText.Text)", StringComparison.Ordinal) ||
            source.Contains("state.SetProblem(ComposerText.Text)", StringComparison.Ordinal) ||
            !source.Contains("ComposerText.Text = snapshot.Surface == AssistantSurface.Task ? string.Empty : snapshot.ComposerText", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Composer text must remain independent from the legacy Problem compatibility field.");
        }
        foreach (var selector in new[]
        {
            "GameSelector", "ProfileSelector", "ClassSelector", "ProblemSelector", "TimingSelector",
            "ToolsButton", "ModsButton", "LocationSelector", "MissionSelector", "ItemSelector", "EntitySelector", "GoalSelector",
        })
        {
            if (!xaml.Contains($"x:Name=\"{selector}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Canonical Ticket DIF is missing selector '{selector}'.");
        }
        if (!xaml.Contains("controls:CanonicalSelectorNavigationControl x:Name=\"LocationSelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("controls:CanonicalSelectorNavigationControl x:Name=\"MissionSelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("controls:CanonicalSelectorNavigationControl x:Name=\"ItemSelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("controls:CanonicalSelectorNavigationControl x:Name=\"EntitySelector\"", StringComparison.Ordinal) ||
             !selectorControl.Contains("CanonicalSelectorControlContract.V1.ClosedHeightDip", StringComparison.Ordinal) ||
            !selectorControl.Contains("\\u00B7", StringComparison.Ordinal) ||
            !selectorControl.Contains("selected.CatalogRevisionId.Value", StringComparison.Ordinal) ||
            !selectorControl.Contains("Placement = FlyoutPlacementMode.Bottom", StringComparison.Ordinal) ||
            !selectorControl.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", StringComparison.Ordinal) ||
            !selectorControl.Contains("currentPath = node.PathId", StringComparison.Ordinal) ||
            !selectorControl.Contains("OtherRequested", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"LocationOtherText\"", StringComparison.Ordinal) ||
            !xaml.Contains("Height=\"28\" Width=\"84\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "All four canonical selectors must use the shared attached, replace-level, immediate-child control with root-only 28x84 DIP Other context.");
        }
        foreach (var removed in new[]
        {
            "CapabilitySelector", "ProblemText", "ExpectedBehaviorText", "ReproductionLocationText", "DesiredOutcomeText",
            "AttachEvidenceIntakeButton", "Starting the investigation grants no access.",
        })
        {
            if (xaml.Contains(removed, StringComparison.Ordinal))
                throw new InvalidOperationException($"Canonical Ticket DIF retained legacy visible intake '{removed}'.");
        }
        if (xaml.Contains("x:Name=\"InstallationSelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"GameSelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ProfileSelector\"", StringComparison.Ordinal) ||
            source.Contains("OnInstallationSelectionChanged", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "New Investigation must derive its hidden installation identity from the selected connected profile.");
        }
        if (!xaml.Contains("x:Name=\"ClassificationRow\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ReferenceContextRow\"", StringComparison.Ordinal) ||
            !source.Contains("ApplyTicketSelectorLayout", StringComparison.Ordinal) ||
            !source.Contains("SetConfiguredToolSelected", StringComparison.Ordinal) ||
            !source.Contains("snapshot.TicketPreview", StringComparison.Ordinal) ||
            !xaml.Contains("AutomationProperties.Name=\"Authorization information\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Canonical Ticket DIF must preserve five semantic groups, responsive wrapping, configured-tool context, deterministic preview, and compact authorization information.");
        }
        var actions = new (string Name, string Handler)[]
        {
            ("Capture Current State", "OnCaptureCurrentStateClicked"),
            ("Continue live profile repair", "OnRefreshRecoverySourcesClicked"),
            ("Diagnose", "OnDiagnoseClicked"),
            ("Attach Evidence", "OnAttachEvidenceClicked"),
            ("Review Evidence", "OnReviewEvidenceClicked"),
            ("Review Repair", "OnReviewRepairClicked"),
            ("Copy complete recovery action list", "OnCopyRecoveryActionManifestClicked"),
            ("Download available exact recovery sources", "OnDownloadAvailableSourcesClicked"),
            ("Apply Repair", "OnApplyRepairClicked"),
            ("Roll Back", "OnRollBackClicked"),
        };
        foreach (var action in actions)
        {
            if (!xaml.Contains($"AutomationProperties.Name=\"{action.Name}\"", StringComparison.Ordinal) ||
                !xaml.Contains($"Click=\"{action.Handler}\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Investigation task markup does not expose a wired '{action.Name}' action.");
            }
        }

        foreach (var resultSurface in new[] { "Complete recovery human action manifest", "Archive candidate evidence", "Diagnostic result", "Capability Required result" })
        {
            if (!xaml.Contains($"AutomationProperties.Name=\"{resultSurface}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Investigation task markup does not expose the '{resultSurface}' surface.");
        }

        foreach (var label in new[]
        {
            "Affected mod(s):",
            "Mod role(s):",
            "Finding:",
            "Solution:",
            "Confidence:",
            "Evidence:",
        })
        {
            if (!xaml.Contains($"Text=\"{label}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Diagnostic result markup does not expose the '{label}' field.");
        }
    }

    private static void VerifyAssistantToolCatalogContract()
    {
        var loader = typeof(GridCompositionRoot).Assembly.GetType("Grid.App.Services.AssistantToolCatalogLoader", throwOnError: true)!;
        var load = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Assistant tool catalog loader is missing its deterministic Load boundary.");
        var tools = (ImmutableArray<AssistantToolOption>)(load.Invoke(null, null)
            ?? throw new InvalidOperationException("Assistant tool catalog loader returned no catalog."));

        if (tools.Length != 10 ||
            tools.Any(tool => tool.Id.Value == "grid.tool.sseedit") ||
            tools.Any(tool => tool.Id.Value.Contains("cherax", StringComparison.OrdinalIgnoreCase)) ||
            tools.Any(tool => tool.GameCompatibility is not [{ GameId.Value: "game.skyrim-special-edition" } compatibility] ||
                compatibility.EvidenceCapabilityIds.IsDefaultOrEmpty ||
                string.IsNullOrWhiteSpace(compatibility.Provenance)))
        {
            throw new InvalidOperationException(
                "Assistant tools must load only registered read-only collectors with exact canonical GameID compatibility evidence; unproven tools remain hidden.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Grid.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the Grid repository root.");
    }

    private static void VerifyPurePolicies()
    {
        if (Grid.App.Composition.GridLaunchOptions.Parse(null) != Grid.App.Composition.GridApplicationMode.Production ||
            Grid.App.Composition.GridLaunchOptions.Parse("--demo", [], "demo") != Grid.App.Composition.GridApplicationMode.Demo ||
            Grid.App.Composition.GridLaunchOptions.Parse(string.Empty, ["--demo"], "demo") != Grid.App.Composition.GridApplicationMode.Demo ||
            Grid.App.Composition.GridLaunchOptions.Parse("--DEMO") != Grid.App.Composition.GridApplicationMode.Production)
        {
            throw new InvalidOperationException("Application mode parsing is not explicit and deterministic.");
        }

        double[] widths = [1920, 1536, 1280, 960, 1600, 1366];
        foreach (var width in widths)
        {
            foreach (var requested in new[] { 320d, 400d, 560d })
            {
                var decision = Grid.App.Services.ResponsiveLayoutPolicy.Evaluate(width - 64, true, requested);
                var reserved = decision.AssistantPresentation == Grid.App.Services.AssistantPresentationMode.Docked
                    ? decision.AssistantWidth + Grid.App.Services.ResponsiveLayoutPolicy.PaneDividerThickness : 0;
                if (decision.AssistantWidth < 0 || reserved > decision.AvailableWidth || decision.WorkspaceWidth < 0)
                {
                    throw new InvalidOperationException($"Responsive allocation exceeded the {width} DIP budget.");
                }
                var shouldDock = width - 64 >= Grid.App.Services.ResponsiveLayoutPolicy.AssistantDockThreshold;
                if (shouldDock != (decision.AssistantPresentation == Grid.App.Services.AssistantPresentationMode.Docked))
                {
                    throw new InvalidOperationException($"Assistant presentation did not match the {width} DIP budget.");
                }
            }
        }
    }



    private static void VerifyTaskboardProjection()
    {
        var context = ApplicationContextSnapshot.Home;
        var ticketDraft = new InvestigationTicketDraft(
            InvestigationTicketDraft.CurrentSchemaVersion,
            new InvestigationId("investigation.ui-taskboard"),
            new TicketId("ticket.ui-taskboard"),
            1,
            null,
            null,
            null,
            Mods: [],
            IntegratedTools: [],
            ConfiguredToolContext: [],
            ReferenceContext: [],
            UserContext: [],
            Attachments: []);
        var assistant = new AssistantSessionSnapshot(
            true, 360, AssistantOperatingMode.Ask, AssistantProviderAvailability.NotConfigured,
            AssistantLifecycleStage.Idle, context, "fixture", AssistantSurface.Home, false, false,
            ticketDraft, InvestigationTicketDraftPolicy.Evaluate(ticketDraft), string.Empty,
            new AssistantDraftSnapshot(AssistantIntakeScope.Grid, null, [], [], null, string.Empty, "New request", "grid.icon.unknown",
                AssistantDraftReadiness.Incomplete, "fixture", false),
            [], [], [], [], [
                new AssistantTaskSummary("queue", "Queued task", DateTimeOffset.UtcNow, "AwaitingApproval"),
                new AssistantTaskSummary("progress", "Running task", DateTimeOffset.UtcNow, "InProgress"),
                new AssistantTaskSummary("ready", "Ready task", DateTimeOffset.UtcNow, "EvidenceReady"),
                new AssistantTaskSummary("done", "Done task", DateTimeOffset.UtcNow, "Completed"),
                new AssistantTaskSummary("diagnosed", "Diagnosed task", DateTimeOffset.UtcNow, "Diagnosed")
            ], null, null, null);
        var board = Grid.Core.Application.TaskboardProjection.Create(assistant);
        if (board.QueueCount != 1 || board.InProgressCount != 1 || board.ReadyCount != 1 || board.LedgerCount != 2)
            throw new InvalidOperationException("Taskboard phase projection did not preserve the four-panel contract.");

        var historyTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var activity = Grid.Core.Application.ActivityFeedProjection.CreateRecent(board,
        [
            new HistoryEntry(
                new HistoryEntryId("history.fixture"), historyTime, HistoryActor.User,
                HistoryEventKind.InstallationConnected, HistoryEventStatus.Succeeded,
                null, null, null, "Installation connected", "Fixture history detail.")
        ]);
        if (activity.Length != 6 || activity[0].Title != "Installation connected" ||
            activity.Count(item => item.TaskId is not null) != 5 ||
            activity.Single(item => item.TaskId == "done").Status != "Completed" ||
            activity.Single(item => item.TaskId == "diagnosed").Status != "Completed")
        {
            throw new InvalidOperationException("Recent Activity did not merge resumable investigations with ordinary history.");
        }
    }

    private static void VerifyInvestigationSnapshotContract()
    {
        var attachment = new AssistantAttachmentDraft(
            @"C:\fixture\evidence.png", "evidence.png", "image/png", 2048);
        var draft = new AssistantDraftSnapshot(
            AssistantIntakeScope.Game,
            new GameId("game.fixture"),
            [],
            [],
            "grid.class.world-objects",
            "The entrance is missing.",
            "Missing entrance",
            "grid.icon.world-object",
            AssistantDraftReadiness.ReadyForDeterministicCollection,
            "Ready to preserve and collect evidence.",
            true,
            InstallationId: new InstallationId("installation.fixture"),
            ProfileId: new ProfileId("profile.fixture"),
            Problem: "The entrance is missing.",
            ExpectedBehavior: "The entrance should be usable.",
            ReproductionOrLocation: "At the exterior hatch.",
            DesiredOutcome: "Identify the evidence-backed cause.",
            Attachments: [attachment]);

        var capabilityRequired = new AssistantCapabilityRequired(
            ["Runtime reference evidence was not collected."],
            ["Exact entrance reference identity"],
            ["grid.game.fixture.runtime-reference.collect"],
            [new AssistantCapabilityGapResolution(
                "gap.request.fixture", "grid.game.fixture.runtime-reference.collect",
                "CreationProposalRequired", "CreateCapabilityProposal", null, true,
                "Prepare a reusable CODE capability proposal.", new string('A', 64))]);
        var finding = new AssistantDeterministicFinding(
            [], [], "UNRESOLVED", "Collect the declared missing evidence.", [],
            CapabilityRequired: capabilityRequired);
        var archiveCandidate = new AssistantArchiveCandidate(
            "MysticismMagic.esp", "Mysticism", "Mysticism-2.4.2.zip", "ExactRestoration", "Complete",
            "NotRequiredForExactRestoration", [], "archive-candidate.1234567890abcdef12345678");
        var repair = new AssistantRepairAvailability(null, null, false, false, "No exact repair specification exists.",
            ArchiveCandidates: [archiveCandidate]);
        AssistantCaseActionState[] actions =
        [
            new(AssistantCaseAction.CaptureCurrentState, true, true, false, "Capture selected-profile context."),
            new(AssistantCaseAction.Diagnose, true, true, false, "Invoke registered deterministic coverage."),
            new(AssistantCaseAction.ApplyRepair, false, false, true, "No exact repair specification exists."),
            new(AssistantCaseAction.RollBack, false, false, true, "No verified rollback receipt exists."),
        ];

        if (draft.InstallationId?.Value != "installation.fixture" ||
            draft.ProfileId?.Value != "profile.fixture" ||
            draft.Attachments.Length != 1 ||
            finding.CapabilityRequired?.MissingCapabilityIds.Length != 1 ||
            finding.CapabilityRequired?.Resolutions.Single().Route != "CreateCapabilityProposal" ||
            repair.HasExactSpecification || repair.HasVerifiedRollbackReceipt || repair.ArchiveCandidates.Length != 1 ||
            actions.Single(state => state.Action == AssistantCaseAction.ApplyRepair).IsEnabled ||
            actions.Single(state => state.Action == AssistantCaseAction.RollBack).IsVisible)
        {
            throw new InvalidOperationException("New Investigation snapshots did not preserve context, evidence, CapabilityRequired, or repair gating.");
        }
    }

    private static void VerifyFirstRunStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-first-run-check-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "first-run.v1.json");
        try
        {
            var store = new LocalFirstRunStateStore(path);
            if (store.IsComplete()) throw new InvalidOperationException("A missing first-run document was treated as complete.");
            if (!store.MarkComplete() || !store.IsComplete()) throw new InvalidOperationException("First-run completion did not persist atomically.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyGameCatalogConnectionEngineAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-game-catalog-check-{Guid.NewGuid():N}");
        try
        {
            var healthRoot = Path.Combine(root, "scripts", "health");
            var definitionRoot = Path.Combine(root, "scripts", "games", "skyrimspecialedition");
            var gameRoot = Path.Combine(root, "Skyrim Special Edition");
            Directory.CreateDirectory(healthRoot);
            Directory.CreateDirectory(definitionRoot);
            Directory.CreateDirectory(gameRoot);
            await File.WriteAllTextAsync(Path.Combine(definitionRoot, "discovery.steam.v1.json"),
                """
                {"schemaVersion":1,"providerId":"steam","gameId":"skyrimspecialedition","displayName":"Skyrim Special Edition","applications":[{"appId":"489830","edition":"Special Edition","requiredFiles":["SkyrimSE.exe"],"conflictingFiles":[],"gameId":"skyrim-special-edition"}]}
                """);
            await File.WriteAllBytesAsync(Path.Combine(gameRoot, "SkyrimSE.exe"), [0x4d, 0x5a]);
            var discovery = new GridProviderDiscoveryService(
                Path.Combine(healthRoot, "Invoke-GridProviderDiscovery.ps1"),
                Path.Combine(root, "provider-scan.v1.json"));
            var candidate = await discovery.ResolveManualGameAsync(
                ProductionGridCatalogService.SkyrimSpecialEditionId,
                "Skyrim Special Edition",
                gameRoot);
            if (candidate.GameId != ProductionGridCatalogService.SkyrimSpecialEditionId.Value ||
                candidate.ProviderId != "manual" ||
                !candidate.ExecutablePath.EndsWith("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Manual Game Catalog resolution did not remain adapter-owned and game-specific.");
            if (GridProviderDiscoveryService.NormalizeCatalogGameId("grandtheftautov-legacy") ==
                GridProviderDiscoveryService.NormalizeCatalogGameId("grandtheftautov-enhanced"))
                throw new InvalidOperationException("GTA V Legacy and Enhanced collapsed to one catalog identity.");

            var managerRoot = Path.Combine(root, "Portable MO2");
            var profilesRoot = Path.Combine(managerRoot, "profiles");
            var reportPath = Path.Combine(root, "provider-scan.v1.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                registrationCandidates = Array.Empty<object>(),
                modManagers = new[]
                {
                    new
                    {
                        providerId = "mo2",
                        status = "Verified",
                        executablePath = Path.Combine(managerRoot, "ModOrganizer.exe"),
                        applicationRoot = managerRoot,
                        instanceRoot = managerRoot,
                        profilesRoot,
                        profiles = new[] { "First", "Second" },
                        running = true,
                        profileFidelity = "Authoritative",
                    },
                },
            }));
            var observedManagers = await discovery.ReadAsync();
            var observedManager = observedManagers.Managers.Single();
            if (observedManager.ApplicationRoot != managerRoot ||
                observedManager.InstanceRoot != managerRoot ||
                !observedManager.ProfileRoots.SequenceEqual([profilesRoot]) ||
                !observedManager.Profiles.SequenceEqual(["First", "Second"]))
                throw new InvalidOperationException("Provider discovery discarded the manager roots required by bounded existing-profile resolution.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyAccountScopedFirstRunAndConnectionStateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-account-scope-check-{Guid.NewGuid():N}");
        var previousDataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        const string accountAId = "github:account-a/with-path-characters";
        const string accountBId = "github:account-b";
        try
        {
            Environment.SetEnvironmentVariable("GRID_DATA_ROOT", root);
            var accountA = GridAccountDataScope.Resolve(root, accountAId);
            var accountB = GridAccountDataScope.Resolve(root, accountBId);
            var expectedParent = Path.Combine(Path.GetFullPath(root), "accounts", "v1");
            var expectedAccountA = Path.Combine(
                expectedParent,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountAId))).ToLowerInvariant());

            if (string.Equals(accountA, accountB, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(accountA, expectedAccountA, StringComparison.Ordinal) ||
                !string.Equals(accountA, GridAccountDataScope.Resolve(root, accountAId), StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(accountA), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(accountA).Length != 64 ||
                !Path.GetFileName(accountA).All(Uri.IsHexDigit) ||
                accountA.Contains(accountAId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Authenticated account state was not resolved to a deterministic opaque account scope.");
            }

            try
            {
                _ = GridAccountDataScope.Resolve(root, " ");
                throw new InvalidOperationException("A blank stable account identity did not fail closed.");
            }
            catch (ArgumentException)
            {
                // Expected: product state must never fall back to machine-global data.
            }
            try
            {
                _ = GridCompositionRoot.CreateProduction(" ");
                throw new InvalidOperationException("Production composition accepted a blank stable account identity.");
            }
            catch (ArgumentException)
            {
                // Expected: account-owned services require an authenticated identity.
            }

            var shell = GridCompositionRoot.CreateProductionShell();
            if (shell.FirstRunStateStore is not null || shell.GameRegistrationStore is not null || shell.Mo2ReferenceStore is not null)
                throw new InvalidOperationException("The signed-out Production shell composed account-owned persistence.");

            var legacyFirstRun = new LocalFirstRunStateStore(Path.Combine(root, "setup", "first-run.v1.json"));
            if (!legacyFirstRun.MarkComplete())
                throw new InvalidOperationException("The legacy first-run fixture could not be established.");

            var registration = new GameInstallationRegistration(
                GameInstallationRegistration.CurrentSchemaVersion,
                new InstallationReferenceId("reference.fixture.account-a"),
                new InstallationId("installation.fixture.account-a"),
                ProductionGridCatalogService.GrandTheftAutoVLegacyId,
                ProductionGridCatalogService.ProviderDiscoveryAdapterId,
                "Account A GTA fixture",
                "Legacy",
                "fixture",
                Path.Combine(root, "fixture-game"),
                Path.Combine(root, "fixture-game", "gta5.exe"),
                ImmutableArray<string>.Empty,
                new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

            var legacyRegistrations = new JsonGameInstallationRegistrationStore(
                Path.Combine(root, "connections", "game-installations.v1.json"));
            await legacyRegistrations.SaveAsync([registration]);
            var legacySetupBefore = await File.ReadAllBytesAsync(Path.Combine(root, "setup", "first-run.v1.json"));
            var legacyConnectionsBefore = await File.ReadAllBytesAsync(Path.Combine(root, "connections", "game-installations.v1.json"));

            var accountAComposition = GridCompositionRoot.CreateProduction(accountAId);
            var accountBComposition = GridCompositionRoot.CreateProduction(accountBId);
            if (accountAComposition.FirstRunStateStore is null || accountBComposition.FirstRunStateStore is null ||
                accountAComposition.GameRegistrationStore is null || accountBComposition.GameRegistrationStore is null)
            {
                throw new InvalidOperationException("Authenticated Production composition omitted account-owned persistence.");
            }
            if (accountAComposition.FirstRunStateStore.IsComplete() || accountBComposition.FirstRunStateStore.IsComplete())
                throw new InvalidOperationException("Legacy unowned first-run state leaked into an authenticated account scope.");

            var discoveryReportPath = Path.Combine(accountB, "discovery", "provider-scan.v1.json");
            Directory.CreateDirectory(Path.GetDirectoryName(discoveryReportPath)!);
            await File.WriteAllTextAsync(discoveryReportPath,
                $$"""
                {
                  "registrationCandidates": [
                    {
                      "gameId": "grandtheftautov",
                      "edition": "Legacy",
                      "registrationStatus": "ReadyForReview",
                      "installRoot": "{{Path.Combine(root, "discovered-game").Replace("\\", "\\\\")}}",
                      "observations": [
                        {
                          "gameDisplayName": "Grand Theft Auto V",
                          "providerId": "steam",
                          "executablePath": "{{Path.Combine(root, "discovered-game", "GTA5.exe").Replace("\\", "\\\\")}}"
                        }
                      ]
                    }
                  ],
                  "modManagers": []
                }
                """);
            var discovery = new GridProviderDiscoveryService(
                Path.Combine(root, "unused-discovery-script.ps1"),
                discoveryReportPath);
            var candidates = await discovery.ReadAsync();
            if (candidates.Games.Count != 1 ||
                !(await accountBComposition.GameRegistrationStore.LoadAsync()).Registrations.IsEmpty ||
                File.Exists(Path.Combine(accountB, "connections", "game-installations.v1.json")))
            {
                throw new InvalidOperationException(
                    "A discovery candidate was persisted or labeled connected before explicit registration.");
            }
            if (!accountAComposition.FirstRunStateStore.MarkComplete() ||
                !accountAComposition.FirstRunStateStore.IsComplete() ||
                accountBComposition.FirstRunStateStore.IsComplete())
            {
                throw new InvalidOperationException("First-run completion was not isolated by stable account identity.");
            }
            await accountAComposition.GameRegistrationStore.SaveAsync([registration]);

            var accountAConnections = await accountAComposition.GameRegistrationStore.LoadAsync();
            var accountBConnections = await accountBComposition.GameRegistrationStore.LoadAsync();
            if (!accountAConnections.Issues.IsEmpty || accountAConnections.Registrations.Length != 1 ||
                !accountBConnections.Issues.IsEmpty || !accountBConnections.Registrations.IsEmpty)
            {
                throw new InvalidOperationException("Persisted game connections were not isolated by stable account identity.");
            }

            var accountACatalog = await accountAComposition.CatalogService.GetCatalogAsync();
            var accountBCatalog = await accountBComposition.CatalogService.GetCatalogAsync();
            var shellCatalog = await shell.CatalogService.GetCatalogAsync();

            if (accountACatalog.Games.Sum(game => game.Installations.Length) != 1)
                throw new InvalidOperationException("The owning account did not project its explicit persisted connection.");
            if (accountBCatalog.Games.Sum(game => game.Installations.Length) != 0 ||
                shellCatalog.Games.Sum(game => game.Installations.Length) != 0)
            {
                throw new InvalidOperationException("Unowned or another account's registrations leaked into connected games.");
            }
            if (accountBCatalog.Games.Length != 88 || shellCatalog.Games.Length != 3 ||
                accountBCatalog.Games.Any(game => !game.Installations.IsEmpty) ||
                shellCatalog.Games.Any(game => !game.Installations.IsEmpty))
                throw new InvalidOperationException("Supported catalog entries were hidden instead of remaining unconnected definitions.");

            var accountAConnectionPath = Path.Combine(accountA, "connections", "game-installations.v1.json");
            var accountBConnectionPath = Path.Combine(accountB, "connections", "game-installations.v1.json");
            if (!File.Exists(accountAConnectionPath) || File.Exists(accountBConnectionPath))
                throw new InvalidOperationException("Connection files were not confined to the owning account scope.");
            if (!legacySetupBefore.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(root, "setup", "first-run.v1.json"))) ||
                !legacyConnectionsBefore.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(root, "connections", "game-installations.v1.json"))))
            {
                throw new InvalidOperationException("Legacy unowned records were modified during account-scope verification.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRID_DATA_ROOT", previousDataRoot);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyOfflineAlertIndexStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-offline-alert-check-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalOfflineAlertIndexStore(root);
            var context = new WorkspaceToolOutputContext(
                new GameId("game.fixture"),
                new InstallationId("installation.fixture"),
                new ProfileId("profile.fixture"),
                new InstallationReferenceId("reference.fixture"),
                "catalog.fixture",
                new string('A', 64),
                new string('B', 64));
            var expected = new OfflineAlertIndexBuilder().Build(context, null, null);
            store.Save(expected);
            var actual = store.Load(context.GameId, context.InstallationId, context.ProfileId);
            if (actual is null || actual.Fingerprint != expected.Fingerprint || actual.WorkspaceFingerprint != expected.WorkspaceFingerprint)
                throw new InvalidOperationException("The local offline alert index did not round-trip exact profile evidence.");

            store.Save(expected with { UpdatedAtUtc = expected.UpdatedAtUtc.AddMinutes(1) });
            if (Directory.GetFiles(root, "*.v1.json").Length != 1)
                throw new InvalidOperationException("The local offline alert index did not atomically replace its exact profile snapshot.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyHistoryStoreAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-history-check-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "history.v1.json");
        try
        {
            var store = new LocalUserHistoryStore(path);
            var entry = new HistoryEntry(
                new HistoryEntryId("history.ui.fixture"),
                DateTimeOffset.UtcNow,
                HistoryActor.User,
                HistoryEventKind.InstallationConnected,
                HistoryEventStatus.Succeeded,
                new GameId("game.fixture"),
                new InstallationId("installation.fixture"),
                null,
                "Installation connected",
                "Grid stored a reconnectable reference.");
            await store.AppendAsync(entry);
            var loaded = await store.LoadAsync();
            if (loaded.Issue is not null || loaded.Entries.Length != 1 || loaded.Entries[0].Id != entry.Id)
            {
                throw new InvalidOperationException("Versioned user History did not round-trip atomically.");
            }
            try
            {
                await store.AppendAsync(entry with { Id = new HistoryEntryId("history.path"), Detail = @"Private C:\fixture path" });
                throw new InvalidOperationException("History accepted a private absolute-path shape.");
            }
            catch (ArgumentException)
            {
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyDifLayoutCleanupContracts()
    {
        var checks = 0;
        void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("DIF layout: " + detail);
            checks++;
        }
        var repository = FindRepositoryRoot();
        var markup = System.Xml.Linq.XDocument.Load(Path.Combine(repository, "src/Grid.App/Views/AssistantPanel.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var all = markup.Descendants().ToArray();
        System.Xml.Linq.XElement Named(string name) => all.Single(element => (string?)element.Attribute(x + "Name") == name);
        var style = all.Single(element => (string?)element.Attribute(x + "Key") == "TicketSelectorButtonStyle");
        string? Setter(string name) => (string?)style.Elements().Single(element => (string?)element.Attribute("Property") == name).Attribute("Value");
        Check(Setter("Height") == "32", "Every closed selector must share the 32 DIP field height.");
        Check(Setter("CornerRadius") == "0" && Setter("Padding") == "9,0" && Setter("FontSize") == "11",
            "Shared closed-selector shape, padding and typography must remain identical.");
        foreach (var name in new[] { "GameSelector", "ProfileSelector", "ClassSelector", "ProblemSelector", "TimingSelector", "GoalSelector" })
            Check((string?)Named(name).Attribute("Style") == "{StaticResource TicketSelectorButtonStyle}",
                name + " must use the shared closed-field style.");
        var goal = Named("GoalSelector");
        Check(goal.Parent == Named("GoalRow") && (string?)goal.Attribute("Grid.Column") == "1" &&
              (string?)Named("GoalRow").Attribute("ColumnDefinitions") == "*,*,*",
            "Goal must be centered in the middle of three equal columns.");
        Check(Named("DifContentInset").Ancestors().Contains(Named("ChatContentScrollViewer")),
            "The outer DIF inset must belong to the chat content viewport.");
        var dif = Named("TicketSelectorRows");
        foreach (var viewport in dif.Descendants().Where(element => element.Name.LocalName == "ScrollViewer"))
        {
            Check(viewport.Attribute("Width") is null && viewport.Attribute("Height") is null &&
                  viewport.Attribute("MaxWidth") is null && viewport.Attribute("MaxHeight") is null,
                "DIF flyout bounds must come from current content/window measurement rather than fixed markup dimensions.");
            Check((string?)viewport.Attribute("HorizontalScrollBarVisibility") == "Disabled",
                "Measured selector flyouts must wrap to their available width, without horizontal scrolling.");
        }
        var panel = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Views/AssistantPanel.xaml.cs"));
        var control = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Controls/CanonicalSelectorNavigationControl.cs"));
        var sizing = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Controls/SelectorFlyoutSizing.cs"));
        Check(panel.Contains("DifContentInset.Padding = IntakeForm.Visibility == Visibility.Visible", StringComparison.Ordinal) &&
              panel.Contains("? new Thickness(5) : new Thickness(16, 14, 16, 24)", StringComparison.Ordinal),
            "Only visible DIF uses the five DIP outer gutter; other chat surfaces retain their prior inset.");
        Check(panel.Contains("selector.ApplyClosedSelectorStyle(selectorStyle)", StringComparison.Ordinal) &&
              panel.Contains("ToolsButton.Style = selectorStyle", StringComparison.Ordinal) &&
              panel.Contains("ModsButton.Style = selectorStyle", StringComparison.Ordinal),
            "Scaffold controls and Tool/Mod must use the very same style as single selectors.");
        var styleStart = control.IndexOf("public void ApplyClosedSelectorStyle(", StringComparison.Ordinal);
        var styleEnd = control.IndexOf("public void ConfigureScaffold(", styleStart, StringComparison.Ordinal);
        var appliedStyle = control[styleStart..styleEnd];
        foreach (var property in new[] { "FrameworkElement.HeightProperty", "Control.PaddingProperty", "Control.FontSizeProperty",
            "Control.CornerRadiusProperty", "Control.BackgroundProperty", "Control.BorderBrushProperty", "Control.BorderThicknessProperty",
            "Control.TemplateProperty", "ContentControl.ContentTemplateProperty" })
            Check(appliedStyle.Contains("ClearValue(" + property + ")", StringComparison.Ordinal),
                "A local scaffold override must not defeat the shared style: " + property);
        Check(appliedStyle.Contains("selectorButton.Style = style", StringComparison.Ordinal), "The cleared scaffold field must receive the shared style.");
        Check(panel.Contains("flyout.Opening += (_, _) => SelectorFlyoutSizing.Apply", StringComparison.Ordinal) &&
              panel.Contains("AttachContentSizedFlyout(overflow)", StringComparison.Ordinal),
            "Single-select and overflow flyouts must measure their current contents when opened.");
        var renderStart = control.IndexOf("private void RenderScaffold()", StringComparison.Ordinal);
        var renderEnd = control.IndexOf("private static Button FlatScaffoldButton", renderStart, StringComparison.Ordinal);
        var render = control[renderStart..renderEnd];
        Check(render.Contains("SelectorFlyoutSizing.Apply", StringComparison.Ordinal),
            "Every newly rendered scaffold level must update its dynamic size.");
        Check(!control.Contains("Width = 320", StringComparison.Ordinal) && !control.Contains("MaxHeight = 280", StringComparison.Ordinal),
            "Scaffold levels must not retain superseded fixed flyout dimensions.");
        Check(sizing.Contains("body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))", StringComparison.Ordinal) &&
              sizing.Contains("body.Measure(new Size(width, double.PositiveInfinity))", StringComparison.Ordinal),
            "Sizing must measure natural width then the height after wrapping at available width.");
        Check(sizing.Contains("body.DesiredSize", StringComparison.Ordinal) && sizing.Contains("AvailableContentBounds(anchor)", StringComparison.Ordinal) &&
              sizing.Contains("bounds.Height - fixedHeight", StringComparison.Ordinal),
            "Body height must account for actual measured header/footer space and window availability.");
        Check(sizing.Contains("root.Size.Width", StringComparison.Ordinal) && sizing.Contains("Math.Max(above, below)", StringComparison.Ordinal),
            "Bounds must derive from the current window and anchor placement, not a fixed menu size.");
        Check(sizing.Contains("ConditionalWeakTable<Flyout, PresenterBasis>", StringComparison.Ordinal),
            "Repeated sizing must retain only the original presenter style without an accumulating style chain.");
        Check(!render.Contains("QueryPage", StringComparison.Ordinal) && !render.Contains("CanonicalSelectorProjectionEngine", StringComparison.Ordinal),
            "Layout measurement must not reconnect canonical querying to scaffold rendering.");
        Console.WriteLine($"PASS: {checks} focused DIF layout cleanup contracts; no GRID launch or catalog access.");
    }

    private static void VerifySelectorPresentationContracts()
    {
        var checks = 0;
        void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Selector presentation: " + detail);
            checks++;
        }
        var panelType = typeof(Grid.App.Views.AssistantPanel);
        var valueMethod = panelType.GetMethod("SelectorValue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The pure selector caption helper is absent.");
        var labelMethod = panelType.GetMethod("ReferenceContextLabel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The pure reference display helper is absent.");
        foreach (var pair in new[]
        {
            (Field: "Game", Value: "GTA V Enhanced"),
            (Field: "Profile", Value: "GTA V Enhanced"),
            (Field: "Class", Value: "Crash & Freeze"),
            (Field: "Problem", Value: "Current State"),
            (Field: "Timing", Value: "When entering"),
            (Field: "Tool", Value: "Example Tool"),
            (Field: "Mod", Value: "Example Mod"),
        })
        {
            Check((string?)valueMethod.Invoke(null, [pair.Field, pair.Value]) == pair.Value,
                $"{pair.Field} must show the selected value without repeating its field name.");
            Check((string?)valueMethod.Invoke(null, [pair.Field, null]) == pair.Field &&
                  (string?)valueMethod.Invoke(null, [pair.Field, " "]) == pair.Field,
                $"{pair.Field} must retain a compact field-only empty caption.");
        }
        foreach (var knowledgeKind in Enum.GetValues<KnowledgeKind>())
        {
            var contextKind = knowledgeKind switch
            {
                KnowledgeKind.Location => TicketReferenceContextKind.Location,
                KnowledgeKind.MissionQuest => TicketReferenceContextKind.MissionOrQuest,
                KnowledgeKind.Item => TicketReferenceContextKind.Item,
                KnowledgeKind.Actor => TicketReferenceContextKind.Entity,
                _ => throw new ArgumentOutOfRangeException(nameof(knowledgeKind)),
            };
            foreach (var node in SelectorScaffoldContract.Enumerate(knowledgeKind))
            {
                var context = new TicketUserContext(contextKind,
                    SelectorScaffoldContract.DisplayPath(knowledgeKind, node.Key),
                    TicketUserContextResolution.Unresolved, TicketSelectionProvenance.ExplicitUserSelection)
                    { ScaffoldPathId = node.Key };
                var before = JsonSerializer.Serialize(context);
                Check((string?)labelMethod.Invoke(null, [context]) == node.Label,
                    $"{knowledgeKind} preview must show only '{node.Label}', not its full ancestry.");
                Check(JsonSerializer.Serialize(context) == before,
                    "Short display labels must not rewrite stored context identity or display paths.");
            }
            var other = new TicketUserContext(contextKind, "User's exact place / detail",
                TicketUserContextResolution.Unresolved, TicketSelectionProvenance.ExplicitUserSelection);
            Check((string?)labelMethod.Invoke(null, [other]) == other.Value,
                $"{knowledgeKind} Other display must retain user text without a redundant prefix.");
        }
        var repository = FindRepositoryRoot();
        var panel = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Views/AssistantPanel.xaml.cs"));
        var control = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Controls/CanonicalSelectorNavigationControl.cs"));
        var layoutStart = panel.IndexOf("private void ApplyTicketSelectorLayout(", StringComparison.Ordinal);
        var layoutEnd = panel.IndexOf("private static void ConfigureGrid(", layoutStart, StringComparison.Ordinal);
        var layout = panel[layoutStart..layoutEnd];
        Check(layout.Contains("ConfigureGrid(ClassificationRow, 3, 1)", StringComparison.Ordinal) &&
              layout.Contains("Place(ClassSelector, 0, 0)", StringComparison.Ordinal) &&
              layout.Contains("Place(ProblemSelector, 0, 1)", StringComparison.Ordinal) &&
              layout.Contains("Place(TimingSelector, 0, 2)", StringComparison.Ordinal) &&
              !layout.Contains("threeAcross", StringComparison.Ordinal),
            "Class, Problem and Timing must remain three equal columns on one row at narrow widths.");
        Check(panel.Contains("new GridLength(1, GridUnitType.Star)", StringComparison.Ordinal),
            "Selector columns must share available width equally.");
        Check(panel.Contains("SetScaffoldCaptionFallback(", StringComparison.Ordinal) &&
              panel.Contains("ResetScaffoldPresentationContext(", StringComparison.Ordinal),
            "Closed scaffold captions must retain useful context and reset at an actual draft/profile boundary.");
        var renderStart = control.IndexOf("private void RenderScaffold()", StringComparison.Ordinal);
        var renderEnd = control.IndexOf("private static Button FlatScaffoldButton", renderStart, StringComparison.Ordinal);
        var render = control[renderStart..renderEnd];
        Check(!render.Contains("parent?.Label ?? title", StringComparison.Ordinal),
            "The open scaffold root must not duplicate its selector title in a heading.");
        Check(!render.Contains("QueryPage", StringComparison.Ordinal) &&
              !render.Contains("CanonicalSelectorProjectionEngine", StringComparison.Ordinal),
            "Compact rendering must preserve the local scaffold path without canonical work.");
        Check(!control.Contains("Height = 291", StringComparison.Ordinal) &&
              control.Contains("SelectorFlyoutSizing.Apply", StringComparison.Ordinal) &&
              control.Contains("header.Visibility = parent is null ? Visibility.Collapsed : Visibility.Visible", StringComparison.Ordinal),
            "Scaffold flyouts must size to content with a bounded maximum, rather than fixed empty height.");
        Console.WriteLine($"PASS: {checks} focused selector presentation contracts; no GRID launch or catalog access.");
    }

    private static async Task VerifyLocationScaffoldContractsAsync()
    {
        var checks = 0;
        void Check(bool value, string detail)
        {
            if (!value) throw new InvalidOperationException("DIF scaffold: " + detail);
            checks++;
        }
        var catalog = await new MockGridCatalogService().GetCatalogAsync();
        var game = catalog.Games.Single(value => value.Id.Value == "game.skyrim-special-edition");
        var installation = game.Installations[0];
        var context = new ApplicationContextSnapshot(ApplicationSurface.GameWorkspace,
            game.Id, installation.Id, installation.Profiles[0].Id, [],
            null, null, null, null, null, null, null, null);
        var state = new AssistantSessionState(context, catalog, [], []);
        var policy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(game.Id);
        CanonicalSelectorSelection Selection(KnowledgeKind kind, char key) => new(
            CanonicalSelectorSelectionKind.CanonicalRecord, kind,
            new CatalogRevisionId("grid.catalog-revision.v5.sha256." + new string('5', 64)),
            new CatalogCompositionId("composition.scaffold.fixture"), policy.Id, policy.ExactVersion,
            new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string(key, 64)),
            new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string(key, 64)), null);
        var actor = Selection(KnowledgeKind.Actor, 'a');
        state.SetCanonicalSelectorSelection(actor);
        state.ToggleCanonicalLocationSelection(Selection(KnowledgeKind.Location, 'b'));
        state.ToggleCanonicalLocationSelection(Selection(KnowledgeKind.Location, 'c'));
        state.ReplaceOtherContext(TicketReferenceContextKind.Item, "existing item Other");
        var before = state.Snapshot().TicketDraft;
        state.ClearCanonicalSelectorSelection(KnowledgeKind.Location);
        var cleared = state.Snapshot().TicketDraft;
        Check(cleared.CanonicalSelections.SequenceEqual([actor]), "Clearing Location must preserve another selector's canonical selection.");
        Check(cleared.UserContext.SequenceEqual(before.UserContext), "Clearing Location must preserve unresolved context.");
        Check(cleared.GameId == before.GameId && cleared.ProfileId == before.ProfileId &&
              cleared.InstallationId == before.InstallationId, "Clearing Location must preserve the connected context.");
        state.AppendLocationOtherContext("existing Location Other");
        var withOther = state.Snapshot().TicketDraft;
        state.ClearCanonicalSelectorSelection(KnowledgeKind.Location);
        state.ClearCanonicalSelectorSelection(KnowledgeKind.Location);
        var repeated = state.Snapshot().TicketDraft;
        Check(repeated.CanonicalSelections.SequenceEqual(withOther.CanonicalSelections) &&
              repeated.UserContext.SequenceEqual(withOther.UserContext), "Repeated scaffold clearing must preserve Location Other and be idempotent.");
        state.AppendLocationOtherContext("temporary scaffold context");
        Check(state.Snapshot().TicketDraft.UserContext.Last().Resolution == TicketUserContextResolution.Unresolved &&
              state.Snapshot().TicketDraft.CanonicalSelections.All(value => value.KnowledgeKind != KnowledgeKind.Location),
            "Scaffold Other must remain unresolved, without a canonical record.");
        state.RemoveLocationOtherContext("temporary scaffold context");
        Check(state.Snapshot().TicketDraft.UserContext.SequenceEqual(withOther.UserContext), "Removing only the test Other must preserve preexisting context and order.");

        void Reject(Action action, string detail)
        {
            try { action(); }
            catch (ArgumentException) { checks++; return; }
            throw new InvalidOperationException("DIF scaffold accepted invalid input: " + detail);
        }
        foreach (var kind in Enum.GetValues<TicketReferenceContextKind>())
        {
            var draftState = new AssistantSessionState(context, catalog, [], []);
            var prefix = "scaffold:" + kind;
            draftState.AppendOtherContext(kind, "  Same label  ");
            draftState.ToggleScaffoldContext(kind, prefix + "/parent", "Same label");
            draftState.ToggleScaffoldContext(kind, prefix + "/parent/leaf", "Parent > Leaf");
            draftState.AppendOtherContext(kind, "Second Other");
            draftState.ToggleScaffoldContext(kind, prefix + "/sibling/leaf", "Sibling > Leaf");
            draftState.ToggleScaffoldContext(kind, prefix + "/another", "Another");
            var ordered = draftState.Snapshot().TicketDraft.UserContext;
            Check(ordered.Length == 6 && ordered.Select(value => value.Value).SequenceEqual(
                    ["Same label", "Same label", "Parent > Leaf", "Second Other", "Sibling > Leaf", "Another"]),
                $"{kind}: parent, descendants, sibling and Other must coexist in original selection order beyond the three-inline preview limit.");
            Check(ordered[0].ScaffoldPathId is null && ordered[1].ScaffoldPathId == prefix + "/parent" &&
                  ordered[2].ScaffoldPathId != ordered[4].ScaffoldPathId,
                $"{kind}: identical Other/slot labels and repeated leaf labels must retain distinct path identities.");
            Check(ordered.All(value => value.Resolution == TicketUserContextResolution.Unresolved &&
                    value.Provenance == TicketSelectionProvenance.ExplicitUserSelection && value.MatchedReferenceId is null) &&
                  draftState.Snapshot().TicketDraft.CanonicalSelections.IsEmpty,
                $"{kind}: scaffold selections must never become canonical records or matched knowledge.");
            var revision = draftState.Snapshot().TicketDraft.Revision;
            draftState.AppendOtherContext(kind, "Same label");
            Check(draftState.Snapshot().TicketDraft.Revision == revision &&
                  draftState.Snapshot().TicketDraft.UserContext.SequenceEqual(ordered),
                $"{kind}: duplicate Other must not reorder selections or edit the draft.");
            draftState.ToggleScaffoldContext(kind, prefix + "/parent", "Same label");
            Check(draftState.Snapshot().TicketDraft.UserContext.SequenceEqual(ordered.Where(value => value.ScaffoldPathId != prefix + "/parent")),
                $"{kind}: parent toggle must preserve selected descendants and same-label Other.");
            draftState.ToggleScaffoldContext(kind, prefix + "/parent", "Same label");
            Check(draftState.Snapshot().TicketDraft.UserContext.Last().ScaffoldPathId == prefix + "/parent",
                $"{kind}: reselected context must append at the end.");
            draftState.RemoveReferenceContext(kind, "Same label", null);
            Check(draftState.Snapshot().TicketDraft.UserContext.Any(value => value.ScaffoldPathId == prefix + "/parent") &&
                  !draftState.Snapshot().TicketDraft.UserContext.Any(value => value.ScaffoldPathId is null && value.Value == "Same label"),
                $"{kind}: Other removal must not remove a same-label scaffold.");
            draftState.RemoveReferenceContext(kind, "display text is not identity", prefix + "/parent");
            Check(draftState.Snapshot().TicketDraft.UserContext.All(value => value.ScaffoldPathId != prefix + "/parent") &&
                  draftState.Snapshot().TicketDraft.UserContext.Any(value => value.ScaffoldPathId == prefix + "/parent/leaf"),
                $"{kind}: exact path removal must leave descendants selected.");
            Reject(() => draftState.ToggleScaffoldContext(kind, " ", "Name"), "blank path");
            Reject(() => draftState.ToggleScaffoldContext(kind, new string('x', 513), "Name"), "oversized path");
            Reject(() => draftState.ToggleScaffoldContext(kind, prefix, " "), "blank display path");
            Reject(() => draftState.AppendOtherContext(kind, " "), "blank Other");
            var sample = ordered[1];
            var valid = draftState.Snapshot().TicketDraft with { UserContext = [sample] };
            Check(InvestigationTicketDraftPolicy.Evaluate(valid).Status != InvestigationTicketReadinessStatus.Invalid,
                $"{kind}: valid scaffold context remains a valid inert draft.");
            foreach (var invalid in new[]
            {
                sample with { ScaffoldPathId = " " },
                sample with { ScaffoldPathId = new string('x', 513) },
                sample with { Resolution = TicketUserContextResolution.Matched, MatchedReferenceId = new TicketReferenceContextId("reference.fixture") },
                sample with { Provenance = TicketSelectionProvenance.LegacyImported },
                sample with { MatchedReferenceId = new TicketReferenceContextId("reference.fixture") },
            })
                Check(InvestigationTicketDraftPolicy.Evaluate(valid with { UserContext = [invalid] }).Status == InvestigationTicketReadinessStatus.Invalid,
                    $"{kind}: forged scaffold resolution/provenance/path must be rejected.");
            Check(InvestigationTicketDraftPolicy.Evaluate(valid with { UserContext = [sample, sample] }).Status == InvestigationTicketReadinessStatus.Invalid,
                $"{kind}: duplicate scaffold path identities must be rejected.");
            var roundTrip = JsonSerializer.Deserialize<TicketUserContext>(JsonSerializer.Serialize(sample));
            Check(roundTrip == sample, $"{kind}: scaffold identity must round-trip as unresolved user context.");
        }
        var historicalOther = new TicketUserContext(TicketReferenceContextKind.Location, "Historical Other",
            TicketUserContextResolution.Unresolved, TicketSelectionProvenance.ExplicitUserSelection);
        var historicalJson = JsonSerializer.Serialize(historicalOther);
        Check(!historicalJson.Contains("ScaffoldPathId", StringComparison.Ordinal) &&
              JsonSerializer.Deserialize<TicketUserContext>(historicalJson) == historicalOther,
            "Legacy untagged context must keep its JSON shape and remain readable.");

        var crossKind = new AssistantSessionState(context, catalog, [], []);
        foreach (var kind in Enum.GetValues<TicketReferenceContextKind>())
            crossKind.ToggleScaffoldContext(kind, "same-path", "Same path");
        crossKind.ToggleScaffoldContext(TicketReferenceContextKind.Location, "same-path", "Same path");
        Check(crossKind.Snapshot().TicketDraft.UserContext.Length == 3 &&
              crossKind.Snapshot().TicketDraft.UserContext.All(value => value.Kind != TicketReferenceContextKind.Location),
            "Scaffold identities must be scoped by selector kind.");

        foreach (var kind in Enum.GetValues<KnowledgeKind>())
        {
            var nodes = SelectorScaffoldContract.Enumerate(kind).ToArray();
            Check(nodes.Length > 0 && nodes.Select(node => node.Key).Distinct(StringComparer.Ordinal).Count() == nodes.Length,
                $"{kind}: all tree paths must be unique and nonempty.");
            foreach (var node in nodes)
            {
                Check(SelectorScaffoldContract.Resolve(kind, node.Key) == node &&
                      SelectorScaffoldContract.DisplayPath(kind, node.Key).EndsWith(node.Label, StringComparison.Ordinal),
                    $"{kind}: every node must resolve to its stable complete display path.");
                Check(node.Children.All(child => child.Key.StartsWith(node.Key + "/", StringComparison.Ordinal)),
                    $"{kind}: descendants must retain their structural parent path.");
                if (!node.SortViews.IsEmpty)
                {
                    var childKeys = node.Children.Select(child => child.Key).ToArray();
                    foreach (var view in node.SortViews)
                        Check(SelectorScaffoldContract.Resolve(kind, node.Key)!.Children.Select(child => child.Key).SequenceEqual(childKeys),
                            $"{kind}: sort view '{view}' must not create another identity for the same leaf.");
                }
            }
            Check(SelectorScaffoldContract.Resolve(kind, "unknown-path") is null,
                $"{kind}: unknown structural paths must not resolve to invented nodes.");
        }
        Check(SelectorScaffoldContract.Enumerate(KnowledgeKind.Location).Select(node => node.Label).SequenceEqual(
            ["World", "Continent", "Country", "State", "County/Region", "City", "Town/Neighborhood", "Street", "Structure", "Room"]),
            "Location must retain the ten approved structural levels.");
        Check(SelectorScaffoldContract.Roots(KnowledgeKind.MissionQuest).Select(node => node.Label).SequenceEqual(
            ["DLC", "Mod", "Online", "Story Mode"]) &&
            SelectorScaffoldContract.Enumerate(KnowledgeKind.MissionQuest).Count(node => node.Label == "Job") == 4,
            "Mission branches must keep distinct Job leaves with shared sort views.");
        Check(SelectorScaffoldContract.Roots(KnowledgeKind.Item).Select(node => node.Label).SequenceEqual(
            ["Ammunition/Projectile", "Clutter/Props", "Consumables", "Magic", "Vehicles", "Weapons", "Wearables"]),
            "Item must expose exactly the seven approved root families.");
        Check(SelectorScaffoldContract.Resolve(KnowledgeKind.Item, "wearables")!.Children.Select(node => node.Label).SequenceEqual(
            ["Accessories", "Head", "Top", "Bottom", "Hands", "Feet", "Outfit"]) &&
            SelectorScaffoldContract.Resolve(KnowledgeKind.Item, "outfit") is null &&
            SelectorScaffoldContract.DisplayPath(KnowledgeKind.Item, "wearables/outfit/outfit-class/outfit") ==
                "Item → Wearables → Outfit → Outfit Class → Outfit",
            "Outfit belongs beneath Wearables after Feet, with its existing class and leaf.");
        Check(SelectorScaffoldContract.Roots(KnowledgeKind.Actor).Select(node => node.Label).SequenceEqual(
            ["Player Character", "Non Player Character"]), "Actor must preserve the approved two-branch structure.");

        var repository = FindRepositoryRoot();
        var panel = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Views/AssistantPanel.xaml.cs"));
        var control = File.ReadAllText(Path.Combine(repository, "src/Grid.App/Controls/CanonicalSelectorNavigationControl.cs"));
        Check(panel.Contains("ConfigureScaffold(", StringComparison.Ordinal) &&
              panel.Contains("ToggleScaffoldContext(", StringComparison.Ordinal) &&
              panel.Contains("AppendOtherContext(", StringComparison.Ordinal), "All-selector App integration must use scaffold and unresolved-context APIs.");
        var activePanelStart = panel.IndexOf("private void PopulateReferenceContexts(", StringComparison.Ordinal);
        var activePanelEnd = panel.IndexOf("private void PopulateReferenceSelectionPreview(", activePanelStart, StringComparison.Ordinal);
        var activePanel = panel[activePanelStart..activePanelEnd];
        Check(!activePanel.Contains("canonicalCatalogRuntimeService", StringComparison.Ordinal) &&
              !activePanel.Contains("ConfigureCanonicalSelector", StringComparison.Ordinal) &&
              !activePanel.Contains("CanonicalSelectionLabel", StringComparison.Ordinal) &&
              !activePanel.Contains("PruneInvalidCanonicalSelections", StringComparison.Ordinal),
            "Active reference controls must not query, match, materialize or prune canonical catalog state.");
        Check(panel.Contains("!IsCurrentReferenceEditingContext(originatingContext)", StringComparison.Ordinal) &&
              panel.Contains("pendingOtherContexts.TryGetValue(kind, out var editingContext)", StringComparison.Ordinal) &&
              panel.Contains("!IsCurrentReferenceEditingContext(editingContext)", StringComparison.Ordinal) &&
              panel.Contains("!context.Lifetime.IsCancellationRequested", StringComparison.Ordinal),
            "Deferred Other commits and removal buttons must reject retired investigation/profile/account contexts.");
        var configureStart = control.IndexOf("public void ConfigureScaffold(", StringComparison.Ordinal);
        var configureEnd = control.IndexOf("public void Configure(", configureStart, StringComparison.Ordinal);
        var configure = control[configureStart..configureEnd];
        Check(configure.Contains("query = null;", StringComparison.Ordinal) && configure.Contains("asyncQuery = null;", StringComparison.Ordinal),
            "Scaffold configuration must detach both canonical query providers.");
        var renderStart = control.IndexOf("private void RenderScaffold", StringComparison.Ordinal);
        var renderEnd = control.IndexOf("private void RenderHeader()", renderStart, StringComparison.Ordinal);
        var render = control[renderStart..renderEnd];
        Check(!render.Contains("QueryPage", StringComparison.Ordinal) &&
              !render.Contains("CanonicalSelectorProjectionEngine", StringComparison.Ordinal), "Structural navigation must neither query canonical data nor commit fake records.");
        Console.WriteLine($"PASS: {checks} focused DIF scaffold contracts; no GRID launch or catalog access.");
    }

    private static async Task<int> DirectKnowledgeRebuildAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var gridDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid");
        var catalogsRoot = Path.Combine(gridDataRoot, "catalogs");
        var storePath = Path.Combine(catalogsRoot, CanonicalCatalogRuntimeBindingStore.DefaultCatalogStoreFileName);
        var bindingPath = Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json");
        var binding = new CanonicalCatalogRuntimeBindingStore(bindingPath).Load()
            ?? throw new InvalidDataException("Runtime binding is required for knowledge rebuild proof.");
        var gameId = new GameId("game.grandtheftautov-enhanced");
        var installationId = new InstallationId("installation.game.bacfc475fd76113cc0bfe5fe");
        var profileId = new ProfileId("profile.grid.4bbecfed02005580f00e");
        var storeShaBefore = await Sha256FileAsync(storePath);
        var relationshipCountBefore = CountReferenceHierarchyRelationships(storePath, binding.PackageId);
        var rootNodesBefore = await CountLocationRootNodesAsync(storePath, binding.PackageId);
        Environment.SetEnvironmentVariable("GRID_REPOSITORY_ROOT", @"c:\Users\woods\Documents\GitHub\grid");
        var service = new CanonicalRegistrationRefreshService(gridDataRoot, new CanonicalTerminologyLocalePreference("en-US", []));
        var result = await service.RefreshAsync(gameId, installationId, profileId, null);
        var storeShaAfter = await Sha256FileAsync(storePath);
        var bindingAfter = new CanonicalCatalogRuntimeBindingStore(bindingPath).Load();
        var relationshipCountAfter = CountReferenceHierarchyRelationships(storePath, result.PublishedPackageId ?? bindingAfter!.PackageId);
        var rootNodesAfter = await CountLocationRootNodesAsync(storePath, result.PublishedPackageId ?? bindingAfter!.PackageId);
        var report = new
        {
            schemaVersion = 1,
            scenario = "grid.direct-knowledge-rebuild",
            staleReason = "registration-engine.v2 and location-hierarchy source catalog v2 (11 reference edges)",
            gameId = gameId.Value,
            installationId = installationId.Value,
            profileId = profileId.Value,
            previousPackageId = result.PreviousPackageId?.Value,
            publishedPackageId = result.PublishedPackageId?.Value,
            mode = result.Mode.ToString(),
            status = result.Status.ToString(),
            detail = result.Detail,
            catalogStoreSha256Before = storeShaBefore,
            catalogStoreSha256After = storeShaAfter,
            catalogRollbackPath = result.CatalogRollbackPath,
            bindingRollbackPath = result.BindingRollbackPath,
            admittedLocationRelationships = result.AdmittedLocationRelationships,
            rejectedLocationRelationships = result.RejectedLocationRelationships,
            referenceHierarchyRelationshipCountBefore = relationshipCountBefore,
            referenceHierarchyRelationshipCountAfter = relationshipCountAfter,
            locationRootNodesBefore = rootNodesBefore,
            locationRootNodesAfter = rootNodesAfter,
            receiptPath = result.ReceiptPath,
            completedUtc = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Direct knowledge rebuild {result.Status}: {reportPath}");
        return result.Status == RegistrationRefreshStatus.Completed && result.Mode == RegistrationRefreshMode.KnowledgeRebuild ? 0 : 1;
    }

    private static int CountReferenceHierarchyRelationships(string storePath, CatalogPackageId packageId)
    {
        var loaded = new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync().GetAwaiter().GetResult();
        var package = loaded.Snapshot.FindImportedPackage(packageId);
        if (package is null) return 0;
        return package.Payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.ContainedBy &&
            value.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal) &&
            value.ResolvedTargetKnowledgeRecordId is not null);
    }

    private static async Task<int> CountLocationRootNodesAsync(string storePath, CatalogPackageId packageId)
    {
        var loaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync().ConfigureAwait(false);
        var composition = new CatalogCompositionId("grid.runtime-catalog-composition.v1.sha256." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("grid.runtime-catalog-composition.v1\0" + packageId.Value))).ToLowerInvariant());
        var package = loaded.Snapshot.FindImportedPackage(packageId)
            ?? throw new InvalidDataException($"Package {packageId.Value} is not imported (catalog valid={loaded.IsValid}).");
        var applicability = new CanonicalApplicabilityProjection(
            composition,
            package.Payload.KnowledgeRecords.Select(value => value.Id).OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray(),
            [],
            "grid.runtime-applicability.v1");
        var input = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
            loaded,
            packageId,
            composition,
            CanonicalSelectorProjectionPolicy.LocationPrepared,
            applicability);
        var locale = new CanonicalTerminologyLocalePreference("en-US", []);
        var selector = CanonicalSelectorProjectionEngine.Query(
            input,
            new CanonicalSelectorQuery(
                input.CatalogRevisionId,
                composition,
                KnowledgeKind.Location,
                input.Policy.Id,
                input.Policy.ExactVersion,
                null,
                null,
                false,
                false,
                locale));
        return selector.ImmediateChildren.Length;
    }

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
