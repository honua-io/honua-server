---
type: reference
title: "Coming from GeoServer"
description: "Translate GeoServer terms into Connections, Datasets, Layers, Services, and Folders."
tags: [gis, domain-language, migration]
---

# Coming from GeoServer

Start with **Connection → Dataset → Layer → Service**, with **Folder** grouping Services inside an **Environment**. Use the [Honua vocabulary](README.md) for definitions. The table describes conceptual mappings; supported import formats, protocols, and operations still depend on the running deployment.

| GeoServer term | Honua term | What to check |
| --- | --- | --- |
| Data store or coverage store | [Connection](connection.md) | Identifies a reachable database or file. Registering it alone does not publish data. |
| Feature type | [Dataset](dataset.md), kind **Feature type** | Owns the logical records, schema, and spatial metadata independently of protocols. |
| Non-spatial table | Dataset, kind **Feature type**, non-spatial; shown as **Table** | Preserve the lack of geometry. A table does not gain invented geometry when imported. |
| Coverage | Dataset, kind **Coverage** | Represents raster or imagery data; check extent, CRS, bands, and supported storage. |
| Published layer | [Layer](layer.md) | Exposes a Dataset within a Service, with access and display defaults. Dataset and Layer identifiers have different scopes. |
| Workspace | [Folder](folder.md), for organization | A Folder groups Services, not stores or Datasets. It does not nest or grant permissions. Workspace namespaces and security rules need separate review. Workspace-scoped OWS endpoints correspond more closely to a Service. |
| WMS/WFS service settings | [Service](service.md) and its protocols | A Honua Service groups Layers with one protocol list. Check deployment capabilities and permissions instead of assuming all source operations exist. |
| Layer group | [Map](map.md) for a drawing composition; Service for endpoint organization | These serve different purposes. A source group is not automatically a Honua Service, and its rendering modes need explicit review. |
| Style | [Style](style.md) | Check renderer support. A Map override does not change shared Layer defaults. |
| GeoServer instance | [Environment](environment.md) | The Honua server managed by a Console. A database Connection selects data instead. |
| Layer Preview or discovery listing | [Catalog](catalog.md) | A Catalog helps people find accessible objects. Visibility in a listing does not grant access. |
| GetCapabilities document | A [Service](service.md) protocol endpoint | Protocol capabilities describe the Service and its exposed Layers. Use the enabled protocol endpoint; a Catalog listing does not replace it. |
| Layer or workspace security rule | [Permissions](permissions.md) through effective Service and Layer policies | Folder membership alone conveys no permissions. Verify access using the intended identity. |

For example, roads in a database store corresponds to a Dataset of kind Feature type reached through a Connection. A Layer exposes that Dataset inside a Service. A transportation Folder can group that Service with others. A Map can draw the Layer alongside others without changing the Service's membership or protocols.

Keep returned identifiers when connecting clients. A source `workspace:layer` name is not a formula for a Honua Layer identifier, URL, or permission boundary. CRS projection remains a valid technical term; use Layer for an exposed Dataset.

## Use the current interfaces

Follow [connect and publish existing data](../../guides/workflows/connect-publish-data.md), check the actual protocol endpoints, and verify fields, geometry or raster metadata, styles, and access. Import screens should use source terms for the objects being read and Honua terms for the destination objects. This reference does not promise automatic migration of namespaces, styles, groups, or security rules. It does not implement object-search aliases in the Console or server.

Consult the current API reference for literal fields and routes during the [implementation transition](README.md#implementation-transition). For source terminology, see the GeoServer manuals for [workspaces](https://docs.geoserver.org/stable/en/user/data/webadmin/workspaces/) and [layers](https://docs.geoserver.org/stable/en/user/data/webadmin/layers/). See also [Coming from ArcGIS](coming-from-arcgis.md).
