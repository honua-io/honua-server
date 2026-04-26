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
