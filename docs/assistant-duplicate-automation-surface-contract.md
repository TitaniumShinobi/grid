# Assistant duplicate automation surface acceptance

GRID has two AssistantPanel presentations:

- docked assistant;
- responsive drawer assistant.

Both intentionally expose the same stable automation names because they represent the same product surface. UI automation therefore must not use `FindFirst` to decide whether chat content is visible: the first matching element may be the hidden presentation.

Assistant intake readiness is proven when at least one visible, non-empty instance of both `Grid chat content` and `Chat history` exists.
