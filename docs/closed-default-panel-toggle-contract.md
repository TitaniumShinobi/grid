# Closed-default panel toggle acceptance

GRID's clean/default shell starts with the bottom panel closed.

The UI test therefore verifies this order:

1. terminal is absent/offscreen at default;
2. Toggle bottom panel opens the terminal;
3. Toggle bottom panel closes the terminal;
4. Toggle bottom panel reopens it;
5. maximize verification then runs against the visible bottom panel.

This preserves the product rule instead of reopening Console by default to satisfy an older test assumption.
