# Metadata v2 Admin UI Information Model

This is a Claude Design handoff derived from
[honua-server#1046](https://github.com/honua-io/honua-server/issues/1046) and
the Metadata v2 epic,
[honua-server#1035](https://github.com/honua-io/honua-server/issues/1035).
GitHub remains authoritative for scope and acceptance. This file translates the
issue intent into a design brief for workflow, navigation, labels, and UI state.
Use [Honua Admin Operator Workflows](admin-operator-workflows.md) for the wider
server management workflow map, including imports, jobs, service management,
connections, security, CORS, settings, and BlueSpatial workflow references.

## Design Intent

The admin UI should make Metadata v2 navigable through workflows, not through
raw schema editing. Users should be able to connect to data, create or inspect a
data resource, describe it once, publish it to services or catalogs, control
access, and validate readiness across standards.

The dominant mental model is:

Catalog Workspace -> Connections -> Data Resources -> Source, Fields,
Metadata, Publish, Access, Validation -> Services and Catalogs.

Supporting schema objects can exist behind these workflows, but the main UI
should keep users focused on what they are trying to manage.

## UI Vocabulary

Use these as primary interface terms:

- Connections
- Data Resources
- Source
- Fields
- Metadata
- Publish
- Access
- Validation
- Readiness
- Projection Preview
- Advanced Overrides

Forbidden as primary UI terms:

- storageBinding
- projectionProfile
- ABAC
- canonical graph
- distribution object
- policy condition
- runtime snapshot

These forbidden terms may appear only in advanced diagnostics, developer
tooling, raw JSON inspection, or support documentation where the internal object
name is necessary.

## Top Navigation

1. Dashboard
2. Connections
3. Data Resources
4. Services
5. Publishing
6. Access
7. Validation
8. Settings

Top navigation should favor operational scanning. Avoid marketing-style
overview pages; the first screen should show actionable status, recent changes,
validation blockers, and publishing readiness.

## Data Resources List

Recommended columns:

| Column | Purpose |
|---|---|
| Name | Human display name and stable identity cue |
| Type | Feature dataset, raster dataset, table, tile dataset, process, style, document, or external resource |
| Source | Connection and source summary |
| Metadata | Completeness state |
| Published to | Service and catalog targets |
| Access | Preset or custom access summary |
| Validation | Ready, warning, blocked, or not applicable |
| Modified | Last meaningful authoring change |

Primary actions:

- Create data resource
- Filter by type, source, validation, access, and publish target
- Open validation blockers
- Open projection preview

## Resource Detail Tabs

1. Overview
2. Source
3. Fields
4. Metadata
5. Publish
6. Access
7. Validation
8. Advanced

### Overview

Show identity, lifecycle state, source summary, metadata completeness, publish
status, access summary, and validation state. The Overview tab should answer:
"What is this resource, where does it come from, where is it published, and what
blocks readiness?"

### Source

Show connection, selected table/file/API asset, detected capabilities, health,
and source-specific warnings. Keep credential details behind secret references
and do not expose resolved secrets.

### Fields

Recommended field columns:

| Column | Purpose |
|---|---|
| Field name | Source field identifier |
| Display name | User-facing label |
| Type | Data type |
| Role | Primary ID, geometry, display name, temporal field, owner, status, category, asset URL, license, quality flag |
| Required | Whether this field is required for selected targets |
| Query | Query exposure |
| Edit | Edit exposure |
| Sensitive | Visibility or masking state |
| Standard bindings | Advanced mapping summary |

The default field UI should expose simple roles first and advanced bindings
second.

### Metadata

Sections:

- Basic description
- Themes and keywords
- Publisher and contacts
- License and rights
- Dates
- Spatial extent
- Temporal extent
- Quality and lineage
- Links
- Distributions

Metadata editing should show completeness by selected readiness target instead
of asking users to edit one standard-specific document at a time.

### Publish

Show publication targets and compatibility:

| Target | Example Status | Common Blocker |
|---|---|---|
| OGC API Features collection | Ready | None |
| WFS feature type | Blocked | Source is not queryable |
| WMS layer | Ready | None |
| WMTS layer | Warning | Tile settings incomplete |
| GeoServices FeatureServer layer | Ready | None |
| GeoServices MapServer layer | Ready | None |
| GeoServices ImageServer raster | Warning | Raster source metadata incomplete |
| OData entity set | Ready | None |
| STAC collection | Warning | Missing license |
| DCAT dataset/distribution | Warning | Missing publisher identifier |
| OGC Records record | Ready | None |
| Esri catalog/portal item | Warning | Missing thumbnail or owner details |

### Access

Access presets:

- Public read
- Organization read
- Private
- Publisher edit
- Admin only
- Custom

Preset summaries should be human readable. Raw policy JSON is advanced-only.

### Validation

Show blockers and warnings grouped by workflow area, with a direct route to the
tab that can fix each issue.

### Advanced

Advanced should contain raw object inspection, schema diagnostics, projection
debugging, and override tools. It should not become the default path for normal
resource authoring.

## Create Resource Flow

1. Choose source type.
2. Select or create connection.
3. Pick table, file, API asset, or external source.
4. Inspect schema and capabilities.
5. Confirm resource identity.
6. Add required metadata.
7. Choose access preset.
8. Choose publishing targets.
9. Review validation.
10. Create as draft or publish.

The flow should support draft creation before all publish targets are ready.
Validation should distinguish blockers from warnings and from missing optional
metadata.

## Publish Resource Flow

1. Choose target service or catalog.
2. Review compatibility.
3. Configure path, layer name, collection name, or item identity.
4. Review field exposure.
5. Review metadata projection.
6. Validate.
7. Publish.

Publishing should show a projection preview before commit. The preview should be
available as a drawer or side panel so users can compare canonical metadata with
target output without leaving the workflow.

## Validation Center

Validation is both a global workspace view and a resource-level tab.

Global Validation Center groups:

- Source
- Schema
- Metadata
- Publishing
- Security
- Standards
- Cache/runtime

Readiness targets:

- OGC Records
- DCAT
- STAC
- ISO 19115
- Esri catalog/item
- GeoServices REST
- OGC API
- OData

Validation states:

- Ready
- Warning
- Blocked
- Not applicable

Each validation row should identify the resource, target, issue, severity, and
fix location. Rows should support filtering by severity, target, resource type,
and publish target.

## Claude Design Deliverables

- Information architecture map
- Dashboard layout
- Connections list and detail page
- Data Resources list
- Data Resource detail page with tabs
- Create Resource wizard
- Publish Resource flow
- Server operator workflow map from
  [Honua Admin Operator Workflows](admin-operator-workflows.md)
- Publishing matrix
- Access preset and policy UI
- Validation Center
- Projection Preview drawer
- Empty, loading, warning, blocked, and success states

## Visual Direction

This is an operational admin product. Use dense tables, tabs, side panels,
segmented controls, status badges, inline validation, and projection preview
drawers. Avoid decorative landing pages and avoid making raw schema editing the
primary workflow.
