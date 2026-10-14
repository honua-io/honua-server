---
type: concept
title: "Map drawing layer"
description: "An ordered drawing element in a map composition, bound to a source and style."
tags: [gis, domain-language]
aliases: ["map layer", "drawing layer", "overlay", "visual layer"]
sources:
  - id: composition
    resource: "https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core/Features/Studio/Domain/StudioCompositionModels.cs"
relations:
  - predicate: part_of
    target: /concepts/domain/map.md
  - predicate: references
    target: /concepts/domain/layer.md
  - predicate: uses
    target: /concepts/domain/style.md
---
# Map drawing layer

A map drawing layer is one ordered visual element in a [map](map.md). Studio composition layers have their own identifier, a source identifier, a drawing type, visibility, and an optional style reference. Their order controls composition.[^composition]

A [published layer](layer.md) is a server data resource. Adding or removing a drawing layer changes the map; it does not by itself create or delete the published dataset. Several drawing elements may refer to one data source, such as an outline and a fill.

“Remove that layer” is therefore ambiguous. Confirm whether the user means hide it, remove its map reference, unpublish its service exposure, or delete owned data. These have different effects and permissions.

Use [compose a map](../../guides/workflows/compose-map.md) for drawing changes and [cleanup](../../guides/workflows/cleanup-resources.md) for resource removal.

[^composition]: [Studio composition model](https://github.com/honua-io/honua-server/blob/3fb2c48d15395f2e49a5a55e4a2445a3387e0145/src/Honua.Core/Features/Studio/Domain/StudioCompositionModels.cs).
