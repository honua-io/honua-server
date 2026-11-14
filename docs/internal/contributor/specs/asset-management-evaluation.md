# Asset management evaluation contract

This defines reproducible scenarios and evidence for the
[asset management core loop](asset-management-rc3.md). It is an evaluation
contract, not a report of successful runs. No scenario below has execution or
usability evidence attached here. Implementations must record their own results;
the existence of this document does not satisfy a release gate.

Use the architecture in [ADR-0084](../adr/0084-task-first-asset-management.md)
and the native client boundaries in
[ADR-0087](../adr/0087-native-client-protocols.md). Native clients consume
published SDK contracts over the approved native transports; browser clients
use the corresponding browser transport. Missing native task contracts block
that client scenario. Compatibility API tests do not establish native client
support. Protected task and attachment data must retain authorization through
offline packaging, download and reconciliation.

## Fixture identity and reset

Use a synthetic county containing signs, trees, trails, road segments and catch
basins, plus a custom polygon asset layer. Names, contacts, photos and report
text are synthetic. A source-derived public asset must retain its source layer
and stable source key separately from the fixture's stable asset identity;
an internal numeric feature ID alone is not a portable asset identity.

The samples repository owns reusable sample data and its provenance. Server
schema-coupled seeds belong under `tests/seed/`; tests own deterministic scene
fixtures. A recorded run pins both references and the server migration set.
Do not introduce a second independently maintained copy of the sample dataset.

Each fixture has a stable namespace, profile IDs, asset IDs, task UUIDs,
programme IDs and command IDs. Seed twice and compare logical row sets and
counts: the second seed must add no records or outbox intents. Cleanup removes
only records owned by that namespace, preserves unrelated tenant data and is
also safe to repeat. Historical evidence remains immutable during normal use;
cleanup is a test-environment reset, never an application delete operation.

Record a fixed query instant of `2026-01-15T12:00:00Z` for the dashboard fixture.
Run recurrence scenarios with an explicitly controlled clock, timezone, anchor
and calendar boundary; never derive expected cycles from the operator's wall
clock. Reported observations and server acceptance timestamps are distinct.

## Required fixture cases

| Fixture case | Required property | Primary assertion |
|---|---|---|
| Signs | Point assets with condition, published inspection form and prior inspections | Completion appends one observation and updates only eligible summary fields |
| Trees | Point assets with an older observation and a newer service record | Inspection and service watermarks advance independently |
| Trails | Line assets with a recurring inspection programme | Cycle uniqueness survives concurrent workers and retry |
| Road segments | Line assets with a stable external source identity | Installation preserves source attributes, geometry and SRID |
| Catch basins | Assets with a required photo field | Pending, foreign, expired and missing attachments cannot complete work |
| Custom areas | Polygon and multipart assets with customer column mappings | Preview validates mappings without rewriting customer columns |
| Assetless request | Explicit location and pinned service form | Completion records service and location provenance without inventing an asset |
| Moved asset | Existing task followed by an asset geometry change | Task location remains its snapshot until an authorized versioned relink |
| Condition boundary | Assets with each numeric condition 1–5 and a null condition | Approved labels map consistently; unknown remains distinct; poor-condition totals use the approved definition |
| Isolated service | A second service with distinct actors and intake | Queries, receipts, photos, exports and cache paths reveal no cross-service data |

The dashboard fixture contains the following eight tasks. All due and completion
times are UTC. Its arithmetic follows the core-loop specification; it does not
choose an overdue-cohort or poor-condition policy.

| Task | State | Due at | Completed at | Expected category |
|---|---|---|---|---|
| on-time | completed | Jan 14 12:00 | Jan 14 11:00 | On time |
| exact-boundary | completed | Jan 14 12:00 | Jan 14 12:00 | On time |
| late | completed | Jan 14 12:00 | Jan 14 12:01 | Late |
| undated | completed | None | Jan 14 11:00 | Undated, excluded from timeliness denominator |
| cancelled | cancelled | Jan 14 12:00 | None | Cancelled, excluded from timeliness denominator |
| incomplete | assigned | Jan 14 12:00 | None | Incomplete, excluded from timeliness denominator |
| future | assigned | Jan 16 12:00 | None | Incomplete, excluded from timeliness denominator |
| reassigned | in_progress | Jan 14 12:00 | None | Incomplete, with retained assignment history |

Expected completed-and-dated denominator: **3**. On time: **2**; late: **1**;
completed-undated: **1**. The displayed timeliness rate is derived from `2 / 3`,
with presentation rounding recorded separately. Compare these values with
direct authorized queries at the fixed instant. Do not label the incomplete
rows as a policy-defined “neglected” count without an approved definition.
Unknown asset condition remains null and must not appear as condition zero.

## Workflow and evidence matrix

Each row requires both user-visible evidence and assertions against canonical
server state. The acceptance IDs refer to the core-loop specification. A
recording alone cannot prove transactionality, isolation or idempotency.

| Workflow | Acceptance IDs | Required evidence |
|---|---|---|
| Lead installs starter profiles and maps a custom layer | A1 | Preview, rejected invalid mappings, repeated installation, unchanged customer columns and stable identities |
| Manager previews and approves recurring work | P1, P2, P3 | Preview-to-generated-cycle comparison, concurrent workers, crash/retry, disable/end/access-loss behavior and generation change |
| Crew opens Today, switches list/map and starts assigned work | T1, T4 | Authorized transitions and immutable scoped history; rejected transitions leave every record unchanged |
| Crew inspects, photographs and completes | A2, F1 | Pinned published form validation, ready attachment ownership, one inspection/service record, atomic task/history/receipt/outbox/summary writes |
| Crew completes three tasks offline and reconnects | O1 | Device/emulator queue survives process restart; start precedes completion; retry creates one accepted receipt per command |
| Dispatcher reassigns while a crew member is offline | O2 | Server assignment survives reconciliation; field payload remains recoverable; approved completion-authority policy is visible to the user |
| Crew creates a request from an asset while offline | O3 | Stable client UUID, persisted photo and payload, restart/retry, one accepted request, recoverability after access revocation |
| Concurrent users complete or retry the same task | T2 | One CAS winner, one conflict, replay of the winner returns its original receipt, changed-body reuse is rejected |
| Older observations arrive after newer work | A3 | History retains both; independent inspection/service watermarks prevent summary regression; equal-time conflict becomes a review candidate |
| Asset moves or a task is relinked | A4 | Geometry snapshot retention and authorized versioned relink provenance |
| Lead reviews performance and poor-condition work | D1 | Fixed-instant fixture arithmetic, direct-query comparison and explicit unknown/undated categories; approved metric definitions for remaining views |
| Resident reports a problem with a pin and photo | R1, R2 | Receipt, isolated private intake, validation/abuse limits, protected contacts and media, no public task-layer write permission |
| Clerk triages, links an asset and approves work | R3, F1 | Missing/stale version rejection, retry/changed-body behavior and exactly one linked task with the resolved pinned form |
| Lead receives overdue notifications | N1 | Deduplication and suppression after completion/cancellation through the existing notification pipeline |
| Desktop or compatibility client reads and edits | T3, X1 | Runtime/capability agreement, scoped query parity and rejection of protected domain-field edits |
| Assistant proposes a task mutation | X2 | Scoped proposal creates no mutation until human approval; stale approval is rejected |
| Operator upgrades and rolls back the server binary | M1 | N-1 reader accepts the expanded schema and new records; journals and evidence survive rollback |
| Staff and residents evaluate the three clickable flows | U1, E1 | Consented prototype sessions before UI implementation, repeated demo sessions and anonymized task success/time/errors |

Open311 interoperability is a separate compatibility surface over the same
intake and triage pipeline. Its scenarios must cover JSON/XML discovery and
service attributes, required/invalid fields, location/address input, media and
contact handling, request/token lookup and status/filter behavior. Assert that
the compatibility adapter preserves the same authorization, private-field
redaction, receipt and approval invariants as the native reporter flow. An
Open311 success cannot substitute for a native reporter or Collect run.

For the three clickable flows, recruit 3–5 consenting partner staff and obtain
representative resident feedback for reporter comprehension. Record keyboard and
screen-reader operation, WCAG 2.2 AA checks, slow-link behavior, sunlight
readability, glove-sized targets and one-handed use. Capture the design-system
version and both display modes. Repeat these checks against the integrated
demo; do not infer field usability from a desktop recording.

## Failure and concurrency evidence

For atomic completion, inject failure at each database write boundary and
compare the task, history, inspection/service record, summary revision, receipt,
attachment claims and outbox with their pre-command values. Staged object
storage bytes are outside the database transaction: prove interrupted uploads
remain recoverable or eligible for cleanup, and that cleanup never deletes
claimed evidence. Retrying an accepted completion must not claim more media or
emit another event.

Record the ordering barriers for concurrent tests, not timing guesses: hold a
database lock, establish the competing operation is blocked, release the lock
and assert the resulting state. Include assignment changes, cancellation,
configuration generation changes, competing completion and programme workers.
Run against PostgreSQL; in-memory stores cannot establish these invariants.

For intake, inspect anonymous and authenticated responses plus application,
edge and service-worker cache behavior. Use two unrelated anonymous reporters
and a separate staff service. Evidence must demonstrate private contact and
attachment isolation without placing real contact values in public artifacts.

## Run record and release evidence

A run record identifies the scenario and acceptance IDs, fixture namespace and
source refs, server commit/migrations, published client and SDK package versions,
device/browser versions, controlled clock/timezone, actor roles and configuration
generation. Record preconditions, commands, expected and actual outcomes,
canonical state assertions, artifact links and one result: pass, fail or blocked.
A blocked result names its missing dependency; an unexecuted scenario is not a
pass. Redact credentials, contacts and private media from shared evidence.

Native-client runs require their published packages and matching contracts.
Public demo smoke runs additionally require the approved demo environment and
versioned CI artifacts. Reuse the private demo repository's deployment runbooks;
this contract grants no deployment authorization and contains no live endpoints.
Do not modify sealed evaluation material to make a scenario pass.

The following decisions remain prerequisites for their affected scenarios:

- Recurrence catch-up/calendar rules and overdue cohort/cancellation semantics.
- Completion authority after reassignment, including offline conflict recovery.
- Condition labels and dashboard metric definitions, including neglected work.
- Public intake quotas, consent, retention, abuse handling and triage policy.
- Published neutral native contracts and SDK ownership for cross-client use.
- Consenting staff and resident participants and the approved research setting.

Until those prerequisites and the corresponding implementation/evidence exist,
the integrated demo and usability acceptance remain incomplete. Research records
must distinguish observed findings from hypotheses; neither synthetic fixtures
nor developer walkthroughs count as partner sessions.
