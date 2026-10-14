---
type: "guide"
title: "Clean up owned outputs and configuration"
description: "Remove only authorized owned resources while preserving source data and shared dependencies."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "remove my test resources"
  - "clean up"
  - "undo this setup"
sources:
  -
    id: "contract"
    resource: "/reference/admin-api/connections-and-layers.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Remove only authorized owned resources while preserving source data and shared dependencies."
  inputs:
    -
      name: "inventory"
      concept: "/concepts/domain/catalog.md"
      required: true
      description: "Explicit resource identities, ownership, creation lineage, and dependency inventory."
  outputs:
    -
      name: "inventory"
      concept: "/concepts/domain/catalog.md"
      description: "Verified cleanup outcome and explicit retained resources."
      from: "$actions.cleanup.inventory"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "remove the layer"
      interpretations:
        - "hide a drawing layer"
        - "remove a map reference"
        - "unpublish a resource"
        - "delete data"
      clarify: "Which object should disappear? Source-data deletion is outside the AI workflow."
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_list_layers"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "inspect_cleanup_scope"
      inputs:
        -
          name: "inventory"
          from: "$inputs.inventory"
          concept: "/concepts/domain/catalog.md"
      outputs:
        -
          name: "inventory"
          concept: "/concepts/domain/catalog.md"
          description: "Confirmed owned cleanup set and retained dependency set."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Creation lineage distinguishes this work from pre-existing or shared resources."
      authorization:
        - "Read ownership and dependencies."
      idempotency: "Read-only inventory."
      checks:
        - "Authoritative source records/tables/files are excluded from AI deletion."
        - "Running jobs, publications, map references, keys, and shared storage are accounted for."
      recovery:
        - "Ask for an explicit scope decision when ownership or dependencies are uncertain."
    -
      id: "cleanup"
      verb: "remove_owned_resources"
      inputs:
        -
          name: "inventory"
          from: "$actions.inspect.inventory"
          concept: "/concepts/domain/catalog.md"
      outputs:
        -
          name: "inventory"
          concept: "/concepts/domain/catalog.md"
          description: "Final inventory of removed resources, retained dependencies, and unresolved items."
      effect: "delete"
      effectTarget: "catalog_configuration"
      preconditions:
        - "Workers are terminal and each deletion is supported and explicitly authorized."
        - "Delete only owned catalog/configuration objects here; derived artifact removal uses its own scoped contract."
      authorization:
        - "Delete these exact owned resources; no source-data mutation authority is inferred."
      idempotency: "Treat already-absent owned objects as observed absence only when the runtime contract allows it."
      checks:
        - "Removed resources are absent and retained shared resources remain usable."
        - "No running worker can recreate removed outputs."
        - "Source data remains unchanged."
      recovery:
        - "Report any residual resources and their owner; do not broaden deletion patterns."
      rollback:
        snapshot: "Read and retain the prior object identity, content, generation or resource revision, and dependencies."
        revisionGuard: "Verify the current revision before compensation; preserve unrelated concurrent changes."
        scope: "Only the inventoried owned resources, never a broad namespace or source table."
        compensation: "Use existing resource history/reapply mechanisms where supported; treat permanent deletion as irreversible."
        verification:
          - "Read the result through the same authority boundary."
          - "Verify dependency access and the intended previous state."
        nonReversible:
          - "Deleted credentials or artifacts may not be recoverable."
          - "External copies and billing cannot be undone."
---

# Clean up owned outputs and configuration

Start from explicit resource identities and creation lineage. “Everything for this project” is not a safe deletion scope when connections, storage, services, or source tables are shared.

Stop or cancel owned jobs through their existing control surface, and wait for terminal confirmation. Remove owned publication/configuration references in dependency order. Deleting a map drawing layer is not permission to delete its data.

Authoritative source data stays unchanged, even when it is in a workspace associated with the task. Separately created derived outputs may be cleaned up only through a supported, explicitly scoped artifact operation after dependency checks. Never turn a cleanup request into broad feature, table, or bucket deletion.

Verify absence of removed objects and continued access to retained dependencies. Report residual resources and irreversible deletions honestly. Use existing revision history where available; do not promise a universal undo.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../reference/admin-api/connections-and-layers.md) defines the executable surface.
