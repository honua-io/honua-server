# ADR-0001: Raw Npgsql over ORM/Dapper

## Status
Accepted

## Context
The greenfield MVP needs a data access strategy for PostgreSQL/PostGIS. Options considered:
- Entity Framework Core
- Dapper
- Raw Npgsql (ADO.NET)

Key constraints:
- Native AOT compatibility required for fast cold starts
- PostGIS spatial operations need direct SQL control
- Minimal dependencies preferred

## Decision
Use raw Npgsql (ADO.NET) for all database access.

**Rejected alternatives:**
- **EF Core**: Heavy (~50 dependencies), reflection-based, poor AOT support, abstracts away PostGIS functions
- **Dapper**: Source generators exist but immature; adds dependency for minimal benefit over raw ADO.NET

## Consequences

### Positive
- Full AOT compatibility (Npgsql v8+ supports AOT)
- Direct control over SQL, critical for PostGIS spatial functions
- Zero ORM abstraction overhead
- Minimal dependency footprint
- Explicit query construction prevents N+1 and other ORM pitfalls

### Negative
- More verbose code (no automatic mapping)
- Manual parameter binding
- No change tracking (must implement if needed)
- Developers must write SQL

### Mitigation
- Use records and helper methods for common mapping patterns
- Centralize query building in dedicated classes
- Parameterized queries via NpgsqlParameter for safety
