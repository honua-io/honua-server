---
type: concept
title: "Data model"
description: "Connections reach Datasets; Layers expose them through Services, organized in Folders within an Environment."
---

# Data model

The [Honua vocabulary](domain/README.md) defines the names used across the Console, Studio, SDKs, and guides. An **Environment** is one Honua server managed by a Console. Within it, **Connections** reach **Datasets**, **Layers** expose those Datasets through **Services**, and **Folders** group Services. **Maps** and **Apps** reference served Layers and have their own saved-content lifecycle.

```text
Environment
  Connection → Dataset → Layer → Service → enabled protocol endpoints
                                      ↑
                                    Folder (or root)

Studio Map/App → drawing layers → served Layers
  Draft → Version → Released
```

These are views of the same relationships. A Folder move organizes a Service; publishing a Layer exposes data; saving a Map records content; going live activates a saved Version.

## Connections and Datasets

A [Connection](domain/connection.md) registers a database or file that Honua can reach. Registration makes candidate tables discoverable; it does not publish every table or grant access to viewers. Credentials are encrypted or supplied through supported secret references and are never returned as Catalog metadata.

A [Dataset](domain/dataset.md) is the collection of records, features, or imagery. Its identity is independent of protocol and physical location. An imported file and an existing database table can contain equivalent information while having different update rules. Provider support and storage mode determine what Honua can do with them; see the [data source reference](../reference/configuration/data-sources/README.md).

## Layers and Services

A [Layer](domain/layer.md) exposes a Dataset in a [Service](domain/service.md). It brings together schema, geometry and CRS, physical access, capabilities, policy, and display defaults. One Dataset can be exposed through several Services without inherently copying its data.

Source-backed Layers keep the source table authoritative and do not accept feature writes through that binding. Managed publishing copies source rows into Honua's managed store, which becomes authoritative for the Layer and no longer tracks the source table. Editing also requires the relevant declared capability and permissions. See the [storage-mode and edit-capability contract](../reference/admin-api/connections-and-layers.md#editable-layers).

For smooth cross-protocol publishing, use an integer primary key where FeatureServer requires it, one geometry column with an explicit type and SRID, a spatial index, and simple field names. FeatureServer reserves `objectid` and `shape` for the primary-key and geometry roles. AI workflows do not edit source feature data.

A Service is a named group of Layers with one explicit list of enabled protocols. It also carries settings such as record limits and an access policy. A permissive Service cannot override a restrictive Layer policy: deny wins. A Service's existence does not enable every protocol or every write capability.

Keep returned identifiers distinct. A Dataset's logical identifier, its numeric storage-layer handle, and a Layer's identifier within a Service have different scopes. OGC collection IDs and GeoServices Layer indexes are protocol identifiers; use discovery values instead of deriving one from another.

## Folders and Environments

A [Folder](domain/folder.md) groups Services within an [Environment](domain/environment.md). The hierarchy is **Folder → Service → Layer**, with Services also allowed at the root. A Service belongs to one Folder, and Folders do not nest. Moving a Service preserves its identity and does not move or copy the underlying data.

Folder membership does not grant permissions. Public path reservations protect the original Service identity across moves and renames. Environment selection chooses the server where an action runs; a database Connection chooses data reachable by that server.

## Styles, Maps, and Apps

A [Style](domain/style.md) describes drawing and label rules. Shared Layer styles feed supported protocol rendering and discovery surfaces. A Map can override drawing choices without modifying the shared Layer's defaults. Descriptive metadata such as title, attribution, provenance, and keywords also feeds discovery from shared definitions.

A [Map](domain/map.md) combines ordered drawing layers, data references, styles, labels, and view state. An [App](domain/app.md) adds controls, widgets, and interactions. A drawing layer is Map-local rendering configuration; several drawing layers can reference one served Layer.

Studio content starts as a mutable [Draft](domain/content-version.md). Saving creates an immutable Version. **Released** means a saved Version is active at its governed route; **Go live** means activate that Version. Previewing or saving does not activate a route or grant access to dependent data. The [current publication and approval contract](domain/publication.md) and [Studio release boundary](../studio/README.md#release-boundary) determine what the deployed system permits and what has been qualified.

## Catalog, authority, and background work

The [Catalog](domain/catalog.md) is the browsable listing of Services, Layers, and Apps. Listing an object or receiving its URL does not grant access. [Users and Roles](domain/permissions.md), object policies, and scoped [API keys](domain/api-key.md) determine effective authority.

A [Proposal](domain/approval.md) records a requested change; Approval is the authorized decision on it. An [Operation](domain/task.md) is a typed control-plane action. A [Job](domain/job.md) records one accepted background execution, such as import, export, or process, with status, progress, and results. Acceptance is not completion. Consult each API contract for its current routes and payloads.

## Contributor view of the same graph

The user terms above describe the Metadata v2 graph:

| User concept | Internal representation |
| --- | --- |
| Dataset | A data-bearing resource owns logical identity, schema, spatial metadata, policy, and style references. |
| Physical access to a Dataset | A storage binding connects that resource to a Connection and locator. |
| Layer in a Service | A publication connects the resource to a Service, optionally selecting a binding, and carries its Service-local identifier. |
| Service grouping | A Folder referenced by Service membership; current protocol facets agree on Folder and public name. |

[Resource](domain/resource.md), [storage binding](domain/storage-binding.md), and [publication](domain/publication.md) are model terms for contributor discussion. Product labels use Dataset, Connection, Layer, and Service. Protocol adapters translate the graph into their specified wire names. Declarative metadata export, diff, approval, and apply operate on this same graph.

## Where to go next

- [Honua vocabulary](domain/README.md) — definitions and model mappings
- [Quickstart](../get-started/quickstart.md) — publish your first Layer
- [Publish Layers](../guides/publish/publish-layers.md) — registration, publishing, and verification
- [Protocols](protocols.md) — supported exposure surfaces
- [Admin API overview](../reference/admin-api/overview.md) — control-plane contracts
