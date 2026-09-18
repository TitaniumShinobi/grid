# Assistant-open UI test contract

GRID starts with Chat closed by default.

`EnsureAssistantOpen()` must therefore prove the transition rather than merely click a shell control:

1. If `Grid chat content` and `Chat history` are already visible, return.
2. Otherwise require an enabled `Show Grid Assistant` control.
3. Activate it.
4. Wait for WinUI dispatcher/render passes until both the authoritative chat surface and Chat history are visible.
5. Fail with a setup-specific error if the panel never becomes ready.

This keeps runtime behavior unchanged and makes UI acceptance resilient to the intentional closed-by-default shell.
