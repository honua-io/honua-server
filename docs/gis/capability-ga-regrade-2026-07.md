# Capability GA re-grade (2026-07, #2946)

Tracking issue: [#2946](https://github.com/honua-io/honua-server/issues/2946), part of
the release-safety program
[honua-release#58](https://github.com/honua-io/honua-release/issues/58) (P2). Companion
to the test-depth ticket [#2945](https://github.com/honua-io/honua-server/issues/2945),
which is landing in parallel and closes several of the evidence gaps this re-grade
found.

This is the one-time decision record for applying the
[GA criteria](capability-keys-schema.md#ga-criteria-2946) to the specific keys the
2026-07-20 audit flagged. It does not re-derive all 110 keys in
`capability-keys.v1.json` from scratch — that full sweep is out of scope for one PR —
it records the disposition of every key the audit named as a factually-wrong allowlist
reason, a mis-attributed route, or a shallow-evidence GA claim.

## Demotion candidates: kept-GA-pending-#2945 vs. demoted

`#2945` adds interface-level proving-test depth for several of the audit's demotion
candidates. Where its acceptance criteria closes the specific evidence gap the audit
found, the key stays GA rather than being demoted and re-promoted a week later — the
dependency is recorded here instead.

| Key | Decision | Why |
|---|---|---|
| `analytics.viewshed` | **Keep GA** (depends on #2945) | Audit found the proving test only ever seeds flat terrain (visibility trivially true). #2945's acceptance criteria adds a ridge/occlusion terrain fixture with expected-visibility assertions at the endpoint, closing the gap without a demotion round-trip. |
| `analytics.line-of-sight` | **Keep GA** (depends on #2945) | Same shared terrain-fixture fix in #2945 covers this key too. |
| `analytics.sun-shadow` | **Keep GA** (depends on #2945) | Audit found the test asserts `> 0`, not a known value. #2945's acceptance criteria adds a known-value assertion. |
| `analytics.reporting` | **Keep GA** (depends on #2945) | Audit found all 3 proving tests are error paths, no happy path. #2945's acceptance criteria adds "seed completed job → generate + render report". |
| `temporal.histogram` | **Keep GA** (depends on #2945) | Audit found no bucket-count assertion. #2945's acceptance criteria adds one. Already `CapabilityMaturity.Implemented` in `CapabilityRegistry.cs` (promoted GA in #2429) — no registry change needed either way. |
| `staticmap.*` (high-dpi, large-dimensions, rich-overlays) | **Keep GA** (depends on #2945) | Audit found entitlement-band tests only hit the absolute cap, never the entitlement-gated 1280–4096 band, and DPI tests never check output resolution. #2945's acceptance criteria adds entitlement-band tests and a real-resolution DPI assertion. |
| `analytics.slice` | **Demoted (documented decision only — no lever wired in this PR)** | Audit found the sole proving test asserts `> 0`, not a value (same shallow-evidence class as sun-shadow/viewshed). Unlike those, **#2945's acceptance criteria does not mention `analytics.slice`** — only viewshed/line-of-sight/sun-shadow/density/reporting/temporal.histogram are listed as fixed. See "Mechanism" below for why this demotion is recorded here rather than applied at runtime. |
| `scene.catalog` | **Demoted (documented decision only — no lever wired in this PR)** | Audit found only 3 shape-level discovery tests (`GET /api/scenes`, `/api/scenes/{sceneId}`, `/api/scenes/{sceneId}/resolve`) with no depth beyond listing/resolving a fixture. **Not mentioned anywhere in #2945.** See "Mechanism" below. |

**Explicitly not demoted** (per the issue's own instruction, these are "keep-GA-and-fix"
regardless of evidence depth, because the underlying surfaces are too core to hide and
#2945 is adding the missing depth): `identity.oidc`, `serve.wms`, `serve.wmts`, and the
`alerts.*` family (`alerts.evaluation`, `alerts.enter-exit`, `alerts.threshold`,
`alerts.dwell`).

## Mechanism: why `analytics.slice` / `scene.catalog` are a documentation-only demotion

The repo already has a real, runtime-enforced "hold a capability off the GA surface"
lever: `CapabilityRegistry.cs` descriptors carry a `CapabilityMaturity`, and
`CapabilityGateResolver` 404s the route group for any descriptor whose maturity is
`Experimental` unless `Capabilities:Experimental:<id>:Enabled` is set (globally or per
capability). It is a real mechanism — it already gates `sync.offline` and
`versioning.branch` off the default GA surface today, and every route group that uses it
calls `.WithCapabilityGate("<id>")` on its `MapGroup(...)`.

That lever does **not** reach `analytics.slice` or `scene.catalog` today:

- Neither key has a `CapabilityDescriptor` in `CapabilityRegistry.cs` at all — that
  registry only covers the curated `/mcp` + `honua.capability_manifest.v1` roster
  (~30 ids), not every key in `capability-keys.v1.json`.
- `analytics.slice`'s route (`POST /elevation/{datasetId}/slice`, in
  `SceneAnalysisEndpoints.cs`) is gated by `LicenseGate.RequireEntitlement` — the
  edition/entitlement system, a different mechanism that answers "is this edition paid
  for," not "is this GA yet."
- `scene.catalog`'s routes (`GET /api/scenes`, `/api/scenes/{sceneId}`,
  `/api/scenes/{sceneId}/resolve`, in the endpoint mapping referenced by
  `EndpointRegistry.Scenes.cs`) are public, unauthenticated discovery routes with no
  entitlement or capability gate at all today.

Wiring either into `CapabilityGateResolver` would mean: adding new
`CapabilityDescriptor` rows, converting the endpoint mappings from individual
`MapPost`/`MapGet` calls to a `MapGroup(...).WithCapabilityGate(...)`, and — for
`scene.catalog` specifically — accepting that a public discovery surface with unknown
consumers (potential CesiumJS/3D-client integrations) disappears from the default
surface by default. That is new plumbing for these two specific keys, not "apply the
existing lever," and per the #2946 scope decision it is left for a dedicated follow-up
rather than built inside this honesty/re-grade PR. Filing that follow-up (and, for
`scene.catalog`, assessing real-world consumer impact before flipping the default) is
the recommended next step; until then, treat this table as the authoritative "these two
keys did not meet the GA bar" record, independent of what `capability-matrix.v1.json`'s
generator-derived `maturity` count currently shows (it will keep reading
`implemented` for both, because the underlying `feature-catalog.json` entries have no
registry descriptor to resolve `experimental` from — a mechanical limitation of the
aggregation, not a disagreement with this decision).

## Allowlist corrections (factually wrong `noSurface` reasons)

Six `capability-no-surface-allowlist.v1.json` reasons were wrong; two of the six were
route mis-attributions rather than allowlist-reason problems and are now expected to
carry >0 `feature-catalog.json` entries once regenerated:

| Key | Problem | Fix |
|---|---|---|
| `dr.backup-automation` | Claimed "operational background job" — no such job exists in `src/`. | Reworded to `infra-owned`: backup automation is owned by the deployment's infrastructure/managed-database layer (honua-terraform parameterizes RDS backups/multi-AZ); a BYO-database deployment delegates entirely to the customer's managed database. |
| `dr.cache-backup` | Same false "background job" claim. | Reworded to `infra-owned`: no cache-state backup/restore feature exists in this server; Redis persistence is an infrastructure-layer concern. |
| `dr.failover` | Same false claim; also implied `FailoverDecisionEvaluator` was the surface behind this key. | Reworded to `infra-owned`; notes the evaluator had zero callers and was removed as dead code (see below), so it never was a real surface. |
| `dr.rto-rpo-reporting` | Same false claim; implied `RecoveryReadinessEvaluator` was the surface. | Reworded to `infra-owned`; same dead-code note. |
| `ai.spec-apply` | Allowlisted as "no distinct route" — but `POST /v1/spec/apply` is a real, distinctly catalogued route with 17 proving tests; it was just mis-attributed to `ai.spec-artifacts` in `capability-route-mapping.v1.json`'s family-wide `HTTP+gRPC` catch-all (no specific rule existed for this route). | Added a specific `routePrefix` rule mapping `/v1/spec/apply` → `ai.spec-apply` ahead of the catch-all; removed the now-inapplicable allowlist row (the key now has a real `feature-catalog.json` entry once regenerated). |
| `ai.workflow-generation` | Same shape: `POST /api/v1/console/workflow-packages/generate` is real (3 proving tests) but mis-attributed to `admin.control-plane` via the `Admin API` catch-all. | Added a specific `routePrefix` rule mapping `/api/v1/console/workflow-packages/generate` → `ai.workflow-generation` ahead of the catch-all; removed the allowlist row. |

A seventh finding was investigated per the issue but is **not** a mis-attribution:

| Key | Finding | Fix |
|---|---|---|
| `raster.cloud-storage-config` | Verified zero `LicenseGate` call sites reference this entitlement key anywhere in the codebase — it gates nothing. Cloud storage provider selection (`FileStorageServiceCollectionExtensions.AddCloudFileStorage`) is process-startup DI wiring driven by `HONUA_STORAGE_PROVIDER`, not a request-time endpoint, so there is no natural per-request gate point to wire it into. | Corrected the allowlist reason to state the truth plainly (no enforcement exists; paid-tier enforcement for cloud-served raster/COG delivery happens through the distinct `raster.cloud-cog-serving` gate instead) rather than falsely implying coverage. Reason code kept as `config-flag`. |

## Dead code removed

- `Honua.Core.Features.DisasterRecovery.Domain.FailoverDecisionEvaluator` and its test
  (`FailoverDecisionEvaluatorTests.cs`) — a pure, unit-tested static evaluator with zero
  call sites anywhere outside its own test.
- `Honua.Core.Features.DisasterRecovery.Domain.RecoveryReadinessEvaluator` and its test
  (`RecoveryReadinessEvaluatorTests.cs`) — same shape, zero call sites.
- `FailoverAssessment`, `FailoverPolicy`, `FailoverEnums` (`FailoverDecision`), and
  `HealthSample` — pure data types that existed only to support
  `FailoverDecisionEvaluator`; verified zero remaining consumers once the evaluator is
  gone, so they were removed alongside it rather than left orphaned.
- The `BackupKind.RedisSnapshot` enum value — verified zero producers (nothing ever
  constructs a `BackupRecord` with it in production code) and its only consumer was the
  test suite asserting it does *not* count as data-protecting.

All of the above are recoverable from git history if a concrete backup/failover
automation implementation resumes this work; `IBackupStatusProvider` and the remaining
`RecoveryObjectives`/`RecoveryReadiness`/`BackupRecord`/`BackupSchedule` vocabulary types
stay, since they have real consumers/contracts beyond the two removed evaluators.
`IBackupStatusProvider` itself currently has zero implementations in the codebase — noted
here as a related observation, not acted on in this PR (removing an unimplemented public
interface is a larger call than this ticket's scope).

## Redis durable-job loss contract

See
[Redis durable-job loss contract](../guides/deploy/backup-and-restore.md#redis-durable-job-loss-contract)
in the backup-and-restore guide for the stated contract, and
`RedisJobExecutionResilienceTests` in
`tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane/` for the new
integration test that kills and restarts a real Redis container mid-job and proves the
job still completes rather than being silently lost or permanently wedged.

## Non-goals of this re-grade

- A full manual re-grade of all 110 `capability-keys.v1.json` entries against the GA
  criteria. This record covers exactly the keys the 2026-07-20 audit named.
- Building a new capability-gating mechanism. `CapabilityRegistry` /
  `CapabilityGateResolver` / `Capabilities:Experimental:<id>:Enabled` already exist and
  were reused where they reached; where they did not reach (`analytics.slice`,
  `scene.catalog`), the demotion is documentation-only per the scope decision above.
- Promoting any existing experimental capability (`sync.offline`, `versioning.branch`).
- Building server-side DR automation. Architecture decision: the server stays
  stateless; DR is owned by IaC/managed-database tooling.
