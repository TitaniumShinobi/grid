# Diagnostic process lifecycle

Process handling is shared infrastructure so adapters do not implement conflicting ownership rules.

Before launch, Grid verifies the executable identity, architecture, configured MO2 route, case-local plugin list, arguments, and absence of an already-running incompatible xEdit process. Arguments are passed structurally; no shell command is constructed.

Evidence records whether MO2 and SSEEdit appeared, observable PIDs and exit codes, timestamps, launch arguments, loader progress, status telemetry, and captured recent logs. Outcomes distinguish launch failure, early exit, module-loading exhaustion, collector compile/runtime failure, timeout, cancellation, and successful collection.

Grid does not terminate MO2 or SSEEdit automatically. Monitoring cancellation detaches observation; it is not process termination or rollback. A process may be closed only through a separately authorized ownership-aware operation with an explicit confirmation boundary.
