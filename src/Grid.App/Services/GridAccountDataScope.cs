using System.Security.Cryptography;
using System.Text;

namespace Grid.App.Services;

/// <summary>
/// Resolves local GRID product state for one authenticated account on this
/// Windows device. The raw account identity is never used as a path segment.
/// </summary>
public static class GridAccountDataScope
{
    public static string ResolveBaseDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid")
            : Path.GetFullPath(configured);
    }

    public static string Resolve(string baseDataRoot, string stableAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableAccountId);

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(stableAccountId)))
            .ToLowerInvariant();

        return Path.Combine(Path.GetFullPath(baseDataRoot), "accounts", "v1", digest);
    }
}
