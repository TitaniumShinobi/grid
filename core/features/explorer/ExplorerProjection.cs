// Dependency-free C# adaptation of CODE's local tree projection, folder-first
// sorting, and loaded-subtree filtering. This is NOT a filesystem authority.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grid.Explorer;

public sealed record ExplorerEntry(string Name, string RelativePath, bool IsDirectory);

public static class ExplorerProjection
{
    // Input paths are already authorized, root-relative, slash-delimited paths.
    // A native file service must validate them before calling this pure function.
    public static IReadOnlyList<ExplorerEntry> ChildrenOf(IEnumerable<string> filePaths, string directory = "")
    {
        var prefix = directory.Length == 0 ? "" : directory.TrimEnd('/') + "/";
        var children = new Dictionary<string, ExplorerEntry>(StringComparer.Ordinal);
        foreach (var path in filePaths)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = path[prefix.Length..];
            if (remainder.Length == 0) continue;
            var slash = remainder.IndexOf('/');
            var name = slash < 0 ? remainder : remainder[..slash];
            if (name.Length == 0) continue;
            var childPath = prefix + name;
            var isDirectory = slash >= 0;
            if (!children.TryGetValue(childPath, out var existing))
                children[childPath] = new ExplorerEntry(name, childPath, isDirectory);
            else if (isDirectory && !existing.IsDirectory)
                children[childPath] = existing with { IsDirectory = true };
        }
        return Sort(children.Values);
    }

    public static IReadOnlyList<ExplorerEntry> Sort(IEnumerable<ExplorerEntry> entries) => entries
        .OrderByDescending(entry => entry.IsDirectory)
        .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
        .ToArray();

    // CODE filters only entries currently present in the lazy-loaded tree map.
    // Unloaded children cannot produce a descendant match.
    public static IReadOnlyList<ExplorerEntry> FilterLoaded(
        IEnumerable<ExplorerEntry> entries,
        IReadOnlyDictionary<string, IReadOnlyList<ExplorerEntry>> loadedChildren,
        string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return entries.ToArray();
        bool Matches(ExplorerEntry entry, HashSet<string> visited)
        {
            if (entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (!entry.IsDirectory || !visited.Add(entry.RelativePath)) return false;
            return loadedChildren.TryGetValue(entry.RelativePath, out var children)
                && children.Any(child => Matches(child, visited));
        }
        return entries.Where(entry => Matches(entry, new HashSet<string>(StringComparer.Ordinal))).ToArray();
    }
}
