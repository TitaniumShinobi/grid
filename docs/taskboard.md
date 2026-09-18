# GRID Activity / Taskboard contract

GRID records durable work independently of the UI surface that initiated it.

## Four-panel contract

The expanded Activity Taskboard has exactly four equal work phases:

1. **Queue** — proposed/accepted work that has not crossed the execution boundary. Card subtitle is the task description.
2. **In Progress** — active work. The card remains in this panel while internal workflow steps advance; its subtitle is the current step.
3. **Ready** — work that has reached a reviewable/result handoff. Card subtitle is a one-line summary.
4. **Ledger** — terminal work. Card subtitle is `Completed`, `Cancelled`, or `Failed`.

A task retains one identity while moving between phases. Internal steps do not create duplicate task cards.

Suggestions are candidate work and belong under Queue. They are not a fifth panel and do not authorize mutation.

## Activity shell

Collapsed Activity is a left-panel snapshot:

- a slim four-number scoreboard in Queue/In Progress/Ready/Ledger order;
- recent Ledger/activity history in the scrolling body;
- a fixed task composer at the bottom.

Expanding Activity reveals the four-panel Taskboard. Queue occupies the left-most lane and retains the scoreboard and composer. The Ledger presentation moves conceptually from the collapsed Activity body to the fourth lane; collapsing returns to the prior content surface. The Taskboard is a shell work surface, not a user-facing Case page.

## Work ownership

- Chat is a human interface to work.
- AUTO and deterministic GRID subsystems may also originate work.
- Taskboard is the durable work-state model.
- Internal cases remain execution/evidence/authorization identities.
- Ledger is chronological work history.
- Receipts are proof attached to completed/verified operations.
- Mediation/suggestions never silently mutate external state.

## Tabs

Tabs are independently closable content surfaces. Navigation provenance does not imply lifetime ownership: closing a workstation tab never cascades to a child resource tab that is already open. Activity expansion is a shell projection and does not require a persistent content tab.
