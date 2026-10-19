---
type: concept
title: "Map"
description: "A visual composition of data references, drawing rules, and view state."
tags: [gis, domain-language]
aliases: ["web map","map composition","saved map","map view"]
sources:
  - id: contract
    resource: "/studio/mcp-tools.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/style.md"
  - predicate: relates_to
    target: "/concepts/domain/content-version.md"
  - predicate: relates_to
    target: "/concepts/domain/service.md"
  - predicate: relates_to
    target: "/concepts/domain/publication.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/guides/workflows/compose-map.md"
  - predicate: relates_to
    target: "/guides/workflows/save-resume-map.md"
  - predicate: relates_to
    target: "/studio/README.md"
---

# Map
A map combines references to data with ordered drawing [layers](layer.md), [styles](style.md), labels, and view state. A Studio draft stores a composition; saving creates an immutable [content version](content-version.md). Reopening a version creates a new mutable draft.[^contract]

A browser map visible on screen is not proof that a durable map was saved. A rendered image is another output, and a published [service](service.md) is a data interface rather than the map composition.

“Save my map” means preserve composition and identifiers. “Share my map” additionally needs an audience and an active [publication](publication.md) with readable data dependencies. Saving does not grant [permissions](permissions.md).

Use [compose a map](../../guides/workflows/compose-map.md), then [save and resume](../../guides/workflows/save-resume-map.md). Studio's [release boundary](../../studio/README.md#release-boundary) applies; these definitions do not assert that every browser publication journey is qualified.

[^contract]: [Source contract](../../studio/mcp-tools.md).
