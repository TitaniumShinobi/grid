using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.UiTests;

/// <summary>
/// Headless lifecycle checks using a tiny verified Core fixture. Reflection seeds only the
/// already-matched handle boundary, deliberately avoiding Steam, UI and installed stores.
/// Real LoadAsync still performs generation invalidation through an absent scratch store.
/// </summary>
internal static class CanonicalCatalogRuntimePerformanceContractChecks
{
    public static async Task<int> RunAsync(CanonicalSelectorProjectionInput input, string scratchDirectory)
    {
        Directory.CreateDirectory(scratchDirectory);
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var storePath = Path.Combine(scratchDirectory, "absent-runtime-contract-store.json");
        if (File.Exists(storePath)) throw new InvalidOperationException("The lifecycle fixture requires an absent store.");
        var runtime = new CanonicalCatalogRuntimeService(storePath, new("en-US", ["en"]), true, input.VerifiedPackage.Id, usePreparedNavigation: false);
        var match = new CanonicalRuntimeMatch(CanonicalRuntimeMatchState.Exact, "Synthetic verified handle",
            input.VerifiedPackage.Id, input.CatalogRevisionId, 1, input.CatalogCompositionId,
            input.VerifiedPackage.Manifest.GameScope.ExactGameVersion,
            input.VerifiedPackage.Manifest.ValidationStatus, input);
        var handlesField = typeof(CanonicalCatalogRuntimeService).GetField("currentExactMatches",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Runtime handle boundary changed.");
        var handles = (HashSet<CanonicalRuntimeMatch>)handlesField.GetValue(runtime)!;
        handles.Add(match);
        var roots = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => runtime.Query(match, KnowledgeKind.Location))));
        Assert(roots.All(value => ReferenceEquals(value, roots[0])),
            "Concurrent same-path runtime queries share an immutable result.");
        var root = roots[0];
        var parent = root.ImmediateChildren.First(value => value.CanDescend);
        var child = runtime.Query(match, KnowledgeKind.Location, parent.PathId);
        Assert(ReferenceEquals(root, runtime.Query(match, KnowledgeKind.Location)) &&
               ReferenceEquals(child, runtime.Query(match, KnowledgeKind.Location, parent.PathId)),
            "Root reopening and nested reopening reuse frozen results.");
        var selectedNode = child.ImmediateChildren.First(value => value.IsSelectable);
        var selection = CanonicalSelectorProjectionEngine.Select(child, selectedNode.PathId);
        Assert(runtime.ValidateSelection(match, selection), "An emitted path and record pair remains valid.");
        CanonicalSelectorSelection Alter(KnowledgeRecordId? record = null, CatalogCompositionId? composition = null) =>
            new(selection.SelectionKind, selection.KnowledgeKind, selection.CatalogRevisionId,
                composition ?? selection.CatalogCompositionId, selection.ProjectionPolicyId,
                selection.ProjectionPolicyVersion, selection.SelectedPathId,
                record ?? selection.KnowledgeRecordId, null);
        Assert(!runtime.ValidateSelection(match, Alter(record:
                new KnowledgeRecordId("grid.knowledge-record.v1.sha256." + new string('0', 64)))),
            "A forged record identity cannot reuse a real selected path.");
        Assert(!runtime.ValidateSelection(match, Alter(composition: new CatalogCompositionId("composition.wrong"))),
            "A selection from another composition is rejected.");
        var forgedMatch = match with { };
        Assert(!runtime.ValidateSelection(forgedMatch, selection),
            "A value-equal runtime match cannot borrow the live reference handle.");
        try { _ = runtime.Query(forgedMatch, KnowledgeKind.Location); }
        catch (InvalidOperationException) { checks++; goto ForgedRejected; }
        throw new InvalidOperationException("Query accepted a copied runtime match handle.");

        ForgedRejected:
        using var graphReached = new ManualResetEventSlim();
        using var releaseGraph = new ManualResetEventSlim();
        using var observer = CanonicalRuntimeDiagnostics.Observe(stage =>
        {
            if (stage.Stage != "projection.graph-build") return;
            graphReached.Set();
            if (!releaseGraph.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The bounded reload-race test did not release graph construction.");
        });
        var racingQuery = Task.Run(() => runtime.Query(match, KnowledgeKind.Actor));
        try
        {
            Assert(graphReached.Wait(TimeSpan.FromSeconds(30)), "The uncached query reached graph construction.");
            await runtime.LoadAsync(new GridCatalogSnapshot("runtime.contract.reload", CatalogSourceKind.Adapter, []));
        }
        finally { releaseGraph.Set(); }
        try { _ = await racingQuery; }
        catch (InvalidOperationException) { checks++; goto StaleRejected; }
        throw new InvalidOperationException("A query completed successfully against a replaced runtime generation.");

        StaleRejected:
        Assert(!runtime.ValidateSelection(match, selection), "Reload invalidates previously emitted selections.");
        try { _ = runtime.Query(match, KnowledgeKind.Location); }
        catch (InvalidOperationException) { checks++; goto ReloadRejected; }
        throw new InvalidOperationException("Reload left an old match queryable.");

        ReloadRejected:
        Assert(runtime.Match(new GameId("game.selector-fixture"), null, null).State == CanonicalRuntimeMatchState.MissingCatalog,
            "The public empty-store reload publishes a fail-closed missing catalog state.");
        Assert(!File.Exists(storePath), "The read-only runtime lifecycle creates no canonical store.");
        return checks;
    }
}
