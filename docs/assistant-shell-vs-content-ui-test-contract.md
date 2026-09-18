# Assistant shell vs content UI acceptance

`EnsureAssistantOpen()` verifies only shell expansion:

- `Show Grid Assistant` is available while closed.
- activating it transitions the shell to `Hide Grid Assistant`.

It does not require `Chat history` or other intake controls to be visible, because the helper is also used while specialized center-workspace surfaces such as full Console are active.

Tests that specifically require the chat intake call `WaitForAssistantIntake()` after opening the assistant. This keeps shell-state verification separate from content-readiness verification.
