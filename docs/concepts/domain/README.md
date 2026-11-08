---
type: index
title: "Honua vocabulary"
description: "Canonical user terms, model meanings, and relationships between Honua concepts."
tags: [gis, domain-language, okf]
---

# Honua vocabulary

Use these names in the Console, Studio, SDKs, guides, and error messages. The model column explains the same concepts for contributors; it does not introduce a second set of product labels.

| User term | Definition | Model meaning |
| --- | --- | --- |
| [Connection](connection.md) | A registered database or file that Honua can reach; registration publishes nothing. | Connection registry entry and MetadataV2Connection; a client session is not a Connection. |
| [Dataset](dataset.md) | A collection of records, features, or imagery, independent of how it is served. | Data-bearing MetadataV2Resource; physical access is recorded by a storage binding. |
| [Layer](layer.md) | A Dataset exposed in a Service, with schema, access rules, capabilities, and display defaults. | A publication connects the resource and its selected storage binding to a service. Resource, storage-layer, and service-local identifiers differ. |
| [Service](service.md) | A named group of Layers with one list of enabled protocols. | MetadataV2Service; current protocol facets represent one logical Service and agree on its Folder and public name. |
| [Folder](folder.md) | The grouping of Services within an Environment; Services can also be at the root. | MetadataV2Folder and service folderId; organizational rather than an access boundary. |
| [Environment](environment.md) | One Honua server managed by a Console, such as development or production. | Console environment profile with server endpoints and authentication configuration. Snapshot environment identifies a graph partition, not a database Connection. |
| [Style](style.md) | Drawing and label rules for a Layer or Map. | Resource style metadata and references; Map overrides belong to the Map. |
| [Map](map.md) | Ordered drawing layers, data references, styles, labels, and view state. | Studio content with a Draft and saved Versions; drawing layers reference served Layers. |
| [App](app.md) | A Map with controls, widgets, and interactions. | Studio content and its interaction configuration. |
| [Draft](content-version.md) | Mutable authoring state for a Map or App. | Generation-checked Studio draft. |
| [Version](content-version.md) | An immutable saved snapshot of a Map or App. | Saved content version with an identifier and content hash. |
| [Released](content-version.md) | A saved Version activated at its governed route. “Go live” means activate that Version. | Studio content activation; saving and previewing do not activate a route. |
| [Catalog](catalog.md) | The browsable listing of Services, Layers, and Apps. | Discovery views over accessible data and saved content; listing grants no access. |
| [Proposal](approval.md) | A requested change awaiting an authorized decision. | Proposed control-plane change with its target and evidence. |
| [Approval](approval.md) | The authorized decision on a Proposal. | Governed decision; AwaitingApproval is the user-facing waiting state. See the Approval page for current authority rules. |
| [Operation](task.md) | A typed control-plane action, such as applying an approved change. | Control-plane execution and audit contract. Analysis processes are called processes. |
| [Job](job.md) | One execution of accepted background work, with status, progress, and results. | Execution record classified by kind, such as import, export, or process. Current routes and payloads follow the owning API contract. |
| [User](identity.md) | A person with an authenticated identity. | Durable principal identity; a credential is not a User. |
| [Role](permissions.md) | A named set of permissions assigned to an identity. | Role grants composed with resource policies; deny wins. |
| [API key](api-key.md) | A scoped credential for an application or automation. | Stored key identity, permissions, restrictions, and lifecycle; secrets stay out of Map and Catalog metadata. |
| [Drawing layer](map-layer.md) | One ordered visual element in a Map. | Map-local rendering configuration; several can reference one served Layer. |
| [Feature set](feature-set.md) | Records returned by a query at an observed state. | Query result snapshot, not automatically a durable Dataset. |
| [Query view](query-view.md) | A selection rule over a Dataset. | Resource reference and query constraints. |
| [CRS and units](crs.md) | The coordinate reference and measurement units used by data and analysis. | Spatial metadata, SRID, and the process contract. |
| [Metadata](metadata.md) | Descriptive and structural facts about data or content. | Shared schema, provenance, attribution, and other metadata; not authorization. |
| [Permissions](permissions.md) | What an authenticated actor may do to a particular object. | Grants and effective resource policies. |
| [Sharing](sharing.md) | The intended audience and its effective access to content and dependencies. | Access policy and readable dependencies; a URL grants nothing by itself. |
| [Artifact](artifact.md) | A retained output that a workflow can reference. | Output identity, format, location, and retention contract. |
| [Capability](capability.md) | An action supported and currently available for the selected object. | Declared capability plus provider, deployment, and permission checks. |
| [Deployment](deployment.md) | The running artifact, configuration, and infrastructure behind an Environment. | Serving runtime and execution backends. |
| [Worker](native-worker.md) | A backend that executes accepted work. | Managed or native execution worker; availability must be verified. |
| [Storage](storage.md) | Where durable data and outputs are kept. | Physical stores and retention contracts. |
| [Session](session.md) | Temporary interaction context. | Client/agent state and handoff context, distinct from durable content. |
| [Spatial analysis](spatial-analysis.md) | Filtering or computing results from spatial data. | Query and process contracts with explicit CRS, units, and outputs. |
| [Everyday requests](intent-interpretation.md) | Natural language expressing the intended outcome. | Contextual intent interpretation before selecting an action. |
| [DevOps workflow](devops-language.md) | Plan, apply, verify, and recover a deployment change. | Desired and observed state with an explicit execution target. |
| [Workflow contract](semantic-contracts.md) | The inputs, effects, recovery, and evidence for a supported workflow. | Semantic contract and executable workflow slice. |
| [Workflow](workflow-language.md) | Steps connecting an intended outcome to supported actions and recovery. | Semantic contracts and executable workflow slices. |
| [Studio editor](studio-workspace.md) | The editing canvas, selection, preview, and history for a Map or App. | Temporary authoring context around a Draft. |
| [Data source](data-source.md) | Contextual language for the origin of data. Name product objects Connection, Dataset, or Layer. | Disambiguate registry entry, physical location, and Map reference. |
| [Project language](project.md) | An informal description of related work. Use Folder for Service grouping and Map/App for saved Studio content. | No universal project or workspace grouping object. |
| [— (resource, model only)](resource.md) | Logical identity and semantics shared across storage and protocols. | MetadataV2Resource, including data, styles, and documents. |
| [— (storage binding, model only)](storage-binding.md) | The link from a resource to its physical data. | MetadataV2StorageBinding, including connection and locator. |
| [— (publication, model only)](publication.md) | The link from a resource to a service and its service-local identifier. | MetadataV2Publication; distinct from Studio Version activation. |

The [data-model narrative](../data-model.md) follows these same relationships. Protocol wire names remain protocol-specific; Honua's own labels use this table. A definition does not promise that a deployment supports an action.

## Vocabulary review rules

Review new UI labels, public SDK names, guides, and error messages against this table. Block additions that use:

- workspace for grouping or tenancy; use Folder or Environment for the corresponding concept;
- slot, serving target, projection, or publication as a product label; use Layer or Service;
- vendor type names, hosted layer, portal item, or web map for Honua objects;
- HonuaApiToken for an API key;
- package, family, or composition as Studio content labels; use Map or App;
- tenant selection as a product surface; Honua is single-tenant.

Resource, storage binding, and publication belong in model code and contributor documentation, including the model explanations in this glossary. Protocol wire names remain unchanged where clients depend on them. A compatibility exception must identify the exact literal and its reason; never split a retired term to evade a scan. Historical filenames and links remain stable for readers.

## Workflows and interpretation

Honua's agent journeys are setup, service/layer administration and operations,
and realtime map creation. The [workflow-language guide](../../guides/workflows/use-workflow-language.md)
connects those journeys to the shared vocabulary and current execution surfaces.

Start with [understanding everyday GIS requests](intent-interpretation.md): use ordinary language, contextual synonyms, and the current application state to determine intent. The [workflow language](workflow-language.md) provides the structured meanings, effects, recovery, and interface links behind the agent's interpretation. People do not need to write that structure. Compact agent context is derived from these same human-readable OKF pages.

The model follows Honua's existing Metadata v2 resource, storage-binding, connection, service, and publication graph. It distinguishes those runtime entities from semantic planning objects such as a query view or a feature-set result, and from map/app design state. Definitions do not grant capability or permission.

Use [workflow guides](../../guides/workflows/README.md) to connect concepts to outcomes. Common phrases are search associations, not guaranteed synonyms. Use selection, conversation, and established parameters to resolve words such as "layer," "source," "publish," "save," "project," and "near"; ask only when a consequential choice remains unclear.

## Concepts

- [Understanding everyday GIS requests](intent-interpretation.md)
- [Approval](approval.md)
- [Artifact and output reference](artifact.md)
- [Canonical resource](resource.md)
- [Capability and availability](capability.md)
- [Catalog and discovery](catalog.md)
- [Connection](connection.md)
- [Coordinate reference system and units](crs.md)
- [Data source language](data-source.md)
- [Dataset](dataset.md)
- [Deployment and execution target](deployment.md)
- [DevOps workflow language](devops-language.md)
- [Draft and content version](content-version.md)
- [Feature set snapshot](feature-set.md)
- [Filtering and spatial analysis](spatial-analysis.md)
- [GIS workflow language in OKF](workflow-language.md)
- [Identity and credentials](identity.md)
- [API key](api-key.md)
- [Environment](environment.md)
- [Folder](folder.md)
- [Job and result](job.md)
- [Managed and native workers](native-worker.md)
- [Map](map.md)
- [App](app.md)
- [Map drawing layer](map-layer.md)
- [Metadata and provenance](metadata.md)
- [Permissions](permissions.md)
- [Persistent storage](storage.md)
- [Project language](project.md)
- [Publication](publication.md)
- [Layer](layer.md)
- [Query view and filter](query-view.md)
- [Service](service.md)
- [Semantic contracts and executable workflow slices](semantic-contracts.md)
- [Session and handoff](session.md)
- [Sharing and audience](sharing.md)
- [Storage binding](storage-binding.md)
- [Studio editor](studio-workspace.md)
- [Style and labels](style.md)
- [Task and operation](task.md)
