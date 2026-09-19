using Grid.DevLauncher;

namespace Grid.DevLauncher.Tests;

internal static class Program
{
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-root-launcher-{Guid.NewGuid():N}");
        try
        {
            VerifyStartContract(root);
            VerifyNestedLauncherContract(root);
            VerifyMissingTarget(root);
            VerifyRecursionGuard(root);
            VerifyRepositoryGuard(root);
            Console.WriteLine("All 5 Grid.DevLauncher checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyStartContract(string root)
    {
        var target = CreateSyntheticRepository(root);
        var launcher = Path.Combine(root, "Grid.exe");
        var priorDataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        var expectedDataRoot = Path.Combine(root, "isolated-data");
        Environment.SetEnvironmentVariable("GRID_DATA_ROOT", expectedDataRoot);
        try
        {
            string[] arguments = ["--demo", "literal & value", "$(inert)"];
            var startInfo = Launcher.CreateStartInfo(root, launcher, arguments);
            Assert(startInfo.FileName == target, "The launcher did not resolve the fixed Debug executable.");
            Assert(startInfo.WorkingDirectory == root, "The launcher did not preserve the repository working directory.");
            Assert(!startInfo.UseShellExecute, "The launcher enabled shell parsing.");
            Assert(startInfo.ArgumentList.SequenceEqual(arguments), "Arguments were not forwarded literally and in order.");
            Assert(startInfo.Environment["GRID_DATA_ROOT"] == expectedDataRoot, "GRID_DATA_ROOT was not inherited.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRID_DATA_ROOT", priorDataRoot);
        }
        Console.WriteLine("PASS root launcher resolves Debug and preserves literal arguments and environment.");
    }

    private static void VerifyNestedLauncherContract(string root)
    {
        var target = CreateSyntheticRepository(root);
        var launcherDirectory = Path.Combine(
            root, "src", "Grid.DevLauncher", "bin", "x64", "Debug",
            "net9.0-windows10.0.19041.0", "win-x64");
        Directory.CreateDirectory(launcherDirectory);
        var launcher = Path.Combine(launcherDirectory, "Grid.RootLauncher.exe");
        File.WriteAllBytes(launcher, [0x4D, 0x5A]);

        var resolution = Launcher.Resolve(launcherDirectory, launcher);

        Assert(resolution.RepositoryRoot == root,
            "Nested launcher did not resolve the ancestor repository root.");
        Assert(resolution.TargetPath == target,
            "Nested launcher did not resolve the Grid Debug executable.");

        Console.WriteLine("PASS nested compiled launcher resolves ancestor repository root.");
    }
    private static void VerifyMissingTarget(string root)
    {
        var target = ExpectedTarget(root);
        File.Delete(target);
        var exception = AssertThrows(() => Launcher.Resolve(root, Path.Combine(root, "Grid.exe")));
        Assert(exception.Message.Contains(Launcher.BuildCommand, StringComparison.Ordinal),
            "The missing-target error omitted the exact build command.");
        Console.WriteLine("PASS missing Debug payload fails with an actionable build command.");
    }

    private static void VerifyRecursionGuard(string root)
    {
        var target = CreateSyntheticRepository(root);
        var exception = AssertThrows(() => Launcher.Resolve(root, target));
        Assert(exception.Message.Contains("recursively", StringComparison.OrdinalIgnoreCase),
            "The recursion guard did not identify self-launch.");
        Console.WriteLine("PASS root launcher refuses recursive self-launch.");
    }

    private static void VerifyRepositoryGuard(string root)
    {
        var invalidRoot = Path.Combine(
            Path.GetTempPath(),
            $"grid-not-a-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidRoot);
        try
        {
            var exception = AssertThrows(() =>
                Launcher.Resolve(invalidRoot, Path.Combine(invalidRoot, "Grid.exe")));
            Assert(exception.Message.Contains("repository root", StringComparison.OrdinalIgnoreCase),
                "The repository-boundary error was not explicit.");
        }
        finally
        {
            try { Directory.Delete(invalidRoot, recursive: true); } catch { }
        }

        Console.WriteLine("PASS launcher refuses an unexpected root location.");
    }

    private static string CreateSyntheticRepository(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Grid.sln"), "synthetic marker");
        var target = ExpectedTarget(root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, [0x4D, 0x5A]);
        return target;
    }

    private static string ExpectedTarget(string root) => Path.Combine(
        root, "src", "Grid.App", "bin", "x64", "Debug",
        "net9.0-windows10.0.19041.0", "win-x64", "Grid.exe");

    private static LauncherConfigurationException AssertThrows(Action action)
    {
        try { action(); }
        catch (LauncherConfigurationException exception) { return exception; }
        throw new InvalidOperationException("Expected LauncherConfigurationException was not thrown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
