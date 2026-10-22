# GA spec review foundation

This opt-in experimental foundation is **not a qualified GA journey**. Set
`GaSpec:Enabled=true` to register `/v1/spec/ga/*`; it is absent by default.
Existing `/v1/spec/*`, imperative tools and release pins retain their behavior.

## Integration prerequisite

The canonical AgentWorkspace implementation remains on the unlanded #6297
feature branch. `IGaSpecContextReader` deliberately defaults to
`UnavailableGaSpecContextReader`; valid plan requests return
`workspace-context-unavailable`. There is no substitute workspace registry.

A conforming reader must use `IAgentWorkspaceStore.GetAsync`,
`GetTargetIdAsync`, `AgentWorkspaceState.CanRead`, and `Current`/`Capture`
from the canonical workspace implementation. It must verify current actor
membership/resource permissions, current view revision, tenant, target,
environment and exact publication draft/source inputs; return a stable hash of
that authorized snapshot. Apply re-reads it under the applying human. Changed
context invalidates the review. Register this adapter only with the qualified
workspace dependency. A browser revision string/header is never authority.

## HTTP contract

All routes require current admin authorization. `GET /bundle` returns the
bounded generated canonical bundle. Pin its `bundleVersion` exactly.

`POST /plans` accepts only the following intent fields (unknown fields fail):
`bundleVersion`, `workspaceId`, `viewId`, `targetId`, `environment`,
`contextRevision`, `connectionId`, `schema`, `table`, `layerName`,
`serviceName`, optional `expectEnabled`.

`GET /plans/{planId}?revision={revision}` requires the exact review revision.
`POST /plans/{planId}/apply` accepts `{revision,commandId}` (commandId UUID).
It authenticates the concrete AdminSession cookie handler and independently
re-evaluates the human's current admin grants, then checks the apply
entitlement. API keys, development bypass and model-facing operator bearers
cannot authorize Apply. Console forwards its existing isolated server cookie
session through its CSRF-protected BFF. Attribution is server-derived.

`GET /runs/{planId}` reads the admitted run. **runId equals planId**, so loss of
the Apply response does not lose the lookup key. Before admission it returns
404. The identical command and applying actor may retrieve a prior result;
another command cannot replay a consumed plan. Plan and run records use the
existing durable `IWorkflowOperationStore` and its optimistic CAS. They have
the store's normal bounded retention (seven days by default), not indefinite
archival retention. There is no process-local fallback or second scheduler.

Views contain `planId`, nullable `runId`, `revision`, full `spec`,
`actionHash`, `verificationHash`, `observedHash`, `verdict`, `execution`,
`evidence`, `draftedBy`, nullable `appliedBy`/`layerId`, and `blockers`.
Problem details have a stable top-level `code`. Unknown adapter failures are
sanitized without serializing provider exception details.

## Qualified adapter scope and remaining work

The initial adapter supports create/noop/refuse for one PostgreSQL source-table
layer under an operator-bound encrypted connection. It re-observes the catalog
before Apply and after the effect, uses the existing publisher's database
identity/advisory lock for duplicate prevention, and never treats an artifact
cache hit as success. `expectEnabled` is hashed separately from the effect.
Only enabled layers with matching source and display identity can be noop.
Update/drift and multiple matches refuse. The readback oracle verifies catalog
publication/enablement; it does not prove dataset-content fidelity. The final independent readback hash is persisted
with the layer identity in the durable review context.

Configuration is scoped per registered connection:

```json
{"GaSpec":{"Enabled":false,"ConnectionBindings":{"<connection-guid>":{
  "TenantId":"<tenant>","Purpose":"layer.publish","Revision":"<operator-policy-revision>",
  "Host":"<literal-IP>","Port":5432,"Database":"<database>"
}}}}
```

This binding is provisioned/revoked outside the model surface. Private networks
require this explicit destination binding. DNS names, redirects, imports and
unregistered destinations are unsupported. The adapter compares decrypted
connection host/port/database inside the outbound adapter; credentials never
enter the plan, events, stored context, hashes or result. env/file secret refs
are prohibited; aws-sm/vault refs return `secret-adapter-unavailable` until
qualified adapters and a human-bound catalog exist. No secret adapter support
is claimed by this first foundation.

Synchronous publication is bounded to 1–10,000 rows. Larger jobs block until a
qualified durable layer-publish job adapter is integrated. Source migrations
must already be installed; this route does not auto-migrate a destination.
Apply persists admission before invoking the publisher. Restart/lost final
write exposes `interrupted`, unverified state and prevents blind replay;
automatic uncertain-outcome reconciliation is not implemented. Readback is
not proof that a previously uncertain command caused the current resource.

Still required for production: canonical workspace adapter integration and
connected qualification; durable async jobs/receipts and restart reconciliation;
secret catalog/providers and broader destination adapters; source-version
preconditions and independent data oracles; scoped artifact storage; separate
proposer/approver publication governance; the remaining effectful operations
and GP execution adapters; both-entry-path connected/pilot acceptance evidence.
