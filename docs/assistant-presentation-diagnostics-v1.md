# Assistant presentation diagnostics v1

Temporary diagnostic instrumentation only.

The Assistant shell toggle exposes UI Automation HelpText containing:
- IsExpanded
- IsFullScreen
- responsive presentation
- docked/drawer booleans
- docked column width
- drawer width
- docked visibility
- drawer visibility

The UI test failure also reports UIA offscreen/bounds for:
- docked Grid Assistant panel
- Grid Assistant drawer layer

This pass is intended to identify the exact divergence before another behavioral repair.
