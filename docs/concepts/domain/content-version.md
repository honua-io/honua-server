---
type: concept
title: "Draft and content version"
description: "Mutable authoring state and an immutable saved snapshot with stable identity."
tags: [gis, domain-language]
aliases: ["save","revision","version","restore","reopen","undo"]
sources:
  - id: contract
    resource: "/studio/mcp-tools.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/publication.md"
  - predicate: relates_to
    target: "/guides/workflows/save-resume-map.md"
  - predicate: relates_to
    target: "/concepts/domain/identity.md"
---

# Draft and content version
A draft is mutable authoring state. Its generation supports optimistic concurrency. Saving creates an immutable version; reopening a version creates a new draft rather than modifying that historical snapshot.[^contract]

“Restore the earlier map” can mean reopen a previous version for editing or move an active published choice back to it. Identify the intended effect before changing [publication](publication.md). A browser undo operation is not automatically a durable revision restore.

Record item, version, and content-hash identities where returned. On a generation conflict, read the current draft and reconcile; do not overwrite changes simply by retrying with a newer number.

Use [save and resume](../../guides/workflows/save-resume-map.md). Validate the saved composition and reopen it under the intended [identity](identity.md), with its referenced layers still readable.

[^contract]: [Source contract](../../studio/mcp-tools.md).
