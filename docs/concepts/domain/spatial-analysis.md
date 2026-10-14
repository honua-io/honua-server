---
type: concept
title: "Filtering and spatial analysis"
description: "A question over attributes or geometry with explicit predicates, units, and result meaning."
tags: [gis, domain-language]
aliases: ["filter","buffer","nearby","within","intersect","select","distance"]
sources:
  - id: contract
    resource: "/guides/query-analyze/run-geoprocessing.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/crs.md"
  - predicate: relates_to
    target: "/concepts/domain/task.md"
  - predicate: relates_to
    target: "/guides/workflows/analyze-features.md"
  - predicate: relates_to
    target: "/concepts/domain/map.md"
---

# Filtering and spatial analysis
An attribute filter selects records by values; a spatial predicate relates geometry. A buffer creates an area around geometry using a distance contract. Selection, derived geometry, and aggregation produce different outputs.

“Nearby,” “inside,” and “touching” are not interchangeable. Resolve the reference geometry, distance and [units](crs.md), boundary treatment, and whether the user wants selected source records, a derived polygon, or counts. Preserve source identities when the result must be traced back.

Single-geometry buffer and layer-based analysis are different [tasks](task.md); inspect the advertised input type instead of assuming every buffer reads a published layer.[^contract]

Use [analyze features](../../guides/workflows/analyze-features.md). Validate the operation and check output geometry, extent, schema, and relevant membership. A visually plausible [map](map.md) alone does not establish the analysis result.

[^contract]: [Source contract](../../guides/query-analyze/run-geoprocessing.md).
