# Metadata v2 Admin Input Model

Companion to [Metadata v2 Admin UI Information Model](metadata-v2-admin-ui-information-model.md).
That document covers the workflow / navigation / labels for the admin UI.
This document covers the **input-vs-calculated classification** per field — the
information the UI designer needs to decide which form fields are operator
inputs vs which are read-only / system-computed / discovered from data.

## Operator mental model

The user-facing hierarchy in the admin UI:

```
Folder (Esri-style grouping, optional)
└── Service               (protocol exposure: FeatureServer, OGC API Features, ...)
    └── Layer             (operator-facing concept = Publication + Resource view)
        ├── Source         ← Resource.StorageBindings[0] + Connection
        ├── Fields         ← Resource.SchemaFields
        ├── Metadata       ← Resource.Metadata
        ├── Display        ← Resource.Display
        ├── Editing        ← Resource.Editing
        ├── Style          ← Resource.StyleResourceIds
        ├── Access         ← Resource.AccessPolicy (composed with Service.AccessPolicy)
        ├── Filter         ← Resource.PermanentFilter
        ├── Relationships  ← Resource.Relationships
        └── Publication    ← Publication-only fields (Identifier, TitleOverride)
```

Notable: **"Layer"** in the admin UI is a composite view over (Publication, Resource).
A single Resource can be referenced by multiple Publications across multiple Services
(e.g. parcels data published through both a FeatureServer and an OGC API Features
collection). The UI needs to surface this sharing.

## Sharing semantics

When the operator edits a Resource field, the UI must indicate whether the edit
is **resource-wide** (affects every layer using the resource) or
**publication-local** (only affects this one layer).

| Field category | Scope | Sharing indicator |
|---|---|---|
| `Resource.Metadata` (description, license, attribution, keywords) | Resource-wide | "Affects 3 layers across 2 services" |
| `Resource.SchemaFields` (field shape) | Resource-wide | "Affects 3 layers..." |
| `Resource.Spatial` (CRS, geometry type, bbox) | Resource-wide | "Affects 3 layers..." |
| `Resource.Temporal` (time fields, extent) | Resource-wide | same |
| `Resource.PermanentFilter` | Resource-wide | same |
| `Resource.Display` (scale, visibility, displayField) | Resource-wide | "Default for all layers; override per layer below" |
| `Resource.Editing` (capabilities, tracking fields) | Resource-wide | same |
| `Resource.StyleResourceIds` (default style list) | Resource-wide | "Default style for all layers" |
| `Resource.AccessPolicy` | Resource-wide | "Composed with each service's policy (deny-wins)" |
| `Publication.Identifier` | Publication-local | (one layer only) |
| `Publication.TitleOverride` | Publication-local | "Override display title for this service" |
| `Publication.IsPrimary` | Publication-local | (one layer only) |
| `Service.Metadata` | Service-wide | "Affects all layers in this service" |
| `Service.Settings` | Service-wide | "Operational limits for this service" |
| `Service.AccessPolicy` | Service-wide | "Composed with each resource's policy" |
| `Connection.*` | Connection-wide | "Affects N storage bindings" |

The UI should **route resource-wide edits through the resource page**, not through
the layer page. Layer page provides a read-only view of resource fields with
"Edit resource" links that navigate to the resource page (with a warning if
shared).

---

## Input-vs-calculated classification per entity

Legend:
- ✏️ — operator inputs explicitly
- 🔍 — auto-discovered from the data (operator can override)
- 🧮 — calculated from other fields (read-only in UI)
- ⚙️ — system-assigned (read-only; e.g. auto-generated IDs)
- 🔒 — admin-only (not editable in UI; configuration-time only)

---

### Connection

Operator input describes physical infrastructure.

| Field | Type | UI |
|---|---|---|
| `Name` | slug | ✏️ — operator picks |
| `Title` | string | ✏️ |
| `Description` | string | ✏️ |
| `Type` | enum (Database / ObjectStorage / FileSystem / HttpApi / Stac / Managed) | ✏️ — picker |
| `Provider` | string (postgres / mysql / s3 / stac / honua / ...) | ✏️ — picker |
| `Endpoint` | URL | ✏️ — operator pastes from infrastructure |
| `SecretRef` | string | ✏️ — picker from managed secrets store |
| `Options.poolSize` | int | ✏️ — optional override (default from provider) |
| `Options.timeout` | duration | ✏️ — optional |
| `Options.sslMode` | enum | ✏️ — Postgres only, picker |
| `Id` | string | ⚙️ — auto-generated UUID |
| `CreatedAt` / `UpdatedAt` | timestamp | ⚙️ |
| Health status | enum (Healthy / Degraded / Unhealthy) | 🧮 — read-only badge from periodic checks |
| Connection latency p50/p99 | duration | 🧮 — from probe history |

---

### Storage Binding

Operator input maps a resource to a connection + location.

| Field | Type | UI |
|---|---|---|
| `Name` (display) | string | ✏️ |
| `ResourceId` | FK | ✏️ — picker; OR auto-created when binding a new connection to a new resource |
| `ConnectionId` | FK | ✏️ — picker |
| `StorageType` | enum (RelationalTable / SqlView / SqlQuery / GeoPackageTable / GeoJson / GeoParquet / FlatGeobuf / MBTiles / PMTiles / ObjectPrefix / ExternalApi / StacAsset / ...) | ✏️ — picker (filtered by Connection.Type) |
| `Locator` | string (table name / S3 prefix / file path / SQL view name) | ✏️ — operator inputs OR "Browse..." button that queries the connection |
| `StorageLayerId` | int | 🔍 — auto-assigned at bind time (operator can override only if reusing a known int) |
| `Capabilities` | enum-list (Query / Filter / Sort / Aggregate / Edit / Transactions / Render / Tile / Download / Search) | 🔍 — derived from StorageType + Connection.Provider; operator can disable specific capabilities |
| `Options.schema` (Postgres schema) | string | ✏️ — when relational |
| `Options.geometryColumn` | string | 🔍 — discovered from table introspection; operator can override |
| `Options.idColumn` | string | 🔍 — same |
| `Id` | string | ⚙️ |
| `CreatedAt` / `UpdatedAt` | timestamp | ⚙️ |

**UI flow for new binding**: operator picks Connection → "Browse" lists candidate
tables/views/prefixes → operator selects one → introspection runs → schema fields
+ geometry column + id column + bbox auto-populate → operator reviews/edits the
inferred Resource → save.

---

### Resource — Metadata (universals)

After Slice 1 lands.

| Field | Type | UI |
|---|---|---|
| `Name` | slug (unique within graph) | ✏️ — operator picks; UI validates uniqueness |
| `Title` | string | ✏️ |
| `Description` | rich text / markdown | ✏️ |
| `Labels` | dictionary string→string | ✏️ — key/value editor |
| `Annotations` | dictionary string→string | ✏️ — k/v editor; advanced section |
| `Keywords` | string[] | ✏️ — chip input with autocomplete against existing keywords |
| `Themes` | string[] | ✏️ — picker from DCAT theme taxonomy |
| `Language` | string (BCP-47) | ✏️ — picker (en / en-US / es / ...) |
| `License` | string (SPDX id or "proprietary") | ✏️ — picker from SPDX list; free-text for custom |
| `Attribution` | string | ✏️ |
| `ContactPoint.Name` | string | ✏️ |
| `ContactPoint.Email` | email | ✏️ |
| `ContactPoint.Url` | URL | ✏️ |
| `Publisher` | string | ✏️ |
| `Links[]` | list of {Href, Rel, Type, Title} | ✏️ — link editor; preset rels (data, docs, license, source, …) |
| `Id` | string | ⚙️ |
| `CreatedAt` / `UpdatedAt` | timestamp | ⚙️ |

---

### Resource — Type & spatial

| Field | Type | UI |
|---|---|---|
| `Type` | enum (FeatureDataset / RasterDataset / TileDataset / Process / Style / Document / ExternalResource) | ✏️ — picker; default inferred from StorageType |
| `Spatial.SpatialReference.Srid` | int | 🔍 — discovered from data (PostGIS, GeoParquet header, etc.); operator can override |
| `Spatial.SpatialReference.Crs` | string (URI or short code) | 🧮 — derived from Srid via SRID lookup; operator override locks the canonical form |
| `Spatial.SpatialReference.IsGeographic` | bool | 🧮 — derived from Srid |
| `Spatial.GeometryType` | enum (Point / MultiPoint / LineString / MultiLineString / Polygon / MultiPolygon / GeometryCollection / Mixed / None) | 🔍 — discovered from data sample; operator can override (often needed when data is mixed but UI should treat as single type) |
| `Spatial.Bbox` (W, S, E, N) | doubles | 🔍 — discovered from `ST_Extent` or equivalent; operator can override for performance; "Recompute" button |
| `Spatial.PrimaryGeometryField` | string | 🔍 — first column with geometry type, or column with `geometry.primary` semantic role |
| `Spatial.SupportedCrs[]` (slice 4) | list of SpatialReference | ✏️ — multi-picker; default = [SpatialReference] |
| `Spatial.StorageCrs` (slice 4) | SpatialReference | 🔍 — usually = SpatialReference; only set when service re-projects |
| `Spatial.StorageCrsCoordinateEpoch` (slice 4) | decimal year | ✏️ — rarely set; time-varying CRS only |

---

### Resource — Temporal

| Field | Type | UI |
|---|---|---|
| `Temporal.StartTimeField` | string | 🔍 — auto-suggest from Date/DateTime fields; operator picks from dropdown |
| `Temporal.EndTimeField` | string | ✏️ — operator picks; null = instantaneous |
| `Temporal.TrackIdField` | string | ✏️ — operator picks; null = no trajectories |
| `Temporal.Extent.Start` | DateTimeOffset | 🔍 — discovered via min(StartTimeField); operator can override |
| `Temporal.Extent.End` | DateTimeOffset | 🔍 — discovered via max(EndTimeField ?? StartTimeField) |

UI: "Time aware?" toggle. Off → all temporal fields null. On → reveals the
field-name pickers and extent inputs.

---

### Resource — SchemaFields (per-attribute)

After Slice 2 lands.

| Field per attribute | Type | UI |
|---|---|---|
| `Name` | string | 🔍 — from db introspection; **read-only after creation** (renames are a separate migration) |
| `Type` | enum MetadataV2FieldType | 🔍 — discovered; **read-only** (DDL change needed to change) |
| `Title` | string | ✏️ — defaults to Name |
| `Alias` (slice 2) | string | ✏️ — display alias; defaults to Title |
| `Description` | string | ✏️ |
| `Nullable` | bool | 🔍 — discovered; read-only |
| `Editable` (slice 2) | bool | ✏️ — defaults true; operator can lock for read-only fields |
| `Length` (slice 2) | int? | 🔍 — discovered for VARCHAR; read-only |
| `DefaultValue` (slice 2) | JSON | ✏️ |
| `Domain.Type` (slice 2) | enum (codedValue / range) | ✏️ — radio |
| `Domain.CodedValues` (slice 2) | list of (code, label) | ✏️ — editable grid |
| `Domain.Range` (slice 2) | [min, max] | ✏️ |
| `SemanticRoles[]` | string[] | ✏️ — multi-select with canonical vocabulary (id.primary, geometry.primary, temporal.start, editor.creator, display.label, ...) |
| `SqlType` (slice 2) | string | 🔍 — discovered (postgres `varchar(50)`, `int4`, ...); read-only |

UI: field list = grid with all of the above; click a row to edit. Locked fields
(Name, Type, Nullable, Length, SqlType) show as read-only with explanation.

---

### Resource — Display (slice 3)

**Resource-wide defaults**. Publication-level overrides discussed below.

| Field | Type | UI |
|---|---|---|
| `Display.MinScale` | double? (denominator) | ✏️ — scale denominator input; preset picker (1:50,000 / 1:500,000 / "any") |
| `Display.MaxScale` | double? (denominator) | ✏️ — same |
| `Display.DefaultVisibility` | bool, default true | ✏️ — checkbox |
| `Display.DisplayField` | string | ✏️ — picker from SchemaFields (filtered to display-suitable types) |
| `Display.Queryable` | bool, default true | ✏️ — checkbox |
| `Display.HasZ` | bool, default false | 🔍 — discovered from geometry sample; operator can override |
| `Display.HasM` | bool, default false | 🔍 — same |

---

### Resource — Editing (slice 3)

| Field | Type | UI |
|---|---|---|
| `Editing.GlobalIdField` | string | 🔍 — auto-suggest from UUID-typed fields; operator picks |
| `Editing.CreatorField` | string | ✏️ — picker from string fields |
| `Editing.CreatedAtField` | string | ✏️ — picker from DateTime fields |
| `Editing.EditorField` | string | ✏️ — picker from string fields |
| `Editing.UpdatedAtField` | string | ✏️ — picker from DateTime fields |
| `Editing.CanModify` | bool | ✏️ — checkbox; default true if StorageBinding.Capabilities includes Edit |
| `Editing.SupportsAttachments` | bool | ✏️ — checkbox |
| `Editing.SupportsRelatedRecords` | bool | ✏️ — checkbox; default true if Relationships non-empty |

---

### Resource — Style references (slice 5)

| Field | Type | UI |
|---|---|---|
| `StyleResourceIds[]` | list of resource ids (Type=Style) | ✏️ — orderable picker; [0] is primary |

Style resources themselves (Type=Style) have their own form:

| Field | Type | UI |
|---|---|---|
| `Style.Title` | string | ✏️ |
| `Style.Abstract` | string | ✏️ |
| `Style.LegendUrl` | URL | ✏️ — or "Upload legend graphic" with auto-fill |
| `Style.StyleVersion` | int | 🧮 — auto-incremented on each Save |
| `Style.Encodings[].Encoding` | enum string | ✏️ — picker (mapbox-style / sld-1.0.0 / esri-drawing-info / ...) |
| `Style.Encodings[].Body` | string (code editor) | ✏️ — embedded editor with format-aware syntax highlighting (MapLibre / XML / JSON) |
| `Style.Encodings[].StorageBindingId` | FK | ✏️ — alternative to Body when payload is large |
| `Style.Encodings[].ContentType` | MIME | 🧮 — derived from Encoding |

UI: Maputnik-style preview pane next to the editor for the mapbox-style encoding.
For non-primary encodings (SLD, Esri), preview is read-only render.

---

### Resource — Access policy

| Field | Type | UI |
|---|---|---|
| `AccessPolicy.AllowAnonymous` | bool | ✏️ — radio: "Public" / "Authenticated users only" |
| `AccessPolicy.AllowAnonymousWrite` | bool | ✏️ — checkbox under "Public" |
| `AccessPolicy.AllowedRoles[]` | string[] | ✏️ — multi-select role picker |
| `AccessPolicy.AllowedWriteRoles[]` | string[] | ✏️ — multi-select |

UI shows the **effective** policy when this resource is published through each
service (composed with each Service.AccessPolicy under deny-wins). When the
operator hovers on a publication row, the UI shows: "Public read at the
resource, but service `internal-api` restricts to role `analyst`. Effective:
analyst-only read."

---

### Resource — PermanentFilter

| Field | Type | UI |
|---|---|---|
| `PermanentFilter.Expression` | string | ✏️ — code editor with filter-language syntax highlighting |
| `PermanentFilter.Language` | enum (arcgis-sql / cql2-text / cql2-json) | ✏️ — picker |

UI: "Test filter" button runs against the storage backend and shows match count.

---

### Resource — Relationships

| Field per relationship | Type | UI |
|---|---|---|
| `Id` | string | ⚙️ — auto-generated |
| `Name` | string | ✏️ |
| `Description` | string | ✏️ |
| `RelatedResourceId` | FK | ✏️ — picker from other resources |
| `Role` | enum (origin / destination) | ✏️ — radio |
| `Cardinality` | enum | ✏️ — radio (1:1 / 1:N / N:M) |
| `OriginField` | string | ✏️ — picker from current resource's fields |
| `DestinationField` | string | ✏️ — picker from related resource's fields (after RelatedResourceId set) |
| `EsriRelationshipId` | int? | 🧮 — auto-assigned stable int from Id hash; operator can override |

---

### Service

| Field | Type | UI |
|---|---|---|
| `Metadata.Name` | slug | ✏️ |
| `Metadata.Title` | string | ✏️ |
| `Metadata.Description` | string | ✏️ |
| `Metadata.Keywords` / `Themes` / `Language` / `License` / `Attribution` / `ContactPoint` / `Publisher` / `Links` | same shape as Resource.Metadata | ✏️ — defaults can inherit from resources in the service |
| `Route` | string | 🧮 — derived from Service.Group + Name; operator can override (rare) |
| `Group` (Esri folder, when added) | string | ✏️ — picker or freetext |
| `Protocols[]` | string[] | ✏️ — multi-select from supported protocols list |
| `SpatialReference` | SpatialReference | 🔍 — derived from primary resources; operator can pin if the service forces a single CRS |
| `AccessPolicy` | AccessPolicy | ✏️ — same shape as Resource |
| `Id` | string | ⚙️ |
| `CreatedAt` / `UpdatedAt` | timestamp | ⚙️ |

---

### Service.Settings (slice 4 — operational limits)

**This is what the user's "server settings around limits" message asks about.**

| Field | Type | UI | Default |
|---|---|---|---|
| `Settings.MaxRecordCount` | int? | ✏️ — number input | 2000 (Esri convention) |
| `Settings.DefaultRecordCount` | int? | ✏️ | 1000 |
| `Settings.MaxImageWidth` | int? | ✏️ — applies to MapServer/WMS | 2048 |
| `Settings.MaxImageHeight` | int? | ✏️ — applies to MapServer/WMS | 2048 |
| `Settings.DefaultDpi` | int? | ✏️ — applies to MapServer/WMS | 96 |
| `Settings.MaxFeaturesPerLayer` | int? | ✏️ — MapServer rendering limit | 4000 |
| `Settings.DefaultFormat` | string | ✏️ — picker from SupportedFormats | "json" |
| `Settings.SupportedFormats[]` | string[] | ✏️ — multi-select (json, geojson, pbf, png, jpg, ...) | derived from Protocols |
| `Settings.DefaultTileMatrixSet` | string | ✏️ — picker (WebMercatorQuad / WorldCRS84Quad / ...) | "WebMercatorQuad" |
| `Settings.SupportsAttachments` | bool | 🧮 — derived from any layer's Editing.SupportsAttachments | — |
| `Settings.MaxAttachmentSizeBytes` | long? | ✏️ — when attachments enabled | 10 MB |
| `Settings.QueryTimeoutMs` | int? | ✏️ — per-request query timeout | 60000 |
| `Settings.RateLimit.RequestsPerMinute` | int? | ✏️ — per-token / per-IP | 600 |
| `Settings.RateLimit.BurstSize` | int? | ✏️ — burst tokens | 50 |
| `Settings.MaxEditsPerTransaction` | int? | ✏️ — applies to Editing protocols | 5000 |
| `Settings.MaxPayloadBytes` | long? | ✏️ — per-request body limit | 10 MB |

UI: group these as **Limits** / **Defaults** / **Rate limits** sections in the
service edit form. Show effective values when a setting is null (falls back to
server-wide default from configuration).

Server-wide defaults that the UI displays (in italic/grey) when service has no
override:
- Configured in `appsettings.json` `Limits:Service:*` or env vars `HONUA_LIMITS_*`
- Operator can see them as "(server default: 2000)" hints on each input

---

### Publication (layer slot — service-local view of a resource)

| Field | Type | UI |
|---|---|---|
| `ServiceId` | FK | ✏️ — already set by navigation context |
| `ResourceId` | FK | ✏️ — picker from available resources |
| `StorageBindingId` | FK? | ✏️ — picker; auto = resource's primary binding |
| `PublicationType` | enum (OgcCollection / EsriFeatureLayer / WfsFeatureType / WmsLayer / WmtsLayer / StacCollection / OgcRecord / ODataEntitySet / Custom) | 🧮 — derived from Service.Protocols + Resource.Type |
| `Identifier.Value` | string | ✏️ — operator picks or auto-assigns ("0", "1", ... for Esri-style numeric; resource name for OGC-style) |
| `Identifier.IsNumeric` | bool | 🧮 — derived from Value (digit-only) |
| `Identifier.PathOverride` | string? | ✏️ — rare; operator only sets for non-default URLs |
| `IsPrimary` | bool, default true on first | ✏️ — checkbox |
| `TitleOverride` | string? | ✏️ — when service needs different display title than resource |
| `Id` (Metadata.Id) | string | ⚙️ |
| `Capabilities` (per-publication overrides) | derived | 🧮 — read-only display showing which Resource capabilities are exposed on this service |

UI flow: "Add layer to service" → picks Resource → auto-suggests Identifier
(next int for Esri-style services; resource name for OGC-style). Operator
confirms or edits. Save creates Publication.

---

### Style (Type=Style resource)

Same as Resource but with the Style-specific slot active. UI presents as a
separate "Styles" navigation section, not under "Layers".

(See "Resource — Style references" above for the slot shape.)

---

## Server-wide settings (not on V2 graph)

Some limits and operational controls are server-wide, not per-service.
Configuration-time, edited via admin "Server" page or appsettings/env vars:

| Setting | UI | Storage |
|---|---|---|
| Server name + admin contact | ✏️ — server identity card | `appsettings.json` `Server:*` |
| Max upload size (global) | ✏️ — number input | env `HONUA_LIMITS_MAX_UPLOAD_BYTES` |
| Max concurrent requests | ✏️ | env `HONUA_LIMITS_CONCURRENCY` |
| Database connection pool size | ✏️ — per-connection (on Connection.Options) | Connection.Options.poolSize |
| Cache TTLs (response cache, capabilities cache, …) | ✏️ — duration inputs | `appsettings.json` `Caching:*` |
| Health-check probe intervals | ✏️ | `appsettings.json` `HealthChecks:*` |
| Audit log retention | ✏️ | `appsettings.json` `Audit:RetentionDays` |
| TLS / CORS / hosts | ✏️ — managed via separate Security page | env / appsettings |
| Telemetry / OTLP endpoint | ✏️ | env `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Feature flags | ✏️ — toggles (e.g. enable OGC API Part 5 Schemas) | `appsettings.json` `Features:*` |

**Rule of thumb**: anything that's per-(service, layer) tuple goes on
`Service.Settings` or `Resource.*`. Anything global to the server instance is
configuration-time, not graph-time.

---

## What the operator should NEVER touch

These are graph internals that the UI never exposes for editing:

- `Resource.Metadata.Id`, `Service.Metadata.Id`, `Publication.Metadata.Id`,
  `StorageBinding.Metadata.Id`, `Connection.Metadata.Id` — system IDs.
- `Graph.Revision`, `Graph.GeneratedAt`, `Graph.SchemaVersion`,
  `Graph.ApiVersion` — system-managed.
- `StorageBinding.StorageLayerId` — auto-assigned at bind time (advanced
  override possible for migrating from existing systems).
- Derived computed properties (`Resource.PrimaryStorageBindingId`,
  `Publication.LayerIndex`, `Publication.Path`, `Service.PrimaryProtocol`) —
  read-only computed views; UI shows them as informational.
- ETag headers, internal cache keys — never user-visible.
- `StorageBinding.Capabilities` enum values that the underlying connection
  doesn't support — UI grays them out instead of letting operator pick.

---

## High-fidelity UI layout sketches

### Folder / Service / Layer navigation

```
┌───────────────────────────────────────────────────────────────────────┐
│ Honua Admin                                              [User ▾]     │
├──────────────┬────────────────────────────────────────────────────────┤
│ ▾ Workspace  │  Services                                              │
│   Catalogs   │  ┌───────────────────────────────────────────────────┐ │
│   Resources  │  │ PublicWorks/  ▾                                   │ │
│   Services   │  │ ├─ Roads (FeatureServer + OGC API Features)   ⋯  │ │
│   Styles     │  │ │  ├─ 0  parcels                                  │ │
│   Connections│  │ │  ├─ 1  hydrants                                 │ │
│   Jobs       │  │ │  └─ 2  signage                                  │ │
│   Audit      │  │ └─ Hydrants (FeatureServer)                       │ │
│              │  │    └─ 0  hydrants  (shares 'hydrants' resource ↗) │ │
│              │  └───────────────────────────────────────────────────┘ │
└──────────────┴────────────────────────────────────────────────────────┘
```

`(shares 'hydrants' resource ↗)` — sharing indicator that the operator can
click to navigate to the resource page.

### Resource edit page (showing sharing)

```
┌───────────────────────────────────────────────────────────────────────┐
│ Resource:  hydrants                                                   │
│            ┌──────────────────────────────────────────────────────┐   │
│            │ ⚠ Used by 2 layers:                                  │   │
│            │   PublicWorks/Roads/1  (FeatureServer)               │   │
│            │   PublicWorks/Hydrants/0  (FeatureServer)            │   │
│            │ Edits affect both layers.                            │   │
│            └──────────────────────────────────────────────────────┘   │
│                                                                       │
│ [Metadata] [Fields] [Spatial] [Temporal] [Display] [Editing]          │
│ [Style] [Access] [Filter] [Relationships]                             │
│                                                                       │
│ ─── Metadata ──────────────────────────────────────────────────────   │
│ Name:        hydrants                            [unique]             │
│ Title:       Fire Hydrants                                            │
│ Description: ...                                                      │
│ License:     CC-BY-4.0  ▾                                             │
│ ...                                                                   │
└───────────────────────────────────────────────────────────────────────┘
```

### Service edit page (showing settings)

```
┌───────────────────────────────────────────────────────────────────────┐
│ Service:  Roads     (in folder PublicWorks)                           │
│                                                                       │
│ [Metadata] [Layers] [Protocols] [Access] [Settings]                   │
│                                                                       │
│ ─── Settings ──────────────────────────────────────────────────────   │
│ Limits                                                                │
│   Max records per query:    [2000     ] (server default: 2000)        │
│   Default records:          [1000     ]                               │
│   Max image dimensions:     [2048] × [2048] px (MapServer/WMS only)   │
│   Max features per layer:   [4000     ] (MapServer only)              │
│   Max edits per txn:        [5000     ] (Editing only)                │
│   Max payload size:         [10 MB    ]                               │
│                                                                       │
│ Defaults                                                              │
│   Format:                   [json     ] ▾                             │
│   Tile matrix set:          [WebMerc..] ▾                             │
│   Image DPI:                [96       ]                               │
│                                                                       │
│ Rate limits                                                           │
│   Requests/minute:          [600      ]                               │
│   Burst size:               [50       ]                               │
│                                                                       │
│ Timeouts                                                              │
│   Query timeout:            [60000 ms ]                               │
└───────────────────────────────────────────────────────────────────────┘
```

### Layer edit page (publication + resource view)

```
┌───────────────────────────────────────────────────────────────────────┐
│ Layer 0:  parcels  (in service PublicWorks/Roads)                     │
│                                                                       │
│ [Publication] [Resource ↗] [Validation]                               │
│                                                                       │
│ ─── Publication ───────────────────────────────────────────────────   │
│ Identifier value:    [0       ]  ☑ numeric                            │
│ Path override:       [        ]  (rare; leave blank for default URL)  │
│ Primary publication: ☑                                                │
│ Title override:      [        ]  (rare; defaults to resource title)   │
│                                                                       │
│ ─── Resource: parcels ─────────────────────────────────────────────   │
│ Title:        Parcels                          [Edit resource ↗]      │
│ Type:         FeatureDataset                                          │
│ Geometry:     Polygon (auto-detected)                                 │
│ Storage:      postgres → public.parcels                               │
│ Fields:       12 fields (see Fields tab on resource page)             │
│                                                                       │
│ Used by 1 other layer:                                                │
│   PublicWorks/Hydrants/0  (FeatureServer)                            │
└───────────────────────────────────────────────────────────────────────┘
```

---

## Cross-references

- [ADR-0040](../adr/0040-metadata-v2-canonical-graph.md) — design rationale
  (the layered semantic vocabulary)
- [metadata-v2-crosswalk.md](metadata-v2-crosswalk.md) — concept-by-concept
  inventory and gap analysis
- [metadata-v2-mapping.md](metadata-v2-mapping.md) — field-by-field
  V2-to-standards correspondence
- [metadata-v2-extensions.md](metadata-v2-extensions.md) — Extensions
  vocabulary specification (to be written)
- [metadata-v2-admin-ui-information-model.md](metadata-v2-admin-ui-information-model.md)
  — workflow / navigation / labels
- [metadata-v2-cutover-plan.md](metadata-v2-cutover-plan.md) — v1→v2 cutover plan
