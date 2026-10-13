---
type: guide
title: "Local geoprocessing dev quickstart"
description: "Stand up a local Honua server that runs geoprocessing (GP) jobs in-process, submit a real GP job, fetch results, and iterate on your own GP processes — with"
---
# Local geoprocessing dev quickstart

Stand up a local Honua server that runs geoprocessing (GP) jobs **in-process**,
submit a real GP job, fetch results, and iterate on your own GP processes — with
**no AWS / Azure Batch, no cloud credentials, and no manual license step**.

This is the dev-environment and iterate-loop companion to the
[Run geoprocessing](run-geoprocessing.md) task guide (which covers the request
shapes in depth) and the
[Geoprocessing operations reference](../../reference/geoprocessing-operations.md)
(the full operation catalog). This page is about the local setup and the
edit → rebuild → re-run loop.

## How local GP runs (so the setup makes sense)

This Compose stack uses persistent workers and no Batch service. The lean serving
image coordinates the job lifecycle:

- The serving host registers **both** the durable job-orchestration substrate
  and the **in-process worker loop** (`AddJobOrchestration` + `AddJobWorker` in
  `src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs`).
- Managed processes — the `geometry.*` family, the `*-managed` analytics
  counterparts, and the GeoETL transforms — execute **in-process** inside the
  server via `GeoprocessingDispatchJobExecutor`. The
  `LocalBatchComputeBackend` (`Backend: "local"`,
  `src/Honua.Jobs/Features/ControlPlane/LocalBatchComputeBackend.cs`) bridges
  their progress back to the canonical reconciler. GP jobs default to this
  backend when no remote workload is configured.
- The job queue, execution-job store, log store, and result-package store are
  all **Redis-backed**, and the worker loop self-gates on a Redis connection.
  **So Redis is required** — asynchronous submissions are refused with a typed
  capability-unavailable response when the durable substrate is unavailable.
- Redis wiring (`IConnectionMultiplexer`) is only enabled when the Pro
  `caching.redis` entitlement is present at bootstrap. In a non-Production
  environment the bootstrap snapshot honors `Licensing:DevGrantEdition`, so
  `Licensing__DevGrantEdition=Pro` unlocks it **without a signed license**. The
  compose file sets this for you.

The heavyweight **GDAL/PDAL worker** (`docker/worker-gdal/Dockerfile`)
is a separate, optional container only needed for the native-profile `gdal.*`
raster/surface family. The `geometry.buffer` sample below does not need it.

## Prerequisites

- Docker with Compose v2 (`docker compose`).
- Python 3.11+ with the published SDK: `python -m pip install honua-sdk`.
- .NET 10 SDK for the authoring loop; no cloud account or license file.

## 1. Bring up the stack

```bash
docker compose -f docker-compose.gp-dev.yml up
```

This builds the server image and starts three containers: `postgres` (required
at boot for migrations), `redis` (the GP job substrate), and `honua` (the
server, running the in-process GP worker loop). Wait for readiness:

Open `http://localhost:8080/healthz/ready` in a browser and wait for `Ready`.

To also run the optional GDAL worker for `gdal.*` jobs:

```bash
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker up
```

## 2. Submit a GP job, watch it, get results

A ready-to-run sample lives in [`samples/gp-local-dev/`](../../../samples/gp-local-dev/):

```bash
./samples/gp-local-dev/submit-buffer.sh
```

It buffers `POINT(0 0)` in EPSG:3857 by 500 metres using the installed Python
SDK, waits for completion, and validates the returned polygon's bounds and area.
The expected bounds are `[-500, -500, 500, 500]` in metres. Planar distances use
the input CRS units; EPSG:4326 coordinates would interpret `500` as degrees.

The equivalent SDK lifecycle is:

```python
from honua_sdk import HonuaClient

with HonuaClient("http://localhost:8080", api_key="quickstart-admin-password") as client:
    gp = client.geoprocessing()
    job = gp.submit_inputs("geometry.buffer", {
        "wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
        "srid": 3857,
        "distance": 500,
        "geodesic": False,
    })
    terminal = gp.wait(job, timeout=120)
    if not terminal.succeeded:
        raise RuntimeError(terminal.message)
    print(gp.results(job.job_id))
```

The same catalog is exposed Esri-style at
`/rest/services/{serviceId}/GPServer` for ArcGIS clients, and the deterministic
single-geometry tasks also accept a synchronous `execute` route — see
[Run geoprocessing](run-geoprocessing.md) for those shapes.

## 3. Prototype your own GP process (the dev loop)

GP processes are catalog entries backed by job executors. To add or change one:

1. **Declare the process in the catalog.** Add a `ProcessDefinition` (id,
   title, parameters, and a `RuntimeProfile` — `managed` for in-process NTS,
   `native` for the GDAL worker) in
   `src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs`.

2. **Implement and register an executor.** Add an `IJobExecutor`-style executor
   next to the existing ones under
   `src/Honua.Geoprocessing/Features/Geoprocessing/Execution/` (managed) or
   `src/Honua.Worker.Gdal/Execution/` (native), and register it in the matching
   `*ServiceCollectionExtensions`. Managed executors are composed behind
   `GeoprocessingDispatchJobExecutor` and run in-process on the local backend;
   native executors are dispatched by `GdalDispatchJobExecutor` in the GDAL
   worker.

3. **Rebuild and re-run.**
   - **Managed (`geometry.*` / analytics / GeoETL):** the executor lives in the
     server image, so rebuild and restart `honua`:
     ```bash
     docker compose -f docker-compose.gp-dev.yml up --build honua
     ```
   - **Native (`gdal.*`):** rebuild the worker image and restart it. When adding
     or changing a catalog definition, rebuild `honua` as well so discovery and
     validation use the updated process contract:
     ```bash
     docker compose -f docker-compose.gp-dev.yml --profile gdal-worker up --build honua gdal-worker
     ```

4. **Submit against your new process id** (swap `geometry.buffer` for your id in
   the SDK submission above). The local backend picks it up automatically: managed
   ids drain through the in-process loop; native ids are claimed by the GDAL
   worker because they declare `RuntimeProfile: native`.

For a tight inner loop without rebuilding the image, run the server directly
against the compose Redis/Postgres instead:

```bash
docker compose -f docker-compose.gp-dev.yml up -d postgres redis
ASPNETCORE_ENVIRONMENT=Development \
  Licensing__DevGrantEdition=Pro \
  ConnectionStrings__redis=localhost:6379 \
  ConnectionStrings__DefaultConnection="Host=localhost;Database=honua_dev;Username=honua_user;Password=honua_password" \
  HONUA_ADMIN_PASSWORD=quickstart-admin-password \
  dotnet run --project src/Honua.Server
```

## Troubleshoot

- **Job stuck in `accepted`** — Redis is down or not wired. Confirm the `redis`
  container is healthy and `ConnectionStrings__redis` resolves; the GP worker
  loop only starts when the Redis connection is registered.
- **401 on submit** — discovery is anonymous but execution is not; send
  `X-API-Key: quickstart-admin-password`.
- **Server won't start / GP never runs even with Redis up** — make sure
  `ASPNETCORE_ENVIRONMENT=Development` and `Licensing__DevGrantEdition=Pro` are
  set so the bootstrap snapshot grants `caching.redis` and wires
  `IConnectionMultiplexer`.
- **404 for a process id in the full catalog** — only first-slice vector
  processes project individually through OGC API Processes; others run via the
  canonical `honua-geoprocessing` plan process. See the
  [reference](../../reference/geoprocessing-operations.md).

## Next steps

- [Run geoprocessing](run-geoprocessing.md) — full request/response shapes.
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md).
- [Automate workflows](automate-workflows.md).
