---
type: concept
title: "What an agent can do in each edition"
description: "Reading through MCP is Community. Letting an agent change anything is Pro. Organisational approval policy is Enterprise."
resources:
  - "honua://capability/ai.mcp-discovery"
  - "honua://capability/ai.spec-artifacts"
  - "honua://capability/ai.agent-operations"
  - "honua://capability/ai.grounding"
  - "honua://capability/ai.spec-apply"
  - "honua://capability/ai.approval-workflows"
---
# What an agent can do in each edition

Connecting an agent and letting it read costs nothing. Letting it *change*
anything is the edition boundary, and organisational approval policy is a second
one above that. Six capability keys draw those two lines.

| Capability | Edition | What it allows |
|---|---|---|
| [`ai.mcp-discovery`](../../okf/capabilities/ai.mcp-discovery.md) | Community | Discover, search and query through MCP — the agent read surface. |
| [`ai.spec-artifacts`](../../okf/capabilities/ai.spec-artifacts.md) | Community | Retrieve executable spec artifacts by content hash. Retrieval only. |
| [`ai.agent-operations`](../../okf/capabilities/ai.agent-operations.md) | Pro | Agent-initiated changes, through a plan / dry-run / validate layer. |
| [`ai.grounding`](../../okf/capabilities/ai.grounding.md) | Pro | Turn a natural-language request into a validated spec mutation plan. |
| [`ai.spec-apply`](../../okf/capabilities/ai.spec-apply.md) | Pro | Apply an executable spec and submit plan-execution jobs. |
| [`ai.approval-workflows`](../../okf/capabilities/ai.approval-workflows.md) | Enterprise | Organisational approval workflows and policy-scoped agent permissions. |

## Reading the table

**Community gets you a working agent.** Connect Claude or any MCP client, list
tools, search the catalog, query features, retrieve artifacts. Everything a
read-only assistant needs is here, and none of it needs a licence file. If your
use is "ask questions about my data", you are finished at Community.

**Pro is the write boundary.** The three Pro keys are one capability seen from
three angles: `ai.grounding` turns a sentence into a plan, `ai.agent-operations`
validates and dry-runs it, `ai.spec-apply` executes it. An agent that composes a
Studio draft, changes a service, or runs a plan is using them. Without Pro, those
tool calls are refused — the tools still appear in `tools/list`, because tool
visibility is not an authorization boundary.

**Enterprise is about who may approve, not what may be done.** Honua already
refuses to let an agent approve its own work at every edition: mutating
control-plane requests go through schema-closed proposal tools and a human
resolves them. `ai.approval-workflows` adds organisational policy on top —
scoped agent permissions and routed approvals. Buy it when "a human approves"
must become "*this* human, under *this* policy".

## What this does not change

Editions gate capabilities, not safety. The propose-and-approve model is
structural: an agent proposes, a human disposes, and that holds on a Community
deployment exactly as it does on an Enterprise one. See
[Connect AI agents to Honua over MCP](ai-agents-mcp.md) for the proposal tools
and how approval resolves.

Per-capability status, maturity and evidence live on each capability page above.
The full catalogue, filterable by edition, is the
[capability concepts index](../../okf/capabilities/README.md).
