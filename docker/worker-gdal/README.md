# honua-worker-etl — heavyweight GP/ETL worker image

This is the **optional, native-profile** worker image described by
[ADR-0038](../../docs/internal/contributor/adr/0038-geoetl-pipeline-architecture-and-runtime-boundary.md).
It is the GDAL-equipped counterpart to the lean serving image (`/Dockerfile`).

## What it is

- Built on a **GDAL base image** (`ghcr.io/osgeo/gdal:ubuntu-full-3.12.4`, pinned)
  with the .NET runtime layered on. The GDAL CLI tools (`gdalwarp`, `ogr2ogr`, …)
  and PROJ (datum-shift grids) are on `PATH`.
- Runs the **same** durable job-execution loop (`JobExecutionService` /
  `JobReconciliationService` / `RedisJobQueue`) that the serving image hosts —
  it does **not** fork a parallel runtime. The entrypoint is
  `Honua.Worker.Gdal.dll`, a headless .NET Generic Host.
- Registers **only native-profile executors** (`GdalDispatchJobExecutor`). Each
  overrides `AcceptedRuntimeProfiles` to `{ "native" }`. Native processes:
  - `gdal.gdalwarp` — raster reprojection (PROJ).
  - `gdal.ogr2ogr` — vector format conversion.
  - `transform.reproject` — **native vector reproject with full PROJ datum/grid
    shifts** (e.g. NAD 27 → WGS 84). The heavyweight counterpart to the managed
    `transform.reproject`, which serves only the in-memory fast paths (identity,
    Web Mercator aliases, WGS 84 ↔ Web Mercator) and rejects datum shifts. The
    submit path escalates a reproject job to the native profile when its SRID
    pair is not a managed fast path (`ManagedReprojectFastPath`), so the claim
    fence routes it here.
  - `source.ogr` — **GDAL/OGR-backed import reader** for the broad format
    universe the managed `source.geojson` / `source.csv` readers cannot serve
    (native FileGDB / OpenFileGDB, GML app-schema, KML, MapInfo TAB, ESRI
    Shapefile, GeoPackage, FlatGeobuf), canonicalizing to a GeoJSON
    FeatureCollection. Multi-file datasets arrive as a base64 ZIP.
  - `pcloud.translate` — LAZ/COPC decompress + reproject (PDAL).
  - `surface.*` / `raster.*` / `coverage.multidim.metadata` raster families.

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

## GP Devkit container-exec fidelity (`--real-worker`, #2180)

The GP Devkit local runner (`honua gp run`) executes the real executor in-process
for a sub-second loop. For **managed** ops that is full fidelity. For **native**
(`gdal.*`) ops the in-process path never crosses the image / driver-set / CRS-data /
arg-handling boundary a production native submit crosses (the job is packaged into
*this* image and dispatched by Batch) — a `gdal.hillshade` that passes locally
against a host's GDAL could still fail at that boundary.

`honua gp run … --real-worker` (alias `--container`) closes that cliff: it runs each
GDAL tool inside **this image** via `DockerGdalCommandRunner` instead of the host
CLIs. It is **opt-in** and auto-selected only when the image is already present
locally, so the managed loop is never blocked on a pull (`--in-process` forces the
fast path). The runner is correct-by-construction:

- Each native executor does all file I/O inside a per-job scratch workspace under
  `GdalWorker:ScratchRoot` and passes absolute workspace paths to the tool.
- `DockerGdalCommandRunner` bind-mounts that workspace at the **identical absolute
  path** (`docker run --rm --network none --user 1001:1001 -v <ws>:<ws> -w <ws>
  --entrypoint <tool> honua-worker-etl <args…>`), so the executor's absolute paths
  resolve to the same files inside the container, and outputs land back on the host
  workspace for read-back. The executor code path is byte-for-byte unchanged — only
  the `IGdalCommandRunner` implementation differs.

The `docker run` invocation (image ref, mount, working dir, user, entrypoint
override, arg ordering) is unit-tested offline against a fake container-runtime seam
(`DockerGdalCommandRunnerTests`); the **end-to-end container run** is
CI / local-Docker-verified — it needs a Docker daemon and the `honua-worker-etl`
image (`docker build -f docker/worker-gdal/Dockerfile -t honua-worker-etl .`).

Build the image and run a native op against it:

```sh
DOCKER_BUILDKIT=1 docker build -f docker/worker-gdal/Dockerfile -t honua-worker-etl .
honua gp run gdal.ogr2ogr --input in.geojson \
  --param sourceFormat=GeoJSON --param targetFormat=CSV --out out.csv --real-worker
```

`gp run`/`gp plan` build the **same** `ExecutionJobSpec` the production submit path
produces (both project through `GeoprocessingSpecBuilder`), so "plan" is a true
dry-run of the real native submit spec, not a parallel representation.
