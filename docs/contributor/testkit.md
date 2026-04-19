# Honua.TestKit

Comprehensive test infrastructure for Honua Server integration and unit tests.

## Features

- **PostgreSQL/PostGIS Integration**: Testcontainers-based database fixtures with schema-based isolation
- **Redis Integration**: Testcontainers-based Redis fixture for caching and import coordination tests
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

#### External Database (opt-in)

Set `HONUA_TEST_DB_URL` to use an existing PostGIS database instead of Testcontainers.

```bash
export HONUA_TEST_DB_URL="Host=localhost;Database=honua_test;Username=test;Password=test"
```

### RedisFixture

Shared Redis container for integration tests that need distributed caching or job coordination.

#### External Redis (opt-in)

Set `HONUA_TEST_REDIS_URL` to use an existing Redis instance instead of Testcontainers.

```bash
export HONUA_TEST_REDIS_URL="localhost:6379"
```

#### External Service Tests (opt-in)

Enable Geoportal-backed Esri import integration tests:

```bash
export HONUA_TEST_ESRI_GEOPORTAL="1"
```

Enable ArcGIS parity import checks (source snapshot vs imported table parity):

```bash
export HONUA_TEST_ESRI_PARITY="1"
```

#### Shared YAML Seed

Apply shared YAML seed data to a schema:

```csharp
using Honua.TestKit.Seeding;

var schema = await _postgres.CreateSeededSchemaAsync(
    nameof(MyTests),
    "tests/seed/seed.yaml",
    profile: "core");
```

Or opt-in via environment variables:

```bash
export HONUA_TEST_DB_SEED_PATH="tests/seed/seed.yaml"
export HONUA_TEST_DB_SEED_PROFILE="core"
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

## End-to-End Operator Eval Harness

The `Honua.TestKit.Eval` namespace hosts the end-to-end operator-workflow eval
harness. It drives the canonical `HonuaProcessService` runtime and both
compatibility adapters (gRPC, OGC API Processes, GeoServices GPServer) through a
single fixture-backed scenario suite and emits a versioned report that
`honua-devops-31` treats as the canonical server-side integration gate.

### Authoring scenarios

Scenarios are JSON documents under `tests/Eval/scenarios/*.json`, deserialized
through the source-generated `EvalJsonContext` (AOT-safe, no runtime
reflection). Each scenario declares:

- `id`, `name`, `mode` (`Analysis` | `Publish` | `Package` | `Deploy`)
- `fixtureProfile` (seed profile applied to the shared eval schema; all scenarios
  in one harness run must agree on it)
- `intent` — shape of `AnalysisIntent` (goal, inputs, constraints, requested
  outputs, `assumptionPolicy`)
- `precompiledPlan` — shape of `AnalysisPlan` (steps, DAG edges, declared
  outputs) used in Phase 1 until the compile seam from #529/#723 lands
- `expectedOutcome` — Phase 1 currently asserts `isExecutable`,
  `requiresApproval`, and `estimatedArtifactKinds`. It also carries forward
  `terminalWorkflowStatus` plus `expectsMapPackage` / `expectsAppPackage` for
  later execution/package assertions; today they are forward-declared and not
  validated against runtime outputs yet (`expectsAppPackage` is the only one
  that currently affects stage scoping)

The loader resolves scenarios (in order) from `HONUA_EVAL_SCENARIO_ROOT`, the
`tests/Eval/scenarios/` directory under `Honua.sln`, then the directory next to
the test binary.

### Stages and protocol parity

`EvalRunner` executes each scenario through a fixed stage sequence:
`CaptureIntent` → `CompilePlan` → `ValidatePlan` → `DryRun` → `ProtocolParity`
→ `SubmitPlanJob` → `PollJob` → `GetJobResults` → `ComposeMapPackage` →
`ComposeAppPackage` → `PromoteDeployment`. Stages whose upstream capability is
not yet wired (execution engine, publish surface, package composition, deploy
promotion) report `Skipped` with a reason rather than failing — only `Failed`
stages break the gate today. `SubmitPlanJob` also degrades to
`Skipped(redis-unavailable)` when the durable Redis-backed job store is absent,
which keeps local/dev runs honest instead of treating infrastructure gaps as
contract failures.

`ProtocolParity` cross-checks plan acceptance across gRPC and OGC API
Processes, and it probes the GPServer `submitJob` surface separately. Because
the GPServer adapter still lacks a formal task catalog binding, that probe is
recorded as `Skipped(task-resolution-unavailable)` instead of a false `Passed`.
When OGC execution cannot enqueue because Redis is unavailable, that probe is
recorded as `Skipped(service-unavailable)` rather than `Failed`.
Spans are emitted from the `Honua.Tests.Eval` `ActivitySource` (one span per
scenario, one per stage).

### Fixture corpus

Fixture resolution is routed through `IEvalFixtureSource` and applied to the
shared `WebAppFixture` before host startup:

- `SharedCorpusFixtureSource` — binds to the geospatial-mcp corpus located by
  `HONUA_EVAL_CORPUS_PATH`, which must point to a YAML seed file or a directory
  containing `seed.yaml`; `HONUA_EVAL_CORPUS_VERSION` is surfaced in the report
  envelope.
- `LocalSeedFixtureSource` — falls back to the in-repo `tests/seed/seed.yaml`
  baseline (`corpusVersion: seed.yaml@v1`) so the harness runs locally without
  external mounts.

The harness resolves the single `fixtureProfile` declared by the discoverable
scenario set and applies that profile with `WebAppFixture.UseSeed(...)`. Mixed
profiles fail fast because the eval harness intentionally uses one class-scoped
seeded schema per run.

### Report artifact

Each run emits `tests/TestResults/eval-report.json` (override with
`HONUA_EVAL_REPORT_DIR`) serialized through `EvalJsonContext`. The document is
pinned to `reportSchemaVersion = "1"` (`EvalReportSchema.Version`) and
includes:

- `environment` — `corpusSource`, `corpusVersion`, `corpusPath`,
  `redisAvailable` (derived from observed successful `SubmitPlanJob` stages)
- `scenarios[]` — id, mode, overall `status`
  (`Passed` / `Failed` / `PassedWithSkips`), per-stage outcomes, protocol parity
  probes, elapsed ms
- `rollup` — totals, `firstFailure` pointer, `totalElapsedMs`

`honua-devops-31` pins on the schema version and fails closed on mismatch.

### Running the harness

The harness runs in the **.NET Tests (Server - Operator Eval Harness)** CI
lane (`ci.yml`), which filters on
`Features.Eval|Features.Geoprocessing|Features.OgcProcesses|Features.Grpc` and
uploads the report as the `operator-eval-report` workflow artifact.

Locally:

```bash
# Run the full operator eval harness lane
dotnet test tests/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~Honua.Server.Tests.Features.Eval"

# Filter by the OperatorEval protocol trait
dotnet test --filter "Protocol=OperatorEval"

# Point at the shared corpus when available
HONUA_EVAL_CORPUS_PATH=/path/to/geospatial-mcp \
HONUA_EVAL_CORPUS_VERSION=core@0001 \
  dotnet test --filter "Protocol=OperatorEval"
```

## Environment Requirements

- Docker (for Testcontainers)
- .NET 10 SDK
- 200+ max connections in PostgreSQL (configured automatically)
