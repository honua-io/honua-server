---
type: guide
title: "Track external database edits"
description: "Register an external writer, retire origin cache entries on every task, and drain regional CDN invalidations after committed edits."
---
# Cache invalidation for external database edits

External edit tracking is an opt-in deployment feature for layers changed by a
database writer outside Honua. It requires the external-edit journal migration
and `ExternalEdits__Enabled=true` on every serving task. It is disabled by default.
Enable it only after every task runs a version that supports revision-bound caches
and regional tile tags, and purge existing CDN responses once during rollout.

## Register a layer and its writer

An administrator registers the storage layer ID in the configured Honua schema:

```sql
INSERT INTO honua.external_layer_revisions (layer_id, enabled)
VALUES (7, true)
ON CONFLICT (layer_id) DO UPDATE SET enabled = true;
```

Replace `honua` with the deployment's schema. The external writer must call
`record_external_layer_change` in the **same transaction** as each edit batch:

```sql
BEGIN;
-- Apply the layer's inserts, updates and deletes here.
SELECT honua.record_external_layer_change(
    7, ARRAY[-123.1, 45.4, -122.8, 45.7]::double precision[]);
COMMIT;
```

The optional extent is `[west, south, east, north]` in EPSG:4326. It must cover
all old and new feature locations affected by the transaction, including deleted
features. Split a batch at the antimeridian or omit the extent. Omitting it uses a
broad layer purge. Incorrectly narrow extents can leave CDN tiles stale.

The migration does not modify source tables, install triggers, or grant writer
permissions. The database administrator must give the writer the permissions it
needs on the function and journal tables. A missing or disabled registration
returns `NULL` and does not enqueue a change. Check the returned revision while
integrating the writer. Track at most 1,024 layers per deployment; exceeding this
limit causes revision lookups to fail closed.

## Cache behavior

Each committed revision changes the origin response-cache and output-cache keys
on every host. The revision is read once per request, and a cache fill keeps the
revision from before its query. Requests already in flight can finish using their
original revision; subsequent requests cannot reuse that response under the new
revision. A failed database lookup fails the request rather than replaying an old
cache entry.

Serving hosts read the durable change journal about once a second. A host claims
a layer for 30 seconds before purging it, so each layer is purged at most once per
30 seconds across all hosts, and the changes recorded in the meantime are combined
into the next purge. A change is removed from the journal only after its purge
succeeds. A layer whose purge fails stays queued and is retried with a growing
delay of up to 15 minutes, without holding up other layers. A purge record carries
the change ID `external-layer:<layer-id>:<latest revision>`. With tag invalidation
enabled, changes that all supply an extent purge regional native tiles, overview
tiles, buffered tile responses (including H3 tiles) and tile metadata for their
combined extent. A change without an extent makes that purge layer-wide. Other
protocols retain their layer-wide purge.
Unsupported or oversized invalidations fall back to a broad purge. CDN propagation
still takes the time required by the configured provider.

AWS Lambda does not run this background drain. Use an always-on serving host to
drain the journal, or retain the route's CDN TTL and an explicit purge process.
Origin revision checks still apply when the feature is enabled.

## Disable tracking

Stop the external writer or remove its hook before disabling the feature on
serving hosts. Drain pending changes and purge the CDN before returning to
ordinary TTL-based caching. Keep revision rows: deleting or resetting revisions
can make an earlier cache key reachable again. Disabling a layer registration
does not remove its revision from cache keys.
