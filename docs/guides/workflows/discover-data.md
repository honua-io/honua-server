---
type: "guide"
title: "Discover and disambiguate data"
description: "Resolve the user's data description to readable, identified resources with known meaning."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "find a dataset"
  - "search layers"
  - "show available data"
sources:
  -
    id: "contract"
    resource: "/guides/connect/ai-agents-mcp.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Resolve the user's data description to readable, identified resources with known meaning."
  inputs:
    -
      name: "catalog"
      concept: "/concepts/domain/catalog.md"
      required: true
      description: "The authenticated catalog and search intent."
  outputs:
    -
      name: "layer"
      concept: "/concepts/domain/layer.md"
      description: "Selected published resource; no data was changed."
      from: "$actions.inspect.selected"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "latest"
      interpretations:
        - "latest observation date"
        - "latest publication"
        - "latest catalog update"
      clarify: "Which date determines suitability?"
  actions:
    -
      id: "search"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_list_layers"
          contract: "/guides/connect/ai-agents-mcp.md"
        -
          kind: "mcp"
          operation: "honua_ground_candidates"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "discover_resources"
      inputs:
        -
          name: "catalog"
          from: "$inputs.catalog"
          concept: "/concepts/domain/catalog.md"
      outputs:
        -
          name: "candidate"
          concept: "/concepts/domain/layer.md"
          description: "A readable candidate layer with a stable identifier."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Search scope, geography, topic, and intended use are understood."
      authorization:
        - "Metadata discovery for the current actor and tenant."
      idempotency: "Read-only; follow documented pagination."
      checks:
        - "Candidates include provenance, coverage, fields, and CRS."
        - "Competing matches are surfaced rather than chosen by title alone."
      recovery:
        - "Clarify ambiguous matches; do not widen tenant or credential scope."
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_describe_layer"
          contract: "/guides/connect/ai-agents-mcp.md"
        -
          kind: "mcp"
          operation: "honua_query_features"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_candidate"
      inputs:
        -
          name: "layer"
          from: "$actions.search.candidate"
          concept: "/concepts/domain/layer.md"
      outputs:
        -
          name: "selected"
          concept: "/concepts/domain/layer.md"
          description: "The selected resource with verified suitability."
      effect: "read"
      effectTarget: "source_data"
      preconditions:
        - "The candidate identifier resolves under current authority."
      authorization:
        - "Read the relevant metadata and representative data."
      idempotency: "Read-only validation."
      checks:
        - "Schema and representative values support the user's intended question."
        - "Coverage, temporal meaning, source rights, and access limitations are stated."
      recovery:
        - "Return to discovery or explain that no suitable readable resource was found."
---

# Discover and disambiguate data

Translate the user's topic into candidate searches, preserving their geographic and temporal intent. A title is only a clue. Examine coverage, publisher, timestamp meaning, schema, geometry, and access before selecting a resource.

Follow the catalog's pagination and visibility rules. An empty response may reflect permissions; it does not justify probing another tenant. Read representative records only through authorized surfaces.

When several candidates fit, explain the meaningful difference and resolve the choice. For example, a facility inventory and a live availability feed can share a title while answering different questions.

Return a stable resource identifier and why it fits. This workflow changes neither the data nor its policy. Continue with [analysis](analyze-features.md) or [map composition](compose-map.md).

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/connect/ai-agents-mcp.md) defines the executable surface.
