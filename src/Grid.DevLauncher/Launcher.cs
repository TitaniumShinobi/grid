using System.Diagnostics;

namespace Grid.DevLauncher;

internal sealed class LauncherConfigurationException(string message) : InvalidOperationException(message);

internal readonly record struct LauncherResolution(
    string RepositoryRoot,
    string LauncherPath,
    string TargetPath);

internal static class Launcher
{
    internal const string BuildCommand = "dotnet build Grid.sln -c Debug -p:Platform=x64";

    private static readonly string[] TargetSegments =
    [
        "src", "Grid.App", "bin", "x64", "Debug",
        "net9.0-windows10.0.19041.0", "win-x64", "Grid.exe",
    ];

    internal static LauncherResolution Resolve(string launcherDirectory, string launcherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);

        var repositoryRoot = Path.GetFullPath(launcherDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var canonicalLauncher = Path.GetFullPath(launcherPath);
        var solutionPath = Path.Combine(repositoryRoot, "Grid.sln");
        if (!File.Exists(solutionPath))
        {
            throw new LauncherConfigurationException(
                $"Grid.exe must remain in the Grid repository root. Expected solution marker: '{solutionPath}'.");
        }

        var targetPath = TargetSegments.Aggregate(repositoryRoot, Path.Combine);
        targetPath = Path.GetFullPath(targetPath);
        var containedPrefix = repositoryRoot + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(containedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(targetPath).Equals("Grid.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherConfigurationException("The fixed Grid Debug target resolved outside the repository boundary.");
        }

        if (targetPath.Equals(canonicalLauncher, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherConfigurationException("The root launcher refused to launch itself recursively.");
        }

        if (!File.Exists(targetPath))
        {
            throw new LauncherConfigurationException(
                $"The current Grid Debug build was not found at:\n{targetPath}\n\nBuild it first with:\n{BuildCommand}");
        }

        return new(repositoryRoot, canonicalLauncher, targetPath);
    }

    internal static ProcessStartInfo CreateStartInfo(
        string launcherDirectory,
        string launcherPath,
        IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var resolution = Resolve(launcherDirectory, launcherPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = resolution.TargetPath,
            WorkingDirectory = resolution.RepositoryRoot,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument ?? string.Empty);
        }
        return startInfo;
    }
}
