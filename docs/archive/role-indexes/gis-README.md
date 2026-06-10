# GIS User Guide

Connect to Honua from desktop GIS applications and consume geospatial services.

## Getting Started

- [Protocols Overview](../../concepts/protocols.md) — Major protocol families, client fit, and entry points
- [QGIS Tutorial](../../guides/connect/qgis.md) — Zero to querying in 5 minutes
- [GeoServer Migration](../../guides/migrate/from-geoserver.md) — Endpoint mapping, inventory scan, compatibility review, and dry-run import

## Client Templates

- [Client Setup Runbook](../../gis/CLIENT_TEMPLATE_RUNBOOK.md) — ArcGIS Pro, QGIS, Power BI, Excel
- [Version Matrix](../../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) — Tested client software versions
- [Template Files](../../gis/client-templates) — Ready-to-use project templates
- [Honua QGIS Plugin (staging)](../../../clients/qgis) — Two-click "Add Honua Server" plugin sources, packaging, and registry submission notes

## Data & Import

- [Data Modeling Guide](../../concepts/data-model.md) — Spatial data modeling best practices
- [FileGDB Import](../../guides/publish/filegdb-import-workflow.md) — File Geodatabase packaging and upload
- [Raster Overview](../../guides/publish/publish-rasters.md) — Raster import, COG registration/direct serving, and remaining mosaic/catalog roadmap status
- [Cloud-Optimized HDF5 / NetCDF4 Support](../../guides/publish/cloud-optimized-hdf-netcdf-support.md) — MVP registration / validation surface for multidimensional coverage sources
- [Terrain-RGB Tiles](../../guides/publish/publish-terrain-and-elevation.md) — DEM/raster elevation tiles for MapLibre/Mapbox `raster-dem` clients
- [Hosted 3D Tiles Scenes](../../guides/publish/publish-3d-scenes.md) — Already-hosted OGC 3D Tiles tilesets for CesiumJS and other 3D Tiles clients
- [OpenUSD and Omniverse Export Path](../../internal/spikes/openusd-omniverse-export-path.md) — Spike recommendation for a conservative USDA stage-manifest path for Honua scenes
- [Point Cloud, Drone, and Reality-Capture Ingest](../../internal/spikes/point-cloud-reality-capture-ingest.md) — Spike recommendation for a pre-tiled-first ingest path with bounded follow-ups for COPC streaming and CPU/GPU PDAL conversion
- [3D Tiles Generation Pipeline (v1)](../../guides/publish/scene-generation.md) — Deterministic OGC 3D Tiles 1.1 tilesets produced from PostGIS feature layers via the admin publishing path
- [Extruded 3D Feature Layers (v1)](../../guides/publish/extruded-3d-feature-layers.md) — Height-driven extrusion metadata on FeatureServer layers (precedes 3D Tiles generation)
- [Elevation Query and Profile API](../../reference/protocols/terrain-and-elevation.md) — Numeric point/line elevation lookup over registered raster datasets

## Protocol Coverage

- [GeoServices REST Parity](../../reference/compatibility/geoservices-parity.md) — Canonical landing page for FeatureServer, MapServer, ImageServer, Geometry Service, and GPServer
- [GeoServices REST Parity Data (JSON)](../../gis/data/geoservices-rest-parity.json) — Machine-readable export of the same parity contract
- [FeatureServer Matrix](../../reference/compatibility/feature-server-matrix.md) — Esri FeatureServer endpoint coverage
- [MapServer Matrix](../../reference/compatibility/map-server-matrix.md) — MapServer, WMS 1.3, WMTS 1.0 coverage
- [ImageServer Matrix](../../reference/compatibility/image-server-matrix.md) — Esri ImageServer endpoint coverage
- [Geometry Service Matrix](../../reference/compatibility/geometry-service-matrix.md) — Geometry operations plus Honua supplemental `area` and `length` routes
- [Geoprocess Framework Analysis](../../guides/query-analyze/run-geoprocessing.md) — GPServer, OGC API Processes, and GeoServer WPS mapped to Honua canonical model
- [OGC API Features](../../reference/protocols/specifications/ogc-api-features-coverage.md) — Parts [1](../../reference/protocols/specifications/ogc-api-features-part1-core.md), [2](../../reference/protocols/specifications/ogc-api-features-part2-crs.md), [3](../../reference/protocols/specifications/ogc-api-features-part3-filtering.md)
- [OGC API Tiles](../../reference/protocols/specifications/ogc-api-tiles-coverage.md)
- [OGC API Coverages](../../reference/protocols/specifications/ogc-api-coverages-coverage.md) — modern REST/JSON raster coverage discovery, schema, and export
- [OGC API Processes](../../reference/protocols/specifications/ogc-api-processes-coverage.md) — async geoprocessing adapter over canonical runtime
- [WCS 2.0.1](../../reference/protocols/specifications/wcs-2.0.1-coverage.md) — raw raster/coverage export over the shared raster store
- [OData v4](../../reference/protocols/specifications/odata-v4-coverage.md)

## Styling

- [Style Engine: Cross-Protocol Consumption](../../guides/style/style-maps.md) — Canonical MapLibre style ingest, theme transforms (`dark`, `colorblind-safe`, `print`), revision metadata, and how stored styles flow into MVT, MapServer, and WMS rendering paths.

## Time-aware Layers

- [Temporal Animation API](../../guides/query-analyze/work-with-time.md) — Server-first contract for time-aware feature layers, including `timeInfo`/`timeExtent` discovery, OGC `TIME` dimension support, MVT `?time=` filtering, entitlement gates, and accepted date/time formats.

## Compatibility

- [Known Limitations](../../reference/compatibility/clients.md) — Current protocol limitations
- [I3S Compatibility Matrix](../../internal/spikes/i3s-compatibility-matrix.md) — Esri Indexed 3D Scene Layer compatibility spike and Enterprise-roadmap conformance plan
- [Public Interface Proof Ledger](../../gis/data/public-interface-proof.json) — Machine-readable inventory of every shipped public surface, proof classes, CI lanes, and evidence locations
- [Certification Matrix](../../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md) — Cross-client interop test results
- [Certification Evidence](../../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) — Final `.cert.json` envelope plus the Windows client smoke-artifact contract
- [Cross-Client Certification Gap Report](../../gis/gap-report.md) — Latest real-client interop baseline comparison from `client-interop-nightly.yml`
- [Cross-Server Consume Gap Report](../../internal/compatibility/cross-server-consume-gap-report.md) — Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer sources (refreshed by the nightly `cross-server-consume-nightly.yml` workflow)
