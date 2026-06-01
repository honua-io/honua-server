# Honua Admin Operator Workflows

This is a Claude Design handoff for the Honua server admin experience. It is
tracked by [honua-server#1057](https://github.com/honua-io/honua-server/issues/1057)
and complements the Metadata v2 admin UI handoff in
[honua-server#1046](https://github.com/honua-io/honua-server/issues/1046).

The Metadata v2 epic,
[honua-server#1035](https://github.com/honua-io/honua-server/issues/1035),
remains authoritative for metadata schema scope. This document maps the
operator workflows that need to exist around that model.

## Design Intent

Honua should give admins one clear operating model:

Connections -> Sources -> Data Resources -> Fields and Metadata -> Services and
Catalogs -> Publishing -> Access -> Validation -> Jobs.

The user should not need to understand the internal Metadata v2 object graph to
complete common work. The UI should let an operator connect to data, create or
import resources, describe them once, publish them to many service and catalog
formats, control access, and monitor long-running work.

## Scope Rules

- BlueSpatial BSCore is workflow evidence, not a schema template.
- Do not copy the legacy folder/service/layer hierarchy as the Honua metadata
  model.
- Tenancy is intentionally out of scope for the first release. Do not show
  workspace, organization, or tenant switching in the primary UI.
- Keep internal terms out of primary navigation and form labels. Terms such as
  `storageBinding`, `projectionProfile`, `ABAC`, `canonical graph`,
  `distribution object`, `policy condition`, and `runtime snapshot` belong only
  in advanced diagnostics or developer inspection.
- Every create or import flow must support draft creation before full publish
  readiness.
- Long-running operations must be jobs with progress, logs, cancellation or
  retry where safe, and resumable status after navigation.

## Legacy Workflow Evidence

BlueSpatial exposed several practical GIS admin workflows that Honua still
needs, even though the target metadata model is different.

| Legacy Evidence | Operator Need | Honua Translation |
|---|---|---|
| `manage-connection-component` lists, tests, saves, edits, and deletes database connections | Operators must manage reusable data connections without exposing secrets | Connections list/detail with provider, endpoint, database/schema, secret reference, health, and usage |
| `add-layer-from-database-component` selects a table and generates a layer | Operators must create a resource from an existing table | Create Data Resource from Table wizard |
| `add-layer-from-file-component` uploads GIS files and creates layers asynchronously | Operators must create resources from files with format detection and progress | Create Data Resource from File import job |
| `import-service-modal-component` discovers ArcGIS MapServer/FeatureServer layers from a URL | Operators must import remote Esri services as many Honua resources | Import Esri Service wizard with discovery, selection, mapping, materialization choice, and job progress |
| `create-service-modal-component` configures service type, connection, CRS, scale, records, WMS, tiles, and anonymous access | Operators must create service and catalog endpoints from publishable resources | Services and Catalogs detail pages with publishing targets and runtime settings |
| `layer-field-component` edits field aliases, inclusion, editability, domains, display field, filters, and time awareness | Operators must define field meaning and exposure once | Fields tab with roles, aliases, domains, query/edit exposure, temporal role, display role, and standard bindings in advanced view |
| `create-renderer-component`, labels, HTML popups, related records, events, and versions | Operators need presentation and behavior controls around resources | Styles, Labels, Popups, Relationships, Events, and History tabs or panels |
| `build-tiles-component` creates and clears tile caches with per-level status | Operators need cache build and invalidation controls | Tile cache jobs with selected levels, extents, storage estimate, progress, and invalidation |
| user, Azure AD, CORS, settings, license, and about components | Operators need server administration outside metadata authoring | Access, Auth Providers, CORS, Settings, License, and About sections |

Reference repository:
[BlueSpatial BSCore](https://github.com/mikemcdougall/BlueSpatial/tree/BSCore).

## Primary Users

| User | Goals | Common Entry Points |
|---|---|---|
| Server admin | Configure server, connections, auth, CORS, license, and operational health | Dashboard, Connections, Access, Settings, Jobs |
| GIS publisher | Create resources, edit fields, metadata, styles, and publishing targets | Data Resources, Imports, Services and Catalogs, Publishing, Validation |
| Data steward | Improve metadata completeness, domains, field roles, lineage, and rights | Data Resources, Metadata, Fields, Validation |
| Support operator | Diagnose blocked imports, failed jobs, broken connections, and publish errors | Dashboard, Jobs, Validation, Advanced diagnostics |

## Navigation Model

1. Dashboard
2. Connections
3. Data Resources
4. Imports
5. Services and Catalogs
6. Publishing
7. Jobs
8. Access
9. Validation
10. Settings

The first screen should be operational: recent jobs, blocked validations,
degraded connections, publish status, and quick actions. Avoid a marketing-style
landing page.

## Information Model For Design

| UI Concept | Meaning For The Operator | Metadata Or Runtime Mapping |
|---|---|---|
| Connection | Reusable way to reach a database, object store, file store, or remote API | Provider, endpoint, database/schema/path, secret reference, capability summary, health |
| Source | The concrete table, file, remote layer, raster, tile set, or API asset selected from a connection | Source identity and source-specific capabilities |
| Data Resource | The canonical thing Honua manages and describes once | Resource identity, resource kind, source binding, field model, descriptive metadata, lifecycle state |
| Field | A source attribute with display, query, edit, sensitivity, and semantic role controls | Field name/type, alias, role, domain, exposure, target-specific bindings in advanced view |
| Metadata | Human and standards meaning for the resource | Description, contacts, rights, license, dates, extents, quality, lineage, links, distributions |
| Service or Catalog | Runtime endpoint or catalog surface that can expose one or more resources | GeoServices, OGC API, WMS, WMTS, WFS, OData, STAC, DCAT, OGC Records, Esri catalog |
| Service Layer Slot | A service-local publication position for one resource, especially useful for Esri MapServer/FeatureServer ergonomics | Service id, layer index or route, display name, linked data resource, readiness state |
| Publication | A resource exposed to a target endpoint, service layer slot, or catalog item | Target, path or item identity, field exposure, metadata projection, readiness state |
| Projection Preview | Read-only preview of target output before publish | Derived standard output for OGC, DCAT, STAC, ISO, GeoServices, OData, or Esri catalog |
| Access Preset | Human-readable security choice | Roles, grants, anonymous access, service/resource/field restrictions, advanced policy diagnostics |
| Job | Long-running import, publish, tile, validation, or migration operation | Job type, status, progress, logs, result links, retry/cancel support |
| Validation Result | Readiness issue tied to a fix location | Severity, target, resource, workflow area, blocker or warning, deep link |

The semantic center is the Data Resource, not the service. Canonical source
binding, field roles, descriptive metadata, lineage, rights, and access policy
remain owned by the Data Resource. A resource can publish to many service and
catalog formats. A service or catalog is a target surface, not the owner of the
resource meaning. For multi-layer Esri services, the service can have many
layer slots or publication entries under it; each slot is a publication
representation linked back to one Data Resource.

## Storage, Service, And Catalog Mapping Rules

The admin UI should reinforce these rules:

- Connection explains how Honua reaches storage or an external API.
- Source explains which concrete table, file, raster, tile set, remote layer, or
  API asset the resource comes from.
- Data Resource explains what the thing means and owns the canonical source,
  fields, metadata, lineage, rights, and access controls.
- Field roles explain field semantics once.
- Publication explains where the resource is exposed. In an Esri service this
  may be a service layer slot with a service-local layer id, route, display
  name, renderer, popup, or field exposure override.
- Projection Preview explains how that one resource will look in a specific
  service, catalog, or standard output.
- Runtime snapshots and Redis caches are derived outputs. They should never be
  presented as the user's editable source of truth.

This keeps one storage model able to support many source types and one metadata
model able to support many output formats. For example, the same parcel resource
can originate from a PostGIS table, carry one canonical field and metadata
model, publish to GeoServices FeatureServer and OGC API Features, and project
catalog meaning into OGC Records, DCAT, STAC, and an Esri catalog item.

Target-specific differences belong in the publication or projection preview,
not in duplicated resource metadata. A target may need a collection id, item id,
layer index, route, output format, thumbnail, or field exposure override, but
those choices should not fork the canonical resource meaning.

Service detail pages should still show layers underneath a service for Esri
admin ergonomics. This is a navigation and publication-management view, not a
change in ownership: service layers are slots/publication entries, and edits to
source, fields, metadata, lineage, rights, or canonical access deep-link back to
the linked Data Resource.

## Workflow Matrix

| Workflow | Entry Point | Output | Required States |
|---|---|---|---|
| Create connection | Connections -> New connection | Saved connection with secret reference, health, and usage summary | Empty, testing, valid, warning, failed |
| Create resource from table | Data Resources -> Create -> Database table | Draft data resource with table source and detected fields | Selecting, inspecting, draft, blocked, ready |
| Create resource from file | Imports -> File import or Data Resources -> Create -> File | One or more draft resources and import job result | Uploading, scanning, mapping, importing, complete, failed |
| Import Esri service | Imports -> Esri service | Imported draft resources, optional publications, import job | Discovering, selecting, mapping, importing, partial, failed |
| Create service/catalog endpoint | Services and Catalogs -> New endpoint | Service/catalog target ready for publications | Draft, configured, running, stopped, degraded |
| Publish resource | Resource -> Publish or Publishing matrix | Publication to service/catalog target | Compatible, warning, blocked, published |
| Configure fields/domains | Resource -> Fields | Field roles, aliases, domains, exposure, sensitivity | Clean, changed, invalid, saved |
| Configure metadata | Resource -> Metadata | Standards-ready descriptive metadata | Incomplete, warning, ready |
| Configure styles/labels/popups | Resource -> Presentation | Default map presentation and identify behavior | Draft, previewing, saved |
| Build tile cache | Service -> Tiles or Jobs -> New tile job | Tile cache build/invalidation job | Estimating, queued, running, complete, failed |
| Manage access | Access or Resource -> Access | Presets, roles, grants, anonymous access, auth provider config | Public, restricted, private, custom, invalid |
| Monitor jobs | Jobs | Progress, logs, result links, retry/cancel | Queued, running, succeeded, failed, canceled, partial |
| Validate readiness | Validation or Resource -> Validation | Fix list across source, schema, metadata, publish, security, runtime | Ready, warning, blocked, not applicable |

## Core Creation Flows

### Create Connection

Entry points:

- Connections -> New connection
- Create Resource wizard -> Create connection inline
- Import wizard -> Create connection inline when materializing imported data

Steps:

1. Choose provider: PostgreSQL/PostGIS, SQL Server, Oracle Spatial,
   MySQL/MariaDB, DuckDB, object store, local file store, or remote API.
2. Enter endpoint details and credentials. Credentials are write-only and stored
   as a secret reference.
3. Test connection.
4. Select database, schema, bucket, path, or API scope when the provider can
   enumerate it.
5. Review detected capabilities: tables, geometry support, raster support,
   query support, write support, tile support, health check support.
6. Save connection.
7. Show usage: resources, services, jobs, and validations that depend on it.

Validation:

- Block save when required endpoint or credential fields are missing.
- Warn when the connection works but capabilities are limited.
- Block delete when active resources or services depend on the connection,
  unless the UI offers an explicit migration or detach path.

Outputs:

- Connection record.
- Secret reference, never resolved secret values in admin responses.
- Capability and health summary.

### Create Data Resource From Table

Entry points:

- Data Resources -> Create -> Database table
- Connection detail -> Browse tables -> Create resource

Steps:

1. Select connection.
2. Browse database/schema/table.
3. Inspect source fields, geometry columns, primary key candidates, temporal
   fields, row estimate, extent, and CRS.
4. Choose resource name and stable identifier.
5. Confirm geometry, primary ID, display field, and temporal fields.
6. Assign basic metadata: summary, keywords, contacts, license, rights, extent.
7. Choose access preset.
8. Optionally select initial publishing targets.
9. Review validation.
10. Create as draft or create and publish.

Validation:

- Block publishing when required ID, geometry, CRS, source permission, or field
  mappings are missing for selected targets.
- Allow draft creation with warnings.
- Show target-specific compatibility: OGC API, GeoServices, WMS, WFS, OData,
  catalog records, or tile output.

Outputs:

- Draft data resource.
- Source binding to the selected table.
- Detected field model with editable roles.
- Optional publication drafts.

### Create Data Resource From File

Entry points:

- Imports -> File import
- Data Resources -> Create -> File
- Service/catalog detail -> Add resource from file

Steps:

1. Select or drag in file.
2. Show supported formats and detected type. Legacy BSCore supported common GIS
   files such as shapefile zip, FileGDB zip, GeoJSON, JSON, KML/KMZ, GPX, GML,
   RSS, and CSV; Honua should expose the supported Honua formats from runtime
   capabilities.
3. Upload and scan file.
4. If the file contains multiple layers/tables, select which ones to import.
5. Inspect geometry, CRS, fields, row counts, extent, and warnings.
6. Choose import strategy: materialize into managed storage, register as an
   external file source, or stage for later review when supported.
7. Choose destination connection or storage when materializing.
8. Name created resources and assign minimal metadata.
9. Choose access preset and optional publish targets.
10. Start import job and show progress.
11. Open created resource(s) or failed-row diagnostics from the job result.

Validation:

- Block import when file type is unsupported, file is empty, geometry cannot be
  read, CRS is required but missing, or destination storage is unavailable.
- Warn when field names are normalized, types are coerced, geometry is repaired,
  or features are skipped.

Outputs:

- Import job.
- One or more draft data resources.
- Import diagnostics and source file provenance.

### Import Esri Service

Entry points:

- Imports -> Esri service
- Data Resources -> Create -> Remote service
- Services and Catalogs -> Import existing service

Steps:

1. Enter ArcGIS REST service URL.
2. Discover service metadata and layers from MapServer or FeatureServer.
3. Show service summary: title, service type, layer count, spatial reference,
   capabilities, extent, ownership, auth requirement, and warnings.
4. Select layers/tables to import.
5. For each selected item, preview fields, geometry, CRS, renderer hints,
   relationships, attachments, time awareness, edit/query support, and record
   limits.
6. Choose import mode:
   - Reference remote service without copying data.
   - Materialize selected layers into a Honua connection.
   - Create draft resources only and defer data movement.
7. Choose destination connection/storage when materializing.
8. Map names and identifiers for resources and optional publications.
9. Review metadata mapping: description, tags, rights, thumbnails, extents,
   dates, and source links.
10. Choose access preset and optional publish targets.
11. Start import job.
12. Show per-layer results, partial failures, diagnostics, and links to created
    resources.

Validation:

- Block when URL cannot be reached or parsed.
- Warn when a layer cannot map cleanly to selected targets.
- Preserve partial success: one failed layer should not hide successful imports.
- Keep source URL and import provenance visible on the resource.

Outputs:

- Import job with per-layer results.
- One or more draft data resources.
- Optional source references, materialized storage, and publication drafts.

## Service And Publishing Flows

### Create Service Or Catalog Endpoint

Entry points:

- Services and Catalogs -> New endpoint
- Resource -> Publish -> Create target

Steps:

1. Choose endpoint type: GeoServices MapServer/FeatureServer/ImageServer,
   OGC API, WMS, WMTS, WFS, OData, STAC, DCAT, OGC Records, or Esri catalog.
2. Set route, display name, description, and runtime state.
3. Configure target-specific settings: CRS, scale range, max record count,
   cache settings, allowed output formats, metadata profile, and anonymous
   access.
4. Add resources or leave endpoint empty for later publishing.
5. Validate.
6. Save as draft, start, or publish.

Outputs:

- Service/catalog endpoint.
- Runtime health and publish compatibility summary.

### Publish Resource

Entry points:

- Data Resource -> Publish tab
- Publishing matrix
- Service/catalog detail -> Add resource

Steps:

1. Select target service or catalog.
2. Review compatibility and blockers.
3. Configure target identity: route segment, layer index, collection id, entity
   set name, item id, or catalog record id.
4. Review field exposure and aliases.
5. Review metadata projection preview.
6. Review access impact.
7. Validate.
8. Publish immediately or save as publication draft.

Outputs:

- Publication record.
- Target-specific projection output.
- Runtime cache/projection invalidation job when needed.

### Service Detail Layer Slots

Service and catalog detail pages should include a concise table of the
publication entries under that service. For Esri MapServer and FeatureServer
targets, label these entries as layers so administrators can scan the service in
the same shape as ArcGIS REST.

Recommended columns:

| Column | Purpose |
|---|---|
| Layer | Service-local layer index, route, or display name |
| Data Resource | Linked canonical resource; opens the resource detail |
| Kind | Feature, table, raster, tile, catalog item, or other target-specific type |
| Source | Read-only connection/source summary from the resource |
| Fields | Readiness or exposure summary, with edit link to Resource -> Fields |
| Metadata | Projection readiness, with edit link to Resource -> Metadata |
| Access | Effective access summary, with canonical edit link to Resource -> Access |
| Status | Draft, warning, blocked, published, stale, or failed |
| Actions | Preview, reorder where supported, validate, unpublish, or open resource |

Do not make the service layer table the canonical authoring surface for the
resource. It can manage service-local choices such as layer order, layer route,
published display name, renderer override, popup override, target field
exposure, cache settings, and unpublish/reorder actions. It should deep-link to
the Data Resource for source, field role, metadata, rights, lineage, and access
ownership.

### Data Resource Publish View

The Data Resource Publish tab should provide the inverse view: all places where
one canonical resource is published or drafted for publication.

Recommended columns:

| Column | Purpose |
|---|---|
| Target | Service, catalog, or standard output name |
| Entry | Layer index, collection id, route, entity set, record id, or portal item |
| Type | GeoServices, OGC API, WMS, WFS, OData, STAC, DCAT, OGC Records, or Esri catalog |
| Projection | Ready, warning, blocked, or stale projection state |
| Access | Effective access compared with the resource policy |
| Last publish | Timestamp, author, and job link |
| Actions | Preview, validate, publish, republish, unpublish, or open service |

This inverse view makes it clear that the resource owns meaning once, while
each row is a publication representation of that resource in a specific service
or catalog target.

## Resource Authoring Flows

### Fields, Domains, And Relationships

The Fields tab should support:

- Field alias.
- Semantic role such as primary ID, geometry, display name, start time, end
  time, owner, status, category, asset URL, license, or quality flag.
- Query exposure.
- Edit exposure.
- Sensitive or masked state.
- Required state for selected targets.
- Coded value or range domains.
- Display field.
- SQL/filter expression where supported.
- Related table or relationship configuration.
- Advanced standard bindings for target-specific exceptions.

### Metadata

The Metadata tab should support:

- Summary and description.
- Themes and keywords.
- Publisher and contacts.
- License, rights, and access constraints.
- Dates.
- Spatial extent.
- Temporal extent.
- Quality and lineage.
- Source links.
- Distributions and download links.
- Thumbnail or preview media when catalog targets need it.

Readiness should be calculated by target. The UI should say "Missing publisher
identifier for DCAT" or "Missing license for STAC" instead of asking users to
edit separate standard documents.

### Styles, Labels, Popups, Events, And History

Presentation and behavior controls should be grouped under resource-level tabs
or panels:

- Styles: default renderer, unique values, class breaks, symbol preview.
- Labels: label field, expression, scale range, placement.
- Popups: title, visible fields, media, HTML template where supported.
- Relationships: related records and relationship labels.
- Events: configured webhooks or server-side event behavior.
- History: versions, rollback, audit trail, and last publish changes.

These are not metadata schema primitives, but they are real operator work and
should be reachable without leaving the resource.

## Operational Flows

### Jobs

Jobs should cover imports, publishes, tile builds, validation runs, migrations,
and cache refreshes.

Required job fields:

- Job name and type.
- Target resources or services.
- Submitted by.
- Started, updated, and completed timestamps.
- Status: queued, running, succeeded, failed, canceled, partial.
- Progress value and text.
- Log messages.
- Result links.
- Retry or cancel actions when safe.

### Tile Cache Management

Tile controls should support:

- Select service or resource.
- Select levels, extent, and storage target.
- Estimate tile count and storage.
- Start build job.
- Show per-level status.
- Clear or invalidate cache with confirmation.
- Link to WMTS or tile endpoints when available.

### Access, Auth, And CORS

Access should support:

- Access presets: Public read, authenticated read, private, publisher edit,
  admin only, custom.
- Anonymous access at service/catalog target where supported.
- User and role assignment.
- API keys or tokens when supported.
- OIDC/Azure AD or other auth provider settings.
- CORS/origin domain management.
- Field-level restrictions in advanced view.

The primary UI should show readable summaries. Raw policy diagnostics stay in
Advanced.

### Settings, License, And About

Settings should include:

- Server status and version.
- License status and upload, if licensed builds require it.
- Map preview provider settings.
- Catalog enablement and runtime toggles.
- Auth provider settings.
- CORS.
- Feature flags or edition-gated capabilities.

Avoid mixing settings into resource authoring screens unless the setting is
directly needed to complete that workflow.

## Validation Model

Validation is both global and resource-level.

Global groups:

- Source
- Schema
- Metadata
- Publishing
- Security
- Standards
- Cache and runtime
- Jobs

States:

- Ready
- Warning
- Blocked
- Not applicable

Each validation row should include resource, target, workflow area, severity,
message, and a fix link. For example:

| Target | Severity | Message | Fix Location |
|---|---|---|---|
| STAC | Warning | Missing license | Resource -> Metadata |
| GeoServices FeatureServer | Blocked | Primary ID field is not selected | Resource -> Fields |
| WMTS | Warning | Tile levels are not configured | Service -> Tiles |
| OGC Records | Ready | No blockers | Projection Preview |

## Claude Design Deliverables

Claude Design should produce:

- Full information architecture map.
- Dashboard screen with operational status and quick actions.
- Connections list, connection detail, and create connection wizard.
- Data Resources list and resource detail layout.
- Create resource from table wizard.
- Create resource from file wizard.
- Import Esri service wizard.
- Services and Catalogs list and endpoint detail pages.
- Publishing matrix and publication flow.
- Resource tabs for Overview, Source, Fields, Metadata, Publish, Access,
  Validation, Presentation, and Advanced.
- Jobs center with progress and logs.
- Validation center.
- Access and auth provider screens.
- Settings, CORS, license, and about screens.
- Empty, loading, error, warning, blocked, partial success, and success states.

## Source Links

- Tracking issue:
  [honua-server#1057](https://github.com/honua-io/honua-server/issues/1057)
- Metadata v2 epic:
  [honua-server#1035](https://github.com/honua-io/honua-server/issues/1035)
- Metadata v2 admin UI handoff:
  [metadata-v2-admin-ui-information-model.md](metadata-v2-admin-ui-information-model.md)
- BlueSpatial BSCore admin scripts:
  [BSCore/BSWeb/scripts/app/Admin](https://github.com/mikemcdougall/BlueSpatial/tree/BSCore/BSCore/BSWeb/scripts/app/Admin)
- BlueSpatial service import modal:
  [import-service-modal-component.html](https://github.com/mikemcdougall/BlueSpatial/blob/BSCore/BSCore/BSWeb/scripts/app/Admin/SharedComponent/import-service-modal-component.html)
- BlueSpatial file import component:
  [add-layer-from-file-component.html](https://github.com/mikemcdougall/BlueSpatial/blob/BSCore/BSCore/BSWeb/scripts/app/Admin/add-layer-from-file-component.html)
- BlueSpatial table layer component:
  [add-layer-from-database-component.html](https://github.com/mikemcdougall/BlueSpatial/blob/BSCore/BSCore/BSWeb/scripts/app/Admin/add-layer-from-database-component.html)
- BlueSpatial connection management:
  [manage-connection-component.html](https://github.com/mikemcdougall/BlueSpatial/blob/BSCore/BSCore/BSWeb/scripts/app/Admin/manage-connection-component.html)
