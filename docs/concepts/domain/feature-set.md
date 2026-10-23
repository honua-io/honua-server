---
type: concept
title: "Feature set snapshot"
description: "A bounded collection of identified features and attributes evaluated at a particular state."
tags: [gis, domain-language]
aliases: ["selected features", "result rows", "feature collection", "snapshot"]
sources:
  - id: query
    resource: /guides/query-analyze/query-features.md
relations:
  - predicate: derived_from
    target: /concepts/domain/dataset.md
  - predicate: evaluated_from
    target: /concepts/domain/query-view.md
---
# Feature set snapshot

A feature set snapshot is the concrete records returned or materialized by a query at an observed state. It includes feature identities, readable attributes, geometry, and spatial-reference information as exposed by the chosen query contract.[^query]

This workflow type describes result semantics; it does not invent a new server resource or promise a transactionally consistent snapshot across arbitrary paginated requests. A complete result must follow the endpoint's pagination and consistency contract.

A [query view](query-view.md) is a rule that can yield different rows later. A [map drawing layer](map-layer.md) is a presentation of data. Neither is interchangeable with a complete set of returned features.

“Keep these results” requires a durable output and provenance if they must survive the session. Preserve identity, schema, null values, geometry type, coordinate reference, and the query used. An empty set can be a valid result; it is not the same as a failed query.

[^query]: [Query features](../../guides/query-analyze/query-features.md).
