# JavaScript Client Compatibility Tests

Honua currently ships two JavaScript/TypeScript test suites:

| Suite | Path | Runner | Purpose |
|---|---|---|---|
| Protocol integration | `tests/js/` | Vitest | Headless FeatureServer protocol coverage, query matrix expansion, edit flows, and metadata |
| Browser compatibility | `tests/js-browser/` | Playwright | Real-client certification against Esri Leaflet 3.x plus MapLibre GL JS browser compatibility |

## Shared Server Bootstrap

Both suites can run against an existing Honua instance or bootstrap their own local stack.

If `HONUA_BASE_URL` points to a healthy server (`/healthz/live` returns `200 OK`), the suite uses it directly. Otherwise the JavaScript test harness starts `tests/python/shared/js_test_server.py`, which:

- Starts PostGIS (Docker required)
- Seeds deterministic test data
- Launches Honua
- Exports runtime `service_id` and `layer_id`

The bootstrap uses `.venv-tests/bin/python` when available, otherwise `python3`.

Common environment variables (defaults differ per suite):

| Variable | Vitest default | Playwright default | Description |
|---|---|---|---|
| `HONUA_BASE_URL` | `http://localhost:5555` | `http://localhost:5556` | Honua server URL |
| `HONUA_SERVICE_ID` | `test_service_gw0` | `test_service_gw0` | Test service name |
| `HONUA_LAYER_ID` | `1000` | `1000` | Test layer ID |
| `HONUA_TEST_PORT` | `5555` | `5556` | Port for bootstrapped server |
| `HONUA_TEST_TIMEOUT` | `30000` | N/A | Request timeout (ms); Playwright uses its own config-level timeouts |

The Playwright suite serves its own static test page and proxies `/rest/` and `/temp/` requests to `HONUA_BASE_URL`. It does not inject browser auth headers, so the configured service/layer must be anonymously accessible or exposed through the local test bootstrap.

## Vitest Protocol Suite (`tests/js/`)

This suite focuses on direct FeatureServer protocol compatibility.

Coverage highlights:

- Spatial relationships including `esriSpatialRelContains`, distance predicates, and unit conversions
- Geometry-type round trips for points, lines, polygons, multiparts, and null geometry handling
- WHERE-clause operators including comparison, `LIKE`, `IN`, `BETWEEN`, null checks, and nested logical expressions
- Query output formats: `json` and `geojson`
- Spatial-reference flows for EPSG:4326 and EPSG:3857

Run it with:

```bash
cd tests/js
npm ci
npm test
```

Useful commands:

```bash
npm run test:query
npm run test:matrix
npm run test:geometry
npm run test:edits
npm run test:metadata
npm run test:watch
npm run test:coverage
```

Approximate suite size: `~330+` tests across query, geometry, edits, and metadata coverage.

## Playwright Browser Suite (`tests/js-browser/`)

This workspace contains three Playwright lanes:

- `esri-leaflet/` proves that a real browser client can consume Honua through the `leaflet`, `esri-leaflet`, and `esri-leaflet-renderers` packages. It is the merge-blocking Esri Leaflet compatibility lane in `ci.yml` (`esri-leaflet-browser-tests` job).
- `maplibre/` proves that MapLibre GL JS can load styles, discover TileJSON, fetch MVT tiles, and render vector tiles from Honua. It runs in `ci.yml` as the `maplibre-compat` job.
- `cesium/` proves that CesiumJS imagery providers (`WebMapServiceImageryProvider`, `WebMapTileServiceImageryProvider`, `UrlTemplateImageryProvider` for OGC API Tiles, `SingleTileImageryProvider` for OGC API Maps) and the `Cesium3DTileset` scene primitive (hosted OGC 3D Tiles via `/scenes/{sceneId}/`) can load against Honua. PR CI runs only the 3D Tiles smoke (`cesium-3d-tiles-smoke` job in `ci.yml`, scoped to the `--grep "3D Tiles"` subset) — it bootstraps a Honua server with a `SceneDataset` bound to the committed fixture under `tests/fixtures/scenes/fixture-tileset` and asserts `tileset.json` content-type and CORS, the first nested tile asset's content-type and matching CORS, and that `Cesium3DTileset.fromUrl` reaches `tilesLoaded`, fetches at least one binary tile body (`.b3dm`/`.i3dm`/`.pnts`/`.cmpt`/`.glb`), and reports no `/scenes/**` 4xx/5xx responses, no Playwright network failures, and no Cesium `tileFailed` events. The full Cesium suite (imagery providers + 3D Tiles) runs nightly under the `cesium` lane of `client-interop-nightly.yml` via `docker/client-compat/cesium/`. The local command and required `Scenes:Datasets` / CORS configuration are documented in [`tests/js-browser/cesium/README.md`](../../tests/js-browser/cesium/README.md). Cesium does not consume vector-feature endpoints, so the lane records the query/schema/pagination/geometry-fidelity CERT-\* IDs as `not-applicable` and seeds the visual / style slice IDs as `not-applicable` (Cesium consumes server-rendered raster output and binary tile payloads rather than per-feature drawing info).

Current browser coverage:

- FeatureServer load, metadata discovery, schema inspection, query/filter, paging, malformed-filter handling
- Geometry fidelity and `outSR=4326` request handling from an Esri Leaflet query object
- Feature attribute access through `eachFeature`
- MapServer `DynamicMapLayer` export rendering, metadata discovery, identify, and redraw/state preservation
- Visual regression checks with Playwright screenshots plus a non-blank render guard
- `.cert.json` evidence emission for `featureserver` and `mapserver`

Run it with:

```bash
cd tests/js-browser
npm ci
npx playwright install --with-deps chromium
npm test
```

Useful commands:

```bash
npm run test:update-snapshots
npm run test:headed
npm run test:debug
npm run test:maplibre
npm run lint
```

Outputs:

- `tests/js-browser/test-results/` — Playwright traces and failure artifacts
- `tests/js-browser/evidence/` — generated `*-js-featureserver.cert.json` and `*-js-mapserver.cert.json` envelopes
- `tests/js-browser/esri-leaflet/rendering.spec.ts-snapshots/` — committed screenshot baselines

The reporter resolves `client_version` from the installed `esri-leaflet` package version, not from the semver range alone. Unexercised CERT-\* IDs are emitted as `skip`, and MapServer query-only IDs are emitted as `not-applicable`, so every evidence file keeps a full 24-case common-core shape (18 base CERT-\* IDs plus the six `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` visual / style slice IDs introduced by ticket [`#478`](https://github.com/honua-io/honua-server/issues/478); see [`visual-style-certification-slice.md`](../gis/visual-style-certification-slice.md)). The browser suite substantiates `CERT-RNDR-SYM-01` (drawingInfo point symbol) and `CERT-RNDR-URL-01` (FeatureServer drawingInfo metadata document consumed) on the FeatureServer surface; on `mapserver` evidence files all six slice IDs are recorded as `not-applicable` because drawingInfo per-category style assertions live on FeatureServer, not the MapServer export endpoint. Slice IDs that are applicable to a protocol but not yet substantiated by this lane (`CERT-RNDR-{LIN,FIL,LBL,SPR}-01` on FeatureServer for Esri Leaflet) emit `skip` with a `pending-fixture:` note prefix rather than the suite's generic "Not exercised" message, so the [release-checklist promise](RELEASE_CHECKLIST.md#cross-client-certification-required) (`pending-fixture` is a documented skip reason) is verifiable in the envelope text. The matching contract for the OpenLayers lane is implemented in [`tests/js/openlayers/shared/evidence.ts`](../../tests/js/openlayers/shared/evidence.ts).
