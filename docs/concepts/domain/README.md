---
type: index
title: "Honua vocabulary"
description: "One vocabulary for Honua users, APIs, and the system model."
tags: [gis, domain-language, okf]
---

# Honua vocabulary

Use the same words in the Console, Studio, SDKs, guides, error messages, APIs, database schema, and model types. Only storage binding and principal are internal-only concepts. Definitions describe the canonical graph; availability and permission still determine what a deployment permits.

| Term | Definition |
| --- | --- |
| [Connection](connection.md) | A registered database or file that Honua can reach. Registration exposes no data by itself; a client session is a Session. |
| [Dataset](dataset.md) | A collection of records, features, or imagery, with logical identity, schema, spatial metadata, policy, and style references independent of storage and protocol. Its kind is Feature type (vector) or Coverage (raster); a non-spatial Feature type is shown as Table. |
| [Layer](layer.md) | A Dataset exposed in a Service, optionally selecting a storage binding, with a Service-local identifier, capabilities, access rules, and display defaults. |
| [Service](service.md) | A named group of Layers with one list of enabled protocols. It belongs to a Folder or the root of its Environment. |
| [Folder](folder.md) | The grouping of Services within an Environment. Folders do not nest or grant permissions. |
| [Environment](environment.md) | One Honua server managed by a Console, such as development or production. A database Connection selects data; an Environment selects the server. |
| [Style](style.md) | Drawing and label rules for a Layer or Map. Map overrides do not change shared Layer defaults. |
| [Map](map.md) | Ordered drawing layers, data references, styles, labels, and view state, with a Draft and saved Versions. |
| [App](app.md) | A Map with controls, widgets, and interactions, with the same saved-content lifecycle. |
| Content | The Studio library of Maps, Apps, Dashboards, Reports, Forms, Processes, Queries, Workflows, GP, and ETL. Each kind retains its own name and explicit discriminator. |
| [Draft](content-version.md) | Mutable authoring state for a supported Studio kind, with a generation for optimistic concurrency. |
| [Version](content-version.md) | An immutable saved snapshot of a supported Studio kind, identified by its version identifier and content hash. |
| [Released](content-version.md) | A Version active at its governed route. Go live means activate that Version; saving and previewing do not activate it. |
| [Catalog](catalog.md) | The browsable listing of accessible Services, Layers, and Apps. Listing grants no access. |
| [Proposal](approval.md) | A requested change and its target and evidence, submitted for an authorized decision. |
| [Approval](approval.md) | The authorized decision on a Proposal. AwaitingApproval is the canonical waiting state; the Approval page defines the current authority rules. |
| [Operation](task.md) | A typed control-plane action, such as applying an approved change. Analysis processes are called processes. |
| [Job](job.md) | One execution of accepted background work, classified by kind, such as import, export, or process, with status, progress, and results. |
| [User](identity.md) | A person with an authenticated identity. A credential is not a User. |
| [Role](permissions.md) | A named set of permissions assigned to an identity. Deny rules take precedence. |
| [API key](api-key.md) | A scoped credential for an application or automation, with managed permissions, restrictions, expiry, and revocation. |
| [Drawing layer](map-layer.md) | One ordered visual element in a Map. Several drawing layers can reference one served Layer. |
| [Feature set](feature-set.md) | Records returned by a query at an observed state; a query result is not automatically a durable Dataset. |
| [Query view](query-view.md) | A selection rule over a Dataset, with query constraints. |
| [CRS and units](crs.md) | The coordinate reference and measurement units used by data and analysis. |
| [Metadata](metadata.md) | Descriptive and structural facts about data or content, including schema, provenance, and attribution. Metadata does not grant authority. |
| [Permissions](permissions.md) | What an authenticated actor may do to a particular object, determined by grants and effective object policies. |
| [Sharing](sharing.md) | The intended audience and its effective access to a Map or App and its dependencies. A URL grants nothing by itself. |
| [Artifact](artifact.md) | A retained output with identity, format, location, and retention rules that a workflow can reference. |
| [Capability](capability.md) | An action supported and currently available for the selected object, subject to provider, deployment, and permission checks. |
| [Deployment](deployment.md) | The running artifact, configuration, and infrastructure behind an Environment. |
| [Worker](native-worker.md) | A backend that executes accepted work; its availability must be verified. |
| [Storage](storage.md) | Where durable data and outputs are kept. |
| [Session](session.md) | Temporary interaction context, distinct from durable Maps, Apps, and Datasets. |
| [Spatial analysis](spatial-analysis.md) | Filtering or computing results from spatial data, with explicit CRS, units, and outputs. |
| [Everyday requests](intent-interpretation.md) | Natural language expressing an intended outcome, interpreted using context before selecting an action. |
| [DevOps workflow](devops-language.md) | Planning, applying, verifying, and recovering a deployment change. |
| [Workflow](workflow-language.md) | Steps connecting an intended outcome to supported actions and recovery, described by a workflow contract. |
| [Workflow contract](semantic-contracts.md) | The inputs, effects, recovery, and evidence for a supported workflow. |
| [Studio editor](studio-workspace.md) | The editing canvas, selection, preview, and history around a Map or App Draft. |
| [Data source](data-source.md) | Contextual language for the origin of data. Name the actual object Connection, Dataset, or Layer. |
| [Project language](project.md) | An informal description of related work. Use Folder for Service grouping and Map or App for saved Studio content. |
| [Storage binding (internal only)](storage-binding.md) | The link from a Dataset to physical data, recording a Connection, locator, provider, and storage capabilities. Its storage handle differs from the Dataset ID and Service-local Layer ID. |
| [Principal (internal only)](identity.md) | The authenticated identity used for authorization and audit, representing a User or automation. Credentials authenticate a principal; display names do not establish identity. |

The [data-model narrative](../data-model.md) follows these same relationships.

## Coming from another system

Moving from another system? Use [Coming from ArcGIS](coming-from-arcgis.md) or [Coming from GeoServer](coming-from-geoserver.md) to map familiar terms to these definitions.

## Implementation transition

The canonical names are Dataset, Layer, Map, App, and each other named Studio kind throughout the system. Existing code and persisted metadata still contain `MetadataV2Resource`, `MetadataV2Publication`, and Studio content-item names. These are legacy names scheduled for migration, not a second vocabulary. Until that migration lands, use the current API reference for literal routes and fields. This glossary changes no runtime contract. A forward migration will convert existing databases after a verified backup and draining pending Proposals; steady-state readers will use only canonical shapes. Protocol wire names required by clients remain unchanged.

## Vocabulary review rules

Review new UI labels, model types, database names, API fields, MCP schemas, public SDK names, guides, and error messages against this table. Block additions that use:

- resource for Dataset, publication for Layer, or content item for a named Studio kind;
- workspace for grouping or tenancy; use Folder or Environment for the corresponding concept;
- slot, serving target, or projection as object names; use Layer or Service;
- vendor type names, hosted layer, portal item, or web map for Honua objects;
- HonuaApiToken for an API key;
- package, family, or composition as Studio object names; use the actual kind, such as Map, App, or Dashboard;
- tenant selection as a product surface; internal isolation checks still apply.

Ordinary technical uses such as HTTP resource, MCP resource, coordinate projection, and package dependency are not product-object names. Historical filenames and existing implementation references remain readable during migration. A compatibility exception must identify the exact literal, location, and reason; never split a retired term to evade a scan. Keep protocol-defined wire names where clients depend on them. Prefer `feature-service` and `feature-layer` for Honua's own type names.

## Workflows and interpretation

Honua's agent journeys are setup, service/layer administration and operations,
and realtime map creation. The [workflow-language guide](../../guides/workflows/use-workflow-language.md)
connects those journeys to the shared vocabulary and current execution surfaces.

Start with [understanding everyday GIS requests](intent-interpretation.md): use ordinary language, contextual synonyms, and the current application state to determine intent. The [workflow language](workflow-language.md) provides the structured meanings, effects, recovery, and interface links behind the agent's interpretation. People do not need to write that structure. Compact agent context is derived from these same human-readable OKF pages.

The Metadata v2 graph describes Connections, Datasets, storage bindings, Layers, Services, and Folders. Maps and Apps have their own saved-content lifecycle. Query views and feature-set results describe planning and query outcomes. Definitions do not grant capability or permission.

Use [workflow guides](../../guides/workflows/README.md) to connect concepts to outcomes. Common phrases are search associations, not guaranteed synonyms. Use selection, conversation, and established parameters to resolve words such as "layer," "source," "publish," "save," "project," and "near"; ask only when a consequential choice remains unclear.

## Concepts

- [API key](api-key.md)
- [App](app.md)
- [Approval](approval.md)
- [Artifact and output reference](artifact.md)
- [Capability and availability](capability.md)
- [Catalog and discovery](catalog.md)
- [Connection](connection.md)
- [Coordinate reference system and units](crs.md)
- [Data source language](data-source.md)
- [Dataset](dataset.md)
- [Deployment and execution target](deployment.md)
- [DevOps workflow language](devops-language.md)
- [Draft, Version, and Released](content-version.md)
- [Environment](environment.md)
- [Feature set snapshot](feature-set.md)
- [Filtering and spatial analysis](spatial-analysis.md)
- [Folder](folder.md)
- [GIS workflow language in OKF](workflow-language.md)
- [Go live and activation](publication.md)
- [Identity and credentials](identity.md)
- [Job and result](job.md)
- [Layer](layer.md)
- [Managed and native workers](native-worker.md)
- [Map drawing layer](map-layer.md)
- [Map](map.md)
- [Metadata and provenance](metadata.md)
- [Permissions](permissions.md)
- [Persistent storage](storage.md)
- [Project language](project.md)
- [Query view and filter](query-view.md)
- [Semantic contracts and executable workflow slices](semantic-contracts.md)
- [Service](service.md)
- [Session and handoff](session.md)
- [Sharing and audience](sharing.md)
- [Storage binding](storage-binding.md)
- [Studio editor](studio-workspace.md)
- [Style and labels](style.md)
- [Task and operation](task.md)
- [Understanding everyday GIS requests](intent-interpretation.md)
