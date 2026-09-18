# Closed-default shell UI acceptance

GRID starts on Home with optional shell surfaces closed.

The Activity/Taskboard acceptance test therefore must not require Chat or Console to be visible before Taskboard expansion.

When Taskboard is expanded, the test verifies:
- the Taskboard surface exists;
- the ordinary assistant surface is absent/offscreen;
- the ordinary bottom tool panel is absent/offscreen;
- the Taskboard occupies the shell workspace from the topbar boundary to the footer boundary.

Tests that need Explorer, Chat, Console, or another optional panel must explicitly open that surface before asserting its behavior.
