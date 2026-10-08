using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

/// <summary>
/// Loads GRID-owned investigation vocabulary. This boundary is deliberately
/// separate from game/mod canonical knowledge and carries no game evidence.
/// </summary>
internal static class AssistantTicketTaxonomyLoader
{
    private const string TaxonomyId = "grid.investigation-taxonomy.minimal-canary";
    private const string TaxonomyVersion = "1.0.0";
    private const string CrashClassId = "grid.class.crash-freeze";

    public static AssistantTicketTaxonomy Load() => Load(
        Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts", "health", "ticket-taxonomy.v1.json"));

    internal static AssistantTicketTaxonomy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            RequireObjectProperties(root,
                "schemaVersion", "taxonomyId", "taxonomyVersion", "problems", "timings", "goals");
            if (RequiredInt32(root, "schemaVersion") != 1 ||
                !StringComparer.Ordinal.Equals(RequiredString(root, "taxonomyId"), TaxonomyId) ||
                !StringComparer.Ordinal.Equals(RequiredString(root, "taxonomyVersion"), TaxonomyVersion))
            {
                throw new InvalidDataException("The packaged investigation taxonomy has an unsupported identity or version.");
            }

            var problems = RequiredArray(root, "problems").Select(value =>
            {
                RequireObjectProperties(value, "problemId", "classId", "displayName");
                return new AssistantProblemOption(
                    new TicketProblemId(RequiredString(value, "problemId")),
                    new TicketClassId(RequiredString(value, "classId")),
                    RequiredString(value, "displayName"));
            }).ToImmutableArray();
            var timings = RequiredArray(root, "timings").Select(value =>
            {
                RequireObjectProperties(value, "timingId", "classId", "displayName");
                return new AssistantTimingOption(
                    new TicketTimingId(RequiredString(value, "timingId")),
                    new TicketClassId(RequiredString(value, "classId")),
                    RequiredString(value, "displayName"));
            }).ToImmutableArray();
            var goals = RequiredArray(root, "goals").Select(value =>
            {
                RequireObjectProperties(value, "goalId", "displayName");
                return new AssistantGoalOption(
                    new TicketGoalId(RequiredString(value, "goalId")),
                    RequiredString(value, "displayName"));
            }).ToImmutableArray();

            RequireUnique(problems.Select(value => value.Id.Value), "Problem");
            RequireUnique(timings.Select(value => value.Id.Value), "Timing");
            RequireUnique(goals.Select(value => value.Id.Value), "Goal");
            if (problems is not [{ Id.Value: "grid.problem.crash", ClassId.Value: CrashClassId, DisplayName: "Crash" }] ||
                timings is not [{ Id.Value: "grid.timing.after-leaving-activity", ClassId.Value: CrashClassId, DisplayName: "After leaving an activity" }] ||
                goals is not [{ Id.Value: "grid.goal.identify-evidence-backed-cause", DisplayName: "Identify evidence-backed cause" }])
            {
                throw new InvalidDataException("The packaged investigation taxonomy is not the approved minimal canary vocabulary.");
            }

            return new AssistantTicketTaxonomy(problems, timings, goals, []);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The packaged investigation taxonomy is not valid strict JSON.", exception);
        }
    }

    private static IEnumerable<JsonElement> RequiredArray(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The packaged investigation taxonomy is missing array '{propertyName}'.");
        return property.EnumerateArray();
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The packaged investigation taxonomy is missing string '{propertyName}'.");
        return property.GetString()!;
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var result))
            throw new InvalidDataException($"The packaged investigation taxonomy is missing integer '{propertyName}'.");
        return result;
    }

    private static void RequireObjectProperties(JsonElement value, params string[] expectedNames)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The packaged investigation taxonomy contains a non-object value.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Length != expectedNames.Length ||
            expectedNames.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("The packaged investigation taxonomy contains missing, duplicate, or unsupported fields.");
    }

    private static void RequireUnique(IEnumerable<string> identities, string kind)
    {
        var values = identities.ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException($"The packaged investigation taxonomy contains duplicate {kind} identities.");
    }
}
