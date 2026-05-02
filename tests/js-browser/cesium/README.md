# Cesium Browser Compatibility Tests

Playwright-based smoke and certification specs that exercise CesiumJS
against Honua's WMS, WMTS, OGC API Tiles, OGC API Maps, and **3D Tiles**
(`/scenes/{id}`) protocol surfaces.

Canonical contributor documentation:
[docs/contributor/testing-javascript.md](../../../docs/contributor/testing-javascript.md).

## Local prerequisites

A running Honua server seeded with `tests/seed/browser-compat.yaml`. For the
3D Tiles smoke spec the server must additionally bind a `SceneDataset`
configuration entry to the committed fixture and allow the test CORS origin.
Set the following before launching the server:

```bash
export Scenes__Datasets__0__Id=fixture-tileset
export Scenes__Datasets__0__Name="Honua Cesium 3D Tiles smoke fixture"
export Scenes__Datasets__0__AssetRoot="$(git rev-parse --show-toplevel)/tests/fixtures/scenes/fixture-tileset"
export Scenes__Datasets__0__TilesetFileName=tileset.json
# Production CORS policy reads Cors:AllowedOrigins. Set DevelopmentOrigins
# too if the server runs with ASPNETCORE_ENVIRONMENT=Development.
export Cors__AllowedOrigins__0=http://cesium-test.honua.local
export Cors__DevelopmentOrigins__0=http://cesium-test.honua.local
```

## Run all Cesium specs

```bash
cd tests/js-browser/cesium
npm ci
npx playwright install --with-deps chromium
npx playwright test --config playwright.config.ts
```

## Run only the 3D Tiles smoke

The single documented command for the smoke suite:

```bash
cd tests/js-browser/cesium
npm ci
npx playwright install --with-deps chromium
npx playwright test --config playwright.config.ts --grep "3D Tiles"
```

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `HONUA_BASE_URL` | `http://localhost:5000` | Honua server under test. |
| `HONUA_SCENE_FIXTURE_ID` | `fixture-tileset` | Scene id matched against `Scenes:Datasets:0:Id`. |
| `HONUA_CORS_TEST_ORIGIN` | `http://cesium-test.honua.local` | Origin used to probe Honua's CORS configuration. Must appear in `Cors:AllowedOrigins` (production) or `Cors:DevelopmentOrigins` (development). |

## CI surfaces

- **`cesium-3d-tiles-smoke`** job in `.github/workflows/ci.yml` runs only the
  3D Tiles smoke (`--grep "3D Tiles"`) on every integration-bearing PR.
- **`cesium`** lane in `.github/workflows/client-interop-nightly.yml`
  (driven by `docker/client-compat/cesium/`) runs every Cesium spec
  including 3D Tiles and emits `.cert.json` envelopes. The 3D Tiles
  baseline + `expected-pairs.json` entry land as a separate bootstrap
  commit after the first successful nightly run, per
  [`tests/baselines/client-compat/README.md`](../../baselines/client-compat/README.md).
