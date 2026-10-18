---
type: concept
title: "What an agent can do in each edition"
description: "MCP discovery and query are Community. Licensed agent mutations and configurable approval policy are available from Pro."
resources:
  - "honua://capability/ai.mcp-discovery"
  - "honua://capability/ai.spec-artifacts"
  - "honua://capability/ai.agent-operations"
  - "honua://capability/ai.grounding"
  - "honua://capability/ai.spec-apply"
  - "honua://capability/ai.approval-workflows"
---
# What an agent can do in each edition

Community includes MCP discovery and query. Pro adds licensed agent planning and
execution capabilities together with configurable approval workflows and
policy-scoped agent permissions. Enterprise includes those Pro capabilities.
Authorization, tenant isolation and credential security apply in every edition.

| Capability | Edition | What it allows |
|---|---|---|
| [`ai.mcp-discovery`](../../okf/capabilities/ai.mcp-discovery.md) | Community | Discover, search and query through MCP — the agent read surface. |
| [`ai.spec-artifacts`](../../okf/capabilities/ai.spec-artifacts.md) | Community | Retrieve executable spec artifacts by content hash. Retrieval only. |
| [`ai.agent-operations`](../../okf/capabilities/ai.agent-operations.md) | Pro | Agent-initiated changes, through a plan / dry-run / validate layer. |
| [`ai.grounding`](../../okf/capabilities/ai.grounding.md) | Pro | Turn a natural-language request into a validated spec mutation plan. |
| [`ai.spec-apply`](../../okf/capabilities/ai.spec-apply.md) | Pro | Apply an executable spec and submit plan-execution jobs. |
| [`ai.approval-workflows`](../../okf/capabilities/ai.approval-workflows.md) | Pro | Organisational approval workflows and policy-scoped agent permissions. |

## Reading the table

**Community supports an assistant that reads permitted data.** An agent can
list tools, discover the catalog, query permitted features and retrieve supported
artifacts. A capability being included in Community does not bypass resource
permissions or tenant boundaries.

**Pro includes agent execution and configurable governance.** `ai.grounding`
produces a spec mutation plan, `ai.agent-operations` supplies the validation
entitlement, and `ai.spec-apply` permits spec application and plan submission.
`ai.approval-workflows` adds organisational approval policy and scoped agent
permissions. These capabilities remain subject to the individual operation's
permissions, policy, dependencies and preconditions. Tool discovery is not an
authorization grant.

Approval policy determines which operations need review. An approval binds to a
specific proposal; the operation gateway rejects approval by that proposal's own
requester. This does not mean every write in every deployment is approval-gated:
operator policies can permit direct actions, and configured administrator
exemptions affect approval requirements. See
[Operation policy in Production](ai-agents-mcp.md#operation-policy-in-production).

Pro and Enterprise use the same approval-level agent policy. An entitlement
makes the capability available; it does not confer a user role or approve a
change. Explicitly restricted licenses continue to grant only their listed
entitlements. Studio draft composition remains realtime by default in every
edition; publication and rollback retain their governed action policy. Explicit
operator overrides still apply.

Per-capability status, maturity and evidence live on each capability page above.
The full catalogue is the [capability concepts index](../../okf/capabilities/README.md).
