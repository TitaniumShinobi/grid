namespace Grid.Core.Startup;

public enum StartupWorkClassification
{
    Registration,
    StartupRequired,
    BackgroundOptional,
    StaleRefresh,
    Rebuild,
}
