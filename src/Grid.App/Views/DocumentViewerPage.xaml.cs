using Grid.DocumentViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Grid.App.Views;

public sealed partial class DocumentViewerPage : Page
{
    public DocumentViewerPage()
    {
        InitializeComponent();
    }

    public void BindMarkdown(string content)
    {
        DocumentBody.Children.Clear();

        foreach (var block in MarkdownBlocks.Parse(content))
        {
            DocumentBody.Children.Add(block.Kind switch
            {
                MarkdownBlockKind.Heading => CreateHeading(block),
                MarkdownBlockKind.CodeFence => CreateCode(block.Text),
                _ => CreateParagraph(block.Text),
            });
        }
    }

    private static TextBlock CreateHeading(MarkdownBlock block) =>
        new()
        {
            Text = block.Text,
            FontSize = block.HeadingLevel switch
            {
                1 => 28,
                2 => 22,
                _ => 18,
            },
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, block.HeadingLevel == 1 ? 12 : 18, 0, 4),
            IsTextSelectionEnabled = true,
        };

    private static TextBlock CreateParagraph(string text) =>
        new()
        {
            Text = text,
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

    private static Border CreateCode(string text) =>
        new()
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 30, 30, 30)),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        };
}
