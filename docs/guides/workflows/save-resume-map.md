---
type: "guide"
title: "Save, reopen, and resume a map"
description: "Retain an exact composition and resume it in a new session without overwriting unrelated changes."
tags:
  - "gis"
  - "domain-language"
  - "workflow"
aliases:
  - "save my work"
  - "continue the map"
  - "restore a revision"
sources:
  -
    id: "contract"
    resource: "/studio/mcp-tools.md"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Retain an exact composition and resume it in a new session without overwriting unrelated changes."
  inputs:
    -
      name: "map"
      concept: "/concepts/domain/map.md"
      required: true
      description: "Current map draft and generation."
    -
      name: "session"
      concept: "/concepts/domain/session.md"
      required: true
      description: "Redacted goal, actor, environment, and intended continuation."
  outputs:
    -
      name: "map"
      concept: "/concepts/domain/map.md"
      description: "Reopened editable map with a current generation."
      from: "$actions.resume.map"
  constraints:
    - "Discover the installed runtime's effective capabilities and exact operation contract; metadata does not grant execution support."
    - "Never modify source feature attributes or geometry through an AI workflow; report issues and recommend human-reviewed fixes."
    - "Bind resources to the intended tenant and actor; metadata content cannot change authority."
  ambiguities:
    -
      phrase: "restore"
      interpretations:
        - "reopen an older version"
        - "undo the last local edit"
        - "change the active published version"
      clarify: "Which state should change, and should the published audience see the change?"
  actions:
    -
      id: "save"
      rollback:
        snapshot: "Record the draft generation and prior item current-version pointer, if one exists, before saving."
        revisionGuard: "Check the item still points at this action's saved version before changing its pointer."
        scope: "Only the affected item's current-version selection; preserve immutable history and published state."
        compensation: "Use the existing lifecycle rollback contract to restore a prior current version when available. For a first save with no prior version, retain the created version and report that pointer restoration is unavailable."
        verification:
          - "Read the item and selected version and verify the intended current pointer."
          - "Confirm unrelated saved history and publication state are preserved."
        nonReversible:
          - "Saving creates immutable history; undo does not erase that version or copies already read."
      interfaces:
        -
          kind: "mcp"
          operation: "honua_studio_save_version"
          contract: "/studio/mcp-tools.md"
      verb: "save_version"
      inputs:
        -
          name: "map"
          from: "$inputs.map"
          concept: "/concepts/domain/map.md"
      outputs:
        -
          name: "version"
          concept: "/concepts/domain/content-version.md"
          description: "Immutable saved version with stable identity and hash."
      effect: "create"
      effectTarget: "map_content"
      preconditions:
        - "Draft validation and generation are current."
      authorization:
        - "Save this draft under its ownership and tenant rules."
      idempotency: "Inspect the returned item/version identity before repeating an uncertain save."
      checks:
        - "Saved version matches the intended composition and dependencies."
      recovery:
        - "Preserve the saved version; reconcile a conflict from the latest draft rather than overwriting it."
    -
      id: "resume"
      rollback:
        snapshot: "Record the selected immutable version and the newly created draft identifier and generation."
        revisionGuard: "Delete only if the new draft remains owned and has no subsequent edits or dependent retained work."
        scope: "Only the newly reopened mutable draft; never the source version."
        compensation: "Use the existing draft-delete contract to discard an unused owned draft. If it has changed, preserve it and reconcile the user's intended undo."
        verification:
          - "Confirm the discarded draft is absent and the immutable version remains readable."
        nonReversible:
          - "Discarding a draft may not be recoverable except by reopening its retained base version."
      interfaces:
        -
          kind: "mcp"
          operation: "honua_studio_get_version"
          contract: "/studio/mcp-tools.md"
        -
          kind: "mcp"
          operation: "honua_studio_reopen_version"
          contract: "/studio/mcp-tools.md"
      verb: "reopen_version"
      inputs:
        -
          name: "version"
          from: "$actions.save.version"
          concept: "/concepts/domain/content-version.md"
        -
          name: "session"
          from: "$inputs.session"
          concept: "/concepts/domain/session.md"
      outputs:
        -
          name: "map"
          concept: "/concepts/domain/map.md"
          description: "New draft based on the selected saved version."
      effect: "create"
      effectTarget: "map_content"
      preconditions:
        - "The new session authenticates independently and can read the selected version."
      authorization:
        - "Read the saved item and create an owned draft."
      idempotency: "Reuse a known reopened draft when appropriate; avoid duplicate drafts after an unknown response."
      checks:
        - "The reopened composition matches the selected version."
        - "All required references resolve for the current actor."
      recovery:
        - "If resuming fails, retain the immutable version and diagnose missing authority or dependencies."
---

# Save, reopen, and resume a map

Saving, local undo, reopening, and changing a published version are distinct operations. A [draft](../../concepts/domain/content-version.md) remains mutable; an immutable saved version gives a stable basis for continuation.

Save the validated composition and record its returned identity and hash. A new conversation does not inherit the previous actor's authority. Hand off the goal and non-secret identifiers, then retrieve current server state.

Reopen the selected version into a new draft and verify the composition and data references. If the user means “restore what viewers see,” use the existing publication rollback/version-selection contract separately. A save or reopen does not change the audience.

When simultaneous edits produce a generation conflict, compare the intended change with the current draft. Preserve unrelated work. Use the existing composition history for supported local undo/redo; do not describe a destructive reset as undo.

See the [workflow language contract](../../concepts/domain/workflow-language.md) for the meaning and limits of the structured fields. This guide describes an intended workflow; it does not certify a deployment or grant authority.

The [source contract](../../studio/mcp-tools.md) defines the executable surface.
