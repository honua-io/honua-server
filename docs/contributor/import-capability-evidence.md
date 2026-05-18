# Import and Migration Capability Evidence

Last reviewed: 2026-05-18

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
> GeoServer dry-run validation, emits classic OGC WFS/WMS/WMTS scan, manifest,
> parity, and fidelity-classification artifacts for operator review, and runs
> cross-server WMS/WFS/WMTS consume tests against reference GeoServer and
> MapServer services.

Do not currently claim applied catalog/data migration from WMS/WMTS render
services or non-dry-run GeoServer catalog migration. The classic OGC path is an
operator-facing planning path: WFS feature types can produce feature-import
manifest targets, while WMS/WMTS produce explicit service plans, unsupported
data-copy items, and manual-review classifications. Do not use broad
low-risk/cost automated migration language until the release-gated acceptance
evidence suite in
[honua-server#1024](https://github.com/honua-io/honua-server/issues/1024) is
passing and linked from the compatibility evidence page.

## Current Capability Matrix

| Capability | Status | Evidence | Public caveat |
|---|---|---|---|
| File import | Production path | `POST /api/v1/admin/import/upload`; `SupportedFileFormat` includes GeoJSON, Shapefile, GeoPackage, GPX, KML, GML, WKT, CSV, TinyWKB, FileGDB, FlatGeobuf, and GeoParquet | This is file/object import, not service migration. |
| Raster import | Production path | `POST /api/v1/admin/import/raster`; GeoTIFF/COG and PNG/JPEG world-file paths | Raster import is separate from coverage-service import. |
| ArcGIS GeoServices REST discovery | Production path | `POST /api/v1/admin/import/geoservices/discover` | Requires an HTTPS service-root URL ending in `FeatureServer` or `MapServer`; layer URLs, embedded credentials, and credential query parameters are rejected. Token/OAuth/Basic credentials are accepted through the `credentials` object for synchronous discovery only. |
| ArcGIS GeoServices REST layer import | Production path | `POST /api/v1/admin/import/geoservices/start`; background job uses paged `/query` reads, creates a PostGIS table, inserts attributes/geometries, builds a spatial index, and can auto-publish | Queryable public and credentialed feature/map-service layers are supported. Queued credentialed imports must use secret references; plaintext tokens/passwords are not persisted. Attachments and renderers are inventoried or flagged for manual follow-up, not imported as first-class data. |
| ArcGIS Server service-fidelity migration | Partial / gap | Current layer import and GeoServices parity evidence | Stable layer identity, domains/subtypes, relationships, attachments, renderers/styles, time metadata, service metadata, route parity, and post-migration parity evidence are tracked in [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025). |
| Unified migration scanner | Production path | `POST /api/v1/admin/import/scan` with `sourceKind=geoserver-rest` or `sourceKind=arcgis-geoservices-rest` | The scanner returns deterministic planning artifacts. It does not mutate catalog or data tables. |
| GeoServer REST discovery | Production path | `POST /api/v1/admin/import/geoserver/discover` and scanner support | HTTPS public URL required outside test mode. Basic auth is supported when both username and password are supplied. |
| GeoServer REST import job | Dry-run validation plus apply-plan generation | `POST /api/v1/admin/import/geoserver/start` supports `dryRun=true` validation and `dryRun=false` deterministic apply-plan jobs | Non-dry-run jobs emit replayable intent only. They do not yet mutate the Honua catalog, copy data, or persist migrated styles. |
| GeoServer SLD migration | Partial supporting path | Admin SLD import/export endpoints and `ISldStyleConverter` integration | Bulk GeoServer import validates/converts SLD content for diagnostics, but per-layer style persistence is handled by the admin SLD endpoint. |
| Classic OGC WFS/WMS/WMTS service migration planning | First operator-facing scanner slice | `POST /api/v1/admin/import/scan` with `sourceKind=ogc-wfs`, `ogc-wms`, or `ogc-wmts`; `artifactSet=all` returns inventory, manifest, and parity evidence; Core scanner tests cover WFS feature types plus WMS/WMTS render/tile manual-review classifications | WFS is a feature-import planning path only. WMS/WMTS are metadata/style/tile/service-plan paths and are marked unsupported for automated data copy unless paired with WFS, coverage, database, or file sources. Track further applied migration work in [honua-server#1016](https://github.com/honua-io/honua-server/issues/1016). |
| OGC API Features service import | Not implemented | Honua serves OGC API Features but does not yet import external OGC API Features sources | Track source scan/import/parity evidence in [honua-server#1029](https://github.com/honua-io/honua-server/issues/1029). |
| OGC coverage service import | Not implemented | Raster file import exists, but WCS/OGC API Coverages source migration is not an operator path | Track WCS/OGC API Coverages migration in [honua-server#1030](https://github.com/honua-io/honua-server/issues/1030). |
| Cross-server OGC consume | Test/nightly evidence | Test-only `/__test/cross-server-consume/proxy`; nightly `cross-server-consume-nightly.yml`; gap report at `docs/compatibility/cross-server-consume-gap-report.md` | The probe exists only in the Test environment and should not be presented as an operator API. |
| End-to-end migration acceptance suite | Not implemented as one release gate | Existing evidence is split across source-specific tests, SDK compatibility, and artifact unit tests | Track the full scan/manifest/apply/parity/readiness suite in [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024). |
| Operator review and cutover workbench | Not complete | Stable artifact contracts can be displayed by downstream UI | Track review, approvals, parity evidence, redaction, retries, exports, and cutover readiness in [honua-server-admin#94](https://github.com/honua-io/honua-server-admin/issues/94). |
| Migration cost/performance evidence | Not implemented as release evidence | No current artifact measures duration, throughput, source request counts, retry/resume behavior, resource use, or manual-review ratio | Track measured cost/performance evidence in [honua-server#1033](https://github.com/honua-io/honua-server/issues/1033). |
| GP/process migration execution evidence | First-slice scaffold | GPServer and OGC API Processes expose concrete vector process ids/result routes, with classification and fixture artifact contracts in [Process Migration Evidence](process-migration-evidence.md) | Populate passing execution/parity evidence before broader process portability claims. Track server evidence in [honua-server#1031](https://github.com/honua-io/honua-server/issues/1031), paired with Python SDK ArcPy migration in [honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59). |

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
The public `start` endpoint supports `dryRun=true` validation and
`dryRun=false` deterministic apply-plan generation. Non-dry-run jobs surface a
`honua.migration.apply-plan` artifact with a replay token, ordered steps,
manual-review items, and unsupported items; they do not yet mutate the Honua
catalog, copy source data, or persist migrated styles.

### OGC Cross-Server Consume

- Test-only proxy: `src/Honua.Server/Features/Import/CrossServerConsumeProbeEndpoints.cs`
- Gap report: `docs/compatibility/cross-server-consume-gap-report.md`
- Workflow inventory: `docs/ci/workflow-inventory.md`
- TestKit guide: `docs/contributor/testkit.md`

The cross-server consume suite validates Honua-as-client reads of WMS 1.3,
WFS 2.0, and WMTS 1.0 from reference GeoServer and MapServer containers. This
is interoperability evidence for consuming OGC services. It is not a production
import feature.

### Classic OGC WFS/WMS/WMTS Migration Planning

- Operator endpoint: `src/Honua.Server/Features/Import/MigrationScannerEndpoints.cs`
- Scanner implementation: `src/Honua.Core/Features/Import/Services/OgcServiceMigrationScanner.cs`
- URL validation: `src/Honua.Server/Features/Import/OgcServiceUrlValidation.cs`
- Focused coverage:
  `tests/dotnet/Honua.Core.Tests/Features/Import/OgcServiceMigrationScannerTests.cs`
  and `tests/dotnet/Honua.Server.Tests/Import/MigrationScannerEndpointTests.cs`

The classic OGC scanner validates a secret-safe HTTPS source URL, reads
GetCapabilities, and emits the shared migration artifact chain. WFS scans
enumerate feature types, attempt DescribeFeatureType, capture CRS/schema
metadata, and generate feature-import manifest targets. WMS scans capture
render layers, styles, GetMap/GetFeatureInfo operation metadata, and explicit
unsupported data-copy classifications. WMTS scans capture tile layers, styles,
GetTile operation metadata, ResourceURL templates, tile matrix sets, and
manual-review tile-service classifications. The path does not apply catalog
changes or copy rendered WMS/WMTS data by itself.

## Review Checklist

Before using import language in marketing, sales, or website copy:

- Use "ArcGIS GeoServices REST layer import" for the production live-service
  import path.
- Use "GeoServer REST migration inventory, dry-run validation, and apply-plan
  generation" for GeoServer.
- Use "classic OGC WFS/WMS/WMTS migration planning artifacts" for the
  `POST /api/v1/admin/import/scan` path.
- Use "cross-server WMS/WFS/WMTS consume testing" for OGC reference-service
  evidence.
- Do not say "applied OGC service migration" unless a production WFS/WMS/WMTS
  apply path has been implemented and tested.
- Do not say "GeoServer catalog import applies changes" until non-dry-run jobs
  write real catalog, data, and style state rather than emitting apply-plan
  evidence only.

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

dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
  --filter "FullyQualifiedName~OgcServiceMigrationScannerTests|FullyQualifiedName~MigrationManifestTranslatorTests|FullyQualifiedName~MigrationParityEvidenceGeneratorTests"

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportServiceScanTests|FullyQualifiedName~GeoServerImportServiceScanTests|FullyQualifiedName~GeoservicesArcGisInventoryBaselineTests|FullyQualifiedName~GeoServerInventoryBaselineTests"
```

External/live tests such as `GeoservicesGeoportalImportIntegrationTests` and
`GeoServerLiveImportIntegrationTests` require their respective opt-in
environment variables and should be treated as live-service evidence, not the
baseline local proof.
