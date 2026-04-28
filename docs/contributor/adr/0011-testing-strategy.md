# ADR-0011: Testing Strategy and API Surface Coverage

## Status

Accepted. Extended by [ADR-0035: Unified CI Test Tier Strategy](0035-unified-ci-test-tier-strategy.md), which adds the `Tier=Fast|Integration|Slow` xUnit trait and defines the PR / merge-to-trunk / nightly execution schedule. This ADR continues to define **what** is tested (API-surface and operation coverage); ADR-0035 defines **when and where** each test runs.

## Context

Code coverage metrics (line/branch) don't guarantee that all API endpoints and operations are tested. A project could have 80% line coverage while missing tests for entire endpoints if other code paths are heavily tested.

For a multi-protocol feature server (GeoServices REST, OGC API Features, OData, MVT), we need confidence that:
1. Every implemented endpoint has integration tests
2. Every protocol operation pathway is exercised
3. Conformance to external specifications is verified

## Decision

### 1. API Surface Coverage Requirement

**Every implemented API endpoint must have at least one integration test.** This is enforced via architecture tests that:
- Scan for route registrations in `Program.cs` / endpoint modules
- Verify corresponding test classes exist with `[Protocol]` and `[Operation]` attributes
- Fail the build if any endpoint lacks test coverage

```csharp
// tests/dotnet/Honua.Architecture.Tests/ApiSurfaceCoverageTests.cs
[ArchitectureTest]
public void AllEndpoints_HaveIntegrationTests()
{
    // Get all registered endpoints from the application
    var endpoints = GetRegisteredEndpoints();

    // Get all tested endpoints from test attributes
    var testedEndpoints = GetTestedEndpoints();

    // Every endpoint must have at least one test
    foreach (var endpoint in endpoints)
    {
        testedEndpoints.Should().Contain(e =>
            e.Protocol == endpoint.Protocol &&
            e.Operation == endpoint.Operation,
            $"Endpoint {endpoint.Protocol}/{endpoint.Operation} has no integration tests");
    }
}
```

### 2. Protocol Coverage Matrix

Each protocol requires tests for all implemented operations:

| Protocol | Operations | Required Test Classes | Registry |
|----------|------------|----------------------|----------|
| **GeoServices REST** | Query, ApplyEdits, AddAttachment, DeleteAttachment, QueryAttachments, GenerateRenderer, QueryRelated | `FeatureServer/*Tests.cs` | `EndpointRegistry` |
| **OGC API Features** | Landing, Conformance, Collections, Collection, Items, Item, Create, Replace, Update, Delete | `OgcFeatures/*Tests.cs` | `EndpointRegistry` |
| **OData v4** | Query ($filter, $select, $expand, $orderby, $top, $skip), Batch | `OData/*Tests.cs` | `EndpointRegistry` |
| **MVT** | GetTile, TileJSON | `Tiles/*Tests.cs` | `EndpointRegistry` |
| **WFS 2.0** | GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue | `Wfs20/*Tests.cs` | `EndpointRegistry` + `OperationRegistry` |
| **gRPC** | QueryFeatures, QueryFeaturesStream, ApplyEdits | `Grpc/*Tests.cs` | `OperationRegistry` |
| **Admin** | CreateLayer, UpdateLayer, DeleteLayer, ListLayers, GetLayer | `Admin/*Tests.cs` | `EndpointRegistry` |

### 3. Parameter and Filter Combination Coverage

Beyond endpoint coverage, each endpoint must have tests for:

#### Query Parameters (per endpoint)

| Parameter Category | Required Test Coverage |
|--------------------|----------------------|
| **Spatial Filters** | bbox, geometry, spatialRel (intersects, contains, within, overlaps, crosses, touches) |
| **Attribute Filters** | where/filter with all operators (=, <>, <, >, <=, >=, LIKE, IN, BETWEEN, IS NULL) |
| **Logical Operators** | AND, OR, NOT, nested parentheses |
| **Output Control** | outFields/properties, returnGeometry, outSR/crs |
| **Pagination** | resultOffset/offset, resultRecordCount/limit, exceeds max |
| **Sorting** | orderByFields/$orderby (ASC, DESC, multiple fields) |
| **Geometry Options** | returnCentroid, returnExtentOnly, geometryPrecision |
| **Response Format** | f=json, f=geojson, f=pbf (where applicable) |

#### Filter Operator Matrix

Each filter implementation must have tests for:

```
Comparison:     =, <>, <, >, <=, >=
String:         LIKE, ILIKE (with %, _), STARTS_WITH, ENDS_WITH, CONTAINS
Null:           IS NULL, IS NOT NULL
Range:          BETWEEN, NOT BETWEEN
Set:            IN, NOT IN (strings, numbers, mixed)
Logical:        AND, OR, NOT
Grouping:       Nested parentheses ((a AND b) OR (c AND d))
Spatial:        ST_Intersects, ST_Contains, ST_Within, ST_DWithin
Temporal:       Date comparisons, DURING (OGC)
```

#### Test Matrix Example (Query Endpoint)

```csharp
[Protocol(Protocols.FeatureServer)]
[Operation(Operations.Query)]
public class QueryParameterTests
{
    // Spatial filter tests
    [Theory]
    [InlineData("intersects")]
    [InlineData("contains")]
    [InlineData("within")]
    [InlineData("crosses")]
    [InlineData("overlaps")]
    [InlineData("touches")]
    public async Task Query_SpatialRel_FiltersCorrectly(string spatialRel) { }

    // Attribute filter operator tests
    [Theory]
    [InlineData("population = 1000")]
    [InlineData("population <> 1000")]
    [InlineData("population > 1000")]
    [InlineData("population >= 1000")]
    [InlineData("population < 1000")]
    [InlineData("population <= 1000")]
    [InlineData("name LIKE 'San%'")]
    [InlineData("name IN ('A', 'B', 'C')")]
    [InlineData("population BETWEEN 100 AND 1000")]
    [InlineData("description IS NULL")]
    [InlineData("description IS NOT NULL")]
    public async Task Query_WhereOperator_FiltersCorrectly(string where) { }

    // Logical combination tests
    [Theory]
    [InlineData("a = 1 AND b = 2")]
    [InlineData("a = 1 OR b = 2")]
    [InlineData("NOT a = 1")]
    [InlineData("(a = 1 AND b = 2) OR c = 3")]
    [InlineData("a = 1 AND (b = 2 OR c = 3)")]
    public async Task Query_LogicalOperators_CombineCorrectly(string where) { }

    // Pagination boundary tests
    [Fact] public async Task Query_OffsetZero_ReturnsFromStart() { }
    [Fact] public async Task Query_OffsetBeyondResults_ReturnsEmpty() { }
    [Fact] public async Task Query_LimitExceedsMax_CapsAtMaxRecordCount() { }
    [Fact] public async Task Query_OffsetPlusLimit_PaginatesCorrectly() { }

    // Output format tests
    [Theory]
    [InlineData("json")]
    [InlineData("geojson")]
    [InlineData("pbf")]
    public async Task Query_OutputFormat_ReturnsCorrectContentType(string format) { }
}
```

### 4. Coverage Levels

| Level | Target | Enforcement |
|-------|--------|-------------|
| **API Surface (HTTP routes)** | 100% | Architecture test — `EndpointRegistry` (hard fail) |
| **Operation Coverage (WFS/gRPC)** | 100% | Architecture test — `OperationRegistry` (hard fail) |
| **Parameter Coverage** | All documented params | Code review checklist |
| **Filter Operators** | All supported operators | Theory tests required |
| **Line Coverage** | 80% | CI gate (hard fail) |
| **Branch Coverage** | 70% | CI gate (hard fail) |
| **Conformance** | Per spec | Nightly CI (report) |

**Note:** CI currently enforces a staged 40% line / 30% branch gate during Phase 0-1 to maintain velocity; the target remains 80%/70%.

### 6. Test Categories and Attributes

```csharp
// Required attributes for API tests
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]  // Which protocol
public class QueryEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]     // Which operation
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures() { }
}
```

The `[Endpoint]` attribute explicitly ties tests to route patterns, enabling the architecture test to verify coverage.

For WFS and gRPC operations, add `[InterfaceOperation]` alongside `[Endpoint]`:

```csharp
[Collection("Database")]
[Protocol(Protocols.Wfs20)]
public class Wfs20EndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(Protocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_GeoJsonOutput_ReturnsFeatureCollection() { }
}
```

The `[InterfaceOperation]` attribute maps to `OperationRegistry` entries rather than HTTP routes.

### 7. Conformance Test Requirements

External specification conformance tests are separate from functional tests:

```csharp
[Collection("Database")]
[Protocol(Protocols.OgcFeatures)]
[Conformance(Specs.OgcApiFeaturesPart1)]
public class OgcConformanceTests
{
    [IntegrationTest]
    [ConformanceRequirement("req/core/root-success")]
    public async Task LandingPage_ReturnsRequiredLinks() { }

    [IntegrationTest]
    [ConformanceRequirement("req/core/conformance-success")]
    public async Task Conformance_ListsDeclaredConformanceClasses() { }
}
```

### 8. Endpoint Registry for Validation

Endpoints must be registered in a discoverable way:

```csharp
// src/Honua.Server/Endpoints/EndpointRegistry.cs
public static class EndpointRegistry
{
    public static IReadOnlyList<EndpointDefinition> All { get; } =
    [
        // GeoServices REST
        new("GET", "/rest/services/{serviceId}/FeatureServer", Protocols.FeatureServer, Operations.Metadata),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}", Protocols.FeatureServer, Operations.Metadata),
        new("GET", "/rest/services/{serviceId}/FeatureServer/{layerId}/query", Protocols.FeatureServer, Operations.Query),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/query", Protocols.FeatureServer, Operations.Query),
        new("POST", "/rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits", Protocols.FeatureServer, Operations.Edit),

        // OGC API Features
        new("GET", "/ogc/features", Protocols.OgcFeatures, Operations.Landing),
        new("GET", "/ogc/features/conformance", Protocols.OgcFeatures, Operations.Conformance),
        new("GET", "/ogc/features/collections", Protocols.OgcFeatures, Operations.Collections),
        new("GET", "/ogc/features/collections/{collectionId}", Protocols.OgcFeatures, Operations.Collection),
        new("GET", "/ogc/features/collections/{collectionId}/items", Protocols.OgcFeatures, Operations.Query),
        new("GET", "/ogc/features/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.GetFeature),
        new("POST", "/ogc/features/collections/{collectionId}/items", Protocols.OgcFeatures, Operations.Create),
        new("PUT", "/ogc/features/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Replace),
        new("PATCH", "/ogc/features/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Update),
        new("DELETE", "/ogc/features/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Delete),

        // OData
        new("GET", "/odata/Features", Protocols.OData, Operations.Query),

        // MVT
        new("GET", "/tiles/{layerId}/{z}/{x}/{y}.mvt", Protocols.Tiles, Operations.GetTile),
        new("GET", "/tiles/{layerId}/tile.json", Protocols.Tiles, Operations.TileJSON),

        // Admin
        new("GET", "/api/v1/admin/connections/{id}/layers", Protocols.Admin, Operations.List),
        new("POST", "/api/v1/admin/connections/{id}/layers", Protocols.Admin, Operations.Create),
        new("PUT", "/api/v1/admin/connections/{id}/layers/{layerId}/enabled", Protocols.Admin, Operations.Update),
    ];
}
```

### 9. CI Integration

The execution-tier dispatch (which test runs on which event, and how shards are selected on PRs) is owned by [ADR-0035](0035-unified-ci-test-tier-strategy.md). The illustrative snippet below shows the coverage gate only; the actual `ci.yml` filters by `Tier=` and selects `server-tests` shards via `scripts/ci/honua-server-targeted-tests.sh`.

```yaml
# .github/workflows/ci.yml
test:
  steps:
    - name: Run All Tests
      run: dotnet test

    - name: API Surface Coverage
      run: dotnet test --filter "Category=Architecture" --logger "trx"
```

## Consequences

### Positive

- **Confidence**: Every API pathway has verified behavior
- **Regression Prevention**: Can't accidentally break untested endpoints
- **Documentation**: Test attributes serve as living documentation of API surface
- **Conformance Tracking**: Clear mapping from spec requirements to tests

### Negative

- **Initial Overhead**: Must create test stubs for all endpoints before implementation
- **Maintenance**: Endpoint registry must stay in sync with actual routes
- **Test Volume**: More tests to maintain (mitigated by good test infrastructure)

### Neutral

- Architecture tests add ~5 seconds to CI
- Requires discipline to add `[Endpoint]` attributes to new tests

## Implementation Notes

### HTTP Route Coverage vs Full Public-Interface Coverage

The project enforces two complementary coverage gates:

**`EndpointRegistry` — HTTP route coverage**

`EndpointRegistry` tracks every HTTP route deployed by the application (e.g. `GET /rest/services/{id}/FeatureServer/{layerId}/query`). The drift test (`EndpointRegistryDriftTests`) fails the build when a deployed route is missing from the registry, and `ApiSurfaceCoverageTests` fails when a registered route lacks an `[IntegrationTest]`-tagged test with a matching `[Endpoint]` attribute.

This gate works well for protocols where each operation maps to a unique HTTP route. However, two public surfaces escape it:

- **WFS 2.0** dispatches multiple operations (GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue) through a single `GET|POST /wfs` route based on the `REQUEST` query parameter.
- **gRPC** methods are mapped via `MapGrpcService` and carry gRPC-specific metadata alongside standard `HttpMethodMetadata` (POST). The HTTP drift test identifies them by their gRPC metadata and routes them to `OperationRegistry` coverage instead.

**`OperationRegistry` — public-interface operation coverage**

`OperationRegistry` extends the coverage policy to logical operations that are not fully represented by HTTP route metadata. Each entry is a `(Protocol, Operation)` pair — for example `("WFS-2.0", "GetCapabilities")` or `("Grpc", "geospatial.v1.FeatureService/QueryFeatures")`.

Tests covering these operations use the `[InterfaceOperation(protocol, operation)]` attribute alongside the standard `[IntegrationTest]` and `[Endpoint]` attributes. The architecture test `OperationCoverageTests` fails the build when a registered operation has no matching integration test. A companion drift test detects new gRPC methods deployed without registry entries.

**When to use which:**

| Scenario | Registry | Test attribute |
|----------|----------|----------------|
| New HTTP endpoint (REST, OGC, OData, Admin) | `EndpointRegistry` | `[Endpoint("METHOD /path")]` |
| New WFS 2.0 operation | `EndpointRegistry` (for `/wfs` route) + `OperationRegistry` | `[Endpoint]` + `[InterfaceOperation]` |
| New gRPC service method | `OperationRegistry` | `[Endpoint]` + `[InterfaceOperation]` |

### Phase-by-Phase Coverage Requirements

| Phase | Required Coverage |
|-------|-------------------|
| Phase 1 | GeoServices Query (100%), Health endpoints |
| Phase 2 | GeoServices ApplyEdits (100%), Phase 1 maintained |
| Phase 3 | OGC API Features Part 1 (100%), Phase 1-2 maintained |
| Phase 4 | OData Query (100%), Tiles (100%), Phase 1-3 maintained |
| Phase 5 | Admin API (100%), All protocols maintained |

### Test-First Development

When implementing a new HTTP endpoint:

1. Add endpoint to `EndpointRegistry`
2. Create test class with proper attributes
3. Write failing test for happy path
4. Implement endpoint
5. Add error case tests
6. Verify architecture test passes

When implementing a new WFS operation or gRPC method:

1. Add operation to `OperationRegistry`
2. For WFS: ensure the `/wfs` route is in `EndpointRegistry` (already tracked)
3. Create test with `[InterfaceOperation(protocol, operation)]` alongside `[Endpoint]`
4. Write failing test for happy path
5. Implement the operation/method
6. Verify both `OperationCoverageTests` and drift tests pass

This ensures no public interface ships without tests.
