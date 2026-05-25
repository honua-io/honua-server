# honua-worker-etl — heavyweight GP/ETL worker image

This is the **optional, native-profile** worker image described by
[ADR-0038](../../docs/contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md).
It is the GDAL-equipped counterpart to the lean serving image (`/Dockerfile`).

## What it is

- Built on a **GDAL base image** (`ghcr.io/osgeo/gdal`) with the .NET runtime
  layered on. The GDAL CLI tools (`gdalwarp`, `ogr2ogr`, …) are on `PATH`.
- Runs the **same** durable job-execution loop (`JobExecutionService` /
  `JobReconciliationService` / `RedisJobQueue`) that the serving image hosts —
  it does **not** fork a parallel runtime. The entrypoint is
  `Honua.Worker.Gdal.dll`, a headless .NET Generic Host.
- Registers **only native-profile executors** (`GdalDispatchJobExecutor` →
  `gdal.ogr2ogr`, `gdal.gdalwarp`). Each declares
  `AcceptedRuntimeProfiles = { "native" }`.

## How heavy jobs route here (and never to the lean image)

ADR-0038 reserves `ExecutionJobSpec.RuntimeProfile`. This slice makes it a
**claim filter**:

1. `IJobExecutor.AcceptedRuntimeProfiles` (default `null`) declares which
   profiles an executor will run.
2. `JobExecutionService` aggregates its executors' accepted profiles and passes
   them to `IJobQueue.TryClaimAsync(..., acceptedRuntimeProfiles, ...)`.
3. `RedisJobQueue` honors the filter: a job is claimable iff its
   `RuntimeProfile` is `null` (profile-agnostic) **or** is in the worker's
   accepted set.

Result:

- The **lean serving image** registers managed-profile executors with
  `AcceptedRuntimeProfiles == null` → it claims profile-agnostic jobs but
  **never** claims a `RuntimeProfile = "native"` job.
- This **worker image** declares `{ "native" }` → it claims **only**
  native-profile jobs.

The substrate's atomic claim still guarantees exactly one worker runs each job;
the filter ensures the *right* worker is the only candidate.

## Build

```sh
DOCKER_BUILDKIT=1 docker build -f docker/worker-gdal/Dockerfile -t honua-worker-etl .
```

Run alongside the serving image, pointed at the same Redis + PostgreSQL:

```sh
docker run --rm \
  -e ConnectionStrings__redis="redis-host:6379" \
  honua-worker-etl
```

## Configuration (`GdalWorker` section)

| Key | Default | Meaning |
|---|---|---|
| `GdalWorker:MaxArtifactBytes` | 52428800 (50 MiB) | Per-payload ceiling for source + produced artifact. |
| `GdalWorker:ScratchRoot` | `/tmp/honua-gdal-worker` | Per-job isolated scratch workspace root. |
| `GdalWorker:ToolTimeout` | `00:15:00` | Wall-clock limit per GDAL CLI invocation. |

## GDAL access approach

The worker shells out to the **GDAL CLI** (`ogr2ogr`, `gdalwarp`) via
`IGdalCommandRunner` / `ProcessGdalCommandRunner`, rather than bundling managed
GDAL bindings. This keeps **zero** GDAL/native package references in any managed
assembly (including `Honua.Server`), so the lean serving image's
package-graph and cold-start budget are completely unaffected. The CLI binaries
come from the base image layer. The `IGdalCommandRunner` seam also lets the
executor logic be unit-tested without GDAL installed.
