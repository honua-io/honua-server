---
type: guide
title: "Design a map application through prompt and preview"
description: "Iterate directly on map and app content, inspect the latest preview, and undo scoped changes while preserving source data and unrelated work."
status: draft
tags: [gis, domain-language, workflow, studio]
aliases: ["design in Studio", "prompt to map app", "refine the preview", "undo the last design change"]
sources:
  - id: studio
    resource: "https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md"
  - id: history
    resource: "https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/src/composition/history.ts"
workflow:
  profile: "honua.gis-workflow/v1"
  intent: "Make a scoped creative change and show its current preview immediately, with recoverable history."
  inputs:
    - name: workspace
      concept: /concepts/domain/studio-workspace.md
      required: true
      description: "Current design state, selection, base revision, dirty state, and owned source-asset scope."
    - name: app
      concept: /concepts/domain/app.md
      required: true
      description: "Current map application and the user's intended visual or interaction change."
  outputs:
    - name: workspace
      from: "$actions.preview.workspace"
      concept: /concepts/domain/studio-workspace.md
      description: "Current workspace, latest preview identity, scoped change history, and any remaining design issue."
  constraints:
    - "Creative map/app edits are direct; do not insert proposal or approval states into ordinary iteration."
    - "Authoritative GIS source data is immutable to this AI workflow."
    - "Preserve unrelated staged, unstaged, untracked, and concurrent changes."
    - "Git-backed source-asset hosting is a design contract; current Studio uses typed reducer history and server draft synchronization."
    - "Use the existing history or Git mechanism of the actual host; do not promise a host adapter that is not implemented."
  ambiguities:
    - phrase: "undo"
      interpretations: ["reverse this design change", "restore a saved composition", "revert a deployed publication"]
      clarify: "Which scoped change and which state should return, while keeping unrelated work?"
  actions:
    - id: inspect
      verb: inspect_design_context
      inputs:
        - name: workspace
          from: "$inputs.workspace"
          concept: /concepts/domain/studio-workspace.md
        - name: app
          from: "$inputs.app"
          concept: /concepts/domain/app.md
      outputs:
        - name: workspace
          concept: /concepts/domain/studio-workspace.md
          description: "Observed basis, selection, target assets, and current preview."
      effect: read
      effectTarget: observation
      preconditions:
        - "The host identifies its actual persistence and history adapter."
        - "For a Git workspace, resolve the real worktree including a .git file and capture commit plus dirty state."
      authorization:
        - "Read the selected design and source assets."
      idempotency: "Refresh the basis before every edit; inspection does not reset the workspace."
      checks:
        - "Target content and unrelated existing changes are identified."
      recovery:
        - "Resolve an ambiguous selection or stale workspace basis before applying."
    - id: edit
      verb: apply_design_change
      inputs:
        - name: workspace
          from: "$actions.inspect.workspace"
          concept: /concepts/domain/studio-workspace.md
      outputs:
        - name: workspace
          concept: /concepts/domain/studio-workspace.md
          description: "Workspace after the direct bounded creative change."
      interfaces:
        - kind: mcp
          operation: honua_studio_update_draft
          contract: /studio/mcp-tools.md
      effect: update
      effectTarget: map_content
      preconditions:
        - "The basis revision or draft generation still matches."
        - "Changes affect only requested map/app source content or composition."
      authorization:
        - "Edit this design workspace; this grants no source feature mutation or deployment authority."
      idempotency: "Record the scoped before/after change; do not apply the same patch twice after an uncertain result."
      checks:
        - "The intended change is present and unrelated content is preserved."
      recovery:
        - "On drift, re-read and reconcile the scoped change before retrying."
      rollback:
        snapshot: "Capture the relevant prior content plus base revision and dirty workspace state."
        revisionGuard: "Compare the current affected content to the action's after-state; preserve later edits."
        scope: "Only assets or composition fields changed by this design action."
        compensation: "Use the current host's undo/history or a scoped inverse Git change; never blanket reset or clean."
        verification:
          - "Confirm the intended prior design and a preview of the resulting current state."
        nonReversible:
          - "Undo does not revoke published artifacts or reverse external effects."
    - id: preview
      verb: preview_current_design
      inputs:
        - name: workspace
          from: "$actions.edit.workspace"
          concept: /concepts/domain/studio-workspace.md
      outputs:
        - name: workspace
          concept: /concepts/domain/studio-workspace.md
          description: "Current design paired with the preview generated for this revision."
      effect: read
      effectTarget: observation
      preconditions:
        - "The preview renderer can identify which design revision it rendered."
      authorization:
        - "Read the design's permitted dependencies."
      idempotency: "Repeated previews read the current design and do not create publication state."
      checks:
        - "The displayed preview corresponds to the latest accepted revision, not a stale completion."
        - "Requested layout, style, controls, and interactions are visible and usable."
      recovery:
        - "Show the actual rendering or dependency error; retain editable design state for refinement or undo."
---

# Design a map application through prompt and preview

Studio design is a conversation with a visible result. Start from the current [map application](../../concepts/domain/app.md), selection, and [workspace](../../concepts/domain/studio-workspace.md). Apply the requested creative change directly and show the corresponding preview. Refine it or undo it without turning every turn into a publication review.

A request such as “put the layer list beside the map and make the selected feature stand out” changes layout and presentation. It does not change the selected feature's attributes or geometry. If a request asks to repair source records, return findings and a recommendation for direct human control.

The current Studio implementation has typed composition commands, a live MapLibre canvas, local undo/redo history, and server draft synchronization.[^studio] History records composition revisions; undo restores and resynchronizes an earlier state, and a new edit clears the redo stack.[^history]

The Git workspace contract described here is the intended source-asset adapter, not a claim of a shipped Git-backed Studio UI. It binds a change to the actual worktree, base commit, staged and unstaged content, and selected files. A worktree's `.git` can be a file. Preserve untracked and unrelated work. A whole-worktree reset cannot stand in for undoing one design turn.

The preview action requires the actual Studio host's rendered canvas for the current design. The MCP `honua_studio_preview_draft` tool returns a plan; it does not query source data or render that canvas. Structural validation, a preview plan and a saved version each leave the visual outcome unverified until the exact composition is opened in a supported renderer.

Preview freshness is part of the contract: if a slower render for an older revision finishes later, it must not replace the newest design's preview. Keep enough basis and change identity to explain or reverse the specific turn.

[Saving](save-resume-map.md) checkpoints the design. [Publication](publish-share-map.md) or operational deployment is a separate requested action using the real target's policy, version, and rollback contract. No extra approval ceremony belongs in routine composition.

[^studio]: [Current Studio architecture](https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/README.md).
[^history]: [Composition history and draft synchronization](https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/src/composition/history.ts).
