# ArcGIS Migration Inventory Discovery

Honua's migration toolkit begins with a deterministic discovery pass against an
ArcGIS GeoServices REST source (FeatureServer or MapServer). The discovery
slice produces a normalized JSON inventory artifact that operators and
downstream automation can review before any data is migrated.

This document describes what the discovery slice does, how to invoke it, what
the artifact contains, and how the slice fits into the larger migration
workflow.

## Non-goals for this slice

The discovery slice intentionally stops at producing a reviewable inventory.
The following are tracked separately in honua-server#646 child tickets:

- GeoServer source scanning beyond the existing surface (separate ticket).
- Manifest translation (turning the inventory into target Honua catalog
  records).
- Parity / evidence reports that compare source and target after a pilot
  migration.
- Pilot checklist execution and production cutover orchestration.
- Admin UI screens, SDK surface, and managed-runtime integrations.

If you need any of those capabilities, file or follow the appropriate child
ticket — do not extend the discovery scanner.

## When to run discovery

Run discovery as the first step of any planned ArcGIS-to-Honua migration:

1. **Plan**: Capture the inventory and use it as the input artifact for
   migration planning conversations.
2. **Review**: Walk through the artifact with the source-system owner to
   confirm coverage, identify auth gaps, and surface manual-review items.
3. **Translate** (separate ticket): Convert the reviewed inventory into a
   target manifest.
4. **Pilot** (separate ticket): Migrate a representative subset and produce a
   parity report.
5. **Cutover** (separate ticket): Promote the pilot or migrate at scale once
   pilot evidence is approved.

The discovery slice is safe to run repeatedly: it performs only read-only
GeoServices REST calls and never mutates source state.

## Endpoint

```
POST /api/v1/admin/import/scan
POST /api/v1/admin/import/scan?export=json
```

The endpoint requires admin authorization. The scanner adapts to the
canonical migration import abstractions and shares all cross-cutting
behavior (validation, error mapping, telemetry) with the GeoServer scan
path.

### Request body

```json
{
  "sourceKind": "geoservices",
  "sourceUrl": "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
  "timeoutSeconds": 30
}
```

`sourceKind` accepts `geoservices` or `arcgis-geoservices-rest`. Service URLs
must be HTTPS service-root URLs (FeatureServer or MapServer); the validator
rejects layer-specific URLs and disallowed network ranges.

### Response shapes

| Form | Behavior |
|---|---|
| `POST /api/v1/admin/import/scan` | Returns the inventory artifact as compact JSON. |
| `POST /api/v1/admin/import/scan?export=json` | Returns the inventory artifact as **indented JSON** with `Content-Disposition: attachment; filename="<service-slug>-inventory.json"`. Suitable for committing to a migration project repository. |

The export filename is derived from the source `displayName`; the slug is
sanitized to alphanumeric, dash, and underscore characters and capped at 64
characters. Credentials supplied in the request body are never echoed into
the artifact.

### Auth-required sources

When the source rejects anonymous discovery, the artifact is still returned
with HTTP 200 but flagged:

- `authPosture.mode = "auth-required"`,
- `scanCompleteness.status = "failed"`, and
- `overallCompatibility.code` set to a stable
  [compatibility code](#compatibility-codes) such as
  `ARCGIS_TOKEN_REQUIRED` or `ARCGIS_ACCESS_DENIED`.

Operators should add credentials and rerun. The discovery slice does **not**
attempt token acquisition itself.

## Artifact shape

The artifact is a `MigrationSourceInventoryArtifact`. The top-level fields
are:

| Field | Purpose |
|---|---|
| `artifactKind` | Constant `honua.migration.source-inventory`. |
| `artifactVersion` | Schema version (`1.0` for this slice). |
| `sourceKind` | `arcgis-geoservices-rest` for ArcGIS sources. |
| `source` | Service identity, version, and protocol subtype. |
| `authPosture` | Whether the scan ran anonymously or with credentials, and whether access was confirmed. |
| `scanCompleteness` | `complete`, `partial`, or `failed`, plus warnings and missing artifacts. |
| `summary` | Counts: containers, resources, styles, dependencies, plus compatibility tallies. |
| `overallCompatibility` | Aggregate `level` + `code` + `reason` + remediation guidance. |
| `containers` | Logical groupings (services, workspaces) with their own compatibility. |
| `resources` | Layer/table inventory with geometry, capabilities, fields, spatial references, related styles, and external dependencies. |
| `styles` | Renderer entries and their portability classification. |
| `externalDependencies` | Datastores, attachments, external symbol URLs, and similar planning concerns. |

### Resource fields

Each resource records the source schema in a `fields` array:

```json
{
  "name": "ZONING",
  "alias": "Zoning",
  "fieldType": "esriFieldTypeString",
  "nullable": true,
  "domainType": "codedValue",
  "domainName": "ZoningCode",
  "domainValues": [
    { "code": "C1", "name": "Commercial 1" },
    { "code": "R1", "name": "Residential 1" }
  ]
}
```

- `nullable` is `null` when the source omits the property (older ArcGIS
  versions).
- `domainValues` is bounded; coded-value domains exceeding the cap drop
  the values rather than truncating silently and emit a
  `scanCompleteness.warnings[]` entry prefixed with the
  `ARCGIS_DOMAIN_TRUNCATED:` stable code so automation can branch on the
  prefix. The warning carries the domain name when known and falls back to
  the field name otherwise.
- Fields are sorted alphabetically by name to keep artifacts diff-stable.

## Compatibility codes

Stable, machine-readable codes accompany every classifiable assessment so
downstream automation can branch deterministically. The code namespace for
this slice is the constants in
`Honua.Core.Features.Import.Domain.ImportCompatibilityCodes`.

| Code | Level | Meaning | Remediation |
|---|---|---|---|
| `COMPATIBLE` | compatible | Resource can be queried and migrated as-is. | None. |
| `MANUAL_REVIEW` | partial | Renderer needs to be recreated via Honua style endpoints. | Recreate the renderer after data import. |
| `ARCGIS_EXTERNAL_SYMBOL` | partial | Renderer references external symbol URLs. | Mirror or replace external symbol assets. |
| `ARCGIS_ATTACHMENTS` | partial | Layer advertises attachments. | Plan a separate attachment migration. |
| `ARCGIS_MISSING_SPATIAL_REF` | partial | Spatial reference metadata was unavailable. | Confirm CRS, datum, and units before migrating. |
| `ARCGIS_DOMAIN_TRUNCATED` | warning | Coded-value domain exceeded the deterministic capture cap. | Re-import the domain manually if the full list is needed. |
| `ARCGIS_QUERY_CAPABILITY_MISSING` | incompatible | Layer does not advertise `Query`. | Enable query access or export source data through another path. |
| `ARCGIS_UNSUPPORTED_GEOMETRY` | incompatible | Geometry type is not supported by the import path. | Normalize or export to a supported vector geometry type. |
| `ARCGIS_UNSUPPORTED_RENDERER` | incompatible | Renderer cannot be portably translated. | Rebuild an equivalent target style manually. |
| `ARCGIS_MIXED_RENDERERS` | partial | Service mixes supported and unsupported renderer types across layers. | Address each layer's renderer code independently. |
| `ARCGIS_TOKEN_REQUIRED` | partial | Source returned 401/498/499 — credentials are required. | Provide a token or credentials and rerun the scan. |
| `ARCGIS_ACCESS_DENIED` | partial | Source returned 403 — supplied identity lacks access. | Confirm the identity has read access and rerun. |
| `ARCGIS_SERVICE_ERROR` | partial | Source returned a generic non-auth error. | Verify reachability, access, and metadata exposure. |

`overallCompatibility.code` is omitted on aggregate paths where no single
code applies — automation should fall back to `level` in that case.

## Determinism guarantees

- Property order in the JSON artifact is stable and tied to record
  declaration order in `Honua.Core.Features.Import.Domain`.
- Resources, styles, dependencies, and field arrays are sorted by stable
  identifiers/names.
- External URLs are normalized to a secret-safe form (no userinfo, query, or
  fragment) before they appear in the artifact.
- IDs use deterministic prefixes (`service:`, `resource:`, `renderer:`,
  `dependency:`) plus the source service key.

These guarantees mean that successive scans of the same source produce
byte-identical artifacts and that downstream tooling can rely on stable
diffs.

## Local validation and CI

- Normal CI does **not** call live ArcGIS Server endpoints. Scanner
  classification tests run against committed JSON fixtures under
  `tests/dotnet/Honua.Postgres.Tests/Features/Import/Fixtures/ArcGis/` and
  compare scanner output against committed baselines under
  `Features/Import/Baselines/ArcGis/`.
- Baselines can be regenerated when the artifact model intentionally
  changes by running the test suite with
  `UPDATE_ARCGIS_INVENTORY_BASELINES=1`.

## Related work

- Parent epic: honua-io/honua-server#646
- This slice: honua-io/honua-server#877
- Related style work: honua-io/honua-server#375
- SLD style migration: [SLD Migration Reference](sld-migration.md)
- GeoServer → Honua tutorial: see `docs/gis/tutorials/`
