using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Services;

sealed record Mo2FidelityAuditCheckResult(string Name, Exception? Failure);

static class Mo2FidelityAuditChecks
{
    public static async Task<ImmutableArray<Mo2FidelityAuditCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2FidelityAuditCheckResult>();
        await Run(results, "audit fingerprint binds every selected-context identity", BindingAsync);
        await Run(results, "audit preserves discrepancy order and blocking rules", DiscrepanciesAsync);
        return results.ToImmutable();
    }

    private static async Task BindingAsync()
    {
        var service = new Mo2FidelityAuditService();
        var first = (await service.RunAsync(Mo2FidelityAuditFixtureBuilder.Context())).Snapshot!;
        var second = (await service.RunAsync(Mo2FidelityAuditFixtureBuilder.Context("changed"))).Snapshot!;
        NotEqual(first.Fingerprint, second.Fingerprint);
        Equal(FidelityAuditReadiness.Ready, first.Readiness);
        True(first.Items.Any(item => item.Area == FidelityAuditArea.NonMutation && item.Title == "No change observed"));
    }

    private static async Task DiscrepanciesAsync()
    {
        var evidence = ImmutableArray.Create(
            new FidelityAuditItem("warning-first", FidelityAuditArea.Mods, FidelityAuditItemStatus.Warning,
                FidelityAuditCriticality.LaunchRelevant, FidelityAuditDisposition.AcknowledgementRequired, "Warning", "Preserved"),
            new FidelityAuditItem("failure-second", FidelityAuditArea.Executables, FidelityAuditItemStatus.Failure,
                FidelityAuditCriticality.LaunchCritical, FidelityAuditDisposition.Blocking, "Failure", "Preserved"));
        var service = new Mo2FidelityAuditService((_, _) => Task.FromResult(evidence));
        var result = (await service.RunAsync(Mo2FidelityAuditFixtureBuilder.Context())).Snapshot!;
        Equal("warning-first", result.Items[0].Code);
        Equal("failure-second", result.Items[1].Code);
        Equal(FidelityAuditReadiness.Blocked, result.Readiness);
    }

    private static async Task Run(ImmutableArray<Mo2FidelityAuditCheckResult>.Builder results, string name, Func<Task> check)
    { try { await check(); results.Add(new(name, null)); } catch (Exception e) { results.Add(new(name, e)); } }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void NotEqual<T>(T left, T right) where T : notnull { if (EqualityComparer<T>.Default.Equals(left, right)) throw new InvalidOperationException("Expected values to differ."); }
}
