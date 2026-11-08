---
type: concept
title: "Layer"
description: "A Dataset exposed in a Service, with a Service-local identifier and access and display rules."
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

# Layer
A Layer exposes a [Dataset](dataset.md) within a [Service](service.md), with fields, geometry, [CRS](crs.md), storage, access, and display settings. The Service determines its protocol exposure.[^contract]

A Layer links the Dataset to the Service and may select a [storage binding](storage-binding.md). The Dataset ID, backing-store handle, and Service-local Layer identifier have different scopes and must not be substituted for one another. The [glossary transition note](README.md#implementation-transition) explains legacy names in current APIs and stored metadata.

A source-backed Layer keeps the source table authoritative and does not offer feature writes through that binding. A managed Layer copies records into Honua's managed store, which becomes authoritative for that layer. Edit capabilities must be supported and permitted; a PostGIS source alone does not imply write-through editing.

A renderer's “layer” is different: it describes how features are drawn inside a [map](map.md). Several drawing layers can use one source. An OGC collection or GeoServices REST layer can be a protocol projection of a published layer, but resolve the returned identifier instead of assuming names match across protocols.

“Add a layer” may mean publish server data or add a reference to a map. Identify which object should exist afterward.

[^contract]: [Source contract](../../reference/admin-api/connections-and-layers.md).
