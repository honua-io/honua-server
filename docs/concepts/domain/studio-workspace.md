---
type: concept
title: "Studio design workspace"
description: "The working context for immediate map and app iteration, with bounded edits and recoverable history."
tags: [gis, domain-language]
aliases: ["design workspace","creative workspace","git workspace","live preview"]
sources:
  - id: contract
    resource: "https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/src/composition/history.ts"
---

# Studio design workspace

A Studio design workspace contains the editable [map](map.md) or [app](app.md), selection, current design state, preview, and history. Routine creative changes follow prompt, direct scoped edit, preview, refine, and undo/redo. They do not require publication approval.

The current Studio implementation uses a typed reducer and client revision history synchronized with server drafts. Undo restores a prior composition and resynchronizes it; a new edit clears redo history.[^contract]

The Git workspace design contract extends this semantic model to source assets: bind an edit to the repository/worktree, base commit, staged and unstaged state, and selected files. A valid Git worktree may have a `.git` file rather than a directory. Retain the scoped before/after change and preview identity; undo reverses that change while preserving unrelated work. Do not use a blanket reset or clean operation as creative undo.

That Git-backed source-asset host is a design contract here, not a claim that the current Studio UI already implements a Git worktree. The implementation-backed reducer/draft path and the proposed Git adapter must report which contract they provide. Neither permits editing authoritative GIS source data.

Use [design a map application](../../guides/workflows/design-studio.md). Keep publication and infrastructure deployment separate from the fast local design loop.

[^contract]: [Source contract](https://github.com/honua-io/honua-studio/blob/8a658c7f75887df8842fabb50cd2585be64ce48d/src/composition/history.ts).
