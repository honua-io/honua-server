---
type: concept
title: "Storage binding"
description: "The Metadata v2 link from a logical resource to its physical provider and locator."
tags: [gis, domain-language]
aliases: ["physical binding","table binding","storage layer"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs"
---

# Storage binding

A Metadata v2 storage binding materializes a [resource](resource.md). It records `resourceId`, optional `connectionId`, `storageType`, `locator`, and binding capabilities. A `storageLayerId` can identify the resource within the store; it is distinct from a publication's service-local identifier.[^contract]

A [connection](connection.md) supplies the provider and safe endpoint/secret reference. A publication may select a binding while exposing the resource through a service. Changing a binding can therefore change what several consumers read; it is not just renaming a map overlay.

“Move the data source” needs to distinguish changing a locator from copying records. Inspect shared references, desired storage semantics, credentials, and effective capabilities before a catalog change. AI does not modify source features to make a binding fit.

[^contract]: [Source contract](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs).
