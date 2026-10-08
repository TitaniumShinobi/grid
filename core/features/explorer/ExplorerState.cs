// Portable state logic only. GRID owns persistence, loading, UI, and authority.
using System;
using System.Collections.Generic;

namespace Grid.Explorer;

public sealed class ExplorerState
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ExplorerEntry>> _loaded = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ExpandedPaths => _expanded;
    public IReadOnlyDictionary<string, IReadOnlyList<ExplorerEntry>> LoadedChildren => _loaded;

    public bool IsExpanded(string relativePath) => _expanded.Contains(relativePath);
    public bool NeedsLoad(string relativePath) => !_loaded.ContainsKey(relativePath);
    public void SetChildren(string relativePath, IReadOnlyList<ExplorerEntry> children) => _loaded[relativePath] = ExplorerProjection.Sort(children);
    public void Invalidate(string relativePath) => _loaded.Remove(relativePath);
    public bool Toggle(string relativePath)
    {
        if (_expanded.Remove(relativePath)) return false;
        _expanded.Add(relativePath);
        return true;
    }
    public void CollapseAll() => _expanded.Clear();
    public void ResetForDifferentRoot()
    {
        _expanded.Clear();
        _loaded.Clear();
    }
}
