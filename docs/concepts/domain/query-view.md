---
type: concept
title: "Query view and filter"
description: "A selection rule over a resource, distinct from the records returned when it is evaluated."
tags: [gis, domain-language]
aliases: ["filter", "selection rule", "query", "view"]
sources:
  - id: query
    resource: /guides/query-analyze/query-features.md
relations:
  - predicate: selects_from
    target: /concepts/domain/layer.md
  - predicate: produces
    target: /concepts/domain/feature-set.md
---
# Query view and filter

A query view in this domain language describes a source resource and selection rule. It is a semantic description, not a promise of a persisted SQL view or a new query-view API. Evaluating it produces a [feature set](feature-set.md) under the source's current state and the caller's permissions.[^query]

“Only show open facilities” requires the relevant field, its type, and values that mean open. A missing or null status is not automatically false, true, or an empty string. Numeric comparison, text comparison, and matching a label are different predicates.

“Filter the map” may mean a drawing-only filter, a server query, or materializing a derived dataset. Establish which outcome the user needs. Display filtering does not prevent a viewer from querying data they otherwise have permission to read.

State operand direction and boundary treatment for spatial predicates. Do not map the word “contains” to every protocol without inspecting its subject/object convention. See [analyze features](../../guides/workflows/analyze-features.md).

[^query]: [Query features](../../guides/query-analyze/query-features.md).
