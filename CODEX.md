# Honua Server - Project Instructions

## Project Overview

Honua Server is a greenfield implementation of a geospatial feature server supporting multiple protocols (GeoServices REST, OGC API Features, OData v4, MVT). This is a **clean rewrite** — the legacy codebase exists as reference only.

## Critical Rules

### Legacy Code Reference Policy

There is a legacy project at `../Honua.Server/` which serves as **reference documentation only**.

**DO:**
- Read legacy code to understand behavior and edge cases
- Reference file paths when documenting ported behavior
- Use legacy tests as specification for expected outcomes

**DO NOT:**
- Copy-paste code from legacy project
- Port patterns that led to the 22-dependency controller problem
- Bring over unused features, dead code, or tech debt
- Assume legacy implementation is correct without verification

When porting behavior, document the source:
```csharp
// Behavior reference: ../Honua.Server/src/platform/core/Query/Filter/CqlFilterParser.cs
// Handles nested parentheses in CQL expressions
```

### Quality Standards

- **Warnings as errors**: All builds must pass with `TreatWarningsAsErrors=true`
- **Coverage gates**: 80%+ line coverage, 70%+ branch coverage
- **AOT compatibility**: No reflection in hot paths, source-generated JSON/logging
- **Dependency limits**: Max 5 dependencies per endpoint, max 4 per handler

### Test-Driven Development

1. Write failing integration test first (Testcontainers + PostGIS)
2. Implement minimum code to pass
3. Refactor with confidence
4. Verify phase coverage checkpoint met

### Architecture

- **Vertical slices**: Organize by feature, not layer
- **Composition over inheritance**: Small focused classes
- **Integration-first testing**: Real database in tests, minimal mocking

## Phase-Based Development

See `docs/MVP_PLAN.md` for detailed phase breakdown. Current focus should always align with the active phase. Do not implement features from future phases.

## Commit Guidelines

- Conventional commits: `feat:`, `fix:`, `test:`, `docs:`, `refactor:`, `ci:`
- Reference GitHub issue: `feat: add query endpoint (#12)`
- Keep commits atomic and focused

## File Organization

```
src/
├── Honua.Server/          # Main host (Minimal APIs)
├── Honua.Core/            # Domain models, abstractions
├── Honua.Postgres/        # PostgreSQL implementation
└── Honua.Admin/           # Blazor WASM admin UI

tests/
├── Honua.TestKit/         # Shared test infrastructure
├── Honua.Core.Tests/      # Unit tests
├── Honua.Server.Tests/    # Integration tests
└── Honua.Architecture.Tests/  # Architecture enforcement
```
