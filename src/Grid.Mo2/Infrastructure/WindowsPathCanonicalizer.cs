using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class WindowsPathCanonicalizer : IMo2PathCanonicalizer
{
    private readonly Func<string, DriveType> getDriveType;

    public WindowsPathCanonicalizer(Func<string, DriveType>? getDriveType = null) =>
        this.getDriveType = getDriveType ?? (root => new DriveInfo(root).DriveType);

    public bool TryNormalizeLexically(string path, out string normalizedPath, out string? error)
    {
        normalizedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is empty.";
            return false;
        }

        var candidate = path.Trim();
        if (candidate.Any(char.IsControl) || ContainsTraversalSegment(candidate))
        {
            error = "Path contains a disallowed segment.";
            return false;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            error = "Path must be fully qualified.";
            return false;
        }

        if (candidate.StartsWith("\\\\", StringComparison.Ordinal) ||
            candidate.StartsWith("//", StringComparison.Ordinal) ||
            candidate.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            candidate.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            error = "Remote and device paths are not supported.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Path.IsPathFullyQualified(fullPath))
            {
                error = "Path is not fully qualified.";
                return false;
            }

            normalizedPath = Normalize(fullPath);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = exception switch
            {
                UnauthorizedAccessException => "Path access was denied.",
                PathTooLongException => "Path is too long.",
                IOException => "Path is unavailable.",
                _ => "Path syntax is invalid.",
            };
            return false;
        }
    }

    public bool TryCanonicalize(string path, out string canonicalPath, out string? error)
    {
        canonicalPath = string.Empty;
        if (!TryNormalizeLexically(path, out var normalizedPath, out error))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(normalizedPath);
            if (string.IsNullOrWhiteSpace(root) || getDriveType(root) == DriveType.Network)
            {
                error = "Remote and mapped-network paths are not supported.";
                return false;
            }

            var resolvedPath = ResolveExistingLink(normalizedPath);
            if (resolvedPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                resolvedPath.StartsWith("//", StringComparison.Ordinal) ||
                resolvedPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            {
                error = "A reparse point resolved to a remote path.";
                return false;
            }

            var resolvedRoot = Path.GetPathRoot(resolvedPath);
            if (string.IsNullOrWhiteSpace(resolvedRoot) || getDriveType(resolvedRoot) == DriveType.Network)
            {
                error = "A reparse point resolved to a mapped-network path.";
                return false;
            }

            canonicalPath = Normalize(resolvedPath);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = exception switch
            {
                UnauthorizedAccessException => "Path access was denied.",
                PathTooLongException => "Path is too long.",
                IOException => "Path is unavailable.",
                _ => "Path syntax is invalid.",
            };
            return false;
        }
    }

    public bool Equals(string left, string right) =>
        TryCanonicalize(left, out var canonicalLeft, out _) &&
        TryCanonicalize(right, out var canonicalRight, out _) &&
        string.Equals(canonicalLeft, canonicalRight, StringComparison.OrdinalIgnoreCase);

    public bool IsImmediateChildOf(string child, string parent)
    {
        if (!TryCanonicalize(child, out var canonicalChild, out _) ||
            !TryCanonicalize(parent, out var canonicalParent, out _))
        {
            return false;
        }

        var childParent = Path.GetDirectoryName(canonicalChild);
        return childParent is not null &&
            string.Equals(Normalize(childParent), canonicalParent, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsWithinRoot(string child, string parent)
    {
        if (!TryCanonicalize(child, out var canonicalChild, out _) ||
            !TryCanonicalize(parent, out var canonicalParent, out _))
        {
            return false;
        }

        if (string.Equals(canonicalChild, canonicalParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var root = Path.TrimEndingDirectorySeparator(canonicalParent) + Path.DirectorySeparatorChar;
        return canonicalChild.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public string GetIdentityKey(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var normalized = paths.Select(path =>
        {
            if (!TryCanonicalize(path, out var canonical, out var error))
            {
                throw new ArgumentException($"Cannot canonicalize a connection path: {error}", nameof(paths));
            }

            return canonical.ToUpperInvariant();
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", normalized)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string ResolveExistingLink(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var current = root;
        foreach (var segment in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var linkTarget = info.LinkTarget;
                if (string.IsNullOrWhiteSpace(linkTarget))
                {
                    continue;
                }

                var targetPath = Path.IsPathFullyQualified(linkTarget)
                    ? linkTarget
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, linkTarget));
                if (targetPath.StartsWith("\\\\", StringComparison.Ordinal) ||
                    targetPath.StartsWith("//", StringComparison.Ordinal) ||
                    targetPath.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                    targetPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
                {
                    throw new IOException("A reparse point targets an unsupported remote or device path.");
                }

                var targetRoot = Path.GetPathRoot(targetPath);
                if (string.IsNullOrWhiteSpace(targetRoot) || getDriveType(targetRoot) == DriveType.Network)
                {
                    throw new IOException("A reparse point targets an unsupported mapped-network path.");
                }

                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
        }
        return current;
    }

    private static bool ContainsTraversalSegment(string path) => path
        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => segment is "." or "..");

    private static string Normalize(string path)
    {
        var normalized = path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
        var root = Path.GetPathRoot(normalized);
        if (!string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.TrimEndingDirectorySeparator(normalized);
        }

        return normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}
