# Compatibility and Automated Migration Evidence

Last reviewed: 2026-05-18

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
> migration inventories, validates GeoServer dry-run plans, emits deterministic
> GeoServer apply-plan evidence, and produces migration evidence artifacts for
> review before cutover.

Do not yet claim full automated production migration from all GeoServer catalog
items, arbitrary OGC services, private ArcGIS services, process workloads, or
licensed ArcGIS Pro desktop workflows. Do not claim full ArcGIS Server
service-fidelity migration until service topology, layer identity, domains,
relationships, attachments, renderers/styles, time metadata, and app-facing
parity are covered by
[honua-server#1025](https://github.com/honua-io/honua-server/issues/1025).
Do not claim broad "minimal-risk automated migration from existing ArcGIS
Server, GeoServer, and OGC estates" until the release-gated migration acceptance
evidence suite in
[honua-server#1024](https://github.com/honua-io/honua-server/issues/1024) is
passing and linked from this page. Those gaps are tracked below.

## Claim 1: Compatibility

| Evidence area | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| OGC CITE standards compliance | Passing current trunk evidence | [OGC CITE Conformance Evidence](ogc-cite-conformance-evidence.md); latest passing run: [26005533282](https://github.com/honua-io/honua-server/actions/runs/26005533282), 952 passed, 0 failed, 0 skipped, 0 CantTell | None for the listed public CITE suite set. |
| GeoServices REST parity | Implemented surface with parity docs and committed scorecard baseline; current nightly evidence lane failing | [GeoServices REST Parity](../gis/geoservices-rest-parity.md); baseline scorecard has 10 ArcGIS service cases and 110/110 applicable checks passing | Restore current nightly evidence: [honua-server#1013](https://github.com/honua-io/honua-server/issues/1013). |
| Cross-server OGC consume | Passing latest nightly against reference GeoServer/MapServer sources, with known MapServer WMTS reference-source gaps | [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md); latest passing run: [25986591739](https://github.com/honua-io/honua-server/actions/runs/25986591739) | MapServer WMTS requires a MapCache-backed reference source; currently reported as open gaps in the report. |
| Real-client interop | Lane jobs run, but latest nightly fails because current-run envelopes are not being collected by baseline diff | [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md); [Cross-Client Certification Gap Report](../gis/gap-report.md); latest failing run: [25986671493](https://github.com/honua-io/honua-server/actions/runs/25986671493) | Restore current client evidence collection: [honua-server#1014](https://github.com/honua-io/honua-server/issues/1014). |
| SDK/server compatibility | Passing current server/SDK compatibility matrix for supported cells | [SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md); latest passing run: [25668006533](https://github.com/honua-io/honua-server/actions/runs/25668006533), 9/9 supported cells passing | Migration-specific SDK automation evidence is not yet in the matrix: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |

## Claim 2: Automated Migration

| Source family | Current status | Evidence | Gap/backlog |
|---|---|---|---|
| End-to-end migration acceptance evidence | Not yet release-gated as a single proof suite | Current evidence is slice-level: ArcGIS import tests, GeoServer inventory/dry-run tests, OGC consume tests, SDK compatibility, and migration artifact unit coverage | Add a release-gated scan -> manifest -> apply/dry-run -> publish -> parity -> readiness suite for ArcGIS Server, GeoServer, and OGC sources: [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024). |
| ArcGIS GeoServices REST, public and credentialed queryable layers | Production import path | [Import and Migration Capability Evidence](import-capability-evidence.md); `POST /api/v1/admin/import/geoservices/start`; focused endpoint/scanner tests cover credential redaction and secret-reference queuing | Current external parity nightly is failing and must be restored before linking as fresh parity proof: [honua-server#1013](https://github.com/honua-io/honua-server/issues/1013). |
| ArcGIS Server service-fidelity migration | Partial; current strongest proof is data/layer import, not full service behavior portability | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServices REST Parity](../gis/geoservices-rest-parity.md) | Expand migration fidelity for stable layer identity, domains/subtypes, relationships, attachments, renderers/styles, time metadata, service metadata, route parity, and post-migration parity evidence: [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025). |
| ArcGIS GeoServices REST, private/authenticated services | Focused token/OAuth/Basic credential plumbing is implemented for discovery, inventory, and queued layer import; broader private-service parity remains issue-scoped | [Import and Migration Capability Evidence](import-capability-evidence.md); [ArcGIS Migration Inventory Discovery](../operator/arcgis-inventory-discovery.md) documents auth posture artifacts | Complete full private-service parity and external evidence under [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017). |
| GeoServer REST | Automated inventory, dry-run validation, and non-dry-run apply-plan generation only | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md); `POST /api/v1/admin/import/geoserver/start` can emit a `honua.migration.apply-plan` artifact | Apply-plan jobs do not yet mutate catalog/data/style state. Add applied GeoServer migration: [honua-server#1015](https://github.com/honua-io/honua-server/issues/1015). |
| Classic OGC WFS/WMS/WMTS services | Compatibility and consume evidence exist; production service import is not implemented | [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md); OGC CITE evidence above | Add classic OGC service migration importers: [honua-server#1016](https://github.com/honua-io/honua-server/issues/1016). |
| OGC API Features services | Honua can serve OGC API Features, but external OGC API Features source migration is not yet implemented | OGC API Features CITE evidence above proves serving compatibility, not source migration | Add OGC API Features source migration importer and parity evidence: [honua-server#1029](https://github.com/honua-io/honua-server/issues/1029). |
| OGC coverage services | Honua has raster import and WCS/OGC API Coverages serving surfaces, but external WCS/OGC API Coverages service migration is not yet implemented | [Import and Migration Capability Evidence](import-capability-evidence.md) documents raster file import, not coverage-service migration | Add WCS/OGC API Coverages source migration importers and parity evidence: [honua-server#1030](https://github.com/honua-io/honua-server/issues/1030). |
| Migration artifact chain and review workflow | Core artifact contracts exist for inventory, manifest, parity evidence, and cutover readiness; operator review/cutover workbench is not yet complete | [Migration Toolkit](../operator/migration-toolkit.md); Core tests cover manifest translation and parity evidence generation | Managed admin persistence/orchestration and broader UI/SDK workflows remain downstream work; SDK evidence gap is tracked by [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018), and low-risk operator review/cutover UI is tracked by [honua-server-admin#94](https://github.com/honua-io/honua-server-admin/issues/94). |
| Migration cost/performance evidence | Not yet measured as part of release evidence | Current import tests prove correctness slices, not migration duration, throughput, retry/resume behavior, or resource use | Add cost/performance metrics and thresholds to migration evidence: [honua-server#1033](https://github.com/honua-io/honua-server/issues/1033). |
| SDK-driven migration automation | SDK migration toolkit issues are closed in SDK repos, but the central SDK compatibility run does not yet exercise migration flows | [SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md); closed SDK tickets: [JS#105](https://github.com/honua-io/honua-sdk-js/issues/105), [.NET#134](https://github.com/honua-io/honua-sdk-dotnet/issues/134), [Python#49](https://github.com/honua-io/honua-sdk-python/issues/49) | Add live migration flows to SDK compatibility evidence: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| ArcGIS JS app migration | Extensive `honua-sdk-js` migration tooling exists: scanner, codemod, migration report, parity matrices, WebMap/content conversion, reconciliation, browser smoke, the default Honua Esri-compat target, and an `esri-leaflet` fallback target | Closed tracking issues: [honua-server#324](https://github.com/honua-io/honua-server/issues/324), [#325](https://github.com/honua-io/honua-server/issues/325), [#326](https://github.com/honua-io/honua-server/issues/326), [#384](https://github.com/honua-io/honua-server/issues/384); current SDK source lives in `honua-sdk-js` | The website-preferred target is Honua JS + MapLibre, not only Esri-compat wrappers or Esri Leaflet. Add explicit `honua-maplibre` migration target and evidence: [honua-sdk-js#205](https://github.com/honua-io/honua-sdk-js/issues/205), then surface it through [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| Paired Esri sample app and service migration | Not yet implemented as a repeatable integration corpus | Current evidence has separate app-migration tooling and service-import tests, but no paired proof that a current Esri sample app and its referenced demo services can migrate together | Add curated Esri sample app + referenced service migration integration tests with licensing/terms guardrails: [honua-sdk-js#206](https://github.com/honua-io/honua-sdk-js/issues/206). Feed results into [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024), [honua-server#1025](https://github.com/honua-io/honua-server/issues/1025), and [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| ArcGIS Pro / desktop app automation | REST-stub and Esri Leaflet/browser automation exist; licensed ArcGIS Pro desktop automation is not current proof | [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) documents `arcgis-stub` and pending licensed-runner render/style checks | Add licensed ArcGIS Pro automation evidence: [honua-server#1019](https://github.com/honua-io/honua-server/issues/1019). |
| Python GP / ArcPy process migration | Honua can expose GPServer and OGC API Processes surfaces, and the Python SDK can execute/poll OGC Processes jobs; existing ArcPy script/toolbox migration is not automated or proven | [MVP Compatibility and Limitations](../gis/MVP_COMPATIBILITY_CONTRACT.md); [Geoprocess Framework Analysis](../gis/geoprocess-framework-analysis.md); Python SDK OGC Processes client and migration artifact models | Add ArcPy/Python GP scan, translate, execute, parity, and readiness evidence: [honua-sdk-python#59](https://github.com/honua-io/honua-sdk-python/issues/59). Add server-side concrete process execution/result evidence: [honua-server#1031](https://github.com/honua-io/honua-server/issues/1031). Include the resulting evidence in [honua-server#1024](https://github.com/honua-io/honua-server/issues/1024) and [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |

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
| [#1013 Restore GeoServices parity nightly evidence](https://github.com/honua-io/honua-server/issues/1013) | Needed before citing current Esri import parity as passing evidence. |
| [#1014 Restore real-client interop nightly evidence](https://github.com/honua-io/honua-server/issues/1014) | Needed before citing current client compatibility matrix evidence. |
| [#1015 Apply GeoServer REST migration plans](https://github.com/honua-io/honua-server/issues/1015) | Needed before claiming full automated GeoServer migration. |
| [#1016 Add OGC service migration importers](https://github.com/honua-io/honua-server/issues/1016) | Needed before claiming automated migration from arbitrary OGC WFS/WMS/WMTS services. |
| [#1017 Support authenticated ArcGIS GeoServices import](https://github.com/honua-io/honua-server/issues/1017) | Needed before claiming enterprise/private ArcGIS source migration. |
| [#1018 Add SDK migration automation evidence](https://github.com/honua-io/honua-server/issues/1018) | Needed before claiming SDK-driven migration automation as release evidence. |
| [honua-sdk-js#205 Add Honua JS MapLibre migration target for ArcGIS JS apps](https://github.com/honua-io/honua-sdk-js/issues/205) | Needed before claiming ArcGIS JS apps can be quickly ported to Honua-native MapLibre applications instead of compatibility/fallback targets. |
| [honua-sdk-js#206 Add paired Esri sample app and service migration integration corpus](https://github.com/honua-io/honua-sdk-js/issues/206) | Needed before claiming real ArcGIS JS sample workflows can be ported end to end, including both referenced services and app code. |
| [#1019 Add licensed ArcGIS Pro automation evidence](https://github.com/honua-io/honua-server/issues/1019) | Needed before claiming automated migration proof for Esri desktop apps, beyond REST stubs. |
| [#1024 Add automated migration acceptance evidence suite](https://github.com/honua-io/honua-server/issues/1024) | Needed before claiming broad low-risk/cost automated migration from existing ArcGIS Server, GeoServer, and OGC estates. |
| [#1025 Expand ArcGIS Server migration fidelity](https://github.com/honua-io/honua-server/issues/1025) | Needed before claiming ArcGIS Server service portability beyond public queryable layer import. |
| [#1029 Add OGC API Features source migration importer](https://github.com/honua-io/honua-server/issues/1029) | Needed before claiming automated migration from modern OGC API Features services. |
| [#1030 Add OGC coverage service migration importers](https://github.com/honua-io/honua-server/issues/1030) | Needed before claiming automated migration from WCS or OGC API Coverages sources. |
| [#1031 Add concrete GP execution and result evidence](https://github.com/honua-io/honua-server/issues/1031) | Needed before claiming migrated GP/process workloads execute and return usable Honua results. |
| [#1033 Add migration cost and performance evidence](https://github.com/honua-io/honua-server/issues/1033) | Needed before claiming minimal-cost migration with measured evidence. |
| [honua-server-admin#94 Add migration review and cutover workbench](https://github.com/honua-io/honua-server-admin/issues/94) | Needed before claiming a low-risk operator workflow with review, approvals, parity evidence, redaction, and cutover readiness. |
| [honua-sdk-python#59 Add ArcPy/Python GP migration scanner and Honua process runner](https://github.com/honua-io/honua-sdk-python/issues/59) | Needed before claiming existing ArcPy/Python geoprocessing jobs can be ported with automation and parity evidence. |
