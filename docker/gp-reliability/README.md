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

Referenced output staging is fail-closed, so the harness provisions the shared
object volume from the topology's own declared store contract (via
`scripts/operations/initialize-gp-output-store.sh`) before the first `compose up`,
and refuses to start if the digest it computes differs from the one `compose.yml`
declares. The `output-store-attestation` scenario then proves an existing but
unattested mount is rejected without being self-provisioned, that the runtime
publishes path-free store evidence, and that a staged artifact read back from the
peer server is byte-identical after every server and worker container is replaced.

The lifecycle lane also qualifies the production worker's supported cancellation and timeout
seams. It pauses a real native GDAL child at claimed, process-started, staged-output, and
terminal-CAS fences; cancels through OGC DELETE from the peer server; and exercises both a
cooperative and deliberately cancellation-ignoring native executor under the submitted
`batch.timeout_seconds` workload policy. The qualification-only barrier and object roots are
shared between the server and worker containers and default to isolated directories below the
receipt root.

The same inputs are exposed by the **GP Lifecycle Qualification** dispatchable
workflow. Its artifact upload uses `if: always()` so receipts survive a red run.

For candidate store qualification, run both `HONUA_GP_LANE=output-store-dr` and
`HONUA_GP_LANE=crash-boundaries` with separate `HONUA_GP_RECEIPT_DIR` directories.
Use `HONUA_GP_SCENARIO_TIMEOUT_SECONDS=900` to allow natural stale-worker lease
recovery after SIGKILL. The DR lane **destroys its isolated original Postgres,
Redis and output stores**, restores the cold backups into empty stores, and
checks the artifact and descriptor through the peer server. Keep its backup
directory outside the output volume. Never point this drill at a customer store.

The **GP candidate store recovery** workflow reads the current honua-release
manifest every six hours and on dispatch (`gp-candidate-repinned` is also accepted
as a repository dispatch event). It signs only complete passing restore and
crash-boundary receipts from trunk. The server runs by the manifest's digest;
the production worker is built locally from the manifest's matching source SHA,
and its own image digest is recorded. A repinned server cannot consume the old
receipt: `scripts/qualification/gp-candidate-binding.py` compares every observed
host and required scenario against the manifest supplied to the verifier.

The resilience mode adds poison entries, worker/output-store disruption, stale
claims, backlog drain, TTL cleanup, retry exhaustion, output-size enforcement,
tenant concurrency/backpressure and nondisclosure, plus an optional sustained
soak. Tenant checks require two independently issued bearer tokens; an admin key
is intentionally not accepted as isolation evidence. Enable the soak with
`HONUA_GP_RUN_SOAK=true` and tune `HONUA_GP_SOAK_SECONDS` and
`HONUA_GP_SOAK_CONCURRENCY`.

`GP geometry.buffer Canary` runs every six hours. Each run retains both a
`honua.gp-buffer-canary.v1` output receipt and a
`honua.gp-buffer-canary-streak.v1` seven-run history receipt for burn-in readers.
Configure repository variable `GP_CANARY_URL` and secret `GP_CANARY_TOKEN`.
