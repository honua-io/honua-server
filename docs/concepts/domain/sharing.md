---
type: concept
title: "Sharing and audience"
description: "The intended audience and effective access to an artifact and its dependencies."
tags: [gis, domain-language]
aliases: ["share","public link","private link","send a map"]
sources:
  - id: contract
    resource: "/guides/secure/access-control.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/concepts/domain/layer.md"
  - predicate: relates_to
    target: "/concepts/domain/approval.md"
  - predicate: relates_to
    target: "/guides/workflows/publish-share-map.md"
  - predicate: relates_to
    target: "/concepts/domain/identity.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
---

# Sharing and audience
Sharing identifies who should be able to open and use a resource: only its owner, authenticated collaborators, an authorized group, or anonymous viewers. A link identifies a location; it does not supply access rights.[^contract]

A [map](map.md) depends on [layers](layer.md), styles, and possibly other artifacts. A viewer who can load the map document can still lack permission to read its data. Conversely, restricting the map route does not revoke an independently public data endpoint.

“Make it public” needs explicit audience intent and any required [approval](approval.md). “Share with my team” needs a concrete identity or access group, not an assumption that every authenticated person belongs to the team.

In [publish and share](../../guides/workflows/publish-share-map.md), check the complete experience as an intended viewer and verify denial for a viewer outside the permitted audience. Keep [identity](identity.md) and [permissions](permissions.md) separate.

[^contract]: [Source contract](../../guides/secure/access-control.md).
