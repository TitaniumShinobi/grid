using System.Collections.Immutable;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ToolOutputCheckResult(string Name, Exception? Failure);

static class Mo2ToolOutputChecks
{
    public static async Task<ImmutableArray<Mo2ToolOutputCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ToolOutputCheckResult>();
        await RunAsync(results, "tool recognition requires corroboration", RecognitionAsync);
        await RunAsync(results, "overwrite and custom outputs are observed", OutputObservationAsync);
        await RunAsync(results, "output comparison detects external changes", ComparisonAsync);
        await RunAsync(results, "large output falls back to structural evidence", BoundedFingerprintAsync);
        await RunAsync(results, "observation is read-only", NonMutationAsync);
        await RunAsync(results, "cancellation is honored", CancellationAsync);
        return results.ToImmutable();
    }

    private static Task RecognitionAsync()
    {
        var candidate = Mo2ToolRecognition.Recognize(new(
            new("observed.body"),
            "BodySlide",
            "renamed.exe",
            null,
            []));
        Equal(Mo2RecognizedToolFamily.BodySlide, candidate.Family);
        Equal(Mo2RecognitionConfidence.Candidate, candidate.Confidence);

        var confirmed = Mo2ToolRecognition.Recognize(new(
            new("observed.synthesis"),
            "Synthesis",
            "Synthesis.exe",
            null,
            []));
        Equal(Mo2RecognizedToolFamily.Synthesis, confirmed.Family);
        Equal(Mo2RecognitionConfidence.Corroborated, confirmed.Confidence);

        var filenameOnly = Mo2ToolRecognition.Recognize(new(
            new("observed.loot"),
            "Utility",
            "LOOT.exe",
            null,
            []));
        Equal(Mo2RecognitionConfidence.Candidate, filenameOnly.Confidence);
        return Task.CompletedTask;
    }

    private static async Task OutputObservationAsync()
    {
        using var fixture = await Mo2ToolOutputFixtureBuilder.CreateAsync();
        fixture.CreateProfile("+Synthesis Output\n+Meshes - BodySlide");
        var synthesis = fixture.CreateOutputMod("Synthesis Output");
        var body = fixture.CreateOutputMod("Meshes - BodySlide");
        fixture.WriteFile(synthesis, "Synthesis.esp", "generated");
        fixture.WriteFile(body, "meshes\\actors\\body.nif", "mesh");
        fixture.WriteFile(fixture.OverwriteRoot, "SKSE\\Plugins\\generated.log", "overwrite");
        var request = await fixture.RequestAsync(
            [
                Mo2ToolOutputFixtureBuilder.Tool("observed.synthesis", "Synthesis", Mo2RecognizedToolFamily.Synthesis),
                Mo2ToolOutputFixtureBuilder.Tool("observed.body", "BodySlide x64", Mo2RecognizedToolFamily.BodySlide),
            ],
            [new("Synthesis", "Synthesis Output", 1, 10)]);

        var result = await fixture.CreateService().ObserveAsync(request);
        Equal(Mo2ToolOutputObservationStatus.Complete, result.Status);
        Equal(3, result.Outputs.Length);
        var overwrite = result.Outputs.Single(output => output.Kind == Mo2GeneratedOutputKind.Overwrite);
        Equal(Mo2OutputAssociationConfidence.ConfirmedByMo2, overwrite.AssociationConfidence);
        Equal(Mo2OutputFingerprintStrength.CompleteContent, overwrite.Fingerprint.Strength);
        var mapped = result.Outputs.Single(output => output.DisplayName == "Synthesis Output");
        Equal(Mo2OutputAssociationConfidence.ConfirmedByMo2, mapped.AssociationConfidence);
        Equal(Mo2GeneratedOutputKind.Synthesis, mapped.Kind);
        var inferred = result.Outputs.Single(output => output.DisplayName == "Meshes - BodySlide");
        Equal(Mo2OutputAssociationConfidence.Corroborated, inferred.AssociationConfidence);
        Equal(Mo2GeneratedOutputKind.BodySlide, inferred.Kind);
    }

    private static async Task ComparisonAsync()
    {
        using var fixture = await Mo2ToolOutputFixtureBuilder.CreateAsync();
        fixture.CreateProfile("+Synthesis Output");
        var output = fixture.CreateOutputMod("Synthesis Output");
        var file = fixture.WriteFile(output, "Synthesis.esp", "one");
        var request = await fixture.RequestAsync(
            [Mo2ToolOutputFixtureBuilder.Tool("observed.synthesis", "Synthesis", Mo2RecognizedToolFamily.Synthesis)],
            [new("Synthesis", "Synthesis Output", 1, 4)]);
        var cache = fixture.CreateCache();
        var first = await cache.RefreshAsync(request);
        True(first.Current.Outputs.All(value => value.Comparison == Mo2OutputComparisonState.FirstObservation));

        File.WriteAllText(file, "two-and-different");
        var second = await cache.RefreshAsync(request);
        Equal(
            Mo2OutputComparisonState.Modified,
            second.Current.Outputs.Single(value => value.DisplayName == "Synthesis Output").Comparison);
        var third = await cache.RefreshAsync(request);
        Equal(
            Mo2OutputComparisonState.NoChange,
            third.Current.Outputs.Single(value => value.DisplayName == "Synthesis Output").Comparison);
    }

    private static async Task BoundedFingerprintAsync()
    {
        using var fixture = await Mo2ToolOutputFixtureBuilder.CreateAsync();
        fixture.CreateProfile("+DynDOLOD Output");
        var output = fixture.CreateOutputMod("DynDOLOD Output");
        fixture.WriteFile(output, "large.bin", new string('x', 128));
        var request = await fixture.RequestAsync(
            [Mo2ToolOutputFixtureBuilder.Tool("observed.dyndolod", "DynDOLOD", Mo2RecognizedToolFamily.DynDoLod)],
            []);
        var limits = new Mo2ToolOutputObservationLimits(
            new(MaximumEntries: 100),
            MaximumContentHashBytes: 32,
            MaximumConcurrentReaders: 2,
            BufferSize: 16);
        var result = await fixture.CreateService(limits).ObserveAsync(request);
        var observed = result.Outputs.Single(value => value.DisplayName == "DynDOLOD Output");
        Equal(Mo2OutputFingerprintStrength.Structural, observed.Fingerprint.Strength);
        True(observed.Fingerprint.ContentFingerprint is null);
        True(result.Status == Mo2ToolOutputObservationStatus.Partial);
    }

    private static async Task NonMutationAsync()
    {
        using var fixture = await Mo2ToolOutputFixtureBuilder.CreateAsync();
        fixture.CreateProfile("+Pandora Output");
        var output = fixture.CreateOutputMod("Pandora Output");
        fixture.WriteFile(output, "meshes\\actors\\animation.hkx", "animation");
        fixture.WriteFile(fixture.OverwriteRoot, "readme.txt", "untouched");
        var before = Mo2ToolOutputFixtureBuilder.HashTree(fixture.Root);
        var request = await fixture.RequestAsync(
            [Mo2ToolOutputFixtureBuilder.Tool("observed.pandora", "Pandora", Mo2RecognizedToolFamily.Pandora)],
            []);
        _ = await fixture.CreateService().ObserveAsync(request);
        var after = Mo2ToolOutputFixtureBuilder.HashTree(fixture.Root);
        Equal(before, after);
    }

    private static async Task CancellationAsync()
    {
        using var fixture = await Mo2ToolOutputFixtureBuilder.CreateAsync();
        fixture.CreateProfile("+Output");
        fixture.CreateOutputMod("Output");
        var request = await fixture.RequestAsync([], []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() =>
            fixture.CreateService().ObserveAsync(request, cancellation.Token));
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ToolOutputCheckResult>.Builder results,
        string name,
        Func<Task> check)
    {
        try
        {
            await check();
            results.Add(new(name, null));
        }
        catch (Exception exception)
        {
            results.Add(new(name, exception));
        }
    }

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected condition to be true.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
