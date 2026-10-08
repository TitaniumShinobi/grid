using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Grid.App.Controls;

/// <summary>
/// A compact, dependency-free seven-segment numeric display. This mirrors the
/// manager-style LCD presentation without relying on a machine-installed font.
/// </summary>
public sealed class SevenSegmentDisplay : StackPanel
{
    private static readonly byte[] DigitSegments =
    [
        0b0111111, // 0
        0b0000110, // 1
        0b1011011, // 2
        0b1001111, // 3
        0b1100110, // 4
        0b1101101, // 5
        0b1111101, // 6
        0b0000111, // 7
        0b1111111, // 8
        0b1101111, // 9
    ];

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SevenSegmentDisplay),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty DigitCountProperty = DependencyProperty.Register(
        nameof(DigitCount), typeof(int), typeof(SevenSegmentDisplay),
        new PropertyMetadata(1, OnPresentationChanged));

    public static readonly DependencyProperty DigitWidthProperty = DependencyProperty.Register(
        nameof(DigitWidth), typeof(double), typeof(SevenSegmentDisplay),
        new PropertyMetadata(9d, OnPresentationChanged));

    public static readonly DependencyProperty DigitHeightProperty = DependencyProperty.Register(
        nameof(DigitHeight), typeof(double), typeof(SevenSegmentDisplay),
        new PropertyMetadata(18d, OnPresentationChanged));

    public static readonly DependencyProperty DigitSpacingProperty = DependencyProperty.Register(
        nameof(DigitSpacing), typeof(double), typeof(SevenSegmentDisplay),
        new PropertyMetadata(1d, OnPresentationChanged));

    public SevenSegmentDisplay()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 1;
        IsHitTestVisible = false;
        RenderText();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Gets or sets the fixed number of LCD digits. Values are zero-padded on
    /// the left; an out-of-range value renders the capacity limit rather than
    /// changing the control's measured width.
    /// </summary>
    public int DigitCount
    {
        get => (int)GetValue(DigitCountProperty);
        set => SetValue(DigitCountProperty, value);
    }

    public double DigitWidth
    {
        get => (double)GetValue(DigitWidthProperty);
        set => SetValue(DigitWidthProperty, value);
    }

    public double DigitHeight
    {
        get => (double)GetValue(DigitHeightProperty);
        set => SetValue(DigitHeightProperty, value);
    }

    public double DigitSpacing
    {
        get => (double)GetValue(DigitSpacingProperty);
        set => SetValue(DigitSpacingProperty, value);
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SevenSegmentDisplay)sender).RenderText();

    private static void OnPresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SevenSegmentDisplay)sender).RenderText();

    private void RenderText()
    {
        Children.Clear();
        Spacing = Math.Max(0, DigitSpacing);
        var capacity = Math.Clamp(DigitCount, 1, 8);
        var numericText = new string((Text ?? string.Empty)
            .Where(character => character is >= '0' and <= '9')
            .ToArray());
        if (numericText.Length == 0)
        {
            numericText = "0";
        }

        numericText = numericText.Length > capacity
            ? new string('9', capacity)
            : numericText.PadLeft(capacity, '0');

        foreach (var character in numericText)
        {
            Children.Add(CreateDigit(DigitSegments[character - '0']));
        }
    }

    private Viewbox CreateDigit(byte enabledSegments)
    {
        var canvas = new Canvas { Width = 10, Height = 18 };
        var activeBrush = new SolidColorBrush(Color.FromArgb(255, 238, 238, 238));
        var inactiveBrush = new SolidColorBrush(Color.FromArgb(255, 39, 39, 39));
        var points = new[]
        {
            new[] { new Point(2, 0), new Point(8, 0), new Point(7, 2), new Point(3, 2) },       // a
            new[] { new Point(8, 1), new Point(10, 3), new Point(10, 8), new Point(8, 9), new Point(7, 7), new Point(7, 3) }, // b
            new[] { new Point(8, 9), new Point(10, 10), new Point(10, 15), new Point(8, 17), new Point(7, 15), new Point(7, 11) }, // c
            new[] { new Point(2, 16), new Point(8, 16), new Point(7, 18), new Point(3, 18) },   // d
            new[] { new Point(0, 10), new Point(2, 9), new Point(3, 11), new Point(3, 15), new Point(2, 17), new Point(0, 15) }, // e
            new[] { new Point(0, 3), new Point(2, 1), new Point(3, 3), new Point(3, 7), new Point(2, 9), new Point(0, 8) }, // f
            new[] { new Point(2, 8), new Point(3, 7), new Point(7, 7), new Point(8, 9), new Point(7, 10), new Point(3, 10) }, // g
        };

        for (var index = 0; index < points.Length; index++)
        {
            var polygon = new Polygon
            {
                Fill = (enabledSegments & (1 << index)) != 0 ? activeBrush : inactiveBrush,
                Points = new PointCollection(),
            };
            foreach (var point in points[index]) polygon.Points.Add(point);
            canvas.Children.Add(polygon);
        }

        return new Viewbox
        {
            Width = Math.Max(1, DigitWidth),
            Height = Math.Max(1, DigitHeight),
            Stretch = Stretch.Fill,
            Child = canvas,
        };
    }
}
