# Compatibility and Automated Migration Evidence

Last reviewed: 2026-05-21

This page is the evidence index for Honua's first two website claims:

1. **Compatibility**: existing GIS clients, SDKs, and standards-based tooling
   can connect to Honua.
2. **Automated migration**: operators can move services from Esri/ArcGIS,
   GeoServer, and OGC sources into Honua with automation.

The two claims are related but not interchangeable. Standards compliance and
client compatibility prove Honua can serve migrated workloads. Importers,
source inventories, parity reports, SDK automation, and app-level smoke tests
prove operators can migrate into that compatible surface.

## Website-Safe Claim Wording

Use this wording until the open backlog items below are complete:

> Honua provides standards-based compatibility across OGC APIs, classic OGC
> services, GeoServices REST, OData, STAC, tiles, SDKs, and client workflows.
> Its automated migration tooling imports public, queryable ArcGIS GeoServices
> REST layers into PostGIS, generates deterministic ArcGIS and GeoServer
> migration inventories, applies reviewed GeoServer manifests to the Honua
> catalog with idempotent data-source, feature-data, and style persistence,
> classifies ArcGIS service fidelity across attachments, relationships,
> renderers, labels, time metadata, and post-migration parity probes, drives
> a release-gated scan -> manifest -> apply -> parity -> readiness acceptance
> suite over deterministic ArcGIS, GeoServer, and OGC API Features fixtures,
> executes a deterministic vector geoprocessing set (buffer, clip, intersect,
> project, area, union, centroid, length, convex hull, dissolve, simplify,
> snap) through OGC API Processes and GeoServices GPServer, emits classic OGC
> WFS/WMS/WMTS migration planning artifacts, exposes admin orchestration
> endpoints for migration runs and ArcGIS/performance evidence, and publishes
> a release-gated `honua.migration.performance-evidence` artifact with
> measured duration, throughput, retry/resume, idempotency, and
> manual-review-ratio metrics across small/medium/large fixtures.

Do not yet claim applied WMS/WMTS render-service migration, private ArcGIS
services, licensed ArcGIS Pro desktop workflows, OGC API Features source
migration, or WCS/OGC API Coverages source migration. Process portability
claims should name the supported vector process set above; broader ArcPy /
Python GP claims depend on the SDK-side scanner/translator/runner work tracked
in [honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59).
SDK-driven migration evidence is not yet in the central compatibility matrix —
add it via [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018).
Paired Esri-sample-app-and-service migration evidence is tracked in
[honua-sdk-js#206](https://github.com/honua-io/honua-sdk-js/issues/206).
Those gaps are tracked below.

## Claim 1: Compatibility

| Evidence area | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| OGC CITE standards compliance | Passing current trunk evidence | [OGC CITE Conformance Evidence](ogc-cite-conformance-evidence.md); latest passing run: [26005533282](https://github.com/honua-io/honua-server/actions/runs/26005533282), 952 passed, 0 failed, 0 skipped, 0 CantTell | None for the listed public CITE suite set. |
| GeoServices REST parity | Passing current trunk evidence | [GeoServices REST Parity](../gis/geoservices-rest-parity.md); baseline scorecard has 10 ArcGIS service cases and 110/110 applicable checks passing; latest passing run: [26155341221](https://github.com/honua-io/honua-server/actions/runs/26155341221) (2026-05-20), with two earlier consecutive green runs [26090743833](https://github.com/honua-io/honua-server/actions/runs/26090743833) (2026-05-19) and [26028722114](https://github.com/honua-io/honua-server/actions/runs/26028722114) (2026-05-18) | None for the listed parity scorecard cases. |
| Cross-server OGC consume | Passing latest nightly against reference GeoServer/MapServer sources, with known MapServer WMTS reference-source gaps | [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md); latest passing run: [25986591739](https://github.com/honua-io/honua-server/actions/runs/25986591739) | MapServer WMTS requires a MapCache-backed reference source; currently reported as open gaps in the report. |
| Real-client interop | Lane jobs queued for recovery: the docker-compose `--no-build` flag rejection that caused every lane to exit before producing envelopes is removed, and the diff step now self-diagnoses missing envelopes per lane | [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md); [Cross-Client Certification Gap Report](../gis/gap-report.md); lane-crash root cause + envelope-collection diagnostics shipped in PRs #1142 and the follow-up batch | Verify the next scheduled nightly run produces full lane envelopes and link a passing run; deeper individual lane crashes (e.g., pyqgis pip resolver) remain tracked separately under [honua-server#1014](https://github.com/honua-io/honua-server/issues/1014). |
| SDK/server compatibility | Passing current server/SDK compatibility matrix for supported cells; explicit SDK migration evidence schema now defined and ready for SDK repos to implement against | [SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md); [SDK Migration Evidence Manifest](../developer/sdk-migration-evidence-manifest.md); latest passing run: [25668006533](https://github.com/honua-io/honua-server/actions/runs/25668006533), 9/9 supported cells passing | SDK repos still need to emit per-cell migration evidence into the matrix per the manifest contract: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |

## Claim 2: Automated Migration

| Source family | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| End-to-end migration acceptance evidence | Pipeline runners on trunk for scan, apply, parity, and readiness stages over deterministic ArcGIS, GeoServer, and OGC API Features fixtures | `MigrationAcceptance{Scan,Apply,Parity,Readiness}StageRunner` in `src/Honua.Core/Features/Import/Services/`, with integration tests in `tests/dotnet/Honua.Postgres.Tests/Features/Import/MigrationAcceptance*StageTests.cs`; merged via PRs [#1093](https://github.com/honua-io/honua-server/pull/1093), [#1108](https://github.com/honua-io/honua-server/pull/1108), [#1136](https://github.com/honua-io/honua-server/pull/1136) closing [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024) | Cross-repo SDK migration evidence ([honua-server#1018](https://github.com/honua-io/honua-server/issues/1018)) and paired Esri-app + service migration corpus ([honua-sdk-js#206](https://github.com/honua-io/honua-sdk-js/issues/206)) still feed the acceptance suite. |
| ArcGIS GeoServices REST, public and credentialed queryable layers | Production import path | [Import and Migration Capability Evidence](import-capability-evidence.md); `POST /api/v1/admin/import/geoservices/start`; focused endpoint/scanner tests cover credential redaction and secret-reference queuing; current external parity nightly is passing on trunk with the linked run above | None for the public queryable layer import path. |
| ArcGIS Server service-fidelity migration | Fidelity matrix + manifest identity remap + attachments/relationships classification + renderer/label diagnostics + post-migration parity probes + admin evidence endpoints on trunk | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServices REST Parity](../gis/geoservices-rest-parity.md); ArcGIS fidelity tests under `tests/dotnet/Honua.Postgres.Tests/Features/Import/ArcGis*` and `PostgresArcGisMigrationEvidenceStore`; merged via PRs [#1075](https://github.com/honua-io/honua-server/pull/1075), [#1103](https://github.com/honua-io/honua-server/pull/1103), [#1139](https://github.com/honua-io/honua-server/pull/1139) closing [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025) | Authenticated private-source parity evidence still depends on [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017); SceneServer/I3S, Network Analyst, Utility Network remain explicitly out of scope. |
| ArcGIS GeoServices REST, private/authenticated services | Focused token/OAuth/Basic credential plumbing is implemented for discovery, inventory, and queued layer import; broader private-service parity remains issue-scoped | [Import and Migration Capability Evidence](import-capability-evidence.md); [ArcGIS Migration Inventory Discovery](../operator/arcgis-inventory-discovery.md) documents auth posture artifacts | Complete full private-service parity and external evidence under [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017). |
| GeoServer REST | Applied migration: catalog manifest application, data-source + feature-data copy, style persistence with conversion diagnostics, deterministic evidence pack + nightly fixture, and admin orchestration endpoints for migration runs | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md); `POST /api/v1/admin/import/geoserver/start` emits `honua.migration.apply-plan` and `honua.migration.apply-execution` artifacts; `/api/v1/admin/migration/runs` lists/inspects/cancels runs and serves evidence packs; nightly fixture: `.github/workflows/nightly-migration-evidence.yml`; merged via PRs [#1095](https://github.com/honua-io/honua-server/pull/1095), [#1107](https://github.com/honua-io/honua-server/pull/1107), [#1140](https://github.com/honua-io/honua-server/pull/1140) closing [honua-server#1015](https://github.com/honua-io/honua-server/issues/1015) | Layer-group, WMS/WFS/WMTS service-exposure mutations, and non-PostGIS data-source migration remain recorded as manual-review/unsupported per the per-source caveat; pre-existing trunk PG-compat flake on `GeoServerImportServiceStyleApplyTests` must clear before citing a current nightly pass. |
| Classic OGC WFS/WMS/WMTS services | First operator-facing scanner and planning artifact slice | `POST /api/v1/admin/import/scan` accepts `sourceKind=ogc-wfs`, `ogc-wms`, and `ogc-wmts`; [Import and Migration Capability Evidence](import-capability-evidence.md); OGC CITE and cross-server consume evidence above remain compatibility proof, not import proof | WFS emits feature-import manifest targets after GetCapabilities/DescribeFeatureType discovery. WMS/WMTS emit service plans, style/tile metadata, and unsupported/manual-review classifications for render/tile-only sources. Applied data/catalog migration remains tracked by [honua-server#1016](https://github.com/honua-io/honua-server/issues/1016). |
| OGC API Features services | Collection import + inline feature-count parity probe on trunk; per-import `OgcApiFeaturesFeatureCountParity` reports pass/fail/not-applicable comparing `numberMatched` vs. features written | `POST /api/v1/admin/import/ogc-api-features/collection`; `OgcApiFeaturesImportService` + `OgcApiFeaturesImportServiceTests` exercising the parity probe across pass, fail, and three not-applicable variants; OGC API Features CITE evidence remains the serving-compatibility proof | Heavier per-feature parity probes (sampled geometry, attribute hashing) and end-to-end migration evidence pack continue under [honua-server#1029](https://github.com/honua-io/honua-server/issues/1029). |
| OGC coverage services | WCS + OGC API Coverages importers on trunk with classified visualization-metadata diagnostics for vendor-specific encodings and missing/undecodable style refs | `POST /api/v1/admin/import/ogc-wcs/import` and `POST /api/v1/admin/import/ogc/coverages/import`; `CoverageStyleDiagnosticBuilder` emits `MigrationCoverageStyleDiagnostic` records on both importer result envelopes; classification tests under `tests/dotnet/Honua.Core.Tests/Features/Import/CoverageStyleDiagnosticBuilderTests.cs` | End-to-end coverage migration evidence pack (inventory + manifest + parity + readiness) and post-import data parity probe remain under [honua-server#1030](https://github.com/honua-io/honua-server/issues/1030). |
| Migration artifact chain and review workflow | Core artifact contracts exist for inventory, manifest, parity evidence, and cutover readiness; operator review/cutover workbench is not yet complete | [Migration Toolkit](../operator/migration-toolkit.md); Core tests cover manifest translation and parity evidence generation | Managed admin persistence/orchestration and broader UI/SDK workflows remain downstream work; SDK evidence gap is tracked by [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018), and low-risk operator review/cutover UI is tracked by [honua-server-admin#94](https://github.com/honua-io/honua-server-admin/issues/94). |
| Migration cost/performance evidence | Release-gated `honua.migration.performance-evidence` artifact with metric schema, S/M/L fixture sizing, baseline thresholds, retry/resume/idempotency evidence, and admin endpoints for browsing past records on trunk | [Migration Performance Evidence](../evidence/migration-performance-evidence.md) — schema, fingerprint, redaction posture, and the latest passing run; release workflow `.github/workflows/release-migration-performance.yml`; admin endpoints under `/api/v1/admin/migration/performance-evidence`; merged via PRs [#1092](https://github.com/honua-io/honua-server/pull/1092), [#1110](https://github.com/honua-io/honua-server/pull/1110), [#1138](https://github.com/honua-io/honua-server/pull/1138) closing [honua-server#1033](https://github.com/honua-io/honua-server/issues/1033) | Seeded baselines (`geoserver-small-v1` + retry-resume + the slice-4 fixtures) cover the initial S/M/L sizes; additional source families (ArcGIS, OGC Features, OGC map/tile metadata, coverage) get baselines as their importers populate the suite. |
| SDK-driven migration automation | SDK migration toolkit issues are closed in SDK repos, but the central SDK compatibility run does not yet exercise migration flows | [SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md); closed SDK tickets: [JS#105](https://github.com/honua-io/honua-sdk-js/issues/105), [.NET#134](https://github.com/honua-io/honua-sdk-dotnet/issues/134), [Python#49](https://github.com/honua-io/honua-sdk-python/issues/49) | Add live migration flows to SDK compatibility evidence: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| ArcGIS JS app migration | Extensive `honua-sdk-js` migration tooling exists: scanner, codemod, migration report, parity matrices, WebMap/content conversion, reconciliation, browser smoke, the default Honua Esri-compat target, and an `esri-leaflet` fallback target | Closed tracking issues: [honua-server#324](https://github.com/honua-io/honua-server/issues/324), [#325](https://github.com/honua-io/honua-server/issues/325), [#326](https://github.com/honua-io/honua-server/issues/326), [#384](https://github.com/honua-io/honua-server/issues/384); current SDK source lives in `honua-sdk-js` | The website-preferred target is Honua JS + MapLibre, not only Esri-compat wrappers or Esri Leaflet. Add explicit `honua-maplibre` migration target and evidence: [honua-sdk-js#205](https://github.com/honua-io/honua-sdk-js/issues/205), then surface it through [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| Paired Esri sample app and service migration | Not yet implemented as a repeatable integration corpus | Current evidence has separate app-migration tooling and service-import tests, but no paired proof that a current Esri sample app and its referenced demo services can migrate together | Add curated Esri sample app + referenced service migration integration tests with licensing/terms guardrails: [honua-sdk-js#206](https://github.com/honua-io/honua-sdk-js/issues/206). Feed results into [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024), [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025), and [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| ArcGIS Pro / desktop app automation | REST-stub and Esri Leaflet/browser automation exist; licensed ArcGIS Pro desktop automation now has a manual/scheduled runner scaffold, but no successful licensed run is linked as current proof | [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) documents `arcgis-stub` and the distinct `desktop-arcgis` scaffold; [Licensed ArcGIS Pro Desktop Evidence](../gis/ARCGIS_PRO_LICENSED_EVIDENCE.md) documents runner prerequisites and artifact guardrails | Execute and link a successful licensed ArcGIS Pro evidence run before closing: [honua-server#1019](https://github.com/honua-io/honua-server/issues/1019). |
| Python GP / ArcPy process migration | Server-side: concrete executable vector process set on trunk through OGC API Processes and GeoServices GPServer — buffer, clip, intersect, project, area, union, centroid, length, convex hull, dissolve, simplify, snap — with result artifacts, redaction, and unsupported/manual-review classification for heavyweight raster/destructive process families | [MVP Compatibility and Limitations](../gis/MVP_COMPATIBILITY_CONTRACT.md); [Geoprocess Framework Analysis](../gis/geoprocess-framework-analysis.md); [Process Migration Evidence](process-migration-evidence.md); per-executor tests under `tests/dotnet/Honua.Postgres.Tests/Features/Geoprocessing/Execution/Geometry*JobExecutorTests.cs` plus `VectorProcessParityIntegrationTests`; merged via PRs [#1094](https://github.com/honua-io/honua-server/pull/1094), [#1109](https://github.com/honua-io/honua-server/pull/1109), [#1137](https://github.com/honua-io/honua-server/pull/1137) closing [honua-server#1031](https://github.com/honua-io/honua-server/issues/1031) | ArcPy/Python GP scan/translate/runner and end-to-end ArcPy parity remain in [honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59); GeoServer WPS / OGC API Processes source-process migration is explicitly out of scope for this server claim. |

## Public Evidence Bar

Use these rules when deciding whether a claim is ready for website copy:

- A standards claim needs a passing conformance or compatibility run for the
  exact standard/profile named.
- A client compatibility claim needs a current passing client lane or a clear
  manual evidence artifact for the named client.
- A migration claim needs an operator-facing path that either imports/applies
  changes or explicitly generates a migration artifact showing what is not
  automated.
- A broad low-risk/cost migration claim needs a passing end-to-end acceptance
  suite, not only protocol conformance or per-endpoint import tests.
- A minimal-risk claim needs an operator review path that exposes unsupported
  items, parity failures, approvals, redaction, retries, and cutover readiness.
- A minimal-cost claim needs measured migration duration, throughput, retry,
  resume, resource-use, and manual-review-ratio evidence on representative
  fixtures.
- A source-specific migration claim must name the supported source family:
  public ArcGIS GeoServices REST, authenticated ArcGIS, GeoServer REST,
  classic OGC WFS/WMS/WMTS, OGC API Features, OGC coverage services, process
  workloads, or Esri desktop app workflows.
- A parity claim must link to a current parity report or state that only a
  committed baseline exists.

## Backlog Summary

| Issue | Why it blocks stronger website language |
|---|---|
| [#1014 Restore real-client interop nightly evidence](https://github.com/honua-io/honua-server/issues/1014) | Needed before citing current client compatibility matrix evidence. |
| [#1016 Add OGC service migration importers](https://github.com/honua-io/honua-server/issues/1016) | Needed before claiming automated migration from arbitrary OGC WFS/WMS/WMTS services. |
| [#1017 Support authenticated ArcGIS GeoServices import](https://github.com/honua-io/honua-server/issues/1017) | Needed before claiming enterprise/private ArcGIS source migration. |
| [#1018 Add SDK migration automation evidence](https://github.com/honua-io/honua-server/issues/1018) | Needed before claiming SDK-driven migration automation as release evidence. |
| [honua-sdk-js#205 Add Honua JS MapLibre migration target for ArcGIS JS apps](https://github.com/honua-io/honua-sdk-js/issues/205) | Needed before claiming ArcGIS JS apps can be quickly ported to Honua-native MapLibre applications instead of compatibility/fallback targets. |
| [honua-sdk-js#206 Add paired Esri sample app and service migration integration corpus](https://github.com/honua-io/honua-sdk-js/issues/206) | Needed before claiming real ArcGIS JS sample workflows can be ported end to end, including both referenced services and app code. |
| [#1019 Add licensed ArcGIS Pro automation evidence](https://github.com/honua-io/honua-server/issues/1019) | Needed before claiming automated migration proof for Esri desktop apps, beyond REST stubs; the current scaffold still needs a successful licensed run linked as evidence. |
| [#1029 Add OGC API Features source migration importer](https://github.com/honua-io/honua-server/issues/1029) | Needed before claiming automated migration from modern OGC API Features services. |
| [#1030 Add OGC coverage service migration importers](https://github.com/honua-io/honua-server/issues/1030) | Needed before claiming automated migration from WCS or OGC API Coverages sources. |
| [honua-server-admin#94 Add migration review and cutover workbench](https://github.com/honua-io/honua-server-admin/issues/94) | Needed before claiming a low-risk operator workflow with review, approvals, parity evidence, redaction, and cutover readiness. |
| [honua-sdk-python#59 Add ArcPy/Python GP migration scanner and Honua process runner](https://github.com/honua-io/honua-sdk-python/issues/59) | Needed before claiming existing ArcPy/Python geoprocessing jobs can be ported with automation and parity evidence. |

### Recently delivered (kept for traceability)

| Issue | Delivered via | Lifts |
|---|---|---|
| [#1015 Apply GeoServer REST migration plans](https://github.com/honua-io/honua-server/issues/1015) | PRs [#1095](https://github.com/honua-io/honua-server/pull/1095), [#1107](https://github.com/honua-io/honua-server/pull/1107), [#1140](https://github.com/honua-io/honua-server/pull/1140) | Full applied GeoServer migration (catalog, data sources/copy, styles, evidence pack, admin orchestration) for PostGIS-backed feature layers. |
| [#1024 Add automated migration acceptance evidence suite](https://github.com/honua-io/honua-server/issues/1024) | PRs [#1093](https://github.com/honua-io/honua-server/pull/1093), [#1108](https://github.com/honua-io/honua-server/pull/1108), [#1136](https://github.com/honua-io/honua-server/pull/1136) | Release-gated scan → manifest → apply → parity → readiness pipeline runners over deterministic fixtures. |
| [#1025 Expand ArcGIS Server migration fidelity](https://github.com/honua-io/honua-server/issues/1025) | PRs [#1075](https://github.com/honua-io/honua-server/pull/1075), [#1103](https://github.com/honua-io/honua-server/pull/1103), [#1139](https://github.com/honua-io/honua-server/pull/1139) | ArcGIS service-fidelity beyond queryable-layer import: fidelity matrix, manifest identity, attachments/relationships, renderer/label diagnostics, post-migration parity probes, admin evidence endpoints. |
| [#1031 Add concrete GP execution and result evidence](https://github.com/honua-io/honua-server/issues/1031) | PRs [#1094](https://github.com/honua-io/honua-server/pull/1094), [#1109](https://github.com/honua-io/honua-server/pull/1109), [#1137](https://github.com/honua-io/honua-server/pull/1137) | Concrete vector process executor set (buffer, clip, intersect, project, area, union, centroid, length, convex hull, dissolve, simplify, snap) plus result-route evidence. |
| [#1033 Add migration cost and performance evidence](https://github.com/honua-io/honua-server/issues/1033) | PRs [#1092](https://github.com/honua-io/honua-server/pull/1092), [#1110](https://github.com/honua-io/honua-server/pull/1110), [#1138](https://github.com/honua-io/honua-server/pull/1138) | Release-gated `honua.migration.performance-evidence` artifact with metric schema, S/M/L fixture sizing, baseline thresholds, retry/resume/idempotency, and admin endpoints. |
