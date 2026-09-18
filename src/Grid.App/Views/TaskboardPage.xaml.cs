using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class TaskboardPage : Page
{
    private Action? closeBoard;
    private Action<string>? composeTask;
    private Action? attach;
    private Action<string>? openTask;

    public TaskboardPage() => InitializeComponent();

    public void BindContext(
        GridTaskboardSnapshot snapshot,
        Action closeBoard,
        Action<string> composeTask,
        Action attach,
        Action<string> openTask)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.closeBoard = closeBoard ?? throw new ArgumentNullException(nameof(closeBoard));
        this.composeTask = composeTask ?? throw new ArgumentNullException(nameof(composeTask));
        this.attach = attach ?? throw new ArgumentNullException(nameof(attach));
        this.openTask = openTask ?? throw new ArgumentNullException(nameof(openTask));

        QueueScore.Text = snapshot.QueueCount.ToString();
        ProgressScore.Text = snapshot.InProgressCount.ToString();
        ReadyScore.Text = snapshot.ReadyCount.ToString();
        LedgerScore.Text = snapshot.LedgerCount.ToString();

        Populate(QueueCards, snapshot.Cards.Where(card => card.Phase == GridTaskboardPhase.Queue));
        Populate(ProgressCards, snapshot.Cards.Where(card => card.Phase == GridTaskboardPhase.InProgress));
        Populate(ReadyCards, snapshot.Cards.Where(card => card.Phase == GridTaskboardPhase.Ready));
        Populate(LedgerCards, snapshot.Cards.Where(card => card.Phase == GridTaskboardPhase.Ledger));

        SuggestionCards.Children.Clear();
        if (snapshot.Suggestions.IsEmpty)
            SuggestionCards.Children.Add(Empty("No evidence-backed suggestions."));
        else
            foreach (var suggestion in snapshot.Suggestions)
                SuggestionCards.Children.Add(Card(suggestion.Title, suggestion.Description));
    }

    public void FocusLedger() => LedgerScroll.ChangeView(null, 0, null);

    private void Populate(StackPanel panel, IEnumerable<GridTaskboardCard> cards)
    {
        panel.Children.Clear();
        var values = cards.ToArray();
        if (values.Length == 0) { panel.Children.Add(Empty("No tasks.")); return; }
        foreach (var card in values) panel.Children.Add(TaskCard(card));
    }

    private static UIElement Empty(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static UIElement Card(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis });
        var border = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(5), Child = stack };
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ShellPanelBrush"];
        AutomationProperties.SetName(border, $"{title}: {subtitle}");
        return border;
    }

    private UIElement TaskCard(GridTaskboardCard card)
    {
        var button = new Button
        {
            Tag = card.TaskId,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = Card(card.Title, card.Subtitle),
        };
        AutomationProperties.SetName(button, $"Open activity task {card.Title}");
        button.Click += OnTaskCardClicked;
        return button;
    }

    private void OnTaskCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string taskId }) openTask?.Invoke(taskId);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => closeBoard?.Invoke();
    private void OnComposeClicked(object sender, RoutedEventArgs e)
    {
        var value = TaskComposer.Text.Trim();
        if (value.Length == 0) return;
        composeTask?.Invoke(value);
        TaskComposer.Text = string.Empty;
    }
    private void OnAttachClicked(object sender, RoutedEventArgs e) => attach?.Invoke();
}
