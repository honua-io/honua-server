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
recommended actions through the gateway. Findings with proven automatic compensation can graduate from propose-only to
auto-apply after operators have observed a clean track record. Dead-letter redrive
requires approval because already-delivered notifications cannot be undone. Before this ADR,
there was no durable policy layer or kill switch for that graduation.

## Decision

Honua will support a per-rule ops-finding autonomy policy with two modes:
`ProposeOnly` and `AutoApply`. The default `Unattended` profile enables only
tier-1 rules from the built-in allowlist. That allowlist is currently empty:
no shipped actuator proves automatic compensation. The unattended policy default
is enabled, but unattended remediation remains an unfulfilled release-gate dependency.
The `ProposeOnly` profile disables that default. Policies can be
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
  max actions per rolling window, both per rule and across rules sharing an
  operation class and action discriminator.
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
mutation surface. Deploy and migration actions require approval. Destructive and security-disabling
actions are blocked; unregistered actions fail closed. Hosts
without the durable control plane remain read-only for autonomy even when config
contains an `AutoApply` rule.

## Amendment: mechanical tiers and unattended installs (2026-11-13)

The action catalog records six scores: reversibility (automatic/manual/none),
blast radius (task or job/layer or service/deployment), data risk
(none/recoverable/irrecoverable), cost (none/within bound/above bound/commitment),
security effect (none/exposure change/disable or widen), and diagnosis
(deterministic/inference). The tier is computed and cannot be overridden in an
individual action registration or lowered by a rule policy.

- Tier 0 observes and emits findings without an execution action.
- Tier 1 requires automatic reversibility, bounded task/job or layer/service
  effects, no data loss, no cost or bounded cost, no security effect,
  deterministic diagnosis, idempotency, and durable rate limiting.
- Tier 2 remains empty for rc.2. Inference requires human approval.
- Tier 3 requires one human approval through the existing operation gateway.
  Manual recovery and deployment-wide effects stay here, including whole-service
  restarts, one-way admission tuning, and arbitrary configuration changes.
- Tier 4 is never executable, including irreversible data or backup deletion,
  disabled security controls, wider IAM/network access, and billing commitments.
  Approval cannot override this tier.

The unattended default never overrides persisted operator policy or the global
kill switch, and it cannot grant auto execution to an action outside tier 1.
A missing PostgreSQL policy store fails closed. PostgreSQL remains the authority
for settings, reservation budgets, finding idempotency, and outcomes; adding a
second rule for the same action does not create another independent action budget.
