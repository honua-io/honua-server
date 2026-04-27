# JavaScript Browser Compatibility Tests

This directory contains the Playwright-based browser compatibility suites used by
the JavaScript certification lane.

Canonical contributor documentation: [docs/contributor/testing-javascript.md](../../docs/contributor/testing-javascript.md).

Quick start:

```bash
cd tests/js-browser
npm ci
npx playwright install --with-deps chromium
npm test
npm run test:maplibre
```

Layout:

- `esri-leaflet/` contains the Esri Leaflet browser lane and its support files
- `maplibre/` contains the MapLibre GL JS browser lane and its support files
- `cesium/` contains the CesiumJS imagery-provider lane (WMS, WMTS, OGC API Tiles, OGC API Maps); run via `docker/client-compat/cesium/` in the nightly real-client interop matrix
- `fixtures/` contains the static browser page served by the Esri Leaflet lane
