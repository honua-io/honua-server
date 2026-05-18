# Migration Toolkit

The migration toolkit is the operator workflow for moving GIS services into
Honua without treating discovery as proof of cutover readiness. It uses a
deterministic artifact chain:

1. `MigrationSourceInventoryArtifact` captures what the source advertised.
2. `MigrationManifestArtifact` translates that inventory into target Honua
   intent and explicit manual-review or unsupported items.
3. `MigrationParityEvidenceArtifact` summarizes capability, style, data, and
   cutover-readiness evidence.

These artifacts are stable intermediate contracts for the migration epic
(honua-server#646). The manifest slice is tracked by honua-server#651, and
the parity and evidence-pack slice is tracked by honua-server#652.

## Stable Contract Catalogue

The migration toolkit contracts below are the stable admin and SDK handoff
surface tracked by honua-server#880:

| Contract | Artifact kind and version | Owning source |
|---|---|---|
| Source inventory | `honua.migration.source-inventory` v1.0 | `src/Honua.Core/Features/Import/Domain/MigrationSourceInventoryArtifact.cs` |
| Migration manifest | `honua.migration.manifest` v1.0 | `src/Honua.Core/Features/Import/Domain/MigrationManifestArtifact.cs` |
| Parity evidence pack | `honua.migration.parity-evidence-pack` v1.0 | `src/Honua.Core/Features/Import/Domain/MigrationParityEvidenceArtifact.cs` |
| Cutover readiness summary | embedded in `honua.migration.parity-evidence-pack` v1.0 as `cutoverReadiness` | `src/Honua.Core/Features/Import/Domain/MigrationParityEvidenceArtifact.cs` |

The admin HTTP surface currently exposed for this artifact chain is
`POST /api/v1/admin/import/scan`, with `?export=json` returning indented JSON
as an attachment. By default the endpoint returns
`MigrationSourceInventoryArtifact`. Set request field `artifactSet` to `all`
to return an envelope containing the source inventory, generated
`MigrationManifestArtifact`, and generated `MigrationParityEvidenceArtifact`.
The manifest and parity evidence remain deterministic planning artifacts; this
route does not mutate the target catalog or copy source data by itself.

## Artifact Flow

Run the source scanner first. GeoServer REST, ArcGIS GeoServices REST, and OGC
service scans produce the same top-level source inventory contract, so
downstream review tooling can work against one artifact shape.

GeoServer source inventories emit advertised WMS/WFS service endpoints as
external dependencies, preserve GeoServer layer capabilities on resources, and
link styles by REST metadata URL plus SLD content URL instead of embedding raw
SLD bodies. Setting `includeStyleContent` lets the scanner inspect SLD/SE
documents for compatibility warnings and external graphic dependencies; the
inventory remains a deterministic planning artifact, not a style translation
payload.

Translate the reviewed inventory into a manifest before planning a pilot.
The manifest does not claim that unsupported source items are migrated:
incompatible resources are excluded from `targetResources` and emitted under
`unsupportedItems`; partially compatible resources are emitted with the
`manual-review` action and a matching `manualReviewItems` entry.

Generate parity evidence after the manifest has been produced. A missing
manifest leaves data parity `unknown`; it is never treated as a pass.

## OGC Service Migration Scope

OGC consume compatibility and OGC migration are separate claims. Cross-server
consume probes prove Honua can read reference OGC services; they are not proof
that a source has been imported or that cutover is ready.

For OGC sources, use `sourceKind` values `ogc-wfs`, `ogc-wms`, or `ogc-wmts`.
The first implemented data-copy planning path is WFS. WFS 2.0.0, 1.1.0, and
1.0.0 scans read GetCapabilities, enumerate feature types, and attempt
DescribeFeatureType so the inventory can emit fields, geometry type, CRS
metadata, capabilities, manifest targets, and parity evidence.

WMS and WMTS scans are metadata and planning paths only. They capture layers,
styles, tile matrix sets, and service endpoints where advertised, but render
and tile services are marked manual-review or unsupported for automated data
copy unless paired with a WFS, coverage, database, or file source. This keeps
render compatibility distinct from applied migration.

## State Values

Every parity and readiness item uses one of these state values:

| State | Meaning |
|---|---|
| `pass` | Evidence is present and satisfies the check. |
| `fail` | Evidence is present and shows the check failed. |
| `unknown` | Evidence is missing or not yet reviewed. |
| `not-applicable` | The check does not apply to this migration. |

Overall state aggregation is conservative: any `fail` makes the evidence
pack fail; otherwise any `unknown` keeps the evidence pack unknown. Missing
operator attestations therefore block cutover readiness until they are
recorded explicitly.

## Manifest Review

Use the manifest as the technical planning contract before pilot migration:

- Review `targetResources` for target service and resource names.
- Review `styleActions` before importing or recreating styles.
- Resolve every `manualReviewItems` entry or record a waiver.
- Remove or route around every `unsupportedItems` entry before cutover.

The manifest carries copied field, capability, style, dependency, and spatial
reference identifiers from the inventory. It does not mutate server catalog
state by itself.

## Parity Evidence

The parity evidence pack groups generated checks into:

- `capability`: source capabilities and unsupported source behaviors.
- `style`: source style/renderer portability.
- `data`: manifest target evidence for each migratable source resource.
- `cutoverReadiness`: operator attestations required before traffic moves.

Operator attestations are supplied separately as
`MigrationReadinessAttestation` JSON. Use
[migration-cutover-readiness-template.json](examples/migration-cutover-readiness-template.json)
as the starting point and keep items `unknown` until evidence exists.

## Pilot And Cutover

Use the Markdown checklist in
[Migration Pilot And Cutover Checklist](migration-pilot-cutover-checklist.md)
for human review, and keep the JSON readiness attestation with the migration
artifacts so automated checks can consume it later. Use
[migration-cutover-readiness-template.json](examples/migration-cutover-readiness-template.json)
as the empty starting point and
[migration-cutover-readiness-example.json](examples/migration-cutover-readiness-example.json)
as a deterministic reference for a completed attestation.

The generated readiness checklist contains these stable item IDs:

| ID | Purpose |
|---|---|
| `inventory-confirmed` | Source inventory has been reviewed with the source owner. |
| `manifest-reviewed` | Target manifest actions and gaps have been reviewed. |
| `parity-report-reviewed` | The generated evidence pack has been reviewed. |
| `known-gaps-accepted` | Fail or unknown items have explicit approval or waiver. |
| `rollback-plan-documented` | Rollback or traffic restoration plan is documented. |
| `traffic-switch-planned` | DNS, load-balancer, or client endpoint change is scheduled. |

## Rollback Notes

The `rollback-plan-documented` readiness item asserts that a rollback plan
exists for the source system and the customer traffic path. This section
describes what that plan must record. Honua does not execute the rollback;
execution lives in customer- and team-owned runbooks outside this server slice.

A complete migration rollback plan must record:

- **Restore point**: Database snapshot or backup identifier and the timestamp
  the snapshot was taken. Confirm the snapshot is retained through the cutover
  validation window.
- **Source traffic reversion**: DNS, load-balancer, or API gateway steps that
  return traffic to the source system, including the expected propagation
  window for each change.
- **Cache invalidation**: CDN, tile cache, or client cache purge required after
  reversion so clients do not retain Honua-served responses for source-served
  routes.
- **Escalation path**: Named owner and after-hours contact for each dependent
  team (source owner, application owner, network or DNS owner, on-call lead).
- **Rollback timing**: Latest acceptable point-of-no-return before cutover
  proceeds, and the maximum validation window after cutover during which
  rollback remains the documented response to a regression.
- **Decision owner**: The single named individual who authorises rollback
  execution, with a documented backup if that individual is unavailable.

Record the link to the rollback plan document in the `evidence` field of the
`rollback-plan-documented` readiness item. Do not mark the item `pass` until
the linked document covers all six points above.

## Admin And SDK Follow-Up

The server contracts in this slice are the stabilized handoff point for
admin UI and SDK work tracked by honua-server#880. Downstream UX should use
the artifact contracts above instead of inventing source-specific manifest
or readiness shapes.

Repo-owned downstream implementation tickets:

| Surface | Ticket | Scope |
|---|---|---|
| Admin UI | honua-io/honua-server-admin#79 | Trigger inventory scans, render inventory review, and display manifest, parity evidence, and readiness artifacts. |
| JavaScript/TypeScript SDK | honua-io/honua-sdk-js#105 | Add scan methods and typed artifact models. |
| .NET SDK | honua-io/honua-sdk-dotnet#134 | Add scan methods, typed artifact models, and source-generated JSON support where applicable. |
| Python SDK | honua-io/honua-sdk-python#49 | Add scan methods and typed artifact models. |

Server implementation remains unblocked by these downstream tickets. If an
admin UI or SDK workflow needs managed manifest, parity, readiness persistence,
job orchestration, or another server route, file a new bounded `honua-server`
child issue instead of expanding the downstream ticket.

## Non-Goals

This slice does not perform live source mutation, Honua catalog publishing,
data copying, traffic switching, admin UI implementation, SDK release work,
or managed cutover orchestration. Those remain separate implementation
tickets under the migration epic.

## Related Docs

- [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md)
- [ArcGIS Migration Inventory Discovery](arcgis-inventory-discovery.md)
- [SLD Migration Reference](sld-migration.md)
