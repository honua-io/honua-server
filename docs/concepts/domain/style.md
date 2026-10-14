---
type: concept
title: "Style and labels"
description: "Drawing rules for a layer or composition, separate from its underlying features."
tags: [gis, domain-language]
aliases: ["symbology","colors","legend","labels","renderer"]
sources:
  - id: contract
    resource: "/concepts/data-model.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/map.md"
  - predicate: relates_to
    target: "/guides/workflows/compose-map.md"
  - predicate: relates_to
    target: "/concepts/domain/capability.md"
---

# Style and labels
A style controls visual representation: symbols, colors, widths, and related drawing rules. Labels bind text to fields or expressions. Honua exposes layer styling through supported protocol and composition surfaces.[^contract]

Changing the [map](map.md) appearance does not change source feature attributes. A catalog layer default and a style override inside one composition have different scope; identify which the user intends.

“Make busy roads red” needs an actual field or other rule defining “busy.” “Label by name” needs the correct readable field, treatment of missing values, and a useful map scale. A legend should explain the visible categories rather than merely repeat layer titles.

Preview the appearance and preserve attribution. Use [compose a map](../../guides/workflows/compose-map.md), and verify effective [capabilities](capability.md) before assuming one renderer's style options transfer unchanged to another.

[^contract]: [Source contract](../../concepts/data-model.md).
