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

## Integration profile

The base file starts the host, PostGIS, and Redis. The integration profile adds
the managed worker, the native heavy worker, and local artifact storage:

```bash
docker compose -f docker-compose.gp-dev.yml --profile integration up --build -d
```

`managed-worker` is a second serving-image process with no published ports. It
joins the same Redis substrate and claims managed jobs. `gdal-worker` is the
GDAL/PDAL heavy worker from `docker/worker-gdal`. Both mount the named volume
`gp_artifacts` at `/var/lib/honua/gp-artifacts` and bind-mount local process
source read-only at `/workspace/process-source` (the checkout, or
`HONUA_GP_PROCESS_SOURCE`). That path is for inspection. Managed executors are
the compiled registrations in the serving image, and native helper scripts load
from the image. A source edit reaches a worker only after that image is rebuilt
and the container is recreated. Section 4 has the commands.

`honua-gp-devkit doctor` reports the prerequisite for this profile: Docker
Compose v2, with host ports 5432, 6379, 8080, and 8081 free. It also reports
whether `docker`, `kind`, `kubectl`, and `helm` are on PATH, each as `found` or
`missing` with one remediation line. A missing tool does not fail doctor.
Doctor still exits 0 only when the managed probe succeeds and, unless
`--managed-only`, the native worker image is available.

This profile does not exercise a kind lifecycle matrix, debugger attachment, or
a cloud account. The sections below document a trusted-local debugger attach
and the kind case list. Neither runs as part of this profile, and the live kind
matrix is not executed by this change. Referenced output staging stays off.
Stop the profile with:

```bash
docker compose -f docker-compose.gp-dev.yml --profile integration down
```

## Attach a debugger on the native worker

This recipe is for a developer machine. It is not a production custom-code
target, and it is not the default. The integration profile's `gdal-worker`
keeps the image entrypoint, publishes no debugger port, and leaves
`DOTNET_EnableDiagnostics=0`. Nothing below is written into
`docker-compose.gp-dev.yml` or into `honua-worker-etl`. debugpy and vsdbg stay
off the image. Do not point the listen port at a shared or production host.
Publish it only on `127.0.0.1`.

The one-shot container uses the profile's existing read-only source mount at
`/workspace/process-source` (the checkout, or `HONUA_GP_PROCESS_SOURCE`). A
source edit is visible on that mount. Workers still run the image's compiled
executors, and native helper scripts still load from the image, until that
image is rebuilt and the container is recreated. Attaching a debugger does not
switch execution onto the mount. The published image has no debugger symbols,
so a breakpoint in the mounted sources binds only after a symbolized rebuild.

Confirm the toolchain, then build the native image if doctor reports it
unavailable:

```bash
honua-gp-devkit doctor
docker compose -f docker-compose.gp-dev.yml --profile integration build gdal-worker
```

### Python (debugpy)

The native image has Python 3 for its GDAL helper scripts and does not include
debugpy. This command installs the pinned debugpy into a throwaway directory
inside one container, then waits. The host publish is loopback only; inside the
container debugpy must listen on all interfaces or Compose cannot forward the
port. Point the debugger at `127.0.0.1` port 5678 and map the checkout to
`/workspace/process-source`. The file launched here is the mounted script. Jobs
still execute the copy under `/app/Scripts` in the image. The command passes
the checked-in proximity fixture as `src` and writes `dst` under `/tmp`, which
is writable inside the container. The script exits 2 when those two paths are
omitted.

```bash
docker compose -f docker-compose.gp-dev.yml --profile integration run --rm --no-deps \
  --entrypoint /bin/sh \
  --publish 127.0.0.1:5678:5678 \
  -e HOME=/tmp \
  -e PYTHONUSERBASE=/tmp/debugpy-site \
  gdal-worker -c "python3 -m pip install --user --quiet --disable-pip-version-check debugpy==1.8.14 && python3 -m debugpy --listen 0.0.0.0:5678 --wait-for-client /workspace/process-source/src/Honua.Worker.Gdal/Scripts/gdal_euclidean_allocation.py /workspace/process-source/tests/dotnet/Honua.Worker.Gdal.Tests/Fixtures/Proximity/proximity-sources.tif /tmp/euclidean-allocation.tif"
```

Ctrl-C stops it, and `--rm` removes it. This change does not include a Linux
debugger smoke. If `python3 -m pip` is absent, the command fails; do not add
debugpy to the image to get past that.

### .NET (vsdbg)

Install Microsoft's vsdbg with the official getvsdbg script into
`$HOME/.honua/vsdbg` on the developer machine first. Mount that directory
read-only. It is a local prerequisite, not part of the image.

Stop the profile's native worker before the one-shot container so two workers
do not claim the same Redis jobs. The idle container sets
`DOTNET_EnableDiagnostics=1` only for itself.

```bash
docker compose -f docker-compose.gp-dev.yml --profile integration stop gdal-worker
docker rm -f honua-gp-vsdbg >/dev/null 2>&1 || true
docker compose -f docker-compose.gp-dev.yml --profile integration run --no-deps -d \
  --name honua-gp-vsdbg \
  --entrypoint sleep \
  -e DOTNET_EnableDiagnostics=1 \
  --volume "${HOME}/.honua/vsdbg:/vsdbg:ro" \
  gdal-worker infinity
```

`compose run` starts a new container. It is not the `gdal-worker` service, so
`compose exec gdal-worker` does not reach it. The editor pipe program is
`docker`. Its arguments are `exec -i honua-gp-vsdbg`, and the debugger path is
`/vsdbg/vsdbg`. Map `/workspace/process-source` to the checkout. The process is
`dotnet` with argument `/app/Honua.Worker.Gdal.dll` (the image's compiled
worker).

```bash
docker exec -i honua-gp-vsdbg /vsdbg/vsdbg --interpreter=vscode
```

When finished, remove that named container and start the normal worker again.
The normal worker does not enable diagnostics.

```bash
docker rm -f honua-gp-vsdbg
docker compose -f docker-compose.gp-dev.yml --profile integration up -d gdal-worker
```

## Kind lifecycle cases

Doctor reports `docker`, `kind`, `kubectl`, and `helm` as found or missing.
The case list is
[gp-kind-lifecycle-matrix.py](../../../scripts/ci/gp-kind-lifecycle-matrix.py).
It names six cases: success, failure, cancellation, timeout, worker loss, and
artifact-upload failure. The default command does not call kind, create a
cluster, or pull images. If one of those four tools is missing, the script
prints a remediation line and exits non-zero. `--execute` is refused: live
execution is not implemented in this slice. This change does not run the live
kind matrix and does not add a required CI job.

```bash
python3 scripts/ci/gp-kind-lifecycle-matrix.py
```

## 4. Edit, rebuild, and submit again

Use `new`, `run`, and `test` in the [authoring guide](gp-devkit-authoring.md)
to implement a process and its fixture. Then rebuild the integration stack:

```bash
# Managed executor or catalog changes.
docker compose -f docker-compose.gp-dev.yml up --build -d honua

# Recreate the integration managed worker from that image when the profile is running.
docker compose -f docker-compose.gp-dev.yml --profile integration up --build -d managed-worker

# Native executor changes; rebuild the server too when its catalog changes.
docker compose -f docker-compose.gp-dev.yml --profile gdal-worker up --build -d honua gdal-worker
```

Describe the deployed process before submitting it. Local devkit registration
and exposure as an individual OGC process are separate: some catalog entries
execute through the canonical `honua-geoprocessing` plan process. See
[run geoprocessing](run-geoprocessing.md) and the [operation reference](../../reference/geoprocessing-operations.md).

For AI clients, connect to this server's `/mcp` endpoint and follow
[geoprocessing with AI](geoprocessing-with-ai.md). For GeoServices REST clients, the
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
