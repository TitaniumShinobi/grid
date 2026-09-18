# Assistant visibility state contract

Assistant presentation mode and Assistant visibility are separate facts.

`ResponsiveLayoutPolicy` decides how an expanded Assistant should be presented:
- Docked
- Drawer

`AssistantSessionState.IsExpanded` decides whether either presentation may be visible.

Runtime rules:
- closed => docked hidden, drawer hidden, docked width 0, splitter hidden;
- expanded + Docked => docked visible, drawer hidden;
- expanded + Drawer => docked hidden, drawer visible;
- expanded + FullScreen => existing dedicated full-screen branch owns the center/right workspace.

A preferred presentation mode must never make a closed Assistant visible.
