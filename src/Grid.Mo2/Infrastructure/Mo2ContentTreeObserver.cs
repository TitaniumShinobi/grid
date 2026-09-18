using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.Mo2.Infrastructure;

public sealed class Mo2ContentTreeObserver : IMo2ContentTreeObserver
{
    private readonly IMo2PathCanonicalizer paths;

    public Mo2ContentTreeObserver(IMo2PathCanonicalizer paths) =>
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public Task<Mo2ContentTreeObservation> ObserveAsync(
        Mo2ContentTreeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        return Task.Run(() => Observe(request, cancellationToken), cancellationToken);
    }

    private Mo2ContentTreeObservation Observe(
        Mo2ContentTreeRequest request,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (!paths.TryCanonicalize(request.RootPath, out var root, out _))
        {
            issues.Add(new(
                "mo2.content.root_invalid",
                Mo2IssueSeverity.Error,
                "The content root is invalid or inaccessible."));
            return Empty(request.RootKind, root, Mo2PathState.Invalid, issues);
        }

        DateTime rootWriteBefore;
        try
        {
            if (!Directory.Exists(root))
            {
                issues.Add(new(
                    "mo2.content.root_missing",
                    Mo2IssueSeverity.Error,
                    "The content root is missing."));
                return Empty(request.RootKind, root, Mo2PathState.Missing, issues);
            }

            rootWriteBefore = Directory.GetLastWriteTimeUtc(root);
        }
        catch (UnauthorizedAccessException)
        {
            return Inaccessible(request.RootKind, root, issues);
        }
        catch (IOException)
        {
            return Inaccessible(request.RootKind, root, issues);
        }

        var entries = ImmutableArray.CreateBuilder<Mo2ContentTreeEntry>();
        var pending = new Stack<(string LexicalPath, int Depth)>();
        pending.Push((request.RootPath, 0));
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var partial = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(
                    current.LexicalPath,
                    "*",
                    SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                partial = true;
                issues.Add(new(
                    "mo2.content.directory_inaccessible",
                    Mo2IssueSeverity.Warning,
                    "A directory could not be enumerated; the content observation is partial."));
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= request.Limits.MaximumEntries)
                {
                    partial = true;
                    issues.Add(new(
                        "mo2.content.entry_limit",
                        Mo2IssueSeverity.Warning,
                        "The content entry safety limit was reached; the observation is partial."));
                    pending.Clear();
                    break;
                }

                if (!paths.TryCanonicalize(child, out var canonical, out _) ||
                    !paths.IsWithinRoot(canonical, root))
                {
                    partial = true;
                    issues.Add(new(
                        "mo2.content.path_escape",
                        Mo2IssueSeverity.Warning,
                        "An entry resolved outside the authorized root and was ignored."));
                    continue;
                }

                var relative = Path.GetRelativePath(request.RootPath, child);
                if (!TryNormalizeVirtualPath(relative, request.Limits, out var virtualPath))
                {
                    partial = true;
                    issues.Add(new(
                        "mo2.content.virtual_path_invalid",
                        Mo2IssueSeverity.Warning,
                        "An entry has an unsupported virtual path and was ignored."));
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(child);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var depth = current.Depth + 1;
                    long? length = null;
                    long lastWrite;
                    if (isDirectory)
                    {
                        lastWrite = Directory.GetLastWriteTimeUtc(child).Ticks;
                    }
                    else
                    {
                        var info = new FileInfo(child);
                        length = info.Length;
                        lastWrite = info.LastWriteTimeUtc.Ticks;
                    }

                    entries.Add(new(
                        isDirectory ? Mo2ContentEntryKind.Directory : Mo2ContentEntryKind.File,
                        virtualPath,
                        canonical,
                        paths.GetIdentityKey(canonical),
                        depth,
                        length,
                        lastWrite,
                        attributes));

                    if (!isDirectory)
                    {
                        continue;
                    }

                    if (depth >= request.Limits.MaximumDepth)
                    {
                        partial = true;
                        issues.Add(new(
                            "mo2.content.depth_limit",
                            Mo2IssueSeverity.Warning,
                            "A directory exceeded the traversal depth limit; its descendants were not observed."));
                    }
                    else if (!visitedDirectories.Add(canonical))
                    {
                        partial = true;
                        issues.Add(new(
                            "mo2.content.directory_cycle",
                            Mo2IssueSeverity.Warning,
                            "A directory resolves to an already visited identity and was not traversed again."));
                    }
                    else
                    {
                        pending.Push((child, depth));
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    partial = true;
                    issues.Add(new(
                        "mo2.content.entry_inaccessible",
                        Mo2IssueSeverity.Warning,
                        "An entry could not be observed; the content observation is partial."));
                }
            }
        }

        try
        {
            if (Directory.GetLastWriteTimeUtc(root) != rootWriteBefore)
            {
                partial = true;
                issues.Add(new(
                    "mo2.content.root_changed",
                    Mo2IssueSeverity.Warning,
                    "The content root changed during observation; the result is inconsistent."));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            partial = true;
            issues.Add(new(
                "mo2.content.root_recheck_failed",
                Mo2IssueSeverity.Warning,
                "The content root could not be rechecked after observation."));
        }

        var ordered = entries
            .OrderBy(entry => entry.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.VirtualPath, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            request.RootKind,
            root,
            Mo2PathState.Present,
            ordered,
            Fingerprint(ordered),
            partial,
            issues.ToImmutable());
    }

    private static bool TryNormalizeVirtualPath(
        string value,
        Mo2ContentObservationLimits limits,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > limits.MaximumVirtualPathLength ||
            Path.IsPathFullyQualified(value) ||
            value.StartsWith('\\') ||
            value.StartsWith('/') ||
            value.Any(char.IsControl))
        {
            return false;
        }

        var segments = value.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
            segment is "." or ".." || segment.Length > limits.MaximumSegmentLength))
        {
            return false;
        }

        normalized = string.Join('\\', segments);
        return normalized.Length <= limits.MaximumVirtualPathLength;
    }

    private static string Fingerprint(ImmutableArray<Mo2ContentTreeEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(
                $"{entry.Kind}:{entry.VirtualPath}:{entry.IdentityKey}:{entry.Length}:{entry.LastWriteTimeUtcTicks}\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Mo2ContentTreeObservation Empty(
        Mo2ContentRootKind kind,
        string root,
        Mo2PathState state,
        ImmutableArray<Mo2ValidationIssue>.Builder issues) =>
        new(kind, root, state, [], string.Empty, true, issues.ToImmutable());

    private static Mo2ContentTreeObservation Inaccessible(
        Mo2ContentRootKind kind,
        string root,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        issues.Add(new(
            "mo2.content.root_inaccessible",
            Mo2IssueSeverity.Error,
            "The content root is inaccessible."));
        return Empty(kind, root, Mo2PathState.Inaccessible, issues);
    }
}
