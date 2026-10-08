import { StartupObserver } from '../runtime/startup-observer.mjs';

// GRID owns stage meaning and timestamps. This adapter only shows the call shape.
export function createGridStartupObserver({ startup_id, receipt_directory }) {
  return new StartupObserver({ startup_id, product_id: 'grid', receipt_directory });
}

// Example:
// const stage = observer.begin('SHELL_INITIALIZATION', 'STARTUP_REQUIRED', true);
// await loadShell();
// stage.complete({ progress_units: { status: 'INDETERMINATE', completed: 0, total: null } });
// observer.finish({ status: 'PASS', evidence_refs: ['grid.ready'] }, { status: 'PASS' });
