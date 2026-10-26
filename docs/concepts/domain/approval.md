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

In [publish and share](../../guides/workflows/publish-share-map.md), identify the approved version, decision, and active result. A workflow requiring approval must retain that boundary across a fresh [session](session.md) or recovery.

A refused self-approval at REST preflight or invalid evidence decision before
the gateway claims execution retains a minimal
`operation.approval_denied` audit event joined to the accessible proposal and
its correlation identifier. The event records the deciding actor and a fixed
reason code, without the proposal payload or evidence text. It leaves the
proposal state unchanged and does not invoke its actuator. If durable audit
storage cannot accept that event, the request returns service unavailable
instead of claiming that a receipt was retained. Caller cancellation propagates
without a receipt guarantee. Missing proposals and callers without approval
permission do not create this proposal decision join. Failures after the execution
claim retain their existing compensation and `operation.apply_failed` audit
boundary; they do not also emit this pre-claim denial event.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
