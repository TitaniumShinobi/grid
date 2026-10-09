using System.Text.Json;
using Grid.Core.Services;

internal static class GameRegistrationInspectionContractChecks
{
    public static int Run()
    {
        var checks = 0;
        var mold = CanonicalRegistrationChecklist.Load();
        Assert(mold.Version == "1" && mold.Nodes.Length == 98,
            "Frozen DIF mold remains version 1 with exactly 98 nodes.");
        checks++;
        Assert(mold.Nodes.Where(node => node.ParentId is null).Select(node => node.Id)
                .SequenceEqual(["Tool", "Mod", "Location", "MissionQuest", "Item", "Actor"]),
            "Frozen DIF selector roots are unchanged.");
        checks++;

        using var inspection = JsonDocument.Parse(File.ReadAllBytes(FindInspectionContract()));
        var root = inspection.RootElement;
        Assert(root.GetProperty("schemaVersion").GetInt32() == 1, "Inspection schemaVersion is 1.");
        Assert(root.GetProperty("contractId").GetString() == "grid.game-registration-inspection.v1",
            "Inspection contractId is stable.");
        Assert(root.GetProperty("status").GetString() == "Proposed", "Inspection contract remains Proposed.");
        Assert(root.GetProperty("loadedByCanonicalRegistrationChecklist").GetBoolean() is false,
            "Inspection contract is not loaded by CanonicalRegistrationChecklist.");
        Assert(root.GetProperty("authority").GetProperty("publicationState").GetString() == "NOT_PUBLISHED",
            "Inspection contract preserves NOT_PUBLISHED.");
        Assert(root.GetProperty("authority").GetProperty("difMold").GetProperty("requiredNodeCount").GetInt32() == 98,
            "Inspection contract does not replace the 98-node DIF mold.");
        checks += 6;

        var categories = root.GetProperty("categories").EnumerateArray().ToArray();
        Assert(categories.Length == 21, "Inspection checklist enumerates 21 categories.");
        for (var index = 0; index < categories.Length; index++)
        {
            var expected = $"GR-{index + 1:00}";
            Assert(categories[index].GetProperty("id").GetString() == expected, "Category order is GR-01 through GR-21.");
            var coverage = categories[index].GetProperty("coverage").GetString();
            Assert(coverage is "Partial" or "Missing",
                expected + " uses Partial or Missing in v1; Implemented inspection completeness is not claimed.");
        }
        checks += 22;
        Assert(root.GetProperty("coverageRule").GetString()?.Contains("game-agnostic completeness", StringComparison.Ordinal) is true,
            "Coverage rule keeps game-specific coverage distinct from game-agnostic completeness.");
        checks++;

        Console.WriteLine($"PASS: {checks} game-registration inspection contract checks; DIF mold 98; NOT_PUBLISHED.");
        return checks;
    }

    private static string FindInspectionContract()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "src", "Grid.Core", "Contracts",
                    "game-registration-inspection.v1.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException(
            "game-registration-inspection.v1.json was not found from the current directory or AppContext.BaseDirectory.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
