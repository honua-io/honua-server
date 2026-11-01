# Compatibility and Automated Migration Evidence

Last reviewed: 2026-07-25 (Round 3)

This page is the evidence index for Honua's first two website claims:

1. **Compatibility**: existing GIS clients, SDKs, and standards-based tooling
   can connect to Honua.
2. **Automated migration**: operators can move services from GeoServices REST,
   GeoServer, and OGC sources into Honua with automation.

The two claims are related but not interchangeable. Standards compliance and
client compatibility prove Honua can serve migrated workloads. Importers,
source inventories, parity reports, SDK automation, and app-level smoke tests
prove operators can migrate into that compatible surface.

## Website-Safe Claim Wording

Use this wording until the open backlog items below are complete:

> Honua provides standards-based compatibility across OGC APIs, classic OGC
> services, GeoServices REST, OData, STAC, tiles, SDKs, and client workflows.
> Its migration tooling inventories supported sources, imports queryable service
> layers into PostGIS, applies reviewed GeoServer manifests, classifies service
> fidelity across attachments, relationships, renderers, labels, and time
> metadata, and records post-migration parity. A release-gated acceptance suite
> covers scan, manifest, apply, parity, and readiness stages. Supported vector
> geoprocessing workflows run through OGC API Processes and GeoServices GPServer.
> Migration performance evidence records duration, throughput, retry/resume,
> idempotency, and manual-review-ratio metrics across representative fixtures.

Do not claim source workflows that are not implemented, including private-source
parity, full rendering or coverage migration, and SDK automation without a passing
compatibility artifact. Broader Python geoprocessing migration remains issue-scoped.
SDK-driven migration evidence is not yet in the central compatibility matrix;
track it through [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018).

## Claim 1: Compatibility

| Evidence area | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| OGC CITE standards compliance | Partial: 1137/1138 current trunk evidence | [OGC CITE Conformance Evidence](ogc-cite-conformance-evidence.md); latest WFS receipt: [33376911053](https://github.com/honua-io/honua-server/actions/runs/33376911053), 166/167 because multi-layer `rollbackOnFailure=true` transactions are rejected; other suite profiles retain their all-pass evidence from aggregate run 33205558805 | Multi-layer WFS transaction rollback semantics. |
| GeoServices REST parity | Passing current trunk evidence | [GeoServices REST Parity](../../reference/compatibility/geoservices-parity.md); baseline scorecard has 10 service cases and 110/110 applicable checks passing; latest passing run: [26155341221](https://github.com/honua-io/honua-server/actions/runs/26155341221) (2026-05-20), with two earlier consecutive green runs [26090743833](https://github.com/honua-io/honua-server/actions/runs/26090743833) (2026-05-19) and [26028722114](https://github.com/honua-io/honua-server/actions/runs/26028722114) (2026-05-18) | None for the listed parity scorecard cases. |
| Cross-server OGC consume | Passing latest nightly against reference GeoServer/MapServer sources, with known MapServer WMTS reference-source gaps | [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md); latest passing run: [25986591739](https://github.com/honua-io/honua-server/actions/runs/25986591739) | MapServer WMTS requires a MapCache-backed reference source; currently reported as open gaps in the report. |
| Real-client interop | Lane jobs queued for recovery: the docker-compose `--no-build` flag rejection (root cause for every lane crashing) is removed, the diff step self-diagnoses missing envelopes per lane, and the pyqgis lane's `rpds-py` distro-package uninstall conflict that was preventing its Docker build is fixed via `--ignore-installed` overlay | [Cross-Client Certification Evidence](../../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md); [Cross-Client Certification Gap Report](../../gis/gap-report.md); fixes shipped in PRs #1142 and #1143 plus the Round 3 pyqgis Dockerfile fix | Verify the next scheduled nightly run produces full lane envelopes across all five lanes and link a passing run before closing [honua-server#1014](https://github.com/honua-io/honua-server/issues/1014). |
| SDK/server compatibility | Passing current server/SDK compatibility matrix for supported cells; explicit SDK migration evidence schema now defined and ready for SDK repos to implement against | [SDK Compatibility Matrix](../../concepts/ecosystem.md); [SDK Migration Evidence Manifest](../developer/sdk-migration-evidence-manifest.md); latest passing run: [25668006533](https://github.com/honua-io/honua-server/actions/runs/25668006533), 9/9 supported cells passing | SDK repos still need to emit per-cell migration evidence into the matrix per the manifest contract: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |

## Claim 2: Automated Migration

| Source family | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| GeoServices REST, public and credentialed queryable layers | Production import path | [Import and Migration Capability Evidence](import-capability-evidence.md); `POST /api/v1/admin/import/geoservices/start`; focused endpoint/scanner tests cover credential redaction and secret-reference queuing; current external parity nightly is passing on trunk with the linked run above | None for the public queryable layer import path. |
| GeoServices REST service-fidelity migration | Fidelity matrix + manifest identity remap + attachments/relationships classification + renderer/label diagnostics + post-migration parity probes + admin evidence endpoints on trunk | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServices REST Parity](../../reference/compatibility/geoservices-parity.md); service-fidelity tests in the import test suites; merged via PRs [#1075](https://github.com/honua-io/honua-server/pull/1075), [#1103](https://github.com/honua-io/honua-server/pull/1103), [#1139](https://github.com/honua-io/honua-server/pull/1139) closing [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025) | Authenticated private-source parity evidence still depends on [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017); SceneServer/I3S, Network Analyst, Utility Network remain explicitly out of scope. |
| GeoServices REST, private/authenticated services | Focused token/OAuth/Basic credential plumbing is implemented for discovery, inventory, and queued layer import; broader private-service parity remains issue-scoped | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServices REST Migration Inventory Discovery](../../guides/migrate/from-geoservices-rest.md) documents auth posture artifacts | Complete full private-service parity and external evidence under [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017). |
| GeoServer REST | Applied migration: catalog manifest application, data-source + feature-data copy, style persistence with conversion diagnostics, deterministic evidence pack + nightly fixture, and admin orchestration endpoints for migration runs | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServer to Honua Migration Guide](../../guides/migrate/from-geoserver.md); `POST /api/v1/admin/import/geoserver/start` emits `honua.migration.apply-plan` and `honua.migration.apply-execution` artifacts; `/api/v1/admin/migration/runs` lists/inspects/cancels runs and serves evidence packs; nightly fixture: `.github/workflows/nightly-migration-evidence.yml`; merged via PRs [#1095](https://github.com/honua-io/honua-server/pull/1095), [#1107](https://github.com/honua-io/honua-server/pull/1107), [#1140](https://github.com/honua-io/honua-server/pull/1140) closing [honua-server#1015](https://github.com/honua-io/honua-server/issues/1015) | Layer-group, WMS/WFS/WMTS service-exposure mutations, and non-PostGIS data-source migration remain recorded as manual-review/unsupported per the per-source caveat; pre-existing trunk PG-compat flake on `GeoServerImportServiceStyleApplyTests` must clear before citing a current nightly pass. |
| Classic OGC WFS/WMS/WMTS services | Applied migration with deterministic evidence pack: WFS data import + WMS/WMTS metadata, style, and tile-cache planning (XYZ/TMS tile-cache export from migration plans), bundled into a SHA-256-fingerprinted `OgcMigrationEvidencePackArtifact` | `POST /api/v1/admin/import/scan` (`sourceKind=ogc-wfs|ogc-wms|ogc-wmts`); `OgcMigrationEvidencePackBuilder` aggregates per-source inventory + manifest + apply-plan + parity into one deterministic pack; classification tests in `tests/dotnet/Honua.Core.Tests/Features/Import/OgcMigrationEvidencePackBuilderTests.cs`; slices forward-ported via the Round 3 batch | Operator-driven end-to-end runs against external sources remain manual; deeper render/tile parity probes against live tile pyramids are out of scope. |
| OGC API Features services | Collection import + inline feature-count parity probe + cql2 filter / bbox / datetime pushdown + schema-mapping diagnostics + deterministic `OgcApiFeaturesMigrationEvidencePackArtifact` bundling inventory/manifest/parity | `POST /api/v1/admin/import/ogc-api-features/collection`; `OgcApiFeaturesImportService` with filter pushdown and schema-mapping diagnostics; `OgcApiFeaturesMigrationEvidencePackBuilder` + tests; OGC API Features CITE evidence remains the serving-compatibility proof; slices forward-ported via the Round 3 batch | Heavier per-feature parity probes (sampled geometry, attribute hashing) and live end-to-end operator runs remain under [honua-server#1029](https://github.com/honua-io/honua-server/issues/1029). |
| OGC coverage services | WCS + OGC API Coverages importers on trunk with classified visualization-metadata diagnostics for vendor-specific encodings/missing style refs and a deterministic `OgcCoverageMigrationEvidencePackArtifact` aggregating slices 1-4 | `POST /api/v1/admin/import/ogc-wcs/import` and `POST /api/v1/admin/import/ogc/coverages/import`; `CoverageStyleDiagnosticBuilder` + `OgcCoverageMigrationEvidencePackBuilder` with tests under `tests/dotnet/Honua.Core.Tests/Features/Import/`; slice 5 forward-ported via the Round 3 batch | Live operator-driven runs against external coverage services and live data parity remain under [honua-server#1030](https://github.com/honua-io/honua-server/issues/1030). |
| Migration artifact chain and review workflow | Core artifact contracts exist for inventory, manifest, parity evidence, and cutover readiness; operator review/cutover workbench is not yet complete | [Migration Toolkit](../../guides/migrate/from-geoservices-rest.md); Core tests cover manifest translation and parity evidence generation | Managed admin persistence/orchestration and broader UI/SDK workflows remain downstream work; SDK evidence gap is tracked by [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018), and low-risk operator review/cutover UI belongs in [honua-console](https://github.com/honua-io/honua-console). |
| SDK-driven migration automation | SDK migration toolkit issues are closed in SDK repos, but the central SDK compatibility run does not yet exercise migration flows | [SDK Compatibility Matrix](../../concepts/ecosystem.md); closed SDK tickets: [JS#105](https://github.com/honua-io/honua-sdk-js/issues/105), [.NET#134](https://github.com/honua-io/honua-sdk-dotnet/issues/134), [Python#49](https://github.com/honua-io/honua-sdk-python/issues/49) | Add live migration flows to SDK compatibility evidence: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |

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
  public and authenticated GeoServices REST, GeoServer REST,
  classic OGC WFS/WMS/WMTS, OGC API Features, OGC coverage services, process
  workloads, or desktop client workflows.
- A parity claim must link to a current parity report or state that only a
  committed baseline exists.

## Backlog Summary

| Issue | Why it blocks stronger website language |
|---|---|
| [#1014 Restore real-client interop nightly evidence](https://github.com/honua-io/honua-server/issues/1014) | Needed before citing current client compatibility matrix evidence. |
| [#1016 Add OGC service migration importers](https://github.com/honua-io/honua-server/issues/1016) | Needed before claiming automated migration from arbitrary OGC WFS/WMS/WMTS services. |
| [#1017 Support authenticated GeoServices REST import](https://github.com/honua-io/honua-server/issues/1017) | Needed before claiming enterprise/private service migration. |
| [#1018 Add SDK migration automation evidence](https://github.com/honua-io/honua-server/issues/1018) | Needed before claiming SDK-driven migration automation as release evidence. |
| [honua-sdk-js#205 Add a Honua JS MapLibre migration target](https://github.com/honua-io/honua-sdk-js/issues/205) | Needed before claiming JavaScript apps can be quickly ported to Honua-native MapLibre applications instead of compatibility/fallback targets. |
| [honua-sdk-js#206 Add paired sample app and service migration integration coverage](https://github.com/honua-io/honua-sdk-js/issues/206) | Needed before claiming real JavaScript sample workflows can be ported end to end, including both referenced services and app code. |
| [#1029 Add OGC API Features source migration importer](https://github.com/honua-io/honua-server/issues/1029) | Needed before claiming automated migration from modern OGC API Features services. |
| [#1030 Add OGC coverage service migration importers](https://github.com/honua-io/honua-server/issues/1030) | Needed before claiming automated migration from WCS or OGC API Coverages sources. |
| [honua-console](https://github.com/honua-io/honua-console) migration review and cutover workbench | Needed before claiming a low-risk operator workflow with review, approvals, parity evidence, redaction, and cutover readiness. |

### Recently delivered (kept for traceability)

| Issue | Delivered via | Lifts |
|---|---|---|
| [#1015 Apply GeoServer REST migration plans](https://github.com/honua-io/honua-server/issues/1015) | PRs [#1095](https://github.com/honua-io/honua-server/pull/1095), [#1107](https://github.com/honua-io/honua-server/pull/1107), [#1140](https://github.com/honua-io/honua-server/pull/1140) | Full applied GeoServer migration (catalog, data sources/copy, styles, evidence pack, admin orchestration) for PostGIS-backed feature layers. |
| [#1024 Add automated migration acceptance evidence suite](https://github.com/honua-io/honua-server/issues/1024) | PRs [#1093](https://github.com/honua-io/honua-server/pull/1093), [#1108](https://github.com/honua-io/honua-server/pull/1108), [#1136](https://github.com/honua-io/honua-server/pull/1136) | Release-gated scan → manifest → apply → parity → readiness pipeline runners over deterministic fixtures. |
| [#1031 Add concrete GP execution and result evidence](https://github.com/honua-io/honua-server/issues/1031) | PRs [#1094](https://github.com/honua-io/honua-server/pull/1094), [#1109](https://github.com/honua-io/honua-server/pull/1109), [#1137](https://github.com/honua-io/honua-server/pull/1137) | Concrete vector process executor set (buffer, clip, intersect, project, area, union, centroid, length, convex hull, dissolve, simplify, snap) plus result-route evidence. |
| [#1033 Add migration cost and performance evidence](https://github.com/honua-io/honua-server/issues/1033) | PRs [#1092](https://github.com/honua-io/honua-server/pull/1092), [#1110](https://github.com/honua-io/honua-server/pull/1110), [#1138](https://github.com/honua-io/honua-server/pull/1138) | Release-gated `honua.migration.performance-evidence` artifact with metric schema, S/M/L fixture sizing, baseline thresholds, retry/resume/idempotency, and admin endpoints. |
