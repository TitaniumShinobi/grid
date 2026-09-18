# GRID UI shell normalization contract

Stateful UI acceptance cases may not depend on shell state left behind by earlier tests.

Before Activity/Taskboard, panel-toggle, Home Assistant, and demo workspace acceptance, the harness establishes:

- Home route;
- Taskboard closed;
- maximized bottom panel restored;
- Assistant closed;
- bottom panel closed;
- left panel closed.

The helper acts only through the same public UI Automation controls available to a user. It does not reach into application internals or alter GRID runtime behavior.

Each test then opens only the surfaces it is responsible for verifying.
