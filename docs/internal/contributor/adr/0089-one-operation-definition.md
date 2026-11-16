# ADR 0089: One operation definition for governed transports and recovery

## Status

Proposed — design for [#7856](https://github.com/honua-io/honua-server/issues/7856).
This records the target contract and migration gates; it does not assert that the
existing endpoint families have migrated.

## Context

The operation runtime already supplies a catalog, dispatch, durable instances,
policy checks, proposal mapping and audit storage. `PublishedOperationToolSource`
projects catalog descriptors into MCP tools and refuses gated operations without
an approval mapper. Those are useful foundations, not a replacement runtime.

The remaining duplication is between those foundations and endpoint registration.
`AdminApiOperationCatalog`, `AdminAccessOperationCatalog` and
`AdminOperateOperationCatalog` repeat HTTP methods, paths and operation identities
that endpoint modules also declare. OpenAPI supplies request and response schemas,
but its relationship to the executing route is partly maintained by drift tests.
Approval mappings and executor registrations are assembled separately. Recovery is
operation-specific; a destructive operation does not acquire a safe inverse merely
because another operation has an opposite verb.

Consequently a route can ship without a governed projection, or its declared
safety can diverge from its execution. A second registry would only move the drift.

## Decision

### Definition and bindings

Evolve the existing operation definition and catalog rather than introduce a
parallel service. Each migrated operation has one stable `OperationId` and one
immutable definition containing its schemas, execution kind, side effects, blast
radius, determinism, dry-run support, approval model and compensation contract.
The definition is the source for mechanical projections. It is not a principal,
authorization grant or claim that an operation is safe in every deployment.

Keep transport-neutral policy and recovery contracts in Core. Server owns the HTTP
binding (method, route, OpenAPI operation ID, content type, input binding and typed
handler). Endpoint registration consumes that binding; it must not retype its
method and path. Dependencies continue to point from Server and Ai into Core.
Core does not depend on ASP.NET routing or MCP types. Bind handlers explicitly;
no reflection discovery, arbitrary delegate serialization or runtime codegen.

For the admin migration, retain the shipped OpenAPI schema as the existing input
source until its generator can consume the same binding. Resolve by operation ID
and verify method, path, required inputs and outputs against the binding. Do not
add another schema copy to the definition. Generated contracts and endpoint
metadata must agree in both directions, including operations missing from either
side. Any temporary adapter is named and counted in the migration tests.

### Five consumers

| Consumer | Derived from the definition | Runtime responsibility retained |
| --- | --- | --- |
| HTTP API | Binding identity, method, route, schema and endpoint metadata | Typed parsing, tenant/resource authorization, concurrency and handler execution |
| MCP | Existing descriptor/tool projection, inputs, outputs and safety hints | Caller visibility, descriptor budgets, approved exposure policy and dispatch |
| Approval | Approval model, operation class and review target | Durable proposal, exact input seal, current requester authorization and replay validation |
| Audit | Stable operation ID and definition revision linked to the instance | Existing durable audit writer records caller, tenant, correlation, proposal and outcome |
| Recovery | Explicit compensation capability and required evidence | Validate evidence, authorize, obtain any approval, dispatch and audit the compensation |

Registration associates each definition with exactly one executor and, when
gated, exactly one safe approval mapper. Reuse the existing publication and startup
validators. Unknown, duplicate or inconsistent registrations fail closed. A
runtime-disabled executor is not advertised as executable. Descriptor presence
alone never expands MCP exposure: the current audited projection and exclusions
remain until the operation has the same security and journey evidence as its peers.

Keep a definition revision in durable execution/recovery evidence so that a later
catalog change cannot reinterpret an old approved request. This is distinct from
the catalog snapshot version, which covers the whole published catalog. Adding a
revision requires an explicit persisted-contract migration and backward-reader
proof; do not silently change the meaning of existing operation instances.

### Approval is derived policy, not a shortcut

Retain ADR 0062's mechanical autonomy scoring and ADR 0064's fail-closed treatment
of unknown and mutating work. A definition can describe required safeguards; it
cannot override the resulting tier or mark an operation approved. A compensation
operation has its own classification and cannot inherit approval from the original
request. The common gateway remains the decision point for approved replay.

Both transport entry points must reach the same application execution pipeline.
Migrating route metadata is a first step, not permission to add more loopback HTTP
executors or to call one protocol handler from another. Existing loopback adapters
remain explicit migration debt until their underlying application service is
shared by the route and executor. Direct REST authorization and approved replay
must remain covered throughout that extraction.

### Compensation is explicit and evidence bound

Every migrated definition declares one of:

* **Not applicable:** a read-only operation has no mutation to reverse.
* **Unavailable:** a mutation has no supported compensation, with an operator-facing
  reason. Omission never means automatically reversible.
* **Supported:** a named compensation operation, typed capture/output contract,
  precondition and retention requirements, and a recovery executor with tests.

Do not infer an inverse from HTTP methods, operation names or CRUD pairs. A create
can have irreversible external effects; restoring old settings can overwrite a
subsequent operator's work. Before a supported mutation executes, capture the
minimum durable prior state required for recovery. Bind it to operation instance,
tenant, target, definition revision and resulting version. Redact secrets from
public descriptors and audit summaries; sensitive recovery data uses the existing
protected state boundary and its retention policy.

Recovery targets a completed operation instance, not model-supplied prior values.
It checks the target's current version against the recorded postcondition and
refuses when intervening edits invalidate the inverse. Submit the compensation
through normal validation, policy, approval and audit. Record the original and
compensating instance IDs and make retries idempotent. Partial or unconfirmed
compensation is reported as such. None of this relaxes ADR 0028's data-edit boundary.

### Migration and proof

Start with the folder/service-move family: it already has shared catalog-derived
MCP schemas, approval replay and ETag guards, and has focused authorization tests.
First remove repeated route declarations and attach the actual definition to each
endpoint. Prove one binding reaches the API route, descriptor, MCP tool, approval
review and audit identity. Declare unavailable compensation honestly until capture
and safe replay exist; route deduplication alone does not complete #7856.

Then extract the folder application mutation service before adding compensation.
Use service move as the first recovery journey: record all affected facets and
previous folder assignments, preserve tenant and facet authorization, and reject
recovery after any relevant graph change. Existing graph ETags are conservative
but safe preconditions. Folder delete must not claim support until child-folder,
service reassignment and alias semantics have equivalent durable evidence.

Migrate the remaining admin families in bounded groups, deleting their duplicate
registration/approval/audit tables as their bindings take over. Keep capability
coverage (ADR 0058) separate from operation execution policy, connected by stable
identities. Preserve compatibility-only visibility restrictions and intentional
MCP exclusions. No blanket expansion from all routes to all tools.

Before calling the issue complete, require:

1. A bidirectional inventory of mutating admin endpoints and operation definitions,
   with explicitly reasoned exclusions; no unexplained unprojected mutations.
2. Negative drift tests for changed path, method, schema, mapper, executor, policy
   and compensation registration, plus successful generated-contract checks.
3. Transport tests proving the same identity, required inputs and policy across
   HTTP and stock-client MCP discovery/submit/results, including both content channels.
4. Runtime refusal tests for unauthorized/cross-tenant requests, forged approvals,
   stale ETags, unsupported recovery and replay after intervening changes.
5. Durable audit and recovery tests for crash/retry boundaries and duplicate delivery;
   no success outcome until the underlying mutation or compensation is confirmed.
6. A recorded installed journey through proposal, human approval, execution, audit
   and compensation (or explicit unavailable response) via the verification queue.

Write failing tests for each migration slice before changing its implementation.
Record remaining families on the issue; a merged slice is not the live completion
of this design.

## Consequences

Adding a governed operation changes one definition and its application handler,
with mechanical transports and review metadata sharing the same identity. Drift
becomes an explicit failing invariant instead of a periodic endpoint-count audit.

The migration crosses existing endpoint families and persisted execution contracts;
it must be incremental. Unknown recovery stays unavailable. Sharing definitions
reduces duplicated metadata but does not eliminate operation-specific validation,
resource authorization, side-effect handling or evidence needed for safe recovery.
