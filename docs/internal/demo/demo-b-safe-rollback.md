# Demo B — Flagship beat: AI-DevOps safe layer evolution with DB-inclusive rollback

> This is the runnable sequence behind **Demo B Beat 8** (the flagship "safe layer
> evolution with reversible rollback" beat that the ops-champion runbook
> `demo-b-ops-runbook.md` lists as the closing moment). It documents the exact
> endpoints/commands the L3 E2E harness calls. The harness owns the *assertions*;
> this document + `scripts/demo-b-safe-rollback.sh` own the *capability and the
> sequence*.
>
> **The beat:** a metadata/layer change is deployed → a post-publish health check
> fails (deterministically injected) → everything rolls back safely (metadata
> revision reactivated AND the operational schema reverted via the down-script) →
> the DevOps AI detects the failed health check, diagnoses it, and proposes a
> human-approved resolve.

## What is real (shipped on the feature branches)

| Piece | Where | Status |
|---|---|---|
| Additive metadata-release lifecycle (Preflight → Backup → ScriptMigration → ETL → MetadataApply → Smoke → Complete) | honua-server `Features/ControlPlane/MetadataReleaseReconciler.cs` (#1738/#1739) | Real (draft PR) |
| Health gate = post-publish **Smoke** check via the canonical query pipeline | `MetadataReleaseStageActions.cs` (`MetadataReleaseSmokeChecker`) | Real |
| Smoke failure → **rollback**: reactivate prior Metadata v2 revision **+ run the reversible down-script (drop-added-column)** | `MetadataReleaseReconciler.ExecuteRollbackAsync` + `MetadataReleaseScriptExecutor.ApplyInverseAsync` | Real (metadata + DB-inclusive) |
| Deterministic **fault injection** for the Smoke gate (demo only, fenced to non-prod) | `MetadataReleaseFaultInjectionOptions` + `MetadataReleaseSmokeChecker` | Real (this PR) |
| Durable op storage (required) | Redis-backed `RedisWorkflowOperationStore` | Real |
| AI **detect** seam: read the rolled-back operation + smoke evidence | honua-devops `inspect_metadata_release` tool + `DeployOperationReader` metadata-release readers | Real (this PR, honua-devops) |
| AI **diagnose + propose resolve** (human-approved) | honua-devops `GuidedFixPlanner` / `triage_support_ticket` / `auto_remediation_plan` | Real (plan-mode, pr-first) |

**Snapshot-required (data-affecting, non-reversible) rollback is deferred** — the
server refuses it at Preflight with a clear message and parks the op at
`ManualInterventionRequired`. Demo B uses the **additive/reversible** path only.

## Durable op storage decision (Redis vs Postgres) — and cost

The metadata-release lifecycle persists every workflow operation through
`IWorkflowOperationStore`. The **only** implementation is
`RedisWorkflowOperationStore`; there is no Postgres-backed op store today. So the
rollback beat needs Redis.

Cheapest viable path, in order:

1. **Local Docker Redis ($0)** — for recording the demo or running the L3 harness
   against a local server, `docker compose up -d` already starts Redis. The
   lifecycle, smoke gate, rollback, and down-script all run with zero cloud spend.
   The unit/integration tests in this PR prove the closed loop here. **Prefer this
   for recording.**
2. **Ephemeral ElastiCache, record-and-teardown** — only if the beat must run
   against the deployed AWS env. Stand up a single `cache.t3.micro` node, point the
   server at it, run the sequence, then **destroy it**. Discipline: it is created
   for the take and torn down immediately after. See "Ephemeral Redis" below.

A Postgres-backed op store would avoid the extra Redis cost, but building it is a
separate, larger change (owned by the durable-stores work, honua-server#1593) and
is **not** required for the demo: option 1 is already $0. Recommendation: **record
Beat 8 against the local Docker stack (option 1)**; reserve option 2 for a live
AWS take and tear it down the same hour.

### Ephemeral Redis (only for a live-AWS take; record-and-teardown)

Use the AWS CLI directly (do NOT edit the iac `aws-serverless` module — Track 1
owns it; this avoids colliding with the in-flight iac PRs):

```bash
# Create — single node, no replicas, smallest type. ~$0.017/hr on-demand
# (cache.t3.micro, us-west-2). A 1-hour take is ~$0.02. TEAR IT DOWN after.
aws elasticache create-cache-cluster \
  --cache-cluster-id honua-demo-b-ephemeral \
  --engine redis --cache-node-type cache.t3.micro --num-cache-nodes 1 \
  --security-group-ids <sg-with-server-ingress> \
  --cache-subnet-group-name <demo-subnet-group>

# ... point the server at it (Cache:Redis:ConnectionString / Aspire Redis CS),
#     run the sequence below, capture the recording ...

# TEARDOWN (do this the same hour — the founder is watching the bill):
aws elasticache delete-cache-cluster --cache-cluster-id honua-demo-b-ephemeral
```

Cost guardrail: a forgotten `cache.t3.micro` is ~**$12/mo**. The ephemeral node
exists only for the take. The default demo footprint stays ~$25/mo (per devops#97)
because we do **not** leave Redis running.

## Deploy target: never run fault injection on `live`

Fault injection is hard-fenced two ways:

1. `MetadataReleaseFaultInjectionOptions.Enabled` + `ForceSmokeFailure` are both
   **off by default**; nothing injects unless explicitly configured.
2. Even when enabled, injection only fires for a target environment on
   `AllowedEnvironments` (default `staging`, `dev`, `demo-staging`, `demo-dev`).
   `production` / `live` are never on the list, so a misconfiguration cannot fail a
   real release.

On AWS, Demo B runs against the **`staging`** Lambda alias
(`honua-demo-demo-honua:staging`), created additively for exactly this purpose —
never the customer-facing `live` alias.

## Configuration to arm the beat (demo only)

```jsonc
// appsettings / env (server side). Off by default; set ONLY for the demo run.
{
  "ControlPlane": {
    "MetadataRelease": {
      "FaultInjection": {
        "Enabled": true,
        "ForceSmokeFailure": true,
        "AllowedEnvironments": [ "staging" ],
        "Reason": "Injected smoke failure (Demo B safe-rollback fault injection)."
      }
    }
  }
}
```

Env-var form:

```bash
export ControlPlane__MetadataRelease__FaultInjection__Enabled=true
export ControlPlane__MetadataRelease__FaultInjection__ForceSmokeFailure=true
export ControlPlane__MetadataRelease__FaultInjection__AllowedEnvironments__0=staging
```

## The sequence (endpoints the harness calls)

All admin endpoints are gated by `X-API-Key: $HONUA_DEMO_API_KEY`.

### 1. Submit the layer change (add a field + optional ETL populate)

`POST /api/v1/admin/metadata/releases/operations`

```bash
curl -s -X POST "$BASE/api/v1/admin/metadata/releases/operations" \
  -H "X-API-Key: $HONUA_DEMO_API_KEY" -H 'Content-Type: application/json' \
  -d '{
        "packageId": "demo-b-add-owner-email",
        "targetEnvironment": "staging",
        "resourceSemanticId": "maui-parcels",
        "newFieldName": "owner_email",
        "newFieldType": "String",
        "dataPopulateWorkloadId": "populate-owner-email",
        "reason": "Demo B: additive layer evolution",
        "idempotencyKey": "demo-b-add-owner-email"
      }' | jq .
```

Returns a `201` with a `DeployOperationResponse` (`operationId`, `status:
Submitted`, `metadataRelease.currentStage: Preflight`). The background reconciler
then walks the additive stages.

### 2. Watch the lifecycle health-gate and roll back

`GET /api/v1/admin/metadata/releases/{packageId}/operation` (reconciles on read)

```bash
curl -s "$BASE/api/v1/admin/metadata/releases/demo-b-add-owner-email/operation" \
  -H "X-API-Key: $HONUA_DEMO_API_KEY" | jq '{status, currentPhase, stage: .metadataRelease.currentStage, rollback: .metadataRelease.rollbackPlan, evidence: .metadataRelease.evidenceRefs}'
```

Poll until terminal. With fault injection armed the Smoke gate fails and the
operation lands at:

- `status: RolledBack`
- `currentPhase: "Reversible rollback complete (ScriptRollback): reactivated prior revision and executed the inverse script."`
- `metadataRelease.evidenceRefs[]` contains a `kind: "smoke"` ref (the health-gate proof)
- the added field `owner_email` is **gone** from the activated schema (down-script ran)

This is the **metadata + DB-inclusive rollback**: the prior Metadata v2 revision is
reactivated *and* the reversible down-script drops the added column.

### 3. AI detects → diagnoses → proposes resolve (human-approved)

The DevOps agent (honua-devops, plan mode + pr-first by default) reads the
rolled-back operation and proposes a resolve:

```bash
cd honua-devops
export HONUA_DEVOPS_HONUA_API_BASE_URL="$BASE"
export HONUA_DEVOPS_HONUA_API_KEY="$HONUA_DEMO_API_KEY"

# Detect + diagnose the safe-rollback outcome (read-only):
dotnet run --project src/Honua.DevOps.Agent -- \
  --prompt "inspect the metadata release for package demo-b-add-owner-email and tell me what happened and how to resolve it"
```

The agent calls the `inspect_metadata_release` tool, which surfaces: the health
gate ran (smoke evidence present), the deploy was rolled back (metadata + DB
down-script), the rollback class, and the failing phase. It then proposes a
human-approved resolve (fix the layer change / ETL and re-submit through the
governed create path). It never auto-mutates: re-submission is human-approved.

Programmatic detect (what the harness asserts on) — the same JSON the tool reads:

```bash
curl -s "$BASE/api/v1/admin/metadata/releases/demo-b-add-owner-email/operation" \
  -H "X-API-Key: $HONUA_DEMO_API_KEY" \
  | jq '{detected: (.status=="RolledBack"),
         health_gate_ran: ([.metadataRelease.evidenceRefs[].kind] | index("smoke") != null),
         db_inclusive_revert: (.currentPhase | test("Reversible rollback complete")),
         rollback_class: .metadataRelease.rollbackPlan.class}'
```

### 4. Disarm fault injection (clean up after the take)

```bash
unset ControlPlane__MetadataRelease__FaultInjection__Enabled
unset ControlPlane__MetadataRelease__FaultInjection__ForceSmokeFailure
# (and tear down the ephemeral Redis if you stood one up — see above)
```

A re-run with injection **disarmed** completes the same release `Succeeded`
(smoke passes, `owner_email` stays live) — proving the additive path works and the
rollback was purely the injected fault, not a real defect.

## Endpoints summary (what the L3 harness calls)

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/v1/admin/metadata/releases/operations` | Submit the additive layer change (+ ETL) |
| GET | `/api/v1/admin/metadata/releases/{packageId}/operation` | Detect lifecycle/health-gate/rollback state (reconciles on read) |
| GET | `/api/v1/admin/deploy/operations/{operationId}` | Inspect the same operation by id |
| POST | `/api/v1/admin/deploy/operations/{operationId}/rollback` | (Optional) operator-initiated rollback of the same op |

DevOps AI tool: `inspect_metadata_release` (honua-devops) — read-only detect +
diagnose + propose human-approved resolve.

## Where the closed loop is proven without the deployed env

- `tests/dotnet/Honua.Core.Tests/.../MetadataReleaseFaultInjectionOptionsTests.cs`
  — the fault-injection fence (off by default; never fires on prod/live).
- `tests/dotnet/Honua.Server.Tests/.../MetadataReleaseReconcilerTests.cs`
  — `ReconcileAsync_InjectedSmokeFailure_DrivesReversibleRollbackClosedLoop`: an
  injected smoke failure drives the reversible rollback (prior revision reactivated
  + down-script run), plus the existing forward/rollback/snapshot-refusal coverage.
- honua-devops `MetadataReleaseDetectReaderTests` — the AI detect seam reads
  RolledBack/Succeeded/ManualInterventionRequired + smoke evidence from the
  operation JSON.

Run them:

```bash
# honua-server
dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
  --filter FullyQualifiedName~MetadataReleaseFaultInjectionOptions
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter FullyQualifiedName~MetadataReleaseReconcilerTests
# honua-devops
dotnet test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj \
  --filter FullyQualifiedName~MetadataReleaseDetectReader
```
