---
type: concept
title: "Dataset naming transition"
description: "The historical resource name is being replaced by Dataset in the model, stored metadata, and APIs."
tags: [gis, domain-language]
aliases: ["logical resource","canonical dataset"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs"
---

# Dataset naming transition

Use [Dataset](dataset.md) for the collection of records, features, or imagery with logical identity and data semantics. The model and product use the same word. A [storage binding](storage-binding.md) records physical access; a [Layer](layer.md) exposes the Dataset in a [Service](service.md).

The current Metadata v2 implementation uses `MetadataV2Resource` and fields such as `resourceId` for this identity.[^contract] These are legacy names awaiting migration, not internal-only vocabulary. The legacy type also carries Style and document values; their existing references must survive migration to explicit Style, Map, or App concepts without being reclassified as data. See the [implementation transition](README.md#implementation-transition) before choosing literal API names.

A Dataset identifier, a storage handle, and a Service-local Layer identifier have different scopes. Publishing a Dataset through another Service does not inherently duplicate its data.

[^contract]: [Current implementation](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs).
