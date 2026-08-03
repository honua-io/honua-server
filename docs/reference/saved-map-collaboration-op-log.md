# Saved-map collaborative edit operation log

Server-owned durable ordering and conflict behavior for multi-user saved-map editing. Where presence and live cursors are ephemeral ([collaboration session transport](#related-surfaces)), saved-map edits are durable: every accepted edit is appended to a per-map operation log, assigned a monotonic server cursor, and replayable for reconnecting clients. This page is the contract for `honua-sdk-js` and Portal consumers.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/saved-maps/{mapId}/collaboration/operations` | Append one edit operation. |
| `GET` | `/api/v1/saved-maps/{mapId}/collaboration/operations?since={cursor}` | Replay operations after a known cursor. |

Both routes require an authenticated principal **and** a per-map capability grant — see [Authorization](#authorization). Responses use the standard `ApiResponse<T>` envelope (`data`, `success`, `error`).

## Operation envelope

A client submits an append request; the server stamps it into a durable envelope.

### Append request

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `operationId` | string (≤200) | yes | Client-generated, stable id. Re-submitting the same id is idempotent per map. |
| `actorId` | string | no | Defaults to the authenticated principal's stable identifier (`sub`/name id). |
| `baseCursor` | int64 | no | Server cursor the edit was authored against. Defaults to `0` (empty-log base). |
| `kind` | enum | yes | Operation kind (see [families](#operation-families)). |
| `payload` | JSON | no | Stored opaquely; the MVP log does not interpret WebMapDoc fields. |
| `idempotencyKey` | string (≤200) | no | Optional second dedupe key alongside `operationId`. |

### Accepted envelope

The append response (and every replayed operation) carries:

| Field | Type | Notes |
| --- | --- | --- |
| `operationId` | string | Echoes the client id. |
| `mapId` | string | Saved map the operation belongs to. |
| `actorId` | string | Resolved actor. |
| `baseCursor` | int64 | Cursor the edit was authored against. |
| `kind` | enum | Operation kind. |
| `serverCursor` | int64 | **Monotonic** server-assigned cursor; the reconnect/replay key. |
| `acceptedAt` | timestamp | Server accept time (UTC). |
| `payload` | JSON | Original payload. |
| `idempotencyKey` | string? | Present when supplied. |

Cursor `0` is the empty-log base. The first accepted operation on a map gets `serverCursor: 1`; cursors increase by one per accepted operation and never repeat.

## Append semantics

The append response `status` is one of:

- **`accepted`** — the operation entered the log. `operation.serverCursor` is the assigned cursor and `headCursor` is the new log head. A replayed duplicate is also `accepted` with `isDuplicate: true` and the **original** cursor (a duplicate never advances the head). HTTP `200`.
- **`conflict`** — the operation could not be merged safely from its `baseCursor`. The body carries a [conflict payload](#conflict-semantics); no operation is appended. HTTP `409`.
- **`resyncRequired`** — `baseCursor` is ahead of the head or older than the retained replay window. The caller must [resync](#snapshot-compaction-and-resync) from a full document snapshot. HTTP `409`.

### Idempotency

Duplicate detection is keyed on `operationId` and, when supplied, `idempotencyKey`, scoped per map. A repeat of either returns the originally accepted envelope with `isDuplicate: true` and leaves the head cursor unchanged. Clients should generate a stable `operationId` per logical edit and safely retry on network failure.

## Replay and catch-up

`GET …/operations?since={cursor}` returns every operation with `serverCursor > since`, ordered by cursor, so a reconnecting client can reapply missed edits in order. The response carries:

| Field | Notes |
| --- | --- |
| `status` | `ok` or `resyncRequired`. |
| `sinceCursor` | The cursor the caller replayed after. |
| `headCursor` | Current log head; the cursor to resume from. |
| `minimumReplayCursor` | Earliest cursor the retained log can replay from. |
| `operations` | Envelopes after `sinceCursor`. |

`since=0` against an empty or unknown map returns `ok` with no operations. A `since` value above the head or below `minimumReplayCursor` returns `resyncRequired` — the client must reload the saved-map document and resume from `headCursor`.

## Operation families

Each `kind` maps to a conflict **family**. The MVP policy treats per-aspect families as independently reconcilable and whole-document replacement as unsafe.

| `kind` | Family | Conflict class | Accepted |
| --- | --- | --- | --- |
| `SetMetadataField` | Metadata | safe scalar | **No — rejected with `400`** |
| `SetViewport` | Viewport | safe scalar | Yes |
| `SetLayerVisibility` | LayerVisibility | safe scalar | Yes |
| `ReorderLayers` | LayerOrdering | safe scalar | Yes |
| `PatchStyle` | Style | safe scalar | Yes |
| `ReplaceWebMapDocument` | WebMapDocument | unsafe | Yes |

### `SetMetadataField` is not accepted (honua-server#2999)

The append endpoint rejects `SetMetadataField` with `400`. The checkpoint applier persists
operations onto the Studio composition body, which carries no metadata bag, so there is no
transform that could durably apply a metadata edit. Admitting the kind anyway would be worse
than rejecting it: the operation would take a permanent op-log cursor and then fail every
later checkpoint (`422` while retained, `409` on the continuity guard once pruned), leaving the
map unable to save any further version.

Edit saved-map titles and other metadata through the Studio draft surface
(`PUT /api/v1/studio/package-drafts/{draftId}`) instead. The enum member is retained so existing
payloads still parse and receive this explicit error rather than a deserialization failure.

Only kinds the checkpoint applier can express are admitted; the append endpoint and the applier
share one source of truth (`SavedMapOperationDraftApplier.IsCheckpointable`), and payloads are
shape-validated on admission for the same reason.

Viewport payloads (`SetViewport`, and the `view` member of a `ReplaceWebMapDocument`) are held to
the shared Studio view contract (`StudioCompositionViewBounds`): `bbox` is exactly four ordinates,
`center` exactly two, `zoom` is `0..24` and `pitch` is `0..85` (both inclusive). These are the same
bounds the Studio composition MCP tool schemas advertise, so an out-of-range viewport is rejected
with `400` here instead of consuming a permanent cursor for a view no client can render.

## Conflict semantics

The MVP conflict policy is **last-writer-wins (LWW) for safe scalar fields** and **fail-with-conflict for whole-document replacement**:

- **Safe scalar families** (viewport, layer visibility, layer ordering, style; see the metadata note above) never conflict with each other. Concurrent edits to these aspects are appended in cursor order, and the last operation per aspect wins on replay. Two clients restyling the same layer concurrently both append; the higher-cursor style is the effective value once both replay. This is safe because each family targets an independent slice of the saved-map document.
- **`ReplaceWebMapDocument` (unsafe)** conflicts with **any** concurrent operation, and any safe operation conflicts with a concurrent `ReplaceWebMapDocument`. Whole-document replacement based on a stale cursor would silently discard peer edits, so it returns a typed `conflict` instead.

A `conflict` response body carries:

| Field | Notes |
| --- | --- |
| `code` | Stable `saved-map.operation.conflict`. |
| `message` | Human-readable reason. |
| `baseCursor` | The stale base cursor that produced the conflict. |
| `headCursor` | Current head to replay toward. |
| `conflictingOperations` | The concurrent envelopes that made the append unsafe. |

On conflict, the client should replay from `baseCursor` to `headCursor`, rebase its edit, and re-append with a fresh `baseCursor`.

### CRDT/OT seam

The policy is intentionally pluggable (`ISavedMapOperationConflictPolicy`). When MVP operation families outgrow LWW — for example field-level WebMapDoc transforms or rich concurrent object edits — a CRDT/OT policy can replace the MVP policy without changing the envelope, cursor, or replay contract.

## Snapshot compaction and resync

The operation log is bounded: each map retains a finite window of recent operations and prunes older ones. Replay and append are valid only within `[minimumReplayCursor, headCursor]`.

- A cursor inside the window replays/appends normally.
- A cursor older than `minimumReplayCursor` (compacted away) or ahead of `headCursor` returns `resyncRequired`.

`resyncRequired` is the explicit signal that the client must reload the current saved-map document snapshot (the compacted base state) and resume the log from `headCursor`. Compaction therefore preserves replay from supported cursors and returns a typed resync signal for cursors outside the window — it never silently drops edits.

## Authorization

Both endpoints reject anonymous callers at the middleware boundary (`401`), then run a **per-map** capability check through the shared `ISavedMapCollaborationAuthorizer` — the same authorizer the [collaboration session join](#related-surfaces) uses, so presence and durable edits share one identity/RBAC seam. The check is **fail-closed**: a generally authenticated principal that lacks a grant on *this* saved map gets:

- `401 Unauthorized` when authentication is required, or
- `403 Forbidden` when authenticated but not permitted on the map.

Durable edits are never accepted on the strength of authentication alone.

## Related surfaces

- **Session transport** (presence, live cursors, follow): `POST /api/v1/saved-maps/{mapId}/collaboration/sessions/join`, `GET …/sessions/stream`. Ephemeral; see issue #971.
- **Feature-service edit locks/conflicts**: row/geometry edit persistence is a separate seam from saved-map document edits.
- **Capability discovery**: `GET /api/v1/capabilities/manifest` reports realtime/transport availability — see [integration patterns](integration-patterns.md#runtime-capability-discovery). Discovery only; authorization still happens at the operation endpoint.
