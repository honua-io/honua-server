---
type: reference
title: "Coming from ArcGIS"
description: "Translate ArcGIS terms into the Honua vocabulary without assuming equivalent behavior."
tags: [gis, domain-language, migration]
---

# Coming from ArcGIS

Use this table to find familiar concepts in the [Honua vocabulary](README.md). These are conceptual mappings, not a promise that every source feature can be imported. An import should retain the source's words when describing what it reads, then identify the Honua objects it creates.

| ArcGIS term | Honua term | What to check |
| --- | --- | --- |
| Database connection | [Connection](connection.md) | Registering a database does not expose its data. A Console connection to a server is an Environment. |
| Feature class | [Dataset](dataset.md), kind **Feature type** | Check geometry, fields, CRS, and identifiers. A Dataset can be exposed through several Layers. |
| Standalone table | Dataset, kind **Feature type**, non-spatial; shown as **Table** | Preserve the absence of geometry. Do not invent coordinates to make a table spatial. |
| Raster dataset | Dataset, kind **Coverage** | Check supported storage, CRS, extent, bands, and raster operations for the deployment. |
| Feature layer in a service | [Layer](layer.md) | A Layer exposes a Dataset inside a Service; its identifier has Service-local scope. |
| Feature service, map service, image service | [Service](service.md) | A Honua Service groups Layers and has one protocol list. A name match does not establish equivalent capabilities. Protocol-defined names such as FeatureServer stay unchanged. |
| Server service folder | [Folder](folder.md) | Groups Services in one Environment. Folders do not nest or grant access. |
| ArcGIS Server site | [Environment](environment.md) | Selects the Honua server that the Console manages. It does not select a source database. Honua's [Deployment](deployment.md) is the infrastructure behind an Environment. |
| Web map | [Map](map.md) | Drawing layers, references, styles, and view state. Check renderer and dependency support when importing. |
| Web application | [App](app.md), or the actual named kind | Dashboards, Reports, Forms, and Workflows retain their own names. Do not convert every kind into an App. |
| Portal item | The actual kind, such as Map, App, Style, or Dataset | There is no one-to-one generic object mapping. Identify the kind before mapping identity, dependencies, or sharing. |
| Publish or update saved map/app content | Save a [Version](content-version.md), then [Release](publication.md) it | A Draft is mutable; a Version is immutable; Released identifies the active Version. Saving and previewing do not activate a route. |
| Catalog or discovery listing | [Catalog](catalog.md) | A listing helps people find permitted objects. Listing an object does not grant access to it. |
| User and role | [User](identity.md) and [Role](permissions.md) | Re-evaluate effective permissions and dependencies. Source sharing settings are not proof of Honua access. |

A feature dataset in a geodatabase is not automatically a Honua Folder: a Folder groups Services, not source tables. Map each feature class separately and review shared constraints or relationships. An organization or group is not a tenancy boundary or grouping object in Honua; choose Folder for Service organization, Role for permissions, and [Sharing](sharing.md) for a Map or App audience.

For example, a roads feature class becomes a Dataset of kind Feature type. Publishing it creates a Layer in a Service. Adding that Layer to a Map creates a drawing-layer reference; it does not copy the Dataset. Releasing a saved Map Version activates the Map's route independently of publishing the Layer.

## Use the current interfaces

Follow [connect and publish existing data](../../guides/workflows/connect-publish-data.md) or [import data](../../guides/workflows/import-data.md), then verify the returned identifiers, record counts, representative attributes, geometry, and effective access. Use the current API reference for literal fields and routes during the [implementation transition](README.md#implementation-transition). This vocabulary reference does not implement object-search aliases in the Console or server, convert source objects, or change a protocol payload.

Source terminology: [geodatabase dataset types](https://doc.esri.com/en/arcgis-pro/latest/help/data/geodatabases/overview/geodatabase-dataset-types.html). See also [Coming from GeoServer](coming-from-geoserver.md).
