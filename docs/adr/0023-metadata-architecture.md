# ADR-0023: Metadata Resource Model and GitOps-Ready Storage

## Status
Accepted

## Context

Metadata is the control plane for Honua. It defines connections, datasets, layers,
services, styles, imports, and policies that drive every protocol. Schema changes
in this metadata have high risk across releases (Admin UI, API, and data plane).
We also want a clean path to GitOps workflows without forcing a heavy document
store or a complicated release process.

We need an idealized architecture that:
- Supports safe, additive evolution across releases.
- Keeps Admin UI and API compatibility predictable.
- Enables optional GitOps without making it the primary storage.
- Works with an optional Redis cache but does not depend on it.
- Separates desired state (author intent) from derived runtime artifacts.

## Decision

Adopt a Kubernetes-style resource model for metadata, with versioned schemas,
explicit spec and status separation, and an optional GitOps interface.

### Resource Envelope

All metadata is stored as resources with a common envelope:

```json
{
  "apiVersion": "honua.io/v1alpha1",
  "kind": "Layer",
  "metadata": {
    "id": "01JABCDEF...",
    "name": "parcels",
    "namespace": "default",
    "labels": { "env": "prod" },
    "annotations": { "source": "admin-ui" },
    "resourceVersion": "42",
    "generation": 3,
    "createdAt": "2025-12-01T12:00:00Z",
    "updatedAt": "2025-12-05T10:30:00Z"
  },
  "spec": { },
  "status": { }
}
```

Principles:
- `spec` is desired state and is the only user-authored data.
- `status` is computed or observed state (validation, readiness, compiled ids).
- `resourceVersion` and ETags enable optimistic concurrency.
- `apiVersion` and `kind` are required for schema and compatibility control.

### Storage Model (Idealized)

Store resources in a document-first system with strong consistency, backed by
indexes for fast lookups:

- `metadata_resources`: canonical resource documents (JSONB or doc DB).
- `metadata_history`: immutable change log for audit and rollback.
- `metadata_indexes`: relational indexes for name and dependency lookups.
- `metadata_compiled`: derived artifacts for runtime use (read models).

This preserves a single source of truth while providing performant queries.

### Versioning and Compatibility

- Every resource carries `apiVersion` and optional `specVersion`.
- Server supports N-1 versions with up-conversion on read and validation on write.
- Contract changes are additive; breaking changes require a new `apiVersion`.
- Admin UI queries `/api/v1/admin/version` and `/api/v1/admin/capabilities` to
  warn on incompatibility.

### Change Pipeline and Compilation

Writes create an immutable event (outbox) and update the resource:
- A compiler service converts `spec` into runtime artifacts.
- Artifacts are stored in `metadata_compiled` with a version stamp.
- `status` references the compiled artifact version and readiness.

This separates author intent from runtime representation.

### GitOps Interface (Optional)

Expose a declarative manifest API without changing the source of truth:
- `GET /api/v1/admin/manifest` returns a full snapshot.
- `POST /api/v1/admin/manifest/apply` supports `dryRun` and `prune`.
- Secrets are referenced by name, never embedded.
- Store `last_applied_manifest_hash` for drift detection.

### Caching Alignment (Redis Optional)

Redis is a read-through cache for derived metadata and read models only:
- Cache keys include `apiVersion` and `resourceVersion`.
- Cache invalidation happens on resource updates.
- If Redis is unavailable, fall back to database reads.

## Consequences

### Positive
- Safe upgrades through versioned schemas and up-conversion.
- Clear separation of desired state and runtime artifacts.
- Optional GitOps without replacing the database.
- Cache strategy is safe across releases and deploys.
- Consistent Admin UI and API contracts.

### Negative
- Higher metadata complexity (envelope, versions, compiler).
- Requires a background compiler or synchronous compile path.
- Needs careful schema registry and migration discipline.
- Additional storage for history and compiled artifacts.

### Next Steps (Implementation)
- Define canonical resource kinds and schema registry format.
- Decide storage layout and migration strategy (resources, history, compiled).
- Implement resource CRUD with `resourceVersion` + ETag concurrency.
- Add compiler pipeline for derived artifacts with status updates.
- Add `/api/v1/admin/version` and `/api/v1/admin/capabilities` endpoints.
- Implement manifest export/apply (`dryRun`, `prune`) and drift detection.
- Align Redis cache keys with `apiVersion`/`resourceVersion`.
- Add tests: schema validation, up-conversion, manifest round-trip.

### Related ADRs
- `docs/adr/0017-redis-caching-with-fallback.md`
- `docs/adr/0021-redis-usage-and-hybridcache-deferral.md`
