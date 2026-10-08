using System.Collections.ObjectModel;
using System.Diagnostics;
using Grid.Core.Models;
using Grid.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Grid.App.Controls;

/// <summary>Attached, replace-level navigation over immutable prepared pages. Only realized rows create controls.</summary>
public sealed class CanonicalSelectorNavigationControl : UserControl
{
    private readonly Button selectorButton;
    private readonly Flyout flyout;
    private readonly ListView rows;
    private readonly ObservableCollection<NavigationRow> rowItems = [];
    private readonly StackPanel rootActions;
    private readonly Microsoft.UI.Xaml.Controls.Grid header;
    private readonly TextBlock statusText;
    private readonly StackPanel scaffoldContent;
    private readonly ScrollViewer scaffoldViewport;
    private readonly Microsoft.UI.Xaml.Controls.Grid flyoutContent;
    private bool scaffoldMode;
    private string? scaffoldParent;
    private string? scaffoldCaptionPath;
    private bool scaffoldCaptionFromSelection;
    private string? scaffoldCaptionFallback;
    private string? scaffoldCaptionFallbackIdentity;
    private HashSet<string> scaffoldSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> scaffoldSortViews = new(StringComparer.Ordinal);
    private ScrollViewer? scrollViewer;
    private Button? otherButton;
    private KnowledgeKind knowledgeKind;
    private string title = string.Empty;
    private CanonicalNavigationPathId? currentPath;
    private CanonicalSelectorResult? currentResult;
    private Func<KnowledgeKind, CanonicalNavigationPathId?, CanonicalSelectorResult?>? query;
    private Func<KnowledgeKind, CanonicalNavigationPathId?, string?, CancellationToken, Task<PreparedCanonicalNavigationPage?>>? asyncQuery;
    private CancellationTokenSource? requestCancellation;
    private long generation;
    private string? continuationCursor;
    private bool loading;
    private bool isOpen;
    private string runtimeStatus = "Canonical catalog not evaluated.";
    private string automationName = "Canonical selector";
    private string automationId = "canonical-selector:unconfigured";
    private string automationHelpText = "Canonical selector not configured.";

    public CanonicalSelectorNavigationControl()
    {
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Control);
        selectorButton = new Button
        {
            Height = CanonicalSelectorControlContract.V1.ClosedHeightDip,
            MinWidth = 0, Padding = new Thickness(9, 0, 9, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, FontSize = 11,
            CornerRadius = new CornerRadius(0),
        };
        selectorButton.ContentTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <TextBlock Text="{Binding}" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
            </DataTemplate>
            """);
        header = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 6, Padding = new Thickness(4, 2, 4, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6) };
        rootActions = new StackPanel { Spacing = 3 };
        rows = new ListView
        {
            ItemsSource = rowItems, SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Grid Height="28" HorizontalAlignment="Stretch">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                    <Button Grid.Column="0" Content="" Height="28" Padding="8,0,4,0" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"/>
                    <Button Grid.Column="1" Content=">" Height="28" MinWidth="28" Padding="8,0,8,0" Visibility="Collapsed"/>
                  </Grid>
                </DataTemplate>
                """),
        };
        var containerStyle = new Style(typeof(ListViewItem));
        containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        containerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        rows.ItemContainerStyle = containerStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(rows, ScrollBarVisibility.Auto);
        rows.ContainerContentChanging += OnContainerContentChanging;
        rows.Loaded += (_, _) =>
        {
            var found = FindScrollViewer(rows);
            if (ReferenceEquals(found, scrollViewer)) return;
            if (scrollViewer is not null) scrollViewer.ViewChanged -= OnScrollViewChanged;
            scrollViewer = found;
            if (scrollViewer is not null) scrollViewer.ViewChanged += OnScrollViewChanged;
        };
        var content = new Microsoft.UI.Xaml.Controls.Grid { RowSpacing = 0 };
        flyoutContent = content;
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(rows, 1);
        content.Children.Add(rows);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(statusText, 1);
        content.Children.Add(statusText);
        scaffoldContent = new StackPanel { Spacing = 0 };
        scaffoldViewport = new ScrollViewer
        {
            Content = scaffoldContent, Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetRow(scaffoldViewport, 1);
        content.Children.Add(scaffoldViewport);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(rootActions, 2);
        content.Children.Add(rootActions);
        flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, Content = content };
        flyout.FlyoutPresenterStyle = (Style)XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="FlyoutPresenter">
              <Setter Property="CornerRadius" Value="0"/>
              <Setter Property="Padding" Value="4"/>
              <Setter Property="BorderThickness" Value="1"/>
            </Style>
            """);
        selectorButton.Flyout = flyout;
        flyout.Opening += (_, _) =>
        {
            isOpen = true;
            if (scaffoldMode) scaffoldParent = null;
            _ = RenderCurrentLevelAsync();
        };
        flyout.Closed += (_, _) =>
        {
            isOpen = false;
            CancelRequest();
            if (!scaffoldMode)
            {
                currentPath = null;
                currentResult = null;
                continuationCursor = null;
            }
        };
        Unloaded += (_, _) => CancelRequest();
        Content = selectorButton;
    }

    public event EventHandler<CanonicalSelectorSelection>? CanonicalSelectionCommitted;
    public event EventHandler<SelectorScaffoldSelection>? ScaffoldSelectionCommitted;
    public event EventHandler? OtherRequested;

    /// <summary>Uses the very same closed-field style as the other DIF selectors, without local overrides.</summary>
    public void ApplyClosedSelectorStyle(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        selectorButton.ClearValue(FrameworkElement.HeightProperty);
        selectorButton.ClearValue(FrameworkElement.MinWidthProperty);
        selectorButton.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        selectorButton.ClearValue(Control.PaddingProperty);
        selectorButton.ClearValue(Control.FontSizeProperty);
        selectorButton.ClearValue(Control.HorizontalContentAlignmentProperty);
        selectorButton.ClearValue(Control.CornerRadiusProperty);
        selectorButton.ClearValue(Control.BackgroundProperty);
        selectorButton.ClearValue(Control.BorderBrushProperty);
        selectorButton.ClearValue(Control.BorderThicknessProperty);
        selectorButton.ClearValue(Control.TemplateProperty);
        selectorButton.ClearValue(ContentControl.ContentTemplateProperty);
        selectorButton.Style = style;
    }

    /// <summary>Renders local, selectable schema context without canonical identities or catalog queries.</summary>
    public void ConfigureScaffold(KnowledgeKind kind, IReadOnlyCollection<string> selectedPathIds)
    {
        ArgumentNullException.ThrowIfNull(selectedPathIds);
        var sameKind = scaffoldMode && knowledgeKind == kind;
        var previousParent = sameKind ? scaffoldParent : null;
        if (!sameKind)
        {
            scaffoldCaptionPath = null;
            scaffoldCaptionFallback = null;
            scaffoldCaptionFallbackIdentity = null;
            scaffoldCaptionFromSelection = false;
        }
        query = null;
        asyncQuery = null;
        ConfigurePresentation(SelectorScaffoldContract.Title(kind), kind, null, null);
        scaffoldMode = true;
        scaffoldParent = previousParent;
        scaffoldSelections = selectedPathIds.Where(path => SelectorScaffoldContract.Resolve(kind, path) is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (scaffoldCaptionFromSelection && scaffoldCaptionPath is not null && !scaffoldSelections.Contains(scaffoldCaptionPath))
        {
            scaffoldCaptionPath = null;
            scaffoldCaptionFromSelection = false;
        }
        continuationCursor = null;
        automationHelpText = $"{title} scaffold — not populated. Select labels as context; use arrows to navigate.";
        AutomationProperties.SetHelpText(selectorButton, automationHelpText);
        AutomationProperties.SetHelpText(this, automationHelpText);
        runtimeStatus = $"{title} scaffold; canonical queries: 0; no catalog access.";
        AutomationProperties.SetItemStatus(selectorButton, runtimeStatus);
        FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();
        UpdateScaffoldCaption();
        if (isOpen) RenderScaffold();
    }

    public void ConfigureLocationScaffold() => ConfigureScaffold(KnowledgeKind.Location, []);

    /// <summary>Changed selections replace the caption; unrelated refreshes retain the latest navigation anchor.</summary>
    public void SetScaffoldCaptionFallback(string? label, string? selectionIdentity = null)
    {
        var nextFallback = string.IsNullOrWhiteSpace(label) ? null : label;
        if (!string.Equals(scaffoldCaptionFallback, nextFallback, StringComparison.Ordinal) ||
            !string.Equals(scaffoldCaptionFallbackIdentity, selectionIdentity, StringComparison.Ordinal))
        {
            scaffoldCaptionPath = null;
            scaffoldCaptionFromSelection = false;
        }
        scaffoldCaptionFallback = nextFallback;
        scaffoldCaptionFallbackIdentity = selectionIdentity;
        if (scaffoldMode) UpdateScaffoldCaption();
    }

    /// <summary>Called when the owning draft/account/profile changes, never for an ordinary preview refresh.</summary>
    public void ResetScaffoldPresentationContext()
    {
        CancelRequest();
        scaffoldParent = null;
        scaffoldCaptionPath = null;
        scaffoldCaptionFromSelection = false;
        scaffoldCaptionFallback = null;
        scaffoldCaptionFallbackIdentity = null;
        scaffoldSortViews.Clear();
        flyout.Hide();
        if (scaffoldMode) UpdateScaffoldCaption();
    }

    private void UpdateScaffoldCaption()
    {
        var label = scaffoldCaptionPath is null ? null : SelectorScaffoldContract.Resolve(knowledgeKind, scaffoldCaptionPath)?.Label;
        var caption = label ?? scaffoldCaptionFallback ?? title;
        selectorButton.Content = caption;
        AutomationProperties.SetHelpText(selectorButton, $"{automationHelpText} Closed caption: {caption}.");
        AutomationProperties.SetHelpText(this, $"{automationHelpText} Closed caption: {caption}.");
    }

    public void Configure(string selectorTitle, KnowledgeKind selectorKind,
        Func<KnowledgeKind, CanonicalNavigationPathId?, CanonicalSelectorResult?> queryProvider,
        CanonicalSelectorSelection? selected, string? selectedDisplayAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(queryProvider);
        query = queryProvider;
        asyncQuery = null;
        ConfigurePresentation(selectorTitle, selectorKind, selected, selectedDisplayAnchor);
    }

    public void ConfigureAsync(string selectorTitle, KnowledgeKind selectorKind,
        Func<KnowledgeKind, CanonicalNavigationPathId?, string?, CancellationToken, Task<PreparedCanonicalNavigationPage?>> queryProvider,
        CanonicalSelectorSelection? selected, string? selectedDisplayAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(queryProvider);
        asyncQuery = queryProvider;
        query = null;
        ConfigurePresentation(selectorTitle, selectorKind, selected, selectedDisplayAnchor);
    }

    private void ConfigurePresentation(string selectorTitle, KnowledgeKind selectorKind,
        CanonicalSelectorSelection? selected, string? selectedDisplayAnchor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorTitle);
        CancelRequest();
        scaffoldMode = false;
        scaffoldViewport.Visibility = Visibility.Collapsed;
        rows.Visibility = Visibility.Visible;
        header.Visibility = Visibility.Visible;
        header.RowDefinitions.Clear();
        header.ColumnDefinitions[0].Width = GridLength.Auto;
        header.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        title = selectorTitle;
        knowledgeKind = selectorKind;
        AutomationProperties.SetAutomationId(flyoutContent, $"selector-flyout-surface:{selectorKind}");
        AutomationProperties.SetAccessibilityView(flyoutContent, AccessibilityView.Control);
        AutomationProperties.SetAutomationId(rows, $"canonical-level:{selectorKind}");
        currentPath = null;
        currentResult = null;
        rowItems.Clear();
        selectorButton.Content = selected?.KnowledgeRecordId is not null
            ? $"{title} \u00B7 {selectedDisplayAnchor ?? "Selected"}" : $"{title} \u00B7 Optional";
        automationName = $"Select {title}";
        AutomationProperties.SetName(selectorButton, automationName);
        AutomationProperties.SetName(this, automationName);
        var coordinate = selected?.KnowledgeRecordId is KnowledgeRecordId recordId && selected.SelectedPathId is CanonicalNavigationPathId pathId
            ? $"canonical-selection:{selectorKind}:{recordId.Value}:{pathId.Value}:{selected.CatalogRevisionId.Value}:{selected.CatalogCompositionId.Value}"
            : $"canonical-selector:{selectorKind}";
        automationId = coordinate;
        automationHelpText = coordinate;
        AutomationProperties.SetAutomationId(selectorButton, coordinate);
        AutomationProperties.SetHelpText(selectorButton, coordinate);
        AutomationProperties.SetHelpText(this, coordinate);
        FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    public void ResetNavigation() { CancelRequest(); currentPath = null; currentResult = null; scaffoldParent = null; }
    public void SetRuntimeStatus(string status)
    {
        if (scaffoldMode) return;
        runtimeStatus = string.IsNullOrWhiteSpace(status) ? "Canonical catalog not evaluated." : status;
        AutomationProperties.SetItemStatus(selectorButton, runtimeStatus);
        if (!runtimeStatus.StartsWith("Exact:", StringComparison.Ordinal) && !runtimeStatus.StartsWith("Exact (", StringComparison.Ordinal))
        {
            var separator = runtimeStatus.IndexOf(':');
            selectorButton.Content = $"{title} \u00B7 {(separator > 0 ? runtimeStatus[..separator] : "Unavailable")}";
        }
    }
    public void SetClosedCaption(string caption) { ArgumentException.ThrowIfNullOrWhiteSpace(caption); selectorButton.Content = caption; }
    public void SetInteractionEnabled(bool enabled) { selectorButton.IsEnabled = enabled; if (!enabled) flyout.Hide(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new CanonicalSelectorAutomationPeer(this);
    private void InvokeFromAutomation() => selectorButton.Flyout?.ShowAt(selectorButton);

    private void CancelRequest()
    {
        generation++;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = null;
        loading = false;
    }

    private async Task RenderCurrentLevelAsync(bool append = false)
    {
        if (scaffoldMode)
        {
            if (!append) RenderScaffold();
            return;
        }
        if (append && (loading || continuationCursor is null)) return;
        if (!append)
        {
            CancelRequest();
            rowItems.Clear(); header.Children.Clear(); rootActions.Children.Clear();
            otherButton = null; continuationCursor = null;
            statusText.Text = "Loading registered knowledge…";
            statusText.Visibility = Visibility.Visible;
        }
        var requestGeneration = generation;
        requestCancellation ??= new CancellationTokenSource();
        var cancellationToken = requestCancellation.Token;
        loading = true;
        var timer = Stopwatch.StartNew();
        try
        {
            PreparedCanonicalNavigationPage? page;
            if (asyncQuery is not null)
                page = await asyncQuery(knowledgeKind, currentPath, append ? continuationCursor : null, cancellationToken);
            else
            {
                var result = query?.Invoke(knowledgeKind, currentPath);
                page = result is null ? null : new PreparedCanonicalNavigationPage(result, null);
            }
            var queryMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (cancellationToken.IsCancellationRequested || requestGeneration != generation || !isOpen) return;
            if (page is null)
            {
                currentResult = null;
                statusText.Text = "Canonical knowledge unavailable for this exact game build.";
                AutomationProperties.SetItemStatus(selectorButton, $"{runtimeStatus} Query unavailable.");
                AddOtherIfRoot();
                return;
            }
            if (append && page.Result.CurrentPathId != currentPath)
                throw new InvalidDataException("Prepared page changed its navigation path.");
            currentResult = page.Result;
            currentPath = currentResult.CurrentPathId;
            continuationCursor = page.ContinuationCursor;
            foreach (var child in currentResult.ImmediateChildren)
                rowItems.Add(new NavigationRow(child, currentResult));
            statusText.Visibility = Visibility.Collapsed;
            if (!append)
            {
                RenderHeader();
                AddOtherIfRoot();
                // Resolve the new bounded viewport before resetting its anchor. ChangeView before
                // layout can otherwise be undone by ListView's recycled-container scroll anchoring.
                rows.UpdateLayout();
                if (rowItems.Count > 0) rows.ScrollIntoView(rowItems[0], ScrollIntoViewAlignment.Leading);
                scrollViewer?.ChangeView(null, 0, null, true);
                rows.UpdateLayout();
            }
            AutomationProperties.SetItemStatus(selectorButton,
                $"{runtimeStatus} Immediate rows loaded: {rowItems.Count}. More: {continuationCursor is not null}. Page query ms: {queryMilliseconds:F3}.");
            // One real composition frame records populated-view latency; no full UIA enumeration is inside this timer.
            EventHandler<object>? onFrame = null;
            onFrame = (_, _) =>
            {
                CompositionTarget.Rendering -= onFrame;
                if (requestGeneration != generation || !isOpen) return;
                AutomationProperties.SetItemStatus(selectorButton,
                    $"{runtimeStatus} Immediate rows loaded: {rowItems.Count}. More: {continuationCursor is not null}. Page query ms: {queryMilliseconds:F3}. Populated frame ms: {timer.Elapsed.TotalMilliseconds:F3}.");
                if (!append && rows.ContainerFromIndex(0) is ListViewItem firstContainer &&
                    firstContainer.ContentTemplateRoot is Microsoft.UI.Xaml.Controls.Grid firstRow &&
                    firstRow.Children[0] is Button firstLabel)
                    firstLabel.Focus(FocusState.Programmatic);
            };
            CompositionTarget.Rendering += onFrame;
        }
        catch (OperationCanceledException) { /* A linked account/context lifetime may cancel independently of this control. */ }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            if (requestGeneration != generation) return;
            continuationCursor = null;
            statusText.Text = "Prepared canonical knowledge could not be read.";
            statusText.Visibility = Visibility.Visible;
            AutomationProperties.SetItemStatus(selectorButton, $"Invalid: {exception.Message}");
        }
        finally { if (requestGeneration == generation) loading = false; }
    }

    private void RenderScaffold()
    {
        CancelRequest();
        var renderGeneration = generation;
        currentPath = null;
        currentResult = null;
        continuationCursor = null;
        rowItems.Clear();
        rows.Visibility = Visibility.Collapsed;
        statusText.Visibility = Visibility.Collapsed;
        header.Children.Clear();
        header.RowDefinitions.Clear();
        rootActions.Children.Clear();
        otherButton = null;
        scaffoldContent.Children.Clear();
        scaffoldViewport.Visibility = Visibility.Visible;
        var parent = scaffoldParent is null ? null : SelectorScaffoldContract.Resolve(knowledgeKind, scaffoldParent);
        var children = parent?.Children ?? SelectorScaffoldContract.Roots(knowledgeKind);
        header.Visibility = parent is null ? Visibility.Collapsed : Visibility.Visible;

        if (parent is not null)
        {
            header.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[1].Width = GridLength.Auto;
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var back = FlatScaffoldButton("Back");
            AutomationProperties.SetName(back, $"Back one {title} level");
            AutomationProperties.SetAutomationId(back, $"scaffold-back:{knowledgeKind}");
            back.Click += (_, _) =>
            {
                if (!scaffoldMode || generation != renderGeneration || scaffoldParent is null) return;
                var separator = scaffoldParent.LastIndexOf('/');
                scaffoldParent = separator < 0 ? null : scaffoldParent[..separator];
                scaffoldCaptionPath = scaffoldParent;
                scaffoldCaptionFromSelection = false;
                UpdateScaffoldCaption();
                RenderScaffold();
            };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(back, 1);
            header.Children.Add(back);
            var heading = new TextBlock
            {
                Text = parent.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 4, 0),
            };
            AutomationProperties.SetAutomationId(heading, $"scaffold-context:{knowledgeKind}");
            header.Children.Add(heading);
            var divider = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 128, 128, 128)),
            };
            AutomationProperties.SetAutomationId(divider, $"scaffold-context-separator:{knowledgeKind}");
            AutomationProperties.SetName(divider, "Context separator");
            AutomationProperties.SetAccessibilityView(divider, AccessibilityView.Control);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(divider, 1);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(divider, 2);
            header.Children.Add(divider);
        }

        if (parent is not null && !parent.SortViews.IsEmpty)
        {
            var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            var sortKey = knowledgeKind + ":" + parent.Key;
            var selectedView = scaffoldSortViews.GetValueOrDefault(sortKey, "Default");
            foreach (var view in new[] { "Default" }.Concat(parent.SortViews))
            {
                var sort = FlatScaffoldButton(view, selectedView == view);
                sort.FontSize = 10;
                AutomationProperties.SetName(sort, $"Sort by {view}");
                AutomationProperties.SetAutomationId(sort, $"scaffold-sort:{knowledgeKind}:{view}");
                AutomationProperties.SetItemStatus(sort, selectedView == view ? "Selected sort view" : "Alternate sort view");
                sort.Click += (_, _) =>
                {
                    if (!scaffoldMode || generation != renderGeneration || scaffoldParent != parent.Key) return;
                    scaffoldSortViews[sortKey] = view;
                    RenderScaffold();
                };
                sortRow.Children.Add(sort);
            }
            scaffoldContent.Children.Add(sortRow);
        }

        var chevronColumnWidth = children.Any(child => !child.Children.IsEmpty) ? 28d : 0d;
        foreach (var child in children)
        {
            var row = new Microsoft.UI.Xaml.Controls.Grid { MinHeight = 28, ColumnSpacing = 0 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chevronColumnWidth) });
            var selected = scaffoldSelections.Contains(child.Key);
            var label = FlatScaffoldButton(child.Label, selected);
            label.Content = new TextBlock { Text = child.Label, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
            label.HorizontalAlignment = HorizontalAlignment.Stretch;
            label.HorizontalContentAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(label, $"Select {child.Label}");
            AutomationProperties.SetAutomationId(label, $"scaffold-row:{knowledgeKind}:{child.Key}");
            AutomationProperties.SetItemStatus(label, selected ? "Selected scaffold context" : "Unselected scaffold context");
            AutomationProperties.SetHelpText(label, SelectorScaffoldContract.DisplayPath(knowledgeKind, child.Key));
            label.Click += (_, _) =>
            {
                if (!scaffoldMode || generation != renderGeneration) return;
                var selection = new SelectorScaffoldSelection(knowledgeKind, child.Key,
                    SelectorScaffoldContract.DisplayPath(knowledgeKind, child.Key));
                scaffoldCaptionPath = selected ? null : child.Key;
                scaffoldCaptionFromSelection = !selected;
                UpdateScaffoldCaption();
                flyout.Hide();
                ScaffoldSelectionCommitted?.Invoke(this, selection);
            };
            row.Children.Add(label);
            if (!child.Children.IsEmpty)
            {
                var descend = FlatScaffoldButton(">");
                descend.Width = 28;
                descend.Height = 28;
                descend.Padding = new Thickness(0);
                AutomationProperties.SetName(descend, $"Open {child.Label}");
                AutomationProperties.SetAutomationId(descend, $"scaffold-descend:{knowledgeKind}:{child.Key}");
                descend.Click += (_, _) =>
                {
                    if (!scaffoldMode || generation != renderGeneration) return;
                    scaffoldParent = child.Key;
                    scaffoldCaptionPath = child.Key;
                    scaffoldCaptionFromSelection = false;
                    UpdateScaffoldCaption();
                    RenderScaffold();
                };
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(descend, 1);
                row.Children.Add(descend);
            }
            scaffoldContent.Children.Add(row);
        }
        if (parent is null) AddOtherIfRoot();
        SelectorFlyoutSizing.Apply(selectorButton, flyout, flyoutContent, scaffoldViewport, header, rootActions);
        scaffoldViewport.ChangeView(null, 0, null, true);
        AutomationProperties.SetItemStatus(selectorButton,
            $"{runtimeStatus} Scaffold rows: {children.Length}. Canonical records: 0. Branch: {scaffoldParent ?? "root"}.");
    }

    private static Button FlatScaffoldButton(string text, bool selected = false) => new()
    {
        Content = text, MinHeight = 28, Padding = new Thickness(8, 3, 8, 3),
        CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0, 0, 0, 1),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
        Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(45, 128, 160, 200) : Microsoft.UI.Colors.Transparent),
        FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };

    private void RenderHeader()
    {
        header.Children.Clear();
        if (currentResult is null) return;
        if (currentResult.ParentPathId is CanonicalNavigationPathId parent)
        {
            var back = new Button { Content = "Back", Tag = parent, Height = 28, Padding = new Thickness(8, 0, 8, 0) };
            AutomationProperties.SetName(back, $"Back one {title} level");
            back.Click += (_, _) => { currentPath = parent; _ = RenderCurrentLevelAsync(); };
            header.Children.Add(back);
        }
        var heading = new TextBlock { Text = currentResult.CurrentNode.DisplayAnchor, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not NavigationRow item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Microsoft.UI.Xaml.Controls.Grid row)
        {
            args.RegisterUpdateCallback(OnContainerContentChanging);
            return;
        }
        var label = (Button)row.Children[0];
        var chevron = (Button)row.Children[1];
        label.Tag = item;
        label.Content = item.Label;
        chevron.Tag = item;
        chevron.Visibility = item.ChevronVisibility;
        AutomationProperties.SetName(label, item.AccessibleName);
        AutomationProperties.SetAutomationId(label, item.AutomationId);
        AutomationProperties.SetName(chevron, $"Open {item.Label}");
        AutomationProperties.SetAutomationId(chevron, $"canonical-descend:{item.Node.PathId.Value}");
        label.Click -= OnLabelClicked; label.Click += OnLabelClicked;
        chevron.Click -= OnDescendNodeClicked; chevron.Click += OnDescendNodeClicked;
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!loading && isOpen && continuationCursor is not null && scrollViewer is not null &&
            scrollViewer.VerticalOffset >= Math.Max(0, scrollViewer.ScrollableHeight - 96))
            _ = RenderCurrentLevelAsync(append: true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
        return null;
    }

    private void AddOtherIfRoot()
    {
        if (currentResult is not null && currentResult.CurrentPathId != currentResult.RootPathId) return;
        if (otherButton is not null) return;
        var otherGeneration = generation;
        var otherScaffoldMode = scaffoldMode;
        var otherKind = knowledgeKind;
        otherButton = scaffoldMode ? FlatScaffoldButton("Other") :
            new Button { Content = "Other", Height = 28, Padding = new Thickness(8, 0, 8, 0) };
        otherButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        otherButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(otherButton, $"Other {title}");
        AutomationProperties.SetAutomationId(otherButton, $"canonical-other:{knowledgeKind}");
        otherButton.Click += (_, _) =>
        {
            if (!isOpen || generation != otherGeneration || scaffoldMode != otherScaffoldMode || knowledgeKind != otherKind)
                return;
            flyout.Hide();
            if (scaffoldMode)
            {
                scaffoldCaptionPath = null;
                scaffoldCaptionFromSelection = false;
                scaffoldCaptionFallback = "Other";
                scaffoldCaptionFallbackIdentity = null;
                UpdateScaffoldCaption();
            }
            else selectorButton.Content = $"{title} - Other";
            OtherRequested?.Invoke(this, EventArgs.Empty);
        };
        rootActions.Children.Add(otherButton);
    }

    private void OnDescendNodeClicked(object sender, RoutedEventArgs e)
    {
        if (scaffoldMode) return;
        if (sender is not Button { Tag: NavigationRow row } || !row.Node.CanDescend) return;
        currentPath = row.Node.PathId;
        _ = RenderCurrentLevelAsync();
    }
    private void OnLabelClicked(object sender, RoutedEventArgs e)
    {
        if (scaffoldMode) return;
        if (sender is not Button { Tag: NavigationRow row }) return;
        if (!row.Node.IsSelectable) { OnDescendNodeClicked(sender, e); return; }
        var selection = CanonicalSelectorProjectionEngine.Select(row.Page, row.Node.PathId);
        selectorButton.Content = $"{title} \u00B7 {row.Node.DisplayAnchor}";
        flyout.Hide();
        CanonicalSelectionCommitted?.Invoke(this, selection);
    }

    private sealed record NavigationRow(CanonicalNavigationNode Node, CanonicalSelectorResult Page)
    {
        public string Label => Node.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier ? $"Source identifier \u00B7 {Node.DisplayAnchor}" : Node.DisplayAnchor;
        public Visibility ChevronVisibility => Node.CanDescend && Node.IsSelectable ? Visibility.Visible : Visibility.Collapsed;
        public string AccessibleName => $"{(Node.CanDescend && !Node.IsSelectable ? "Open" : "Select")} {Label}";
        public string AutomationId => Node.KnowledgeRecordId is KnowledgeRecordId recordId ? $"canonical-record:{recordId.Value}" : $"canonical-path:{Node.PathId.Value}";
    }

    private sealed class CanonicalSelectorAutomationPeer(CanonicalSelectorNavigationControl owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetNameCore() => owner.automationName;
        protected override string GetAutomationIdCore() => owner.automationId;
        protected override string GetHelpTextCore() => owner.automationHelpText;
        protected override string GetClassNameCore() => nameof(CanonicalSelectorNavigationControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);
        public void Invoke() => owner.DispatcherQueue.TryEnqueue(owner.InvokeFromAutomation);
    }
}
