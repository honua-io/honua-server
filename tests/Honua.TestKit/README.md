# Honua.TestKit

Comprehensive test infrastructure for Honua Server integration and unit tests.

## Features

- **PostgreSQL/PostGIS Integration**: Testcontainers-based database fixtures with schema-based isolation
- **Parallel Test Execution**: Aggressive parallel execution with schema isolation for maximum throughput
- **Custom Test Attributes**: Protocol, Operation, and Endpoint tracking for 100% API surface coverage
- **Fluent Test Data Builders**: Build complex spatial data scenarios with ease
- **HTTP Response Assertions**: FluentAssertions extensions for clean test code
- **AOT-Compatible**: Native AOT-ready test infrastructure

## Quick Start

### Basic Integration Test

```csharp
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Xunit;

[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;

    public QueryEndpointTests(PostgresFixture postgres)
    {
        _ = postgres; // Injected by xUnit collection
    }

    public async Task InitializeAsync()
    {
        _fixture = new WebAppFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        // Arrange - create isolated schema for this test
        var schema = await _fixture.CreateIsolatedSchemaAsync(nameof(QueryEndpointTests));

        await _fixture.Postgres.CreateTestData(schema)
            .WithTable("parcels", "POLYGON")
            .WithPolygon("parcels", "Parcel 1", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")
            .WithPolygon("parcels", "Parcel 2", "POLYGON((2 2, 3 2, 3 3, 2 3, 2 2))")
            .BuildAsync();

        // Act
        var response = await _fixture.Client.GetAsync("/rest/services/1/FeatureServer/0/query?where=name='Parcel 1'");

        // Assert
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Parcel 1");
        content.Should().NotContain("Parcel 2");
    }
}
```

### Unit Test

```csharp
using Honua.TestKit.Attributes;
using FluentAssertions;
using Xunit;

public class CqlParserTests
{
    [UnitTest]
    public void Parse_ValidExpression_ReturnsAst()
    {
        // Arrange
        var parser = new CqlParser();
        var expression = "population > 1000000";

        // Act
        var result = parser.Parse(expression);

        // Assert
        result.Should().NotBeNull();
        result.Operator.Should().Be(ComparisonOperator.GreaterThan);
    }
}
```

## Test Fixtures

### PostgresFixture

Shared PostgreSQL/PostGIS container for integration tests. Supports schema-based isolation for parallel execution.

```csharp
[Collection("Database")]
public class MyTests
{
    private readonly PostgresFixture _postgres;

    public MyTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [IntegrationTest]
    public async Task Test_WithIsolatedSchema()
    {
        // Create isolated schema for this test
        var schema = await _postgres.CreateIsolatedSchemaAsync("MyTests");

        // Schema is automatically cleaned up when test completes
        await _postgres.CreateTestData(schema)
            .WithTable("my_table")
            .WithPoint("my_table", "Point 1", -122.4194, 37.7749)
            .BuildAsync();
    }
}
```

### WebAppFixture

Combined HTTP client and database fixture for end-to-end integration tests.

```csharp
public class ApiTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new WebAppFixture();
        // Optional: replace services
        _fixture.ConfigureServices(services =>
        {
            services.RemoveAll<ITimeProvider>();
            services.AddSingleton<ITimeProvider>(new FakeTimeProvider());
        });
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }
}
```

## Test Data Builders

### Spatial Data

```csharp
await postgres.CreateTestData(schema)
    // Create tables
    .WithTable("points", "POINT")
    .WithTable("polygons", "POLYGON", additionalColumns: new Dictionary<string, string>
    {
        ["area_sqm"] = "NUMERIC",
        ["zone_type"] = "TEXT"
    })

    // Add points
    .WithPoint("points", "San Francisco", -122.4194, 37.7749)
    .WithPointGrid("points", "grid", 0, 0, rows: 10, cols: 10, spacing: 0.01)
    .WithRandomPoints("points", count: 100, minLon: -180, minLat: -90, maxLon: 180, maxLat: 90)

    // Add polygons
    .WithPolygon("polygons", "Park", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")
    .WithCircle("polygons", "Buffer Zone", centerLon: -122.4194, centerLat: 37.7749, radiusMeters: 1000)
    .WithLineString("roads", "Highway 1", new[]
    {
        (-122.4194, 37.7749),
        (-122.4084, 37.7849),
        (-122.3974, 37.7949)
    })

    .BuildAsync();
```

## Custom Attributes

### Test Categories

```csharp
[UnitTest]  // Fast, isolated tests
[IntegrationTest]  // Uses real dependencies
```

### Protocol Tracking

```csharp
[Protocol(Protocols.FeatureServer)]
[Protocol(Protocols.OgcApiFeatures)]
[Protocol(Protocols.ODataV4)]
[Protocol(Protocols.Mvt)]
```

### Operation Tracking

```csharp
[Operation(Operations.Query)]
[Operation(Operations.Create)]
[Operation(Operations.SpatialQuery)]
```

### Endpoint Coverage

```csharp
[Endpoint("GET /healthz/live")]
[Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/addFeatures")]
```

## HTTP Assertions

```csharp
response.Be200Ok();
response.Be201Created();
response.Be400BadRequest();
response.Be404NotFound();
response.BeSuccessful();
response.HaveStatusCode(HttpStatusCode.OK);
response.HaveContentType("application/json");
response.HaveHeader("X-Custom-Header");
```

## Parallel Execution

Tests are configured for aggressive parallel execution:

- **Assembly parallelization**: Enabled
- **Collection parallelization**: Enabled
- **Max threads**: Unlimited (-1)
- **Isolation strategy**: Schema-based (not transaction-based)

Each test class in the `Database` collection gets a shared PostgreSQL container but can create isolated schemas for parallel execution.

## Project Structure

```
tests/Honua.TestKit/
├── Attributes/              # Custom test attributes
│   ├── UnitTestAttribute.cs
│   ├── IntegrationTestAttribute.cs
│   ├── ProtocolAttribute.cs
│   ├── OperationAttribute.cs
│   └── EndpointAttribute.cs
├── Constants/               # Test constants
│   ├── Protocols.cs
│   └── Operations.cs
├── Extensions/              # Helper extensions
│   └── HttpResponseAssertions.cs
├── PostgresFixture.cs       # Database test fixture
├── WebAppFixture.cs         # HTTP + database fixture
├── TestDataBuilder.cs       # Fluent data builders
├── DatabaseCollection.cs    # xUnit collection definition
└── GlobalUsings.cs
```

## Dependencies

- **xUnit**: Test framework
- **FluentAssertions**: Assertion library
- **Testcontainers**: Container management
- **Npgsql**: PostgreSQL driver
- **NetTopologySuite**: Spatial data handling
- **Bogus**: Fake data generation

## Coverage Requirements

- **API Surface**: 100% (enforced by architecture tests)
- **Line Coverage**: 80%
- **Branch Coverage**: 70%

Every endpoint requires at least one integration test with the `[Endpoint]` attribute.

## Best Practices

1. **Prefer Integration Tests**: 70% integration, 20% unit, 10% E2E
2. **Use Schema Isolation**: Create isolated schemas for parallel tests
3. **Clean Test Names**: `MethodUnderTest_Scenario_ExpectedBehavior`
4. **Attribute Your Tests**: Use Protocol/Operation/Endpoint attributes
5. **Build Once**: Use TestDataBuilder for complex setup
6. **Assert Clearly**: Use FluentAssertions and extension methods
7. **Test Real Paths**: No mocking of database or HTTP stack

## Running Tests

```bash
# Run all tests
dotnet test

# Run only integration tests
dotnet test --filter "Category=Integration"

# Run only health endpoint tests
dotnet test --filter "Protocol=Health"

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

## Environment Requirements

- Docker (for Testcontainers)
- .NET 10 SDK
- 200+ max connections in PostgreSQL (configured automatically)
