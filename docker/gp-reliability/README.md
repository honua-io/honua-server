# GP lifecycle qualification harness

This opt-in harness runs the exact digest-addressed Honua serving and GDAL-worker
images against persistent Redis and PostGIS containers. Redis and PostGIS are
also pinned by digest in `compose.yml`. Each invocation gets a unique Compose
project and isolated volumes.

Run locally:

```bash
export HONUA_SERVER_IMAGE='ghcr.io/honua-io/honua-server@sha256:<64 hex characters>'
export HONUA_WORKER_IMAGE='ghcr.io/honua-io/honua-worker-gdal@sha256:<64 hex characters>'
scripts/qualification/gp-lifecycle-harness.sh
```

Set `HONUA_GP_PORT` when port 18080 is occupied. `HONUA_GP_SKIP_PULL=true` is
available for a digest-addressed image already loaded in the local Docker
daemon. Receipts are written to `artifacts/gp-lifecycle` by default. A failing
assertion is recorded as a `FINDING` and the runner exits nonzero; do not edit a
receipt or relax an assertion to make a candidate green.

The same inputs are exposed by the **GP Lifecycle Qualification** dispatchable
workflow. Its artifact upload uses `if: always()` so receipts survive a red run.
