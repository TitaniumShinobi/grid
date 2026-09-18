using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed class Mo2LaunchFixtureBuilder
{
    public Mo2LaunchFixtureBuilder(Mo2InstanceKind instanceKind = Mo2InstanceKind.Portable, string title = "SKSE")
    {
        var app = Path.Combine("C:\\", "MO2");
        var instance = instanceKind == Mo2InstanceKind.Portable ? app : Path.Combine("C:\\", "MO2 Instances", "Global Instance");
        Reference = new(1, new("reference.mo2.launch"), new("installation.mo2.launch"), new("game.skyrim-special-edition"),
            new("adapter.mod-organizer-2"), "Fixture", instanceKind, Path.Combine(app, "ModOrganizer.exe"), instance);
        Validation = new(Mo2ValidationStatus.Valid,
            instanceKind == Mo2InstanceKind.Portable ? Mo2SelectionKind.PortableInstance : Mo2SelectionKind.GlobalInstance,
            true, instanceKind, app, Reference.ExecutablePath, instance, Path.Combine(instance, "ModOrganizer.ini"), instance,
            Path.Combine(instance, "mods"), Path.Combine(instance, "profiles"), Path.Combine(instance, "downloads"),
            Path.Combine(instance, "overwrite"), "C:\\Games\\Skyrim", "Skyrim Special Edition", "connection", [], []);
        Entry = new(0, 0, title,
            new("Binary", "C:\\Games\\Skyrim\\skse64_loader.exe", "C:\\Games\\Skyrim\\skse64_loader.exe",
                Mo2ExecutablePathAvailability.Available, Mo2ExecutableLocationTrust.ExpectedRoot, true, null),
            new("--ignored-by-grid"),
            new("Working directory", null, null, Mo2ExecutablePathAvailability.NotConfigured, Mo2ExecutableLocationTrust.Unknown, false, null),
            null, null, null, null, null, [], false, [], "entry-fingerprint");
        Executables = new(Reference.Id, Mo2ExecutableConfigurationStatus.Complete, [Entry],
            new("customExecutables", 1, [], [], [], ProfileSourceParseStatus.Parsed), null, null, [], "configuration-fingerprint");
    }

    public Mo2InstallationReference Reference { get; }
    public Mo2InstallationValidation Validation { get; }
    public Mo2ObservedExecutable Entry { get; }
    public Mo2ExecutableConfigurationSnapshot Executables { get; }
    public Mo2LaunchEvidence Evidence => new(Reference, Validation, Executables, new("profile.mo2.launch"),
        new("executable.mo2.launch"), Entry, ManagerProfileState.Active, "profile-fingerprint");
}

sealed class FakeVersionReader(Mo2VersionEvidenceStatus status = Mo2VersionEvidenceStatus.Supported) : IMo2PeVersionReader
{
    public Mo2VersionEvidence Read(string path) => new(status, new Version(2, 5, 2), "mo2-identity", status.ToString());
}
sealed class FakeProcessProbe(Mo2ProcessProbeStatus status = Mo2ProcessProbeStatus.Clear) : IMo2ProcessProbe
{
    public Task<Mo2ProcessProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Mo2ProcessProbeResult(status, [], status.ToString()));
}
sealed class FakeProcessRunner(int exitCode = 0) : IMo2ProcessRunner
{
    public Mo2LaunchInvocation? Invocation { get; private set; }
    public Task<IMo2ProcessHandle> StartAsync(Mo2LaunchInvocation invocation, CancellationToken cancellationToken = default)
    { Invocation = invocation; return Task.FromResult<IMo2ProcessHandle>(new Handle(exitCode)); }
    private sealed class Handle(int exitCode) : IMo2ProcessHandle
    {
        public int ProcessId => 42;
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(exitCode);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
