# Operate scenario: evidence before action

Follow one deployment/readiness failure through the bounded loop:

`observe → deterministic finding → diagnose → sealed proposal → separate approval → typed actuator → verify`

The server owns evidence and governed operations. The terminal DevOps client
owns the model session. Console is an optional independent inspector/approver.
The infrastructure control plane provisions the placement; the server control
plane configures resources and governs registered operations.

> **Pre-cut runbook, not a certification receipt.** The September 6 source
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

Use the MCP product workflow below with the verified endpoint and a
secret-backed read profile. For REST/Admin API inspection, the same server
records map to these routes; this table identifies the contract, not a raw
HTTP shell workflow:

| Record | REST route |
|---|---|
| Aggregate status | `GET /api/v1/operate/status` |
| Source health | `GET /api/v1/admin/observability/ops-health` |
| Findings | `GET /api/v1/admin/observability/findings` |
| Timeline | `GET /api/v1/admin/observability/events?pageSize=5` |

An HTTP 200 or aggregate `healthy` verdict is not sufficient authorization
evidence. Inspect the [metric inventory](metrics.md) and
[posture contract](evidence-posture.md).

## 2. Read the same evidence through MCP and DevOps

Connect the installed MCP client to `/mcp` using its secret-backed profile.
After the normal MCP initialize handshake, explicitly discover the authenticated
`full` view. The bounded `default` view omits the Operate observation and
finding-proposal tools. Follow each returned `nextCursor` with the same view
until the required descriptors are found, and retain catalog/view revision
and descriptor digests. Exposing a full catalog does not authorize its writes
or widen this bounded scenario. In the terminal's MCP inspector send:

```json
{"jsonrpc":"2.0","id":9,"method":"tools/list","params":{"view":"full"}}
```

Then send these calls one at a time:

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

The [executed Windows outage receipt](evidence/3475-windows-outage.json)
already demonstrates this suppression for an isolated alert-dispatch source:
zero new proposals and unchanged dispatch rows, followed by fresh recovery.
Its `candidateQualification=false` is intentional. It does not replace this
deployment/readiness scenario, prove partial/unverified deployment sources,
or promote customer alerting beyond Preview.

## 4. Propose, poll, and approve separately

Only after that gate passes, call discovered `honua_propose_finding` with
`findingId` from the finding's `id` and `candidateId` from its `subject.targetId`.
Both are required by the returned input schema. Here `candidateId` is the
deployment target identifier, not the platform release ID or image digest;
the server checks it matches the hidden Deploy action. A missing target is a
stop condition. The deterministic REST
equivalent is `POST /api/v1/admin/observability/findings/{findingId}/propose`.
That operator route follows gateway policy and can execute an auto-safe action;
it is not the model's proposal-only boundary. Use `honua_propose_finding` for
the model workflow here. Replace both placeholders with the observed IDs:

```json
{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"honua_propose_finding","arguments":{"findingId":"<finding-id>","candidateId":"<subject.targetId>"}}}
```

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

On the isolated candidate fixture, also attempt self-approval, an unauthorized
or cross-tenant actor's proposal read, wrong-tenant/wrong-owner targets, and narrowed OAuth
scope replay. Assert denial and zero unauthorized actuation. The
[source proof map](../../internal/contributor/operate-docs-precut-evidence.md#authorization-and-freshness-proof-map)
names existing #3474 and related negative coverage; it does not claim those
tests were rerun against the candidate. A same-tenant reviewer with the
required read authority must be able to inspect the proposal; a different
actor is not automatically an unauthorized actor.

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
