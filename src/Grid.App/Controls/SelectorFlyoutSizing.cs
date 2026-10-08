using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace Grid.App.Controls;

/// <summary>Measures the current rendered selector level and constrains it only to its window.</summary>
public static class SelectorFlyoutSizing
{
    private const double PresenterInset = 5; // Four DIP padding plus the one DIP border.
    private sealed record PresenterBasis(Style? Style);
    private static readonly ConditionalWeakTable<Flyout, PresenterBasis> PresenterBases = new();
    private sealed class PendingMeasurement
    {
        public long Generation;
        public FrameworkElement? Body;
        public RoutedEventHandler? LoadedHandler;
    }
    private static readonly ConditionalWeakTable<Flyout, PendingMeasurement> PendingMeasurements = new();

    public static void Apply(FrameworkElement anchor, Flyout flyout, FrameworkElement content,
        ScrollViewer viewport, params FrameworkElement[] fixedBlocks)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Content is not FrameworkElement body) return;

        var pending = PendingMeasurements.GetValue(flyout, _ => new PendingMeasurement());
        var generation = ++pending.Generation;
        if (pending.Body is not null && pending.LoadedHandler is not null)
            pending.Body.Loaded -= pending.LoadedHandler;
        pending.Body = null;
        pending.LoadedHandler = null;

        var bounds = AvailableContentBounds(anchor);
        flyout.FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
        {
            BasedOn = PresenterBases.GetValue(flyout, value => new PresenterBasis(value.FlyoutPresenterStyle)).Style,
            Setters =
            {
                new Setter(FrameworkElement.MinWidthProperty, 0d),
                new Setter(FrameworkElement.MinHeightProperty, 0d),
                new Setter(FrameworkElement.MaxWidthProperty, bounds.Width + PresenterInset * 2),
                new Setter(FrameworkElement.MaxHeightProperty, bounds.Height + PresenterInset * 2),
                new Setter(Control.PaddingProperty, new Thickness(4)),
                new Setter(Control.BorderThicknessProperty, new Thickness(1)),
                new Setter(Control.CornerRadiusProperty, new CornerRadius(0)),
            },
        };

        ReleaseDimensions(content);
        if (!ReferenceEquals(content, viewport)) ReleaseDimensions(viewport);
        if (!body.IsLoaded)
        {
            // Opening precedes template realization. Measuring here can report only a
            // button's padding; keep natural layout until the actual text is attached.
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                body.Loaded -= loaded;
                if (pending.Generation != generation) return;
                pending.Body = null;
                pending.LoadedHandler = null;
                anchor.DispatcherQueue.TryEnqueue(() =>
                {
                    if (pending.Generation == generation && body.IsLoaded)
                    {
                        body.UpdateLayout();
                        MeasureCurrentLevel(anchor, content, viewport, body, fixedBlocks);
                    }
                });
            };
            pending.Body = body;
            pending.LoadedHandler = loaded;
            body.Loaded += loaded;
            return;
        }
        MeasureCurrentLevel(anchor, content, viewport, body, fixedBlocks);
        // Newly replaced level rows can be attached to an already loaded panel. One
        // dispatcher pass measures their completed content templates, with no retry loop.
        anchor.DispatcherQueue.TryEnqueue(() =>
        {
            if (pending.Generation != generation || !body.IsLoaded) return;
            body.UpdateLayout();
            MeasureCurrentLevel(anchor, content, viewport, body, fixedBlocks);
        });
    }

    private static void MeasureCurrentLevel(FrameworkElement anchor, FrameworkElement content,
        ScrollViewer viewport, FrameworkElement body, FrameworkElement[] fixedBlocks)
    {
        var bounds = AvailableContentBounds(anchor);
        ReleaseDimensions(content);
        if (!ReferenceEquals(content, viewport)) ReleaseDimensions(viewport);
        RealizeTemplates(body);
        foreach (var block in fixedBlocks) RealizeTemplates(block);
        body.InvalidateMeasure();
        body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var naturalWidth = body.DesiredSize.Width;
        foreach (var block in fixedBlocks.Where(block => block.Visibility == Visibility.Visible))
        {
            block.InvalidateMeasure();
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            naturalWidth = Math.Max(naturalWidth, block.DesiredSize.Width);
        }

        var width = Math.Min(Math.Ceiling(naturalWidth), bounds.Width);
        body.InvalidateMeasure();
        body.Measure(new Size(width, double.PositiveInfinity));
        var fixedHeight = 0d;
        foreach (var block in fixedBlocks.Where(block => block.Visibility == Visibility.Visible))
        {
            block.InvalidateMeasure();
            block.Measure(new Size(width, double.PositiveInfinity));
            fixedHeight += block.DesiredSize.Height;
        }

        var viewportHeight = Math.Min(Math.Ceiling(body.DesiredSize.Height), Math.Max(0, bounds.Height - fixedHeight));
        viewport.Width = width;
        viewport.Height = viewportHeight;
        content.Width = width;
        content.Height = Math.Min(bounds.Height, Math.Ceiling(fixedHeight + viewportHeight));
        content.InvalidateMeasure();
    }

    private static void RealizeTemplates(DependencyObject element)
    {
        if (element is Control control) control.ApplyTemplate();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            RealizeTemplates(VisualTreeHelper.GetChild(element, index));
        if (element is FrameworkElement frameworkElement) frameworkElement.InvalidateMeasure();
    }

    private static void ReleaseDimensions(FrameworkElement element)
    {
        element.Width = double.NaN;
        element.Height = double.NaN;
        element.MinWidth = 0;
        element.MinHeight = 0;
        element.MaxWidth = double.PositiveInfinity;
        element.MaxHeight = double.PositiveInfinity;
    }

    private static Size AvailableContentBounds(FrameworkElement anchor)
    {
        if (anchor.XamlRoot is not { } root)
            return new Size(double.PositiveInfinity, double.PositiveInfinity);
        var anchorBounds = anchor.TransformToVisual(root.Content)
            .TransformBounds(new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
        var below = root.Size.Height - anchorBounds.Bottom;
        var above = anchorBounds.Top;
        return new Size(Math.Max(1, root.Size.Width - PresenterInset * 2),
            Math.Max(1, Math.Max(above, below) - PresenterInset * 2));
    }
}
