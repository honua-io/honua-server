# ADR-0087: One first-party field sync contract

## Status

Proposed (2026-11-13), release/2027.1. Documentation decision under
[server #7543](https://github.com/honua-io/honua-server/issues/7543).
Implementation PRs stay draft until MAIN promotes the release. This ADR neither
activates a client migration nor declares the current API ready for field users.
Builds on [ADR-0036](0036-mobile-sdk-language-strategy.md) and
[ADR-0084](0084-task-first-asset-management.md).

## Context

First-party offline work has three competing paths: the dedicated
`/api/v1/fieldcollection` change API, FeatureServer replica synchronization used
by the mobile transport, and the proposed task command contract. Collect also
has a duplicate store/sync implementation. A device must persist one queue and
one local store across process death, replay without duplicate work, and retain
an operator's work when authority changes while offline.

Task commands carry domain rules that generic feature edits cannot express:
assignment, legal transitions, pinned forms, one canonical completion, history
and atomic summary updates. Unifying sync must preserve those rules.

## Decision

Use the **fieldcollection contract family as the single first-party offline
exchange**, backed by one mobile SDK store, queue and sync engine. Extend its
versioned envelope for task commands rather than building another task sync
engine. The task REST API remains an online domain interface; the sync adapter
calls the same neutral task service, never another protocol handler. The SDK
queue dispatches task commands through that shared contract after the extension
and its compatibility tests ship. Current generic feature pushes cannot perform
task commands.

Keep FeatureServer replicas, OGC API Features and WFS-T for supported third-party
clients. Deprecate their use as first-party offline transports, not those server
endpoints. Remove the duplicate Collect store/sync engine under
[mobile #421](https://github.com/honua-io/honua-mobile/issues/421), preserving
useful capture models before deletion. Remove the SDK's replica transport only
after the replacement proves equivalent journeys; no second live queue, silent
fallback or timestamp-based conflict winner. App shells consume published SDK
packages and do not own another database/sync engine.

### Identity, durability and generations

Devices allocate stable UUIDs for new records and each immutable edit/command.
Retry the identical body with the same ID. A changed or resolved edit gets a new
ID and current expected version. Bind receipts to authenticated service,
principal and registered device identity; a supplied client header alone conveys
no authority. Reject ID reuse with another actor, operation or body. Task
`changeId = commandId` resolves to the domain service's shared command receipt,
so online and offline delivery cannot execute it twice.

A row version is an optimistic concurrency token; a server generation orders
committed changes, not wall-clock time. Sequence gaps are legal. Commit canonical
feature/task state, version, change-log entry, receipt and durable outbox intent
atomically. All supported writers feed that same authoritative state and feed;
fieldcollection must not become a parallel feature database. Configuration
revisions in [ADR-0086](0086-one-generation-for-configuration-and-caches.md) are a
different namespace and never substitute for data cursors.

Apply a received page, tombstones and its checkpoint in one local transaction
before acknowledging it. A crash before local commit replays the page; a crash
after commit repeats the acknowledgement safely. Pulling does not acknowledge.
Persist command order per task; reconcile creates before dependent operations.
Receipt replay cannot repeat inspection/history, attachment links or automation.

### Pull scope, tombstones and history expiry

Bind a cursor to server/service, principal/device, layer set, area/filter and
permission revision. Every page reapplies layer/row/field authorization. A
committed watermark bounds a stable page window under concurrent writes;
return a continuation and tombstones for deletions and features leaving scope.
Reauthorize before returning any conflict projection. Revoked access invalidates
the old scope without disclosing inaccessible content; remove protected local
copies under device retention policy while preserving recoverable user input in
an authorized recovery workflow.

A changed scope, invalid cursor or cursor older than configured retained history
returns the proposed named `resync-required` disposition with a safe reason and
repackage instruction. It must not return a seemingly complete empty delta.
Download a fresh scoped package into staging, retain unsent edits separately,
validate the manifest and atomically install it, then reconcile those edits
under current permissions and versions. Never clear the queue to recover a
cursor. Retention policy and cursor lifetime must be documented together.
Packages include layers, assigned tasks, pinned forms and required related data;
manifest generation, integrity and resumable installation belong to
[server #7546](https://github.com/honua-io/honua-server/issues/7546).

### Conflict and command responses

The following **proposed domain dispositions/reason names** map every row of the
[asset-management offline table](../specs/asset-management-rc3.md#offline-sync-contract).
They are not new values available from today's generic feature endpoint.
A versioned task response carries command ID, `accepted`, `duplicate`, `conflict`
or `rejected`, safe current version/projection where permitted, field reasons
and a durable receipt/review reference. `duplicate` replays the original result
and its original disposition; it is not permission to disclose another actor's
receipt. Preserve original input and provenance until review/correction resolves.

| Offline case | Named disposition / reason | Required resolution |
|---|---|---|
| Work request created from an asset | `accepted` / `request-created`; retry `duplicate` | One client task UUID and receipt; reconcile create before dependent commands |
| Server reassigned | `conflict` / `assignment-changed` | Server assignment wins; retain observations separately and recheck permitted actions |
| Local completion vs older server fields | `conflict` / `completion-data-diverged` | Preserve notes, answers, observations and photo references; reconcile against current version without a stale empty overwrite |
| Reassignment plus local completion | `conflict` / `completion-review-required` | Preserve assignment and completion separately; pending policy ruling means needs review, never accepted completion |
| Cancelled task or revoked write access | `rejected` / `task-cancelled` or `write-access-revoked` | Retain recoverable input; no cancellation override or unauthorized server projection |
| Two completions | `conflict` / `completion-already-recorded` | One canonical inspection/service record; retain second payload as a correction candidate |
| Duplicate command/reconnect | `duplicate` / `receipt-replayed` | Same original receipt, no repeated side effect; different body gives `rejected` / `command-id-reused` |
| Asset changed since download | `accepted` / `observation-recorded` | Keep authorized observation; serialize conditional summary updates by stable asset identity; stale summary alone does not reject completion or overwrite unrelated fields |

For ordinary feature edits, an authorized three-way merge may combine disjoint
field edits only when the base snapshot proves they are disjoint. Same-field or
geometry divergence retains both candidates for review. Missing base information
requires explicit resolution, not a guessed merge. Deleted assets cannot be
silently resurrected; retain the local edit as a review candidate. The current
four generic conflict types remain supported until an explicit versioned
extension supplies these field-level and review semantics.

### Attachments and visible device state

Keep attachment identity, content hash, ownership, transfer checkpoint and local
file independent of the record queue entry. Stage and validate uploaded bytes
before a task completion references required ready attachments. Database
acceptance is atomic for records and validated references, not object-storage
bytes. Record acceptance and optional attachment transfer have separate durable
receipts and retry state; retries cannot associate an attachment twice.

The UI distinguishes locally saved, queued, uploading, server accepted and needs
review. An accepted feature with pending optional photos still shows pending
photos. A task requiring photos cannot claim accepted completion until its
required references are ready. Interrupted transfer resumes; permanent rejection
retains recoverable local input and explains the repair. Installable Android/iOS
builds and the scripted founder journey are implementation-phase evidence, not
claims made by this documentation change.

## Current API audit and implementation gaps

Audited against repository source on 2026-11-13. Route registration lives in
[FieldCollectionSyncEndpoints.cs](../../../../src/Honua.Server/Features/Mobile/FieldCollection/FieldCollectionSyncEndpoints.cs);
wire models in
[FieldCollectionSyncModels.cs](../../../../src/Honua.Server/Features/Mobile/FieldCollection/FieldCollectionSyncModels.cs);
provider behavior in
[PostgresFieldCollectionSyncStore.cs](../../../../src/Honua.Db/Postgres/Features/Mobile/FieldCollection/PostgresFieldCollectionSyncStore.cs).

| Surface today | Verified behavior | Required follow-up |
|---|---|---|
| Generation/pull | GET `generation`; GET `changes` uses `sinceGeneration` and `limit` (default 200, maximum 1000), returns `serverGeneration`, `nextCursor`, `hasMore`; committed watermark bounds the read | Scope/permission-bound stable continuation, tombstones for scope exits, retained-history floor and explicit resync: [#7545](https://github.com/honua-io/honua-server/issues/7545) |
| Cursor | GET/POST `sync-cursor`; acknowledgement is monotonic, rejects future generations; GET changes does not advance it | Scope identity, expiry/repackage and local atomic apply/checkpoint: #7545 and mobile #421 |
| Push | POST `changes`; `changeId`, `featureId`, `layerId`, insert/update/delete, `baseVersion`, timestamp and JSON feature; IDs are strings, not enforced UUIDs | Stable ID translation/validation and versioned typed command envelope: [#7549](https://github.com/honua-io/honua-server/issues/7549) |
| Feature truth | Writes `fieldcollection_features` and `fieldcollection_changes`, separate from canonical layer data; transactional row/log/receipt, feature lock and generation allocation lock through commit | Canonical feature pipeline integration and all-writer change capture, never a second feature database: #7549 |
| Identity/access | Administrator authorization plus offline-sync entitlement; caller header `X-Honua-Client-Id` (truncated at 128), principal-name/default fallback; pull does not filter layers/area/permissions | Ordinary field-user authorization, bound identity and safe projections: #7549; scoped feed: #7545. Do not distribute administrator credentials to phones |
| Replay | Durable `(client_id, change_id)` stored result, no body-hash comparison; applied replay can still reach post-push automation dispatch | Actor/body mismatch rejection and replay-safe effects: #7549 |
| Conflict | Wire outcomes `applied`, `conflict`, `rejected`; `update-update`, `update-delete`, `delete-update`, `delete-delete`; safe rejection reason and current version/feature where present | Field/base snapshots, review/correction durability and task dispositions above: #7549. `applied` is not the proposed task value `accepted` |
| Deletes | Tombstones for known features; insert over a tombstone can recreate the row | Preserve third-party compatibility; add explicit field-domain deleted-asset policy and scope-exit/permission handling: #7549 / #7545 |
| Task/package/attachment | Generic fieldcollection store has no task command, form/package manifest or resumable attachment protocol | Task service/receipt integration and attachment contract: #7549 (links existing task tickets); package: #7546 |

The asset specification previously described the original globally keyed feature
receipt. Migration `057_AddFieldCollectionPushedClientScope.sql` changes it to
`(client_id, change_id)`; neither shape is the proposed scoped task command
receipt. Existing wire behavior is preserved until explicit migration/versioning
and tests ship. No implementation or security gap is resolved by this ADR.

## Consequences and adoption gates

One SDK queue makes restart/retry and operator status consistent, at the cost of
explicit server work before client transport replacement. Track server gaps in
#7545, #7546 and #7549; client consolidation in mobile #421; third-party
interoperability in [#7544](https://github.com/honua-io/honua-server/issues/7544).
Task domain implementation remains with its existing owners. No endpoint is
removed, no task business rule is bypassed and no release hold is lifted.

Adoption requires canonical all-writer integration, authorization and negative
projection tests, scoped paging/expiry recovery, command-body replay tests,
conflict preservation and interrupted attachment/package journeys. Validate
process death, network loss and reconnect on both phone platforms with a written
script and installable builds. Migrate existing unsent queues explicitly and
prove the old transports can be removed without losing work. Public client
compatibility stays covered independently of first-party migration.
