---
type: reference
title: "AI invocation admission"
description: "Durable invocation limits, optional Reporting financial accounting, operator controls and migration."
---
# AI invocation admission

Studio chat, report narrative enrichment and imagery inference require an authoritative admission
permit in addition to [model disclosure approval](ai-disclosure-policy.md). Missing configuration,
ledger, trusted identity or available authority denies model invocation before credential resolution
or provider contact. Deterministic and disabled paths, and report cache hits, acquire no permit.
Legacy policies count invocation attempts and active reservations only; they are not token or financial budgets. The optional [Reporting financial policy](#optional-reporting-financial-admission) below adds a narrowly supported token/monetary contract.

## Deployment configuration

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `AiExecutionAdmission__DeploymentId` | empty | Stable identity shared by all replicas; required for model calls, maximum 128 characters. |

PostgreSQL is the sole admission authority and requires schema migration 140. The deployment
identity is a process snapshot; restart replicas to change it consistently. Runtime failure never
selects another authority or a memory fallback. The former `Backend` option is retired: remove it
from deployment configuration. Legacy `auto` and `postgres` values are accepted during migration;
any other value prevents startup rather than silently switching budget authorities.
See the [PostgreSQL admission authority note](../../internal/contributor/ai-admission-postgres.md).
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

Migrate by applying the schema, configuring one stable deployment identity, setting an explicit
policy through initialize, verifying PostgreSQL recovery, and authorizing
resume. Existing disclosure rules and credentials remain required. Disclosure workload classification is
operator asserted; admission does not inspect content or verify that label. Installed recovery,
failover/rollback, remote cancellation and full end-to-end operational qualification remain pending.

## Optional Reporting financial admission

This section describes the supported source contract. It is not an installed qualification, approval of provider terms or prices, or authorization to make external model calls.

### Supported mode and activation

An admission policy without `financial`, or with `financial: null`, retains attempt/concurrency enforcement only. Those limits do not establish token or monetary budgets. Adding an explicit valid `financial` object activates mandatory token and fixed-integer monetary accounting for the configured deployment.

The supported financial path is Reporting narrative generation with the PostgreSQL admission authority and the narrow `openai-basic-text-v1` billing contract. Once financial mode is active, Studio chat and imagery invocation admission deny because their financial adapters are not implemented. Reporting denial uses its existing deterministic fallback. No monetary-safe support is established for DevOps agents, other providers or other billable dimensions.

Before activation, confirm the inventory of every model-sending replica and that each runs admission-aware code. Database fences protect ledger-using legacy writers; they cannot account for or stop an executable that never consults admission. This sender-inventory prerequisite is not installed mixed-version qualification.

Apply migrations 140 (`140_CreateAiExecutionAdmission.sql`) and 142 (`142_CreateAiFinancialAccounting.sql`) before activation. Migration 142 installs `ai_financial_invocations` and the writer and policy fences. Migration 141 is imported-dataset spatial metadata and does not provide those guards. Keep the same deployment identity and PostgreSQL authority across replicas. Use the existing canonical operator operations and their normal authorization/approval policy:

1. For an existing deployment, execute `admin.ai.admission.stop` and wait for active invocation leases to drain. Cancellation and lease expiry do not prove remote billing has stopped.
2. Execute `admin.ai.admission.policy-update` with the complete explicit policy, including `financial`. For a missing deployment, use `admin.ai.admission.initialize`; it creates stopped state.
3. Inspect the resulting policy and current generation/revision/epoch. Authorize `admin.ai.admission.resume` only after the intended route, model, bounds and prices have been reviewed independently.

Initialize and policy-update accept `policy` JSON. The canonical pipeline captures expected state or accepts the complete `expectedGeneration`, `expectedRevision`, `expectedEpoch` tuple. Stale controls fail. Policy JSON uses exact camelCase names; unknown fields and ambiguous/duplicate properties are refused. Application startup does not initialize, approve or resume financial authority.

Activation requires stopped, drained admission. Database triggers also reject legacy unpaid attempt inserts once financial mode is active, financial policy removal, and accounting-identity reset. Do not use direct SQL, an older binary, a replacement deployment ID or backend switching as a downgrade/recovery procedure.

### Synthetic policy shape

The following values are synthetic and use an inert `.invalid` route. `TST` is an illustrative currency label, not an approved currency. The repeated source digest is a placeholder, not reviewed evidence. The dates deliberately describe an expired interval. Do not activate this example against a real provider. Replace every economic assertion through the existing owner review process; no rate or approval is supplied here.

```json
{
  "windowSeconds": 60,
  "maxOperationSeconds": 300,
  "deploymentAttempts": 100,
  "actorAttempts": 100,
  "cohortAttempts": 100,
  "workflowAttempts": 100,
  "deploymentConcurrency": 8,
  "actorConcurrency": 4,
  "cohortConcurrency": 2,
  "workflowConcurrency": 1,
  "financial": {
    "currency": "TST",
    "moneyScale": 1000,
    "periodAnchorUtc": "1970-01-01T00:00:00+00:00",
    "periodSeconds": 60,
    "deployment": { "tokens": 1000, "money": 1000 },
    "actor": { "tokens": 1000, "money": 1000 },
    "cohort": { "tokens": 1000, "money": 1000 },
    "workflow": { "tokens": 1000, "money": 1000 },
    "journalCapacity": 128,
    "basis": {
      "id": "synthetic-expired-v1",
      "sourceDigest": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "provider": "fixture",
      "kind": "openai",
      "billingSchema": "openai-basic-text-v1",
      "serviceTier": "default",
      "useCase": "reporting-narrative",
      "destination": "https://fixture.invalid/v1/chat/completions",
      "model": "fixture-model",
      "effectiveUtc": "1970-01-01T00:00:00+00:00",
      "expiresUtc": "1970-01-02T00:00:00+00:00",
      "maximumRequestBytes": 65536,
      "maximumInputTokens": 10,
      "maximumOutputTokens": 5,
      "inputRatePerMillion": 1000000,
      "outputRatePerMillion": 1000000
    }
  }
}
```

Money values are integer units in the explicit currency scale. Rates are those units per million tokens. Input and output charges round upward independently; no exchange rate is inferred. Token allowances count input plus output. Deployment, canonical actor and actor/use-case scopes always apply; a verified workflow scope also applies when present. Reservation of all applicable financial scopes, attempt charge and concurrency admission is one PostgreSQL transaction.

### Monthly actor cap

`financial.monthlyActor` is optional. When present it is `{ "tokens": <integer>, "money": <integer> }`, in the same token and money units as the other financial scopes, and it limits what one canonical actor may reserve in a UTC calendar month:

- The month is the UTC calendar month of the reservation. It does not follow `periodAnchorUtc` or `periodSeconds`; the per-period `actor` scope and the monthly cap are checked independently, and a request must pass both.
- Unsettled reservations count in full, as do settled invocations that were reserved during the month. Unsettled liability from an earlier month stays charged; settled usage stays in the month it was reserved.
- Apply migration 170 before enabling the cap. Adding or changing it needs stopped, drained admission, like other financial policy changes. Once set, the cap cannot be removed; the database refuses a policy that drops it.
- When an actor's reserved and settled spend reaches 80% of either the token or the money allowance, one notice row is written to `ai_monthly_actor_notices` for that actor and month. Later reservations in the same month do not add another row. The durable outbox then queues a `monthly-actor-spend` warning to the configured operations notification channels.
- A request that would exceed the cap is denied with admission code `financial_monthly_actor_exceeded` before any provider contact. The denying transaction rolls back, so no `ai_financial_invocations` or attempt row records it, and Reporting returns its deterministic fallback narrative.

Every budget denial (`financial_monthly_actor_exceeded`, `financial_budget_exceeded`, `financial_stop`, `financial_ledger_full` and `budget_exceeded`) writes a structured warning, event ID 9542, with the admission code, deployment ID, actor key (the SHA-256 digest of the canonical actor, never the actor itself), use case and UTC month. Search the server log for event 9542 or the code to find out why a report lost its AI narrative.

The basis binds the provider, exact final invocation URL/model, standard/default tier, effective/expiry times, conservative input bound, output cap and rates. The input bound is an operator assertion covering complete framing and messages; it is not estimated from bytes. Request byte limits are a separate check. Reporting freezes its final request and applies the stricter of its configured output limit and the financial output cap before send. Model disclosure approval and credentials remain separately required.

### Incident cap

Optional `financial.incident` applies token and money limits across all actors and periods for one authority scope and episode UUID. Apply migrations 172 and 217 before enabling it. Configure `financial.incidentWorkflows` through the same authorized, stopped and drained policy-update operation. Each binding contains the canonical `workflow` (`job:` followed by the server operation identity), `authorityScope`, `episodeId` and positive `lifecycleVersion`. Bindings are operator-authorized associations in the persisted policy; request labels and model output cannot supply them. At most 64 distinct workflows can be bound.

With an incident cap enabled, every admitted financial invocation needs a matching binding and an active episode at the recorded lifecycle version. The episode observation must be no older than five minutes and no more than thirty seconds in the future. Admission and the durable pre-send claim both validate this authority in PostgreSQL. Missing bindings deny with `financial_incident_required`; stale, closed or version-mismatched episodes deny with `financial_incident_invalid`. A reservation crossing either cap denies with `financial_incident_exceeded` before provider contact. Reporting uses deterministic fallback on denial.

Episode charges include settled and uncertain debt from every period and actor. Refreshing a binding to the same episode UUID preserves its allowance; a different episode UUID has a separate allowance without erasing earlier deployment or actor debt. Incident caps cannot be removed after activation. Changing their limits or bindings requires stopped, drained admission and an advancing control epoch. No automatic job-to-episode producer or additional model-sending path is enabled by configuring this policy.

At 80% of either incident limit, the durable outbox queues an `incident-spend` warning with deployment, authority scope and episode attributes. Retries retain a stable deduplication identity for that episode. A refreshed lifecycle version does not create a new warning. Delivery still requires a configured operations notification channel.

### Liability, stop and recovery limits

Each admitted financial invocation reserves its upper input/output liability before a durable single-send claim. Ambiguous send authorization does not permit a provider send or automatic retry. Cancellation, timeout, transport failure, missing or unsupported usage and response interruption do not establish zero charge. Uncertain debt remains reserved even after permit disposal, lease expiry, restart or period rollover. Outstanding debt constrains later periods; settled charges remain attributed to the original period.

Supported transport-observed receipts settle against the original retained basis, including after stop or policy revision. A receipt is not accepted from a caller or model assertion. Unknown counter dimensions remain SQL null; independently known counters are retained even when a peer counter or root contract is invalid. Uncertain and conflicting receipts retain the independent input/output high-water marks under the original basis. The journal preserves the first usage and conflict digests, not a complete conflict history. Overrun, explicit model/tier contract breach, unrepresentable usage or conflicting/reused response identity causes retained debt and a stop. Response identity deduplication is scoped to the deployment. Resume refuses while retained overrun/conflict state exists. A stop cancels local permits through the existing checks; it cannot undo a charge or guarantee provider-side execution/billing cessation.

Financial activation cannot be removed through policy updates. Currency, money scale, period anchor and period duration are immutable. Stopped/drained updates can change supported remaining policy values without deleting prior debt or its basis. Lowering limits does not forgive previous charges. Missing, unavailable or unverifiable authority denies new model spending; there is no memory/backend fallback.

The lifetime financial journal has an explicit finite capacity. Capacity exhaustion denies; records are not automatically evicted to create budget. This implementation provides no automatic reconciliation, authorized debt-release operation, retention/compaction procedure or history-loss recovery workflow. Do not delete liabilities, reset the deployment identity or increase a period to manufacture recovery capacity. Preserve the ledger and provider evidence, keep affected spending stopped, and resolve the missing reconciliation/recovery capability through separately reviewed work. PostgreSQL history loss, backup restore and downgrade require their own operational evidence; this change does not qualify them.
