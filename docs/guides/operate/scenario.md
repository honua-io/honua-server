# Operate scenario: evidence before action

Follow one deployment/readiness failure through the bounded loop:

`observe → deterministic finding → diagnose → sealed proposal → separate approval → typed actuator → verify`

The server owns evidence and governed operations. The terminal DevOps client
owns the model session. Console is an optional independent inspector/approver.
The infrastructure control plane provisions the placement; the server control
plane configures resources and governs registered operations.

> **Pre-cut runbook, not a certification receipt.** The September 5 source
> review establishes route/tool contracts. Exact 2026.1 candidate replay is
> still required. The platform manifest calls itself a working snapshot, and
> [release #231](https://github.com/honua-io/honua-release/issues/231) owns the
> signed artifact lock. A local source build is not that lock. See the
> [evidence disposition](../../internal/contributor/operate-docs-precut-evidence.md).

## 1. Establish the placement and identities

Use the candidate's verified Local Docker or AWS ECS-small installation
handoff. Record release ID, image digest, architecture, endpoint, deployment
target/backend ID, installed CLI/MCP versions and integrity hashes. Do not
substitute a floating image tag or manufacture a candidate from trunk.

Create separate proposer and approver profiles backed by different principals.
The observation seat needs `ops:read`; proposal authority must be scoped to
its target operation. The human approver needs the appropriate approval grant
(`admin:approve` for API keys) and read authority. Never pass the approver
credential to the model. Profile names alone do not establish separation of
duties: retain the server-resolved principal IDs.

These PowerShell reads assume the verified endpoint in `HONUA_URL` and a
read credential supplied by a secret store in `HONUA_API_KEY`. Do not log the
header or put a literal credential in command history.

```powershell
$base = $env:HONUA_URL.TrimEnd('/')
$headers = @{ 'X-API-Key' = $env:HONUA_API_KEY }
$status = Invoke-RestMethod "$base/api/v1/operate/status" -Headers $headers
$health = Invoke-RestMethod "$base/api/v1/admin/observability/ops-health" -Headers $headers
$findings = Invoke-RestMethod "$base/api/v1/admin/observability/findings" -Headers $headers
$events = Invoke-RestMethod "$base/api/v1/admin/observability/events?pageSize=5" -Headers $headers
```

An HTTP 200 or aggregate `healthy` verdict is not sufficient authorization
evidence. Inspect the [metric inventory](metrics.md) and
[posture contract](evidence-posture.md).

## 2. Read the same evidence through MCP and DevOps

Connect the installed MCP client to `/mcp` using its secret-backed profile.
After the normal MCP initialize handshake, discover `tools/list`; retain the
catalog/view revision and descriptor digests. In the terminal's MCP inspector
send these calls, one at a time:

```json
{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"honua_ops_health","arguments":{}}}
```

```json
{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"honua_ops_findings","arguments":{}}}
```

```json
{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"honua_operate_events","arguments":{"pageSize":5}}}
```

```json
{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"honua_supported_operation_kinds","arguments":{}}}
```

Start the candidate-pinned DevOps stdio client with `honua-devops --mcp` in
the terminal client configuration. Its bounded workflow reads this same
server evidence and explains the stable finding ID and bounded evidence
references. It must not reconstruct the hidden executable action payload.
An empty `supportedKinds` result means no registered typed actuator is
available for this session; stop at diagnosis.

## 3. Apply the evidence gate before proposing

Select a deployment/readiness finding, preserving its ID,
`requiredSourceIds` and `observationWindow`. Every required source must be
present, complete, within its server-owned validity window, and identify
the backend actually queried. Coverage must include the requested window,
components and replicas without truncation. Never replace absent observation
or last-success timestamps with response `generatedAt`.

Stale, partial, unavailable, not-configured or backend-unverified evidence
permits bounded diagnosis but **zero proposals and zero actuator calls**.
The server re-evaluates the finding before routing;
`evidencePostureNotActionable` is a blocked outcome, not an invitation to try
another mutation tool. Recovery requires a fresh complete observation.

In an isolated replay, interrupt only the telemetry backend identified by the
fixture, retain its unavailable observation, attempt the finding proposal,
and assert the blocked reason plus unchanged proposal/actuator counts.
Restore that backend and wait for complete fresh evidence before retrying.
The [live outage harness contract](evidence-posture.md#live-outagerecovery-proof)
defines the controls; they are test-harness endpoints, not product routes.

## 4. Propose, poll, and approve separately

Only after that gate passes, call discovered `honua_propose_finding` with the
finding identifier using its returned input schema. The deterministic REST
equivalent is `POST /api/v1/admin/observability/findings/{findingId}/propose`.
Generic model-facing control tools seal proposals; they do not execute even
when a separate server-owned policy allows direct execution. Do not use the
removed opaque `honua_propose_operation` contract.

Preserve the proposal ID, then poll `honua://proposals/{proposalId}` with MCP
`resources/read`, or use the deterministic Admin CLI. Set `$proposalId` to
the returned ID:

```powershell
honua admin operate getOperationProposal --path "id=$proposalId" --profile proposer
```

The separate human reviews the sealed target, diff, risk, policy, scope and
evidence before running:

```powershell
honua admin operate approveOperationProposal --path "id=$proposalId" --profile approver --yes
```

REST maps these commands to `GET /api/v1/admin/proposals/{id}` and
`POST /api/v1/admin/proposals/{id}/approve` (no approval request body).
The proposer then polls the same proposal. Approval alone is not actuator
success. A conflict or changed authority requires inspection and a newly
reviewed proposal, not blind replay with broader credentials.

On the isolated candidate fixture, also attempt self-approval, an unrelated
actor's proposal read, wrong-tenant/wrong-owner targets, and narrowed OAuth
scope replay. Assert denial and zero unauthorized actuation. The
[source proof map](../../internal/contributor/operate-docs-precut-evidence.md#authorization-and-freshness-proof-map)
names existing #3474 and related negative coverage; it does not claim those
tests were rerun against the candidate.

## 5. Verify fix-forward convergence

For a declared-release divergence, the fix-forward goal is the declared
serving artifact on the selected target, followed by readiness recovery.
Use the finding's registered action; do not invent a shell command or
deployment revision. An unsupported target requires manual intervention.

Retain one canonical operation instance and one typed actuator receipt,
including backend/target, requested and observed revision, timestamps, result
and verification evidence. Free-form `ready` or `applied` output fails this
requirement. Join finding, source observations, proposal, operation, policy,
approver, actuator, audit, correlation and release IDs in the same receipt.

Use the observation interval and sample cadence declared by the selected
backend's verification policy. Record their exact values before the run;
there is no universal window invented by this guide. Poll readiness and
health throughout that interval, assert the intended revision is serving,
and verify that the original finding clears while evidence remains complete
and fresh. A single successful readiness response cannot prove convergence.

## 6. Inspect or approve visually (optional)

Point Console `/operate` at the same verified endpoint. Compare finding,
proposal and operation IDs with the terminal receipt. A separate authorized
Console principal can approve in its focused inbox instead of the terminal
approver. Console does not host the model or create another control plane.
The terminal remains sufficient when Console is absent.

## Rollback capability truth

| Placement / backend | Wiring and required outcome |
|---|---|
| Local Docker | Retain the exact prior image and database compatibility/backup plan. Compose replacement is operator-managed unless the registered backend advertises a real rollback actuator. Otherwise fix forward or report manual intervention. |
| AWS ECS-small | Terraform in `honua-iac` declares infrastructure; inspect the runtime backend and prior task revision. A GitOps handoff with `SupportsRollback=false` cannot revert workload traffic. A direct ECS adapter must supply its provider receipt before rollback is claimed. |
| Helm / Kubernetes GitOps handoff | Pin chart values and image digest; the GitOps owner changes the revision. Unsupported handoff adapters return manual intervention. Helm wiring alone proves neither server rollback nor EKS certification. |
| Registered real rollback adapter | Require advertised support, a prior revision, an approved operation and an observed provider revert, followed by the same verification window. Missing any one prevents a rollback claim. |

Application rollback and database restore are different operations. Consult
[Upgrade and rollback](../deploy/upgrade-and-rollback.md) before reverting an
image across schema changes. Local Docker and AWS ECS-small are the bounded
placement targets; exact-candidate certification is still required for both.
EKS, Azure, hosted models, broad autonomous remediation and #3300 performance
depth are outside this scenario. Whole-catalog GP and four cloud-native
formats retain their separate 2026.1 GA qualification requirements; customer
alerting, multi-tenancy and offline sync remain Preview.
