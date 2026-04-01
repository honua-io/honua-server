# GIS User Guide

Connect to Honua from desktop GIS applications and consume geospatial services.

## Getting Started

- [Protocols Overview](STANDARDS_APIS.md) — All supported protocols (FeatureServer, MapServer, OGC, OData, MVT)
- [QGIS Tutorial](tutorials/qgis-getting-started.md) — Zero to querying in 5 minutes
- [GeoServer Migration](tutorials/geoserver-migration-guide.md) — Endpoint mapping and automated import

## Client Templates

- [Client Setup Runbook](CLIENT_TEMPLATE_RUNBOOK.md) — ArcGIS Pro, QGIS, Power BI, Excel
- [Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md) — Tested client software versions
- [Template Files](client-templates/) — Ready-to-use project templates

## Data & Import

- [Data Modeling Guide](DATA_MODELING_GUIDE.md) — Spatial data modeling best practices
- [FileGDB Import](FILEGDB_IMPORT_WORKFLOW.md) — File Geodatabase packaging and upload

## Protocol Coverage

- [FeatureServer Matrix](feature-server-matrix.md) — Esri FeatureServer endpoint coverage
- [MapServer Matrix](map-server-matrix.md) — MapServer, WMS 1.3, WMTS 1.0 coverage
- [OGC API Features](specifications/ogc-api-features-coverage.md) — Parts [1](specifications/ogc-api-features-part1-core.md), [2](specifications/ogc-api-features-part2-crs.md), [3](specifications/ogc-api-features-part3-filtering.md)
- [OGC API Tiles](specifications/ogc-api-tiles-coverage.md)
- [OData v4](specifications/odata-v4-coverage.md)
- [Geometry Service](specifications/geometry-service-coverage.md)

## Compatibility

- [Known Limitations](MVP_COMPATIBILITY_CONTRACT.md) — Current protocol limitations
- [Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md) — Cross-client interop test results
- [Certification Evidence](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) — Final `.cert.json` envelope plus the Windows client smoke-artifact contract
