# Import and Migration Capability Evidence

Last reviewed: 2026-05-17

This page is the website-linkable evidence summary for Honua Server import and
migration claims. It distinguishes production data import from migration
inventory, dry-run planning, and test-only interoperability probes.

For the website-level compatibility and automated migration claims, start with
[Compatibility and Automated Migration Evidence](compatibility-and-migration-evidence.md).

## Public Claim Wording

Use this wording for public material:

> Honua imports common GIS file formats and public, queryable ArcGIS GeoServices
> REST feature/map-service layers into PostGIS. It also inventories ArcGIS
> GeoServices REST and GeoServer REST sources for migration planning, supports
> GeoServer dry-run validation, and runs cross-server WMS/WFS/WMTS consume tests
> against reference GeoServer and MapServer services.

Do not currently claim general production import from OGC WFS/WMS/WMTS services
or non-dry-run GeoServer catalog migration. Those are not implemented as
operator-facing import paths in `honua-server`.

## Current Capability Matrix

| Capability | Status | Evidence | Public caveat |
|---|---|---|---|
| File import | Production path | `POST /api/v1/admin/import/upload`; `SupportedFileFormat` includes GeoJSON, Shapefile, GeoPackage, GPX, KML, GML, WKT, CSV, TinyWKB, FileGDB, FlatGeobuf, and GeoParquet | This is file/object import, not service migration. |
| Raster import | Production path | `POST /api/v1/admin/import/raster`; GeoTIFF/COG and PNG/JPEG world-file paths | Raster import is separate from coverage-service import. |
| ArcGIS GeoServices REST discovery | Production path | `POST /api/v1/admin/import/geoservices/discover` | Requires an HTTPS service-root URL ending in `FeatureServer` or `MapServer`; layer URLs and embedded credentials are rejected. |
| ArcGIS GeoServices REST layer import | Production path | `POST /api/v1/admin/import/geoservices/start`; background job uses paged `/query` reads, creates a PostGIS table, inserts attributes/geometries, builds a spatial index, and can auto-publish | Baseline path targets queryable public feature/map-service layers. Attachments and renderers are inventoried or flagged for manual follow-up, not imported as first-class data. |
| Unified migration scanner | Production path | `POST /api/v1/admin/import/scan` with `sourceKind=geoserver-rest` or `sourceKind=arcgis-geoservices-rest` | The scanner returns deterministic planning artifacts. It does not mutate catalog or data tables. |
| GeoServer REST discovery | Production path | `POST /api/v1/admin/import/geoserver/discover` and scanner support | HTTPS public URL required outside test mode. Basic auth is supported when both username and password are supplied. |
| GeoServer REST import job | Dry-run only at public endpoint | `POST /api/v1/admin/import/geoserver/start` rejects requests unless `dryRun=true` | Treat this as compatibility review and cutover planning, not applied catalog migration. |
| GeoServer SLD migration | Partial supporting path | Admin SLD import/export endpoints and `ISldStyleConverter` integration | Bulk GeoServer import validates/converts SLD content for diagnostics, but per-layer style persistence is handled by the admin SLD endpoint. |
| OGC WFS/WMS/WMTS service import | Not implemented | No production import endpoint exists for arbitrary OGC services | Current evidence is consume/read interoperability, not import. |
| Cross-server OGC consume | Test/nightly evidence | Test-only `/__test/cross-server-consume/proxy`; nightly `cross-server-consume-nightly.yml`; gap report at `docs/compatibility/cross-server-consume-gap-report.md` | The probe exists only in the Test environment and should not be presented as an operator API. |

## Implementation Evidence

### ArcGIS GeoServices REST

- Endpoint group: `src/Honua.Server/Features/Import/GeoservicesImportEndpoints.cs`
- Service implementation: `src/Honua.Postgres/Features/Import/GeoservicesImportService.cs`
- REST client: `src/Honua.Core/Features/Import/Services/ArcGisRestClient.cs`
- URL validation: `src/Honua.Server/Features/Import/GeoservicesServiceUrlValidation.cs`
- Inventory fixtures and baselines:
  `tests/dotnet/Honua.Postgres.Tests/Features/Import/Fixtures/ArcGis/`
  and `tests/dotnet/Honua.Postgres.Tests/Features/Import/Baselines/ArcGis/`
- Integration coverage:
  `tests/dotnet/Honua.Server.Tests/Import/GeoservicesImportEndpointTests.cs`,
  `GeoservicesParityIntegrationTests.cs`, and
  `GeoservicesGeoportalImportIntegrationTests.cs`

The importer validates a public HTTPS ArcGIS service root, discovers layer
metadata, queues a distributed import job, pages source features with
`resultOffset`/`resultRecordCount`, creates the target table in PostGIS,
converts Esri JSON geometries to GeoJSON/PostGIS geometry, creates a spatial
index, analyzes the table, and optionally publishes the imported table as a
Honua layer.

### GeoServer REST

- Endpoint group: `src/Honua.Server/Features/Import/GeoServerImportEndpoints.cs`
- Service implementation: `src/Honua.Postgres/Features/Import/GeoServerImportService.cs`
- REST client: `src/Honua.Core/Features/Import/Services/GeoServerRestClient.cs`
- URL validation: `src/Honua.Server/Features/Import/GeoServerServiceUrlValidation.cs`
- Inventory fixtures and baselines:
  `tests/dotnet/Honua.Postgres.Tests/Features/Import/Fixtures/GeoServer/`
  and `tests/dotnet/Honua.Postgres.Tests/Features/Import/Baselines/GeoServer/`
- Integration coverage:
  `tests/dotnet/Honua.Server.Tests/Import/GeoServerImportEndpointTests.cs`,
  `GeoServerCuratedImportIntegrationTests.cs`, and
  `GeoServerLiveImportIntegrationTests.cs`

The scanner discovers GeoServer version/global settings, workspaces, data
stores, coverage stores, layers, layer groups, styles, advertised service
endpoints, spatial references, style dependencies, and compatibility status.
The public `start` endpoint currently enforces `dryRun=true`; non-dry-run
application is intentionally not part of the public claim.

### OGC Cross-Server Consume

- Test-only proxy: `src/Honua.Server/Features/Import/CrossServerConsumeProbeEndpoints.cs`
- Gap report: `docs/compatibility/cross-server-consume-gap-report.md`
- Workflow inventory: `docs/ci/workflow-inventory.md`
- TestKit guide: `docs/contributor/testkit.md`

The cross-server consume suite validates Honua-as-client reads of WMS 1.3,
WFS 2.0, and WMTS 1.0 from reference GeoServer and MapServer containers. This
is interoperability evidence for consuming OGC services. It is not a production
import feature.

## Review Checklist

Before using import language in marketing, sales, or website copy:

- Use "ArcGIS GeoServices REST layer import" for the production live-service
  import path.
- Use "GeoServer REST migration inventory and dry-run validation" for
  GeoServer.
- Use "cross-server WMS/WFS/WMTS consume testing" for OGC reference-service
  evidence.
- Do not say "OGC service import" unless a production WFS/WMS/WMTS import path
  has been implemented and tested.
- Do not say "GeoServer catalog import applies changes" until the public
  endpoint accepts non-dry-run requests and writes real catalog state.

## Last Local Verification

The focused baseline checks were refreshed on 2026-05-17:

| Check | Result |
|---|---:|
| Server endpoints and migration scanner slice | 47 passed, 0 failed, 0 skipped |
| Postgres service inventory and baseline slice | 23 passed, 0 failed, 0 skipped |

## Suggested Verification Commands

Run these focused checks when refreshing this evidence:

```bash
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportEndpointTests|FullyQualifiedName~GeoServerImportEndpointTests|FullyQualifiedName~MigrationScannerEndpointTests"

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportServiceScanTests|FullyQualifiedName~GeoServerImportServiceScanTests|FullyQualifiedName~GeoservicesArcGisInventoryBaselineTests|FullyQualifiedName~GeoServerInventoryBaselineTests"
```

External/live tests such as `GeoservicesGeoportalImportIntegrationTests` and
`GeoServerLiveImportIntegrationTests` require their respective opt-in
environment variables and should be treated as live-service evidence, not the
baseline local proof.
