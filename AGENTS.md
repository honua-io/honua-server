**NEVER ADD CLAUDE CODE ATTRIBUTION TO ANY COMMITS - DO NOT INCLUDE "Generated with Claude Code" OR CO-AUTHORED-BY LINES**

# Honua Server - Project Instructions

## Project Overview

Honua Server is a greenfield implementation of a geospatial server that exposes one shared geospatial capability set through multiple protocol adapters: GeoServices REST, OGC API, classic OGC services (WFS/WMS/WMTS), OData v4, STAC, MVT/TileJSON, COG/raster routes, MCP, and gRPC. This is a **clean rewrite** — the legacy codebase exists as reference only.

## MVP Deferrals (Operational Simplicity)

The MVP intentionally defers enterprise/operational features to reduce complexity:
- No app-level rate limiting; enforce at the edge (nginx/ALB/WAF).
- No secure-connection allowlist or connection audit trail; secure connections are encrypted or secret references only.
- No security compliance framework, audit log storage, or compliance dashboards/monitoring.

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
- **Coverage gates**: Target 80%+ line coverage, 70%+ branch coverage; CI enforces staged thresholds (40%/30%) during Phase 0-1
- **API surface coverage**: 100% - every endpoint must have integration tests
- **AOT compatibility**: No reflection in hot paths, source-generated JSON/logging
- **Dependency limits**: Max 5 dependencies per endpoint, max 4 per handler
- **Code formatting**: Always run `dotnet format Honua.sln` before creating PRs to prevent CI failures

### Protocol Adapter Architecture

Every public API surface must be a thin protocol adapter over shared canonical pipelines. Protocol-specific code may parse requests, perform protocol-level validation, map to canonical request/response models, and format protocol-compliant responses; it must not reimplement query, edit, metadata, raster, process, security, caching, logging, or telemetry behavior when shared infrastructure exists.

Canonical pipelines:
- **Query/read**: FeatureServer, OGC API Features, WFS, OData, STAC, MVT, and gRPC query surfaces must adapt into the shared query/filter/paging/CRS pipeline rather than building independent data access paths.
- **Edit/transaction**: FeatureServer edits, OGC API Features mutations, WFS Transaction, OData CRUD, gRPC edits, and admin mutation paths must use the shared edit/transaction pipeline so validation, authorization, optimistic behavior, errors, and telemetry stay consistent.
- **Metadata/capabilities**: OGC landing/conformance/OpenAPI/capabilities, WFS/WMS/WMTS capabilities, GeoServices service/layer metadata, OData `$metadata`, STAC catalogs, TileJSON, MCP resources, and gRPC reflection/metadata surfaces must be generated from shared catalog/capability/metadata services wherever possible.
- **Raster/render/tile/export**: MapServer, ImageServer, WMS/WMTS, OGC API Maps/Tiles, COG, static maps, MVT, and export routes must share raster/render/tile/style/CRS/cache helpers instead of duplicating rendering or geodesy logic.
- **Process/job execution**: OGC API Processes, GeoServices GPServer, MCP tools, and gRPC ProcessService must adapt to the canonical process/job runtime and not create protocol-local job lifecycle semantics.
- **Format I/O**: GeoJSON, Esri JSON, GML, KML, GeoPackage, GeoParquet, FlatGeobuf, shapefile, file geodatabase, WKT/WKB, and CSV readers/writers must live in shared import/export/format services unless the behavior is truly protocol-specific.

Adapter rules:
- Put protocol entrypoints under `src/Honua.Server/Features/Protocols/` (`Ogc/Api`, `Ogc/Classic`, `GeoServices`, `OData`, `Stac`, `Tiles`, `Cog`, `Mcp`, `Grpc`, etc.).
- Keep protocol DTOs and wire-format serializers in the protocol slice, but keep domain models, canonical requests, query/edit/raster/process abstractions, CRS math, filter parsers, validation helpers, and reusable format readers/writers in `Honua.Core` or shared server infrastructure.
- Do not let one protocol depend on another protocol's handler or endpoint implementation. Share behavior by extracting a neutral service/helper, then have both protocols adapt to it.
- Capabilities, OpenAPI documents, conformance declarations, and public metadata must match runtime behavior and registered routes/operations.
- If a protocol intentionally diverges from the shared pipeline, document why in code and add direct endpoint-level tests proving the divergence.

### Cross-Cutting Concerns

Cross-cutting behavior must be consistent across protocol families and should be implemented once in shared infrastructure.

- **Exception handling**: Map failures through shared problem/error helpers. Do not leak raw exception messages, SQL, stack traces, filesystem paths, connection strings, or provider internals to clients.
- **Logging**: Use structured, source-generated logging where practical. Include stable identifiers needed for diagnosis (`serviceName`, `layerId`, `collectionId`, `jobId`, `operation`, `protocol`) and avoid duplicate/noisy logs.
- **Telemetry**: Important query, edit, metadata, raster/render/export, import, batch, and job execution paths need activities/spans with protocol, operation, service/layer identifiers, result size/count, cache hit/miss, and exception status where relevant.
- **Caching**: Use shared cache helpers and vary cache keys by every behavior-changing input: auth/tenant, service/layer, operation, query/filter, CRS/SRID, bbox/geometry, style, format/content negotiation, language, host/scheme when links are emitted, and protocol preferences.
- **Security/RBAC**: Authorization must be enforced in the shared pipeline or a common policy service, not by caller discipline. Check service, layer, field, task/job, import/export, and mutation permissions consistently across equivalent protocols.
- **Validation**: Route/query/body validation must use shared validators and protocol adapters. Validate identifiers, formats, CRS, filters, geometry, paging limits, output formats, URLs, file paths, headers, and upload sizes before reaching provider code.
- **Performance**: Avoid sync-over-async, repeated parsing/serialization, per-feature catalog lookups, unbounded buffering, and protocol-local duplicate expensive computation. Use streaming/paging/shared format readers where available.
- **DRY/shared reuse**: Duplicated protocol helpers are only acceptable for wire-format differences. If duplicate logic affects behavior, error mapping, auth, caching, telemetry, CRS, filters, rendering, or data access, extract a shared helper before adding more protocol code.

### MCP Code Search

- Prefer the MCP code-search tools before broad file reads when working in `honua-server`.
- Use `ast-grep-code-search` for structural pattern search, narrow path/glob search, and low-token code matching.
- Use `tree-sitter-code-search` for symbol lookup, targeted file slices, dependency/symbol inspection, and AST-aware context.
- On first use of `tree-sitter-code-search` in a worktree, register the current worktree path as a project using a unique name derived from the worktree directory.
- Keep retrieval tight: ask for symbols, line ranges, or narrow matches first. Do not dump large files into context unless the smaller retrieval path failed.
- For review and fix work, prefer changed files, call sites, handlers, tests, and directly related symbols before expanding to surrounding modules.

### Development Artifacts Cleanup

**AI agents must clean up development artifacts before committing:**

- **Planning documents**: Delete temporary analysis files like `ARCHITECTURE_IMPROVEMENTS.md`, `IMPLEMENTATION_PLAN.md`, etc.
- **Research notes**: Remove exploration files created during investigation
- **Draft specifications**: Delete incomplete or superseded documentation drafts
- **Development scratch files**: Remove any temporary files created for planning or testing approaches

**Why this matters**: Development artifacts confuse future AI interactions and create misleading "authoritative" documentation that contradicts the actual source of truth. Always integrate findings directly into the canonical files (AGENTS.md, ADRs, etc.) and delete the artifacts.

**Rule**: If you create temporary documentation during development, either integrate it into canonical docs or delete it before committing.

### Test-Driven Development

1. Write failing integration test first (Testcontainers + PostGIS)
2. Implement minimum code to pass
3. Refactor with confidence
4. Verify phase coverage checkpoint met

### Testing Requirements (ADR-0011)

**API Surface Coverage**: Every implemented endpoint requires at least one integration test. This is enforced by architecture tests.

**Test Attributes**: Use protocol, operation, and endpoint attributes for discoverability:
```csharp
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures() { }
}
```

For logical operations that are not fully represented by route metadata, also add `[InterfaceOperation]` to map to `OperationRegistry` (for example WFS/WMS dispatch operations, OData option/payload operation families, and gRPC methods):
```csharp
[Collection("Database")]
[Protocol(Protocols.Wfs20)]
public class Wfs20EndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_ReturnsFeatureCollection() { }
}
```

**Coverage Levels**:
| Level | Target | Enforcement |
|-------|--------|-------------|
| API Surface (HTTP routes) | 100% | Architecture test — `EndpointRegistry` (hard fail) |
| Operation Coverage (WFS/WMS/OData/gRPC) | 100% | Architecture test — `OperationRegistry` (hard fail) |
| Line Coverage | 80% | CI gate (hard fail) |
| Branch Coverage | 70% | CI gate (hard fail) |

**Test Naming**: `MethodUnderTest_Scenario_ExpectedBehavior`
```csharp
Query_WithWhereClause_ReturnsFilteredFeatures()
Query_InvalidSyntax_Returns400WithErrorDetails()
```

**Scale Tests (Multi-Node + Redis)**:
- Start the scale stack: `docker compose -f docker-compose.scale-test.yml up --build --scale honua=3`
- Set env vars (inside the devcontainer): `HONUA_SCALE_TEST_BASE_URL=http://localhost:8080`, `HONUA_SCALE_TEST_REDIS=localhost:6379`, `HONUA_SCALE_TEST_ADMIN_API_KEY=scale-test-admin-password`
- Set `HONUA_SCALE_TEST_SERVICE_ID=<service-name>` to run replica-state scale tests (create/extract/sync/unregister).
- Optional host-port overrides when defaults are busy: `HONUA_SCALE_TEST_HTTP_PORT=18080`, `HONUA_SCALE_TEST_REDIS_PORT=6380`, `HONUA_SCALE_TEST_POSTGRES_PORT=55434`
- Run scale tests only: `dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --filter Category=Scale`
- Scale tests expect `docker/nginx/scale-test.conf` to emit `X-Instance-ID` for `/rest/`, `/ogc/`, and `/odata/`

### Architecture Enforcement

#### BLOCKING VIOLATIONS (must fix before merge)

**1. Dependency direction violations**
```csharp
// VIOLATION: Core depending on Infrastructure
// File: src/Honua.Core/SomeFile.cs
using Honua.Postgres;        // BLOCKING - Core cannot depend on Infrastructure
using Honua.Server;          // BLOCKING - Core cannot depend on Server

// CORRECT: Infrastructure depending on Core
// File: src/Honua.Postgres/SomeFile.cs
using Honua.Core.Features.Abstractions;  // OK - Infrastructure can use Core abstractions
```

Dependency flow rule: `Honua.Core` <- `Honua.Postgres` / `Honua.DuckDB` <- `Honua.Server`
- Core defines abstractions and domain models
- Postgres and DuckDB implement Core interfaces
- Server uses Core plus the active provider (selected via `DataSource:Provider`)

**2. API pattern violations**
```csharp
// VIOLATION: Controller usage (legacy pattern)
public class FeaturesController : ControllerBase  // BLOCKING - No controllers allowed
{
    // Controllers create 22-dependency anti-pattern
}

// CORRECT: Minimal API pattern
// File: src/Honua.Server/Features/Protocols/GeoServices/FeatureServer/FeatureServerEndpoints.cs
public static void MapFeatureServerEndpoints(this WebApplication app)
{
    app.MapGet("/rest/services/{id}/FeatureServer/{layerId}/query",
        async (int id, int layerId, IFeatureReader reader) => { });
}
```

**3. Encapsulation violations**
```csharp
// VIOLATION: Public infrastructure types (security risk)
public class FeatureRepository { }        // BLOCKING - Should be internal
public class PostgresConnection { }       // BLOCKING - Should be internal

// CORRECT: Proper encapsulation
internal class FeatureRepository { }      // OK - Implementation details are internal
public interface IFeatureReader { }       // OK - Abstractions can be public
```

**4. Missing documentation**
```csharp
// VIOLATION: Public type without XML docs
public class LayerDefinition  // BLOCKING - Missing /// documentation
{
}

// CORRECT: Documented public API
/// <summary>
/// Represents a geospatial layer definition with metadata and spatial reference.
/// </summary>
public class LayerDefinition
{
}
```
All public types must have XML documentation comments.

#### WARNING VIOLATIONS (review recommended)

**1. Organizational anti-patterns**
```csharp
// WARNING: Layer-based organization (should be vertical slices)
src/
├── Controllers/           // Layer-based anti-pattern
├── Services/
├── Models/
└── Repositories/

// PREFERRED: Vertical slice organization
src/Honua.Server/Features/
├── Protocols/
│   ├── GeoServices/FeatureServer/
│   │   ├── FeatureServerEndpoints.cs
│   │   ├── FeatureServerQueryHandler.cs
│   │   └── Models/
│   └── Ogc/Api/Features/
│       ├── OgcFeaturesEndpoints.cs
│       └── Services/
└── Admin/
    ├── AdminEndpoints.cs
    └── Services/
```

**2. Complexity violations**
```csharp
// WARNING: Too many dependencies (endpoint limit: 5, handler limit: 4)
public class QueryHandler(
    IFeatureReader reader,      // 1
    ILayerCatalog catalog,      // 2
    ILogger<QueryHandler> log,  // 3
    IValidator validator,       // 4
    IMetrics metrics,          // 5 - At limit, consider refactoring if adding more
    IEventBus events)          // 6 - WARNING: Exceeds limit
```

**3. Performance anti-patterns**
```csharp
// WARNING: Sync-over-async (performance issue)
var result = asyncOperation.Result;      // Use await instead
asyncOperation.Wait();                   // Use await instead

// WARNING: Deep inheritance (composition preferred)
class A : B : C : D { }  // WARNING: >3 levels, consider composition
```

#### POSITIVE PATTERNS TO REINFORCE

**1. Clean dependency flow**
```csharp
// GOOD: Proper dependency direction
// Honua.Core defines interface
public interface IFeatureReader { }

// Honua.Postgres implements interface
internal class PostgresFeatureStore : IFeatureReader { }

// Honua.Server uses interface
public static async Task<IResult> QueryFeatures(IFeatureReader reader) { }
```

**2. Vertical slice organization**
```csharp
// GOOD: Feature cohesion
Features/Protocols/GeoServices/FeatureServer/
├── FeatureServerEndpoints.cs    // API endpoints
├── FeatureServerQueryHandler.cs // Adapter-to-canonical query behavior
├── Models/                      // Protocol DTOs
└── Services/                    // Supporting services
    └── QueryFormatters.cs
```

**3. Proper testing structure**
```csharp
// GOOD: Comprehensive test coverage with proper attributes
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        // Test implementation
    }
}
```

#### Architecture review checklist

For AI reviews - check these patterns:
1. Scan `using` statements for dependency direction violations
2. Look for `ControllerBase` inheritance (forbidden pattern)
3. Check public class declarations in infrastructure projects (should be internal)
4. Verify XML documentation on all public types (`///` comments)
5. Count constructor parameters (endpoints <= 5, handlers <= 4)
6. Search for `.Result` or `.Wait()` (sync-over-async anti-pattern)
7. Verify file organization follows vertical slice pattern
8. Verify protocol endpoints adapt to shared query/edit/metadata/raster/process pipelines
9. Verify exception handling, logging, telemetry, caching, security/RBAC, and validation use shared infrastructure

Severity assessment:
- BLOCKING: Dependency violations, controller usage, public infrastructure types, missing docs
- WARNING: Organizational issues, complexity violations, performance anti-patterns
- APPROVED: Clean dependencies, vertical slices, proper testing, good documentation

## Phase-Based Development

Planning and phase tracking live in GitHub issues and PRs. Do not implement features that are not actively scoped.

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
└── Honua.DuckDB/          # DuckDB read-only provider

tests/
├── Honua.TestKit/         # Shared test infrastructure
├── Honua.Core.Tests/      # Unit tests
├── Honua.Server.Tests/    # Integration tests
├── Honua.DuckDB.Tests/    # DuckDB provider tests
└── Honua.Architecture.Tests/  # Architecture enforcement
```
