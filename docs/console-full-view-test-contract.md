# Console full-view UI acceptance

The Console full-view runtime surface has distinct automation identities from the docked bottom panel.

The acceptance test verifies:

- `Maximized Grid terminal command` is visible after maximizing.
- `Maximized bottom tool panel` spans from the topbar boundary to the footer boundary.
- The left content sidebar remains visible and can be toggled off and back on while Console is full.
- The assistant panel remains visible and can be toggled off and back on while Console is full.
- Restore uses the dedicated `Restore bottom panel` control.

This replaces the obsolete test assumption that Console full view hides both side panels.
