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
the referenced test and fixture files have no content changes.

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
