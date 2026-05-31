# Import and Migration Capability Evidence

Last reviewed: 2026-05-20

This page is the website-linkable evidence summary for Honua Server import and
migration claims. It distinguishes production data import from migration
inventory, dry-run planning, and test-only interoperability probes.

For the website-level compatibility and automated migration claims, start with
[Compatibility and Automated Migration Evidence](compatibility-and-migration-evidence.md).

## Public Claim Wording

Use this wording for public material:

> Honua imports common GIS file formats and public or credentialed queryable
> ArcGIS GeoServices REST feature/map-service layers into PostGIS. It inventories
> ArcGIS GeoServices REST and GeoServer REST sources for migration planning,
> validates GeoServer dry-run plans, and applies reviewed GeoServer manifests to
> the Honua catalog with idempotent data-source, feature-data, and style
> persistence plus a release-gated evidence pack. Admin orchestration endpoints
> drive scan -> manifest -> apply -> parity -> readiness migration runs over
> ArcGIS, GeoServer, and OGC API Features fixtures, and the release-gated
> performance-evidence artifact reports duration, throughput, retry/resume,
> idempotency, and manual-review-ratio metrics. ArcGIS service-fidelity migration
> covers attachments and relationships, renderer/label diagnostics, post-migration
> parity probes, and admin endpoints for inspecting ArcGIS evidence. Concrete
> vector geoprocessing executors (buffer, clip, intersect, project, area, union,
> centroid, length, convex hull, dissolve, simplify, snap) are exposed through
> OGC API Processes and GeoServices GPServer with result-artifact persistence.
> Classic OGC WFS/WMS/WMTS migration remains a planning path emitting scan,
> manifest, parity, and fidelity-classification artifacts, and cross-server
> WMS/WFS/WMTS consume tests run against reference GeoServer and MapServer
> services.

Do not currently claim applied catalog/data migration from WMS/WMTS render
services, OGC API Features source migration, WCS/OGC API Coverages source
migration, or private/authenticated ArcGIS sources beyond focused token
plumbing. The classic OGC path is an operator-facing planning path: WFS feature
types can produce feature-import manifest targets, while WMS/WMTS produce
explicit service plans, unsupported data-copy items, and manual-review
classifications. ArcPy / Python GP claims should name the supported vector
process set above; broader ArcPy scan/translate/parity work depends on
[honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59).
SDK-driven migration evidence in the central compatibility matrix is still
tracked under [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018).

## Current Capability Matrix

| Capability | Status | Evidence | Public caveat |
|---|---|---|---|
| File import | Production path | `POST /api/v1/admin/import/upload`; `SupportedFileFormat` includes GeoJSON, Shapefile, GeoPackage, GPX, KML, GML, WKT, CSV, TinyWKB, FileGDB, FlatGeobuf, and GeoParquet | This is file/object import, not service migration. |
| Raster import | Production path | `POST /api/v1/admin/import/raster`; GeoTIFF/COG and PNG/JPEG world-file paths | Raster import is separate from coverage-service import. |
| I3S/.slpk scene-layer import | First slice | `POST /api/v1/admin/import/i3s-slpk` parses an Esri `.slpk` 3D Object Scene Layer (I3S 1.7 compact NodePage), converts each node's geometry to glTF 2.0 binary (`.glb`), writes `tileset.json` rooted at the configured asset directory, and auto-registers the dataset via `ISceneRegistrationService` so it serves through the existing `/scenes/{id}/tileset.json` route. Closes [honua-server#1268](https://github.com/honua-io/honua-server/issues/1268). | Initial slice supports 3D Object layers with WGS-84 spatial reference (WKID 4326/4979) and PerAttributeArray FLOAT32 geometry buffers. Point Cloud, Integrated Mesh, indexed topology, Draco compression, textures, attribute/feature data, and non-WGS84 inputs are deferred. Requires the Postgres-backed scene registration service. |
| ArcGIS GeoServices REST discovery | Production path | `POST /api/v1/admin/import/geoservices/discover` | Requires an HTTPS service-root URL ending in `FeatureServer` or `MapServer`; layer URLs, embedded credentials, and credential query parameters are rejected. Token/OAuth/Basic credentials are accepted through the `credentials` object for synchronous discovery only. |
| ArcGIS GeoServices REST layer import | Production path | `POST /api/v1/admin/import/geoservices/start`; background job uses paged `/query` reads, creates a PostGIS table, inserts attributes/geometries, builds a spatial index, and can auto-publish | Queryable public and credentialed feature/map-service layers are supported. Queued credentialed imports must use secret references; plaintext tokens/passwords are not persisted. Attachments and renderers are inventoried or flagged for manual follow-up, not imported as first-class data. |
| ArcGIS Server service-fidelity migration | Delivered for the four-bucket fidelity matrix | Fidelity matrix + manifest identity remap + attachments/relationships classification + renderer/label diagnostics + post-migration parity probes + admin evidence endpoints (`PostgresArcGisMigrationEvidenceStore`); merged via [#1075](https://github.com/honua-io/honua-server/pull/1075), [#1103](https://github.com/honua-io/honua-server/pull/1103), [#1139](https://github.com/honua-io/honua-server/pull/1139) closing [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025) | Authenticated private-source parity remains tracked by [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017); SceneServer/I3S, Network Analyst, Utility Network remain explicitly out of scope. |
| Unified migration scanner | Production path | `POST /api/v1/admin/import/scan` with `sourceKind=geoserver-rest` or `sourceKind=arcgis-geoservices-rest` | The scanner returns deterministic planning artifacts. It does not mutate catalog or data tables. |
| GeoServer REST discovery | Production path | `POST /api/v1/admin/import/geoserver/discover` and scanner support | HTTPS public URL required outside test mode. Basic auth is supported when both username and password are supplied. |
| GeoServer REST import job | Applied migration path | `POST /api/v1/admin/import/geoserver/start` supports `dryRun=true` validation and `dryRun=false` apply jobs that emit deterministic apply-plan + apply-execution artifacts, persist data sources, copy feature data, and persist styles with conversion diagnostics. `/api/v1/admin/migration/runs` admin endpoints list/inspect/cancel runs and serve evidence packs. Merged via [#1095](https://github.com/honua-io/honua-server/pull/1095), [#1107](https://github.com/honua-io/honua-server/pull/1107), [#1140](https://github.com/honua-io/honua-server/pull/1140) closing [honua-server#1015](https://github.com/honua-io/honua-server/issues/1015). | Layer-group, WMS/WFS/WMTS service-exposure mutations, and non-PostGIS data-source migration remain manual-review/unsupported execution records. Pre-existing trunk PG-compat flake on `GeoServerImportServiceStyleApplyTests` must clear before linking a current nightly pass. |
| GeoServer SLD migration | Partial supporting path | Admin SLD import/export endpoints and `ISldStyleConverter` integration | Bulk GeoServer import validates/converts SLD content for diagnostics, but per-layer style persistence is handled by the admin SLD endpoint. |
| Classic OGC WFS/WMS/WMTS service migration planning | First operator-facing scanner slice | `POST /api/v1/admin/import/scan` with `sourceKind=ogc-wfs`, `ogc-wms`, or `ogc-wmts`; `artifactSet=all` returns inventory, manifest, and parity evidence; Core scanner tests cover WFS feature types plus WMS/WMTS render/tile manual-review classifications | WFS is a feature-import planning path only. WMS/WMTS are metadata/style/tile/service-plan paths and are marked unsupported for automated data copy unless paired with WFS, coverage, database, or file sources. Track further applied migration work in [honua-server#1016](https://github.com/honua-io/honua-server/issues/1016). |
| OGC API Features service import | Not implemented | Honua serves OGC API Features but does not yet import external OGC API Features sources | Track source scan/import/parity evidence in [honua-server#1029](https://github.com/honua-io/honua-server/issues/1029). |
| OGC coverage service import | Not implemented | Raster file import exists, but WCS/OGC API Coverages source migration is not an operator path | Track WCS/OGC API Coverages migration in [honua-server#1030](https://github.com/honua-io/honua-server/issues/1030). |
| Cross-server OGC consume | Test/nightly evidence | Test-only `/__test/cross-server-consume/proxy`; nightly `cross-server-consume-nightly.yml`; gap report at `docs/compatibility/cross-server-consume-gap-report.md` | The probe exists only in the Test environment and should not be presented as an operator API. |
| End-to-end migration acceptance suite | Pipeline runners delivered for scan/apply/parity/readiness over deterministic fixtures | `MigrationAcceptance{Scan,Apply,Parity,Readiness}StageRunner` with integration tests in `MigrationAcceptance*StageTests.cs`. Merged via [#1093](https://github.com/honua-io/honua-server/pull/1093), [#1108](https://github.com/honua-io/honua-server/pull/1108), [#1136](https://github.com/honua-io/honua-server/pull/1136) closing [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024). | Cross-repo SDK migration evidence and paired Esri app + service corpus still feed the suite; pre-existing trunk PG-compat flake must clear before linking a release-gated passing run. |
| Operator review and cutover workbench | Not complete | Stable artifact contracts can be displayed by downstream UI | Track review, approvals, parity evidence, redaction, retries, exports, and cutover readiness in [honua-server-admin#94](https://github.com/honua-io/honua-server-admin/issues/94). |
| Migration cost/performance evidence | Release-gated `honua.migration.performance-evidence` artifact on trunk | Metric schema, S/M/L fixture sizing, baseline thresholds, retry/resume/idempotency, and admin endpoints under `/api/v1/admin/migration/performance-evidence`. Release workflow: `.github/workflows/release-migration-performance.yml`. Merged via [#1092](https://github.com/honua-io/honua-server/pull/1092), [#1110](https://github.com/honua-io/honua-server/pull/1110), [#1138](https://github.com/honua-io/honua-server/pull/1138) closing [honua-server#1033](https://github.com/honua-io/honua-server/issues/1033). | Seeded baselines cover initial GeoServer S/M/L sizes; additional source-family baselines fill in as their importers populate the suite. |
| GP/process migration execution evidence | Concrete executable vector process set delivered | Buffer, clip, intersect, project, area, union, centroid, length, convex hull, dissolve, simplify, snap exposed through OGC API Processes and GeoServices GPServer with result artifacts. Per-executor tests under `tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/Geometry*JobExecutorTests.cs` plus `VectorProcessParityIntegrationTests`. Merged via [#1094](https://github.com/honua-io/honua-server/pull/1094), [#1109](https://github.com/honua-io/honua-server/pull/1109), [#1137](https://github.com/honua-io/honua-server/pull/1137) closing [honua-server#1031](https://github.com/honua-io/honua-server/issues/1031). See [Process Migration Evidence](process-migration-evidence.md). | ArcPy / Python GP scan/translate/runner and end-to-end ArcPy parity remain in [honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59); GeoServer WPS / OGC API Processes source-process migration is explicitly out of scope. |

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
  `tests/dotnet/Honua.Postgres.Tests/Features/Import/GeoservicesImportServiceAuthenticatedImportTests.cs`,
  `GeoservicesParityIntegrationTests.cs`, and
  `GeoservicesGeoportalImportIntegrationTests.cs`

The importer validates a public HTTPS ArcGIS service root, discovers layer
metadata, queues a distributed import job, pages source features with
`resultOffset`/`resultRecordCount`, creates the target table in PostGIS,
converts Esri JSON geometries to GeoJSON/PostGIS geometry, creates a spatial
index, analyzes the table, and optionally publishes the imported table as a
Honua layer.

Credentialed import uses the GeoServices `credentials` descriptor. Discovery
and scan requests may use inline token/password values for immediate use, but
queued import jobs require secret references; the worker resolves the secret
inside job execution before calling ArcGIS. Local integration coverage includes
a token-protected ArcGIS-compatible fixture that pages a private layer into
PostGIS and verifies request/job artifacts do not persist token material.

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
`dryRun=false` deterministic apply-plan generation plus a bounded
`honua.migration.apply-execution` artifact. Non-dry-run jobs surface a
`honua.migration.apply-plan` artifact with a replay token, ordered steps,
manual-review items, and unsupported items, then apply the safe subset that can
be expressed as idempotent Honua catalog publication for PostGIS-backed layers
whose source tables already exist in the target database. Existing catalog
layers are recorded as idempotent replays. Data copy, layer groups,
WMS/WFS/WMTS exposure changes, and bulk style persistence remain manual-review
or unsupported execution records.

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
- Use "GeoServer REST migration inventory, dry-run validation, and bounded
  PostGIS-backed catalog apply" for GeoServer.
- Use "classic OGC WFS/WMS/WMTS migration planning artifacts" for the
  `POST /api/v1/admin/import/scan` path.
- Use "cross-server WMS/WFS/WMTS consume testing" for OGC reference-service
  evidence.
- Do not say "applied OGC service migration" unless a production WFS/WMS/WMTS
  apply path has been implemented and tested.
- Do not say "full GeoServer automated migration" until non-dry-run jobs also
  copy source data, publish layer groups/service exposure changes, persist bulk
  styles, and produce release-gated parity evidence.

## Last Local Verification

The focused baseline checks were refreshed on 2026-05-17:

| Check | Result |
|---|---:|
| Server endpoints and migration scanner slice | 47 passed, 0 failed, 0 skipped |
| Postgres service inventory and baseline slice | 23 passed, 0 failed, 0 skipped |

Additional authenticated GeoServices checks were refreshed on 2026-05-18:

| Check | Result |
|---|---:|
| ArcGIS REST client auth and secret-redaction slice | 10 passed, 0 failed, 0 skipped |
| Token-protected private GeoServices import fixture | 1 passed, 0 failed, 0 skipped |

## Suggested Verification Commands

Run these focused checks when refreshing this evidence:

```bash
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportEndpointTests|FullyQualifiedName~GeoServerImportEndpointTests|FullyQualifiedName~MigrationScannerEndpointTests"

dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
  --filter "FullyQualifiedName~OgcServiceMigrationScannerTests|FullyQualifiedName~MigrationManifestTranslatorTests|FullyQualifiedName~MigrationParityEvidenceGeneratorTests"

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportServiceScanTests|FullyQualifiedName~GeoServerImportServiceScanTests|FullyQualifiedName~GeoservicesArcGisInventoryBaselineTests|FullyQualifiedName~GeoServerInventoryBaselineTests"

dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
  --filter "FullyQualifiedName~ArcGisRestClientSecurityTests"

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
  --filter "FullyQualifiedName~GeoservicesImportServiceAuthenticatedImportTests"
```

External/live tests such as `GeoservicesGeoportalImportIntegrationTests` and
`GeoServerLiveImportIntegrationTests` require their respective opt-in
environment variables and should be treated as live-service evidence, not the
baseline local proof.
