namespace Grid.Core.Startup;

public enum StartupProgressStatus
{
    Known,
    Indeterminate,
}

public sealed record StartupProgressUnits(
    StartupProgressStatus Status,
    int Completed,
    int? Total)
{
    public static StartupProgressUnits Indeterminate(int completed = 0) =>
        new(StartupProgressStatus.Indeterminate, completed, null);

    public static StartupProgressUnits Known(int completed, int total) =>
        new(StartupProgressStatus.Known, completed, total);
}

public sealed record StartupProgressSnapshot(
    StartupProgressUnits Units,
    string? ActiveStageId,
    string? Detail);
