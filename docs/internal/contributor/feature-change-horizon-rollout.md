# Feature-change horizon

The change-feed cursor is a committed prefix of the global feature generation sequence.
`honua.feature_change_read_horizon()` (migration 213) is the single source of that cursor
for sync, replica registration, version ancestors, alerts, temporal history and realtime
streaming. Readers that inspect changes for upload conflicts still inspect committed
changes; those probes do not advance a download cursor.

Migration 228 activates the committed prefix. Before it, both feature-writer triggers took
one exclusive advisory lock from `nextval` through commit, which serialized every feature
write in the database and kept `MAX(generation)` gap-free. Rolling deployment with an
older reader is not supported (founder ruling, 22 November 2026), so 228 replaces the
function bodies directly.

## Writers

`honua.track_feature_changes()` and `honua.track_version_edits()` take
`pg_advisory_xact_lock_shared(144047712, 0)` before `nextval` and hold it through commit or
rollback. Writers on any layer, and on different rows of one layer, run concurrently and
may commit out of generation order. Row locks still serialize edits to the same row.

Every allocator of `honua.sync_generation` that writes `honua.feature_changes` must hold this
lock before `nextval`. FieldCollection allocates from the same sequence under its own lock;
those values are harmless gaps in the feature prefix. The sequence must stay monotonic with
`CACHE 1` and must never be reset; the publisher checks this.

An exclusive request for the same key (the demo STAC seed's relation recovery) still waits
for every in-flight writer and holds new writers back.

## Publisher

`honua.publish_feature_change_prefix()` stores the prefix in `honua.feature_change_prefix`.
It reads the allocated sequence value first, then lists the virtual transaction ids that
hold the writer lock in this database (`honua.feature_change_writers()`). A writer takes the
lock before `nextval`, so every allocation at or below that value belongs to a transaction
that has finished or is in the list. With no holder the value is published at once;
otherwise it is stored as pending with the holder list and published by the first later
call that finds none of those transactions still holding the lock. Rolled-back allocations
become harmless gaps.

Only feature writers can hold the prefix back. Unrelated transactions, prepared
transactions that hold no writer lock, and other databases on the cluster do not. A long
import into features holds the prefix at the generation before its first allocation until
it commits; edits on other layers keep committing, and appear once the import finishes.

Call the publisher in a fresh, writable READ COMMITTED transaction that owns no transaction
id; it rejects anything else. Competing publishers use a separate non-blocking advisory
lock (key 2) and never block a writer. An idle call reads one row and writes nothing.

## Who publishes

Readers advance the prefix themselves, so no background service has to run for a
database (`FeatureChangePrefix` in Honua.Db):

- `PostgresChangeTracker.GetCurrentGenerationAsync`, replica registration, version create
  and reconcile wait up to 2 s for writers that were in flight when the call started, then
  read the horizon. A caller therefore sees changes committed before it asked; under a
  long import it gets the older, still-safe horizon. Reconcile analyses only DEFAULT changes up
  to the horizon it then records as the ancestor, so a later reconcile neither repeats nor
  misses a change. An update overlap whose base image still equals the live DEFAULT row (for
  example after a version-wins rebase) is not a conflict.
- Version post waits, with no time limit and ignoring request cancellation once committed (the old
  global writer lock blocked it as long), for the prefix to cover its own replayed rows before it advances the
  common ancestor, so the posted rows are not treated as concurrent DEFAULT edits.
- The realtime feature log advances the prefix on every head and cursor read, waiting up to
  250 ms for writers already in flight, so a read that lands during a short edit still
  delivers it. Each commit sends `honua_rt_features`, which wakes the dispatcher, so a commit that was held
  behind an in-flight writer is published when that writer's own commit arrives. A
  rolled-back writer sends no notification; the 10 s safety poll covers that case.
- The outbox dispatcher publishes before each claim.

## Proof

`FeatureChangeWriterConcurrencyTests` holds one writer after allocation and proves a second
writer, on another layer or another row of the same layer, commits first; the cursor holds
until the first commits or rolls back, and resume loses neither change. The same suite
covers COPY alongside an edit and a branch overlay alongside a DEFAULT write.
`FeatureChangePrefixPublisherTests` covers the publisher contract, including an unrelated
open transaction that must not hold the prefix back.
