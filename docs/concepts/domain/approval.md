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
non-admin, it returns a proposal resource to follow. A pending proposal requires
one authorized human decision through a live interactive OIDC session in Console;
that human may be the requester. An API key, ordinary bearer, MCP transport or
client confirmation cannot supply that decision. Direct publication and approval
of an existing proposal are different paths.[^contract]

[Permissions](permissions.md) to compose or propose are distinct from approval authority. A proposal can remain pending or be rejected. Do not report the change as completed while waiting, and do not replace the selected version silently after approval.

For a pending proposal attributed to a canonical stored API-key identity, human approval does
not replace the proposer's current authority. Honua checks that the original key
still exists, is unrevoked and unexpired, and retains admin write authority before
claiming and releasing its replay. This applies to typed proposals with or without
model evidence. A lost key lookup refuses safely; it does not authorize execution.
An active rotation retaining that stored identity remains eligible; this approval
check does not add credential-generation revocation semantics. These checks do not
establish live OIDC revocation or stop effects that have already begun.

In [publish and share](../../guides/workflows/publish-share-map.md), identify the approved version, decision, and active result. A workflow requiring approval must retain that boundary across a fresh [session](session.md) or recovery.

A refused nonhuman approval at REST preflight, or a current-authority or evidence
refusal before the gateway claims execution, retains a minimal
`operation.approval_denied` audit event joined to the accessible proposal and
its correlation identifier. The event records the deciding actor and a fixed
reason code, without the proposal payload or evidence text. It leaves the
proposal state unchanged and does not invoke its actuator. If durable audit
storage cannot accept that event, the request returns service unavailable
instead of claiming that a receipt was retained. Caller cancellation propagates
without a receipt guarantee. The approval API checks proposal access before this decision receipt is
created. Missing proposals and API callers without approval permission do not
create this proposal decision join. Failures after the execution
claim retain their existing compensation and `operation.apply_failed` audit
boundary; they do not also emit this pre-claim denial event.

Proposal, operation-instance and approval history persists in PostgreSQL without an automatic expiration. Redis is not required for this history. Existing Redis-backed deployments must follow the [maintenance cut-over](../../reference/control-plane-migration-guide.md#proposal-history-now-lives-in-postgresql) before upgrading.

Approval records retain a versioned initiation record, the exact reviewed plan
binding, and the verified human decision before execution is claimed. Pending
legacy records without this binding require a fresh proposal; resolved history
remains readable without invented human evidence. An already claimed legacy
execution retains its recorded recovery authority. See
[human approval and rollout](../../guides/secure/authentication.md#human-operation-approval)
for the session boundary and deployment order.

Coordinated releases retain a separate human decision for the container and data gates.
Each review binds the workflow version, gate, tenant and exact release plan. An old
approval flag or actor name cannot start a child operation: an uncrossed legacy
gate requires a fresh review. Already submitted legacy children may finish their
existing recovery path. The requester may approve either gate through the same
verified interactive session boundary.

[^contract]: [Source contract](../../guides/connect/ai-agents-mcp.md).
