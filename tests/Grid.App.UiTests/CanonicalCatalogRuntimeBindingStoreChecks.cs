using System.IO;
using System.Text.Json;
using Grid.App.Composition;
using Grid.App.Services;
using Grid.Core.Models;

namespace Grid.App.UiTests;

internal static class CanonicalCatalogRuntimeBindingStoreChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "grid-catalog-binding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "canonical-runtime-binding.v1.json");
            var store = new CanonicalCatalogRuntimeBindingStore(path);
            if (store.Load() is not null)
                throw new InvalidOperationException("An absent durable catalog binding did not remain absent.");

            Write(path, new
            {
                schemaVersion = 1,
                catalogStoreFileName = "shared-canonical-library.v5.json",
                allowCandidatePackages = true,
                packageId = "grid.catalog-package.v6.sha256." + new string('a', 64),
            });
            var binding = store.Load() ?? throw new InvalidOperationException("The valid durable catalog binding did not load.");
            if (binding.CatalogStoreFileName != CanonicalCatalogRuntimeBindingStore.DefaultCatalogStoreFileName ||
                !binding.AllowCandidatePackages ||
                binding.PackageId.Value != "grid.catalog-package.v6.sha256." + new string('a', 64))
                throw new InvalidOperationException("The durable catalog binding changed its exact registered coordinates.");

            Write(path, new
            {
                schemaVersion = 1,
                catalogStoreFileName = "shared-canonical-library.v5.json",
                allowCandidatePackages = true,
                packageId = "grid.catalog-package.v7.sha256." + new string('b', 64),
            });
            var v7Binding = store.Load() ??
                throw new InvalidOperationException("The valid package-v7 durable catalog binding did not load.");
            if (v7Binding.PackageId.Value != "grid.catalog-package.v7.sha256." + new string('b', 64))
                throw new InvalidOperationException("The durable catalog binding changed the package-v7 identity.");

            Write(path, new
            {
                schemaVersion = 1,
                catalogStoreFileName = "shared-canonical-library.v5.json",
                allowCandidatePackages = true,
                packageId = "grid.catalog-package.v6.sha256." + new string('a', 64),
            });

            var firstAccountRoot = GridAccountDataScope.Resolve(root, "catalog-binding-test-first");
            var secondAccountRoot = GridAccountDataScope.Resolve(root, "catalog-binding-test-second");
            var catalogsRoot = Path.Combine(root, "catalogs");
            Directory.CreateDirectory(catalogsRoot);
            var durablePath = Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json");
            File.Copy(path, durablePath, overwrite: true);
            var decoyCatalogsRoot = Path.Combine(firstAccountRoot, "catalogs");
            Directory.CreateDirectory(decoyCatalogsRoot);
            Write(Path.Combine(decoyCatalogsRoot, "canonical-runtime-binding.v1.json"), new
            {
                schemaVersion = 1,
                catalogStoreFileName = "shared-canonical-library.v5.json",
                allowCandidatePackages = false,
                packageId = "grid.catalog-package.v7.sha256." + new string('c', 64),
            });
            var priorDataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
            var priorCatalogPath = Environment.GetEnvironmentVariable("GRID_CANONICAL_CATALOG_PATH");
            var priorCandidate = Environment.GetEnvironmentVariable("GRID_CANONICAL_ALLOW_CANDIDATE");
            var priorPackage = Environment.GetEnvironmentVariable("GRID_CANONICAL_PACKAGE_ID");
            try
            {
                Environment.SetEnvironmentVariable("GRID_DATA_ROOT", root);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_CATALOG_PATH", null);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_ALLOW_CANDIDATE", null);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_PACKAGE_ID", null);
                var firstComposition = GridCompositionRoot.CreateProduction("catalog-binding-test-first");
                var secondComposition = GridCompositionRoot.CreateProduction("catalog-binding-test-second");
                foreach (var composition in new[] { firstComposition, secondComposition })
                {
                    var runtime = composition.CanonicalCatalogRuntimeService
                        ?? throw new InvalidOperationException("Production did not compose the canonical runtime service.");
                    if (runtime.StorePath != Path.Combine(catalogsRoot, "shared-canonical-library.v5.json") ||
                        !runtime.AllowsCandidatePackages || runtime.PinnedPackageId != binding.PackageId)
                        throw new InvalidOperationException(
                            "Both accounts must consume the shared base catalog path, Candidate decision, and package pin, ignoring account-local bindings.");
                    if (!runtime.UsesPreparedNavigation || !runtime.IsReadinessUnresolved)
                        throw new InvalidOperationException("Normal production must open prepared navigation and preserve pending readiness.");
                    runtime.LoadAsync(new GridCatalogSnapshot("prepared.missing", CatalogSourceKind.Adapter, []))
                        .GetAwaiter().GetResult();
                    if (runtime.RuntimeSourceCatalogLoads != 0 || !runtime.IsReadinessUnresolved ||
                        runtime.Match(new GameId("game.binding-first"), null, null).State != CanonicalRuntimeMatchState.Unavailable)
                        throw new InvalidOperationException("Missing preparation must fail closed without loading JSON or invalidating pending selections.");
                }
                if (Directory.Exists(Path.Combine(secondAccountRoot, "catalogs")))
                    throw new InvalidOperationException(
                        "Production composition created an account-local canonical catalog directory.");

                var firstSelection = new WorkspaceSelection(new GameId("game.binding-first"), null, null);
                var secondSelection = new WorkspaceSelection(new GameId("game.binding-second"), null, null);
                var firstWorkspace = firstComposition.WorkspaceSelectionStore
                    ?? throw new InvalidOperationException("First account workspace store was not composed.");
                var secondWorkspace = secondComposition.WorkspaceSelectionStore
                    ?? throw new InvalidOperationException("Second account workspace store was not composed.");
                if (!firstWorkspace.Save(firstSelection) || secondWorkspace.Load() is not null ||
                    !secondWorkspace.Save(secondSelection) || firstWorkspace.Load() != firstSelection ||
                    secondWorkspace.Load() != secondSelection ||
                    !File.Exists(Path.Combine(firstAccountRoot, "workspace", "selection.v1.json")) ||
                    !File.Exists(Path.Combine(secondAccountRoot, "workspace", "selection.v1.json")) ||
                    File.Exists(Path.Combine(root, "workspace", "selection.v1.json")))
                    throw new InvalidOperationException("Workspace selections did not remain isolated in their respective account roots.");

                var firstRun = firstComposition.FirstRunStateStore
                    ?? throw new InvalidOperationException("First account first-run store was not composed.");
                var secondRun = secondComposition.FirstRunStateStore
                    ?? throw new InvalidOperationException("Second account first-run store was not composed.");
                if (!firstRun.MarkComplete() || !firstRun.IsComplete() || secondRun.IsComplete() ||
                    !File.Exists(Path.Combine(firstAccountRoot, "setup", "first-run.v1.json")))
                    throw new InvalidOperationException("First-run state did not remain isolated in its account root.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GRID_DATA_ROOT", priorDataRoot);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_CATALOG_PATH", priorCatalogPath);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_ALLOW_CANDIDATE", priorCandidate);
                Environment.SetEnvironmentVariable("GRID_CANONICAL_PACKAGE_ID", priorPackage);
            }

            AssertRejected(path, """
                {"schemaVersion":1,"catalogStoreFileName":"..\\shared-canonical-library.v5.json","allowCandidatePackages":true,"packageId":"grid.catalog-package.v6.sha256.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
                """, "A traversal-shaped catalog coordinate was accepted.");
            AssertRejected(path, """
                {"schemaVersion":1,"catalogStoreFileName":"shared-canonical-library.v5.json","allowCandidatePackages":true,"packageId":"grid.catalog-package.v6.sha256.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","gameId":"game.grandtheftautov-enhanced"}
                """, "A game-specific binding field was accepted.");
            AssertRejected(path, """
                {"schemaVersion":1,"catalogStoreFileName":"shared-canonical-library.v5.json","allowCandidatePackages":true,"packageId":"not-a-package"}
                """, "A malformed package identity was accepted.");
            AssertRejected(path, """
                {"schemaVersion":1,"schemaVersion":1,"catalogStoreFileName":"shared-canonical-library.v5.json","allowCandidatePackages":true,"packageId":"grid.catalog-package.v6.sha256.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
                """, "A duplicate binding field was accepted.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value));

    private static void AssertRejected(string path, string json, string message)
    {
        File.WriteAllText(path, json);
        try
        {
            _ = new CanonicalCatalogRuntimeBindingStore(path).Load();
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
