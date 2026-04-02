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

## Hosted Behavior

- The sample is hosted at `/samples/stac-ops/`. Requests to `/samples/stac-ops` may redirect to `/samples/stac-ops/index.html`; use the trailing-slash URL for manual review.
- Honua serves the demo by default in `Development` and `Test`. In other environments, enable it with `HONUA_SERVE_STAC_DEMO=true`.
- Docker production builds trim the sample assets by default. To keep `/samples/stac-ops/` in a custom image, build with `--build-arg HONUA_INCLUDE_STAC_OPS_DEMO=true` and still set `HONUA_SERVE_STAC_DEMO=true` at runtime.
- The dashboard probes the same origin by default. You can override the source to another base URL, but browser access still depends on that target allowing the required CORS requests.

## Scenarios

- `baseline`: Seeds one healthy collection and one warning collection. Expect a mixed dashboard with healthy query probes plus warning signals for undeclared extension usage and missing STAC `datetime`.
- `stale-cache`: Starts from the baseline scenario, warms cached STAC metadata, then advances live item timestamps for the healthy collection without invalidating cached collection metadata. Expect the dashboard to surface discovery freshness and temporal drift warnings.

## What Reviewers Should See

- Status cards for `/healthz/live`, `/healthz/ready`, `/stac`, cache validators, discovery freshness, and overall compatibility.
- Collection cards that distinguish cached `/stac/collections` freshness evidence from live collection-detail validators while still separating declared `stac_extensions` from observed namespaced properties such as `eo:*`, `proj:*`, and `view:*`.
- Query workbench probes for search, paging continuity, sort, fields, and CQL2 filter behavior.
- A request ledger with HTTP status, duration, ETag, cache headers, and probe notes for every sampled call.

## STAC Contract The Demo Expects

- `GET /stac` advertises STAC core, collections, item-search, the OGC API Features bridge, plus fields, sort, and filter conformance, and links to both `search` and `data`.
- `GET /stac`, `GET /stac/collections`, and `GET /stac/collections/{collectionId}` emit strong `ETag` headers and support `If-None-Match`.
- Collection detail always includes `license` and defaults it to `proprietary` when the layer does not declare a STAC-specific value. It may also include `keywords` and `stac_extensions`, plus an `alternate` link to `/ogc/features/collections/{collectionId}`.
- Collection items and item-search results always include `properties.datetime`. If Honua cannot resolve a time field, the property remains present with a `null` value.
- Pagination links preserve encoded `bbox` and `datetime` filters so freshness and continuity checks replay the exact sampled query.
- The workbench probes `GET /stac/search` with `sortby`, `fields`, and `filter` plus `filter-lang=cql2-text`, so those paths should be enabled in the review stack.

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
