---
type: concept
title: "Service"
description: "A grouping and protocol exposure boundary for published layers."
tags: [gis, domain-language]
aliases: ["feature service","map service","GIS endpoint"]
sources:
  - id: contract
    resource: "/concepts/data-model.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/deployment.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/concepts/domain/capability.md"
  - predicate: relates_to
    target: "/guides/workflows/govern-access.md"
---

# Service
A service groups [layers](layer.md), enables selected protocols, and carries settings and an access policy. Protocols present different URLs and response shapes over the shared published resources.[^contract]

“Publish a service” concerns server exposure. “Deploy the service” can instead mean start the Honua process or roll out infrastructure: see [deployment](deployment.md). “Map service” is not the same object as a saved [map](map.md).

A permissive service policy does not override a restrictive layer policy. [Permissions](permissions.md), enabled protocols, provider support, and effective [capabilities](capability.md) all matter. Do not infer that every protocol or write operation is available because a service exists.

For example, an authenticated viewer may read one layer through an enabled endpoint while another layer in the service remains inaccessible. Verify the intended endpoint as that viewer in [govern access](../../guides/workflows/govern-access.md).

[^contract]: [Source contract](../../concepts/data-model.md).
