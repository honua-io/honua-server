---
type: concept
title: "Filtering and spatial analysis"
description: "A question over attributes or geometry with explicit predicates, units, and result meaning."
tags: [gis, domain-language]
aliases: ["filter","buffer","nearby","near","close to","within","intersect","select","distance","short drive"]
sources:
  - id: contract
    resource: "/guides/query-analyze/run-geoprocessing.md"
relations:
  - predicate: interpreted_with
    target: /concepts/domain/intent-interpretation.md
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

People can ask "what is near this school?" without naming a predicate or coordinate system. Reuse the selected school and any distance and measurement method already established. If proximity remains materially ambiguous, ask about the missing choice, such as distance versus travel time. Inspect available technical metadata yourself; do not make the user fill in every analytical parameter. See [contextual interpretation](intent-interpretation.md).

“Nearby,” “inside,” and “touching” are not interchangeable. Resolve the reference geometry, distance and [units](crs.md), boundary treatment, and whether the user wants selected source records, a derived polygon, or counts. Preserve source identities when the result must be traced back.

Single-geometry buffer and layer-based analysis are different [tasks](task.md); inspect the advertised input type instead of assuming every buffer reads a published layer.[^contract]

Use [analyze features](../../guides/workflows/analyze-features.md). Validate the operation and check output geometry, extent, schema, and relevant membership. A visually plausible [map](map.md) alone does not establish the analysis result.

[^contract]: [Source contract](../../guides/query-analyze/run-geoprocessing.md).
