# Architecture Decision Records

This folder contains Architecture Decision Records (ADRs) for the Honua greenfield MVP.

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [0001](0001-raw-npgsql-no-orm.md) | Raw Npgsql over ORM/Dapper | Accepted | 2025-12 |
| [0002](0002-maplibre-canonical-style.md) | MapLibre as Canonical Style Format | Accepted | 2025-12 |
| [0003](0003-odata-full-crud.md) | OData v4 Full CRUD in MVP | Accepted | 2025-12 |
| [0004](0004-proxy-rate-limiting.md) | Proxy-Based Rate Limiting | Accepted | 2025-12 |
| [0005](0005-dbup-migrations.md) | DbUp for Database Migrations | Accepted | 2025-12 |
| [0006](0006-openfreemap-default-basemap.md) | OpenFreeMap as Default Basemap | Accepted | 2025-12 |
| [0007](0007-embedded-maputnik.md) | Embedded Maputnik Style Editor | Accepted | 2025-12 |
| [0008](0008-env-var-configuration.md) | Environment Variables as Primary Config | Accepted | 2025-12 |
| [0009](0009-shared-filter-ast.md) | Shared Filter AST for Multi-Protocol Support | Accepted | 2025-12 |
| [0010](0010-admin-ui-architecture.md) | Admin UI Architecture (Blazor WASM) | Accepted | 2025-12 |
| [0011](0011-testing-strategy.md) | Testing Strategy and API Surface Coverage | Accepted | 2025-12 |
| [0012](0012-clean-architecture-implementation.md) | Clean Architecture Implementation | Accepted | 2025-12 |
| [0013](0013-minimal-apis-vs-controllers.md) | Minimal APIs vs Controllers Decision | Accepted | 2025-12 |
| [0014](0014-dependency-injection-limits.md) | Dependency Injection Limits Rationale | Accepted | 2025-12 |
| [0015](0015-vertical-slice-architecture.md) | Vertical Slice Architecture Pattern | Accepted | 2025-12 |
| [0016](0016-performance-optimization-strategies.md) | Performance Optimization Strategies | Accepted | 2025-12 |
| [0017](0017-redis-caching-with-fallback.md) | Redis Caching with Fallback Strategy | Accepted | 2025-12 |
| [0018](0018-source-generated-json-serialization.md) | Source-Generated JSON Serialization for AOT Compatibility | Accepted | 2025-12 |
| [0019](0019-security-first-file-upload-design.md) | Security-First File Upload Design | Accepted | 2025-12 |

## Template

```markdown
# ADR-NNNN: Title

## Status
Proposed | Accepted | Deprecated | Superseded

## Context
What is the issue that we're seeing that is motivating this decision?

## Decision
What is the change that we're proposing?

## Consequences
What becomes easier or more difficult because of this change?
```
