# ADR-0011: Testing Strategy and API Surface Coverage

## Status

Accepted

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
// tests/Honua.Architecture.Tests/ApiSurfaceCoverageTests.cs
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

| Protocol | Operations | Required Test Classes |
|----------|------------|----------------------|
| **GeoServices REST** | Query, ApplyEdits, AddAttachment, DeleteAttachment, QueryAttachments, GenerateRenderer, QueryRelated | `FeatureServer/*Tests.cs` |
| **OGC API Features** | Landing, Conformance, Collections, Collection, Items, Item, Create, Replace, Update, Delete | `OgcFeatures/*Tests.cs` |
| **OData v4** | Query ($filter, $select, $expand, $orderby, $top, $skip), Batch | `OData/*Tests.cs` |
| **MVT** | GetTile, TileJSON | `Tiles/*Tests.cs` |
| **Admin** | CreateLayer, UpdateLayer, DeleteLayer, ListLayers, GetLayer | `Admin/*Tests.cs` |

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
| **API Surface** | 100% | Architecture test (hard fail) |
| **Parameter Coverage** | All documented params | Code review checklist |
| **Filter Operators** | All supported operators | Theory tests required |
| **Line Coverage** | 80% | CI gate (hard fail) |
| **Branch Coverage** | 70% | CI gate (hard fail) |
| **Conformance** | Per spec | Nightly CI (report) |

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
        new("GET", "/ogc", Protocols.OgcFeatures, Operations.Landing),
        new("GET", "/ogc/conformance", Protocols.OgcFeatures, Operations.Conformance),
        new("GET", "/ogc/collections", Protocols.OgcFeatures, Operations.Collections),
        new("GET", "/ogc/collections/{collectionId}", Protocols.OgcFeatures, Operations.Collection),
        new("GET", "/ogc/collections/{collectionId}/items", Protocols.OgcFeatures, Operations.Query),
        new("GET", "/ogc/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.GetFeature),
        new("POST", "/ogc/collections/{collectionId}/items", Protocols.OgcFeatures, Operations.Create),
        new("PUT", "/ogc/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Replace),
        new("PATCH", "/ogc/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Update),
        new("DELETE", "/ogc/collections/{collectionId}/items/{featureId}", Protocols.OgcFeatures, Operations.Delete),

        // OData
        new("GET", "/odata/{collectionId}", Protocols.OData, Operations.Query),

        // MVT
        new("GET", "/tiles/{collectionId}/{z}/{x}/{y}.mvt", Protocols.Tiles, Operations.GetTile),
        new("GET", "/tiles/{collectionId}.json", Protocols.Tiles, Operations.TileJSON),

        // Admin
        new("GET", "/admin/api/layers", Protocols.Admin, Operations.List),
        new("GET", "/admin/api/layers/{layerId}", Protocols.Admin, Operations.Get),
        new("POST", "/admin/api/layers", Protocols.Admin, Operations.Create),
        new("PUT", "/admin/api/layers/{layerId}", Protocols.Admin, Operations.Update),
        new("DELETE", "/admin/api/layers/{layerId}", Protocols.Admin, Operations.Delete),
    ];
}
```

### 9. CI Integration

```yaml
# .github/workflows/ci.yml
test:
  steps:
    - name: Run All Tests
      run: dotnet test --collect:"XPlat Code Coverage"

    - name: API Surface Coverage
      run: dotnet test --filter "Category=Architecture" --logger "trx"

    - name: Line/Branch Coverage Gate
      run: |
        reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage
        LINE=$(grep -oP 'Line coverage: \K[\d.]+' coverage/Summary.txt)
        BRANCH=$(grep -oP 'Branch coverage: \K[\d.]+' coverage/Summary.txt)

        if (( $(echo "$LINE < 80" | bc -l) )); then
          echo "::error::Line coverage ${LINE}% below 80%"
          exit 1
        fi

        if (( $(echo "$BRANCH < 70" | bc -l) )); then
          echo "::error::Branch coverage ${BRANCH}% below 70%"
          exit 1
        fi
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

### Phase-by-Phase Coverage Requirements

| Phase | Required Coverage |
|-------|-------------------|
| Phase 1 | GeoServices Query (100%), Health endpoints |
| Phase 2 | GeoServices ApplyEdits (100%), Phase 1 maintained |
| Phase 3 | OGC API Features Part 1 (100%), Phase 1-2 maintained |
| Phase 4 | OData Query (100%), Tiles (100%), Phase 1-3 maintained |
| Phase 5 | Admin API (100%), All protocols maintained |

### Test-First Development

When implementing a new endpoint:

1. Add endpoint to `EndpointRegistry`
2. Create test class with proper attributes
3. Write failing test for happy path
4. Implement endpoint
5. Add error case tests
6. Verify architecture test passes

This ensures no endpoint ships without tests.
