namespace Grid.App.Composition;

public enum GridApplicationMode
{
    Production,
    Demo,
}

public static class GridLaunchOptions
{
    public static GridApplicationMode Parse(string? arguments) =>
        Parse(arguments, [], null);

    public static GridApplicationMode Parse(
        string? activationArguments,
        IEnumerable<string> processArguments,
        string? isolatedTestMode = null)
    {
#if DEBUG
        const bool debugBuild = true;
#else
        const bool debugBuild = false;
#endif
        var isolatedHarness = string.Equals(isolatedTestMode, "demo", StringComparison.Ordinal);
        if (!debugBuild && !isolatedHarness)
        {
            return GridApplicationMode.Production;
        }

        var requested = string.Equals(activationArguments?.Trim(), "--demo", StringComparison.Ordinal) ||
            processArguments.Any(argument => string.Equals(argument, "--demo", StringComparison.Ordinal)) ||
            isolatedHarness;
        return requested ? GridApplicationMode.Demo : GridApplicationMode.Production;
    }
}
