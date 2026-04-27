# Client compatibility Docker harnesses

Real-client interop test matrix for Honua Server. Each lane runs the actual GIS
client software customers use (or, for ArcGIS Pro, a stub that mirrors its
REST consumption pattern), so regressions caused by client-specific quirks —
header tolerance, content-type handling, capability-document parsing, axis
order, GML flavor — surface before customers see them.

CITE conformance suites (`docker/cite/`) prove spec compliance. These harnesses
prove that real clients can consume the server.

## Lanes

| Lane | Image | Protocols exercised |
|------|-------|--------------------|
| `gdal` | `ghcr.io/osgeo/gdal:ubuntu-small-latest` | OGC API Features, WFS 2.0 |
| `pyqgis` | `qgis/qgis:ltr` | OGC API Features, WFS 2.0 |
| `openlayers` | `mcr.microsoft.com/playwright:v1.52.0-noble` | OGC API Features, OGC API Maps, OGC API Tiles, WFS, WMS, WMTS |
| `cesium` | `mcr.microsoft.com/playwright:v1.52.0-noble` | WMS, WMTS, OGC API Tiles, OGC API Maps |
| `arcgis-stub` | `python:3.12-slim` | GeoServices REST FeatureServer / MapServer (REST stub) |

ArcGIS Pro itself runs only on Windows + a paid license. The stub lane exercises
the same REST endpoints the Pro client consumes and emits a comparable cert
envelope; full Pro coverage requires a licensed runner (tracked in
`docs/gis/gap-report.md`).

## Run a single lane

```bash
docker compose -f docker/client-compat/compose.yml run --rm cesium
```

The compose file mounts the repo's `tests/` directory read-only and a
`./output` directory read/write so each lane writes `.cert.json` envelopes back
to the host for the baseline-diff step.

## Run the entire matrix

```bash
docker compose -f docker/client-compat/compose.yml --profile matrix up --abort-on-container-exit
```

`--abort-on-container-exit` propagates a non-zero exit if any lane fails.

## Output layout

```
docker/client-compat/output/
  cesium/
    <run-id>-js-cesium-wms.cert.json
    <run-id>-js-cesium-wmts.cert.json
    ...
  openlayers/
    <run-id>-js-ogc-features.cert.json
    ...
  gdal/
    gdal-ogr-results.json
  pyqgis/
    <run-id>-desktop-qgis-ogc-features.cert.json
    ...
  arcgis-stub/
    <run-id>-arcgis-stub-featureserver.cert.json
```

The baseline-diff step in `.github/workflows/client-interop-nightly.yml` reads
these files and compares them with `tests/baselines/client-compat/`.

## Adding a new client lane

1. Add `docker/client-compat/<lane>/Dockerfile`
2. Add a service block to `compose.yml`:
   - `depends_on: { honua: { condition: service_healthy } }`
   - `HONUA_BASE_URL=http://honua:5000`
   - `volumes: [ ../../tests:/workspace/tests:ro, ./output/<lane>:/output ]`
3. Add the lane name to the matrix in `.github/workflows/client-interop-nightly.yml`.

The lane's command must produce one or more `.cert.json` envelopes under
`/output` so the baseline diff can compare deterministically.
