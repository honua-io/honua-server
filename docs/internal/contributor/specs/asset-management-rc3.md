# Asset management core loop — rc.3 specification

Status: proposed contract, 2026-11-10. Scope: approved rc.3 core loop; implementation
starts after rc.2 ships. [ADR-0084](../adr/0084-task-first-asset-management.md)
records the architecture. Unsettled policies below require a main ruling before
contract freeze. Names and paths here describe proposed APIs, not shipped ones.

## People, jobs and scope

| Person | Job | Success |
|---|---|---|
| Crew member | Work today's assigned tasks despite poor signal | Start, inspect, photograph and complete; understand sync and conflicts |
| Public works lead | Keep inspections and repairs on schedule | Set up assets, plan recurring work, assign and see exceptions |
| Parks manager | Plan seasonal work across different asset types | Reuse own layers and review poor-condition assets |
| Resident-facing clerk | Turn a report into actionable work | Triage safely, link an asset, avoid duplicates |
| Resident | Explain a problem and its location | Submit a photo and pin without an account |

Three validation flows: crew opens Today → list/map → start → record → complete
→ sync; manager chooses asset layer → maps columns → previews programme → approves
→ reviews work board; resident answers “What's wrong, and where?” → photo/pin →
submit → receipt → clerk triage. These are hypotheses to test with 3–5 staff,
not findings from interviews already conducted. Obtain consent, record task
success/time/errors, and test clickable prototypes before Console/Collect UI
implementation; repeat on the demo stack before release. Resident comprehension
requires representative resident testing as well as clerk feedback.

Console is task-first with map as a view, guided asset setup and a programme
editor. Collect shows today, due work and route order without requiring route
optimisation. Use Honua design-system tokens, two modes, Geist typography, Honua icons,
condition labels and palette. WCAG 2.2 AA, keyboard/screen-reader operation,
glove-sized targets, one-handed use, sunlight readability and slow links are
acceptance requirements. Do not derive layout from reference screens.

Included: starter signs, trees, trails, road segments and catch basins; custom
asset layers; condition, inspections, maintenance/repair/request tasks, recurring
programmes, anonymous web reporter, performance dashboard, mobile primitives
and Collect task UX. Excluded: lidar extraction, 3D asset viewer, benefit
calculations, commercial work-order integrations, a second staff app.

## Existing components and intended reuse

| Component/source | Reuse and boundary |
|---|---|
| `src/Honua.Server/Features/Forms/FormPackageEndpoints.cs`, `FormSubmissionService.cs`; migration `035_CreateFormPackages.sql` | Published form versions, validation, attachment handling and submission receipts; do not simply grant anonymous target-layer writes |
| `src/Honua.Server/Features/FieldWorkflows/Review/FieldReviewService.cs`; migration 052 | Assignment/review pattern and audit; task-specific CAS and completion remain a canonical service |
| `src/Honua.Server/Features/Mobile/FieldCollection`; migrations 024/057 | Existing sync queue, receipt and automation infrastructure; task commands add domain rules |
| Related tables/attachments, migrations 002/003; domains/visibility 026/028 | Related inspection/service records, protected attachments and mapped fields |
| Feature-change outbox, migrations 024/121/124 | Transactional notifications, condition-change triggers, downstream invalidation; consumer event IDs deduplicate retries |
| PostgreSQL jobs, #7245 | Scheduled generation, leases/fencing/retries; foundation must merge before implementation |
| `src/Honua.Server/Features/Alerts` | Overdue notifications through existing dispatch, not a second notification system |
| Studio publishing; mobile GeoPackage/sync; Collect forms | Templates and field runtime, with neutral contracts consumed from published SDK packages |

Sources above were checked for component presence; integration and proposed
extensions require implementation tests. Add vertical slices `Features/Assets`
and `Features/Tasks` with core abstractions/provider implementations in their
existing architectural projects. Keep protocol adapters thin.

## Data model and invariants

All records are scoped to the existing authorization/service boundary; every
reference is validated in that boundary. IDs survive layer renames and offline
round trips. Ordinary spatial/table queries use the same access policy as REST.
Do not expose private resident contact fields through task queries or exports.

| Object | Required shape and constraints |
|---|---|
| Asset profile | Profile ID, service/layer identity, stable unique asset-ID field, mapped condition/status/last-inspected/last-serviced fields, condition label domain 1–5, profile version, default inspection/service form package references and related-table identities. Point/line/polygon including their multipart forms. Validate types, uniqueness, null handling and edit capability before activation; never silently rewrite customer columns |
| Asset | Existing feature plus profile mapping. Stable identity differs from an internal numeric row ID. Preserve geometry/SRID and original customer attributes. Unknown condition is null, never zero |
| Inspection | ID, asset/profile reference, task ID, pinned form version, observed-at and server-recorded-at, inspector, condition, answers, notes and attachment references. A task completion creates at most one canonical inspection |
| Service record | ID, asset/profile reference, task ID, service type/time, actor, notes and attachment references. Append corrections with provenance instead of erasing history |
| Task | UUID, query-layer feature ID, type `inspection/maintenance/repair/request`, optional asset reference and programme/cycle key, geometry snapshot/SRID, title, assignee, priority (enum pending policy approval), due-at, status, monotonically increasing version, created/started/completed/cancelled timestamps, completion data, snapshotted form package ID/version, server receipt and provenance |
| Task history | Immutable event ID, task ID, old/new version and state, actor, server time, device observed time, command ID, changed fields, reason and approved proposal reference where applicable. One event per accepted mutation |
| Programme | UUID/version, name, target profile/layer plus validated filter, task type, positive interval and unit `days/weeks/months`, recurrence mode `due_date/completion`, anchor, timezone, default assignee, optional form binding override, optional end date and enabled state |
| Programme asset cycle | Programme/asset stable ID, cycle key, scheduled due-at, programme revision, task ID and generation receipt. Unique `(programme_id, asset_id, cycle_key)` enforced by PostgreSQL, not a pre-insert lookup |
| Reporter triage | Submission/form version, intake category/location/details/photos, optional private contact, received time, review status and linked task. Separate restricted intake; triage status is not a sixth task state |

Resolve and pin a published form package ID/version at task creation. Inspection
tasks use the inspection binding; maintenance, repair and request tasks use the
service binding. An authorized explicit create/triage form override takes
precedence over the programme override, then the matching profile default.
Validate form compatibility with task type and mapped fields. Assetless tasks
require an explicit service form binding. Missing bindings reject server creation
with an actionable field error. Offline-created requests remain local pending
reconciliation until a permitted binding resolves; no inspection work may start
before acceptance. Later publication never changes a pinned task. Completion
creates the corresponding inspection or service record; assetless service records
retain task/location provenance without inventing an asset reference.

Tasks with no asset (for example a new resident request) carry explicit location.
Copy geometry when creating an asset-linked task; later asset movement does not
silently move historical work. Re-link/move is an authorized versioned mutation.
Hard deletion of referenced assets/profile fields is rejected or requires an
explicit archival policy; preserve inspection and task provenance.

Completion uses one database transaction for task CAS, history, inspection or
service record, mapped condition/dates, command receipt and outbox intent.
Attachment bytes are staged through existing storage; only validated, owned,
ready references enter the transaction. Interrupted uploads are recoverable and
cleaned by existing retention mechanisms; never claim object storage itself is
part of a database transaction. A failure at any database write rolls back all
of these records. Dates derive from validated observation timestamps, with
server timestamps retained; older inspections must not regress the latest
asset summary. Maintain a domain-owned summary revision in a sidecar keyed by
stable profile/asset identity; customer layers need no native feature version.
Store independent observed-at watermarks for the inspection field group
(condition/last-inspected) and service field group (last-serviced). Serialize
updates on the sidecar key within the transaction; each group advances only for
a newer observation in that group. Stale downloaded summary revisions do not
reject authorized completion; retain the observation even if the summary stays
unchanged. Equal-time conflicting values become correction/review candidates.

Migrations choose the next number at implementation time, after checking open
PRs. Add tables, nullable columns and safe indexes only; no drops, renames,
narrowing, destructive backfills or edits to journaled migrations. New code
handles absent profile data, old readers keep working, and new task data
survives a binary rollback. Prove N-1 reader compatibility under #7276 before
landing schema work. Starter schema installation is retry-safe and does not
modify a customer layer without a preview and approval.

## State machine and concurrency

| From | Command | To | Preconditions |
|---|---|---|---|
| unassigned | assign | assigned | Valid authorized assignee |
| assigned | unassign | unassigned | Planner permission; no completed work |
| assigned | start | in_progress | Current assignee or authorized supervisor |
| assigned / in_progress | reassign | same state | Planner permission; assignment version advances |
| in_progress | complete | completed | Required pinned form fields/attachments valid |
| unassigned / assigned / in_progress | cancel | cancelled | Supervisor permission and reason |

Create with no assignee yields unassigned; valid default assignee yields assigned.
Terminal tasks cannot reopen in rc.3; follow-up work is a new linked task.
Completion directly from assigned is not part of the proposed contract; an offline
start and completion replay in order. Editing a terminal record requires an
explicit correction operation with history rather than status reversal.

Every mutation carries a stable `commandId`; updates/transitions also carry
`expectedVersion` (creation has no prior version). Compare-and-set
updates the task only where the version equals the expectation. One concurrent
command wins, the other returns a conflict with current authorized projection.
Check authorization before disclosing that projection. History/version/side
effects are committed together. Retrying an identical command returns its stored
receipt; reuse of the ID with a different body is rejected. No last-write-wins
whole-record update. Task REST deliberately uses a body version so offline
commands retain one transport-neutral replay envelope; it does not change the
existing field-review surface without an explicit extension. Add an expand-only
task command receipt table keyed by (service scope, commandId), storing actor,
operation, body hash and outcome. Field sync forwards change_id = commandId into
that same task receipt namespace; adapters never execute a task command twice.
The existing generic feature-sync receipt table remains unchanged (global
change_id primary key, insert/update/delete operations). Actor, operation or body
reuse inconsistent with an existing task receipt returns 409. Assignee, status,
cycle keys and completion fields cannot
be edited around the service through generic layer edits. Protocol edit adapters
either invoke the same command semantics with concurrency tokens or reject the
protected field mutation with an actionable domain error. Read-only capability
metadata must reflect any unsupported write shape.

## Programme generation

Use the PostgreSQL execution job substrate and existing scheduling facilities.
Jobs may be delivered more than once or claimed on multiple nodes. A short
transaction fences the worker, reserves the unique asset/cycle key, creates the
task/history/outbox intent and advances cycle progress. Expired worker attempts
cannot advance the cursor. A crash before commit creates nothing; a crash after
commit returns the existing task on retry. Never advance the cursor without
the corresponding durable reservation. Page large cohorts with bounded claims;
retries resume without skipping assets.

Due-date mode derives subsequent due dates from the scheduled anchor, not job
execution time; delayed workers must not cause drift. Completion mode schedules
the next cycle from accepted completion time, not upload or dispatch time.
It does not generate another active cycle while the current task is unfinished.
Cancel, missed cycles, programme edits, filter changes, month-end and timezone
semantics need the rulings below. Do not silently encode them in a worker.
Cycle identity is deterministic from persisted schedule state and is independent
of execution attempt, task UUID and programme display name. A programme edit
cannot bypass the uniqueness key. End dates bound scheduled due dates, not worker
wake-up time. Disabled programmes generate no new tasks; existing tasks remain.
Filters run through validated layer queries and the programme owner's effective
access; losing access pauses generation and surfaces an actionable failure.

Condition-change automations consume the existing outbox once per event/action
key and call the same task service. Completion-generated feature events must
not recursively create duplicate tasks. Overdue means nonterminal and due-at
before the query instant; dispatch deduplicates by task/version/alert policy.

## Offline sync contract

Reuse the existing GeoPackage store, durable command queue, field sync and
conflict review. Map-area download includes the user's assigned tasks, asset
profiles/features, pinned forms and required related records. Access changes
are enforced at reconnection, and protected local data follows existing device
retention rules. Keep command ordering per task, persisted across app restarts.

| Conflict | Required behavior |
|---|---|
| Offline work request created from an asset | Authorized crew persists client-generated task UUID, commandId, asset reference/location, notes and photos; create reconciles before dependent commands; retries return the same task and receipt, never duplicate work |
| Server reassigned while device offline | Server assignee/assignment metadata wins; never restore the downloaded assignee |
| Local completion fields vs older server completion fields | Preserve field notes, answers, observations and photos with provenance; never replace with a stale empty server projection |
| Reassignment plus local completion | Retain completion payload and server assignment separately. Terminal state authorization is an unresolved ruling; until resolved expose pending review, never claim server completion |
| Cancelled task or revoked write access | Reject unauthorized transition, retain recoverable local payload and visible conflict; do not override cancellation or leak newly inaccessible server data |
| Two field completions | One canonical completion record; the second payload is retained as a conflict/correction candidate, never silently erased |
| Duplicate command/reconnect retry | Same receipt, no duplicate inspection/history/attachment association |
| Asset updated since download | Retain observation; serialize conditional summary update by stable asset identity as above; a stale summary revision alone never rejects completion or changes unrelated asset fields |

Field wins applies to completion data, not arbitrary task ownership or security.
For authorized merges, fetch current version, split authoritative assignment from
completion payload, and resubmit a versioned domain command; the server still
checks all preconditions. Responses distinguish `accepted`, `duplicate`,
`conflict` and `rejected`, include command ID and safe current version plus
field-level conflict reasons. UI distinguishes local saved, queued, uploading,
server accepted and needs review. Unsent photos are never labeled uploaded.

## Public reporter intake

A Studio-published web form uses a pinned published package and a dedicated
anonymous intake policy. Category, location and details are required; photo is
optional with bounded count/size/type using existing upload security. Permission
is scoped to creating a submission for that published reporter; no arbitrary
layer, assignee, programme, task state or target overrides. Do not enable
anonymous editing on the task or asset layers. Validate area and geometry and
show useful location errors without requiring precise device GPS permission.

Apply rate limits at the edge under ADR-0004; enforce service-side body/upload
quotas and idempotency without reintroducing general server rate limiting. Use bot/spam
verification with an accessible alternative, and bounded request time. Persist
idempotency receipts to handle slow-phone retries. Treat challenge failures and
malicious content as intake errors, never as executable markup. Keep contact
and spam signals out of public layers and logs; redact metadata in public photos.
Staff-only triage can reject/mark duplicate, request follow-up where contact is
provided, or approve once into a `request` task and optionally link an asset.
Approval/task creation is idempotent and audited; approval never publishes
contact data. Raw reports do not directly dispatch work.

Reporter form, configuration, submission, upload and receipt responses, including
errors, carry `Cache-Control: private, no-store`; edge behavior has minimum TTL
zero, no cached errors and no caching override. Service workers exclude all
reporter routes and bodies. Verify through the deployed CDN with two anonymous
clients, not just origin unit tests. No public listing or receipt lookup that
lets a caller enumerate reports; receipts are opaque and reveal no staff data.

## Proposed REST and OpenAPI shapes

Operation inventory, not raw transport examples. Final routes follow existing
API registration conventions at implementation. JSON uses camelCase; task
states use the exact values above. UUIDs are strings, timestamps RFC3339 UTC,
calendar recurrence retains an IANA timezone. List responses contain `items`,
`nextCursor`; bounded limits and deterministic ordering. OpenAPI declares request,
response and error schemas, security requirements, operation IDs, enum values,
nullability, concurrency and idempotency behavior, including anonymous intake.

| Resource/operation | Input | Output |
|---|---|---|
| `/api/v1/assets/profiles` list/create; `/{id}` get/update | Layer identity, mapped fields, labels; expectedVersion/commandId on update | Profile and validation diagnostics; schema setup preview |
| `/api/v1/tasks` list/create; `/{id}` get | Filters status/assignee/programme/type/due/bbox, cursor; create client task UUID, commandId, assetRef or location, type, dueAt, priority, optional form override | Task projection with version, permitted actions and provenance |
| `/api/v1/tasks/{id}/transitions` create | commandId, expectedVersion, action, assignee/reason as applicable | Task and durable command receipt |
| `/api/v1/tasks/{id}/completion` create | commandId, expectedVersion, optional downloaded summary revision (informational), observedAt, pinned form reference, condition/answers/notes, ready attachment references | Task, inspection/service IDs, versions and receipt |
| `/api/v1/tasks/{id}/history` list | Authorized cursor/limit | Immutable history projection |
| `/api/v1/programmes` list/create; `/{id}` get/update | Target/filter, interval/unit/mode/anchor/timezone/end/default assignee, expectedVersion/commandId | Versioned programme and validation diagnostics |
| `/api/v1/programmes/{id}/preview` | Expected programme version and bounded sample size | Cohort counts, sample scheduled dates, warnings; no tasks created |
| `/api/v1/reporters/{id}/submissions` create | Published form version, commandId, category/location/details/photos and bot proof | Opaque submission receipt and triage acknowledgement |
| Reporter triage via existing field-review surface | Authorized decision, required If-Match/ETag and commandId (proposed extensions), optional asset link/form override | Review receipt and request task reference on approval |

Proposed error contract: invalid body/domain is 400 with field errors, missing
identity 401, forbidden 403, invisible/missing resource 404, stale version or
conflicting command ID 409 with safe conflict details, oversized intake 413,
unavailable dependency 503. Edge-owned rate limiting emits 429 and its headers
per ADR-0004; these are outside the server response contract. Create returns 201,
query/update 200, asynchronous intake 202; duplicate commands return original
operation outcome with duplicate indication. Missing required expectedVersion
is 400 for the proposed task body contract. Existing field review accepts an
optional If-Match and otherwise applies an unconditional decision; its current
request has status/note, without commandId. For reporter triage, propose mandatory
If-Match (428 when absent; 409 when stale) and commandId replay protection as
explicit extensions. Do not expose stack traces, queries or storage details.

FeatureServer and OGC API Features expose asset, task, inspection and service
collections plus programme/history tables through existing query pipelines.
Publish field schemas/domains, stable IDs, relationships, geometry/SRID,
permissions, pagination and temporal/spatial filters. Table-only resources
must not invent geometry. Both adapters return the same authoritative state;
protected writes route to the domain service or explicitly reject as above.
Desktop and OGC client compatibility must be demonstrated, not assumed from
REST tests. No adapter bypasses per-layer/row/field authorization.

MCP inventory: `honua_list_asset_profiles`, `honua_get_asset_profile`,
`honua_propose_asset_profile`, `honua_list_tasks`, `honua_get_task`,
`honua_propose_task_create`, `honua_propose_task_transition`,
`honua_list_programmes`, `honua_get_programme`, `honua_propose_programme`,
`honua_preview_programme`. Read tools return
only authorized projections. Proposed mutations/bulk changes use the existing
operation-proposal path with human approval in Console (MRTR in-client
approval when it ships), carrying expected versions
and an idempotency key. Approval of stale input fails safely and requires a new
proposal. No agent tool silently applies a programme or bulk reassignment.
OpenAPI/tool inventories must match registered capabilities and permissions.

## Dashboard definitions

Use ordinary authorized task/asset queries, with a fixed query instant and UTC
reporting window in fixtures. Completed on time: `completedAt <= dueAt`; late:
`completedAt > dueAt`. Exclude cancelled, incomplete and undated work from the
on-time denominator; display counts for excluded undated work. Break down by
type, assignee and programme, with a completion timeline. Poor condition and
not serviced in 12 months use the agreed condition direction and calendar
cutoff; show unknown dates/condition separately. A current assignee is not
necessarily the completing actor: retain both and label the chosen grouping.
Metric policy needs approval below before freezing fixtures.

## Acceptance tests and exit evidence

| ID | Given / action / expected result |
|---|---|
| A1 | Each starter and an existing custom point/line/polygon layer: preview/mapping validates identity/types; repeat install creates no duplicate tables or profiles |
| A2 | Inspection completion: inject failure at each database write; task/history/record/condition/dates/outbox all commit or none do; attachment retries remain recoverable |
| A3 | Stale downloaded summary revision still permits completion and retains observation; concurrent tasks serialize summaries without regression; inspection/service arrive out of order and advance independent watermarks; equal-time conflicting values create review candidates |
| T1 | Exercise every allowed transition and every disallowed edge above; actor/permission/version violations leave all state unchanged |
| T2 | Two concurrent transitions at one version: exactly one accepted, one conflict; replay winner creates no second history; changed-body ID reuse rejected |
| T3 | Generic protocol edit attempts protected assignment/status/completion: same domain validation or explicit rejection, no bypass |
| P1 | Two running server instances on the deployed demo stack generate the same cohort/cycle; crash before/after commit and expire leases: exactly one task per asset/cycle and no missed cursor entries |
| P2 | Delayed due-date job does not drift; completion-mode waits then anchors to accepted completion; calendar/edit/cancel cases use approved decision fixtures |
| P3 | Disable/end/access loss stops new generation; existing tasks survive; condition-change outbox retries do not duplicate tasks or recurse |
| O1 | Device/emulator downloads area, restarts, completes three tasks with photos offline, reconnects: ordered commands, one inspection each, accurate sync status |
| O2 | Reassign offline task on server; reconnect: server assignee retained, field payload retained, terminal result follows approved ruling; cancellation/revocation and dual completion produce explicit recoverable conflicts |
| O3 | Offline crew creates a work request from an asset with photos, restarts, retries and syncs: exactly one authorized task with stable client UUID and intact evidence; revoked permission yields recoverable conflict |
| T4 | History cannot be edited/deleted through any adapter; history listing enforces task/row/field scopes and never leaks another service’s data |
| A4 | Move source asset after task creation: task geometry stays fixed; authorized versioned re-link/move records provenance; stale/unauthorized mutation leaves geometry and history unchanged |
| N1 | Retry overdue dispatch: one notification per task/version/policy; completion/cancellation before dispatch suppresses the pending alert |
| R1 | Anonymous report passes through deployed CDN, form validation and abuse controls into restricted triage; one approval produces one request task; staff can link asset |
| F1 | Programme form override beats profile default; authorized explicit override wins; publication does not alter pinned tasks; missing/incorrect binding rejects create; assetless triage requires service binding; offline request resolves binding on reconciliation before start |
| R3 | Triage approval without If-Match returns 428; stale ETag returns 409 with no task; retry identical command creates one task; changed-body replay is rejected |
| R2 | Rate and upload limits, spam rejection and accessible alternative work; two anonymous clients never receive another receipt/contact/photo; every reporter response/error is no-store and edge/service-worker caches stay empty |
| X1 | REST/OpenAPI snapshots match runtime; FeatureServer desktop client and OGC API Features read same IDs/state/relationships; cross-scope queries and edits denied |
| X2 | MCP read scopes match REST; a proposal changes nothing before human approval; stale approval cannot overwrite concurrent work |
| D1 | Seed on-time, late, cancelled, undated, reassigned and poor-condition assets; dashboard totals equal direct authorized queries at a fixed instant |
| M1 | Expand-only migration passes N-1 reader gate; roll forward/back binary versions without losing new records; journals unchanged |
| U1 | Test all three clickable flows with design partners before UI build; record success/time/errors and changes, then repeat against demo; WCAG 2.2 AA and field-condition review pass |
| E1 | County signs/trees/trails fixture: set condition tracking, recurring inspections and public problem reporter on the demo stack; customer-arc eval verifies backend state, not just narrative |

Keep receipts in ignored artifacts or CI artifacts; no partner identities,
recordings, private research or hidden eval material in public commits. UI work
requires contract freeze and prototype evidence; release requires demo/device,
CDN, two-instance deployed generation and metric evidence. This docs PR does not claim those tests have run.

## NEEDS-MAIN decisions before contract freeze

These details are not settled by the approved plan. Recommendations are proposals.

1. Calendar and catch-up: recommend local calendar intervals, last-valid-day
   clamping for month ends, explicit timezone/DST rules and bounded catch-up
   with a preview. Decide whether missed cycles create all work or one backlog task.
2. Programme revisions/cohorts: recommend snapshot each cycle's cohort and
   schedule; edits affect future unreserved cycles only. Decide how removed
   assets, cancellation and completion-mode cancellation advance the next cycle.
3. Reassignment completion: recommend preserve field payload, retain server
   assignee, require supervisor review before a stale-assignee completion changes
   terminal state. Decide authority for competing completions and old observations.
4. Condition/metrics: approve whether 1 is best or worst and label wording,
   poor-condition threshold, task priority enum/order, null service-date treatment, observation-time vs
   server-acceptance-time dashboard basis and completing-actor vs assignee grouping.
5. Public intake: approve abuse quotas/challenge provider and accessible fallback,
   data/photo/contact retention and consent, operating area, triage ownership and
   attachment limits. No anonymous path ships until these are configured/tested.
6. Contract placement: mobile repo rules put neutral DTOs/API clients in shared
   SDK packages while the rc.3 plan names mobile models. Recommend neutral shared
   SDK contracts with mobile-owned offline adapters; approve linked SDK work if needed.
7. Partner readiness: name 3–5 consenting partners and recruit resident testers;
   prototype/testing evidence is pending, never fabricated. Freeze numeric task
   success/time targets after baseline sessions and before implementation.

## Delivery tracking

[rc.3 asset-management epic](https://github.com/honua-io/honua-server/issues/7300)
tracks implementation across the owning repositories. Its child issues preserve
the contract and usability gates above; this specification adds no product code.
