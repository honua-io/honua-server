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

## Artifact Flow

Run the source scanner first. GeoServer REST and ArcGIS GeoServices REST
scans produce the same top-level source inventory contract, so downstream
review tooling can work against one artifact shape.

Translate the reviewed inventory into a manifest before planning a pilot.
The manifest does not claim that unsupported source items are migrated:
incompatible resources are excluded from `targetResources` and emitted under
`unsupportedItems`; partially compatible resources are emitted with the
`manual-review` action and a matching `manualReviewItems` entry.

Generate parity evidence after the manifest has been produced. A missing
manifest leaves data parity `unknown`; it is never treated as a pass.

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
artifacts so automated checks can consume it later.

The generated readiness checklist contains these stable item IDs:

| ID | Purpose |
|---|---|
| `inventory-confirmed` | Source inventory has been reviewed with the source owner. |
| `manifest-reviewed` | Target manifest actions and gaps have been reviewed. |
| `parity-report-reviewed` | The generated evidence pack has been reviewed. |
| `known-gaps-accepted` | Fail or unknown items have explicit approval or waiver. |
| `rollback-plan-documented` | Rollback or traffic restoration plan is documented. |
| `traffic-switch-planned` | DNS, load-balancer, or client endpoint change is scheduled. |

## Admin And SDK Follow-Up

The server contracts in this slice are the stabilized handoff point for
admin UI and SDK work tracked by honua-server#880. Downstream UX should use
the artifact contracts above instead of inventing source-specific manifest
or readiness shapes.

## Non-Goals

This slice does not perform live source mutation, Honua catalog publishing,
data copying, traffic switching, admin UI implementation, SDK release work,
or managed cutover orchestration. Those remain separate implementation
tickets under the migration epic.

## Related Docs

- [GeoServer to Honua Migration Guide](../gis/tutorials/geoserver-migration-guide.md)
- [ArcGIS Migration Inventory Discovery](arcgis-inventory-discovery.md)
- [SLD Migration Reference](sld-migration.md)
