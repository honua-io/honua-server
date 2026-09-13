---
type: guide
title: "Operate scenario: a protected update"
description: "Review and approve a bounded update, follow server-owned progress, and verify success or recovery across terminal, MCP and optional Console."
---
# Operate scenario: a protected update

Follow one deployment/readiness failure through the bounded loop:

`observe → diagnose → sealed proposal → separate approval → stage → validate → activate → observe → complete or restore and verify`

The server owns evidence and governed operations. The terminal DevOps client
owns the model session. Console is an optional independent inspector/approver.
The infrastructure control plane provisions the placement; the server control
plane configures resources and governs registered operations.

Review the proposed service change and its protection limits, then obtain a
separate approval. Follow progress in the terminal or Console. Ordinary users
do not need to manage Git branches, PRs or telemetry queries. The numbered
sections below provide the advanced API/MCP procedure for operators and replay.

> **Qualification is still incomplete.** Replay uses the image digest pinned in
> the accepted platform manifest, even before the final signed release lock is
> cut. Source tests, an available rollback method and a proposed newer image
> are not passing qualification for that pin. The [evidence
> disposition](../../internal/contributor/operate-docs-precut-evidence.md)
> records the accepted pin's known failure and remaining receipt requirements.

## Check the installed contract

The September 13 replay of the accepted `7ba4226` image passes the four
observation calls in step 2, but the protected-update journey remains blocked.
Its [read observation](evidence/3302-candidate-read-observation.json) and
[installed staging recheck](evidence/3302-candidate-staging-recheck.json)
establish these limits. A separate [catalog replay](evidence/3302-candidate-catalog.json)
discovers all five named scenario tools across five pages and verifies the
finding-proposal input schema; discovery does not execute a proposal:

| Check | Observed result and operator decision |
|---|---|
| Read health/findings through REST and MCP | The isolated fixture returns structured evidence. Disabled alerting remains `notConfigured`, without observation/success clocks, on both surfaces. Read success does not establish deployment-source outage handling or permission to change a target. |
| Read Operate status | The image returns `schemaVersion=1.0`, before the corrected `1.1` local-diagnostic contract. Its suggestion to configure a platform error budget from the in-process window is obsolete. Follow the [metric semantics](metrics.md#platform-slo-and-local-diagnostics); do not turn that diagnostic into protection evidence. |
| Discover operation kinds | `Deploy` is registered, but that says nothing about the selected backend's rollback support, prior revision or verification policy. Complete the target-specific checks before approval. |
| Stage a protected service revision | The fixture changes live revision 3 → 4 and exposes `owner_email` during preparation; the prior revision is missing from the operation. The expected unchanged service and captured recovery identity are not established. Stop qualification at this failed check. |

The staging failure breaks the promise that preparation preserves the live
service and binds a known prior revision for recovery. Accepting a replacement
image and replaying these checks is still required; updating the documentation
does not qualify the current pin. This observation uses isolated Docker fixtures
and does not certify ECS-small, the installed DevOps client, or Console.

## Progress and protection

These are the required client descriptions of server truth, not additional API
enum values. Inspect the durable operation and its verification evidence; never
advance a label using a client timer or a model's prose.

| User sees | Required server truth and meaning |
|---|---|
| **Checking update** | Preflight checks the supported change, authorization, schema compatibility, target, prior revision and required fresh evidence. Show whether protection is available and its limits before approval. |
| **Updating** | The separately approved operation is staging, validating or activating the change through its registered actuator. Preparation must leave the current service revision available. |
| **Confirming service health** | Activation has occurred but the observation window remains open (`protection.phase=observing`), or recovery is still being verified. Show the observation/recovery deadline; one ready probe is insufficient. |
| **Update complete** | The operation succeeded after its required observation window and verification checks. An expired window without success is not completion; once retained recovery capacity is retired, explain that protection has ended. |
| **Previous version restored** | The rollback settled, the expected prior revision/configuration is observed, and functional recovery checks passed. A provider acknowledgement, routing change or bare `RolledBack` string without those checks is insufficient. |
| **Needs attention** | A check blocks the change, protection is unavailable, recovery fails or its deadline expires. State the reason and required operator action. Never convert an unknown or failed result into success. |

Protection is bounded to the approved target/change and recorded policy. The
supported-change matrix covers backward-compatible application updates and
reversible service configuration; additive schema changes need explicit
qualification. Destructive migrations and irreversible data transformations
are rejected before mutation. Database restore is a separate recovery procedure.

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
available for this session; stop at diagnosis. A nonempty result identifies
operation kinds only. It does not prove a backend is configured for your target
or that its protected-update path has passed qualification.

## 3. Apply the evidence gate before proposing

Select a deployment/readiness finding, preserving its ID,
`requiredSourceIds` and `observationWindow`. Every required source must be
present, complete, within its server-owned validity window, and identify
the backend actually queried. Coverage must include the requested window,
components and replicas without truncation. Never replace absent observation
or last-success timestamps with response `generatedAt`.

Stale, partial, unavailable, not-configured or backend-unverified evidence
permits bounded diagnosis but **zero new proposals and zero new-change actuator calls**.
The server re-evaluates the finding before routing;
`evidencePostureNotActionable` is a blocked outcome, not an invitation to try
another mutation tool. A new change requires a fresh complete observation.

This gate does not cancel deterministic recovery already authorized by a bound
protection policy. After exposure, missing telemetry may itself trigger that
recovery when the policy's grace period expires. Keep the same operation,
approval, target, prior revision and policy digest; do not let the model create
a new proposal to bypass missing evidence. Restoration still requires verified
functional recovery, or the outcome is **Needs attention**.

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

### Check protection before approval

The proposal detail alone does **not** expose backend capabilities or the full
protection policy. Before approving, an authorized operator uses the installed
Admin CLI's planning route with the target and desired revision from the
server-authored finding/release handoff. Obtain the current revision from an
observed deployment/provider receipt; do not infer it from the desired image.
If there is no verified prior revision, stop the protected-update flow.

Replace the three placeholders with those independently observed identities.
The planning profile needs authority for `POST /api/v1/admin/deploy/plan`;
do not broaden the separate approver's grants merely to perform this lookup.

```powershell
$planRequest = @{
  targetId = "<target-id>"
  desiredRevision = "<desired-revision>"
  currentRevision = "<observed-current-revision>"
} | ConvertTo-Json -Compress
honua admin release planDeployOperation --body $planRequest --profile planner --yes --json
```

This calls the plan endpoint, which does not create, submit or approve a deploy;
`--yes` confirms the CLI's POST operation. Inspect `target.targetId`, `target.backend`,
`target.currentRevision`, `target.desiredRevision`, `target.parameters`,
`backendRegistered`, `capabilities.supportsRollback`, `blockingReasons` and
`warnings`. The current revision in the plan reflects the supplied value, so
the plan is not independent proof that the provider is serving it. An absent
backend/capability, `supportsRollback=false`, missing prior revision or blocking
reason means protection is unavailable; stop and report **Needs attention**.
`readyToSubmit` alone is insufficient and may be false pending required approval.

Retain the plan and the installed profile's observation/recovery limits and
policy identity. Match them to the sealed proposal's target, prior/desired
revisions and effective parameters immediately before approval. Do not decode
or reconstruct a hidden executable payload. If the client cannot establish
that match, or cannot expose the exact limits/policy for review, leave the
proposal unapproved: this candidate has not established the protected path.
Show that limitation plainly to the user. A plan response cannot repair a
missing prior identity in a sealed finding proposal.

Only after those checks, the separate human reviews the sealed target, diff,
risk, policy, scope and evidence before running:

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

## 5. Verify the update or recovery

For a declared-release divergence, the fix-forward goal is the declared
serving artifact on the selected target, followed by readiness recovery.
Use the finding's registered action; do not invent a shell command or
deployment revision. Keep the operation pending through its protection window.
If an approved recovery trigger fires, restore the bound prior revision and
verify it before reporting **Previous version restored**. An unsupported target
or failed recovery requires **Needs attention** and an explicit operator action.

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

Exercise functional reads, writes, authorization and committed-data preservation
against independently seeded expected values; include schema compatibility and
render/query correctness where the changed service uses them. The recovery arm
must observe the prior revision/configuration and the preserved data, not just
the load balancer's routing state. Retain detection/recovery durations and
failed checks. The [protected recovery certificate](https://github.com/honua-io/honua-release/issues/321)
owns the installed fault matrix; missing or skipped mandatory cells block the
protected claim for that target.

## 6. Inspect or approve visually (optional)

Point Console `/operate` at the same verified endpoint. Compare finding,
proposal and operation IDs with the terminal receipt. A separate authorized
Console principal can approve in its focused inbox instead of the terminal
approver. Console does not host the model or create another control plane.
The terminal remains sufficient when Console is absent.

The DevOps agent and Console must apply the [same progress rules](#progress-and-protection).
Keep technical provenance expandable. Show the affected service, protection
availability, deadlines, progress, outcome and required action without asking
the user to assemble rollback commands or edit telemetry settings.

## Rollback capability truth

| Placement / backend | Wiring and required outcome |
|---|---|
| Local Docker | Retain the exact prior image and database compatibility/backup plan. Compose replacement is operator-managed unless the registered backend advertises a real rollback actuator. Otherwise fix forward or report manual intervention. |
| AWS ECS-small | Terraform in `honua-iac` declares infrastructure; inspect the runtime backend and prior task revision. A GitOps handoff with `SupportsRollback=false` cannot revert workload traffic. A direct ECS adapter must supply its provider receipt before rollback is claimed. |
| Helm / Kubernetes GitOps handoff | Pin chart values and image digest; the GitOps owner changes the revision. Unsupported handoff adapters return manual intervention. Helm wiring alone proves neither server rollback nor EKS certification. |
| Registered real rollback adapter | Require advertised support, a prior revision, an approved operation and an observed provider revert, followed by the same verification window. Missing any one prevents a rollback claim. |

Helm/Terraform wiring provisions the protection profile and its dependencies;
it is advanced installation work. Retain exact chart/module and configuration
identities with the target's runtime receipt. A chart rollback or Terraform
apply result alone proves neither service recovery nor a protected platform
update. Start from the [Helm installation](https://github.com/honua-io/honua-helm/blob/trunk/README.md)
or [Terraform infrastructure](https://github.com/honua-io/honua-iac/blob/trunk/README.md)
handoff, then verify the registered backend through the server control plane.

Application rollback and database restore are different operations. Consult
[Upgrade and rollback](../deploy/upgrade-and-rollback.md) before reverting an
image across schema changes. Local Docker and AWS ECS-small are the bounded
placement targets; exact-candidate certification is still required for both.
EKS, Azure, hosted models, broad autonomous remediation and #3300 performance
depth are outside this scenario. Whole-catalog GP and four cloud-native
formats retain their separate 2026.1 GA qualification requirements; customer
alerting, multi-tenancy and offline sync remain Preview.
