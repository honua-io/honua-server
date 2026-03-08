# ADR-0025: Multi-Provider Operation Architecture

## Status
Accepted

## Context

Honua now spans multiple kinds of asynchronous work:

- deploy and rollback control-plane workflows
- geoprocessing and batch compute
- imports, tile operations, and other run-to-completion jobs

The existing operation progress model is useful for operator visibility, but it
is not sufficient as the source of truth for all control-plane behavior.

Key constraints:

1. Deploy workflows have different semantics from batch jobs. A Kubernetes
   rollout, an AWS Lambda alias shift, and an Azure Functions revision swap are
   not the same kind of operation as a geoprocessing batch run.
2. Deploy control cannot rely on node-local background workers or in-memory
   fallback state. A deploy operation must survive replica restarts and be safe
   to reconcile from any control-plane instance.
3. Serverless deploy targets run with out-of-band migrations. Honua already
   documents `HONUA_SKIP_MIGRATIONS=true` for serverless targets, so deploy
   control must not assume a startup-migration lifecycle.
4. Redis is already part of Honua's multi-node coordination story. It is the
   correct hot state layer for operation coordination, leases, active indexes,
   and short-lived workflow state.
5. Geoprocessing may legitimately use provider-native execution systems such as
   AWS Batch, but deploy workflows should not inherit batch-job semantics.

Without a clear split, Honua risks baking container-job assumptions into
Lambda/Functions deploy control and collapsing distinct provider models into an
overly generic "job" abstraction.

## Decision

Honua will standardize on an `operation` control-plane concept with two
execution families:

1. `workflow operations`
2. `execution jobs`

### Workflow Operations

Workflow operations are reconciled state machines. They are used for:

- deploy
- rollback
- migration coordination
- other control-plane actions that converge toward a desired state

Characteristics:

- durable Redis-backed state with no in-memory fallback as the authoritative
  record
- desired state and observed state tracked explicitly
- distributed lease-based reconciliation
- provider operation IDs tracked explicitly
- idempotent submission and rollback semantics
- provider-specific observation loops

Example backends:

- Kubernetes
- AWS Lambda
- Azure Functions

Workflow states are richer than generic queue states. They include:

- `planned`
- `awaiting_approval`
- `submitted`
- `reconciling`
- `succeeded`
- `failed`
- `rollback_requested`
- `rolled_back`
- `manual_intervention_required`

### Execution Jobs

Execution jobs are run-to-completion workloads. They are used for:

- geoprocessing
- ETL / data movement
- tile seeding and similar batch-style tasks

Characteristics:

- queue-oriented semantics
- explicit provider job submission
- provider polling or callback-based progress
- cancellation when supported by the backend
- retry on failure when safe

Example backends:

- AWS Batch
- Kubernetes Jobs

Execution job states remain batch-oriented:

- `queued`
- `provisioning`
- `running`
- `succeeded`
- `failed`
- `cancelled`

### Redis and Durability

Redis is the hot coordination store for operations:

- operation records by ID
- active-operation indexes
- provider-operation indexes
- reconciliation leases
- retry scheduling metadata

Redis is the first durable coordination layer for this architecture. Honua may
later mirror operation history or audit trails into PostgreSQL, but Redis is
the system-of-record for active workflow and job coordination.

### Provider Capability Model

Each backend must advertise capabilities instead of pretending all providers are
interchangeable.

Deploy backend capabilities include:

- supports rollback
- supports cancellation
- supports traffic shifting
- requires out-of-band migrations
- supports progress polling
- supports revision pinning

Batch compute capabilities include:

- supports cancellation
- supports log streaming
- supports progress polling
- supports retry
- supports artifact staging

### API and Control-Plane Implications

The admin API should expose operation resources, not node-local background job
internals.

- workflow operations use reconcile-oriented endpoints and state models
- execution jobs use queue/job-oriented endpoints and state models
- the existing generic operation progress endpoints remain useful for operator
  visibility, but they are not the source of truth for deploy orchestration

## Consequences

### Positive

- Honua can support Kubernetes, Lambda, and Functions deploys without forcing
  them into a single fake job model.
- AWS Batch becomes a clean backend for geoprocessing without contaminating
  deploy semantics.
- Redis-backed leases and state make multi-node control-plane coordination
  explicit and survivable.
- Provider capability modeling keeps the control plane honest about what each
  backend can actually do.

### Negative

- This introduces more types and interfaces than a single generic job service.
- Deploy control and batch execution now require separate state machines.
- Redis becomes a critical part of control-plane coordination and must be
  treated as required infrastructure for multi-provider operation control.

### Follow-On Work

- Introduce dedicated operation store interfaces for workflow operations and
  execution jobs.
- Implement Redis-backed operation stores with lease semantics.
- Add deploy backend adapters for Kubernetes, AWS Lambda, and Azure Functions.
- Add batch compute backend adapters for AWS Batch and Kubernetes Jobs.
- Add control-plane endpoints for planning, submission, observation, and
  rollback based on the workflow operation model.
