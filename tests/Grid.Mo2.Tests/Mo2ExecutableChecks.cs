using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ExecutableCheckResult(string Name, Exception? Failure);

static class Mo2ExecutableChecks
{
    public static async Task<ImmutableArray<Mo2ExecutableCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ExecutableCheckResult>();
        await RunAsync(results, "QSettings executable arrays preserve sparse duplicate and unsupported evidence", ParserPreservesEvidenceAsync);
        await RunAsync(results, "opaque arguments remain exact and mask all implicit representations", OpaqueArgumentsAsync);
        await RunAsync(results, "executable observation validates paths and preserves duplicate entries", ObservationAsync);
        await RunAsync(results, "outside-root executable paths require exact session authorization", OutsideAuthorizationAsync);
        await RunAsync(results, "executable parsing remains bounded and cancellation aware", BoundsAndCancellationAsync);
        await RunAsync(results, "managed PE icon reading extracts resources without module loading", IconAsync);
        await RunAsync(results, "executable observation does not mutate fixture state", NonMutationAsync);
        return results.ToImmutable();
    }

    private static Task ParserPreservesEvidenceAsync()
    {
        const string source = "[customExecutables]\r\n" +
            "size=2\r\n" +
            "1\\title=First\r\n" +
            "1\\title=First override\r\n" +
            "1\\arguments=--literal=one & two\r\n" +
            "4\\binary=@ByteArray(C:\\\\Tools\\\\Tool.exe)\n" +
            "4\\future=@Variant(unsupported)\r";
        var decoder = new Mo2TextDecoder();
        var document = decoder.Decode(ImmutableArray.Create(Encoding.UTF8.GetBytes(source)));
        var result = new Mo2QSettingsArrayParser().Parse(document, "customExecutables");

        Equal(2, result.DeclaredSize);
        Equal(2, result.Entries.Length);
        Equal("First override", result.Entries[0].LastSupported("title")!.LogicalValue);
        Equal("C:\\Tools\\Tool.exe", result.Entries[1].LastSupported("binary")!.LogicalValue);
        Equal(ProfileSourceParseStatus.UnsupportedSyntax, result.Status);
        Contains(result.Entries[0].Warnings, warning => warning.Code == "mo2.executables.field_duplicate");
        Contains(result.Entries[1].Warnings, warning => warning.Code == "mo2.executables.sparse_index");
        Equal(source, Reconstruct(document));
        return Task.CompletedTask;
    }

    private static Task OpaqueArgumentsAsync()
    {
        const string exact = "--token=secret-value --literal=hello & whoami";
        var arguments = new Mo2OpaqueArguments(exact);
        Equal(exact, arguments.ExactValue);
        False(arguments.ToString().Contains("secret", StringComparison.Ordinal));
        False(arguments.MaskedDisplay.Contains("token", StringComparison.Ordinal));
        True(arguments.MaskedDisplay.Contains(exact.Length.ToString(), StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task ObservationAsync()
    {
        using var fixture = new Mo2ExecutableFixtureBuilder();
        var binary = fixture.WriteExpectedBinary("tools\\SSEEdit.exe");
        var encodedBinary = IniPath(binary);
        var encodedWorking = IniPath(Path.GetDirectoryName(binary)!);
        fixture.WriteConfiguration(
            "[customExecutables]\r\n" +
            "size=2\r\n" +
            "1\\title=SSEEdit\r\n" +
            $"1\\binary={encodedBinary}\r\n" +
            "1\\arguments=-IKnowWhatImDoing & literal\r\n" +
            $"1\\workingDirectory={encodedWorking}\r\n" +
            "1\\toolbar=true\r\n" +
            "2\\title=SSEEdit\r\n" +
            $"2\\binary={encodedBinary}\r\n" +
            $"2\\workingDirectory={IniPath(Path.Combine(fixture.Application, "missing", "directory"))}\r\n");

        var snapshot = await fixture.Service.ObserveAsync(fixture.Request());
        Equal(Mo2ExecutableConfigurationStatus.Partial, snapshot.Status);
        Equal(2, snapshot.Entries.Length);
        True(snapshot.Entries.All(entry => entry.IsDuplicate));
        True(snapshot.Entries.All(entry => entry.Binary.Availability == Mo2ExecutablePathAvailability.Available));
        Equal(Mo2ExecutablePathAvailability.Missing, snapshot.Entries[1].WorkingDirectory.Availability);
        Equal("-IKnowWhatImDoing & literal", snapshot.Entries[0].Arguments.ExactValue);
        False(snapshot.Revision.Contains("literal", StringComparison.Ordinal));
        Equal(64, snapshot.Provenance!.ContentFingerprint.Length);
        Equal(Mo2TextEncodingKind.Utf8, snapshot.Provenance.Encoding);
    }

    private static async Task OutsideAuthorizationAsync()
    {
        using var fixture = new Mo2ExecutableFixtureBuilder();
        var outside = fixture.WriteOutsideBinary("BodySlide.exe");
        fixture.WriteConfiguration(
            "[customExecutables]\n" +
            "size=1\n" +
            "1\\title=BodySlide\n" +
            $"1\\binary={IniPath(outside)}\n");

        var blocked = await fixture.Service.ObserveAsync(fixture.Request());
        var blockedPath = blocked.Entries.Single().Binary;
        Equal(Mo2ExecutablePathAvailability.OutsideExpectedRoots, blockedPath.Availability);
        Equal(Mo2ExecutableLocationTrust.OutsideExpectedRoots, blockedPath.LocationTrust);
        False(blockedPath.WasProbed);

        fixture.Authorizations.AuthorizePath(fixture.ReferenceId, outside);
        var authorized = await fixture.Service.ObserveAsync(fixture.Request());
        Equal(Mo2ExecutablePathAvailability.Available, authorized.Entries.Single().Binary.Availability);
        Equal(Mo2ExecutableLocationTrust.ExplicitlyAuthorized, authorized.Entries.Single().Binary.LocationTrust);
        True(authorized.Entries.Single().Binary.WasProbed);

        var sibling = fixture.WriteOutsideBinary("Sibling.exe");
        False(fixture.Authorizations.IsPathAuthorized(fixture.ReferenceId, sibling));
    }

    private static async Task BoundsAndCancellationAsync()
    {
        var decoder = new Mo2TextDecoder();
        var document = decoder.Decode(ImmutableArray.Create(Encoding.UTF8.GetBytes(
            "[customExecutables]\nsize=1\n1\\title=" + new string('x', 32))));
        var result = new Mo2QSettingsArrayParser().Parse(
            document,
            "customExecutables",
            new(MaximumLineCharacters: 16));
        Equal(ProfileSourceParseStatus.Malformed, result.Status);
        Contains(result.Warnings, warning => warning.Code == "mo2.executables.line_too_long");

        using var fixture = new Mo2ExecutableFixtureBuilder();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Service.ObserveAsync(fixture.Request(), source.Token));
    }

    private static async Task IconAsync()
    {
        using var fixture = new Mo2ExecutableFixtureBuilder();
        var image = Enumerable.Range(0, 64).Select(index => checked((byte)index)).ToArray();
        var path = fixture.WriteExpectedBinary("tools\\IconTool.exe", Mo2ExecutableFixtureBuilder.MinimalPeWithIcon(image));
        var observation = await new Mo2PeIconReader(fixture.FileSystem).ReadFirstIconAsync(path);
        Equal(Mo2IconObservationStatus.Available, observation.Status);
        True(observation.IcoBytes.Length == 22 + image.Length);
        True(observation.IcoBytes.AsSpan()[22..].SequenceEqual(image));
        Equal(64, observation.Fingerprint!.Length);

        var malformed = fixture.WriteExpectedBinary("tools\\Malformed.exe", "MZbad"u8.ToArray());
        var rejected = await new Mo2PeIconReader(fixture.FileSystem).ReadFirstIconAsync(malformed);
        Equal(Mo2IconObservationStatus.Malformed, rejected.Status);
        Contains(rejected.Warnings, warning => warning.Code == "mo2.icon.pe_invalid");
    }

    private static async Task NonMutationAsync()
    {
        using var fixture = new Mo2ExecutableFixtureBuilder();
        var binary = fixture.WriteExpectedBinary("tools\\ReadOnly.exe", Mo2ExecutableFixtureBuilder.MinimalPeWithIcon([1, 2, 3, 4]));
        fixture.WriteConfiguration(
            "[customExecutables]\r\n" +
            "size=1\r\n" +
            "1\\title=Read only\r\n" +
            $"1\\binary={IniPath(binary)}\r\n" +
            "1\\arguments=--secret=must-not-leak\r\n");
        var before = fixture.ContentHash();
        var snapshot = await fixture.Service.ObserveAsync(fixture.Request());
        _ = await new Mo2PeIconReader(fixture.FileSystem).ReadFirstIconAsync(snapshot.Entries.Single().Binary.CanonicalPath!);
        var after = fixture.ContentHash();
        Equal(before, after);
    }

    private static string IniPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string Reconstruct(Mo2RawTextDocument document)
    {
        var builder = new StringBuilder();
        foreach (var line in document.Lines)
        {
            builder.Append(line.Text);
            builder.Append(line.Terminator switch
            {
                Mo2LineTerminator.CarriageReturn => "\r",
                Mo2LineTerminator.LineFeed => "\n",
                Mo2LineTerminator.CarriageReturnLineFeed => "\r\n",
                _ => string.Empty,
            });
        }

        return builder.ToString();
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ExecutableCheckResult>.Builder results,
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
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static void Contains<T>(IEnumerable<T> source, Func<T, bool> predicate)
    {
        if (!source.Any(predicate))
        {
            throw new InvalidOperationException("Expected matching item.");
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
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
