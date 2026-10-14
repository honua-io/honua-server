---
type: concept
title: "Approval"
description: "A separate authorized decision on a particular proposed change."
tags: [gis, domain-language]
aliases: ["approve","review request","proposal","permission to publish"]
sources:
  - id: contract
    resource: "/guides/connect/ai-agents-mcp.md"
relations:
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/guides/workflows/publish-share-map.md"
  - predicate: relates_to
    target: "/concepts/domain/session.md"
---

# Approval
A proposal records a requested change. Approval is a separate authorized decision under the deployment's policy; a model's confidence, user-facing confirmation sentence, or successful submission is not that decision.

Honua MCP control-plane proposal tools do not approve their own proposals. Studio publication binds the proposal to an exact saved version and hash, and the caller follows the returned proposal resource for its outcome.[^contract]

[Permissions](permissions.md) to compose or propose are distinct from approval authority. A proposal can remain pending or be rejected. Do not report the change as completed while waiting, and do not replace the selected version silently after approval.

In [publish and share](../../guides/workflows/publish-share-map.md), identify the approved version, decision, and active result. A workflow requiring approval must retain that boundary across a fresh [session](session.md) or recovery.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
