# ADR-0062: Graduated autonomy policy for ops findings

Status: Accepted
Date: 2026-07-09

## Context

ADR-0060 defines the operation gateway as the single control-plane choke point:
mutating operations are planned, guardrail-graded, proposed or executed, audited,
and health-gated through shared infrastructure. ADR-0028 forbids model-driven
server-side edits; the server may execute only deterministic policy and
deterministic actions.

Ops findings already identify repeatable operational conditions and can propose
recommended actions through the gateway. Some findings, such as bounded alert
dead-letter redrive, are low-risk enough to graduate from propose-only to
auto-apply after operators have observed a clean track record. Before this ADR,
there was no durable policy layer or kill switch for that graduation.

## Decision

Honua will support a per-rule ops-finding autonomy policy with two modes:
`ProposeOnly` and `AutoApply`. `ProposeOnly` is the default. Policies can be
seeded from configuration and overridden through the admin API; durable policy
and global setting changes are audit events.

Auto-apply never executes through a side channel. The background evaluator
re-evaluates deterministic findings, and eligible findings call the existing
finding propose path. The operation gateway then performs the route-time
autonomy decision and, only when every guardrail passes, converts the current
approval-tier decision into a direct-execute decision for that request.

Server-side guardrails are mandatory:

- A global kill switch forces all rules back to propose-only.
- The durable policy store must be available; without it, autonomy is inert.
- The rule policy must be `AutoApply`.
- The finding action and the registered action catalog must both mark the action
  as auto-safe.
- The action blast radius must fit the rule policy.
- A durable reservation enforces idempotency by finding id and rate limits by
  max actions per rolling window.
- The existing operation gateway/executor path retains health-gate and rollback
  behavior for actions that support it.

Every autonomous attempt records a durable action outcome, writes an audit event,
and emits an ops notification through the existing alert outbox. The console
therefore sees auto-apply activity through the same audit, alert, and operate
surfaces used by manually approved actions.

## Compliance with ADR-0028

No model decides or applies a remediation server-side. The server evaluates only
deterministic findings, deterministic policy, and deterministic action metadata.
External agents may still review findings and suggest policy changes, but the
server-side auto-apply decision is a pure policy decision over registered rules.

## Compliance with ADR-0060

The operation gateway remains the single mutation boundary. Autonomy does not
call executors directly from the findings engine or background loop. The gateway
owns the final guardrail decision, idempotency handoff, execution, audit, and
outcome recording.

## Consequences

Operators can graduate low-risk remediation rules without broadening the
mutation surface. Deploy, migration, destructive, and unregistered actions remain
propose-only because they are not marked auto-safe in the action catalog. Hosts
without the durable control plane remain read-only for autonomy even when config
contains an `AutoApply` rule.
