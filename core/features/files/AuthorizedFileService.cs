using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Files;

public enum AuthorizedFileFailure
{
    None,
    InvalidPath,
    OutsideRoot,
    ReparsePoint,
    NotFound,
    AccessDenied,
    IoFailure,
}

public sealed record AuthorizedRoot(string Id, string Path);

public sealed record AuthorizedFileResult<T>(
    T? Value,
    AuthorizedFileFailure Failure = AuthorizedFileFailure.None,
    string? Detail = null)
{
    public bool Success => Failure == AuthorizedFileFailure.None;
}

public sealed record AuthorizedDirectoryEntry(
    string Name,
    string RelativePath,
    bool IsDirectory);

public sealed class AuthorizedFileService
{
    private readonly Dictionary<string, string> roots =
        new(StringComparer.Ordinal);

    public AuthorizedRoot AuthorizeRoot(string id, string path)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Root id is required.", nameof(id));

        var canonical = CanonicalDirectory(path);

        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException(canonical);

        RejectReparsePoint(canonical);

        roots[id] = canonical;
        return new AuthorizedRoot(id, canonical);
    }

    public bool RevokeRoot(string id) => roots.Remove(id);

    public AuthorizedFileResult<string> Resolve(
        string rootId,
        string relativePath,
        bool requireFile = false,
        bool requireDirectory = false)
    {
        if (!roots.TryGetValue(rootId, out var root))
            return Fail<string>(AuthorizedFileFailure.AccessDenied, "Root is not authorized.");

        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = ".";

        if (Path.IsPathFullyQualified(relativePath))
            return Fail<string>(AuthorizedFileFailure.InvalidPath, "Absolute paths are not accepted.");

        string candidate;
        try
        {
            candidate = Path.GetFullPath(relativePath, root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail<string>(AuthorizedFileFailure.InvalidPath, e.Message);
        }

        if (!ContainedBy(root, candidate))
            return Fail<string>(AuthorizedFileFailure.OutsideRoot, "Path escapes the authorized root.");

        try
        {
            RejectReparseTraversal(root, candidate);

            if (requireFile && !File.Exists(candidate))
                return Fail<string>(AuthorizedFileFailure.NotFound, "File was not found.");

            if (requireDirectory && !Directory.Exists(candidate))
                return Fail<string>(AuthorizedFileFailure.NotFound, "Directory was not found.");

            return new(candidate);
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail<string>(AuthorizedFileFailure.AccessDenied, e.Message);
        }
        catch (InvalidDataException e)
        {
            return Fail<string>(AuthorizedFileFailure.ReparsePoint, e.Message);
        }
        catch (IOException e)
        {
            return Fail<string>(AuthorizedFileFailure.IoFailure, e.Message);
        }
    }

    public AuthorizedFileResult<IReadOnlyList<AuthorizedDirectoryEntry>> ListDirectory(
        string rootId,
        string relativePath = "")
    {
        var resolved = Resolve(rootId, relativePath, requireDirectory: true);
        if (!resolved.Success)
            return Fail<IReadOnlyList<AuthorizedDirectoryEntry>>(resolved.Failure, resolved.Detail);

        try
        {
            var root = roots[rootId];
            var entries = new DirectoryInfo(resolved.Value!)
                .EnumerateFileSystemInfos()
                .Select(info =>
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        return null;

                    var relative = Path.GetRelativePath(root, info.FullName)
                        .Replace('\\', '/');

                    return new AuthorizedDirectoryEntry(
                        info.Name,
                        relative,
                        info is DirectoryInfo);
                })
                .Where(value => value is not null)
                .Cast<AuthorizedDirectoryEntry>()
                .OrderByDescending(value => value.IsDirectory)
                .ThenBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return new(entries);
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail<IReadOnlyList<AuthorizedDirectoryEntry>>(
                AuthorizedFileFailure.AccessDenied, e.Message);
        }
        catch (IOException e)
        {
            return Fail<IReadOnlyList<AuthorizedDirectoryEntry>>(
                AuthorizedFileFailure.IoFailure, e.Message);
        }
    }

    public async Task<AuthorizedFileResult<string>> ReadTextAsync(
        string rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(rootId, relativePath, requireFile: true);
        if (!resolved.Success)
            return Fail<string>(resolved.Failure, resolved.Detail);

        try
        {
            var text = await File.ReadAllTextAsync(
                resolved.Value!,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            return new(text);
        }
        catch (UnauthorizedAccessException e)
        {
            return Fail<string>(AuthorizedFileFailure.AccessDenied, e.Message);
        }
        catch (IOException e)
        {
            return Fail<string>(AuthorizedFileFailure.IoFailure, e.Message);
        }
    }

    private static bool ContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);

        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    private static string CanonicalDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void RejectReparseTraversal(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;

        RejectReparsePoint(current);

        if (relative == ".")
            return;

        foreach (var part in relative.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            current = Path.Combine(current, part);

            if (File.Exists(current) || Directory.Exists(current))
                RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Reparse points are outside GRID's authorized file boundary: {path}");
    }

    private static AuthorizedFileResult<T> Fail<T>(
        AuthorizedFileFailure failure,
        string? detail) =>
        new(default, failure, detail);
}
