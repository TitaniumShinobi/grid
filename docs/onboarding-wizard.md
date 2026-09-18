# GRID existing-MO2 onboarding wizard

The existing-MO2 connection flow is a four-step wizard. Exactly one step body is visible at a time.

1. Detect or browse.
2. Select one installation candidate.
3. Validate the exact backing setup.
4. Confirm the local GRID display name and connect read-only.

Presentation contract:

- The dialog fits inside the native ContentDialog width; navigation may never be clipped.
- Cancel stays bottom-left.
- Back appears beside Cancel from step 2 onward.
- Next stays bottom-right.
- Connect replaces Next on the final step.
- No dots, step wheel, carousel, or stacked future sections.
- Routine success is shown inline and compactly; the separate status bar is reserved for warnings/errors.
- Progress chrome exists only while a real operation is active.
- Back preserves discovery, selection, and validation state.
- Onboarding remains read-only and never launches or mutates MO2, profiles, mods, downloads, or game files.
