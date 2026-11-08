---
type: concept
title: "Dataset"
description: "The records or imagery a workflow uses, before or after Honua publishes access to them."
tags: [gis, domain-language]
aliases: ["data","file","table","features","records","feature class","feature type","coverage","raster dataset"]
sources:
  - id: contract
    resource: "/reference/admin-api/connections-and-layers.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/connection.md"
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/concepts/domain/crs.md"
  - predicate: relates_to
    target: "/concepts/domain/metadata.md"
  - predicate: relates_to
    target: "/guides/workflows/import-data.md"
  - predicate: relates_to
    target: "/guides/workflows/connect-publish-data.md"
---

# Dataset
A Dataset is a collection of records, features, or imagery. It owns logical identity, schema, spatial metadata, policy, and Style references independently of storage and protocol. It can have several [storage bindings](storage-binding.md) and be exposed through several Layers without copying its data. A file and a database table can carry equivalent information while having different formats, identities, and update rules. A [connection](connection.md) makes a database source discoverable; a [layer](layer.md) describes published access to data.[^contract]

Every Dataset has a kind: **Feature type** for vector records or **Coverage** for raster imagery. A non-spatial table is a Feature type with no geometry, shown as **Table**; importing it must not invent geometry. Kind labels describe the data, not whether a deployment supports every storage format or operation.

For familiar source terms, see [Coming from ArcGIS](coming-from-arcgis.md) and [Coming from GeoServer](coming-from-geoserver.md). The aliases in this concept's metadata help vocabulary discovery; they do not implement object-search aliases in the Console or server.

“Load this data” can mean preview a file, import records into storage, register an existing source, or add a layer to a [map](map.md). Resolve that distinction before promising a durable result. Importing changes storage; adding a drawing layer changes a Map.

For example, “show these survey points” identifies a desired display, but does not establish coordinate columns, [CRS](crs.md), destination, or sharing rights. Preserve source identifiers and [metadata](metadata.md), and check counts and representative values after conversion.

Follow [import data](../../guides/workflows/import-data.md) or [connect and publish existing data](../../guides/workflows/connect-publish-data.md).

[^contract]: [Source contract](../../reference/admin-api/connections-and-layers.md).
