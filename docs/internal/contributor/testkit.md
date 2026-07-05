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
A reusable local PostGIS instance is available via `docker compose -f docker/docker-compose.test-db.yml up -d`.

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

Enable the cross-server consume suite — `Honua.TestKit.GeoServerFixture`
plus `Honua.TestKit.MapServerFixture` (`camptocamp/mapserver:8.0`) drive
WMS 1.3, WFS 2.0, and WMTS 1.0 reads from Honua-as-client against
containerized GeoServer and MapServer reference sources. The suite routes
reference-source reads through Honua's Test-environment consume probe endpoint
instead of fetching the source servers directly from test code:

```bash
export HONUA_TEST_CROSS_SERVER_CONSUME="1"
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~CrossServerConsume"
```

The probe endpoint (`GET /__test/cross-server-consume/proxy?url=<sourceUrl>`)
is mounted only when `ASPNETCORE_ENVIRONMENT=Test`. It accepts loopback
`http`/`https` URLs without embedded credentials, forwards the request,
and maps upstream failures to `502 Bad Gateway`; requests exceeding the
two-minute timeout return `504 Gateway Timeout`. Invalid URLs return
`400 Bad Request`.

The nightly `cross-server-consume-nightly.yml` workflow runs the same
suite, refreshes [`docs/compatibility/cross-server-consume-gap-report.md`](../compatibility/cross-server-consume-gap-report.md)
from the TRX, and uploads both the TRX and gap report as workflow
artifacts. The auto-commit step is best-effort — if the push is blocked
(branch protection, missing token), the workflow logs a warning instead
of failing. Known interop quirks are recorded as `[Skip = "gap: ..."]`
on the test method so they surface in the gap report rather than
failing the run.

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

### Test Categories And Tiers

Each attribute emits both a `Category` trait and a `Tier` trait. The tier
maps to the CI schedule defined in ADR-0037 (`docs/contributor/adr/0037-unified-ci-test-tier-strategy.md`).

| Attribute | Category | Tier | When it runs |
|-----------|----------|------|--------------|
| `[UnitTest]` | `Unit` | `Fast` | Every PR (no DB, no HTTP, no Testcontainers). |
| `[IntegrationTest]` | `Integration` | `Integration` | Targeted shards on PRs; full matrix on scheduled/manual full integration runs and PRs labeled `ci/full`. PR shard step composes `(matrix.filter)&Tier!=Slow&Tier!=Fast` so Slow-tagged siblings skip and Fast tests run only once in the foundation lane. |
| `[EmulatorTest]` | `Integration,Emulator` | `Slow` | `nightly-slow-tier.yml` — runs `Tier=Slow&Category=Emulator` against LocalStack S3 + Azurite + Postgres. |
| `[ScaleTest]` | `Integration,Scale` | `Slow` | Currently **not** scheduled. Multi-node compose fixtures are tracked as a separate workflow; the trait is in place for the future workflow to opt in. |
| `[ExternalServiceTest]` | `Integration,External` | `Slow` | Currently **not** scheduled. External service credentials (e.g. Esri Geoportal) are tracked as a separate workflow. |
| `[CloudTest]` | `Integration,Cloud` | `Slow` | Currently **not** scheduled. Real-cloud credentials are tracked as a separate workflow. |
| `[FlakyTest("reason")]` | (additive) | (inherits sibling tier) | Always runs on its tier's normal schedule; surfaced separately by `flaky-detection.yml`. |

```csharp
[UnitTest]              // Fast, isolated tests
[IntegrationTest]       // Uses real dependencies
[ScaleTest]             // Multi-node scale, runs nightly
[ExternalServiceTest]   // External services, runs nightly
[EmulatorTest]          // Emulator-backed integration, runs nightly
[CloudTest]             // Deployed-environment validation, runs nightly
[FlakyTest("reason — tracked in #N")]  // Quarantine reporting, never auto-skips
```

Filter on tier directly: `dotnet test --filter "Tier=Fast"` (or `Integration` / `Slow`).

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
tests/dotnet/Honua.TestKit/
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

Scenarios are JSON documents under `tests/dotnet/eval/scenarios/*.json`, deserialized
through the source-generated `EvalJsonContext` (AOT-safe, no runtime
reflection). Each scenario declares:

- `id`, `name`, `mode` (`Analysis` | `Publish` | `Package` | `Deploy`)
- `fixtureProfile` (seed profile applied to the shared eval schema; all scenarios
  in one harness run must agree on it, and every layer named in `intent.inputs`
  must be served by that profile's collections in `tests/seed/seed.yaml` —
  `BundledScenario_DeclaredInputsAreServedByItsFixtureProfile` enforces this at
  unit-test time so the harness cannot go green against a profile that omits the
  data a scenario claims to exercise)
- `intent` — shape of `AnalysisIntent` (goal, inputs, constraints, requested
  outputs, `assumptionPolicy`)
- `precompiledPlan` — shape of `AnalysisPlan` (steps, DAG edges, declared
  outputs) used in Phase 1 until the compile seam from #529/#723 lands
- `expectedOutcome` — Phase 1 currently asserts `isExecutable`,
  `requiresApproval`, and `estimatedArtifactKinds`. `estimatedArtifactKinds` is
  enforced as an exact set: missing kinds fail with `artifact-kinds-missing`
  and unexpected kinds fail with `artifact-kinds-unexpected` so drift in either
  direction is caught. A proto artifact kind that has no domain counterpart is
  recorded as a deterministic `artifact-kind-unknown` `DryRun` failure rather
  than letting the harness throw, so `eval-report.json` is always emitted when
  the server adds a new proto enum value. It also carries forward
  `terminalWorkflowStatus` plus `expectsMapPackage` / `expectsAppPackage` for
  later execution/package assertions; today they are forward-declared and not
  validated against runtime outputs yet (`expectsAppPackage` is the only one
  that currently affects stage scoping)

The loader resolves scenarios (in order) from `HONUA_EVAL_SCENARIO_ROOT`, then
the `tests/dotnet/eval/scenarios/` directory under `Honua.sln`. When
`HONUA_EVAL_SCENARIO_ROOT` is set but points at a directory that does not
exist, the loader raises `EvalScenarioException` instead of silently falling
back to the bundled corpus so a typoed override cannot mask itself as a green
run. If the loader cannot locate a scenario under either root it also raises
`EvalScenarioException` with both search locations in the message.

### Stages and protocol parity

`EvalRunner` executes each scenario through a fixed stage sequence:
`CaptureIntent` → `CompilePlan` → `ValidatePlan` → `DryRun` → `ProtocolParity`
→ `SubmitJob` → `PollJob` → `GetJobResult` → `ComposeMapPackage` →
`ComposeAppPackage` → `PromoteDeployment`. Stages whose upstream capability is
not yet wired (execution engine, publish surface, package composition, deploy
promotion) report `Skipped` with a reason rather than failing — only `Failed`
stages break the gate today. `SubmitJob` also degrades to
`Skipped(redis-unavailable)` when the durable Redis-backed job store is absent,
which keeps local/dev runs honest instead of treating infrastructure gaps as
contract failures.

`DryRun` is a read operation in the canonical runtime (the gRPC handler
authorizes it as `OperatorOperation.Read`, and
`GeoprocessingJobService.DryRunPlan` only enforces plan-structure and catalog
validity — it does not gate on executability or approval). It therefore still
runs for scenarios that expect `isExecutable: false` or
`requiresApproval: true`, and the resulting `DryRun` outcome is scored against
the scenario's declared `estimatedArtifactKinds` just like any other scenario.

Only `SubmitJob` enforces the execution-only invariants through
`EnsurePlanExecutable` (rejects non-executable plans as `InvalidArgument`) and
`EnsureApproved` (rejects approval-gated plans as `FailedPrecondition`). When a
scenario intentionally expects one of those rejections and `ValidatePlan`
matches that expectation, `SubmitJob` is recorded as
`Skipped(plan-non-executable)` or `Skipped(plan-approval-required)` rather
than invoking the RPC that is contractually guaranteed to reject. This keeps
negative scenarios expressible without polluting the gate with false failures.

`ProtocolParity` cross-checks plan acceptance across gRPC and OGC API Processes
against the scenario's expected state, and it probes the GPServer `submitJob`
surface separately. The gRPC probe reports `matched-acceptance` or
`matched-rejection:{n}-violations` when the validate response matches the
expected `isExecutable` value, and `mismatch:{actual}` when it diverges. The
OGC probe treats `201 Created` as `matched-acceptance` for executable scenarios
and as `unexpected-acceptance` (Failed) for scenarios that expected rejection;
conversely `400 Bad Request` is `matched-rejection` (Passed) when the scenario
expected rejection and `unexpected-rejection` (Failed) otherwise. `403
Forbidden` is the OGC adapter's approval-gate rejection emitted after catalog
validation, so it is `matched-approval-required` (Passed) when the scenario
expected `requiresApproval: true` and `unexpected-approval-required` (Failed)
otherwise. `503` / `501` remain environmental skips, and when OGC execution
cannot enqueue because Redis is unavailable, that probe is recorded as
`Skipped(service-unavailable)` rather than `Failed`. The GPServer probe now
targets the published `geometry.buffer` task directly, so it records either
`Passed(matched-acceptance)` or an environmental skip such as
`Skipped(service-unavailable)` / `Skipped(authorization-required)` instead of a
synthetic task-resolution skip. When an HTTP probe is canceled for a reason
other than the outer run's own `CancellationToken` (for example an
`HttpClient.Timeout` firing), the probe is recorded as `Failed(http-timeout)`
so the overall scenario keeps its no-throw reporting contract instead of
aborting mid-run. Spans are emitted from the `Honua.Tests.Eval`
`ActivitySource` (one span per scenario, one per stage).

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
  `redisAvailable` (derived from observed successful `SubmitJob` stages)
- `scenarios[]` — id, mode, overall `status`
  (`Passed` / `Failed` / `PassedWithSkips`), per-stage outcomes, protocol parity
  probes, elapsed ms
- `rollup` — totals, `firstFailure` pointer, `totalElapsedMs`

`honua-devops-31` pins on the schema version and fails closed on mismatch.

### Running the harness

The harness runs in the **.NET Tests (Server - Operator Eval Harness)** CI
lane (`ci.yml`), which filters on
`Features.Eval|Features.Geoprocessing|Features.Protocols.Ogc.Api.Processes|Features.Protocols.Grpc|Features.Protocols.Mcp|Features.Protocols.GeoServices.GPServer`
and uploads the report as the `operator-eval-report` workflow artifact.

Locally:

```bash
# Run the full operator eval harness lane
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~Honua.Server.Tests.Features.Eval|FullyQualifiedName~Honua.Server.Tests.Features.Geoprocessing|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Grpc|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Mcp|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer"

# Filter by the OperatorEval protocol trait
dotnet test --filter "Protocol=OperatorEval"

# Point at the shared corpus when available
HONUA_EVAL_CORPUS_PATH=/path/to/geospatial-mcp \
HONUA_EVAL_CORPUS_VERSION=core@0001 \
  dotnet test --filter "Protocol=OperatorEval"
```

## Cloud-integration harness (#2163 / #2166 / #2164)

`tests/dotnet/Honua.CloudIntegration.Tests` is a standalone project (not in the ADR-0037 server
shard matrix, never AOT-published) that exercises Honua's cloud control-plane and artifact seams
against REAL backends. Every test carries a `Category` trait so the default PR run never invokes
it; the dedicated workflows opt in by filter.

### Emulated lanes — `Category=CloudIntegration` (free, no paid token)

Runs in `.github/workflows/cloud-integration-harness.yml` (nightly + manual). All tests are
`[SkippableFact]` and skip — never fail — when Docker/kind is unavailable.

| Scenario | Backend | Emulator |
|----------|---------|----------|
| `S3ArtifactRoundTripCloudIntegrationTests` | `AwsS3FileStorage` ServiceURL seam | LocalStack Community S3 |
| `S3ArtifactRollbackCloudIntegrationTests` | versioned-artifact rollback (enable versioning → publish v1/v2 → delete bad version → prior promoted live) | LocalStack Community S3 |
| `KubernetesJobLifecycleCloudIntegrationTests` | `KubernetesJobBatchComputeBackend` submit → succeeded | kind (real Kubernetes-in-Docker) |
| `KubernetesJobNegativePathCloudIntegrationTests` | same backend: non-zero exit → `Failed`; cancel running Job → `Cancelled` | kind |

The kind lane builds three busybox worker images (`/bin/true`, `/bin/false`, `/bin/sleep`) loaded
with `ImagePullPolicy=Never`; the backend's `BuildManifest` sets no container command, so each
image self-selects behaviour via its `ENTRYPOINT`.

### Local-substrate lane — `Category=LocalSubstrate` (ADR-0060 §Verification, #2166 / #2457)

Runs in the `local-substrate` job of `.github/workflows/cloud-integration-harness.yml` (same
nightly + manual triggers, no cloud secrets, plain ubuntu runner with Docker). It is the
substrate-neutral proof that a **single host — containers only, no Kubernetes, no cloud** — can
deploy, rolling-upgrade, run the expand/contract migration gate, and roll back with **zero
downtime** on BOTH planes. Every test is `[SkippableFact]` and skips (never fails) when Docker is
absent; the GP process tests need only a resolvable `dotnet` muxer, so they also run without Docker.

It drives the real trunk seams end-to-end — `YarpRollingDeployBackend` (`honua-yarp-rolling`,
`DeployTargetKind.SelfHostedRolling`) with a real embedded-YARP `InMemoryConfigProvider` and the
real `ProcessContainerRuntimeClient` against docker; `LocalProcessPoolBatchComputeBackend`
(`honua-local-process`, `BatchComputeTargetKind.LocalProcess`) spawning real child processes; and
the real `PostgresDatabaseMigrationRunner` expand/contract gate over a Testcontainers Postgres.

Matrix cells — {serving, GP} × {local/YARP} × {deploy, rolling-upgrade, expand/contract-migration, rollback}:

| Plane | Deploy | Rolling-upgrade | Expand/contract migration | Rollback |
|-------|--------|-----------------|---------------------------|----------|
| **Serving** (`LocalSubstrateRollingDeployTests`) | launch v2 standby, health-gate → `PromotionRecommended` | atomic proxy destination swap v1→v2; continuous request loop asserts **zero failed requests** across cutover | contract migrations gate the coordinated deploy (see migration row) | pre-cutover rollback keeps v1 serving (zero downtime); post-cutover rollback repoints the proxy at the previous replica, drains the failed one, and settles `RolledBack` |
| **GP** (`LocalSubstrateProcessPoolTests`) | child-process launch → `Running`→`Succeeded` with exit-code mapping; `HONUA_*` env contract asserted | non-blocking pool-saturation admission: pending marker then launch once a slot frees; contract-version gate declares baseline v1 so a v2 job is refused | — | `CancelAsync` kills the process tree → `Cancelled` |
| **Migration gate** (`LocalSubstrateMigrationGateTests`) | — | — | real runner over real Postgres: unannotated `DROP COLUMN` → **fail-closed** naming the compatibility-review marker; annotated → applies; `PlanMigrationsAsync` reports pending contract scripts (the `DeployPreflightProbe` signal that blocks a coordinated deploy) | — |

Image/binary choices: the serving lane builds two tiny `busybox:1.36` `httpd` images
(`honua-ci/ls-replica:v1|v2`) whose one-file docroot is the revision marker (HTTP 200 at `/`
doubles as the health signal). The GP lane compiles a shell-free managed helper with Roslyn and
launches it via the absolute-path `dotnet` muxer (no OS coreutil, PATH-shim-safe). The migration
lane compiles per-scenario synthetic migration assemblies (embedded `.sql` resources) with Roslyn so
the real runner is driven over a controlled expand/contract script set. The pure classification logic
(`MigrationSafetyClassifierTests`) and the probe's plan consumption (`DeployPreflightProbeTests`) stay
unit-tested; this lane exercises the real runner + real database path rather than duplicating them.

### Real-AWS certification lane — `Category=RealAwsCertification`

Runs in `.github/workflows/real-aws-certification.yml` (weekly + manual, never on `pull_request`)
and targets a LIVE AWS account. It is gated, budgeted, and teardown-guaranteed:

- **Gated** — the live test step runs only when `vars.REALAWS_CERT_ROLE_ARN` is configured
  (OIDC role assumption). Without it the lane is an explicit no-op; the tests also self-skip
  unless `HONUA_REALAWS_CERT_ENABLED=true`, so forks and credential-less runs never fail.
- **Budgeted** — `AwsBatchRealCertificationTests` only *registers* a job definition (never submits
  a job), so there is no compute and zero cost.
- **Isolated + torn down** — `RealAwsCertificationFixture` mints a unique `honua-cert-*` prefix for
  every resource; tests deregister in a `finally` block and assert no `ACTIVE` resource remains.

Run locally against your own account (credentials via the default chain):

```bash
HONUA_REALAWS_CERT_ENABLED=true HONUA_REALAWS_CERT_REGION=us-west-2 \
  dotnet test tests/dotnet/Honua.CloudIntegration.Tests/Honua.CloudIntegration.Tests.csproj \
  --filter "Category=RealAwsCertification"
```

**Remainder (tracked under #2164):** the full submit-to-`SUCCEEDED` Batch lifecycle and the
ECS/Lambda deploy + rollback certifications need an ephemeral Fargate compute environment + IAM
execution role whose teardown is slower and must be supervised, so they are not performed
automatically by this lane yet.

## Environment Requirements

- Docker (for Testcontainers)
- .NET 10 SDK
- 200+ max connections in PostgreSQL (configured automatically)
