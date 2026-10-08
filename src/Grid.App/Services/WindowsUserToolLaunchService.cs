using System.ComponentModel;
using System.Diagnostics;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

public sealed class WindowsUserToolLaunchService : IUserToolLaunchService
{
    public Task<UserToolLaunchResult> LaunchAsync(
        UserToolLaunchConfiguration configuration,
        UserToolLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Id != request.ConfigurationId || !configuration.Scope.IsVisibleIn(request.CurrentContext) || configuration.Fingerprint != request.ConfigurationFingerprint)
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Stale, null, "The launch request no longer matches the saved configuration."));
        if (!configuration.IsRunnable || string.IsNullOrWhiteSpace(configuration.BinaryPath))
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Unavailable, null, "The selected tool does not have an enabled absolute executable configuration."));

        var binary = Path.GetFullPath(configuration.BinaryPath);
        if (!File.Exists(binary))
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Unavailable, null, "The configured executable no longer exists."));
        var startIn = string.IsNullOrWhiteSpace(configuration.StartInPath)
            ? Path.GetDirectoryName(binary)!
            : Path.GetFullPath(configuration.StartInPath);
        if (!Directory.Exists(startIn))
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Unavailable, null, "The configured working directory no longer exists."));

        try
        {
            var start = new ProcessStartInfo(binary)
            {
                WorkingDirectory = startIn,
                UseShellExecute = true,
            };
            foreach (var argument in configuration.Arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start);
            if (process is null) return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Failed, null, "Windows did not return a process for the launch request."));
            var processId = process.Id;
            process.Dispose();
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Started, processId, $"Started {configuration.Title}."));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Failed, null, $"Windows could not start the configured executable ({exception.GetType().Name})."));
        }
    }
}
