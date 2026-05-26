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
  `gdal.ogr2ogr`, `gdal.gdalwarp`). Each overrides
  `AcceptedRuntimeProfiles` to `{ "native" }`.

## How heavy jobs route here (and never to the lean image)

ADR-0038 reserves `ExecutionJobSpec.RuntimeProfile`; the claim-fence stream
(`Honua.Core.Features.ControlPlane.Domain.RuntimeProfiles`) makes it a
**fail-closed claim filter**:

1. `IJobExecutor.AcceptedRuntimeProfiles` declares which profiles an executor
   will run. Its default is `RuntimeProfiles.DefaultAccepted` — the
   **managed/default profile only**, NOT "accept any". An executor that does not
   opt in can therefore never claim a `native` job.
2. `JobExecutionService` aggregates its executors' accepted profiles and passes
   the union to `IJobQueue.TryClaimAsync(..., acceptedRuntimeProfiles, ...)`.
3. `RedisJobQueue` honors the filter: a job is claimable iff its **effective**
   runtime profile is in the worker's accepted set, where a job whose
   `RuntimeProfile` is `null`/empty is normalized to the managed/default profile
   (`RuntimeProfiles.Normalize`). A `native` job is claimable only by a worker
   whose accepted set contains `"native"`.

Result:

- The **lean serving image** registers managed-profile executors that all fall
  back to the managed-only default → it claims managed/default jobs but
  **never** claims a `RuntimeProfile = "native"` job.
- This **worker image** declares `{ "native" }` → it claims **only**
  native-profile jobs and never claims managed jobs.

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
