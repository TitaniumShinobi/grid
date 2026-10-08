using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grid.App.Services;

public interface IExecutableFilePicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken = default);
}

/// <summary>Returns only the exact executable explicitly selected by the user.</summary>
public sealed class WindowsExecutableFilePicker(Window owner) : IExecutableFilePicker
{
    private readonly Window owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List,
            SettingsIdentifier = "Grid.UserToolExecutable",
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file?.Path;
    }
}
