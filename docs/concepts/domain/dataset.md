---
type: concept
title: "Dataset"
description: "The records or imagery a workflow uses, before or after Honua publishes access to them."
tags: [gis, domain-language]
aliases: ["data","file","table","features","records"]
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
A dataset is a collection of records, features, or imagery. A file and a database table can carry equivalent information while having different formats, identities, and update rules. A [connection](connection.md) makes a database source discoverable; a [layer](layer.md) describes published access to data.[^contract]

“Load this data” can mean preview a file, import records into storage, register an existing source, or add a layer to a [map](map.md). Resolve that distinction before promising a durable result. Importing changes storage; adding a map layer changes a composition.

For example, “show these survey points” identifies a desired display, but does not establish coordinate columns, [CRS](crs.md), destination, or sharing rights. Preserve source identifiers and [metadata](metadata.md), and check counts and representative values after conversion.

Follow [import data](../../guides/workflows/import-data.md) or [connect and publish existing data](../../guides/workflows/connect-publish-data.md).

[^contract]: [Source contract](../../reference/admin-api/connections-and-layers.md).
