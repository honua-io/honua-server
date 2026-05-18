# Compatibility and Automated Migration Evidence

Last reviewed: 2026-05-17

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
> migration inventories, validates GeoServer dry-run plans, and produces
> migration evidence artifacts for review before cutover.

Do not yet claim full automated production migration from all GeoServer catalog
items, arbitrary OGC WFS/WMS/WMTS services, private ArcGIS services, or licensed
ArcGIS Pro desktop workflows. Those gaps are tracked below.

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
| ArcGIS GeoServices REST, public queryable layers | Production import path | [Import and Migration Capability Evidence](import-capability-evidence.md); `POST /api/v1/admin/import/geoservices/start`; focused endpoint/scanner tests 47 passed; Postgres inventory/baseline tests 23 passed | Current external parity nightly is failing and must be restored before linking as fresh parity proof: [honua-server#1013](https://github.com/honua-io/honua-server/issues/1013). |
| ArcGIS GeoServices REST, private/authenticated services | Inventory can report auth-required posture, but import is public-source only today | [ArcGIS Migration Inventory Discovery](../operator/arcgis-inventory-discovery.md) documents auth-required artifacts | Add authenticated ArcGIS discovery/import: [honua-server#1017](https://github.com/honua-io/honua-server/issues/1017). |
| GeoServer REST | Automated inventory and dry-run validation only | [Import and Migration Capability Evidence](import-capability-evidence.md); [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md); `POST /api/v1/admin/import/geoserver/start` requires `dryRun=true` | Add applied/non-dry-run GeoServer migration: [honua-server#1015](https://github.com/honua-io/honua-server/issues/1015). |
| OGC WFS/WMS/WMTS services | Compatibility and consume evidence exist; production service import is not implemented | [Cross-Server Consume Gap Report](../compatibility/cross-server-consume-gap-report.md); OGC CITE evidence above | Add OGC service migration importers: [honua-server#1016](https://github.com/honua-io/honua-server/issues/1016). |
| Migration artifact chain | Core artifact contracts exist for inventory, manifest, parity evidence, and cutover readiness | [Migration Toolkit](../operator/migration-toolkit.md); Core tests cover manifest translation and parity evidence generation | Managed admin persistence/orchestration and broader UI/SDK workflows remain downstream work; SDK evidence gap is tracked by [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| SDK-driven migration automation | SDK migration toolkit issues are closed in SDK repos, but the central SDK compatibility run does not yet exercise migration flows | [SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md); closed SDK tickets: [JS#105](https://github.com/honua-io/honua-sdk-js/issues/105), [.NET#134](https://github.com/honua-io/honua-sdk-dotnet/issues/134), [Python#49](https://github.com/honua-io/honua-sdk-python/issues/49) | Add live migration flows to SDK compatibility evidence: [honua-server#1018](https://github.com/honua-io/honua-server/issues/1018). |
| Esri app migration automation | REST-stub and Esri Leaflet automation exist; licensed ArcGIS Pro desktop automation is not current proof | [Cross-Client Certification Evidence](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) documents `arcgis-stub` and pending licensed-runner render/style checks | Add licensed ArcGIS Pro automation evidence: [honua-server#1019](https://github.com/honua-io/honua-server/issues/1019). |

## Public Evidence Bar

Use these rules when deciding whether a claim is ready for website copy:

- A standards claim needs a passing conformance or compatibility run for the
  exact standard/profile named.
- A client compatibility claim needs a current passing client lane or a clear
  manual evidence artifact for the named client.
- A migration claim needs an operator-facing path that either imports/applies
  changes or explicitly generates a migration artifact showing what is not
  automated.
- A source-specific migration claim must name the supported source family:
  public ArcGIS GeoServices REST, authenticated ArcGIS, GeoServer REST, OGC
  WFS/WMS/WMTS, or Esri desktop app workflows.
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
| [#1019 Add licensed ArcGIS Pro automation evidence](https://github.com/honua-io/honua-server/issues/1019) | Needed before claiming automated migration proof for Esri desktop apps, beyond REST stubs. |
