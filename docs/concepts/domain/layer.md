---
type: concept
title: "Published layer"
description: "A server resource binding data, schema, capabilities, policies, and display defaults."
tags: [gis, domain-language]
aliases: ["GIS layer","feature layer","published table","collection"]
sources:
  - id: contract
    resource: "/reference/admin-api/connections-and-layers.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/dataset.md"
  - predicate: relates_to
    target: "/concepts/domain/crs.md"
  - predicate: relates_to
    target: "/concepts/domain/service.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
---

# Published layer
A published layer binds a [dataset](dataset.md) to server metadata: fields, geometry, [CRS](crs.md), storage, access, and display settings. A [service](service.md) determines its protocol exposure.[^contract]

This familiar API term is interpreted through Metadata v2's [resource](resource.md), [storage binding](storage-binding.md), and [publication](publication.md). The logical resource, backing-store layer handle, and service-local identifier must not be substituted for one another.

Source-backed publication keeps the source table authoritative and does not offer feature writes through that binding. Managed publication copies records into Honua's managed store, which becomes authoritative for that layer. Edit capabilities must be supported and permitted; a PostGIS source alone does not imply write-through editing.

A renderer's “layer” is different: it describes how features are drawn inside a [map](map.md). Several drawing layers can use one source. An OGC collection or Esri layer can be a protocol projection of a published layer, but resolve the returned identifier instead of assuming names match across protocols.

“Add a layer” may mean publish server data or add a reference to a map. Identify which object should exist afterward.

[^contract]: [Source contract](../../reference/admin-api/connections-and-layers.md).
