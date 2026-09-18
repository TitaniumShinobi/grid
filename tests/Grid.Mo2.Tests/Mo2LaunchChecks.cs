using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2LaunchCheckResult(string Name, Exception? Failure);

static class Mo2LaunchChecks
{
    public static async Task<ImmutableArray<Mo2LaunchCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2LaunchCheckResult>();
        await Run(results, "builder emits typed portable global and exact-profile run arguments", BuilderAsync);
        await Run(results, "builder rejects utilities duplicates and control characters", RejectionAsync);
        await Run(results, "launch uses fake runner and reports only MO2 lifecycle", LaunchAsync);
        await Run(results, "running or indeterminate MO2 blocks launch", RunningAsync);
        await Run(results, "inactive ambiguous and unsupported-version evidence blocks launch", PolicyBlocksAsync);
        await Run(results, "nonzero MO2 result is rejection and approval is one use", ResultAndApprovalAsync);
        return results.ToImmutable();
    }

    private static Task BuilderAsync()
    {
        var builder = new Mo2LaunchInvocationBuilder();
        var portable = new Mo2LaunchFixtureBuilder();
        var first = builder.Build(portable.Reference, portable.Validation, portable.Entry, new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"));
        SequenceEqual(new[] { "run", "-e", "SKSE" }, first.Arguments);
        var global = new Mo2LaunchFixtureBuilder(Mo2InstanceKind.Global);
        var second = builder.Build(global.Reference, global.Validation, global.Entry, new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"));
        SequenceEqual(new[] { "-i", "Global Instance", "run", "-e", "SKSE" }, second.Arguments);
        var profile = builder.BuildForProfile(portable.Reference, portable.Validation, portable.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"), "Grid Runtime Fixture");
        SequenceEqual(new[] { "-p", "Grid Runtime Fixture", "run", "-e", "SKSE" }, profile.Arguments);
        var globalProfile = builder.BuildForProfile(global.Reference, global.Validation, global.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"), "Grid Runtime Fixture");
        SequenceEqual(new[] { "-i", "Global Instance", "-p", "Grid Runtime Fixture", "run", "-e", "SKSE" }, globalProfile.Arguments);
        False(first.Arguments.Any(value => value is "-p" or "-a" or "-c" or "--multiple" or "launch"));
        var hostileButRepresentable = new Mo2LaunchFixtureBuilder(title: "SKSE Ω & | < > ^ % ! ' \" spaced");
        var third = builder.Build(hostileButRepresentable.Reference, hostileButRepresentable.Validation, hostileButRepresentable.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"));
        Equal(hostileButRepresentable.Entry.Title, third.Arguments[2]);
        return Task.CompletedTask;
    }

    private static async Task RejectionAsync()
    {
        var builder = new Mo2LaunchInvocationBuilder();
        var utility = new Mo2LaunchFixtureBuilder(title: "BodySlide");
        await Throws(() => Task.FromResult(builder.Build(utility.Reference, utility.Validation, utility.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"))));
        var hostile = new Mo2LaunchFixtureBuilder(title: "SKSE\nwhoami");
        await Throws(() => Task.FromResult(builder.Build(hostile.Reference, hostile.Validation, hostile.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"))));
        var portable = new Mo2LaunchFixtureBuilder();
        await Throws(() => Task.FromResult(builder.BuildForProfile(portable.Reference, portable.Validation, portable.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"), "..")));
        await Throws(() => Task.FromResult(builder.BuildForProfile(portable.Reference, portable.Validation, portable.Entry,
            new(Mo2VersionEvidenceStatus.Supported, new(2, 5, 2), "id", "ok"), "Grid/Unsafe")));
    }

    private static async Task LaunchAsync()
    {
        var fixture = new Mo2LaunchFixtureBuilder();
        var audit = (await new Mo2FidelityAuditService().RunAsync(Context(fixture))).Snapshot!;
        var intent = Intent(fixture, audit);
        var runner = new FakeProcessRunner();
        var service = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(fixture.Evidence),
            new(), new FakeVersionReader(), new FakeProcessProbe(), runner, _ => Task.FromResult("refreshed"));
        var prepared = (await service.PrepareAsync(intent, audit)).Preparation!;
        var approval = new ExternalLaunchApproval(new("approval.mo2.one"), prepared.SessionId, prepared.PreparationFingerprint,
            audit.Fingerprint, DateTimeOffset.UtcNow, true, true);
        var result = await service.LaunchAsync(prepared, approval);
        Equal(ExternalLaunchSessionStatus.Completed, result.Status);
        SequenceEqual(new[] { "run", "-e", "SKSE" }, runner.Invocation!.Arguments);
        Equal(fixture.Reference.ExecutablePath, runner.Invocation.ExecutablePath);
        NotEqual(fixture.Entry.Binary.CanonicalPath!, runner.Invocation.ExecutablePath);
        False(result.Session!.Events.Any(item => item.Kind is ExternalLaunchEventKind.ConfiguredProcessStarted or ExternalLaunchEventKind.ConfiguredProcessExited));
        True(result.Session.Events.All(item => !item.ConfiguredProcessLifecycleObserved));
    }

    private static async Task RunningAsync()
    {
        var fixture = new Mo2LaunchFixtureBuilder();
        var audit = (await new Mo2FidelityAuditService().RunAsync(Context(fixture))).Snapshot!;
        foreach (var status in new[] { Mo2ProcessProbeStatus.Running, Mo2ProcessProbeStatus.Indeterminate })
        {
            var service = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(fixture.Evidence),
                new(), new FakeVersionReader(), new FakeProcessProbe(status), new FakeProcessRunner());
            var result = await service.PrepareAsync(Intent(fixture, audit), audit);
            Equal(ExternalLaunchPreparationStatus.Blocked, result.Status);
        }
    }

    private static async Task PolicyBlocksAsync()
    {
        var fixture = new Mo2LaunchFixtureBuilder();
        var audit = (await new Mo2FidelityAuditService().RunAsync(Context(fixture))).Snapshot!;
        var intent = Intent(fixture, audit);
        var inactive = fixture.Evidence with { ActiveProfileState = ManagerProfileState.Inactive };
        var inactiveService = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(inactive),
            new(), new FakeVersionReader(), new FakeProcessProbe(), new FakeProcessRunner());
        Equal(ExternalLaunchPreparationStatus.Blocked, (await inactiveService.PrepareAsync(intent, audit)).Status);

        var duplicateEntry = fixture.Entry with { IsDuplicate = true };
        var duplicate = fixture.Evidence with
        {
            SelectedExecutable = duplicateEntry,
            Executables = fixture.Executables with { Entries = [duplicateEntry] },
        };
        var duplicateService = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(duplicate),
            new(), new FakeVersionReader(), new FakeProcessProbe(), new FakeProcessRunner());
        Equal(ExternalLaunchPreparationStatus.Blocked, (await duplicateService.PrepareAsync(intent, audit)).Status);

        var oldService = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(fixture.Evidence),
            new(), new FakeVersionReader(Mo2VersionEvidenceStatus.TooOld), new FakeProcessProbe(), new FakeProcessRunner());
        Equal(ExternalLaunchPreparationStatus.Blocked, (await oldService.PrepareAsync(intent, audit)).Status);
    }

    private static async Task ResultAndApprovalAsync()
    {
        var fixture = new Mo2LaunchFixtureBuilder();
        var audit = (await new Mo2FidelityAuditService().RunAsync(Context(fixture))).Snapshot!;
        var service = new Mo2LaunchService((_, _) => Task.FromResult<Mo2LaunchEvidence?>(fixture.Evidence),
            new(), new FakeVersionReader(), new FakeProcessProbe(), new FakeProcessRunner(exitCode: 7));
        var preparation = (await service.PrepareAsync(Intent(fixture, audit), audit)).Preparation!;
        var approval = new ExternalLaunchApproval(new("approval.mo2.nonzero"), preparation.SessionId,
            preparation.PreparationFingerprint, audit.Fingerprint, DateTimeOffset.UtcNow, true, true);
        var first = await service.LaunchAsync(preparation, approval);
        Equal(ExternalLaunchSessionStatus.Rejected, first.Status);
        Equal(7, first.Session!.Events.Single(item => item.Kind == ExternalLaunchEventKind.ManagerInvocationExited).ExitCode!.Value);
        var second = await service.LaunchAsync(preparation, approval);
        Equal(ExternalLaunchSessionStatus.Rejected, second.Status);
    }

    private static ExternalLaunchIntent Intent(Mo2LaunchFixtureBuilder fixture, FidelityAuditSnapshot audit) => new(
        fixture.Reference.GameId, fixture.Reference.InstallationId, audit.Context.ProfileId, fixture.Reference.Id,
        audit.Context.SelectedExecutableId!.Value, fixture.Entry.Fingerprint, audit.Id, audit.Fingerprint);
    private static FidelityAuditContext Context(Mo2LaunchFixtureBuilder fixture) => new(
        fixture.Reference.GameId, fixture.Reference.InstallationId, new("profile.mo2.launch"), fixture.Reference.Id,
        "catalog", "connection", "profile-fingerprint", "inventory", new("resolved.mo2.launch"), "environment",
        new("outputs.mo2.launch"), "outputs", new("executable.mo2.launch"), fixture.Entry.Fingerprint);
    private static async Task Run(ImmutableArray<Mo2LaunchCheckResult>.Builder results, string name, Func<Task> check)
    { try { await check(); results.Add(new(name, null)); } catch (Exception e) { results.Add(new(name, e)); } }
    private static async Task Throws(Func<Task> action) { try { await action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Expected rejection."); }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void NotEqual<T>(T expected, T actual) where T : notnull { if (EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException("Expected values to differ."); }
    private static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual) { if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) throw new InvalidOperationException("Argument sequence differs."); }
}
