---
type: concept
title: "Publication"
description: "Making a specified resource or saved version available through its governed serving surface."
tags: [gis, domain-language]
aliases: ["publish","make available","release a map","activate"]
sources:
  - id: contract
    resource: "/guides/connect/ai-agents-mcp.md"
relations:
  - predicate: interpreted_with
    target: /concepts/domain/intent-interpretation.md
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/service.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/concepts/domain/sharing.md"
  - predicate: relates_to
    target: "/guides/workflows/publish-share-map.md"
  - predicate: relates_to
    target: "/studio/README.md"
---

# Publication

Interpret "publish this" using the current object and previously established destination and audience. A map draft and a database table lead to different publication workflows. Ask only about unresolved choices that change the outcome; preserve existing access unless the user requests a change. Understanding the phrase does not bypass the selected interface's operational policy. See [contextual interpretation](intent-interpretation.md).

“Publish” has several concrete meanings: register a [layer](layer.md), expose it through a [service](service.md), or activate a saved [map](map.md) version. Name the object, version, route, and intended audience.

In the canonical Metadata v2 graph, a publication links a [resource](resource.md) to a service, optionally selecting a [storage binding](storage-binding.md). Its protocol-facing identifier is service-local, distinct from the storage-layer handle. Several publications can expose one resource without copying its data. Studio saved-content activation is a separate lifecycle use of the word publication.

For a connection or data source, “publish” usually means register, validate, bind, and then expose the intended resource; it does not publish credential material. For a style, it means make the style resource available and bind its intended scope, not change source features. Resolve the actual object kind and runtime contract rather than applying one generic publish operation to all nouns.

For Studio publication through MCP, a saved version supplies the version identifier and content hash. The agent proposes publication; a separate authorized principal approves it. A pending proposal is not an active publication, and a preview is not a published artifact.[^contract]

Publication and [sharing](sharing.md) are related but distinct. “Public” must refer to the effective audience, including access to dependent data; it cannot be inferred from a URL. An internal publication can require authentication.

Use [publish and share a map](../../guides/workflows/publish-share-map.md). Recheck the resulting status and route. The [Studio release boundary](../../studio/README.md#release-boundary) remains a separate qualification statement.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
