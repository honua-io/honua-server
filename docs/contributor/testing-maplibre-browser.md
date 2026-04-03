# MapLibre GL JS Browser Compatibility Tests

Playwright + Chromium end-to-end suite that proves MapLibre GL JS can load styles, discover TileJSON, fetch MVT tiles, and render features from Honua Server.

## What It Proves

1. **Style JSON** — `/api/styles/{layerId}.json` returns a valid MapLibre v8 document.
2. **TileJSON discovery** — `/tiles/{layerId}/tile.json` returns tile URLs and vector layer metadata.
3. **Vector tile fetch** — MVT tiles return `200` with `application/vnd.mapbox-vector-tile` or `application/x-protobuf` content-type.
4. **Canvas render** — MapLibre initializes, reaches idle, and renders non-blank pixels for point, line, and polygon layers.
5. **Interactive query** — `queryRenderedFeatures` returns seeded features at known coordinates.

## CERT Mapping

| CERT ID | Spec file |
|---|---|
| CERT-CONN-01 | `style-loading.spec.ts` |
| CERT-RNDR-01 | `style-loading.spec.ts`, `layer-visibility.spec.ts`, `feature-query.spec.ts` |
| JS-EXT-01 | `tile-rendering.spec.ts` |
| JS-EXT-02 | `tile-rendering.spec.ts` |

The custom reporter (`helpers/cert-reporter.ts`) writes a `<run-id>-js-mvt.cert.json` envelope to `test-results/`.

## Prerequisites

- Node.js >= 18
- A running Honua Server with the `browser-compat.yaml` seed data applied

## Running Locally

```bash
cd tests/js-browser
npm install
npx playwright install --with-deps chromium
```

If `HONUA_BASE_URL` points to a healthy server, tests use it directly. Otherwise the global setup attempts to start a local server via `tests/python/shared/js_test_server.py`.

```bash
# Against a running server
HONUA_BASE_URL=http://localhost:5000 npm test

# Headed mode (see the browser)
HONUA_BASE_URL=http://localhost:5000 npm run test:headed

# Debug mode (Playwright inspector)
HONUA_BASE_URL=http://localhost:5000 npm run test:debug
```

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `HONUA_BASE_URL` | `http://localhost:5000` | Honua server URL |
| `HONUA_TEST_PORT` | `5555` | Port for auto-started local server |
| `CI` | — | Set by GitHub Actions; controls reporter environment field |

## Seed Data

The suite uses `tests/seed/browser-compat.yaml`, which creates three layers:

| Layer ID | Geometry | Description |
|---|---|---|
| 2000 | Point | Seeded points in the San Francisco area |
| 2001 | LineString | Seeded line features |
| 2002 | Polygon | Seeded polygon features |

## CI

The `maplibre-compat` job in `ci.yml` runs this suite as a merge-blocking gate. It:

1. Starts Honua Server with PostGIS and the browser-compat seed.
2. Installs Playwright Chromium.
3. Runs `npm test` and uploads the `maplibre-compat-results` artifact (Playwright report + `.cert.json` envelope).

## Test Structure

```
tests/js-browser/
├── package.json
├── playwright.config.ts
├── tsconfig.json
├── global-setup.ts          # Health check / optional server bootstrap
├── helpers/
│   ├── map-harness.ts       # MapLibre map lifecycle helper
│   └── cert-reporter.ts     # .cert.json evidence reporter
└── specs/
    ├── style-loading.spec.ts     # Style + TileJSON discovery
    ├── tile-rendering.spec.ts    # MVT fetch + canvas render
    ├── layer-visibility.spec.ts  # Per-geometry-type visibility
    └── feature-query.spec.ts     # Interactive queryRenderedFeatures
```
