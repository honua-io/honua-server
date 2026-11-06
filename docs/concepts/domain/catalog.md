---
type: concept
title: "Catalog and discovery"
description: "Discoverable metadata about resources or operations within the caller's authority."
tags: [gis, domain-language]
aliases: ["find data","search layers","browse","inventory","available tools"]
sources:
  - id: contract
    resource: "/guides/connect/ai-agents-mcp.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/metadata.md"
  - predicate: relates_to
    target: "/concepts/domain/crs.md"
  - predicate: relates_to
    target: "/guides/workflows/discover-data.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
---

# Catalog and discovery
A data catalog helps identify published resources. A capability or tool catalog describes operations. These are related discovery surfaces, not the same inventory.

How a published layer, scene, or process becomes findable on each protocol is [How published data becomes findable](../../guides/catalog/catalog-and-discovery.md).

Honua MCP grounding and resource discovery respect readable publications and metadata access. Tool listings and workflow views describe available contracts but do not grant authority; calls are authorized independently.[^contract]

“Find roads” can match multiple datasets with different coverage, dates, publishers, and access. Resolve a stable resource identifier and inspect [metadata](metadata.md), fields, geometry, and [CRS](crs.md). Do not choose solely by the first matching title.

Use [discover data](../../guides/workflows/discover-data.md), including pagination where exposed. An empty result can reflect scope or [permissions](permissions.md); it is not permission to probe another tenant or bypass a policy.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
