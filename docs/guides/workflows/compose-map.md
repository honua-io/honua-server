---
type: "guide"
title: "Compose a map from readable layers"
description: "Create or update a map composition with explicit drawing order, styles, and a checked preview."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "make a map"
  - "add an overlay"
  - "style the map"
  - "label features"
sources:
  -
    id: "contract"
    resource: "/studio/mcp-tools.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Create or update a map composition with explicit drawing order, styles, and a checked preview."
  inputs:
    -
      name: "map"
      concept: "/concepts/domain/map.md"
      required: true
      description: "New or existing draft composition with current generation."
    -
      name: "layer"
      concept: "/concepts/domain/layer.md"
      required: true
      description: "Readable published data resource selected for this composition."
    -
      name: "style"
      concept: "/concepts/domain/style.md"
      required: true
      description: "Drawing and label intent resolved to fields and rules."
  outputs:
    -
      name: "map"
      concept: "/concepts/domain/map.md"
      description: "Checked mutable map; saving and publication remain separate."
      from: "$actions.preview.map"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "add a layer"
      interpretations:
        - "publish a server resource"
        - "add a drawing reference"
        - "duplicate a drawing style"
      clarify: "Which new object should exist, and in which scope?"
  actions:
    -
      id: "compose"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_studio_add_layer"
          contract: "/studio/mcp-tools.md"
        -
          kind: "mcp"
          operation: "honua_studio_set_layer_style"
          contract: "/studio/mcp-tools.md"
      verb: "compose_drawing_layer"
      inputs:
        -
          name: "map"
          from: "$inputs.map"
          concept: "/concepts/domain/map.md"
        -
          name: "layer"
          from: "$inputs.layer"
          concept: "/concepts/domain/layer.md"
        -
          name: "style"
          from: "$inputs.style"
          concept: "/concepts/domain/style.md"
      outputs:
        -
          name: "map"
          concept: "/concepts/domain/map.md"
          description: "Updated draft with the intended drawing element."
      effect: "update"
      effectTarget: "map_content"
      preconditions:
        - "Referenced data and label fields are readable."
        - "Latest draft generation and the intended layer scope are known."
      authorization:
        - "Modify this draft; data read authority remains separate."
      idempotency: "Use generation checks and reconcile conflicts before replay."
      checks:
        - "The drawing layer points to the correct data resource."
        - "Order, visibility, styling, and view state match the intended presentation."
      recovery:
        - "Restore the prior draft composition only if no competing edit occurred."
      rollback:
        snapshot: "Record the pre-change resource identifiers, state, revision, and dependencies."
        revisionGuard: "Re-read current state; stop compensation if the resource changed outside this action."
        scope: "Only the edited map draft and its composition."
        compensation: "Restore the prior draft state with a current generation check."
        verification:
          - "Read the affected resources again and verify the intended prior state under the same identity."
          - "Verify dependencies and access through the affected public surface."
        nonReversible:
          - "A preview may already have been observed; source data and shared styles are not implicitly reverted."
    -
      id: "preview"
      verb: "validate_preview"
      inputs:
        -
          name: "map"
          from: "$actions.compose.map"
          concept: "/concepts/domain/map.md"
      outputs:
        -
          name: "map"
          concept: "/concepts/domain/map.md"
          description: "Validated draft with an observed preview."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "The installed renderer supports this composition."
      authorization:
        - "Read every dependency and preview the draft."
      idempotency: "Read-only preview."
      checks:
        - "All intended drawing layers render in the expected order and location."
        - "Labels, legend, attribution, empty-result behavior, and viewport are meaningful."
      recovery:
        - "Diagnose missing data, CRS, style, or renderer limitations without declaring a partial map complete."
---

# Compose a map from readable layers

Resolve [published layers](../../concepts/domain/layer.md) before creating [drawing layers](../../concepts/domain/map-layer.md). A composition records source references, drawing order, style, labels, and view state. It does not copy every feature into the map document.

Use the current draft generation and preserve unrelated edits. “Make the important records stand out” needs a field or rule defining importance. Changing a catalog default can affect other consumers, so prefer the scope the user actually requested.

Ask for the outcome in GIS terms: “Map these parcels by zoning, keep the existing overlays, show a legend, and save a version I can reopen. Tell me which checks passed before publishing.” The agent resolves the selected published data and styling fields rather than asking you to supply internal identifiers.

For catalog-backed composition, the agent copies the discovered `studioSourceId` from `honua_list_layers` or `honua_describe_layer` into the drawing layer's `sourceId`. The drawing layer's own `id` identifies that presentation within the map. Ordinary catalog editing does not require portable `sourceBindings` or a `mapSpec` export; those and supported inline overrides are separate source mechanisms. Discovery leaves data access subject to the current caller's permissions.

`honua_studio_validate_draft` checks structure, and `honua_studio_preview_draft` returns a plan. Source access and canvas rendering remain unverified after either result. A `honua_render_map` image previews the requested published layers, independently of the saved Studio composition. The visual preview action above requires an actual Studio canvas or another supported consumer of that exact composition; it has no equivalent MCP render call.

Preview the result with the real renderer. Check all layers, ordering, labels, legend, attribution, and useful extent. A blank map could be a legitimate empty selection or a broken dependency; establish which.

This workflow ends with a checked draft. [Save and resume](save-resume-map.md) makes it durable, while [publish and share](publish-share-map.md) addresses audience and activation.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../studio/mcp-tools.md) defines the executable surface.
