# Operating Honua

Honua's day-2 operating model is one loop shared by humans, Console, and MCP
agents: observe, diagnose, remediate, learn, and graduate. The server stays the
source of truth for health, findings, proposals, approvals, and execution. Tools
may explain or propose, but the control plane applies only deterministic,
authorized operations.

This guide describes behavior on `trunk` as of July 10, 2026. The self-operating
platform workstream in #2552 is landed; the remaining limits are called out
explicitly so “runs itself” never means “may mutate anything unattended.”

## The loop

The loop starts with a server-computed posture instead of a dashboard-specific
guess:

```bash
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  http://localhost:8080/api/v1/operate/status
```

`GET /api/v1/operate/status` returns one `healthy`, `degraded`, or `unhealthy`
verdict plus rollups for deploys, jobs, alerts, migrations, findings, telemetry
backends, and the availability SLO when configured. An `ops:read` key can read
this and the read-only observability surfaces without gaining rollback or
proposal authority.

1. Observe: read `GET /api/v1/operate/status`, `GET /api/v1/admin/observability/ops-health`,
   `GET /api/v1/admin/observability/ops-health/history`, and
   `GET /api/v1/admin/observability/events`. MCP clients use
   `honua_ops_health`, `honua_operate_events`, and the `honua://ops/health`
   resource for the same posture.
2. Diagnose: read `GET /api/v1/admin/observability/findings` or call
   `honua_ops_findings`. Findings are deterministic, evaluated on demand, and
   include evidence references. No server-side model call is involved.
3. Remediate: when a finding has a real executor, propose its action with
   `POST /api/v1/admin/observability/findings/{findingId}/propose` or route an
   operation through `honua_propose_operation`. The operation gateway either
   executes under the configured guardrail tier, blocks, or creates an approval
   proposal.
4. Learn: use the persisted ops-health history and fused operate timeline to
   understand whether the action improved health. This is operational memory,
   not model training.
5. Graduate: promote a repeated, deterministic, low-risk concern up the autonomy
   ladder only after its signal, executor, guardrails, and proof exist.

## The two seats

The Console `/operate` seat is the human seat. It reads the same status,
history, events, alerts, and findings as the REST APIs, and it is where an
operator reviews approval proposals. It should be the only place a human has to
decide whether a proposed mutating action runs.

The MCP seat is the agent seat. It can observe through read-only tools and
resources, diagnose findings, and propose in-scope control-plane operations. It
does not get a second approval path. If the gateway returns a `proposalId`, the
agent waits for the Console approval lane to resolve it.

The useful split is:

| Task | Console `/operate` | MCP agent seat |
|---|---|---|
| Read current posture | Status, health, findings, alerts, timeline | `honua_ops_health`, `honua_ops_findings`, `honua_alert_events`, `honua_operate_events` |
| Investigate evidence | Drill into the endpoint named by `source` or `evidenceRefs` | Read the same resources and include evidence in the proposal rationale |
| Discover routable fixes | Operator action catalogs | `honua_supported_operation_kinds` (read-only, live executor catalog) |
| Propose a fix | `findings/{id}/propose` and approval inbox actions | `honua_propose_operation`; verify the kind with `honua_supported_operation_kinds` first |
| Approve or reject | Human approval inbox | Not allowed |
| Mutate source GIS data | Human protocol/API workflows only | Not exposed; ADR-0028 forbids AI-driven source-data editing |

Current MCP status: observability read tools are present. The generic
`honua_supported_operation_kinds` reports the actually routable operation classes
without requiring write authority; do not assume unsupported kinds. The
`supportedKinds` field on rejected `honua_propose_operation` responses remains a
compatibility aid but is deprecated as a discovery mechanism. Platform-ops MCP
tools for release status, deploy operation listing, and rollback proposal are
also part of the current-trunk baseline. Architecture tests enforce the REST/MCP
seat-parity map and reject a finding action that has no real executor.

## The autonomy ladder

Every concern sits on an explicit rung:

| Rung | Meaning |
|---|---|
| L0 observable | The server reports the signal in status, health, history, metrics, or events. |
| L1 diagnosable | A deterministic finding explains the condition and points at evidence. |
| L2 remediable | The finding can route a real operation through the gateway and approval lane. |
| L3 autonomous | A policy explicitly allows AutoApply under guardrails, audit, lease/singleton execution, and a kill switch. |

Current trunk has all four rungs for selected concerns. `ProposeOnly` remains the
default. L3 requires a durable policy store, a per-rule `AutoApply` opt-in, an
action marked auto-safe by both the finding and action catalogs, blast-radius and
rate-window bounds, the global kill switch to be off, and a successful durable
reservation. Every autonomous attempt still passes through the operation gateway
and writes audit, outcome, notification, and convergence evidence.

Current rung by concern:

| Concern | Current rung | Notes |
|---|---:|---|
| Aggregate operating posture | L0 | `GET /api/v1/operate/status` gives one server verdict and drill-down sources. |
| Ops health history and cluster aggregation | L0 | `GET /api/v1/admin/observability/ops-health/history` stores bounded rollups for reconnect gap-fill and trend views. |
| Alert dispatch dead letters | L3 (opt-in) | The `alert-dispatch-backlog` rule can auto-apply `alerts.redrive_dead_letters`; integration proof covers convergence, durable audit/outcome evidence, and the kill switch stopping the next evaluation. The default remains L2/ProposeOnly. |
| Alert channel pause/resume | L2 | Privacy-safe per-channel backlog health identifies a failing channel and offers a scoped `alerts.pause_channel` proposal. Pause remains approval-gated and non-auto-safe because it suppresses delivery; healthy channels continue dispatching. |
| Database bounded-admission pressure | L2 where supported | The Postgres runtime-tunable admission gate can be lowered through a proposed `db.tune_bounded_admission` action when headroom exists. |
| Deploy stuck in manual intervention | L2 when a prior revision is known | The finding can propose a rollback Deploy operation only when the previous revision is recorded. |
| Platform release runtime divergence | L2 | For unpinned divergent serving targets, the finding proposes a Deploy operation to the declared platform release artifact. |
| Platform release config skew | L1 | Explicit config pins cannot be cleared by runtime action; change configuration and redeploy. |
| Pending contract migrations | L1 | Contracting is operator-sequenced. It must not be auto-applied. |
| GP queue depth | L1 | The finding points at the scale concern; no safe automatic scale action ships in this slice. |
| Local backend on incompatible substrate | L1 | The finding explains the topology problem; remediation is a deployment decision. |
| Generic serving latency or error-rate SLO breach | L1 | No single safe generic action exists outside a specific deploy operation. |

Opt a rule into AutoApply only if all of these are true:

- The signal is deterministic and bounded to one concern.
- The recommended action already has a real executor and is proven by tests.
- The action is idempotent or safely retryable.
- Guardrails can only tighten the action tier, never loosen it.
- A kill switch can stop evaluation before another action is submitted.
- Audit records identify the rule, evidence, proposal/action id, actor, and
  result.

Never automate contract or breaking migrations, data deletion, cross-environment
promotion, source-data edits, or any action whose approval gate says it is
data-affecting.

### Configure bounded autonomy

Read the live policy and track record before graduating a rule:

```bash
curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  http://localhost:8080/api/v1/admin/observability/autonomy/policies/alert-dispatch-backlog
```

The following example opts only the proven dead-letter rule into AutoApply,
limits it to two actions per ten-minute window, and caps its blast radius. Policy
updates require admin authorization and are audited.

```bash
curl -sS -X PUT \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  -H "Content-Type: application/json" \
  -d '{
    "mode": "AutoApply",
    "maxAutoActionsPerWindow": 2,
    "windowSeconds": 600,
    "maxBlastRadius": 2,
    "reason": "graduated after reviewed successful proposals"
  }' \
  http://localhost:8080/api/v1/admin/observability/autonomy/policies/alert-dispatch-backlog
```

Freeze all autonomous evaluation immediately with the global kill switch. This
does not delete the saved rule policies; setting it back to `false` restores
their eligibility after the incident review.

```bash
curl -sS -X PUT \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
  -H "Content-Type: application/json" \
  -d '{"killSwitchEnabled":true,"reason":"incident freeze"}' \
  http://localhost:8080/api/v1/admin/observability/autonomy/settings
```

Hosts without the durable control plane fail closed: autonomy is inert and
policy changes are read-only even if configuration asks for AutoApply. See
[ADR-0062](../../internal/contributor/adr/0062-graduated-ops-autonomy-policy.md)
for the route-time guardrail contract.

## Rollback taxonomy

Use "rollback" precisely. There are two families.

Platform version rollback moves compute back to a known revision. During a
deploy-API-managed rollout, `POST /api/v1/admin/deploy/operations/{operationId}/rollback`
asks the backend to repoint traffic or aliases when the operation has the
needed prior revision and backend support. Outside an in-flight operation, the
preferred model is forward deploy to a prior image or artifact revision. When a
declared platform release is in use, update the declaration to the prior
serving artifact and call `POST /api/v1/admin/platform-release/converge`; the
converge API creates per-target Deploy operations for divergent serving targets
and defers worker-image convergence to the next GP dispatch.

Service/config rollback uses metadata release history. Metadata-release rollback
is endpoint- or cockpit-driven against an existing metadata release operation
and its rollback plan. The gateway executor for `MetadataRelease` is create-only
by design; it does not invent a new rollback payload for an already-submitted
release operation.

Telemetry-gated deploy backends can trigger rollback during a configured rollout
when their error-rate, latency, or synthetic health-probe gates breach. That is
deploy-safety behavior for a specific operation. It is not a blanket unattended
operate floor, and ADR-0059 keeps the default product story honest: fix forward
through health-gated proposals unless a rollback path is explicitly approved and
available.

## Upgrade safety

The safe upgrade posture is preflight first, additive schema first, and database
restore last.

1. Preflight:

   ```bash
   curl -s -H "X-API-Key: $HONUA_ADMIN_PASSWORD" \
     "http://localhost:8080/api/v1/admin/deploy/preflight?includeDiagnostics=true"
   ```

   Proceed only when coordinated deploy readiness, migration state, database
   compatibility, and platform-release projection are understood.

2. Gate contract migrations on existing databases when you need an explicit
   human step:

   ```bash
   Database__MigrationSafety__ContractApplyPolicy=Gate
   HONUA_APPROVE_CONTRACT_MIGRATIONS=true
   Database__MigrationSafety__BackupCommand="your-backup-command"
   ```

   Fresh installs still provision fully. Existing databases can require the
   explicit approval flag before reviewed contract-phase scripts run.

3. Single-node compose upgrades are not zero-downtime. The safe flow is:
   preflight, backup, pull the new image, start it with the same environment,
   verify `/healthz/ready`, then keep the previous tag available for application
   rollback. Restore the database only if a destructive migration or data
   corruption makes image rollback insufficient.

4. Serverless or managed deployments may run migrations out of band with
   `HONUA_SKIP_MIGRATIONS=true`; keep the same preflight and contract-gate
   discipline.

See [Upgrade and rollback](../deploy/upgrade-and-rollback.md) and
[Docker Compose upgrade and rollback](../deploy/docker-compose.md#upgrade--rollback)
for the command-level runbooks.

## When you want Grafana

Grafana is optional depth, not the front door. Operators should be able to see
the live and historical posture through `/operate`, the admin observability
endpoints, and the MCP read tools without installing Prometheus or Grafana.

Use the `docker/monitoring/` bundle or your managed metrics backend when you
need:

- long-retention Prometheus metrics beyond the bounded ops-health rollups;
- dashboarding for SRE teams that already live in Grafana;
- deep trace and log correlation through an OTLP/LGTM stack;
- custom alert routing outside Honua's built-in alert and findings surfaces;
- capacity planning across multiple services or environments.

For a local or single-node bundle:

```bash
docker compose -f docker/monitoring/compose.yml up -d
```

See [Monitor Honua Server](../deploy/monitoring.md) for metrics, alert rules,
OTLP, and the one-command monitoring bundle.

## Honest limits

The #2552 implementation workstream is complete: persisted cluster health,
realtime fan-out, the Console quickstart/dashboard/cockpit, MCP observability and
platform tools, real operation executors, graduated autonomy, seat-parity tests,
dead-letter self-heal proof, and the platform rollback cell are on trunk.

That does not make every operational concern autonomous:

- The proven L3 path is bounded alert dead-letter redrive. Other rules stay
  ProposeOnly until their deterministic signal, real auto-safe actuator,
  rollback/convergence proof, and guardrails exist.
- GP queue depth is diagnosable, but the server does not invent a generic scale
  action. Kubernetes HPA and cloud/serverless substrate scaling remain deployment
  concerns; HTTP serving does not claim scale-to-zero.
- Generic latency/error SLO breaches remain findings unless they occur inside a
  deploy operation with a known health-gated rollback path.
- Contract/breaking migrations, destructive changes, source-data edits, and
  cross-environment promotion always retain explicit human governance.
- The bounded built-in history is operational memory, not a TSDB. Use the
  optional OTLP/LGTM integration for long retention and deep correlation.

## Related docs

- [Monitor Honua Server](../deploy/monitoring.md)
- [Upgrade and rollback](../deploy/upgrade-and-rollback.md)
- [Connect AI agents to Honua over MCP](../connect/ai-agents-mcp.md)
- [ADR-0060: Two-plane operability architecture](../../internal/contributor/adr/0060-two-plane-operability-architecture.md)
- [ADR-0028: AI-driven data editing is not allowed](../../internal/contributor/adr/0028-ai-data-editing-not-allowed.md)
- [ADR-0059: First-release scope and fix-forward operate model](../../internal/contributor/adr/0059-first-release-scope-and-fix-forward-operate-model.md)
