---
type: concept
title: "Session and handoff"
description: "Temporary client or model context, distinguished from durable server-owned state."
tags: [gis, domain-language]
aliases: ["conversation","resume","continue","fresh session","handoff"]
sources:
  - id: contract
    resource: "/studio/mcp-tools.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/content-version.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/concepts/domain/approval.md"
  - predicate: relates_to
    target: "/guides/workflows/save-resume-map.md"
  - predicate: relates_to
    target: "/guides/workflows/recover-job.md"
---

# Session and handoff
A session holds temporary conversation or protocol context. Durable [drafts and versions](content-version.md) live on the server and are identified separately. A new session should reload authoritative state before editing.[^contract]

“Continue where we left off” needs the intended goal, environment, authenticated actor, resource identifiers, and unresolved decisions. A handoff can carry those facts and a redacted summary; it should not carry passwords, tokens, or a claim of authority inherited from a previous session.

Read current generation and [permissions](permissions.md) again. A stored identifier does not bypass tenant checks. A previous proposal remains subject to its actual [approval](approval.md) status.

Use [save and resume](../../guides/workflows/save-resume-map.md). If an operation was interrupted, use [job recovery](../../guides/workflows/recover-job.md) to establish its outcome before resubmitting.

[^contract]: [Source contract](../../studio/mcp-tools.md).
