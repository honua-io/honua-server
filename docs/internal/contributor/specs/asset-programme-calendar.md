# Asset programme calendar decisions

This defines the calendar calculation slice of the
[asset management specification](asset-management-rc3.md), under server issue
#7301. The lane adopts the recommended calendar approach under the founder's
13 November decide-it-yourself ruling. It does not complete programme execution.

- Persist the original local anchor, positive interval, calendar unit and IANA
  timezone with the programme in PostgreSQL. Supported units are days, weeks,
  months and years. Cycle zero falls on the original anchor.
- Compute due-date cycle N from the original anchor plus N intervals. Clamp
  months and years to their last valid day, without changing the original anchor.
  A 31 January monthly anchor produces 28/29 February, then 31 March.
- Preserve the local clock time across timezone offset changes. A nonexistent
  local time moves forward to the first valid minute. An ambiguous local time
  uses its earlier UTC occurrence. Resolve each cycle independently; a DST
  adjustment does not alter the next cycle's anchor.
- Completion-based recurrence adds one interval to the accepted completion
  instant expressed in the programme timezone. Field observation time, upload
  time and worker wake-up time do not replace that accepted instant. The caller
  must have an accepted completion before it can request the next due date.
- Catch-up retains every missed cycle. A preview returns at most 100 cycles,
  an ordinal at which to resume and whether more cycles are already due.
  Its horizon and optional programme end time are inclusive scheduled due times.
  A future cycle does not advance the returned ordinal. End-of-calendar or
  programme-end exhaustion returns no next ordinal.

`ProgrammeCalendar` is a pure calculation over a schedule snapshot. A preview
does not reserve a cycle, advance persisted progress or create a task. The
PostgreSQL execution transaction must still fence the programme revision,
reserve each asset/cycle uniquely, write task/history/outbox records and advance
progress together. Programme edits cannot reset existing reservations. The
calendar's returned ordinal alone is not an authorization or idempotency key.

Runtime integration, cohort snapshots, cancellation and programme editing remain
in #7301. No additional scheduler, configuration cache or generation is introduced.
