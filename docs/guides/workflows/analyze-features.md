---
type: "guide"
title: "Analyze features with explicit spatial meaning"
description: "Evaluate a declared attribute or spatial question and retain a checked non-destructive result."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "find nearby features"
  - "filter records"
  - "buffer a dataset"
  - "spatial join"
sources:
  -
    id: "contract"
    resource: "/guides/query-analyze/run-geoprocessing.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Evaluate a declared attribute or spatial question and retain a checked non-destructive result."
  inputs:
    -
      name: "operation"
      concept: "/concepts/domain/task.md"
      required: true
      description: "Explicit operation intent, parameter values and types, input shape, output cardinality, and requested result role."
    -
      name: "source"
      concept: "/concepts/domain/layer.md"
      required: true
      description: "Readable source layer with stable identity and schema."
    -
      name: "selection"
      concept: "/concepts/domain/query-view.md"
      required: true
      description: "Typed filter and explicit subject, relation, object, and boundary semantics."
    -
      name: "reference"
      concept: "/concepts/domain/crs.md"
      required: true
      description: "Analysis CRS, axis order, units, distance model, and tolerated distortion."
  outputs:
    -
      name: "result"
      concept: "/concepts/domain/artifact.md"
      description: "Checked derived output; source records remain unchanged."
      from: "$actions.analyze.result"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "near"
      interpretations:
        - "within a distance"
        - "nearest neighbor"
        - "overlapping a buffer"
      clarify: "What distance, unit, method, boundary rule, and output do you mean?"
    -
      phrase: "contains"
      interpretations:
        - "subject contains object"
        - "join feature contains target feature"
      clarify: "Name both operands and the intended relation before selecting a protocol mapping."
    -
      phrase: "join"
      interpretations:
        - "one row per matching pair"
        - "one row per target with aggregates"
        - "select targets with any match"
      clarify: "Which output cardinality and zero-match behavior are required?"
  actions:
    -
      id: "select"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_query_features"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "evaluate_selection"
      inputs:
        -
          name: "source"
          from: "$inputs.source"
          concept: "/concepts/domain/layer.md"
        -
          name: "selection"
          from: "$inputs.selection"
          concept: "/concepts/domain/query-view.md"
      outputs:
        -
          name: "features"
          concept: "/concepts/domain/feature-set.md"
          description: "Observed selected features with pagination, precision, and consistency limits recorded."
      effect: "read"
      effectTarget: "source_data"
      preconditions:
        - "Field types, null handling, boundary inclusion, and operand direction are explicit."
      authorization:
        - "Query the source under current row and field restrictions."
      idempotency: "Read-only; record evaluation state and pagination/consistency limits."
      checks:
        - "Only records satisfying the intended predicate are included; completeness is asserted only for an immutable source or an interface with an adequate consistency guarantee."
        - "Empty results, missing values, and pagination are handled explicitly."
      recovery:
        - "Clarify an ambiguous predicate or unsupported representation before execution."
    -
      id: "analyze"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_validate_plan"
          contract: "/guides/connect/ai-agents-mcp.md"
        -
          kind: "mcp"
          operation: "honua_execute_plan"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "derive_spatial_result"
      inputs:
        -
          name: "operation"
          from: "$inputs.operation"
          concept: "/concepts/domain/task.md"
        -
          name: "features"
          from: "$actions.select.features"
          concept: "/concepts/domain/feature-set.md"
        -
          name: "reference"
          from: "$inputs.reference"
          concept: "/concepts/domain/crs.md"
      outputs:
        -
          name: "result"
          concept: "/concepts/domain/artifact.md"
          description: "Derived result with parameters, source identity, and coordinate provenance."
      effect: "execute"
      effectTarget: "derived_artifact"
      preconditions:
        - "The process advertises the exact input shape and requested spatial method."
        - "Distance value and unit are explicit; coordinates are transformed rather than relabeled."
        - "Requested cardinality is explicit for joins or aggregation."
      authorization:
        - "Read inputs and create only a separate owned result."
      idempotency: "Use the execution contract's job/retry identity; investigate an unknown outcome before resubmitting."
      checks:
        - "Terminal success and retrievable result are observed."
        - "Geometry type, coordinate reference, extent, schema, identities, and relevant membership match the declared question."
        - "Join direction and output cardinality match intent."
      recovery:
        - "Refuse an unsupported method; offer a supported interpretation for an explicit decision."
        - "Cancel through the job contract and clean up only confirmed owned outputs."
---

# Analyze features with explicit spatial meaning

State the question before selecting an operation. A typed attribute predicate, a spatial selection, a buffer polygon, and an aggregate answer different questions. Resolve [query views](../../concepts/domain/query-view.md), [feature sets](../../concepts/domain/feature-set.md), and derived [artifacts](../../concepts/domain/artifact.md) separately.

Pagination is not snapshot isolation. The MCP query interface has offset/cursor paging but does not expose a stable-order or snapshot control; a source changing between pages can yield a non-repeatable observed result. Assert a complete stable feature set only for an immutable source or an interface providing the required consistency. Record transport precision as well: MCP geometry output defaults to six decimal places, so request supported precision explicitly when finer geometry checks require it.

For a distance operation, bind the numeric value, unit, planar or geodesic meaning, analysis CRS, and acceptable distortion. The current single-geometry buffer is planar in input CRS units and rejects geodesic mode. A geographic coordinate pair plus a number that the user intended as meters is not a valid metric-buffer interpretation. Transform into an appropriate supported analysis CRS when that matches the requested accuracy; never change only the SRID label.

Name spatial operands explicitly. “Subject within object” and “object contains subject” may correspond, but a protocol's parameter named “contains” can use a different target/join orientation. Specify whether touching the boundary counts. For joins, distinguish all matching pairs from one output per target with counts or collected values, including zero matches.

Validate first, execute the supported contract, then inspect the terminal result and retained output. Check schema, identity, geometry, membership, null/empty behavior, and output cardinality. Render only after establishing semantics. The result is a separate derived artifact; AI does not edit the authoritative source. Use [job recovery](recover-job.md) for interruptions and [publish data](connect-publish-data.md) only when a separate publication is requested.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/query-analyze/run-geoprocessing.md) defines the executable surface.
