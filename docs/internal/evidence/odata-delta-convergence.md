# Durable OData delta convergence (#3872)

The realtime release promise requires a subscriber's materialized query to converge
after equal-timestamp updates, physical deletes, filter exits and server restarts.
A last-updated predicate over current rows cannot meet that promise.

Tracked OData queries now persist their complete authorized projection in PostgreSQL.
The first page establishes one query image; every continuation reads that immutable
image. A terminal poll compares a fresh canonical query image with the prior image.
Net changes use ascending public object IDs, and page ordinals advance within one
immutable receipt. Database timestamp precision never controls delivery.

The public cursor is `v2.<receipt UUID>.<ordinal>.<phase>`, where `p` continues pages
and `t` polls from the saved terminal baseline. The server stores the defining
filter, projection, computation, format and page size. Credentials are not stored.
Each request reauthorizes its layer and binds the receipt to the canonical actor,
resolved tenant, schema, route, metadata graph tag, row policy and field masking.
Changing that binding returns `410 DeltaScopeChanged` without any saved values.

Rows that leave the query emit only `ObjectId`, `LayerId` and
`"@removed":{"reason":"changed"}`. This represents departure from the query,
including physical deletion, without disclosing a now-inaccessible row. The public
JavaScript SDK's `src/realtime/odata-delta.ts` accepts this key-preserving removal
shape. Delete/recreate collapses to the current projected row; an unchanged net
projection needs no repeated event.

Receipts expire after 24 hours. Missing, expired and invalid future state returns
`410 DeltaTokenExpired`. Malformed tokens and query redefinitions return typed 400
errors. Timestamp-only legacy tokens require an explicit new tracked baseline;
they receive 410 and cannot silently rebaseline. Snapshot capacity is bounded at
10,000 rows and 16 MiB of projected row JSON; capacity overflow returns 413.
Tracked queries require count capability, and an incomplete provider result returns
409 `DeltaSnapshotIncomplete`. They require a positive page size and do not support initial skips,
expansion, bbox or Parquet. Ordinary untracked query behavior is unchanged.

The native .NET regression fixture commits independently specified names, deletes,
recreation and filter transitions at one frozen timestamp, pages at size one, and
compares exact materialized values. It repeats the terminal poll, recreates the
entire host/service provider with PostgreSQL retained, and commits another update
at the same timestamp. Separate paging coverage changes every row after page one
and requires the remaining pages to retain the original values, followed by a
terminal poll that observes the newer values. Recovery tests exercise real stored
receipts, including expired and future state.

These are local server regression proofs. The immutable exact-candidate receipt
still needs a cut image digest and pinned public SDK run, with raw pages, mutation
commit identifiers and per-page state hashes. The release decision record reports
the candidate as not yet cut; local results must not be relabeled candidate proof.

## Local verification

On 2026-09-06, the native Windows .NET 10.0.100 run passed all 20
`ODataDeltaTests` / `ODataDeltaValueTests` cases with zero failures or skips.
The Release build treated warnings as errors and used `-maxcpucount:4`.
After the initial build, the corrected PostgreSQL receipt store was rebuilt and
the focused tests reused unchanged project outputs with `BuildProjectReferences=false`.
The result is retained locally as `proofs-3872-results/odata-delta-3.trx` (9m08s).
This includes explicit rejection of empty delta tokens and legacy snapshot cursors.

The subsequent native Windows full architecture run passed all 287 cases with
zero failures or skips after regenerating the feature catalog through the
`FeatureCatalogEmitter` test (1 passed). Both completed on 2026-09-06 using
the Release assemblies built with warnings as errors and `-maxcpucount:4`.
The architecture result is retained as `proofs-3872-results/architecture-delta.trx`
(3m29s). This validates integration operation attributes and committed catalog
parity; the review-follow-up delta execution cases require their own receipt.

The review-follow-up focused run passed all 22 delta cases with zero failures
or skips on 2026-09-06 (5m25s), retained as
`proofs-3872-results/odata-delta-6.trx`. This includes bounded cross-node clock
skew, baseline versus delta context, and structured error-code telemetry under
the production exception-detail redaction policy. The changed OData production
assembly and test assembly were rebuilt with zero warnings or errors before
execution; unchanged dependency outputs were reused.

The Linux review-adjudication run also passed all 22 delta cases with zero
failures or skips on 2026-09-06 (6m48s), recorded in
`/tmp/pr4441-test-results/delta-focused-fixed.trx`. The first run exposed a
test-helper portability bug: Unix treated a root-relative request as a file URI
and returned 404. Preserving that URL and its query string lets the unchanged
convergence assertions execute through paging and host restart. The full Release
test-project build and subsequent affected-assembly rebuilds passed with zero
warnings or errors; Roslyn shared compilation and the lane CPU cap remained enabled.
