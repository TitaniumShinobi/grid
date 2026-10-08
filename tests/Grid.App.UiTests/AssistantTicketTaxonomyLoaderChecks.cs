using System.IO;
using System.Reflection;
using Grid.App.Composition;
using Grid.Core.Models;

namespace Grid.App.UiTests;

internal static class AssistantTicketTaxonomyLoaderChecks
{
    public static void Run()
    {
        var loader = typeof(GridCompositionRoot).Assembly.GetType(
            "Grid.App.Services.AssistantTicketTaxonomyLoader", throwOnError: true)!;
        var defaultLoad = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException("Investigation taxonomy loader has no packaged Load boundary.");
        var loadPath = loader.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: [typeof(string)], modifiers: null)
            ?? throw new InvalidOperationException("Investigation taxonomy loader has no strict path boundary.");

        var taxonomy = (AssistantTicketTaxonomy)(defaultLoad.Invoke(null, null)
            ?? throw new InvalidOperationException("Investigation taxonomy loader returned no vocabulary."));
        RequireExactCanary(taxonomy);
        if (!taxonomy.ReferenceContexts.IsEmpty)
            throw new InvalidOperationException("Minimal investigation vocabulary must not manufacture reference knowledge.");

        ExpectRejected(loadPath, """
            {"schemaVersion":1,"taxonomyId":"grid.investigation-taxonomy.minimal-canary","taxonomyVersion":"1.0.0",
             "problems":[{"problemId":"grid.problem.crash","classId":"grid.class.wrong","displayName":"Crash"}],
             "timings":[{"timingId":"grid.timing.after-leaving-activity","classId":"grid.class.crash-freeze","displayName":"After leaving an activity"}],
             "goals":[{"goalId":"grid.goal.identify-evidence-backed-cause","displayName":"Identify evidence-backed cause"}]}
            """);
        ExpectRejected(loadPath, """
            {"schemaVersion":1,"taxonomyId":"grid.investigation-taxonomy.minimal-canary","taxonomyVersion":"1.0.0",
             "problems":[
               {"problemId":"grid.problem.crash","classId":"grid.class.crash-freeze","displayName":"Crash"},
               {"problemId":"grid.problem.crash","classId":"grid.class.crash-freeze","displayName":"Crash"}],
             "timings":[{"timingId":"grid.timing.after-leaving-activity","classId":"grid.class.crash-freeze","displayName":"After leaving an activity"}],
             "goals":[{"goalId":"grid.goal.identify-evidence-backed-cause","displayName":"Identify evidence-backed cause"}]}
            """);
        ExpectRejected(loadPath, """
            {"schemaVersion":1,"taxonomyId":"grid.investigation-taxonomy.minimal-canary","taxonomyVersion":"1.0.0",
             "gameId":"game.grandtheftautov-enhanced",
             "problems":[],"timings":[],"goals":[]}
            """);
    }

    private static void RequireExactCanary(AssistantTicketTaxonomy taxonomy)
    {
        if (taxonomy.Problems is not
                [{ Id.Value: "grid.problem.crash", ClassId.Value: "grid.class.crash-freeze", DisplayName: "Crash" }] ||
            taxonomy.Timings is not
                [{ Id.Value: "grid.timing.after-leaving-activity", ClassId.Value: "grid.class.crash-freeze", DisplayName: "After leaving an activity" }] ||
            taxonomy.Goals is not
                [{ Id.Value: "grid.goal.identify-evidence-backed-cause", DisplayName: "Identify evidence-backed cause" }])
            throw new InvalidOperationException("The packaged investigation vocabulary does not match the approved canary values.");
    }

    private static void ExpectRejected(MethodInfo loadPath, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-taxonomy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ticket-taxonomy.v1.json");
        try
        {
            File.WriteAllText(path, content);
            try
            {
                _ = loadPath.Invoke(null, [path]);
                throw new InvalidOperationException("An invalid investigation taxonomy was accepted.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
            }
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
