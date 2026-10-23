---
type: concept
title: "Data model"
description: "Honua's catalog is built from four ideas: connections point at data, layers publish individual tables or rasters, services expose layers through protocols, and styles and metadata control how layers look and describe themselves."
---
# Data model

Honua's catalog is built from four ideas: **connections** point at data, **layers** publish individual tables or rasters, **services** expose layers through protocols, and **styles and metadata** control how layers look and describe themselves. Understand these and the whole admin surface — UI, [admin API](../reference/admin-api/overview.md), and GitOps manifests — reads the same way.

```
 Connection ──▶ Layer(s) ──▶ Service ──▶ Protocol endpoints
 (database)    (published    (protocol    (/rest/…, /ogc/…, /odata,
                table or      exposure)    /tiles/…, /wfs, …)
                raster)          │
                                 └─ styles + metadata
```

## Connections

A connection is a registered data source: a PostGIS database, a DuckDB file, or a read-only SQL Server, Oracle, or MySQL/MariaDB database. Credentials are stored encrypted or referenced from a secret store — they never appear in catalog metadata.

Connections are managed at `/api/v1/admin/connections` (create, test, list tables). Registering a connection does not publish anything by itself; it makes tables discoverable for publishing. Provider capabilities differ; a writable database connection does not imply a source-backed published layer accepts edits. See the [data source reference](../reference/configuration/data-sources/README.md) and the managed-storage contract below.

## Layers

A layer is one published table or raster from a connection. Publishing records, in catalog metadata:

- which connection and table/raster back the layer (its storage binding)
- the schema: fields, primary key, geometry column, geometry type, and CRS
- capabilities such as whether editing is allowed
- display defaults, access policies, and style references

Source-backed publishing registers a binding to the existing table, which remains authoritative. Managed publishing (`storageMode: managed`) copies source rows into the managed feature store, which then becomes authoritative for that layer; it no longer tracks the source table. Source-backed layers do not accept feature writes. See the [storage modes and edit-capability contract](../reference/admin-api/connections-and-layers.md#editable-layers). Layers are published from a connection's tables at `/api/v1/admin/connections/{id}/layers`.

Schema expectations for smooth cross-protocol publishing: an integer primary key (required by FeatureServer), a single geometry column with an explicit type and SRID, a GiST spatial index, and simple snake_case field names. `objectid` and `shape` are FeatureServer-reserved names — use them only for the primary key and geometry roles.

## Services

A service groups layers and decides how they are exposed. Each service declares:

- an identifier that becomes part of public URLs (for example `/rest/services/{serviceId}/FeatureServer`, OGC collection ids)
- an explicit list of enabled protocols — the single source of truth for protocol gating
- service-level settings (record limits, output formats, timeouts) and an optional output CRS
- an access policy that composes with layer-level policies under deny-wins semantics: a service-level deny blocks every layer, but a permissive service never overrides a restrictive layer

Services are managed at `/api/v1/admin/services` (settings, protocols, access policy).

## Styles and metadata

Each layer can carry one or more styles. Honua keeps a canonical style document per layer and serves it to every protocol: it drives the auto-generated MapLibre style for vector tiles (`/api/styles/{layerId}.json`), server-side rendering for MapServer/WMS/OGC Maps, and Esri `drawingInfo` for GeoServices clients. Styles are edited via `/api/v1/admin/metadata/layers/{id}/style` and the `/ogc/styles` API.

Descriptive metadata (title, description, keywords, attribution, temporal info) feeds every protocol's discovery surface — GeoServices service/layer info, OGC collection documents, STAC collections, OData `$metadata`, WMS/WFS capabilities — from one definition.

## What "publishing" means

Publishing a layer creates catalog metadata and, when managed storage is selected, materializes a separate managed copy. The complete flow:

1. **Register a connection** to your database.
2. **Publish a layer** from one of its tables (schema is auto-discovered and can be adjusted).
3. **Attach it to a service** and choose which protocols the service enables.
4. **Optionally refine styles and metadata.**

The layer can then be projected through its enabled and supported protocols, such as FeatureServer, OGC API Features, WFS, OData, vector tiles, and STAC. Those projections share the canonical resource and its policies; a service's existence does not enable every protocol. Human-facing edits require a managed layer with the relevant declared capability and authority, and other readers observe the managed store. AI workflows do not edit source feature data.

The catalog itself can also be managed declaratively: metadata manifests can be exported, diffed, approved, and applied through the admin API's GitOps surfaces, so the connection/layer/service graph can live in version control.

## Where to go next

- [GIS domain language](domain/README.md) — precise meanings, common phrases, and workflow links for people and agents
- [Canonical resource](domain/resource.md), [storage binding](domain/storage-binding.md), and [publication](domain/publication.md) — Metadata v2 separates data semantics, physical storage, and service exposure

- [Quickstart](../get-started/quickstart.md) — publish your first layer end to end
- [Publish layers](../guides/publish/publish-layers.md) — the publishing workflow in detail
- [Protocols](protocols.md) — every endpoint a published layer appears on
- [Admin API overview](../reference/admin-api/overview.md) — the full control-plane surface
