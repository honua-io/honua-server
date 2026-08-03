# ADR-0070: Carry the submitter's claim snapshot — not a materialized predicate — onto background jobs

## Status

Accepted. Implements honua-server#3068 (row-level security and field masking on
geoprocessing background layer reads) and supports honua-server#3046 (submit-time
per-layer read authorization).

## Context

Row-level security (RLS) and field masking are resolved per read, from the caller's
claims:

- `RowLevelSecurityFilterSource` reads `IHttpContextAccessor.HttpContext?.User`, looks
  up the layer's `RlsPolicy` set for the principal's roles, and builds a parameterized
  predicate of the form `attribute IN (values of policy.ClaimType)`.
- `FieldMaskSource` reads the same principal, looks up the layer's `FieldMaskPolicy`
  set for the principal's roles, and returns the attribute names to drop.

Both are consulted inside the feature store (`PostgresFeatureStore`,
`PostgresStorageMappedFeatureReader`) at the shared projection/filter seam, so every
synchronous protocol surface inherits them.

Geoprocessing jobs execute on `JobExecutionService`, a `BackgroundService`. There is no
`HttpContext` there, so both sources returned "nothing applies" and every layer-sourced
job — `source.honua-layer` and everything reading through it (`analytics.*`,
`generalization.*`, `conversion.feature-project`, enrichment) — published an artifact
containing **all** rows and **all** attributes, for a caller who would have been
restricted on a synchronous read of the same layer.

Closing this requires capturing something at submit time (the only moment the principal
exists) and carrying it to the worker. The durable job record is replicated to Redis and,
for out-of-process backends, projected onto a worker payload, so *what* is carried is a
security decision in its own right.

## Decision

**Persist the submitter's claim snapshot; re-derive the predicate and the mask at read
time.**

`OperationAuditInfo.SubmitterSecurityContext` (`JobSecurityContext`) carries the
submitter's principal id, tenant, and captured claims. It is stamped at submit time by
the shared geoprocessing submit pipeline and travels with the job record.

The stored tenant is the **effective request tenant** from `ITenantContext`, after the
tenant middleware has applied configured `TenantClaimTypes`, an authorized
`X-Honua-Tenant` override, or default handling. Capture must not re-parse hard-coded
`tenant_id`/`tid` claims: an accepted header override can intentionally differ from the
token tenant. Restore replaces the canonical tenant aliases with that authoritative
value so deferred authorization cannot fall back to stale token scope.

At execution, `GeoprocessingDispatchJobExecutor` — the single seam every managed process
executor runs through — opens a `JobSecurityScope` (an `AsyncLocal` ambient scope,
following `FeatureMutationOutboxScope`) for the whole dispatch.
`RowLevelSecurityFilterSource` and `FieldMaskSource` fall back to that scope when there
is no request principal, restoring an equivalent `ClaimsPrincipal` and then running their
existing logic unchanged.

### Rejected: serializing the resolved SQL predicate

Writing the materialized `SqlFragment` (or the resolved mask list) onto the job spec was
rejected because it would:

- put an executable SQL predicate on the durable wire and into any out-of-process worker
  payload, widening the blast radius of a compromised job record from "identity" to
  "executable filter";
- freeze policy at submit time, so tightening or revoking an RLS policy would not affect
  jobs already queued;
- duplicate the predicate-construction contract across the submit path and the read path,
  which is exactly the drift the shared-pipeline rule exists to prevent.

### Rejected: role names only

RLS policies declare the claim type their predicate compares against, which is frequently
not a role claim (`category`, `region`, `org_id`, …). A role-only snapshot would produce
an empty value set for those policies. That is fail-secure for RLS (an empty `IN` list
translates to `FALSE`), but it would silently return zero rows for correctly-authorized
callers, so the snapshot carries claims.

Role claims are captured first and are never truncated by the (256-claim) capture budget,
because field masking keys purely on roles and a dropped role is the one truncation
outcome that is **not** fail-secure. Credential-bearing claim types (`access_token`,
`refresh_token`, …) are excluded: a job record is not a credential store.

### Fail-closed, not fail-open

`JobSecurityScope` is opened even when the record carries no snapshot. That combination —
inside a job, with no captured identity — is refused at `HonuaLayerDagSource`, the single
connector every layer-sourced process reads a catalog layer through, and again by both
policy sources. A job record written before this field existed therefore **fails** rather
than reading unrestricted.

A call chain with no scope at all is an ordinary request thread or a server-internal
background read with no caller to constrain (tile seeding, import workers, schedulers);
those are unchanged.

### Approval lane and workflow lane

Two paths submit under a principal that is not the real submitter, and both would
otherwise pin the wrong identity:

- The **approval lane** persists the snapshot on `GeoprocessExecutionPayload`, because
  `ResumeApprovedJobAsync` reconstructs a principal from `Audit.RequestedBy` alone; without
  this, an approved job would resolve an empty role set and therefore no restriction.
- The **workflow lane** submits each step under `OrchestrationSystemPrincipal`, which
  carries `role=admin`. `WorkflowOrchestrationEngine` captures the run requester's snapshot
  at run creation and replays it on every step submission via
  `IWorkflowJobExecutor.SubmitJobAsync`'s `submitterSecurityContext` parameter, so the
  orchestrator identity gates *dispatch* while the requester's identity gates *visibility*.

Both lanes **inherit** their snapshot from a durable record rather than capturing it from the
principal in hand, so a *missing* inherited snapshot must be distinguished from "no snapshot
argument supplied". `SubmitJobCoreAsync` takes an explicit `inheritsSubmitterSecurityContext`
flag for exactly this reason: an ordinary adapter captures live from the submitter, while an
inheriting lane whose record predates the field is REFUSED
(`GeoprocessingAuthorizationException`) at submit time. A `??=` fallback here would be a
privilege escalation, not a convenience — the workflow lane would recapture from the
`role=admin` orchestrator, and the approval lane from the name-only resume principal whose zero
role claims match zero RLS policies and therefore resolve to *no* row filter. Either fallback
yields a non-null snapshot that sails past the fail-closed guards at the read seam, so the
refusal has to happen at submit time.

## Consequences

- Job output for RLS/field-mask-restricted callers shrinks to match the synchronous
  surfaces. This is a behavioral tightening and belongs in release notes.
- Jobs queued before the upgrade have no snapshot and fail on their first catalog-layer
  read instead of returning unrestricted data. They must be resubmitted.
- Workflow runs and approval proposals created before the upgrade are refused at *submit*
  time (the step submission or the approval resume fails) rather than at first read, because
  their snapshot is inherited and absent. They must be resubmitted too.
- Policy changes take effect on already-queued jobs, because the predicate and mask are
  re-derived per read.
- Out-of-process backends (GDAL worker, AWS Batch) do not reopen catalog layers. Vector
  catalog reads go through the managed `source.honua-layer` connector. For a native raster
  process, a `rasterId` is first resolved to its owning `layerId` using registration metadata;
  the shared layer gate authorizes that layer, and only then may the serving process read and
  materialize raster bytes onto the job's `source` input. Unknown or mismatched references
  fail through the same authorization channel as forbidden layers, before byte access. A
  future native process that reads a catalog layer directly must propagate the security scope
  to the read seam or be refused.
