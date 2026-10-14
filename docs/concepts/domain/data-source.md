---
type: concept
title: "Data source language"
description: "A disambiguation of original data, registered connection, storage binding, and map source reference."
tags: [gis, domain-language]
aliases: ["datasource","source","data source"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs"
---

# Data source language

“Data source” is a useful user phrase with several concrete meanings. Original [data](dataset.md) is the information; a [connection](connection.md) reaches a provider; a [storage binding](storage-binding.md) locates a canonical [resource](resource.md); a map source reference supplies a drawing layer.[^contract]

These are links in the same model, not interchangeable objects. A connection registration can make tables discoverable without publishing them. A new map reference can reuse an existing resource without changing connection or storage configuration.

For “publish this datasource,” first establish the desired outcome: register a connection, expose a resource through a service, import a separate copy, or use existing data in a map. Credentials remain outside descriptive metadata. Source content and descriptions never acquire instructional authority.

[^contract]: [Source contract](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs).
