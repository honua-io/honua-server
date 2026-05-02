# GIS User Guide

Connect to Honua from desktop GIS applications and consume geospatial services.

## Getting Started

- [Protocols Overview](STANDARDS_APIS.md) — Major protocol families, client fit, and entry points
- [QGIS Tutorial](tutorials/qgis-getting-started.md) — Zero to querying in 5 minutes
- [GeoServer Migration](tutorials/geoserver-migration-guide.md) — Endpoint mapping, inventory scan, compatibility review, and dry-run import

## Client Templates

- [Client Setup Runbook](CLIENT_TEMPLATE_RUNBOOK.md) — ArcGIS Pro, QGIS, Power BI, Excel
- [Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md) — Tested client software versions
- [Template Files](client-templates/) — Ready-to-use project templates

## Data & Import

- [Data Modeling Guide](DATA_MODELING_GUIDE.md) — Spatial data modeling best practices
- [FileGDB Import](FILEGDB_IMPORT_WORKFLOW.md) — File Geodatabase packaging and upload
- [Raster Overview](raster-overview.md) — Raster import, COG registration/direct serving, and remaining mosaic/catalog roadmap status
- [Terrain-RGB Tiles](terrain-tiles.md) — DEM/raster elevation tiles for MapLibre/Mapbox `raster-dem` clients
- [Hosted 3D Tiles Scenes](scenes-3dtiles.md) — Already-hosted OGC 3D Tiles tilesets for CesiumJS and other 3D Tiles clients
- [Extruded 3D Feature Layers (v1)](extruded-3d-feature-layers.md) — Height-driven extrusion metadata on FeatureServer layers (precedes 3D Tiles generation)

## Protocol Coverage

- [GeoServices REST Parity](geoservices-rest-parity.md) — Canonical landing page for FeatureServer, MapServer, ImageServer, Geometry Service, and GPServer
- [GeoServices REST Parity Data (JSON)](data/geoservices-rest-parity.json) — Machine-readable export of the same parity contract
- [FeatureServer Matrix](feature-server-matrix.md) — Esri FeatureServer endpoint coverage
- [MapServer Matrix](map-server-matrix.md) — MapServer, WMS 1.3, WMTS 1.0 coverage
- [ImageServer Matrix](image-server-matrix.md) — Esri ImageServer endpoint coverage
- [Geometry Service Matrix](geometry-service-matrix.md) — Geometry operations plus Honua supplemental `area` and `length` routes
- [Geoprocess Framework Analysis](geoprocess-framework-analysis.md) — GPServer, OGC API Processes, and GeoServer WPS mapped to Honua canonical model
- [OGC API Features](specifications/ogc-api-features-coverage.md) — Parts [1](specifications/ogc-api-features-part1-core.md), [2](specifications/ogc-api-features-part2-crs.md), [3](specifications/ogc-api-features-part3-filtering.md)
- [OGC API Tiles](specifications/ogc-api-tiles-coverage.md)
- [OGC API Coverages](specifications/ogc-api-coverages-coverage.md) — modern REST/JSON raster coverage discovery, schema, and export
- [OGC API Processes](specifications/ogc-api-processes-coverage.md) — async geoprocessing adapter over canonical runtime
- [WCS 2.0.1](specifications/wcs-2.0.1-coverage.md) — raw raster/coverage export over the shared raster store
- [OData v4](specifications/odata-v4-coverage.md)

## Styling

- [Style Engine: Cross-Protocol Consumption](style-engine-protocol-consumption.md) — Canonical MapLibre style ingest, theme transforms (`dark`, `colorblind-safe`, `print`), revision metadata, and how stored styles flow into MVT, MapServer, and WMS rendering paths.

## Compatibility

- [Known Limitations](MVP_COMPATIBILITY_CONTRACT.md) — Current protocol limitations
- [I3S Compatibility Matrix](i3s-compatibility-matrix.md) — Esri Indexed 3D Scene Layer compatibility spike and Enterprise-roadmap conformance plan
- [Public Interface Proof Ledger](data/public-interface-proof.json) — Machine-readable inventory of every shipped public surface, proof classes, CI lanes, and evidence locations
- [Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md) — Cross-client interop test results
- [Certification Evidence](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) — Final `.cert.json` envelope plus the Windows client smoke-artifact contract
- [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md) — Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer sources (refreshed by the nightly `cross-server-consume-nightly.yml` workflow)
