---
type: concept
title: "Go live and activation"
description: "Activating a saved Map or App Version at its governed route, with the current authority contract."
tags: [gis, domain-language]
aliases: ["go live","activate","release a map","released"]
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

# Go live and activation

**Go live** means activate a saved [Map](map.md) or [App](app.md) Version at its governed route. That Version is **Released**. Saving and previewing do not activate a route. The [Version lifecycle](content-version.md) defines the authoring states.

Interpret “publish this” using the current object, destination, and audience. Exposing a [Dataset](dataset.md) creates a [Layer](layer.md) in a [Service](service.md); activating a saved Map or App Version releases it. These are separate actions. Preserve existing access unless the user requests a change. Ask only about unresolved choices that change the outcome; see [contextual interpretation](intent-interpretation.md).

Publication is a retired object name. The existing Metadata v2 `MetadataV2Publication` links a Dataset to a Service and is being renamed Layer. The Studio API also uses publication in existing route and response names for activation. Consult current API references for those literals until the [model and API transition](README.md#implementation-transition) lands. This page retains the current activation authority contract while that work is pending.

A Connection registration exposes no data or credential material. A Style change affects drawing rules, not source features. Resolve the actual object and supported action rather than applying one generic publish operation to every noun.

For Studio publication through MCP, the current saved version supplies the
version identifier and content hash. An authorized admin publishes in the call
and receives `Published` with a share URL. Other callers receive
`AwaitingApproval` and a proposal handle; a separate authorized principal must
approve before activation. Inspect the returned state rather than treating the
word “propose” as a guarantee of no immediate effect. A pending proposal is not
an active publication, and a preview is not a published artifact.[^contract]

Activation and [sharing](sharing.md) are related but distinct. “Public” must refer to the effective audience, including access to dependent data; it cannot be inferred from a URL. A Released Map or App can require authentication.

Use [publish and share a map](../../guides/workflows/publish-share-map.md). Recheck the resulting status and route. The [Studio release boundary](../../studio/README.md#release-boundary) remains a separate qualification statement.

[^contract]: [Studio publication contract](../../studio/mcp-tools.md).
