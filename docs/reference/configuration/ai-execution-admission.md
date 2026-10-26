---
type: reference
title: "AI invocation admission"
description: "Durable attempt limits, trusted execution identity, operator controls and migration for model calls."
---
# AI invocation admission

Studio chat, report narrative enrichment and imagery inference require an authoritative admission
permit in addition to [model disclosure approval](ai-disclosure-policy.md). Missing configuration,
ledger, trusted identity or available authority denies model invocation before credential resolution
or provider contact. Deterministic and disabled paths, and report cache hits, acquire no permit.
These limits count invocation attempts and active reservations; they are not token or financial budgets.

## Deployment configuration

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `AiExecutionAdmission__DeploymentId` | empty | Stable identity shared by all replicas; required for model calls, maximum 128 characters. |
| `AiExecutionAdmission__Backend` | `auto` | `auto`, `postgres` or `redis`; selects one authoritative backend. |

Auto selects a registered PostgreSQL admission store first, otherwise a configured shared Redis
connection. Selection and the deployment identity are process snapshots. Restart replicas to change
these options consistently. Runtime failure never selects another authority or a memory fallback.
PostgreSQL requires schema migration 140. Redis requires the strict durability settings and recovery
procedure in the [Redis admission authority note](../../internal/contributor/ai-admission-redis.md).
Keep the deployment identity and ledger stable across upgrades: choosing a new identity creates a
separate budget authority. Credential readiness (`Configured`) does not establish admission or
disclosure approval.

## Operator controls and policy

The canonical operation pipeline exposes four typed operations:

| Operation | Behavior |
| --- | --- |
| `admin.ai.admission.initialize` | Creates a missing, stopped generation with an explicit policy; never overwrites an existing ledger. |
| `admin.ai.admission.policy-update` | Changes an explicit policy only while stopped and drained, preserving accounting. |
| `admin.ai.admission.stop` | Stops admissions and fences live permits by epoch. |
| `admin.ai.admission.resume` | Resumes an eligible stopped generation without resetting charged attempts. |

Controls target this process's configured deployment. The server seals the durable authenticated
actor and current generation, revision and epoch into the prepared request; a stale approval or
control cannot mutate a replacement generation. Initialize and policy-update require `policy` JSON.
Other controls cannot change policy. The expected-state tuple is `expectedGeneration`,
`expectedRevision`, `expectedEpoch`; supply all three or let preparation capture the current state.
Missing state is never initialized or resumed by application startup, a request, or reconnect.
Redis initialization also requires a 3,630-second bootstrap quarantine before authorized resume.
OperatorGate metadata follows the installed operation policy; a full administrator may be authorized
directly, so it does not imply a separate human approval for every invocation.

| Policy field | Default | Valid values |
| --- | --- | --- |
| `windowSeconds` | 60 | 1–3,600 |
| `maxOperationSeconds` | 300 | 1–3,600 |
| `deploymentAttempts` | 120 | 1–8,192 |
| `actorAttempts` | 30 | 1–8,192 |
| `cohortAttempts` | 20 | 1–8,192 |
| `workflowAttempts` | 10 | 1–8,192 |
| `deploymentConcurrency` | 8 | 1–8,192 |
| `actorConcurrency` | 4 | 1–8,192 |
| `cohortConcurrency` | 2 | 1–8,192 |
| `workflowConcurrency` | 1 | 1–8,192 |

Policy JSON uses the camelCase field names shown above; unknown or incorrectly cased fields are rejected.

The three mandatory scopes are deployment, durable actor, and actor/use-case cohort. An additional
server-validated workflow scope aggregates across actors. All applicable scopes charge atomically.
Attempt history and active reservations are bounded; a full ledger denies rather than evicting live
accounting. Release frees capacity but never refunds an attempt. Explicit client, tool-loop or job
retries need a fresh permit. Automatic HTTP model POST retries and both Bedrock SDK retry settings
are disabled.

## Identity, lifecycle and compatibility

Actors come from durably revalidatable, scheme/issuer-qualified ownership, not caller budget IDs,
display names or bearer bytes. Chat validates a supplied MCP session using its strong principal
binding; omitting or rotating sessions cannot bypass the mandatory outer scopes. Reports use an
authorized job returned by the job service. Imagery requires captured durable job ownership matching
the executing job; run scope additionally requires server-side run ownership and job membership.
Legacy jobs lacking durable ownership cannot invoke a model.

A permit covers one actual send and remains held through body and response disposal. It links caller
cancellation, a fixed deadline, and authoritative stop/generation/epoch checks on a one-second polling
interval, each check bounded by a one-second timeout, plus a check immediately before send.
This does not establish a real-time cancellation bound. Cleanup uses its own bounded token; abandoned leases
drain conservatively after the deadline plus 30 seconds. Remote cancellation remains best effort
and does not prove remote execution or billing has stopped.

HTTP request JSON is unchanged. Trusted execution-context fields are excluded from JSON. Direct C#
Studio service callers must provide server-derived context; adapter implementations now receive an
explicit permit. Prepared chat calls are single-use and preserve accepted transcript bytes. Chat
admission occurs before SSE where possible: budget, concurrency or ledger-capacity denials return
429; unavailable, stopped or uninitialized authority returns 503 with bounded Retry-After. Invalid
identity/session checks remain separate. Report denial uses deterministic narrative fallback;
imagery returns a sanitized inference failure.

Migrate by applying the schema, configuring one stable deployment/backend, setting an explicit
policy through initialize, completing backend recovery/quarantine requirements, and authorizing
resume. Existing disclosure rules and credentials remain required. Disclosure workload classification is
operator asserted; admission does not inspect content or verify that label. Installed recovery,
failover/rollback, remote cancellation and full end-to-end operational qualification remain pending.
