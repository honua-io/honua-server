# Table of Contents

## Operator Guide

- [Overview](operator/README.md)
- [Infrastructure & Deployment](operator/infrastructure.md)
- [Docker Compose](operator/docker-compose.md)
- [Deployment Scenarios](operator/DEPLOYMENT_SCENARIOS.md)
- [Database Support Matrix](operator/database-support-matrix.md)
- [DuckDB Provider](operator/duckdb-provider.md)
- [SQL Server Provider](operator/sqlserver-provider.md)
- [MySQL / MariaDB Provider](operator/mysql-provider.md)
- [TLS Connection Guide](operator/tls-connection-guide.md)
- [Security](operator/security.md)
- [Client Certificate Authentication](operator/client-certificate-authentication.md)
- [Compliance Framework](operator/compliance-framework.md)
- [HTTP Client Resilience](operator/http-client-resilience.md)
- [Monitoring & Observability](operator/monitoring.md)
- [Operations](operator/operations.md)
- [Control Plane API](operator/CONTROL_PLANE_API.md)
- [ArcGIS Inventory Discovery](operator/arcgis-inventory-discovery.md)
- [Webhooks](operator/feature-change-webhooks.md)
- [Feature Streaming](operator/feature-streaming.md)
- [Tile Operations](operator/tile-operations-runbook.md)
- [SLD Migration Reference](operator/sld-migration.md)
- [PMTiles Publishing](operator/pmtiles-publishing.md)
- [Troubleshooting](operator/troubleshooting.md)
- [Upgrade & Rollback](operator/runbooks/UPGRADE_AND_ROLLBACK.md)

## GIS User Guide

- [Overview](gis/README.md)
- [Protocols Overview](gis/STANDARDS_APIS.md)
- [Data Modeling Guide](gis/DATA_MODELING_GUIDE.md)
- [FileGDB Import](gis/FILEGDB_IMPORT_WORKFLOW.md)
- [Raster Overview](gis/raster-overview.md)
- [Cloud-Optimized HDF5 / NetCDF4 Support](gis/cloud-optimized-hdf-netcdf-support.md)
- [Terrain-RGB Tiles](gis/terrain-tiles.md)
- [Hosted 3D Tiles Scenes](gis/scenes-3dtiles.md)
- [OpenUSD and Omniverse Export Path](gis/openusd-omniverse-export-path.md)
- [Point Cloud, Drone, and Reality-Capture Ingest](gis/point-cloud-reality-capture-ingest.md)
- [3D Tiles Generation Pipeline](gis/scene-generation.md)
- [Elevation Query and Profile API](gis/elevation-api.md)
- [Known Limitations](gis/MVP_COMPATIBILITY_CONTRACT.md)
- [Cross-Server Consume Gap Report](compatibility/cross-server-consume-gap-report.md)
- [Style Engine: Cross-Protocol Consumption](gis/style-engine-protocol-consumption.md)
- Tutorials
  - [QGIS Getting Started](gis/tutorials/qgis-getting-started.md)
  - [GeoServer Migration](gis/tutorials/geoserver-migration-guide.md)
- Protocol Coverage
  - [GeoServices REST Parity](gis/geoservices-rest-parity.md)
  - [FeatureServer](gis/feature-server-matrix.md)
  - [MapServer / WMS / WMTS](gis/map-server-matrix.md)
  - [ImageServer](gis/image-server-matrix.md)
  - [OGC API Features](gis/specifications/ogc-api-features-coverage.md)
  - [OGC API Tiles](gis/specifications/ogc-api-tiles-coverage.md)
  - [OGC API Processes](gis/specifications/ogc-api-processes-coverage.md)
  - [OGC API Records](gis/specifications/ogc-api-records-coverage.md)
  - [WCS 2.0.1](gis/specifications/wcs-2.0.1-coverage.md)
  - [OData v4](gis/specifications/odata-v4-coverage.md)
  - [Geometry Service](gis/geometry-service-matrix.md)
  - [Geoprocess Framework Analysis](gis/geoprocess-framework-analysis.md)
- Client Templates
  - [Setup Runbook](gis/CLIENT_TEMPLATE_RUNBOOK.md)
  - [Version Matrix](gis/CLIENT_TEMPLATE_VERSION_MATRIX.md)

## Developer Guide

- [Overview](developer/README.md)
- [API Examples](developer/API_EXAMPLES.md)
- [Integration Patterns](developer/INTEGRATION_PATTERNS.md)
- [Metadata and Catalog Parity Matrix](developer/metadata-catalog-parity-matrix.md)
- [NVIDIA Construction Demo Fixture](demo/nvidia-construction.md)
- [Console Content and RBAC (Admin API)](admin-api/console-content-and-rbac.md)
- [Console Job Observability (Admin API)](admin-api/console-job-observability.md)
- [Metadata Prevalidation Admin API](admin-api/metadata-prevalidation.md)
- [Scene Dataset Registry (Admin API)](admin-api/scene-dataset-registry.md)
- [SDK Compatibility](developer/SDK_COMPATIBILITY_MATRIX.md)
- [SDK Migration Automation Evidence Manifest](developer/sdk-migration-evidence-manifest.md)
- [SDK Standards Coverage](developer/SDK_STANDARDS_COVERAGE.md)
- [Mobile SDK Roadmap](developer/mobile-sdk-roadmap.md)
- [FieldCollection Mobile Sync API](developer/fieldcollection-mobile-sync-api.md)
- [MCP Server](developer/MCP_SERVER.md)
- [AI Builder SDK Contract](ai-builder-sdk-contract.md)
- [AI Builder Contract Fixtures](developer/ai-builder-contract-fixtures.md)
- [Spec Plan/Apply Engine](developer/SPEC_ENGINE.md)
- [Grounding & Intent Drafting](developer/GROUNDING.md)
- [Redis Fallback Patterns](developer/REDIS_FALLBACK_PATTERNS.md)
- [Versioning Policy](developer/CONTROL_PLANE_VERSIONING_POLICY.md)
- [Migration Guide](developer/CONTROL_PLANE_MIGRATION_GUIDE.md)

## Contributor Guide

- [Overview](contributor/README.md)
- [Getting Started](contributor/development/getting-started.md)
- [Contributing](contributor/development/contributing.md)
- [Architecture](contributor/ARCHITECTURE.md)
- [ADRs](contributor/adr/README.md)
- Metadata v2
  - [Release Readiness](contributor/architecture/metadata-v2-release-readiness.md)
  - [Admin UI Information Model](contributor/architecture/metadata-v2-admin-ui-information-model.md)
  - [Admin Operator Workflows](contributor/architecture/admin-operator-workflows.md)
- [TestKit](contributor/testkit.md)
- [Public Interface Quality Model](contributor/public-interface-quality-model.md)
- [Compatibility and Automated Migration Evidence](contributor/compatibility-and-migration-evidence.md)
- [Import and Migration Capability Evidence](contributor/import-capability-evidence.md)
- [OGC Certification Path](contributor/ogc-certification-path.md)
- [OGC CITE Conformance Evidence](contributor/ogc-cite-conformance-evidence.md)
- [Release Checklist](contributor/RELEASE_CHECKLIST.md)
- [GeoETL Roadmap](contributor/geoetl-roadmap.md)

## Security

- [Security Policy](../SECURITY.md)
- [Base URL and Open-Redirect Handling](security/base-url-and-open-redirect-handling.md)
- [Code-scanning Remediation — 2026 Q2](security/code-scanning-2026-Q2-remediation.md)

## Evidence

- [Evidence Index](evidence/README.md) — cross-cutting map of compatibility, conformance, parity, certification, and migration evidence

## Archive

- [Historical Archive](archive/README.md)
