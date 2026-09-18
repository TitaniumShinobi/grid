using Grid.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class ProviderChainDialog : ContentDialog
{
    public ProviderChainDialog(ProviderChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        InitializeComponent();
        VirtualPathText.Text = chain.VirtualPath;
        ResolutionText.Text = $"{chain.WinnerConfidence} · {chain.ResolutionDetail}";
        ProvidersList.ItemsSource = chain.Providers
            .Select(provider => new ProviderRow(
                provider.SourceName,
                provider.Kind.ToString(),
                provider.Reason,
                provider.IsWinner ? "ESTABLISHED WINNER" : "OBSERVED PROVIDER"))
            .ToArray();
        if (!chain.Discrepancies.IsEmpty)
        {
            DiscrepancyStatus.IsOpen = true;
            DiscrepancyStatus.Title = $"{chain.Discrepancies.Length} resolution discrepancy" +
                (chain.Discrepancies.Length == 1 ? string.Empty : "ies");
            DiscrepancyStatus.Message = string.Join(" ", chain.Discrepancies.Select(value => value.Detail));
        }
    }

    private sealed record ProviderRow(string SourceName, string Kind, string Reason, string WinnerLabel);
}
