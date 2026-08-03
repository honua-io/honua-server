# Raster storage capacity planning

Use this guide to decide whether a raster should remain a monolithic PostGIS
value, become a tiled PostGIS materialization, or use an authoritative object
representation. The answer is workload- and deployment-specific. Do not choose
from source file size alone.

The controlling decisions are
[ADR-0071](../../internal/contributor/adr/0071-raster-execution-boundary.md)
and
[ADR-0072](../../internal/contributor/adr/0072-raster-storage-layout-policy.md).
PostGIS is preferred for supported data-resident work that fits the database
budget. GDAL remains outside the web container and is available only through
isolated GP/native worker or Batch execution.

## What is supported now

| Representation | Use it for | Do not assume |
| --- | --- | --- |
| Monolithic PostGIS raster, TOAST `EXTERNAL` | Baseline storage for small/moderate database-resident rasters and installations without object storage | That a large export, mosaic, or concurrent analysis fits the primary database SLO |
| Tiled PostGIS rows | Benchmark candidate for windowed reads and database analysis | That the benchmark scratch schema is a supported production catalog layout |
| Registered COG | Authoritative external/archive object and compatible bounded direct tile reads | Identify, export, statistics, mosaic, reprojection, surface, or zonal parity |
| COG plus PostGIS materialization/cache | None yet | Authority, invalidation, readiness, or failover behavior; these cells are explicitly unsupported |
| Zarr | Existing bounded parser/slice capability only | A persistent general raster store; #3103 owns that lifecycle |

Existing monolithic data remains readable within its measured envelope. Honua
does not require object storage for a baseline installation and does not migrate
data merely because it crosses an unmeasured size boundary.

## Establish the decision budgets

Record these inputs before benchmarking:

- tile, identify, export, and other serving p50/p95 SLOs;
- allowed p95 degradation at the expected number of concurrent tenants;
- database CPU, block-read, cache-hit, temp-byte, connection, and maintenance
  budgets;
- object request, transferred-byte, latency, availability, egress, and monthly
  cost budgets;
- ingest maintenance window;
- backup RPO, restore RTO, and retained-backup budget; and
- maximum physical-to-logical storage amplification.

Use an otherwise idle database when reading `pg_stat_database` deltas. Core
PostgreSQL does not expose reliable per-query CPU. Add an isolated database
container/VM sampler or `pg_stat_kcache` and mark its source in the result. A run
whose decision metric remains `unavailable` is diagnostic and cannot justify a
layout promotion.

## Generate and run the protocol

The harness is in the benchmark project, not a production server or worker
path. It has no GDAL dependency.

Generate the machine-readable fixture and support contract:

```powershell
dotnet run --configuration Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage describe --output artifacts/raster-storage/protocol-v1.json
```

Run both database layouts against an isolated PostGIS database. The adapter
creates unique `honua_rast_bench_*` schemas and drops only those exact schemas
after the run:

```powershell
$env:HONUA_RASTER_BENCHMARK_CONNECTION = '<isolated benchmark connection>'
dotnet run --configuration Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage run-postgis `
  --fixture small-raster `
  --fixture large-scene `
  --fixture aligned-mosaic `
  --fixture mixed-grid-mosaic `
  --block-size 256 `
  --warmup 2 `
  --samples 20 `
  --tenants 8 `
  --output artifacts/raster-storage/postgis-256.json
```

Repeat with block sizes `128`, `512`, and `1024`. Use `--keep-schema` only when
you need to run the external backup/restore steps, then remove the named scratch
schema after recording evidence. Never point the adapter at a database whose
resource use can affect production.

For a signed HTTP(S) COG fixture that exactly matches `small-raster` or
`large-scene`, measure the current bounded range-read tile cell:

```powershell
dotnet run --configuration Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage run-cog `
  --url '<short-lived signed object URL>' `
  --fixture large-scene `
  --warmup 2 `
  --samples 20 `
  --output artifacts/raster-storage/cog-large.json
```

The URL must honor HTTP byte ranges with `206 Partial Content`. The result omits
the signed URL and records request and byte counts. Metadata scanning is warmed
before sampling; each measured iteration reads and decodes a real tile. This
does not manufacture results for the unsupported COG cells.

Validate a result document before comparison:

```powershell
dotnet run --configuration Release --project benchmarks/Honua.Benchmarks -- `
  raster-storage validate --input artifacts/raster-storage/postgis-256.json
```

Do not commit credentials, signed URLs, or environment-specific result files.
Store durable evidence in the release/performance evidence system with the
protocol version, exact server SHA, Postgres/PostGIS versions, storage class,
region/network topology, cache state, block size, concurrency, and sampler
versions.

## Backup, restore, and vacuum evidence

The in-process adapter deliberately does not launch `pg_dump`, create a restore
database, or replicate object versions. Those are external protocol cells
because they require operator-owned destinations and credentials.

For each kept PostGIS scratch schema:

1. Capture the schema's total relation size and the database/WAL baseline.
2. Time a schema-scoped custom-format `pg_dump` into the same backup class used
   in production and record output bytes.
3. Restore into a new, isolated empty database with the same Postgres/PostGIS
   version and storage class.
4. Validate row counts, raster checksums, grid alignment, indexes, statistics,
   and the representative workload probes.
5. Record backup and restore p50/p95 over repeated runs, plus peak CPU, I/O,
   temp/scratch, WAL, and storage.

For authoritative objects, record versioning/replication bytes, request count,
integrity/checksum verification, restore or failover latency, and catalog
reconciliation time. Copying an object without restoring its catalog binding is
not a successful recovery.

Run `VACUUM (ANALYZE)` evidence after ingest and after churn representative of
the retention/migration policy. Tiled layouts can increase row/index churn even
when their windowed queries are faster.

## Interpret the result

Compare only like-for-like protocol runs. For every representation required by
the planned workload:

1. Reject unsupported and ineligible cells.
2. Reject runs with missing decision metrics.
3. Check serving p95 and concurrent-tenant p95 against their configured SLOs.
4. Check database CPU/I/O/temp/connections or object requests/bytes/cost against
   their budgets.
5. Check ingest, backup, restore, vacuum, and storage amplification.
6. Choose the simplest layout that passes every required gate.

A faster median does not compensate for a failing tail, restore window, or
database budget. An object layout that wins storage cost but lacks a required
analysis cell is archive-eligible, not analysis-ready. A tiled layout that wins
single-tenant tile latency but overwhelms autovacuum or backup is not
serving-eligible.

## Grid and readiness checks

The mixed-grid fixture proves two independent failures: a half-pixel origin
offset and a different pixel scale. A production collection must match SRID,
scale, skew, and origin lattice. Honua must not silently resample on ingest.
Create a new normalized version through the governed raster execution path, then
benchmark that target.

Treat readiness as separate claims:

- **archive-ready:** immutable version/checksum, size, footprint/grid metadata,
  and recovery evidence;
- **serving-ready:** required serving cells and overview/tile dependencies also
  pass; and
- **analysis-ready:** grid, band, NoData, statistics, and required analysis
  cells also pass.

The current catalog does not persist the complete tiled/hybrid readiness model,
so benchmark success alone does not make those production layouts available.

## Capacity warning signs

Re-run the protocol or move work out of the affected envelope when you observe:

- serving p95 or concurrent-tenant degradation above budget;
- growing database temp bytes, low cache-hit ratio, saturated I/O, pool pressure,
  or autovacuum lag;
- backup size or restore time approaching the RPO/RTO boundary;
- object range requests or egress growing faster than served tiles;
- storage amplification above the configured ceiling;
- missing overview/statistics readiness after the ingest window; or
- mixed-grid data reaching a logical mosaic query without a normalization plan.

For bursty, external-object, high-memory, or high-scratch processing, use the
isolated native GP/AWS Batch path selected by ADR-0071. Do not solve database
pressure by adding GDAL to the web container.
