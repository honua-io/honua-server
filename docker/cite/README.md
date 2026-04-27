# CITE Docker Assets

This directory contains reproducible Docker inputs for OGC CITE and related conformance suites. Generated outputs stay in the repository-root result directories, such as `cite-results/`, `cite-wms-results/`, and `cite-wfs20-results/`.

## Layout

- `ogc-api-features/` - OGC API Features CITE compose, config, and seed data.
- `ogc-api-tiles/` - OGC API Tiles CITE compose, config, and seed data.
- `wfs20/` - WFS 2.0 CITE compose and WFS-specific metadata config.
- `wms13/` - WMS 1.3 CITE compose and config.
- `wmts10/` - WMTS 1.0 CITE compose and config.
- `gml32/` - GML 3.2 CITE compose.
- `gpkg12/` - GeoPackage 1.2 CITE compose.
- `kml22/` - KML 2.2 CITE compose.
- `shared/` - Seed data, test data, and runner scripts reused by multiple suites.

Runner scripts in `scripts/conformance/cite/` are the supported entry points. They reference the suite compose files under this directory and keep result artifacts outside `docker/cite/`.

## Classic WMS/WFS CI Entry Point

Use `.github/workflows/cite-classic-conformance.yml` for an on-demand run of WMS 1.3, WFS 2.0, or both suites without external secrets. The workflow builds `honua-server:latest` once per suite job, sets `HONUA_CITE_SKIP_BUILD=true`, and then runs the existing scripts against the Docker Compose assets in `wms13/` and `wfs20/`.

Local runs can reuse an already-built image the same way:

```bash
docker build -t honua-server:latest .
HONUA_CITE_SKIP_BUILD=true ./scripts/conformance/cite/run-cite-wms-tests.sh --profile minimal
HONUA_CITE_SKIP_BUILD=true ./scripts/conformance/cite/run-cite-wfs20-tests.sh --profile basic
```

If another local service already uses the WFS TeamEngine host port, set
`HONUA_CITE_WFS20_TEAMENGINE_PORT`, for example:

```bash
HONUA_CITE_WFS20_TEAMENGINE_PORT=18081 \
HONUA_CITE_SKIP_BUILD=true ./scripts/conformance/cite/run-cite-wfs20-tests.sh --profile basic
```
