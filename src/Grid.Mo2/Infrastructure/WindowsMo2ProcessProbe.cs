using System.Collections.Immutable;
using System.Diagnostics;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class WindowsMo2ProcessProbe(IMo2PathCanonicalizer paths) : IMo2ProcessProbe
{
    public Task<Mo2ProcessProbeResult> ProbeAsync(
        string exactMo2ExecutablePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!paths.TryCanonicalize(exactMo2ExecutablePath, out var expected, out _))
            return Task.FromResult(new Mo2ProcessProbeResult(Mo2ProcessProbeStatus.Indeterminate, [], "The MO2 executable identity could not be canonicalized."));

        var processes = Process.GetProcessesByName("ModOrganizer")
            .Concat(Process.GetProcessesByName("ModOrganizer2"))
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .Take(257)
            .ToArray();
        if (processes.Length > 256)
        {
            Dispose(processes);
            return Task.FromResult(new Mo2ProcessProbeResult(Mo2ProcessProbeStatus.Indeterminate, [], "More than 256 MO2-named processes were observed."));
        }

        var observations = ImmutableArray.CreateBuilder<Mo2ProcessObservation>();
        var indeterminate = false;
        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DateTimeOffset? started = null;
                string? module = null;
                try { started = process.StartTime.ToUniversalTime(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { indeterminate = true; }
                try
                {
                    var candidate = process.MainModule?.FileName;
                    if (candidate is null || !paths.TryCanonicalize(candidate, out module!, out _)) indeterminate = true;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { indeterminate = true; }
                observations.Add(new(process.Id, started, module, module is not null && paths.Equals(module, expected)));
            }
            finally { process.Dispose(); }
        }

        var result = observations.Count == 0
            ? new Mo2ProcessProbeResult(Mo2ProcessProbeStatus.Clear, [], "No MO2-named process was observed.")
            : indeterminate
                ? new(Mo2ProcessProbeStatus.Indeterminate, observations.ToImmutable(), "A running MO2-named process could not be identified unambiguously.")
                : new(Mo2ProcessProbeStatus.Running, observations.ToImmutable(), "A running MO2-named process was observed.");
        return Task.FromResult(result);
    }

    private static void Dispose(IEnumerable<Process> processes) { foreach (var process in processes) process.Dispose(); }
}
