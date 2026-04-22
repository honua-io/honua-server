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
- `fixtures/` contains the static browser page served by the Esri Leaflet lane
