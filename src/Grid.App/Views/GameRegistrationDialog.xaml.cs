using System.Collections.ObjectModel;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class GameRegistrationDialog:ContentDialog
{
    private static readonly GameAdapterId DiscoveryAdapterId=new("adapter.provider-discovery");
    private readonly GridProviderDiscoveryService discovery;
    private readonly IGameInstallationRegistrationStore registrations;
    private readonly IVortexInstallationConnectionStore? vortexConnections;
    private readonly IInstallationPathPicker picker;
    private readonly ObservableCollection<CandidateRow> rows=[];
    private ProviderDiscoverySnapshot snapshot=new([],[],string.Empty);
    private bool registering;

    public GameRegistrationDialog(GridProviderDiscoveryService discovery,IGameInstallationRegistrationStore registrations,IInstallationPathPicker picker,IVortexInstallationConnectionStore? vortexConnections=null)
    {
        this.discovery=discovery;this.registrations=registrations;this.picker=picker;this.vortexConnections=vortexConnections;InitializeComponent();
        GameCandidates.ItemsSource=rows;Loaded+=OnLoaded;IsPrimaryButtonEnabled=false;
    }
    public IReadOnlyList<GameInstallationRegistration> Registered { get; private set; }=[];
    private async void OnLoaded(object sender,RoutedEventArgs e){Loaded-=OnLoaded;await ScanAsync();}
    private async void OnScanClicked(object sender,RoutedEventArgs e)=>await ScanAsync();
    private async Task ScanAsync()
    {
        SetBusy(true);try{snapshot=await discovery.DiscoverAsync();Bind(snapshot);ShowStatus("Discovery complete",$"{rows.Count} reviewable installation(s) found.",InfoBarSeverity.Success);}
        catch(Exception exception){ShowStatus("Discovery unavailable",exception.Message,InfoBarSeverity.Error);}finally{SetBusy(false);}
    }
    private void Bind(ProviderDiscoverySnapshot value)
    {
        rows.Clear();foreach(var game in value.Games)rows.Add(new(game));
        VortexConnectionPanel.Visibility=vortexConnections is not null&&value.Managers.Any(manager=>manager.ProviderId.Equals("vortex",StringComparison.OrdinalIgnoreCase))?Visibility.Visible:Visibility.Collapsed;
        if(rows.Count>0)GameCandidates.SelectAll();UpdateReview();
    }
    private async void OnBrowseVortexClicked(object sender,RoutedEventArgs e)
    {
        var root=await picker.PickDirectoryAsync();if(!string.IsNullOrWhiteSpace(root))VortexStagingPath.Text=root;
    }
    private async void OnBrowseClicked(object sender,RoutedEventArgs e)
    {
        var root=await picker.PickDirectoryAsync();if(string.IsNullOrWhiteSpace(root))return;
        var legacy=Path.Combine(root,"GTA5.exe");var enhanced=Path.Combine(root,"GTA5_Enhanced.exe");
        ProviderGameCandidate? candidate=File.Exists(legacy)?new("grandtheftautov","Grand Theft Auto V","Legacy","manual","ReadyForReview",root,legacy):
            File.Exists(enhanced)?new("grandtheftautov","Grand Theft Auto V","Enhanced","manual","ReadyForReview",root,enhanced):null;
        if(candidate is null){ShowStatus("Game directory not recognized","Choose a directory containing GTA5.exe or GTA5_Enhanced.exe.",InfoBarSeverity.Warning);return;}
        var existing=rows.FirstOrDefault(value=>value.InstallRoot.Equals(root,StringComparison.OrdinalIgnoreCase));
        if(existing is null){var row=new CandidateRow(candidate);rows.Add(row);GameCandidates.SelectedItems.Add(row);}else GameCandidates.SelectedItems.Add(existing);
        UpdateReview();
    }
    private void OnSelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateReview();
    private void UpdateReview()
    {
        var selected=GameCandidates.SelectedItems.Cast<CandidateRow>().ToArray();IsPrimaryButtonEnabled=selected.Length>0&&!registering;
        ReviewText.Text=selected.Length==0?"Select at least one installation.":string.Join(Environment.NewLine,selected.Select(value=>$"{value.Title} · {value.InstallRoot}"));
        ManagerText.Text=snapshot.Managers.Count==0?"No manager relationship detected.":"Detected managers: "+string.Join(", ",snapshot.Managers.Select(value=>$"{value.ProviderId}{(value.Running?" (running)":string.Empty)}"));
    }
    private async void OnRegisterClicked(ContentDialog sender,ContentDialogButtonClickEventArgs args)
    {
        if(registering){args.Cancel=true;return;}var selected=GameCandidates.SelectedItems.Cast<CandidateRow>().ToArray();if(selected.Length==0){args.Cancel=true;return;}
        args.Cancel=true;var deferral=args.GetDeferral();registering=true;SetBusy(true);
        try
        {
            var managers=snapshot.Managers.Select(value=>value.ProviderId).ToArray();var saved=new List<GameInstallationRegistration>();
            foreach(var row in selected)
            {
                var registration=await registrations.RegisterAsync(new("game.grand-theft-auto-v"),DiscoveryAdapterId,row.Title,row.Candidate.Edition,row.Candidate.ProviderId,row.InstallRoot,row.Candidate.ExecutablePath,managers);
                saved.Add(registration);
                if(vortexConnections is not null&&!string.IsNullOrWhiteSpace(VortexStagingPath.Text))await vortexConnections.ConnectAsync(registration.InstallationId,VortexStagingPath.Text);
            }
            Registered=saved;Hide();
        }
        catch(Exception exception){ShowStatus("Registration failed",exception.Message,InfoBarSeverity.Error);registering=false;SetBusy(false);UpdateReview();}
        finally{deferral.Complete();}
    }
    private void SetBusy(bool value){DiscoveryProgress.IsActive=value;DiscoveryProgress.Visibility=value?Visibility.Visible:Visibility.Collapsed;GameCandidates.IsEnabled=!value;}
    private void ShowStatus(string title,string message,InfoBarSeverity severity){StatusBar.Title=title;StatusBar.Message=message;StatusBar.Severity=severity;StatusBar.IsOpen=true;}
    public sealed class CandidateRow
    {
        public CandidateRow(ProviderGameCandidate candidate){Candidate=candidate;}
        public ProviderGameCandidate Candidate{get;}public string Title=>$"{Candidate.GameDisplayName} · {Candidate.Edition}";public string InstallRoot=>Candidate.InstallRoot;public string Evidence=>$"{Candidate.ProviderId} · {Candidate.Status}";
    }
}
