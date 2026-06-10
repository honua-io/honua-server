# Table of Contents

## Concepts

- [Architecture](concepts/architecture.md)
- [Protocols & standards](concepts/protocols.md)
- [Data model](concepts/data-model.md)
- [Ecosystem & SDKs](concepts/ecosystem.md)
- [License migration](concepts/license-migration.md)

## Guides

- Publish data
  - [Serve raster data](guides/publish/publish-rasters.md)
  - [Cloud-optimized HDF5 / NetCDF4](guides/publish/cloud-optimized-hdf-netcdf-support.md)
  - [Terrain & elevation tiles](guides/publish/publish-terrain-and-elevation.md)
  - [3D scenes & 3D Tiles](guides/publish/publish-3d-scenes.md)
  - [3D Tiles generation pipeline](guides/publish/scene-generation.md)
  - [Extruded 3D feature layers](guides/publish/extruded-3d-feature-layers.md)
  - [FileGDB import](guides/publish/filegdb-import-workflow.md)
  - [Tile operations](guides/publish/publish-tiles.md)
  - [PMTiles publishing](guides/publish/pmtiles-publishing.md)
- Style maps
  - [Style maps](guides/style/style-maps.md)
  - [Import SLD styles](guides/style/import-sld-styles.md)
- Query & analyze
  - [Query features](guides/query-analyze/query-features.md)
  - [Run geoprocessing](guides/query-analyze/run-geoprocessing.md)
  - [Work with time](guides/query-analyze/work-with-time.md)
- Connect clients
  - [QGIS](guides/connect/qgis.md)
  - [AI agents (MCP)](guides/connect/ai-agents-mcp.md)
- Edit data
  - [React to feature changes](guides/edit/react-to-changes.md)
  - [Feature streaming](guides/edit/feature-streaming.md)
- Secure
  - [Authentication & security](guides/secure/authentication.md)
  - [TLS & mTLS](guides/secure/tls-and-mtls.md)
  - [Client certificate authentication](guides/secure/client-certificate-authentication.md)
  - [Compliance framework](guides/secure/compliance.md)
- Deploy & operate
  - [Docker Compose](guides/deploy/docker-compose.md)
  - [Kubernetes & infrastructure](guides/deploy/kubernetes.md)
  - [Deployment scenarios](guides/deploy/deployment-scenarios.md)
  - [Monitoring & observability](guides/deploy/monitoring.md)
  - [Operations](guides/deploy/operations.md)
  - [HTTP client resilience](guides/deploy/http-client-resilience.md)
  - [Upgrade & rollback](guides/deploy/upgrade-and-rollback.md)
  - [Troubleshooting](guides/deploy/troubleshooting.md)
- Migrate
  - [From ArcGIS Server](guides/migrate/from-arcgis-server.md)
  - [From GeoServer](guides/migrate/from-geoserver.md)
  - [ArcGIS inventory discovery](guides/migrate/arcgis-inventory-discovery.md)
  - [Migration pilot cutover checklist](guides/migrate/migration-pilot-cutover-checklist.md)

## Reference

- Protocols
  - [gRPC](reference/protocols/grpc.md)
  - [Terrain & elevation API](reference/protocols/terrain-and-elevation.md)
  - [Specification coverage](reference/protocols/specifications/README.md)
    - [OGC API Features](reference/protocols/specifications/ogc-api-features-coverage.md)
    - [OGC API Features Part 1: Core](reference/protocols/specifications/ogc-api-features-part1-core.md)
    - [OGC API Features Part 2: CRS](reference/protocols/specifications/ogc-api-features-part2-crs.md)
    - [OGC API Features Part 3: Filtering](reference/protocols/specifications/ogc-api-features-part3-filtering.md)
    - [OGC API Tiles](reference/protocols/specifications/ogc-api-tiles-coverage.md)
    - [OGC API Coverages](reference/protocols/specifications/ogc-api-coverages-coverage.md)
    - [OGC API Processes](reference/protocols/specifications/ogc-api-processes-coverage.md)
    - [OGC API Records](reference/protocols/specifications/ogc-api-records-coverage.md)
    - [WCS 2.0.1](reference/protocols/specifications/wcs-2.0.1-coverage.md)
    - [OData v4](reference/protocols/specifications/odata-v4-coverage.md)
- Admin API
  - [Overview (Control Plane API)](reference/admin-api/overview.md)
  - [Capability manifest](reference/admin-api/capability-manifest.md)
  - [Form packages](reference/admin-api/forms.md)
  - [License key rotation](reference/admin-api/license-key-rotation.md)
- Configuration
  - [Data sources](reference/configuration/data-sources/README.md)
    - [DuckDB](reference/configuration/data-sources/duckdb.md)
    - [SQL Server](reference/configuration/data-sources/sql-server.md)
    - [Oracle](reference/configuration/data-sources/oracle.md)
    - [MySQL / MariaDB](reference/configuration/data-sources/mysql-mariadb.md)
- Compatibility
  - [OGC conformance](reference/compatibility/ogc-conformance.md)
  - [CITE status](cite-status.md)
  - [Supported clients & known limitations](reference/compatibility/clients.md)
  - [GeoServices REST parity](reference/compatibility/geoservices-parity.md)
  - [FeatureServer matrix](reference/compatibility/feature-server-matrix.md)
  - [MapServer / WMS / WMTS matrix](reference/compatibility/map-server-matrix.md)
  - [ImageServer matrix](reference/compatibility/image-server-matrix.md)
  - [Geometry Service matrix](reference/compatibility/geometry-service-matrix.md)
  - [Client template runbook](gis/CLIENT_TEMPLATE_RUNBOOK.md)
  - [Client template version matrix](gis/CLIENT_TEMPLATE_VERSION_MATRIX.md)
- [Versioning & support](reference/versioning-and-support.md)
- [Control Plane migration guide](reference/control-plane-migration-guide.md)
- [Integration patterns](reference/integration-patterns.md)
- [Spec plan/apply engine](reference/spec-engine.md)

## Security

- [Security policy](../SECURITY.md)

## Internal

- [Internal engineering docs](internal/README.md)

## Archive

- [Historical archive](archive/README.md)
