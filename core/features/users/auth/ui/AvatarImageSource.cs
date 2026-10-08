// Requires WinUI 3 (Microsoft.WindowsAppSDK). This file is UI-only: it is
// intentionally excluded from the non-WinUI verification/test build in
// tests/Grid.Auth.Tests. Grid's WinUI projects include it automatically via
// SDK-style Compile globbing.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Grid.Auth.Features;

/// <summary>
/// Loads the signed-in account avatar (OAuth/profile image surfaced by AUTH in
/// user.Picture) as a WinUI <see cref="ImageSource"/> for Grid's circular
/// account thumbnail (Ellipse clip or PersonPicture). Handles data: URLs
/// (Microsoft Graph photo) and http(s) avatar URLs; returns null when the user
/// has no picture so the shell falls back to its PersonPicture initials.
/// </summary>
public static class AvatarImageSource
{
    /// <summary>Cap on downloaded avatar body to keep the shell cheap.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public static async Task<ImageSource?> FromPictureAsync(string? picture, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(picture)) return null;
        try
        {
            if (picture.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = DataUrlBytes(picture);
                if (data is null) return null;
                return await FromBytesAsync(data, ct);
            }
            if (Uri.TryCreate(picture, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Grid-Auth/1.0");
                var bytes = await client.GetByteArrayAsync(uri, ct);
                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBytes) return null;
                return await FromBytesAsync(bytes, ct);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> FromBytesAsync(byte[] bytes, CancellationToken ct)
    {
        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
        {
            await image.SetSourceAsync(stream.AsRandomAccessStream());
        }
        return image;
    }

    private static byte[]? DataUrlBytes(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return null;
        var meta = dataUrl[..comma];
        var payload = dataUrl[(comma + 1)..];
        try
        {
            if (meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.FromBase64String(payload);
            }
            return Encoding.UTF8.GetBytes(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}