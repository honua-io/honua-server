# GP workspace storage audit

The PostgreSQL repair for #4780 implements the existing `env:workspace` and
`env:overwriteOutput` contract. It adds durable workspace and artifact references
through migration 118 and the shared lifecycle service. It does not add a process,
protocol, SaaS tenancy system or desktop adapter.

Relative to retry repair commit `84a15fde2bc644708243423b4668f6dde104aa44`, the
catalog digest roots change in two files: the dispatcher passes an already
captured optional scope into workspace resolution, and the service registration
comment describes the now-available provider. Catalog membership, individual
executors, destructive classification and declared operation entry points are
unchanged. All 98 operation rows and their semantic evidence remain unchanged;
the existing semantic evidence method bodies remain subject to the unchanged
architecture digest checks. Additional tests cover the provider and review fixes.

Job-entry operations with no requested workspace retain their execution path.
Those requesting a workspace use the same owner, label and retention policy,
with PostgreSQL serializing concurrent named creation. Existing optional scope
values stay separate from null/unscoped workspaces. The current single-deployment
certification configuration remains unscoped.

Output registration uses one transaction for collision checking and replacement.
Failed replacement preserves the prior artifact. Writes require an active,
unexpired workspace; workspace deletion requires prior artifact cleanup. This
store owns reference records and inline data URIs. It does not follow an external
URI to delete another provider's resource or claim to reclaim that resource's
physical storage.

Focused PostgreSQL tests exercise concurrent creation, case-insensitive output
collisions, exact metadata, usage accounting, failed-replacement rollback,
expiration, cancellation and cleanup. Composed GP tests exercise both `execute`
and `submitJob` with the production provider registration, real PostgreSQL/Redis,
an independent rectangle-area oracle, host restart, denied overwrite and successful
replacement. These are source integration tests, not shipping-image or native
desktop receipts. They do not establish every process's workspace output behavior.

No operation verdict is promoted by this bounded source audit. The existing
shared runtime gaps and candidate-binding obligations remain. #4780 still needs
release-artifact inclusion, its migration, positive shipping replay and affected
desktop revalidation before the certification defect can close.

The review follow-up applies migration 118 through DbUp's actual `HonuaSchema`
variable and tests that substitution against isolated PostgreSQL schemas. Both
scheduled ticks and the polling loop honor disabled automatic cleanup. Named and
explicit workspace creation share an owner-level transaction lock for the active
workspace-count quota; reuse at the limit stays valid, and quota rejection is
non-retryable. Native GDAL workers currently have no workspace lifecycle provider.
GPServer rejects their workspace/overwrite controls before submission, with a
second fail-closed guard in the worker for old or directly authored jobs. This
negative control does not establish positive native-worker workspace support.

Earlier source receipts at `cae99b01` precede these four review corrections. The
full local gate for that revision was interrupted deliberately during formatting
after its clean build. Revised focused suites pass: PostgreSQL 8, worker dispatcher
7, lifecycle/cleanup/job execution 129, native-control endpoints 3, and durable GP
8. The first revised durable suite had a SOAP union submission HTTP 503; unchanged
source then passed the isolated case and the complete suite. That failure remains
recorded with no established cause; no test retry or assertion was relaxed.

Review receipts and exact source patches are retained in honua-client-compat at
`evidence/workspace-store-review-20260914/` (commit `0eb417f`). Relative to
`cae99b01`, the only changed catalog digest root adds the typed permanent quota
rejection catch to the shared dispatcher. All 98 operation rows remain unchanged.
The canonical emitter passes (1 check), and all 12 architecture drift/evidence
checks pass. The emitted catalog adds one proving-test reference for native-worker
control rejection; all other fields are unchanged. The full pre-PR gate, including
final formatting, remains separate and is not claimed passed.
