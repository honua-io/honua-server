---
type: concept
title: "Storage binding"
description: "The Metadata v2 link from a Dataset to its physical provider and locator."
tags: [gis, domain-language]
aliases: ["physical binding","table binding","storage layer"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs"
---

# Storage binding

A Metadata v2 storage binding records physical access to a [Dataset](dataset.md). It records the Dataset reference, optional Connection reference, storage type, locator, and binding capabilities. The current implementation calls the Dataset reference `resourceId`; see the [naming transition](README.md#implementation-transition). A `storageLayerId` can identify the Dataset within the store; it is distinct from a Layer's Service-local identifier.[^contract]

A [connection](connection.md) supplies the provider and safe endpoint/secret reference. A Layer may select a binding while exposing the Dataset through a Service. Changing a binding can therefore change what several consumers read; it is not just renaming a map overlay.

“Move the data source” needs to distinguish changing a locator from copying records. Inspect shared references, desired storage semantics, credentials, and effective capabilities before a catalog change. AI does not modify source features to make a binding fit.

[^contract]: [Source contract](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core.Abstractions/Features/Metadata/Domain/V2/MetadataV2Graph.cs).
