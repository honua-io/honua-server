# ADR-0072: Evidence-gated raster storage layouts

## Status

Accepted (2026-08). This decision implements the storage-policy portion of
honua-server#3099 and is subordinate to the execution boundary in ADR-0071.

## Context

Honua currently persists one PostGIS raster value per logical raster, with the
`raster` column using TOAST `EXTERNAL`. It also stores optional rendered tiles
and PostGIS overview rasters. Registered COGs have a catalog and bounded direct
tile range reads. Managed Zarr readers support bounded parsing and slices, but
the catalog is not yet persistent and versioned.

Those capabilities do not prove that one representation is best for every
dataset. A monolithic database raster minimizes catalog and migration
complexity, tiled rows can reduce the pixels touched by database work, and an
authoritative object can reduce database storage and backup pressure. Each can
also fail badly under the wrong workload. Choosing by file size alone ignores
decoded pixels, grids, concurrency, object-request cost, database temp use,
restore objectives, and the operations that the representation can actually
serve.

## Decision

Raster layout selection is evidence-gated. The versioned protocol in
`benchmarks/Honua.Benchmarks/RasterStorage` is the canonical comparison method.
It defines deterministic small, large-scene, aligned-mosaic, and mixed-grid
fixtures; an exhaustive layout/workload support matrix; p50/p95 sampling; and
metric availability. A missing metric is recorded as unavailable, never as
zero. A storage layout cannot be promoted for a workload whose protocol cell is
unsupported or whose required capacity evidence is unavailable.

ADR-0071 remains controlling for execution placement. A database layout does
not mean all work runs synchronously or without admission, and an object layout
does not imply that GDAL belongs in the web process.

### Current layout status

| Layout | Policy status | Eligible use | Current limitation |
| --- | --- | --- | --- |
| PostGIS monolithic with `EXTERNAL` | Supported baseline | Small and moderate database-resident rasters whose measured serving, analysis, backup, and restore behavior fits operator budgets | A policy failure does not trigger implicit migration; the dataset remains readable while an explicit migration is planned |
| PostGIS tiled rows with indexed extents | Candidate target | Database-resident serving or analysis when the tiled result beats the monolith under the same fixture, grid, SLO, and capacity budgets | The benchmark schema is not a production schema; production catalog binding and migration require a separate implementation |
| Authoritative object COG | Supported only for declared bounded cells | Archive/external authority and compatible direct tile range reads | Tile evidence does not establish export, identify, statistics, mosaic, reprojection, surface, or zonal parity; those cells remain unsupported until their shared semantics land |
| Authoritative COG plus PostGIS materialization/cache | Not eligible | None until an authority, version, invalidation, fencing, and readiness lifecycle exists | The benchmark protocol records every cell as unsupported instead of simulating a cache |
| Authoritative object Zarr | Not eligible as a general raster layout | Existing bounded parser/slice envelopes only | Persistent versioned catalog and multi-object serving are owned by #3103; bounded reads are not general raster-store parity |

This table describes current capability, not the desired end state. New
implementation can change a cell only with a benchmark adapter, semantic proof,
and an update to the versioned protocol.

### Threshold contract

There are no universal byte or pixel cutoffs. Each deployment supplies:

- per-workload serving p50/p95 targets and the allowed p95 degradation under
  configured tenant concurrency;
- database CPU, block I/O, temp-byte, connection, duration, and maintenance
  budgets;
- object request, transferred-byte, latency, availability, and cost budgets;
- maximum ingest and restore windows; and
- maximum storage amplification and backup footprint.

A representation is **serving-eligible** only when every required serving cell
is supported, its measured p95 meets the configured SLO, its concurrent-tenant
p95 meets the concurrency SLO, and all database or object budgets pass. It is
**analysis-eligible** only when every required analysis cell is supported and
its duration, database CPU/I/O/temp, scratch, and storage amplification fit the
durable-analysis budget. It is **archive-eligible** only when integrity,
versioning, backup/replication, and restore evidence meet the configured RPO and
RTO; archive eligibility alone never advertises serving or analysis readiness.

Comparison uses runs from the same protocol version, fixture content, database
and object-store class, region/network topology, PostGIS version, cache state,
block-size candidate, sample count, and concurrency. Runs with unavailable
decision metrics are diagnostic only. Operators may choose a more conservative
layout, but may not force an unsupported cell or bypass ADR-0071's hard database
and execution gates.

### Canonical grid contract

A logical raster collection is aligned only when every scene has:

1. the same non-zero SRID;
2. equal X/Y pixel scale within the protocol tolerance;
3. equal X/Y skew within the protocol tolerance; and
4. an origin on the reference pixel lattice in both axes.

Width, height, and extent may differ. Pixel type, band schema, NoData, and
resampling semantics must also satisfy the canonical operation contract before
analysis readiness, but they do not make two affine grids aligned.

Ingest preserves source pixels. It must not silently shift an origin, change
resolution, reproject, or resample to make a collection pass. A mixed grid is
catalogable as source material, but logical-mosaic tile, export, mosaic,
reproject, surface, zonal, and concurrent-serving cells are ineligible until an
explicit normalization job creates a new versioned representation. The
benchmark fixture intentionally contains both a half-pixel origin offset and a
scale mismatch so validation cannot pass by comparing SRID and dimensions
alone.

### Blocks, pyramids, statistics, and readiness

- Benchmark database blocks at 128, 256, 512, and 1024 pixels where practical.
  The selected size is immutable metadata of a materialization. `256` is the
  protocol baseline, not a universal production threshold.
- Prefer square power-of-two blocks for serving. An analysis profile may choose
  a different measured winner, but all scenes in one materialization use the
  same block geometry and grid.
- Build overview levels by powers of two from the canonical grid until the
  configured smallest serving level or one-block extent is reached. Record the
  resampler and NoData policy. A COG's existing overview pyramid is accepted
  only when those levels and semantics satisfy the target profile.
- Compute cheap structural metadata and grid validation at ingest. Compute
  required statistics and expensive pyramids durably after ingest when they do
  not fit the synchronous budget. Never expose placeholder statistics as final.
- `archive-ready` requires immutable identity/version, size, checksum,
  footprint/grid metadata, and verified recovery policy. `serving-ready`
  additionally requires every configured serving cell, overview, and tile
  dependency. `analysis-ready` additionally requires aligned canonical grids,
  band/NoData semantics, statistics, and every configured analysis cell.

The current catalog does not persist all of these readiness states. Until it
does, a new tiled or hybrid layout must not be advertised as production-ready.
Existing monolithic and bounded COG surfaces keep their documented capability
envelopes.

### Online, idempotent migration

Existing monolithic rasters remain supported while they satisfy the configured
envelope. A policy change never rewrites them in place. Migration to a tiled or
object/hybrid representation must use this sequence:

1. Derive an idempotency key from the immutable source version/checksum, target
   layout, grid contract, block/pyramid/statistics profile, and policy version.
2. Create an attempt-scoped shadow representation. Repeating the same key
   resumes or returns the same target; it does not create another visible copy.
3. Validate pixel/grid semantics, checksums, required workload probes, storage
   metrics, statistics, pyramids, and backup/restore evidence.
4. Persist the target readiness evidence while it is still invisible to public
   reads.
5. Atomically switch the catalog's versioned storage binding when source and
   expected target versions still match. Object plus database publication uses
   ADR-0071's fenced, reconciled cross-store protocol and does not claim a
   cross-store transaction.
6. Retain the prior binding for the configured rollback window. Rollback is a
   conditional binding switch, not a pixel rewrite.
7. Reclaim the losing or expired representation asynchronously only after no
   catalog version references it.

Until a production target implements that sequence, the monolith is not
automatically migrated. The benchmark-only tiled schema must never be attached
to the production catalog.

### Dependency boundary

The benchmark project may connect to PostGIS and may exercise PostGIS functions
whose database service uses GDAL internally. It contains no GDAL binding,
native library, or CLI dependency. The authoritative web image remains
native-AOT and GDAL-free. Local or remote GDAL is limited to the isolated GP
worker/Batch envelopes in ADR-0071, including COG construction and large
external-object transforms that should not load the primary database.

## Consequences

### Positive

- Small installations retain the simple monolithic PostGIS path and do not
  need object storage.
- Tiled and object/hybrid promotion requires comparable evidence instead of a
  file-size rule of thumb.
- Unsupported COG and Zarr operations stay visible in the matrix and cannot be
  mistaken for parity.
- Grid incompatibility fails before a costly mosaic query and produces an
  explicit normalization decision.
- Database backup pressure and object request cost participate in the same
  decision as request latency.

### Negative

- Operators need representative benchmark environments and explicit SLO/RPO/
  RTO inputs.
- PostGIS per-query CPU requires an external sampler such as isolated container
  telemetry or `pg_stat_kcache`; PostgreSQL core metrics alone are insufficient.
- Tiled and hybrid layouts remain unavailable for production selection until
  their catalog/migration lifecycle is implemented and benchmarked.
- Layout changes consume temporary duplicate storage during the rollback
  window.

## References

- [ADR-0071: PostGIS-first, database-SLO-aware raster execution boundary](0071-raster-execution-boundary.md)
- [Raster storage capacity planning](../../../guides/deploy/raster-storage-capacity-planning.md)
- honua-io/honua-server#3099 - benchmark and storage policy
- honua-io/honua-server#3098 - durable large raster ingest
- honua-io/honua-server#3102 - bounded COG serving
- honua-io/honua-server#3103 - persistent versioned Zarr
