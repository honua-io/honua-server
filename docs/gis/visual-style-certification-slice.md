# Visual / Style Certification Slice

This document is the canonical specification for the visual / style
compatibility and regression certification lane that ticket
[`#478`](https://github.com/honua-io/honua-server/issues/478) introduces.
It defines the shared scenarios that real OSS clients must exercise to
prove the server still draws the right symbols, lines, fills, labels,
sprites, and style URL/metadata after a server change.

The slice is read in conjunction with:

- [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](CROSS_CLIENT_CERTIFICATION_MATRIX.md) — vocabulary, ID stability policy, lane coverage table
- [`CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) — envelope schema for `.cert.json` outputs
- [`CLIENT_TEMPLATE_RUNBOOK.md`](CLIENT_TEMPLATE_RUNBOOK.md) — manual smoke-runbook lanes for licensed clients

## Scope

The slice covers six visual / style scenario categories. Each category
has a stable `CERT-RNDR-*` ID that lives in the cross-client
certification matrix and is recorded in the lane evidence envelopes
alongside the existing CERT-RNDR-01 (renders without error) and
CERT-RNDR-02 (refresh preserves state) IDs.

| Category | CERT ID | Description |
|---|---|---|
| Symbol — point | `CERT-RNDR-SYM-01` | Point symbol renders with declared color, size, and outline |
| Line — stroke | `CERT-RNDR-LIN-01` | Line geometry renders with declared stroke color and width |
| Fill — polygon | `CERT-RNDR-FIL-01` | Polygon geometry renders with declared fill color |
| Label — text | `CERT-RNDR-LBL-01` | Label / text overlay renders where the client supports it |
| Sprite — icon | `CERT-RNDR-SPR-01` | Sprite / icon resolves through the style URL and draws |
| Style URL | `CERT-RNDR-URL-01` | Client consumes a style URL or style metadata document |

The pre-existing CERT-RNDR-01 and CERT-RNDR-02 IDs are **not** renamed
or deprecated. They cover the broader "map renders without client
error" and "refresh preserves state" assertions that are independent
of any particular style category. The matrix's
[ID Stability Policy](CROSS_CLIENT_CERTIFICATION_MATRIX.md#id-stability-policy)
keeps existing IDs append-only.

## Geodesy Lock

All scenarios in this slice are exercised in **EPSG:3857 (Web
Mercator)**. Web Mercator is the lowest-common-denominator CRS across
the four target client lanes (OpenLayers, Esri Leaflet, MapLibre, and
PyQGIS) and removes per-CRS reprojection drift from the diff signal.
Lane harnesses set the destination CRS explicitly when the client
defaults differ:

- OpenLayers — `ol.proj.fromLonLat()` projects view center to 3857
- Esri Leaflet — Leaflet defaults to `L.CRS.EPSG3857`
- PyQGIS — `QgsMapSettings.setDestinationCrs(QgsCoordinateReferenceSystem("EPSG:3857"))`
  combined with a `QgsCoordinateTransform` that projects
  `layer.extent()` from the OAPIF source CRS into EPSG:3857 before it
  is handed to `QgsMapSettings.setExtent`. The transform is required
  because `setExtent` stores its argument as-is and the renderer
  interprets the values in the destination CRS.

Each lane derives its render extent deterministically from the seed
fixture (`tests/seed/client-compat-v1.sql`): the JS lanes pin the view
center via `ol.proj.fromLonLat`/`L.CRS.EPSG3857`, and the PyQGIS lane
projects the layer's reported extent into EPSG:3857 so the rendered
region matches the projected feature footprint rather than collapsing
to a degenerate strip near the prime meridian.

## Tolerance Defaults

Two complementary assertion strategies are sanctioned for this slice:

1. **Pixel-color sampling.** The lane harness applies a style with a
   known, distinctive RGB color and asserts that color appears in the
   rendered canvas above a configurable pixel-count threshold. This is
   the preferred strategy because it is deterministic across GPU,
   font, and AA stacks.
2. **Snapshot diff.** The lane harness commits a baseline PNG and
   diffs new renders against it. This is the existing Esri Leaflet
   approach for `feature-layer-symbology.png`.

When snapshot diffing is used, the default tolerance is
`maxDiffPixelRatio: 0.02` and `threshold: 0.3`, matching
[`tests/js-browser/playwright.config.ts`](../../tests/js-browser/playwright.config.ts).
Lane-specific overrides are recorded in the per-scenario rows below.

When pixel-color sampling is used, the lane records the
`measured_count` on the evidence envelope as the count of matching
pixels, so reviewers can spot a sudden drop without re-running the
suite. The per-channel RGB tolerance is chosen per lane to match the
rasterization and anti-aliasing characteristics of that renderer, so
the slice spec deliberately does not pin a single number: PyQGIS uses
`COLOR_TOLERANCE = 35` in
[`tests/python/pyqgis/test_render_path.py`](../../tests/python/pyqgis/test_render_path.py),
and the OpenLayers browser lane uses per-target tolerances (`30` for
the stroke target, `40` for the symbol and fill targets) in
[`tests/js/openlayers/rendering/render.spec.ts`](../../tests/js/openlayers/rendering/render.spec.ts).

## Scenario Catalogue

Each scenario row declares the protocols it applies to, the canonical
fixture inputs, the strategy used by each lane to substantiate the
scenario, and the evidence path that shows up in the per-lane
`.cert.json` envelope.

> **Fixture note.** The `client-compat-v1` seed
> ([`tests/seed/client-compat-v1.sql`](../../tests/seed/client-compat-v1.sql))
> currently provides only point geometries. Scenarios that require
> line or polygon geometries are documented here as **slice
> contracts** even when no lane substantiates them yet — the slice
> spec must drive future fixture additions, not be silently narrowed
> to what the current seed happens to contain. Lanes that cannot
> substantiate a scenario record `skip` with a `pending-fixture` note
> so the gap is visible in the rollup.

### CERT-RNDR-SYM-01 — Point symbol

| Attribute | Value |
|---|---|
| Category | Symbol |
| Protocols | FeatureServer, OGC API Features, MVT |
| Fixture | `client-compat-v1` `test_service` collection `0` (point features) |
| Style source | drawingInfo (FS) / inline ol.style.Circle (OGC vector tiles) / QGIS marker symbol (PyQGIS) |
| Expected color | `#1e64c8` (declared, distinctive blue) |
| Pass criterion | At least 25 pixels of the declared symbol color in the canvas |
| Lanes substantiating | Esri Leaflet (drawingInfo), OpenLayers (ol/style/Circle), PyQGIS (QgsMarkerSymbol) |

### CERT-RNDR-LIN-01 — Line stroke

| Attribute | Value |
|---|---|
| Category | Line |
| Protocols | FeatureServer, OGC API Features, MVT |
| Fixture | _Pending fixture_ — slice contract; see open follow-on |
| Style source | drawingInfo (FS) / inline ol.style.Stroke (OGC vector tiles) / QgsLineSymbol (PyQGIS) |
| Expected color | `#1a1a2e` (declared dark stroke) |
| Pass criterion | At least 12 pixels of the declared stroke color along the line geometry |
| Lanes substantiating | OpenLayers and PyQGIS record the marker outline color from the existing fixture (point markers carry an outline that exercises the same code path). Both lanes record `skip` when the measured pixel count falls below threshold so a missing line fixture is visible in the rollup. A real line fixture is the closing follow-on. |

### CERT-RNDR-FIL-01 — Polygon fill

| Attribute | Value |
|---|---|
| Category | Fill |
| Protocols | FeatureServer, OGC API Features, MVT |
| Fixture | _Pending polygon fixture_ — slice contract |
| Style source | drawingInfo (FS) / ol.style.Fill (OGC vector tiles) / QgsFillSymbol (PyQGIS) |
| Expected color | `rgba(30, 100, 200, 0.6)` |
| Pass criterion | At least 50 pixels matching the declared fill color within the lane-specific per-channel tolerance |
| Lanes substantiating | OpenLayers and PyQGIS record the marker fill color from the existing fixture (point markers carry a fill that exercises the fill assertion path). Both lanes record `skip` when the measured pixel count falls below threshold so a missing polygon fixture is visible in the rollup. A real polygon fixture is the closing follow-on. |

### CERT-RNDR-LBL-01 — Label / text

| Attribute | Value |
|---|---|
| Category | Label |
| Protocols | FeatureServer, OGC API Features |
| Fixture | _Pending labelingInfo fixture_ — slice contract |
| Style source | drawingInfo `labelingInfo` (FS) / OGC API Features `featureLabel` |
| Pass criterion | Label DOM node or canvas text glyphs present after render |
| Lanes substantiating | None today — recorded as `skip` (`pending-fixture`) so the gap shows up in the rollup |

### CERT-RNDR-SPR-01 — Sprite / icon

| Attribute | Value |
|---|---|
| Category | Sprite |
| Protocols | MVT |
| Fixture | _Pending sprite endpoint_ — slice contract |
| Style source | MapLibre style JSON `sprite` URL |
| Pass criterion | Sprite atlas resolves and at least one icon glyph appears in the canvas |
| Lanes substantiating | None today — recorded as `skip` (`pending-fixture`). Closing this gap is bounded by the MapLibre lane decision in design Q#2 |

### CERT-RNDR-URL-01 — Style URL / metadata consumption

| Attribute | Value |
|---|---|
| Category | Style URL |
| Protocols | FeatureServer, MVT |
| Fixture | `client-compat-v1` — drawingInfo on the FeatureServer layer; OGC tileset metadata on the vector tile collection |
| Style source | `/rest/services/{id}/FeatureServer/{layerId}?f=json` (drawingInfo) and `/ogc/tiles/collections/{id}/tiles/WebMercatorQuad` (TileSetMetadata) |
| Pass criterion | Client successfully fetches the style/metadata document and uses it to drive the layer |
| Lanes substantiating | Esri Leaflet (drawingInfo via FeatureServer metadata), OpenLayers (OGC TileSetMetadata via OGCVectorTile source) |

> Protocol applicability aligns with [`CROSS_CLIENT_CERTIFICATION_MATRIX.md`](CROSS_CLIENT_CERTIFICATION_MATRIX.md) row `CERT-RNDR-URL-01` (FS, MVT). OGC API Features does not ship a style URL / metadata document today; the SCHM/DISC IDs already cover its collection-metadata discovery path, so `CERT-RNDR-URL-01` is recorded as `not-applicable` on `ogc-features` envelopes per the applicability sets in [`tests/js/openlayers/shared/evidence.ts`](../../tests/js/openlayers/shared/evidence.ts).

## Lane Coverage

Each primary OSS client lane maps its tests to the scenario IDs above
through its existing evidence collector. No new evidence-writing
utility is introduced.

| Lane | Collector | Substantiated | Pending-fixture |
|---|---|---|---|
| **JS — OpenLayers** | [`tests/js/openlayers/shared/evidence.ts`](../../tests/js/openlayers/shared/evidence.ts) | `CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, `CERT-RNDR-FIL-01`, `CERT-RNDR-URL-01` | `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01` |
| **JS — Esri Leaflet** | [`tests/js-browser/shared/cert-reporter.ts`](../../tests/js-browser/shared/cert-reporter.ts) | `CERT-RNDR-SYM-01`, `CERT-RNDR-URL-01` | `CERT-RNDR-LIN-01`, `CERT-RNDR-FIL-01`, `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01` |
| **Desktop — PyQGIS** | [`tests/python/pyqgis/conftest.py`](../../tests/python/pyqgis/conftest.py) | `CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, `CERT-RNDR-FIL-01` | `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01`, `CERT-RNDR-URL-01` |

The pixel-color sampling assertions in the JS lanes use the same RGB
constants declared above so that a single drift in either the test
fixture or the server style code path triggers all lanes to flag the
regression at once.

## Evidence Path

The slice does **not** introduce a new evidence file format. Lane
collectors continue to write `.cert.json` envelopes per the existing
schema. The slice IDs flow into the same envelopes as the original
CERT-* IDs:

- JS Esri Leaflet — `tests/js-browser/evidence/<run-id>-js-<protocol>.cert.json`
- JS OpenLayers — `tests/js/certification-evidence/<run-id>-js-<protocol>.cert.json`
- PyQGIS — `tests/TestResults/<run-id>-desktop-qgis-<protocol>.cert.json`

The release ledger
[`docs/gis/data/public-interface-proof.json`](data/public-interface-proof.json)
references this slice spec from all three `linkedTicket: "#478"`
real-client-certification proofs (`wms-1.3`, `wmts-1.0`, and
`ogc-api-maps-and-static-rendering`). All three remain `planned`
because the slice lane tests exercise adjacent surfaces — OGC API
Features, OGC API Tiles, and FeatureServer — and do not hit
`/ogc/services/{serviceId}/wms`, `/ogc/services/{serviceId}/wmts`,
`/ogc/maps`, or `/static/`. Substantiating those three surfaces
requires a real WMS / WMTS / OGC API Maps client lane (for example
`ol/source/TileWMS`, `ol/source/WMTS`, `ol/source/ImageWMS`, or the
QGIS WMS/WMTS providers) and is a bounded follow-on to this ticket.
The slice spec, the cross-client matrix, and the public-interface
quality model are listed as the proof's evidence locations so the
release ledger points reviewers at the slice contract while the
client lane is still pending.

## CI Posture

The slice runs inside the existing PR-blocking jobs that already
execute the underlying lanes. No new CI jobs are introduced as part
of this ticket — the additional `CERT-RNDR-*` records flow through
the existing evidence pipelines.

| Lane | Job | PR-blocking | Snapshot policy |
|---|---|---|---|
| OpenLayers | `js-integration-tests` (`.github/workflows/ci.yml`) | yes | `updateSnapshots: 'none'` (CI), `'missing'` (local) |
| Esri Leaflet | `esri-leaflet-browser-tests` (`.github/workflows/ci.yml`) | yes | `updateSnapshots: 'none'` (CI), `'missing'` (local) |
| PyQGIS | `pyqgis-client-compat-nightly` (`.github/workflows/pyqgis-client-compat-nightly.yml`) | nightly (not PR-blocking) | structural assertion + envelope validation |

The PyQGIS lane stays nightly because its runtime budget (QGIS
install + xvfb + render time) is significantly larger than the JS
lanes and the lane already runs inside a dedicated nightly workflow.
Promoting it to PR-blocking is a follow-on, captured in the design
brief Question #3 about CI gating posture.

The OpenLayers Playwright lane hard-fails on a `CERT-RNDR-SYM-01`
regression so the PR build gates on the slice substantiation
directly rather than deferring to the release-time evidence review.
The 25-pixel threshold has ample margin against the points-only seed
(radius-5 markers across nine features render hundreds of matching
pixels), so the gate is not a flake risk and the assertion is placed
after every slice ID has been recorded so the envelope still captures
the full measurement set even when the assertion throws.
`CERT-RNDR-LIN-01` and `CERT-RNDR-FIL-01` stay soft-skip in the same
lane until their dedicated line / polygon fixtures land, because the
points fixture only exercises them indirectly via the marker outline
and fill code paths — a hard-fail there would gate on a
substantiation that is not yet contractually guaranteed by the seed.
The substantiation point lives in
[`tests/js/openlayers/rendering/render.spec.ts`](../../tests/js/openlayers/rendering/render.spec.ts).

## How to Refresh a Baseline

For tests that use snapshot diffing (Esri Leaflet
`feature-layer-symbology.png` is the only snapshot today):

```bash
cd tests/js-browser
npx playwright test --update-snapshots
git add esri-leaflet/rendering.spec.ts-snapshots/
```

For tests that use pixel-color sampling (the SYM/LIN/FIL/URL
additions in this ticket): no baseline file is needed — the test
asserts directly against the declared RGB constants. To change the
declared color, update both the constant in the slice spec and the
constant in the lane test in the same commit so the slice and the
runtime stay in sync.

## Cache Invalidation Notes

Each lane uses unique fixture layer IDs from
[`tests/seed/`](../../tests/seed/), so the visual / style scenarios
do not introduce a global cache bust. Scenarios that exercise the
`/api/styles/{layerId}.json` or OGC tileset metadata endpoints rely
on the existing per-layer cache key isolation — they do not require
a `Cache-Control: no-cache` override.

## Out of Scope

- **Server-side style generation.** This slice exercises stored
  drawingInfo and stored tileset metadata. The
  `Honua.Postgres.Features.Styling.PostgresLayerStyleCatalog`
  drawingInfo generation gap noted in the design brief is a separate
  ticket and not closed by this slice.
- **Licensed client lanes.** ArcGIS Pro, Power BI, and Excel remain
  manual per [`CLIENT_TEMPLATE_RUNBOOK.md`](CLIENT_TEMPLATE_RUNBOOK.md)
  and are out of scope for slice automation.
- **No new server endpoints.** The slice consumes only existing
  routes (`/rest/services/.../FeatureServer`, `/ogc/features/...`,
  `/ogc/tiles/collections/.../tiles/WebMercatorQuad`).

## Slice Version

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-04-06 | Initial slice — six visual / style scenarios, lane-coverage table, geodesy lock, evidence path |
| 1.1 | 2026-04-07 | Align `CERT-RNDR-URL-01` Protocols row with the matrix (FS, MVT); scope the release-ledger flip to `ogc-api-maps-and-static-rendering` only (WMS 1.3 / WMTS 1.0 remain `planned` pending a real WMS/WMTS client lane) |
| 1.2 | 2026-04-07 | Reconcile `CERT-RNDR-FIL-01` pass criterion with the actual lane tolerances (PyQGIS `COLOR_TOLERANCE=35`, OpenLayers `30`/`40`); drop the stale `±10 RGB` pin on FIL-01 and document the lane-specific per-channel tolerance contract in `Tolerance Defaults` |
| 1.3 | 2026-04-07 | Append PyQGIS to the `CERT-RNDR-LIN-01` and `CERT-RNDR-FIL-01` `Lanes substantiating` rows so the per-scenario rows match the lane coverage table; clarify the soft-skip behavior on threshold miss |
| 1.4 | 2026-04-07 | Document the OpenLayers Playwright lane's `CERT-RNDR-SYM-01` hard-fail vs `CERT-RNDR-LIN-01`/`CERT-RNDR-FIL-01` soft-skip gating contract under `CI Posture` so the PR-blocking asymmetry is discoverable without reading `tests/js/openlayers/rendering/render.spec.ts` |
| 1.5 | 2026-04-07 | Revert the `ogc-api-maps-and-static-rendering` real-client-certification proof from `implemented` back to `planned` to match the actual lane coverage. The slice tests exercise OAPIF / OGC Tiles / FeatureServer adjacent surfaces and do not hit `/ogc/maps` or `/static/`, so flipping the OGC Maps surface would have overstated coverage. All three `#478`-linked surface proofs (`wms-1.3`, `wmts-1.0`, `ogc-api-maps-and-static-rendering`) now remain `planned` pending dedicated WMS / WMTS / OGC API Maps client lanes. |
