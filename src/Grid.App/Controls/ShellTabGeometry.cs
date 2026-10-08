using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Grid.App.Controls;

/// <summary>
/// Normalizes the WinUI TabView header list without changing the content presenter.
/// TabView's template starts its TabListView two DIPs inside the control; the positive
/// two-DIP left margin below places the list four DIPs from the exterior surface edge:
/// one DIP for the owning border plus GRID's canonical three-DIP interior inset.
/// </summary>
internal static class ShellTabGeometry
{
    private static readonly Thickness InteriorHeaderListMargin = new(2, 4, 4, 2);
    private static readonly DependencyProperty OverflowGeometryRegisteredProperty =
        DependencyProperty.RegisterAttached(
            "OverflowGeometryRegistered",
            typeof(bool),
            typeof(ShellTabGeometry),
            new PropertyMetadata(false));

    public static void ApplyInteriorHeader(TabView tabView, string automationName)
    {
        if (FindNamedVisualDescendant(tabView, "TabListView") is not Control tabList)
        {
            return;
        }

        tabList.Padding = new Thickness(0);
        tabList.Margin = InteriorHeaderListMargin;
        AutomationProperties.SetName(tabList, automationName);
        RemoveNativeItemLead(tabList);
        RegisterOverflowGeometry(tabList);

        // The presenter subtree can be realized one dispatcher pass after TabView.Loaded.
        tabView.DispatcherQueue.TryEnqueue(() =>
        {
            RemoveNativeItemLead(tabList);
            UpdatePreviousTabNavigation(tabList);
        });
    }

    private static void RemoveNativeItemLead(DependencyObject tabList)
    {
        if (FindNamedVisualDescendant(tabList, "TabsItemsPresenter") is ItemsPresenter presenter)
        {
            presenter.Header = null;
            presenter.Footer = null;
        }

        if (FindNamedVisualDescendant(tabList, "ScrollContentPresenter") is ScrollContentPresenter scrollPresenter)
        {
            scrollPresenter.Padding = new Thickness(0);
            if (VisualTreeHelper.GetParent(scrollPresenter) is Microsoft.UI.Xaml.Controls.Grid scrollGrid &&
                scrollGrid.ColumnDefinitions.Count > 0)
            {
                // WinUI reserves two DIPs for the collapsed previous-tab button.
                // Auto sizing still restores the column when overflow navigation appears.
                scrollGrid.ColumnDefinitions[0].MinWidth = 0;
            }
        }
    }

    private static void RegisterOverflowGeometry(Control tabList)
    {
        if ((bool)tabList.GetValue(OverflowGeometryRegisteredProperty))
        {
            return;
        }

        if (FindNamedVisualDescendant(tabList, "ScrollViewer") is not ScrollViewer scrollViewer)
        {
            return;
        }

        tabList.SetValue(OverflowGeometryRegisteredProperty, true);
        scrollViewer.ViewChanged += (_, _) =>
            tabList.DispatcherQueue.TryEnqueue(() => UpdatePreviousTabNavigation(tabList));
        scrollViewer.SizeChanged += (_, _) =>
            tabList.DispatcherQueue.TryEnqueue(() => UpdatePreviousTabNavigation(tabList));
        UpdatePreviousTabNavigation(tabList);
    }

    private static void UpdatePreviousTabNavigation(DependencyObject tabList)
    {
        if (FindNamedVisualDescendant(tabList, "ScrollViewer") is not ScrollViewer scrollViewer ||
            FindNamedVisualDescendant(tabList, "ScrollDecreaseButtonContainer") is not UIElement previousButton)
        {
            return;
        }

        // At the leading edge the previous-tab affordance has no action. Collapsing
        // only that inactive native container keeps the first tab at GRID's canonical
        // three-DIP inset. It returns as soon as the native list scrolls, preserving
        // bidirectional overflow navigation without reserving blank lead space.
        previousButton.Visibility = scrollViewer.HorizontalOffset <= 0.5
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static FrameworkElement? FindNamedVisualDescendant(DependencyObject root, string name)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name.Equals(name, StringComparison.Ordinal))
            {
                return element;
            }

            var descendant = FindNamedVisualDescendant(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
