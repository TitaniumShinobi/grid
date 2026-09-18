using System.Diagnostics;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class WindowsMo2ProcessRunner : IMo2ProcessRunner
{
    public Task<IMo2ProcessHandle> StartAsync(Mo2LaunchInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(invocation.ExecutablePath)
                ?? throw new InvalidOperationException("The validated MO2 working directory is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("MO2 did not return a process handle.");
        return Task.FromResult<IMo2ProcessHandle>(new Handle(process));
    }

    private sealed class Handle(Process process) : IMo2ProcessHandle
    {
        public int ProcessId => process.Id;
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        public ValueTask DisposeAsync() { process.Dispose(); return ValueTask.CompletedTask; }
    }
}
