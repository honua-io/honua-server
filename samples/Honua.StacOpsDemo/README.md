# Honua STAC Ops Demo

`Honua.StacOpsDemo` is a hosted Blazor WebAssembly sample that turns Honua's live STAC surface into an operator-facing review dashboard. It is a manual verification and onboarding artifact, not the automated client-compatibility proof from ticket 687.

## One-Click Local Run

```bash
bash scripts/run-stac-ops-demo.sh baseline
```

The script provisions an isolated Docker Compose stack on `http://localhost:18080`, seeds a deterministic STAC catalog, and prints the sample URL:

```text
http://localhost:18080/samples/stac-ops/
```

It uses a dedicated Compose project and ports by default so it does not need production credentials or a proprietary backing service.
The script also injects deterministic, non-production connection-encryption settings so local readiness checks pass without extra secret setup.

## Scenarios

- `baseline`: Seeds one healthy collection and one warning collection. Expect a mixed dashboard with healthy query probes plus warning signals for undeclared extension usage and missing STAC `datetime`.
- `stale-cache`: Starts from the baseline scenario, warms cached STAC metadata, then advances live item timestamps for the healthy collection without invalidating cached collection metadata. Expect the dashboard to surface discovery freshness and temporal drift warnings.

## What Reviewers Should See

- Status cards for `/healthz/live`, `/healthz/ready`, `/stac`, cache validators, discovery freshness, and overall compatibility.
- Collection cards that distinguish declared `stac_extensions` from observed namespaced properties such as `eo:*`, `proj:*`, and `view:*`.
- Query workbench probes for search, paging continuity, sort, fields, and CQL2 filter behavior.
- A request ledger with HTTP status, duration, ETag, cache headers, and probe notes for every sampled call.

## Script Overrides

- `HONUA_STAC_DEMO_PROJECT`: Docker Compose project name. Default: `honua-stac-demo`
- `HONUA_STAC_DEMO_HTTP_PORT`: Honua HTTP port. Default: `18080`
- `HONUA_STAC_DEMO_POSTGRES_PORT`: PostgreSQL host port. Default: `55432`
- `HONUA_STAC_DEMO_SCENARIO`: Default scenario when no positional argument is passed

Example:

```bash
HONUA_STAC_DEMO_HTTP_PORT=8080 \
HONUA_STAC_DEMO_POSTGRES_PORT=5432 \
bash scripts/run-stac-ops-demo.sh stale-cache
```

## Cleanup

```bash
COMPOSE_PROJECT_NAME=honua-stac-demo \
HONUA_HTTP_PORT=18080 \
POSTGRES_PORT=55432 \
docker compose down --remove-orphans --volumes
```
