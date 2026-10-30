# Honua Architecture Review Criteria

> **Enforcement status:** these criteria are enforced **locally** by `scripts/ci/local-architecture-review.py`, which is invoked from `scripts/ci/pre-pr-check.sh`. A GitHub Actions workflow that runs the same checks at PR time (`scripts/ci/architecture-review.py`) exists but is **not currently shipped** — there is no `.github/workflows/pr-architecture-review.yml`. Treat this doc as the rubric for the local pre-PR script and for human reviewers; CI enforcement is a future addition.

## 🚫 BLOCKING_ISSUES (Fail PR - Must Fix Before Merge)

### Critical Violations:
1. **Dependency Direction Violations**
   - `Honua.Core` depending on `Honua.Db.Postgres` or `Honua.Server`
   - `Honua.Db.Postgres` depending on `Honua.Server`
   - Detection: `using` statements, project references

2. **API Pattern Violations**
   - Controller classes inheriting from `ControllerBase`
   - Usage of `[ApiController]` attribute
   - Detection: Class inheritance, attribute usage

3. **AOT Compatibility Violations**
   - Reflection usage in hot paths without source generation
   - `Activator.CreateInstance` without generic constraints
   - Dynamic JSON serialization without source generation
   - Detection: Reflection APIs, dynamic patterns

4. **Security/Encapsulation Violations**
   - Public database repository types (`*Repository` classes)
   - Public data access types (`*DataAccess` classes)
   - Database connection strings in non-infrastructure layers

5. **Quality Gate Violations**
   - Public types without XML documentation (`/// <summary>`)
   - Missing integration tests for new endpoints
   - Detection: Missing XML docs on public APIs

## ⚠️ NEEDS_ATTENTION (Pass with Warnings - Review Required)

### Design Concerns:
1. **Organizational Issues**
   - Layer-based organization instead of vertical slices
   - Feature code scattered across multiple layers
   - Detection: File organization patterns

2. **Complexity Issues**
   - >5 dependencies in endpoint handlers
   - >4 dependencies in business logic
   - Deep inheritance hierarchies (>3 levels)
   - Detection: Constructor parameters, inheritance depth

3. **Performance Concerns**
   - Synchronous database operations in async context
   - Missing query optimization hints
   - Large object allocations in hot paths
   - Detection: Sync over async, allocation patterns

4. **Maintainability Issues**
   - God classes (>300 lines)
   - Methods with >10 parameters
   - Lack of composition over inheritance

## ✅ APPROVED (Pass - Good to Merge)

### Positive Indicators:
1. **Clean Architecture**
   - Proper dependency flow (Core <- Infrastructure)
   - Vertical slice organization
   - Single responsibility principle

2. **AOT Readiness**
   - Source-generated JSON serializers
   - Compile-time dependency injection
   - Value types for data transfer

3. **Quality Patterns**
   - Comprehensive XML documentation
   - Integration tests for public APIs
   - Proper error handling

## Implementation in Review Script

```python
def assess_overall_rating(analysis_text: str) -> str:
    """Determine overall assessment from analysis"""

    blocking_keywords = [
        "dependency violation", "controller inheritance",
        "reflection in hot path", "public repository",
        "missing xml documentation", "aot breaking"
    ]

    warning_keywords = [
        "layer organization", "too many dependencies",
        "inheritance hierarchy", "sync over async",
        "god class", "complex method"
    ]

    analysis_lower = analysis_text.lower()

    # Check for blocking issues
    if any(keyword in analysis_lower for keyword in blocking_keywords):
        return "BLOCKING_ISSUES"

    # Check for warning-level issues
    if any(keyword in analysis_lower for keyword in warning_keywords):
        return "NEEDS_ATTENTION"

    # Default to approved if no major issues
    return "APPROVED"
```

## GitHub Actions Integration

```yaml
# Optional: Block PR on BLOCKING_ISSUES
architecture-gate:
  name: Architecture Gate
  needs: llm-architecture-review
  runs-on: ubuntu-latest
  if: contains(github.event.pull_request.body, 'BLOCKING_ISSUES') || contains(steps.analysis.outputs.result, 'BLOCKING_ISSUES')
  steps:
    - name: Block PR on Critical Issues
      run: |
        echo "::error::Architecture review found blocking issues that must be resolved before merge."
        echo "::error::Please review the LLM Architecture Review comment and address critical violations."
        exit 1
```

## Usage Guidelines

1. **For Repository Owners**: Configure `OPENAI_API_KEY` in GitHub Secrets
2. **For Contributors**: Review LLM comments and address blocking issues
3. **For Reviewers**: Use LLM feedback as a starting point, not final authority
4. **For Architecture Evolution**: Update criteria as project matures
## Rules In Full

`AGENTS.md` carries the short form of these rules, under "Protocol Adapter Architecture" and
"Architecture Enforcement". This section holds the full text and the examples that used to live
there. Source comments cite rules by name ("DRY", "Security/RBAC", "intentionally diverges"), and
those names refer to the bullets below as well as to their short forms in `AGENTS.md`.

### Protocol Adapter Architecture

Every public API surface is a thin protocol adapter over shared canonical pipelines.
Protocol-specific code may do four things:

- parse requests;
- perform protocol-level validation;
- map to canonical request and response models;
- format protocol-compliant responses.

It must not reimplement query, edit, metadata, raster, process, security, caching, logging or
telemetry behaviour when shared infrastructure exists.

Canonical pipelines:

- **Query/read**: FeatureServer, OGC API Features, WFS, OData, STAC, MVT and gRPC query surfaces
  adapt into the shared query/filter/paging/CRS pipeline rather than building independent data
  access paths.
- **Edit/transaction**: these paths use the shared edit/transaction pipeline, so that validation,
  authorization, optimistic behaviour, errors and telemetry stay consistent:
  - FeatureServer edits;
  - OGC API Features mutations;
  - WFS Transaction;
  - OData CRUD;
  - gRPC edits;
  - admin mutations.
- **Metadata/capabilities**: these surfaces are generated from shared catalog, capability and
  metadata services wherever possible:
  - OGC landing, conformance, OpenAPI and capabilities documents;
  - WFS, WMS and WMTS capabilities;
  - GeoServices service and layer metadata;
  - OData `$metadata`;
  - STAC catalogs;
  - TileJSON;
  - MCP resources;
  - gRPC reflection and metadata.
- **Raster/render/tile/export**: MapServer, ImageServer, WMS/WMTS, OGC API Maps/Tiles, COG, static
  maps, MVT and export routes share the raster/render/tile/style/CRS/cache helpers instead of
  duplicating rendering or geodesy logic.
- **Process/job execution**: OGC API Processes, GeoServices GPServer, MCP tools and gRPC
  ProcessService adapt to the canonical process/job runtime. They do not create protocol-local
  job lifecycle semantics.
- **Format I/O**: readers and writers live in shared import/export/format services unless the
  behaviour is truly protocol-specific. This covers GeoJSON, Esri JSON, GML, KML, GeoPackage,
  GeoParquet, FlatGeobuf, shapefile, file geodatabase, WKT/WKB and CSV.

Adapter rules:

- **Where entrypoints live.** Most protocol families have their own project,
  `src/Honua.Protocols.*`:
  - `GeoServices`;
  - `OgcApi`, `OgcClassic` and `Ogc.Shared`;
  - `OData`;
  - `Stac`;
  - `SensorThings`;
  - `Scene`.

  The remaining raster and transport families live in `src/Honua.Server/Features/Protocols/`:
  `Grpc`, `Tiles`, `Cog`, `Rasters`, `Coverages`, `Zarr`, `Elevation`, `Terrain` and
  `SpatialAnalytics`. MCP lives in `src/Honua.Ai/Features/Protocols/Mcp/`.

  New protocol families get a `Honua.Protocols.<Family>` project.
- **What stays in the protocol slice.** Keep protocol DTOs and wire-format serializers there.
  Keep the following in `Honua.Core` or shared server infrastructure:
  - domain models and canonical requests;
  - query, edit, raster and process abstractions;
  - CRS math and filter parsers;
  - validation helpers;
  - reusable format readers and writers.
- **No cross-protocol dependencies.** One protocol never depends on another protocol's handler
  or endpoint implementation. To share behaviour, extract a neutral service or helper and have
  both protocols adapt to it. `Honua.Architecture.SourceTests/CrossProtocolIsolationTests.cs`
  enforces this.
- **Advertised surfaces match reality.** Capabilities, OpenAPI documents, conformance
  declarations and public metadata match runtime behaviour and the registered routes and
  operations.
- **Intentional divergence.** If a protocol intentionally diverges from the shared pipeline,
  document why in code and add direct endpoint-level tests that prove the divergence.

### Cross-Cutting Concerns

Cross-cutting behaviour is consistent across protocol families and is implemented once, in
shared infrastructure.

- **Exception handling**: map failures through the shared problem/error helpers. Never leak raw
  exception messages, SQL, stack traces, filesystem paths, connection strings or provider
  internals to clients.
- **Logging**: use structured, source-generated logging where practical. Include the stable
  identifiers needed for diagnosis (`serviceName`, `layerId`, `collectionId`, `jobId`,
  `operation`, `protocol`), and avoid duplicate or noisy logs.
- **Telemetry**: important paths need activities/spans: query, edit, metadata,
  raster/render/export, import, batch and job execution. Each span carries the protocol,
  operation, service/layer identifiers, result size or count, cache hit/miss and exception
  status where relevant.
- **Caching**:
  - Use the shared cache helpers. Vary cache keys on every behaviour-changing input:
    - auth and tenant;
    - service and layer;
    - operation;
    - query and filter;
    - CRS/SRID;
    - style;
    - format and content negotiation;
    - language;
    - host and scheme when links are emitted;
    - protocol preferences.
  - Exact response caching and generic query-result caching are opt-in and off by default
    (`Cache:ResponseCachingEnabled=false`). Redis metadata and catalog caching can stay enabled.
    Response caching stays off while the cache is process-local (distributed memory or the in-memory fallback) unless
    `Cache:ResponseCachingAllowInMemoryFallback` is set for a single server instance. Stored
    responses are limited by `Cache:ResponseCacheMaxEntryBytes` and `Cache:ResponseCacheMaxBytes`.
  - Do not add exact response caching for ad hoc spatial feature, query or render requests,
    such as:
    - an arbitrary `bbox`, geometry, distance or nearest query;
    - CQL2 spatial predicates;
    - OData `geo.*` filters;
    - static map or map export bboxes.

    Those keys are too high-cardinality to be useful in the baseline path.
  - Apply the ad hoc spatial response-cache guard after the protocol parameters have been
    translated into the canonical `FeatureQuery`.
  - Cache tile-matrix and cache-hinted paths instead. Those requests snap to finite
    gridset/tile/style/format keys and can be seeded, metatiled, expired and quota-managed like
    GeoWebCache.
  - For small-node and serverless profiles, control pressure with bounded DB admission and pool
    sizing rather than with exact spatial response caches.
- **Spatial bbox semantics**: protocol `bbox` and viewport envelope requests are windowing or
  display paths. They may use envelope-only point predicates when the adapter explicitly marks
  them so. Explicit spatial predicates keep exact spatial relationships unless the protocol
  itself asks for envelope-intersects semantics. These include `Intersects`, `Contains`,
  distance/nearest, CQL2 spatial functions and non-envelope geometries.
- **Security/RBAC**: authorization is enforced in the shared pipeline or a common policy
  service, never by caller discipline. Check service, layer, field, task/job, import/export and
  mutation permissions consistently across equivalent protocols.
- **Validation**: route, query and body validation uses the shared validators and protocol
  adapters. Before a request reaches provider code, validate:
  - identifiers, formats and CRS;
  - filters and geometry;
  - paging limits and output formats;
  - URLs, file paths, headers and upload sizes.
- **Performance**: avoid sync-over-async, repeated parsing or serialization, per-feature catalog
  lookups, unbounded buffering and duplicated expensive computation in protocol code. Use
  streaming, paging and the shared format readers where they are available.
- **DRY/shared reuse**: duplicated protocol helpers are acceptable only for wire-format
  differences. If duplicated logic affects behaviour, error mapping, auth, caching, telemetry,
  CRS, filters, rendering or data access, extract a shared helper before adding more protocol
  code.

### Dependencies On Other Honua Repositories

- **Protos.** Canonical `.proto` definitions live in `geospatial-grpc`. honua-server consumes the
  generated bindings through the published `Geospatial.Grpc` package. Make new services, fields,
  enum values and other wire-contract changes in `geospatial-grpc` first, then consume them here
  by bumping the package version. Do not reintroduce a local proto source-of-truth tree under
  `src/Honua.Core/`.
- **SDKs.** When server-side tools or tests need .NET SDK client behaviour, consume `Honua.Sdk.*`
  as published, versioned NuGet packages. Do not copy SDK source into this repo. Avoid
  long-lived sibling `ProjectReference` links to `honua-sdk-dotnet`; a temporary local reference
  needs an explicit removal issue.

### Quality Standards

- **Warnings as errors**: every build passes with `TreatWarningsAsErrors=true`.
- **API surface coverage**: 100%. Every endpoint has an integration test. The architecture
  tests (`EndpointRegistry`, `OperationRegistry`) enforce this as a hard failure.
- **Line and branch coverage**: the targets are 80% line and 70% branch coverage. CI collects
  coverage (foundation lanes, uploaded to Codecov with `fail_ci_if_error: false`) but does not
  gate on a percentage.
- **AOT compatibility**: no reflection in hot paths; use source-generated JSON and logging.
- **Dependency limits**: at most 5 dependencies per endpoint and 4 per handler.
- **Formatting**: run `dotnet format Honua.sln` before opening a PR.

### Documentation Examples Use Product Surfaces

Documentation examples use Honua's own supported surfaces: the Honua CLI, the published SDKs, or
MCP. Never use a raw HTTP shell command as a substitute for a product workflow.

Sometimes a page's subject is the wire protocol itself and raw HTTP is unavoidable. In that case:

1. justify the exception in the PR body;
2. add the page to `scripts/ci/doc-wire-reference-allowlist.v1.json`;
3. mark each affected example with `<!-- wire-reference -->`.

The exception documents the wire. It must not become the recommended operator or application
path.

### Legacy Code Reference Policy

`../Honua.Server/` is an archived legacy project. It is reference documentation only, not an
active repo or implementation source.

Do:

- read legacy code to understand behaviour and edge cases;
- reference legacy file paths when documenting ported behaviour;
- use legacy tests as a specification of expected outcomes.

Do not:

- copy-paste code from it;
- port the patterns that led to the 22-dependency controller problem;
- bring over unused features, dead code or tech debt;
- assume the legacy implementation is correct without verifying it.

When porting behaviour, cite the source:

```csharp
// Behavior reference: ../Honua.Server/src/platform/core/Query/Filter/CqlFilterParser.cs
// Handles nested parentheses in CQL expressions
```

### Enforcement Examples

#### BLOCKING: dependency direction

```csharp
// VIOLATION: Core depending on infrastructure (file under src/Honua.Core/)
using Honua.Db.Postgres;   // BLOCKING: Core cannot depend on a provider
using Honua.Server;        // BLOCKING: Core cannot depend on Server

// CORRECT: a provider depending on Core (file under src/Honua.Db/Postgres/)
using Honua.Core.Features.Abstractions;
```

The dependency flow is `Honua.Core` <- `Honua.Db.*` providers <- `Honua.Server`. The providers
are Postgres, Postgres.Shared, DuckDB, MySql, SqlServer, Oracle, Databricks and Redshift. Their
project files are `src/Honua.Db/<Provider>/Honua.<Provider>.csproj`, and their namespaces are
`Honua.Db.<Provider>`.

- Core defines the abstractions and domain models.
- The providers implement the Core interfaces.
- The server uses Core plus the active provider, selected by `DataSource:Provider`. Both `mysql`
  and `mariadb` resolve to the MySql provider.

#### BLOCKING: controllers

```csharp
// VIOLATION: controller usage (legacy pattern)
public class FeaturesController : ControllerBase { }   // BLOCKING: no controllers

// CORRECT: Minimal API endpoint mapping
// (see src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerEndpoints.cs)
public static void MapFeatureServerEndpoints(this WebApplication app)
{
    app.MapGet("/rest/services/{id}/FeatureServer/{layerId}/query",
        async (int id, int layerId, IFeatureReader reader) => { });
}
```

#### BLOCKING: encapsulation and XML docs

```csharp
public class FeatureRepository { }     // BLOCKING: implementation types are internal
internal class FeatureRepository { }   // OK
public interface IFeatureReader { }    // OK: abstractions can be public

public class LayerDefinition { }       // BLOCKING: public type without /// docs

/// <summary>
/// Represents a geospatial layer definition with metadata and spatial reference.
/// </summary>
public class LayerDefinition { }       // OK
```

#### WARNING: organisation, complexity, performance

Organise code in vertical feature slices, not in layer-based folders (`Controllers/`,
`Services/`, `Models/`, `Repositories/`):

```text
src/Honua.Protocols.GeoServices/FeatureServer/
├── FeatureServerEndpoints.cs      // API endpoints
├── FeatureServerEditsHandler.cs   // adapter to the canonical edit pipeline
└── AttachmentEndpoints.cs         // one slice per feature, not per layer
```

```csharp
// WARNING: more than 4 handler dependencies (the endpoint limit is 5)
public class QueryHandler(IFeatureReader reader, ILayerCatalog catalog, ILogger<QueryHandler> log,
    IValidator validator, IMetrics metrics, IEventBus events) { }

// WARNING: sync-over-async; use await
var result = asyncOperation.Result;
asyncOperation.Wait();

// WARNING: inheritance deeper than 3 levels; prefer composition
```

#### Review checklist

1. Scan `using` statements for dependency-direction violations.
2. Look for `ControllerBase` inheritance.
3. Check for public class declarations in infrastructure projects; they should be internal.
4. Verify XML documentation on every public type (`///`).
5. Count constructor parameters: endpoints <= 5, handlers <= 4.
6. Search for `.Result` or `.Wait()`.
7. Verify vertical-slice organisation.
8. Verify that protocol endpoints adapt to the shared query, edit, metadata, raster and process
   pipelines.
9. Verify that exception handling, logging, telemetry, caching, security/RBAC and validation use
   shared infrastructure.

Severity:

- **BLOCKING**: dependency violations, controller usage, public infrastructure types, missing
  docs.
- **WARNING**: organisational issues, complexity, performance anti-patterns.
- **APPROVED**: clean dependencies, vertical slices, proper tests, good documentation.

### Deferred Refactors

`src/Honua.Server/Features/Infrastructure/` aggregates many cross-cutting concerns, including
Middleware, Logging, Monitoring, RateLimiting, Resilience, Security and MultiTenancy. The #1144 audit recommended
promoting clearly bounded subfolders into dedicated feature folders. That move is deferred: it
needs a dedicated PR with namespace updates. `Styling/` has since moved to
`src/Honua.Server/Features/Styling/`.
