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

Routine Studio composition does not acquire an approval step merely because an
agent performs it. Check the [composition policy](../../studio/mcp-tools.md#composition-policy)
for current deployment limitations.

Honua MCP control-plane proposal tools do not approve their own proposals. Studio publication binds an exact saved version and hash. For an authorized
admin, its MCP tool can publish directly without creating a proposal. For a
non-admin, it returns a proposal resource to follow; the proposer cannot approve
that proposal. Direct publication and approval of an existing proposal are
different paths.[^contract]

[Permissions](permissions.md) to compose or propose are distinct from approval authority. A proposal can remain pending or be rejected. Do not report the change as completed while waiting, and do not replace the selected version silently after approval.

For a pending proposal attributed to a canonical stored API-key identity, a separate approval does
not replace the proposer's current authority. Honua checks that the original key
still exists, is unrevoked and unexpired, and retains admin write authority before
claiming and releasing its replay. This applies to typed proposals with or without
model evidence. A lost key lookup refuses safely; it does not authorize execution.
An active rotation retaining that stored identity remains eligible; this approval
check does not add credential-generation revocation semantics. These checks do not
establish live OIDC revocation or stop effects that have already begun.

In [publish and share](../../guides/workflows/publish-share-map.md), identify the approved version, decision, and active result. A workflow requiring approval must retain that boundary across a fresh [session](session.md) or recovery.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
