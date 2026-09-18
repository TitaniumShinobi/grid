using Grid.Core.Application;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class HistoryPage : Page
{
    private HistoryState? state;

    public HistoryPage()
    {
        InitializeComponent();
        ActorFilter.ItemsSource = new[] { "All actors" }.Concat(Enum.GetNames<HistoryActor>()).ToArray();
        StatusFilter.ItemsSource = new[] { "All statuses" }.Concat(Enum.GetNames<HistoryEventStatus>()).ToArray();
        ActorFilter.SelectedIndex = 0;
        StatusFilter.SelectedIndex = 0;
    }

    public void BindContext(HistoryState historyState)
    {
        state = historyState ?? throw new ArgumentNullException(nameof(historyState));
        Refresh();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (state is null)
        {
            return;
        }

        var actor = ActorFilter.SelectedIndex > 0 ? (HistoryActor?)(ActorFilter.SelectedIndex - 1) : null;
        var status = StatusFilter.SelectedIndex > 0 ? (HistoryEventStatus?)(StatusFilter.SelectedIndex - 1) : null;
        var rows = state.Entries
            .Where(entry => actor is null || entry.Actor == actor)
            .Where(entry => status is null || entry.Status == status)
            .Select(entry => new HistoryRow(entry))
            .ToArray();
        HistoryList.ItemsSource = rows;
        HistoryList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryIssue.Message = state.Issue ?? string.Empty;
        HistoryIssue.IsOpen = state.Issue is not null;
    }

    private sealed record HistoryRow
    {
        public HistoryRow(HistoryEntry entry)
        {
            Title = entry.Title;
            Detail = entry.Detail;
            Status = entry.Status.ToString();
            Actor = entry.Actor.ToString();
            When = entry.OccurredAtUtc.ToLocalTime().ToString("g");
            Context = string.Join(" · ", new[]
            {
                entry.GameId?.Value,
                entry.InstallationId?.Value,
                entry.ProfileId?.Value,
            }.Where(value => value is not null));
        }

        public string Title { get; }
        public string Detail { get; }
        public string Status { get; }
        public string Actor { get; }
        public string When { get; }
        public string Context { get; }
    }
}
