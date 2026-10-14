---
type: concept
title: "Canonical resource"
description: "The Metadata v2 identity and semantics of a dataset, style, document, or other resource."
tags: [gis, domain-language]
aliases: ["logical resource","canonical dataset"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs"
---

# Canonical resource

Metadata v2 separates the logical resource from physical storage and protocol exposure. A resource carries identity, type, fields, spatial/temporal metadata, policies, relationships, and style references. It can reference multiple [storage bindings](storage-binding.md); document or derived resources need not have physical storage.[^contract]

A [publication](publication.md) connects a resource to a [service](service.md). Publishing the same resource through another service does not inherently duplicate its data. A storage-layer integer, a service-local layer index, and the canonical resource identifier are different identities.

“Publish this layer” must resolve the resource, storage binding, target service, and desired exposure. The [published layer](layer.md) page explains the familiar API vocabulary; the underlying graph remains Metadata v2, not a second ontology invented by this workflow profile.

[^contract]: [Source contract](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs).
