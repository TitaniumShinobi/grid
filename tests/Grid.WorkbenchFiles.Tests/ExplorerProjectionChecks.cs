// Framework-neutral checks; integrate into GRID's existing .NET test framework.
using System;
using System.Collections.Generic;
using System.Linq;
using Grid.Explorer;

namespace Grid.Explorer.Tests;

public static class ExplorerProjectionChecks
{
    public static void Run()
    {
        var files = new[] { "README.md", "src/App.cs", "src/View.cs", ".env.example", "assets/logo.png" };
        var root = ExplorerProjection.ChildrenOf(files);
        if (string.Join(",", root.Select(x => x.Name)) != "assets,src,.env.example,README.md")
            throw new Exception("Folder-first root projection failed");
        var state = new ExplorerState();
        if (!state.NeedsLoad("src") || !state.Toggle("src") || !state.IsExpanded("src"))
            throw new Exception("Lazy expansion failed");
        state.SetChildren("src", ExplorerProjection.ChildrenOf(files, "src"));
        var map = new Dictionary<string, IReadOnlyList<ExplorerEntry>> { ["src"] = state.LoadedChildren["src"] };
        if (ExplorerProjection.FilterLoaded(root, map, "App.cs").Single().RelativePath != "src")
            throw new Exception("Loaded-subtree filtering failed");
        state.ResetForDifferentRoot();
        if (!state.NeedsLoad("src") || state.IsExpanded("src"))
            throw new Exception("Root reset failed");
    }
}
