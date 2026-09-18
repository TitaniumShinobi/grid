using Grid.Core.Application;

namespace Grid.App.Services;

public enum ResponsiveLayoutBand { Narrow, Medium, Wide }

public enum AssistantPresentationMode { Closed, Docked, Solo }

public readonly record struct ResponsiveLayoutDecision(
    ResponsiveLayoutBand Band,
    double AvailableWidth,
    double WorkspaceWidth,
    double AssistantWidth,
    AssistantPresentationMode AssistantPresentation);

public static class ResponsiveLayoutPolicy
{
    public const double WideThreshold = 1320;
    public const double MediumThreshold = 860;
    public const double AssistantDockThreshold = 760;
    public const double MinimumWorkspaceWidth = 360;
    public const double PaneDividerThickness = 4.25;

    public static ResponsiveLayoutDecision Evaluate(double availableWidth, bool assistantExpanded, double requestedAssistantWidth)
    {
        var safeWidth = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        if (!assistantExpanded)
        {
            return new(
                BandFor(safeWidth),
                safeWidth,
                safeWidth,
                0,
                AssistantPresentationMode.Closed);
        }

        if (safeWidth >= AssistantDockThreshold)
        {
            var maximum = Math.Max(
                AssistantSessionState.MinimumPanelWidth,
                Math.Min(AssistantSessionState.MaximumPanelWidth, safeWidth - MinimumWorkspaceWidth - PaneDividerThickness));
            var width = Math.Clamp(
                requestedAssistantWidth,
                AssistantSessionState.MinimumPanelWidth,
                maximum);
            var workspaceWidth = Math.Max(0, safeWidth - width - PaneDividerThickness);
            return new(BandFor(workspaceWidth), safeWidth, workspaceWidth, width, AssistantPresentationMode.Docked);
        }

        // Below the split-workbench threshold, Chat becomes the one complete
        // workbench panel. It never floats over another panel as a drawer.
        return new(BandFor(safeWidth), safeWidth, 0, safeWidth, AssistantPresentationMode.Solo);
    }

    private static ResponsiveLayoutBand BandFor(double width) =>
        width >= WideThreshold ? ResponsiveLayoutBand.Wide
            : width >= MediumThreshold ? ResponsiveLayoutBand.Medium
            : ResponsiveLayoutBand.Narrow;
}
