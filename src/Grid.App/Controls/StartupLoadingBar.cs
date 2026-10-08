using Grid.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Grid.App.Controls;

/// <summary>
/// Truthful 22px startup thermometer with LCD percentage readout. Progress is
/// driven only from verified startup observer events.
/// </summary>
public sealed class StartupLoadingBar : Microsoft.UI.Xaml.Controls.Grid
{
    private readonly Border track;
    private readonly Border fill;
    private readonly SevenSegmentDisplay percentageDisplay;

    public static readonly DependencyProperty ProgressSnapshotProperty = DependencyProperty.Register(
        nameof(ProgressSnapshot),
        typeof(StartupProgressSnapshot),
        typeof(StartupLoadingBar),
        new PropertyMetadata(null, OnProgressSnapshotChanged));

    public StartupLoadingBar()
    {
        Height = 22;
        MinHeight = 22;
        MaxHeight = 22;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(100, 0, 100, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        track = new Border
        {
            Height = 22,
            Background = new SolidColorBrush(Color.FromArgb(255, 28, 28, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 58, 58, 58)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 0,
            Background = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent)
                ? (Brush)accent
                : new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            CornerRadius = new CornerRadius(1),
        };
        track.Child = fill;

        percentageDisplay = new SevenSegmentDisplay
        {
            DigitCount = 3,
            DigitWidth = 8,
            DigitHeight = 14,
            DigitSpacing = 1,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        SetColumn(track, 0);
        SetColumn(percentageDisplay, 1);
        Children.Add(track);
        Children.Add(percentageDisplay);
    }

    public StartupProgressSnapshot? ProgressSnapshot
    {
        get => (StartupProgressSnapshot?)GetValue(ProgressSnapshotProperty);
        set => SetValue(ProgressSnapshotProperty, value);
    }

    private static void OnProgressSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is StartupLoadingBar bar && args.NewValue is StartupProgressSnapshot snapshot)
            bar.ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(StartupProgressSnapshot snapshot)
    {
        var units = snapshot.Units;
        var trackWidth = track.ActualWidth;
        if (trackWidth <= 0)
        {
            track.Loaded -= OnTrackLoaded;
            track.Loaded += OnTrackLoaded;
            return;
        }

        RenderUnits(units, trackWidth);
    }

    private void OnTrackLoaded(object sender, RoutedEventArgs e)
    {
        track.Loaded -= OnTrackLoaded;
        if (ProgressSnapshot is { } snapshot)
            RenderUnits(snapshot.Units, track.ActualWidth);
    }

    private void RenderUnits(StartupProgressUnits units, double trackWidth)
    {
        if (units.Status == StartupProgressStatus.Known && units.Total is > 0)
        {
            var ratio = Math.Clamp((double)units.Completed / units.Total.Value, 0d, 1d);
            fill.Width = Math.Max(0, trackWidth * ratio);
            percentageDisplay.Text = ((int)Math.Round(ratio * 100d)).ToString();
            return;
        }

        var partial = units.Completed <= 0
            ? 0.04d
            : Math.Clamp(units.Completed / (double)StartupProgressCatalog.TotalUnits, 0.04d, 0.96d);
        fill.Width = Math.Max(0, trackWidth * partial);
        percentageDisplay.Text = string.Empty.PadLeft(3, '-');
    }
}
