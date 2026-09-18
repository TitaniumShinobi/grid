using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Grid.App.Services;

public interface IEvidenceFilePicker
{
    Task<ImmutableArray<string>> PickFilesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the WinUI window association required by the unpackaged multi-file picker.
/// It returns only paths explicitly selected by the user; the case service remains
/// responsible for authorization, validation, hashing, and durable import.
/// </summary>
public sealed class WindowsEvidenceFilePicker : IEvidenceFilePicker
{
    private readonly Window owner;

    public WindowsEvidenceFilePicker(Window owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task<ImmutableArray<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.List,
            SettingsIdentifier = "Grid.InvestigationEvidence",
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

        var files = await picker.PickMultipleFilesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return files
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }
}
