# ADR-0034: Provider-Ready Data Source Binding

## Status
Accepted

## Context

Honua now has more than one feature-store provider path, but the runtime model
must stay centered on the server catalog rather than growing a separate
metadata repository. Multi-database support needs three pieces to be explicit:

1. A secure connection identifies the database/provider engine.
2. A layer identifies the physical storage object and key columns used at
   runtime.
3. Provider implementations advertise their capabilities instead of forcing
   every backend to pretend it supports the full PostGIS surface.

Without those boundaries, SQL Server, MySQL, DuckDB, and managed Postgres work
would each invent their own storage metadata and routing rules.

## Decision

Honua secure connections carry a canonical provider name such as `postgis`,
`postgresql`, `sqlserver`, `mysql`, or `duckdb`.

Layer definitions carry a provider-neutral `LayerStorageMapping` with physical
table/view name, optional schema/catalog/database qualifiers, primary key
column, geometry column, storage SRID, optional temporal column, and a small
provider option bag for cases where the neutral fields are not enough.

Feature-store providers implement a Core provider seam that reports read,
statistics, edit, and output capabilities. Runtime binding resolves:

```text
service -> secure connection -> provider engine
layer -> storage mapping
provider engine -> registered provider implementation
```

This model describes runtime storage binding only. It does not create or revive
a separate metadata repository subsystem.

## Consequences

### Positive

- Follow-on provider issues can depend on one shared connection/layer binding
  model.
- Read-only providers can be first-class by declaring unsupported edit/output
  paths instead of throwing from protocol adapters.
- PostGIS remains the reference provider and keeps its fast path.

### Negative

- The catalog schema has a few more storage binding columns.
- Provider implementations must maintain accurate capability declarations.

### Follow-On Work

- Route provider-backed query/edit calls through the provider binding resolver.
- Add SQL Server and MySQL provider implementations behind the same Core seam.
- Expand provider-specific health checks and schema discovery without moving
  ownership into a separate metadata store.
