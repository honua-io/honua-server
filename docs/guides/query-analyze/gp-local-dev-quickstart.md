---
type: guide
title: "Local geoprocessing dev quickstart"
description: "Run the local GP stack, verify a metric buffer through the Python SDK, and connect native workers and AI clients."
---
# Local geoprocessing dev quickstart

Run a GP job through a local server, inspect its result, and repeat after editing
your process. This stack needs no cloud account. For direct function development
without a server or Redis, start with [the authoring guide](gp-devkit-authoring.md).

For Python or .NET custom-code breakpoints, start with the
[filesystem sample](../../../samples/gp-custom-local/README.md), then return here
to verify server authorization, queues, worker dispatch, and result retrieval.
A local function run does not exercise those integration boundaries.

## Prerequisites

Run from the repository root with Docker Compose v2 and Python 3.11 or newer.
Use a virtual environment for the published Python SDK:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install honua-sdk
```

The .NET 10 SDK is also needed when running the devkit from source.

## 1. Start the server

```bash
docker compose -f docker-compose.gp-dev.yml up --build -d
docker compose -f docker-compose.gp-dev.yml ps
```

Open `http://localhost:8080/healthz/ready` and wait for `Ready`. The stack starts
PostGIS, Redis, and the serving host. Managed GP runs in the serving host;
Redis holds durable jobs and results. Compose enables a Development Pro grant
so local Redis wiring works without a signed license file.

## 2. Submit and verify a buffer

```bash
python samples/gp-local-dev/submit-buffer.py
```

The sample discovers `geometry.buffer`, submits it through the installed SDK,
waits up to 120 seconds, and checks the result's bounds and area. It buffers
`POINT(0 0)` in EPSG:3857 by 500 metres. Expect status `successful`, bounds
`[-500, -500, 500, 500]`, and area within 1% of `π × 500²` square metres.
The sample prints the job ID and complete result for inspection.

To target a different local port or identity, set `BASE` and `KEY`. The defaults
are `http://localhost:8080` and Compose's `quickstart-admin-password`.
The shell wrapper `./samples/gp-local-dev/submit-buffer.sh` runs the same sample
and accepts a `PYTHON` interpreter override.

The SDK lifecycle is:

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

Planar distances use the input CRS units. EPSG:4326 would interpret `500` as
degrees. Keep the metric CRS with this result; reproject before WGS 84 map import.

## 3. Add native execution

```bash
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker up --build -d
```

This adds the GDAL/PDAL heavy worker. It claims native-profile jobs from the same
Redis substrate; managed jobs remain in the serving host. Native functions
include vector conversion, raster/surface processing, and point-cloud operations.
The heavy worker uses JIT and supports reflection; it needs no AOT build.

Compose names the image `honua-worker-etl:gp-local` by default. Set
`HONUA_GP_WORKER_IMAGE` to select another local tag. You can use the same image
with the devkit's `--real-worker` option; the [native Docker example](gp-devkit-authoring.md#run-native-functions-in-docker)
converts the checked-in CSV and verifies what to expect in its GeoJSON output.

## 4. Edit, rebuild, and submit again

Use `new`, `run`, and `test` in the [authoring guide](gp-devkit-authoring.md)
to implement a process and its fixture. Then rebuild the integration stack:

```bash
# Managed executor or catalog changes.
docker compose -f docker-compose.gp-dev.yml up --build -d honua

# Native executor changes; rebuild the server too when its catalog changes.
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker up --build -d honua gdal-worker
```

Describe the deployed process before submitting it. Local devkit registration
and exposure as an individual OGC process are separate: some catalog entries
execute through the canonical `honua-geoprocessing` plan process. See
[run geoprocessing](run-geoprocessing.md) and the [operation reference](../../reference/geoprocessing-operations.md).

For AI clients, connect to this server's `/mcp` endpoint and follow
[geoprocessing with AI](geoprocessing-with-ai.md). For ArcGIS clients, the
[GPServer adapter](run-geoprocessing.md) exposes service-scoped tasks over the
same runtime. The [sample README](../../../samples/gp-local-dev/README.md)
links the SDK, native, and MCP examples.

## Stop or diagnose the stack

```bash
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker logs --tail=100 honua gdal-worker redis
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker down
```

`down` preserves the local data volumes. Add `--volumes` only when you intend to
reset this stack's database and jobs.

| Symptom | Check |
| --- | --- |
| `401` on submission | The SDK's API key matches `HONUA_ADMIN_PASSWORD`. |
| `403` | The identity has `Process.Execute` and any additional grants required by the selected process. |
| Async submission refused or job stays queued | Redis is healthy and the matching managed/native worker is running. |
| Native command unavailable | The heavy image was built and the chosen tag exists locally. |
| Unknown process | Describe it on the running server; rebuild the catalog or use the plan process where required. |
| A submitted job failed or stalled | Read `GET /api/v1/admin/jobs/{jobId}/diagnostics`; see [Diagnose a remote geoprocessing job](gp-job-diagnostics.md). |

After local verification, promote images through the release process described
in [From local to environments](gp-devkit-authoring.md#from-local-to-environments).
