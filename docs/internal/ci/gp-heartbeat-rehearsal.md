# Native worker heartbeat recovery rehearsal

The `heartbeat-recovery` lane replaces the resilience harness's synthetic
`stale-lease` cell. It exercises the ordinary production heartbeat expiry and
retry path with two real workers. It does not establish exclusive partition
lease recovery, whole-catalog qualification, or release-candidate certification.

Dispatch `GP Qualification Harness Checks` with `native_store_rehearsal=true`
and `native_rehearsal_lane=heartbeat-recovery`. The existing producer verifies
the retained server image and builds its exact matching, unmodified canonical
worker source into the runner-local registry. The wrapper always records
`qualification:false`; it requires all three actual topology, stale-lease, and
cleanup receipts. No cloud resources or public worker images are created.

The native fixture is the existing 500-point `gdal.ogr2ogr` workload with
independently known IDs and coordinates. Original and recovery workers mount
separate barrier directories and resolve the same image ID, digest reference,
and source revision. Redis is the runner-local Compose instance; this is not
ElastiCache, network partition, or Redis failover evidence. The original container is paused after its real native
process starts. This suspends its heartbeat without changing Redis records,
queue indexes, timestamps, retry policies, or retention settings.

The receipt requires the unmodified 90-second heartbeat timeout, the ordinary
30-second retry delay, and another worker claiming the next attempt. The
winning attempt must complete with one staged artifact. The original process
is then unpaused and released; its own retained log must show the exact job's
terminal transition rejected because ownership changed. Killing the old
worker during cleanup never satisfies that observation. The winning owner,
attempt, entire canonical job record (including version, progress, and warnings),
descriptor, and authenticated output bytes must stay
unchanged through another 65 seconds (two reconciliation intervals). Any
losing-attempt staging must disappear through the configured ordinary output
sweeper, leaving exactly one output object.

Both downloaded GeoJSON files, worker identities, durable state observations,
per-worker barrier records, and logs are retained. The independent point oracle
checks both downloads. A failed observation stays failed even when cleanup
succeeds. Failed unpause or topology restoration blocks later live scenarios;
final cleanup remains permitted.

This is bounded engineering evidence for #3849, #3854, and #3852. Published
candidate worker identity, frozen release configuration, the other catalog
operations, and the remaining resilience cells are separate qualification work.
