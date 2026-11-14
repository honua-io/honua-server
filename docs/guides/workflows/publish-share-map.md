---
type: "guide"
title: "Publish and share a saved map"
description: "Make the selected saved version available to the intended audience through the actual publication policy."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "publish my map"
  - "share a private map"
  - "make a map public"
sources:
  -
    id: "contract"
    resource: "/guides/connect/ai-agents-mcp.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Make the selected saved version available to the intended audience through the actual publication policy."
  inputs:
    -
      name: "version"
      concept: "/concepts/domain/content-version.md"
      required: true
      description: "Exact saved map version and content hash."
    -
      name: "audience"
      concept: "/concepts/domain/sharing.md"
      required: true
      description: "Explicit audience, route, and dependency-access intent."
  outputs:
    -
      name: "publication"
      concept: "/concepts/domain/publication.md"
      description: "Verified active publication when all checks complete."
      from: "$actions.verify.publication"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "public"
      interpretations:
        - "anonymous artifact route"
        - "public data endpoints"
        - "team-accessible link"
      clarify: "Who should read the artifact and each data dependency?"
  actions:
    -
      id: "inspect"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_studio_get_version"
          contract: "/studio/mcp-tools.md"
        -
          kind: "mcp"
          operation: "honua_validate_package"
          contract: "/guides/connect/ai-agents-mcp.md"
      verb: "validate_publication_intent"
      inputs:
        -
          name: "version"
          from: "$inputs.version"
          concept: "/concepts/domain/content-version.md"
        -
          name: "audience"
          from: "$inputs.audience"
          concept: "/concepts/domain/sharing.md"
      outputs:
        -
          name: "version"
          concept: "/concepts/domain/content-version.md"
          description: "The validated exact version to publish."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Composition and dependency validation are current."
      authorization:
        - "Read the version and required dependency metadata."
      idempotency: "Read-only checks."
      checks:
        - "Route and audience are explicit."
        - "The requested audience can be evaluated against data dependencies without silently widening them."
      recovery:
        - "Resolve missing rights or clarify audience before submitting."
    -
      id: "submit"
      interfaces:
        -
          kind: "mcp"
          operation: "honua_studio_propose_publication"
          contract: "/studio/mcp-tools.md"
      verb: "submit_publication"
      inputs:
        -
          name: "version"
          from: "$actions.inspect.version"
          concept: "/concepts/domain/content-version.md"
        -
          name: "audience"
          from: "$inputs.audience"
          concept: "/concepts/domain/sharing.md"
      outputs:
        -
          name: "request"
          concept: "/concepts/domain/publication.md"
          description: "Publication proposal with exact version, operation and proposal identities; AwaitingApproval until a person approves it in Console."
      effect: "propose"
      effectTarget: "catalog_configuration"
      preconditions:
        - "The current saved version and hash match the intended publication; the caller is authorized for the requested route and visibility."
      authorization:
        - "Every MCP caller is an agent, administrators included, so the call files a proposal that a person approves in Console."
      idempotency: "Track the existing publication operation or proposal after a timeout; reconcile state before resubmission."
      checks:
        - "The result binds the selected version/hash and intended audience."
        - "AwaitingApproval remains pending; Published is confirmed only after a person approves and the server issues the share URL."
        - "Do not approve the proposal from the agent or with another credential."
      recovery:
        - "Follow the pending proposal to its decision; only a person in Console can approve or reject it."
      rollback:
        snapshot: "Record the prior published version, route, audience, and current saved version before submission."
        revisionGuard: "Re-read publication state; preserve changes made after this operation."
        scope: "Only this item's publication selection and requested route/access change."
        compensation: "Use the installed lifecycle rollback/unpublish contract under its own authority. If unavailable, report that limitation; do not modify approval storage."
        verification:
          - "Read back the selected publication and check access under the intended viewer."
        nonReversible:
          - "Rollback cannot recall content or data already downloaded by viewers."
    -
      id: "observe"
      verb: "observe_publication"
      inputs:
        -
          name: "request"
          from: "$actions.submit.request"
          concept: "/concepts/domain/publication.md"
      outputs:
        -
          name: "active"
          concept: "/concepts/domain/publication.md"
          description: "Confirmed active publication; an incomplete or rejected outcome does not produce this successful output."
      effect: "observe"
      effectTarget: "observation"
      preconditions:
        - "The returned publication state is observable; a pending non-admin proposal requires a separate authorized decision before activation."
      authorization:
        - "Read the caller-visible publication status."
      idempotency: "Poll boundedly using the returned identity."
      checks:
        - "Only confirmed activation is called published."
        - "The active version matches the submitted version and any required approval decision."
      recovery:
        - "On rejection, timeout, or failure, report the exact state and retain the saved version."
    -
      id: "verify"
      verb: "verify_audience"
      inputs:
        -
          name: "publication"
          from: "$actions.observe.active"
          concept: "/concepts/domain/publication.md"
        -
          name: "audience"
          from: "$inputs.audience"
          concept: "/concepts/domain/sharing.md"
      outputs:
        -
          name: "publication"
          concept: "/concepts/domain/publication.md"
          description: "Publication with observed route and dependency access."
      effect: "read"
      effectTarget: "observation"
      preconditions:
        - "Use an intended viewer identity and an appropriate denied-viewer check."
      authorization:
        - "Read access is tested under each declared viewer's authority."
      idempotency: "Read-only checks."
      checks:
        - "The viewer can load the artifact and intended data."
        - "Viewers outside the intended audience do not gain access."
      recovery:
        - "Use the existing publication rollback/unpublish path under its own authority if exposure is wrong; verify resulting access."
---

# Publish and share a saved map

Routine [map composition](compose-map.md) stays in the direct edit-and-preview loop. This workflow is the separate transition that makes a saved version available through a serving route.

Identify the current saved version, hash, route, audience, and data dependencies.
Every MCP caller is an agent, administrators included, so
`honua_studio_propose_publication` always returns `AwaitingApproval`,
`humanConfirmationRequired: true`, a proposal handle, and no share URL. The
published pointer moves only after a person approves the proposal in Console;
see [Approve agent changes](../secure/approve-agent-changes.md). A pending result
stops short of the workflow's successful output.

See [Studio MCP tools](../../studio/mcp-tools.md) for this operation's contract.
Publication policy does not add approval to every creative edit or every other
operational API.

A pending proposal is not published. A public map document does not grant access to private layers, and a private map does not revoke an independently public data service. Check both the map and its dependencies using the intended viewer.

If access or content is wrong, use the existing governed publication rollback or unpublish operation, with its revision and authorization checks. It cannot recall content already downloaded by viewers. Preserve the immutable saved version and report any non-completion state accurately.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../guides/connect/ai-agents-mcp.md) defines the executable surface.
