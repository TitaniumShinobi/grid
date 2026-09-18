using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grid.App.Services;

public interface IInstallationPathPicker
{
    Task<string?> PickExecutableAsync(CancellationToken cancellationToken = default);

    Task<string?> PickDirectoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the WinUI window association required by unpackaged Windows pickers.
/// It returns only the path explicitly selected by the user and performs no validation or probing.
/// </summary>
public sealed class WindowsInstallationPathPicker : IInstallationPathPicker
{
    private readonly Window _owner;

    public WindowsInstallationPathPicker(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task<string?> PickExecutableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            SettingsIdentifier = "Grid.Mo2Executable",
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }

    public async Task<string?> PickDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            SettingsIdentifier = "Grid.Mo2InstanceDirectory",
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_owner));

        var folder = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return folder?.Path;
    }
}
