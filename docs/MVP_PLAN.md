# Honua Greenfield MVP Implementation Plan

> **Version:** 1.0
> **Date:** December 2025
> **Status:** Draft
> **Scope:** Complete rewrite targeting MVP specification

---

## Executive Summary

This document outlines the plan for a greenfield implementation of Honua, replacing the existing codebase with a clean, focused MVP built with strict quality guardrails from day one.

### Decision Rationale

| Factor | Decision |
|--------|----------|
| **Why greenfield** | Tech debt in existing codebase makes MVP isolation harder than rebuilding |
| **Existing code usage** | Reference only — used as documentation, not ported |
| **Quality philosophy** | Maximum rigor from day one; quality over speed |
| **Timeline** | Flexible — ship when ready, not to arbitrary deadline |

---

## GTM Personas and Primary Workflows

- **Business/BI user**: OData v4 for Excel/Power BI access, may not use maps.
- **Esri user**: GeoServices FeatureServer for ArcGIS tooling compatibility.
- **Open source GIS user**: OGC API Features/Tiles compatibility.
- **GIS admin/DevOps**: Observability, deployability, and metadata management (admin APIs/UI).

These personas shape which protocols are MVP-critical and guide validation targets.

---

## Current Implementation Status (current tree)

This plan is the target architecture and scope. The current repo already ships the core server APIs, but several MVP items remain open.

**Implemented (server + admin API):**
- FeatureServer query/edit/attachments/related endpoints
- OGC API Features and OGC API Tiles endpoints
- OData v4 CRUD + spatial query endpoints
- MVT tiles at `/tiles/{layerId}/{z}/{x}/{y}.mvt`
- File import and Esri service import APIs
- Admin APIs for connections/services/layers/relationships/styles + operations progress
- OIDC authentication plumbing and optional Redis metadata cache

**Pending MVP issues (open):**
- #20 TileJSON metadata endpoint
- #58 Service enable/disable controls
- #25, #26, #27, #42, #43 Admin UI (connections, publishing, health dashboard, map preview)
- #30 Embedded Maputnik style editor
- #244 Canonical cross-protocol style pipeline
- #187 Esri Service Import Wizard UI
- #31, #32, #33 Deployment templates (Helm + AWS/Azure Terraform)
- #38 Documentation and API docs
- #39 Security hardening and input validation

### MVP Gap Matrix (current tree)

| Capability | Target MVP | Current status | Tracking | Risk |
| --- | --- | --- | --- | --- |
| Admin UI (connections/publishing/import/health/preview) | Required (Phase 4) | API only | #25 #26 #27 #42 #43 #187 | High |
| Map style editing | Required (Phase 4) | Not implemented | #30 | Medium |
| TileJSON metadata | Required (Phase 3.25) | Not implemented | #20 | Medium |
| Service enable/disable | Required (Phase 4) | Not implemented | #58 | Medium |
| Deployment templates | Required (Phase 4.5) | Not implemented | #31 #32 #33 #34 | Medium |
| Docs + security hardening | Required (Phase 5) | Incomplete | #38 #39 | High |
| Cross-protocol style pipeline | Required (Phase 4) | Not implemented | #244 | High |

### Locked MVP Scope (recommended)

**Must ship for MVP release:**
- FeatureServer query/edit/attachments/related
- OGC API Features (Core + Transactions)
- OGC API Tiles + MVT endpoints
- OData v4 CRUD with spatial filters
- File import + Esri service import APIs
- Admin UI for connections + layer publishing + import workflow (OIDC required for browser UI)
- Service enable/disable controls
- TileJSON metadata
- Canonical cross-protocol style pipeline (MapLibre as source of truth)
- Security hardening + API/docs alignment

**Explicitly defer to Beta/GA:**
- Query caching + performance enhancements (#233, #229)
- Advanced observability/alerting (#227)
- Layer-level RBAC and rate limiting (#240, #242, #243)
- OGC certification path documentation (#232)
- GCP Terraform deployment templates

---

## Technical Decisions

### Stack

| Layer | Technology | Rationale |
|-------|------------|-----------|
| **Runtime** | .NET 10 | Latest LTS (Nov 2025), best performance |
| **Compilation** | Native AOT | Sub-100ms cold start, ~30MB image |
| **Web framework** | Minimal APIs | Lean, fast, AOT-compatible |
| **Data access** | Npgsql (raw ADO.NET) | Direct SQL, AOT-compatible, maximum control |
| **Database** | PostgreSQL + PostGIS | MVP scope, single database |
| **Admin UI** | Blazor WebAssembly | C# end-to-end, single language |
| **Testing** | xUnit + Testcontainers | Integration-first with real PostgreSQL |
| **Benchmarks** | BenchmarkDotNet | Performance regression detection in CI |
| **Orchestration** | .NET Aspire | Local dev dashboard, service discovery, OpenTelemetry |
| **Container** | Docker (Alpine-based) | Single deployable artifact, AOT + JIT variants |

### Project Structure

```
Honua/
├── src/
│   ├── Honua.AppHost/             # Aspire orchestration (local dev)
│   ├── Honua.ServiceDefaults/     # Shared Aspire defaults (OTel, health, resilience)
│   ├── Honua.Server/              # Main host (Minimal APIs)
│   │   ├── Program.cs             # Composition root
│   │   ├── Endpoints/             # API endpoint modules
│   │   │   ├── FeatureServer/     # GeoServices REST
│   │   │   ├── OgcFeatures/       # OGC API Features
│   │   │   └── Admin/             # Admin API
│   │   ├── Services/              # Business logic
│   │   ├── Data/                  # Repositories, queries
│   │   └── Infrastructure/        # Cross-cutting (auth, logging)
│   │
│   ├── Honua.Core/                # Domain models, abstractions
│   │   ├── Models/                # Layer, Feature, Geometry, etc.
│   │   ├── Queries/               # Query builders, filters
│   │   └── Abstractions/          # Interfaces
│   │
│   └── Honua.Admin/               # Blazor WASM admin UI
│       ├── Pages/                 # Razor components
│       ├── Services/              # API clients
│       └── Shared/                # Shared components
│
├── tests/
│   ├── Honua.TestKit/             # Shared test infrastructure
│   │   ├── Attributes/            # Custom trait attributes
│   │   ├── Fixtures/              # Shared fixtures (Postgres, WebApp)
│   │   ├── Builders/              # Test data builders
│   │   └── Extensions/            # Assertion helpers
│   │
│   ├── Honua.Core.Tests/          # Unit tests (no I/O)
│   │   ├── Query/                 # Filter parsing tests
│   │   ├── Geometry/              # Geometry operation tests
│   │   └── Models/                # Domain model tests
│   │
│   ├── Honua.Server.Tests/        # Integration tests
│   │   ├── FeatureServer/         # FeatureServer protocol tests
│   │   ├── OgcFeatures/           # OGC API Features tests
│   │   ├── OData/                 # OData v4 tests
│   │   ├── Tiles/                 # MVT tile tests
│   │   ├── Admin/                 # Admin API tests
│   │   ├── Performance/           # Soak and concurrency tests
│   │   └── Conformance/           # Protocol conformance tests
│   │
│   └── Honua.Architecture.Tests/  # Architecture enforcement
│       └── DependencyTests.cs     # NetArchTest rules
│
├── benchmarks/
│   └── Honua.Benchmarks/          # BenchmarkDotNet performance tests
│       ├── QueryBenchmarks.cs     # Query endpoint benchmarks
│       ├── EditBenchmarks.cs      # ApplyEdits benchmarks
│       ├── TileBenchmarks.cs      # MVT generation benchmarks
│       ├── MemorySoakBenchmarks.cs # Memory soak benchmarks
│       └── StartupBenchmarks.cs   # Cold start measurement
│
├── docker/
│   ├── Dockerfile                 # Production image
│   └── docker-compose.yml         # Dev environment
│
├── deploy/
│   ├── helm/
│   │   └── honua/                 # Helm chart
│   │       ├── Chart.yaml
│   │       ├── values.yaml
│   │       └── templates/
│   │           ├── deployment.yaml
│   │           ├── service.yaml
│   │           ├── configmap.yaml
│   │           ├── secret.yaml
│   │           └── ingress.yaml
│   │
│   └── terraform/
│       ├── modules/
│       │   ├── aws-ecs/           # AWS ECS/Fargate
│       │   └── azure-aca/         # Azure Container Apps
│       │
│       └── examples/
│           ├── aws/               # Complete AWS example
│           └── azure/             # Complete Azure example
│
├── scripts/
│   ├── check-perf-regression.py   # Benchmark regression checker
│   └── check-instructions-sync.sh # Ensure CLAUDE/CODEX parity
│
├── .github/
│   ├── workflows/
│   │   ├── ci.yml                 # Build, test, coverage, perf
│   │   └── release.yml            # Publish images
│   └── perf-baseline.json         # Benchmark baseline for regression
│
    └── docs/
        ├── api/                       # API documentation
        ├── architecture/              # Architecture decisions
        └── deployment/                # Deployment guides
        ├── kubernetes.md          # Helm install guide
        ├── aws.md                 # AWS ECS/Fargate guide
        └── azure.md               # Azure Container Apps guide
```

### Key Architectural Principles

1. **Vertical Slices**: Organize by feature (FeatureServer, OgcFeatures) not by layer
2. **No Abstractions Without Need**: Start concrete, abstract when you have 2+ implementations
3. **Integration-First Testing**: Real database in tests, minimal mocking
4. **Immutable by Default**: Records, readonly, functional patterns where sensible
5. **Fail Fast**: Validate early, throw on invalid state, no silent failures

### Issue Traceability

- Every plan task maps to a GitHub issue; issue acceptance criteria are the source of truth
- PRs must reference the issue and keep plan + issues in sync
- If plan and issues diverge, reconcile immediately before coding

---

## Performance Architecture

### Design Philosophy

**Zero-cost abstractions.** Every layer must justify its existence with benchmarks. If it's slow, it's wrong.

### Native AOT Configuration

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <OptimizationPreference>Speed</OptimizationPreference>
  <IlcOptimizationPreference>Speed</IlcOptimizationPreference>
  <InvariantGlobalization>true</InvariantGlobalization>
  <StripSymbols>true</StripSymbols>
</PropertyGroup>
```

**AOT Compatibility Rules:**
- No reflection (use source generators)
- No `dynamic` keyword
- No runtime code generation
- All JSON types must have source-generated serializers
- All DI registrations must be explicit (no assembly scanning)

### Source Generators (Zero Reflection)

| Purpose | Generator | Usage |
|---------|-----------|-------|
| **JSON serialization** | `System.Text.Json` source gen | All request/response DTOs |
| **Logging** | `LoggerMessage` source gen | All log statements |
| **DI registration** | Manual registration | No `AddControllers()`, explicit `MapGet/Post` |
| **Validation** | Manual `IValidatable` | No FluentValidation |
| **SQL mapping** | Manual mappers | No Dapper, no EF |

```csharp
// JSON source generator example
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(FeatureCollection))]
[JsonSerializable(typeof(ApplyEditsRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class HonuaJsonContext : JsonSerializerContext { }

// Logging source generator example
public static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Query executed: {LayerId} returned {Count} features in {ElapsedMs}ms")]
    public static partial void QueryExecuted(ILogger logger, string layerId, int count, double elapsedMs);
}
```

### Object Pooling

```csharp
// ArrayPool for temporary buffers
public sealed class GeometryParser
{
    public Geometry Parse(ReadOnlySpan<byte> wkb)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(wkb.Length);
        try
        {
            // Parse without allocation
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

// ObjectPool for expensive objects
services.AddSingleton<ObjectPool<StringBuilder>>(
    new DefaultObjectPoolProvider().CreateStringBuilderPool());

// NpgsqlDataSource (built-in connection pooling)
services.AddNpgsqlDataSource(connectionString, builder =>
{
    builder.MinPoolSize = 10;
    builder.MaxPoolSize = 100;
    builder.ConnectionIdleLifetime = TimeSpan.FromMinutes(5);
});
```

### Zero-Allocation Patterns

```csharp
// Use Span<T> for parsing
public static bool TryParseWhere(ReadOnlySpan<char> input, out WhereClause clause)
{
    // No string allocations during parsing
}

// Use stackalloc for small buffers
Span<byte> buffer = stackalloc byte[256];

// Use string.Create for building strings
string result = string.Create(length, state, (span, s) => { /* write directly */ });

// Avoid LINQ in hot paths - use foreach
foreach (var feature in features) { } // Good
features.Select(f => f.Id).ToList();  // Bad - allocates

// Use ValueTask for likely-synchronous operations
public ValueTask<Feature?> GetCachedAsync(string id)
{
    if (_cache.TryGetValue(id, out var feature))
        return ValueTask.FromResult(feature); // No allocation
    return GetFromDatabaseAsync(id);
}
```

### Response Optimization

```csharp
// Response compression
services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/geo+json", "application/json"]);
});

services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);

// Output caching for metadata endpoints
app.MapGet("/rest/services/{serviceId}/FeatureServer", GetServiceMetadata)
    .CacheOutput(policy => policy
        .Expire(TimeSpan.FromMinutes(5))
        .Tag("metadata"));

// ETag support for feature queries
app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId}/query", QueryFeatures)
    .AddEndpointFilter<ETagFilter>();
```

### Database Performance

```csharp
// Prepared statements (Npgsql caches automatically)
await using var cmd = dataSource.CreateCommand(
    "SELECT * FROM features WHERE layer_id = $1 AND ST_Intersects(geom, $2)");
cmd.Parameters.AddWithValue(layerId);
cmd.Parameters.AddWithValue(geometry);

// Batch operations
await using var batch = dataSource.CreateBatch();
foreach (var feature in features)
{
    var cmd = batch.CreateBatchCommand();
    cmd.CommandText = "INSERT INTO features (id, geom, attrs) VALUES ($1, $2, $3)";
    cmd.Parameters.AddWithValue(feature.Id);
    cmd.Parameters.AddWithValue(feature.Geometry);
    cmd.Parameters.AddWithValue(feature.Attributes);
}
await batch.ExecuteNonQueryAsync(ct);

// Streaming large result sets
await foreach (var row in cmd.ExecuteReaderAsync(ct))
{
    yield return MapFeature(row); // Stream, don't buffer
}
```

### Transaction Management

**Design Principles:**
- Single database = simple transactions (no distributed TX complexity)
- All-or-nothing for ApplyEdits batches
- Savepoints for partial success scenarios (GeoServices compatibility)
- Connection-per-request with explicit transaction scope

**Transaction Abstraction:**

```csharp
// src/Honua.Core/Abstractions/IUnitOfWork.cs
public interface IUnitOfWork : IAsyncDisposable
{
    IFeatureStore Features { get; }
    IAttachmentStore Attachments { get; }

    Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct = default);
}

public interface ITransactionScope : IAsyncDisposable
{
    string TransactionId { get; }

    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);

    // Savepoints for partial rollback (GeoServices ApplyEdits compatibility)
    Task<ISavepoint> CreateSavepointAsync(string name, CancellationToken ct = default);
}

public interface ISavepoint
{
    string Name { get; }
    Task RollbackAsync(CancellationToken ct = default);
    Task ReleaseAsync(CancellationToken ct = default);
}
```

**PostgreSQL Implementation:**

```csharp
// src/Honua.Postgres/PostgresUnitOfWork.cs
public sealed class PostgresUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlDataSource _dataSource;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public PostgresUnitOfWork(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public IFeatureStore Features => new PostgresFeatureStore(_connection!, _transaction);
    public IAttachmentStore Attachments => new PostgresAttachmentStore(_connection!, _transaction);

    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct = default)
    {
        _connection = await _dataSource.OpenConnectionAsync(ct);
        _transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        return new PostgresTransactionScope(_transaction);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null)
            await _transaction.DisposeAsync();

        if (_connection != null)
            await _connection.DisposeAsync();
    }
}

public sealed class PostgresTransactionScope : ITransactionScope
{
    private readonly NpgsqlTransaction _transaction;

    public string TransactionId { get; } = Guid.NewGuid().ToString("N")[..8];

    public PostgresTransactionScope(NpgsqlTransaction transaction)
    {
        _transaction = transaction;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        await _transaction.CommitAsync(ct);
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await _transaction.RollbackAsync(ct);
    }

    public async Task<ISavepoint> CreateSavepointAsync(string name, CancellationToken ct = default)
    {
        await _transaction.SaveAsync(name, ct);
        return new PostgresSavepoint(_transaction, name);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Transaction owns lifecycle
}

public sealed class PostgresSavepoint : ISavepoint
{
    private readonly NpgsqlTransaction _transaction;
    public string Name { get; }

    public PostgresSavepoint(NpgsqlTransaction transaction, string name)
    {
        _transaction = transaction;
        Name = name;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await _transaction.RollbackAsync(Name, ct);
    }

    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await _transaction.ReleaseAsync(Name, ct);
    }
}
```

**ApplyEdits Handler (All-or-Nothing):**

```csharp
// All operations succeed or all fail
public sealed class ApplyEditsHandler
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ApplyEditsHandler> _logger;

    public async Task<ApplyEditsResponse> HandleAsync(
        ApplyEditsRequest request,
        CancellationToken ct)
    {
        await using var tx = await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            var results = new ApplyEditsResults();

            // Process adds
            foreach (var feature in request.Adds ?? [])
            {
                var created = await _unitOfWork.Features.CreateAsync(request.LayerId, feature, ct);
                results.AddResults.Add(new EditResult(created.ObjectId, success: true));
            }

            // Process updates
            foreach (var feature in request.Updates ?? [])
            {
                var updated = await _unitOfWork.Features.UpdateAsync(
                    request.LayerId, feature.ObjectId, feature, ct);
                results.UpdateResults.Add(new EditResult(updated.ObjectId, success: true));
            }

            // Process deletes
            foreach (var objectId in request.Deletes ?? [])
            {
                await _unitOfWork.Features.DeleteAsync(request.LayerId, objectId, ct);
                results.DeleteResults.Add(new EditResult(objectId, success: true));
            }

            await tx.CommitAsync(ct);

            Log.ApplyEditsCompleted(_logger, request.LayerId,
                results.AddResults.Count, results.UpdateResults.Count, results.DeleteResults.Count);

            return new ApplyEditsResponse(results);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            Log.ApplyEditsFailed(_logger, request.LayerId, ex.Message, ex);
            throw;
        }
    }
}
```

**ApplyEdits Handler (Partial Success with Savepoints):**

```csharp
// GeoServices-compatible: report individual failures, commit successes
public sealed class ApplyEditsPartialHandler
{
    public async Task<ApplyEditsResponse> HandleAsync(
        ApplyEditsRequest request,
        bool rollbackOnFailure, // GeoServices 'rollbackOnFailure' parameter
        CancellationToken ct)
    {
        if (rollbackOnFailure)
        {
            return await HandleAllOrNothingAsync(request, ct);
        }

        await using var tx = await _unitOfWork.BeginTransactionAsync(ct);
        var results = new ApplyEditsResults();

        try
        {
            // Process each operation with savepoint
            foreach (var feature in request.Adds ?? [])
            {
                var savepoint = await tx.CreateSavepointAsync($"add_{feature.TempId}", ct);
                try
                {
                    var created = await _unitOfWork.Features.CreateAsync(request.LayerId, feature, ct);
                    results.AddResults.Add(new EditResult(created.ObjectId, success: true));
                    await savepoint.ReleaseAsync(ct);
                }
                catch (Exception ex)
                {
                    await savepoint.RollbackAsync(ct);
                    results.AddResults.Add(new EditResult(null, success: false, error: new EditError(
                        code: 1000,
                        description: ex.Message)));
                }
            }

            // Similar for updates and deletes...

            await tx.CommitAsync(ct);

            Log.ApplyEditsPartialCompleted(_logger, request.LayerId,
                results.SuccessCount, results.FailureCount);

            return new ApplyEditsResponse(results);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
```

**Retry Policy with Polly:**

```csharp
// src/Honua.Server/Infrastructure/Resilience/ResiliencePolicies.cs
public static class ResiliencePolicies
{
    /// <summary>
    /// Retry policy for transient connection errors ONLY.
    /// IMPORTANT: Only retry connection acquisition, not mid-transaction errors.
    /// Once a transaction starts, failures should propagate (transaction will rollback).
    /// Retrying after partial execution risks duplicate operations.
    /// </summary>
    public static IAsyncPolicy GetConnectionRetryPolicy()
    {
        return Policy
            .Handle<NpgsqlException>(ex => IsConnectionError(ex))
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt)),
                onRetry: (exception, timespan, attempt, context) =>
                {
                    Log.ConnectionRetry(context.GetLogger(), attempt, exception.Message);
                });
    }

    private static bool IsConnectionError(NpgsqlException ex)
    {
        // Only connection-level errors are safe to retry
        return ex.SqlState switch
        {
            "57P03" => true,  // cannot_connect_now
            "08000" => true,  // connection_exception
            "08003" => true,  // connection_does_not_exist
            "08006" => true,  // connection_failure
            _ => false
        };
        // NOTE: serialization_failure (40001) and deadlock_detected (40P01)
        // are NOT retried here - they require application-level retry with
        // fresh transaction, handled by the caller if needed.
    }
}

// Usage - retry wraps connection acquisition, not the full operation
public sealed class ApplyEditsHandler
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ApplyEditsHandler> _logger;

    public async Task<ApplyEditsResponse> HandleAsync(ApplyEditsRequest request, CancellationToken ct)
    {
        // Retry only the connection acquisition
        await using var connection = await ResiliencePolicies.GetConnectionRetryPolicy()
            .ExecuteAsync(async () => await _dataSource.OpenConnectionAsync(ct));

        // Transaction operations are NOT retried - if they fail, we rollback and propagate
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            var results = await ExecuteEditsAsync(connection, transaction, request, ct);
            await transaction.CommitAsync(ct);
            return new ApplyEditsResponse(results);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw; // Propagate - caller decides whether to retry the whole operation
        }
    }
}
```

**Serialization/Deadlock Retry (Application Level):**

For `serialization_failure` (40001) and `deadlock_detected` (40P01), the entire operation should be retried at the application level with a fresh request, not within the handler. This ensures idempotency and prevents partial duplicate operations.

**Connection Lifecycle (Scoped per Request):**

```csharp
// Program.cs - DI registration
services.AddNpgsqlDataSource(connectionString);
services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();

// Each HTTP request gets its own UnitOfWork instance
// Connection opened lazily when transaction begins
// Disposed automatically at end of request scope
```

**Transaction Isolation Levels:**

| Operation | Isolation Level | Rationale |
|-----------|----------------|-----------|
| **Query (read)** | Read Committed | Default, no transaction needed |
| **ApplyEdits** | Read Committed | Standard for OLTP |
| **Bulk Import** | Read Committed | With batch inserts |
| **Schema changes** | Serializable | Rare, needs consistency |

**Deadlock Prevention:**

```csharp
// Always acquire locks in consistent order
public async Task ApplyEditsAsync(ApplyEditsRequest request, CancellationToken ct)
{
    // Sort operations by ObjectId to prevent deadlocks
    var sortedUpdates = request.Updates?.OrderBy(f => f.ObjectId).ToList();
    var sortedDeletes = request.Deletes?.OrderBy(id => id).ToList();

    // Process in order: adds, updates, deletes
    // Updates and deletes in ObjectId order
}
```

**Transaction Logging:**

```csharp
public static partial class Log
{
    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Debug,
        Message = "Transaction {TransactionId} started for layer {LayerId}")]
    public static partial void TransactionStarted(ILogger logger, string transactionId, string layerId);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Debug,
        Message = "Transaction {TransactionId} committed: {OperationCount} operations")]
    public static partial void TransactionCommitted(ILogger logger, string transactionId, int operationCount);

    [LoggerMessage(
        EventId = 2012,
        Level = LogLevel.Warning,
        Message = "Transaction {TransactionId} rolled back: {Reason}")]
    public static partial void TransactionRolledBack(ILogger logger, string transactionId, string reason);

    [LoggerMessage(
        EventId = 2013,
        Level = LogLevel.Warning,
        Message = "Transaction retry attempt {Attempt}: {ErrorMessage}")]
    public static partial void TransactionRetry(ILogger logger, int attempt, string errorMessage);
}
```

### Performance Targets

| Endpoint | p50 | p95 | p99 | Throughput |
|----------|-----|-----|-----|------------|
| **Health check** | < 1ms | < 5ms | < 10ms | 50k rps |
| **Layer metadata** | < 5ms | < 20ms | < 50ms | 10k rps |
| **Query (100 features)** | < 50ms | < 150ms | < 300ms | 1k rps |
| **Query (1000 features)** | < 150ms | < 400ms | < 800ms | 200 rps |
| **ApplyEdits (10 features)** | < 100ms | < 300ms | < 500ms | 500 rps |
| **MVT tile (z12)** | < 100ms | < 300ms | < 500ms | 500 rps |
| **Cold start (AOT)** | < 100ms | < 150ms | < 200ms | - |

### Benchmark Suite

```csharp
// benchmarks/Honua.Benchmarks/QueryBenchmarks.cs
[MemoryDiagnoser]          // Track allocations per operation
[ThreadingDiagnoser]       // Track thread pool usage
[SimpleJob(RuntimeMoniker.Net100)]
public class QueryBenchmarks
{
    [Benchmark]
    public async Task<QueryResponse> SimpleWhereQuery()
    {
        return await _handler.HandleAsync(_simpleQuery, CancellationToken.None);
    }

    [Benchmark]
    public async Task<QueryResponse> SpatialIntersectsQuery()
    {
        return await _handler.HandleAsync(_spatialQuery, CancellationToken.None);
    }

    [Benchmark]
    public async Task<QueryResponse> LargeResultSet_1000Features()
    {
        return await _handler.HandleAsync(_largeQuery, CancellationToken.None);
    }
}
```

### Memory Regression & Leak Detection (BenchmarkDotNet)

Use BenchmarkDotNet for allocations and a dedicated soak benchmark to detect heap growth across sustained operations. Treat heap delta as a metric and gate it in CI using the same benchmark regression tooling.

```csharp
// benchmarks/Honua.Benchmarks/MemorySoakBenchmarks.cs
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net100, warmupCount: 1, iterationCount: 1)]
public class MemorySoakBenchmarks
{
    [Benchmark]
    public async Task<long> Query_Soak_10k()
    {
        await WarmupAsync();

        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 10_000; i++)
        {
            await _handler.HandleAsync(_query, CancellationToken.None);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return GC.GetTotalMemory(forceFullCollection: true) - baseline;
    }
}
```

```yaml
# CI memory regression via BenchmarkDotNet
- name: Memory Soak Benchmarks
  run: |
    dotnet run --project benchmarks/Honua.Benchmarks \
      --configuration Release \
      --exporters json \
      -- --filter '*MemorySoak*' --join
```

### CI Performance Gate

```yaml
# .github/workflows/ci.yml
performance:
  needs: build
  runs-on: ubuntu-latest
  services:
    postgres:
      image: postgis/postgis:16-3.4
  steps:
    - name: Run Benchmarks
      run: |
        dotnet run --project benchmarks/Honua.Benchmarks \
          --configuration Release \
          --exporters json \
          -- --filter '*' --join

    - name: Compare Against Baseline
      run: |
        # Fail if time or allocation metrics regress >10% from baseline
        python scripts/check-perf-regression.py \
          --baseline .github/perf-baseline.json \
          --current BenchmarkDotNet.Artifacts/results.json \
          --threshold 0.10

    - name: Upload Results
      uses: actions/upload-artifact@v4
      with:
        name: benchmark-results
        path: BenchmarkDotNet.Artifacts/
```

### Docker Builds

```dockerfile
# docker/Dockerfile.aot (Production - Native AOT)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Honua.Server -c Release -o /app \
    --runtime linux-musl-x64 \
    -p:PublishAot=true \
    -p:StripSymbols=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["./Honua.Server"]
# Result: ~30-40MB image, <100ms cold start

# docker/Dockerfile (Development - JIT)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Honua.Server.dll"]
# Result: ~80-100MB image, faster builds
```

### Structured Logging (Serilog)

**Stack:**

```xml
<!-- Directory.Build.props -->
<ItemGroup>
  <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
  <PackageReference Include="Serilog.Enrichers.Environment" Version="2.*" />
  <PackageReference Include="Serilog.Enrichers.Thread" Version="3.*" />
  <PackageReference Include="Serilog.Enrichers.Span" Version="3.*" />
  <PackageReference Include="Serilog.Expressions" Version="4.*" />

  <!-- Sinks -->
  <PackageReference Include="Serilog.Sinks.Console" Version="5.*" />
  <PackageReference Include="Serilog.Sinks.OpenTelemetry" Version="1.*" />
</ItemGroup>
```

**Configuration:**

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithSpan()  // OpenTelemetry trace/span IDs
    .Enrich.WithProperty("Application", "Honua")
    .Enrich.WithProperty("Version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"));

var app = builder.Build();

// Request logging with structured properties
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        diagnosticContext.Set("Protocol", httpContext.Request.Protocol);

        if (httpContext.User.Identity?.IsAuthenticated == true)
            diagnosticContext.Set("UserId", httpContext.User.FindFirst("sub")?.Value);
    };

    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000}ms";
});
```

**appsettings.json:**

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning",
        "System.Net.Http.HttpClient": "Warning",
        "Npgsql": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact"
        }
      }
    ],
    "Filter": [
      {
        "Name": "ByExcluding",
        "Args": {
          "expression": "RequestPath like '/healthz%'"
        }
      }
    ]
  }
}
```

**Production (OpenTelemetry sink):**

```json
{
  "Serilog": {
    "WriteTo": [
      {
        "Name": "OpenTelemetry",
        "Args": {
          "endpoint": "http://otel-collector:4317",
          "protocol": "Grpc"
        }
      }
    ]
  }
}
```

**Structured Log Messages (with source generators for AOT):**

```csharp
// src/Honua.Server/Infrastructure/Logging/Log.cs
public static partial class Log
{
    // Query operations
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Query executed on layer {LayerId}: {FeatureCount} features returned in {ElapsedMs:F2}ms")]
    public static partial void QueryExecuted(
        ILogger logger, string layerId, int featureCount, double elapsedMs);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Spatial query on layer {LayerId}: {SpatialRel} with {GeometryType}, {FeatureCount} features")]
    public static partial void SpatialQueryExecuted(
        ILogger logger, string layerId, string spatialRel, string geometryType, int featureCount);

    // Edit operations
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "ApplyEdits on layer {LayerId}: +{Adds} ~{Updates} -{Deletes} in {ElapsedMs:F2}ms")]
    public static partial void ApplyEditsCompleted(
        ILogger logger, string layerId, int adds, int updates, int deletes, double elapsedMs);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "ApplyEdits partial failure on layer {LayerId}: {SuccessCount} succeeded, {FailureCount} failed")]
    public static partial void ApplyEditsPartialFailure(
        ILogger logger, string layerId, int successCount, int failureCount);

    // Errors
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Error,
        Message = "Query failed on layer {LayerId}: {ErrorMessage}")]
    public static partial void QueryFailed(
        ILogger logger, string layerId, string errorMessage, Exception? exception = null);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Error,
        Message = "Database connection failed: {ErrorMessage}")]
    public static partial void DatabaseConnectionFailed(
        ILogger logger, string errorMessage, Exception exception);

    // Performance warnings
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Slow query on layer {LayerId}: {ElapsedMs:F2}ms exceeded threshold of {ThresholdMs}ms")]
    public static partial void SlowQuery(
        ILogger logger, string layerId, double elapsedMs, int thresholdMs);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Large result set on layer {LayerId}: {FeatureCount} features, consider paging")]
    public static partial void LargeResultSet(
        ILogger logger, string layerId, int featureCount);
}
```

**Usage in Handlers:**

```csharp
public sealed class QueryHandler
{
    private readonly IFeatureStore _store;
    private readonly ILogger<QueryHandler> _logger;
    private readonly TimeProvider _time;

    public async Task<QueryResponse> HandleAsync(QueryRequest request, CancellationToken ct)
    {
        var startTime = _time.GetTimestamp();

        try
        {
            var result = await _store.QueryAsync(request.LayerId, request.ToQuery(), ct);
            var elapsedMs = _time.GetElapsedTime(startTime).TotalMilliseconds;

            Log.QueryExecuted(_logger, request.LayerId, result.Features.Count, elapsedMs);

            if (elapsedMs > 1000)
                Log.SlowQuery(_logger, request.LayerId, elapsedMs, 1000);

            if (result.Features.Count > 5000)
                Log.LargeResultSet(_logger, request.LayerId, result.Features.Count);

            return result;
        }
        catch (Exception ex)
        {
            Log.QueryFailed(_logger, request.LayerId, ex.Message, ex);
            throw;
        }
    }
}
```

**Correlation ID Middleware:**

```csharp
// Propagate or create correlation ID for request tracing
public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private const string CorrelationIdHeader = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.Id
            ?? Guid.NewGuid().ToString("N");

        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
```

**Log Output (Compact JSON):**

```json
{"@t":"2025-12-16T10:30:45.123Z","@mt":"Query executed on layer {LayerId}: {FeatureCount} features returned in {ElapsedMs:F2}ms","LayerId":"parcels","FeatureCount":847,"ElapsedMs":42.31,"CorrelationId":"abc123","SpanId":"def456","TraceId":"789xyz","Application":"Honua","Version":"1.0.0"}
```

**Event ID Ranges:**

| Range | Category |
|-------|----------|
| 1000-1999 | Query operations |
| 2000-2999 | Edit operations |
| 3000-3999 | Performance warnings |
| 4000-4999 | Security/Auth events |
| 5000-5999 | Errors |
| 6000-6999 | Admin operations |
| 7000-7999 | System lifecycle |

### .NET Aspire (Local Dev & Observability Foundation)

**Why Aspire in MVP:**
- Single `F5` to start Honua + PostgreSQL + Redis (optional)
- Built-in dashboard showing traces, logs, metrics, health
- OpenTelemetry configured automatically (foundation for production observability later)
- Service discovery for local multi-service scenarios
- No production dependency - Aspire is dev-time only

#### AppHost Configuration

```csharp
// src/Honua.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with PostGIS
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis", "16-3.4")
    .WithDataVolume("honua-postgres-data")
    .WithPgAdmin();

var db = postgres.AddDatabase("honua");

// Optional Redis for caching
var redis = builder.AddRedis("redis")
    .WithRedisCommander();

// Honua Server
var honua = builder.AddProject<Projects.Honua_Server>("honua-server")
    .WithReference(db)
    .WithReference(redis)
    .WaitFor(db);

builder.Build().Run();
```

#### ServiceDefaults (Shared Config)

```csharp
// src/Honua.ServiceDefaults/Extensions.cs
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // OpenTelemetry (traces, metrics, logs)
        builder.ConfigureOpenTelemetry();

        // Health checks
        builder.AddDefaultHealthChecks();

        // Service discovery
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Honua.Server"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsql());

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlp = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlp)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }
}
```

#### Server Integration

```csharp
// src/Honua.Server/Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add Aspire service defaults (OTel, health, resilience)
builder.AddServiceDefaults();

// Add Npgsql with connection from Aspire
builder.AddNpgsqlDataSource("honua");

// Add Redis if configured
builder.AddRedisDistributedCache("redis");

// ... rest of app config

var app = builder.Build();

// Map health endpoints for Aspire dashboard
app.MapDefaultEndpoints();
```

#### Local Development Workflow

```bash
# Start everything with Aspire dashboard
cd src/Honua.AppHost
dotnet run

# Opens:
# - Aspire Dashboard: https://localhost:17225
# - Honua Server: https://localhost:7xxx
# - PostgreSQL: localhost:5432
# - pgAdmin: http://localhost:5050
# - Redis Commander: http://localhost:8081 (if Redis enabled)

# Dashboard shows:
# - All services and their health status
# - Structured logs from all services
# - Distributed traces across requests
# - Metrics (request rate, latency, errors)
# - Environment variables and endpoints
```

#### Production (No Aspire Runtime)

Aspire is dev-time only. In production:
- OTel configured via `OTEL_EXPORTER_OTLP_ENDPOINT` env var
- Connection strings via standard `ConnectionStrings__*` env vars
- No Aspire packages in published container

```dockerfile
# Production Dockerfile - no Aspire runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# ... only Honua.Server, no AppHost
```

### Performance Checklist

- [ ] All DTOs have `[JsonSerializable]` attributes
- [ ] All log statements use `[LoggerMessage]` source generator
- [ ] No reflection in hot paths
- [ ] No LINQ allocations in hot paths (use `foreach`)
- [ ] Large collections streamed, not buffered
- [ ] Connection pooling configured and tuned
- [ ] Response compression enabled
- [ ] Output caching for metadata endpoints
- [ ] Benchmarks pass CI gate
- [ ] Allocation budgets enforced via BenchmarkDotNet MemoryDiagnoser
- [ ] AOT build tested for all endpoints
- [ ] Cold start < 100ms verified

### Performance Validation (Beyond Microbenchmarks)

- Maintain a baseline dataset for perf testing (schema + seed data) and keep it versioned
- For new or modified SQL paths, capture `EXPLAIN (ANALYZE, BUFFERS)` and verify index usage
- Run load tests (k6/wrk/nbomber) on critical endpoints nightly or before release; gate >10% regression
- Run soak benchmarks with BenchmarkDotNet (memory + heap delta) and track connection pools to catch leaks early

---

## Quality Guardrails

### Build & Test Parallelism

**Goal:** < 5 minute CI feedback loop, even at 100+ test files.

#### Build Optimization

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <!-- Parallel compilation -->
  <BuildInParallel>true</BuildInParallel>
  <MaxCpuCount>0</MaxCpuCount>  <!-- Use all cores -->

  <!-- Incremental builds -->
  <ProduceReferenceAssembly>true</ProduceReferenceAssembly>

  <!-- Deterministic for caching -->
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

```bash
# Fast local builds
dotnet build --no-restore -maxcpucount

# CI with binary logging for analysis
dotnet build -bl:build.binlog -maxcpucount
```

#### Test Parallelism Strategy

```csharp
// xunit.runner.json - maximize parallelism
{
  "parallelizeAssembly": true,
  "parallelizeTestCollections": true,
  "maxParallelThreads": -1,  // Unlimited (uses all cores)
  "parallelAlgorithm": "aggressive"
}
```

**Test Collection Strategy:**

| Collection Type | Parallelism | Use For |
|----------------|-------------|---------|
| **No collection** | Full parallel | Pure unit tests, no shared state |
| **[Collection("Database")]** | Serial within, parallel across | Tests sharing a Testcontainer |
| **[Collection("Sequential")]** | Fully serial | Port conflicts, global state |

```csharp
// Shared Testcontainer across test classes (fast)
[CollectionDefinition("Database")]
public class DatabaseCollection : ICollectionFixture<PostgresFixture> { }

[Collection("Database")]
public class QueryTests
{
    private readonly PostgresFixture _db;
    public QueryTests(PostgresFixture db) => _db = db;

    // Tests run in parallel, share the container
}

// PostgresFixture spins up ONCE, reused by all [Collection("Database")] tests
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("honua_test")
        .WithReuse(true)  // Reuse across test runs (Testcontainers 3.x)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyMigrations();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
```

#### Test Data Isolation (Parallel-Safe)

```csharp
// Each test gets unique data, no conflicts
public class QueryTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private string _testLayerId = null!;

    public async Task InitializeAsync()
    {
        // Create unique layer for this test class
        _testLayerId = $"test_layer_{Guid.NewGuid():N}";
        await _db.CreateTestLayer(_testLayerId);
    }

    public async Task DisposeAsync()
    {
        await _db.DeleteTestLayer(_testLayerId);
    }

    [Fact]
    public async Task Query_WithWhere_ReturnsFiltered()
    {
        // Uses _testLayerId - isolated from other parallel tests
    }
}
```

#### CI Job Parallelism

```yaml
# .github/workflows/ci.yml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
          cache: true  # Cache NuGet packages

      - name: Restore
        run: dotnet restore --locked-mode

      - name: Build
        run: dotnet build -c Release --no-restore -maxcpucount

      - name: Upload Build Artifacts
        uses: actions/upload-artifact@v4
        with:
          name: build
          path: |
            **/bin/Release/**/
            !**/obj/

  # Tests run in parallel matrix
  test:
    needs: build
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        test-project:
          - Honua.Core.Tests
          - Honua.Server.Tests/FeatureServer
          - Honua.Server.Tests/OgcFeatures
          - Honua.Server.Tests/Admin
          - Honua.Server.Tests/Performance
    services:
      postgres:
        image: postgis/postgis:16-3.4
        env:
          POSTGRES_PASSWORD: test
        ports:
          - 5432:5432
        options: >-
          --health-cmd pg_isready
          --health-interval 5s
          --health-timeout 5s
          --health-retries 5
    steps:
      - uses: actions/checkout@v4

      - name: Download Build
        uses: actions/download-artifact@v4
        with:
          name: build

      - name: Run Tests - ${{ matrix.test-project }}
        run: |
          dotnet test tests/${{ matrix.test-project }} \
            --no-build -c Release \
            --logger "trx;LogFileName=${{ matrix.test-project }}.trx" \
            -- RunConfiguration.MaxCpuCount=0

  # AOT and benchmarks in parallel with tests
  aot-build:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet publish src/Honua.Server -c Release -p:PublishAot=true

  benchmarks:
    needs: build
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgis/postgis:16-3.4
    steps:
      - uses: actions/checkout@v4
      - run: dotnet run --project benchmarks/Honua.Benchmarks -c Release
```

#### CI Timing Targets

| Stage | Target | If Exceeded |
|-------|--------|-------------|
| **Restore** | < 30s | Check NuGet cache, locked mode |
| **Build** | < 60s | Check incremental, parallelism |
| **Unit Tests** | < 60s | Check test isolation |
| **Integration Tests** | < 3min | Check container reuse, parallelism |
| **AOT Build** | < 2min | Acceptable (AOT is slow) |
| **Benchmarks** | < 2min | Reduce iterations if needed |
| **Total CI** | < 5min | Split more jobs |

#### Local Development Speed

```bash
# Watch mode for instant feedback
dotnet watch test --project tests/Honua.Core.Tests

# Run specific test with filter
dotnet test --filter "FullyQualifiedName~QueryTests"

# Skip slow tests locally
dotnet test --filter "Category!=Slow"

# Reuse Testcontainers across runs (Ryuk disabled)
export TESTCONTAINERS_RYUK_DISABLED=true
dotnet test  # Container survives, next run faster
```

#### Test Organization & Filtering

**Folder Structure (mirrors src/):**

```
tests/
├── Honua.Core.Tests/                    # Unit tests (no I/O)
│   ├── Query/
│   │   ├── CqlFilterParserTests.cs
│   │   ├── ODataFilterParserTests.cs
│   │   └── SqlFilterTranslatorTests.cs
│   ├── Geometry/
│   │   └── EnvelopeTests.cs
│   └── Models/
│       └── FeatureRecordTests.cs
│
├── Honua.Server.Tests/                  # Integration tests
│   ├── FeatureServer/
│   │   ├── QueryEndpointTests.cs
│   │   ├── ApplyEditsEndpointTests.cs
│   │   └── AttachmentEndpointTests.cs
│   ├── OgcFeatures/
│   │   ├── CollectionsEndpointTests.cs
│   │   ├── ItemsEndpointTests.cs
│   │   └── TransactionsEndpointTests.cs
│   ├── OData/
│   │   └── ODataQueryEndpointTests.cs
│   ├── Admin/
│   │   └── LayerManagementTests.cs
│   ├── Performance/
│   │   ├── SoakTests.cs
│   │   └── ConcurrencyTests.cs
│   ├── Conformance/                     # Protocol conformance
│   │   ├── GeoServicesCompatibilityTests.cs
│   │   └── OgcCiteTests.cs
│   └── Fixtures/
│       ├── PostgresFixture.cs
│       ├── TestDataBuilder.cs
│       └── WebAppFixture.cs
│
└── Honua.Architecture.Tests/            # Architecture enforcement
    └── DependencyTests.cs
```

**Custom Trait Attributes:**

```csharp
// tests/Honua.TestKit/Attributes.cs

/// <summary>Unit test - no I/O, runs in milliseconds</summary>
public class UnitTestAttribute : FactAttribute { }

/// <summary>Integration test - requires database</summary>
[AttributeUsage(AttributeTargets.Method)]
public class IntegrationTestAttribute : FactAttribute
{
    public IntegrationTestAttribute()
    {
        Traits.Add("Category", "Integration");
    }
}

/// <summary>Slow test - runs in CI only by default</summary>
[AttributeUsage(AttributeTargets.Method)]
public class SlowTestAttribute : FactAttribute
{
    public SlowTestAttribute()
    {
        Traits.Add("Category", "Slow");
    }
}

/// <summary>Tests a specific protocol</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class ProtocolAttribute : Attribute, ITraitAttribute
{
    public ProtocolAttribute(string protocol) => Protocol = protocol;
    public string Protocol { get; }

    public IEnumerable<KeyValuePair<string, string>> GetTraits()
    {
        yield return new("Protocol", Protocol);
    }
}

public static class Protocols
{
    public const string FeatureServer = "FeatureServer";
    public const string OgcFeatures = "OgcFeatures";
    public const string OData = "OData";
    public const string Tiles = "Tiles";
}

/// <summary>Tests a specific operation type</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class OperationAttribute : Attribute, ITraitAttribute
{
    public OperationAttribute(string operation) => Operation = operation;
    public string Operation { get; }

    public IEnumerable<KeyValuePair<string, string>> GetTraits()
    {
        yield return new("Operation", Operation);
    }
}

public static class Operations
{
    public const string Query = "Query";
    public const string Create = "Create";
    public const string Update = "Update";
    public const string Delete = "Delete";
    public const string Attachments = "Attachments";
    public const string Metadata = "Metadata";
}

/// <summary>Conformance test against external specification</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class ConformanceAttribute : Attribute, ITraitAttribute
{
    public ConformanceAttribute(string spec) => Spec = spec;
    public string Spec { get; }

    public IEnumerable<KeyValuePair<string, string>> GetTraits()
    {
        yield return new("Category", "Conformance");
        yield return new("Spec", Spec);
    }
}

public static class Specs
{
    public const string GeoServicesFeatureServer = "GeoServices:FeatureServer";
    public const string OgcApiFeatures = "OGC:API-Features";
    public const string OgcCql2 = "OGC:CQL2";
    public const string ODataV4 = "OData:v4";
}

/// <summary>Architecture test - validates code structure</summary>
public class ArchitectureTestAttribute : FactAttribute
{
    public ArchitectureTestAttribute()
    {
        Traits.Add("Category", "Architecture");
    }
}
```

**Example Test Class:**

```csharp
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _app;
    private readonly PostgresFixture _db;
    private string _layerId = null!;

    public QueryEndpointTests(WebAppFixture app, PostgresFixture db)
    {
        _app = app;
        _db = db;
    }

    public async Task InitializeAsync()
    {
        _layerId = await _db.CreateTestLayer(featureCount: 100);
    }

    public async Task DisposeAsync()
    {
        await _db.DeleteTestLayer(_layerId);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        var client = _app.CreateClient();
        var response = await client.GetAsync(
            $"/rest/services/test/FeatureServer/0/query?where=population>1000");

        response.Should().Be200Ok();
        var result = await response.Content.ReadFromJsonAsync<QueryResponse>();
        result!.Features.Should().AllSatisfy(f =>
            f.Attributes["population"].Should().BeGreaterThan(1000));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task Query_WithSpatialFilter_ReturnsIntersectingFeatures()
    {
        // ...
    }

    [SlowTest]
    [Operation(Operations.Query)]
    public async Task Query_LargeResultSet_StreamsWithoutTimeout()
    {
        // Takes 30+ seconds, skipped locally
    }

    [IntegrationTest]
    [Conformance(Specs.GeoServicesFeatureServer)]
    public async Task Query_MatchesGeoServicesResponseFormat()
    {
        // Validates GeoServices JSON structure
    }
}
```

**Filter Commands:**

```bash
# By category
dotnet test --filter "Category=Integration"
dotnet test --filter "Category!=Slow"              # Skip slow tests
dotnet test --filter "Category=Conformance"

# By protocol
dotnet test --filter "Protocol=FeatureServer"
dotnet test --filter "Protocol=OgcFeatures"
dotnet test --filter "Protocol=OData"

# By operation
dotnet test --filter "Operation=Query"
dotnet test --filter "Operation=Create|Operation=Update|Operation=Delete"

# By spec conformance
dotnet test --filter "Spec=GeoServices:FeatureServer"
dotnet test --filter "Spec=OGC:API-Features"

# Combinations
dotnet test --filter "Protocol=FeatureServer&Operation=Query"
dotnet test --filter "Category=Integration&Protocol=OgcFeatures"

# Local dev (fast feedback)
dotnet test --filter "Category!=Slow&Category!=Conformance"

# CI: all integration tests for a protocol
dotnet test --filter "Category=Integration&Protocol=FeatureServer"
```

**CI Matrix by Protocol:**

```yaml
test:
  strategy:
    matrix:
      include:
        - name: Unit Tests
          filter: "Category!=Integration&Category!=Slow"
          needs-db: false

        - name: FeatureServer
          filter: "Protocol=FeatureServer"
          needs-db: true

        - name: OGC API Features
          filter: "Protocol=OgcFeatures"
          needs-db: true

        - name: OData
          filter: "Protocol=OData"
          needs-db: true

        - name: Conformance
          filter: "Category=Conformance"
          needs-db: true

        - name: Slow/Soak
          filter: "Category=Slow"
          needs-db: true
```

**Naming Conventions:**

```csharp
// Pattern: MethodUnderTest_Scenario_ExpectedBehavior
[Fact] public void Parse_ValidCql_ReturnsAst() { }
[Fact] public void Parse_InvalidSyntax_ThrowsParseException() { }
[Fact] public async Task Query_WithBbox_ReturnsIntersectingFeatures() { }
[Fact] public async Task Query_ExceedsLimit_SetsTransferLimitFlag() { }
[Fact] public async Task ApplyEdits_MixedOperations_CommitsAtomically() { }
[Fact] public async Task ApplyEdits_PartialFailure_RollsBack() { }
```

### CI Pipeline Gates (All Required to Merge)

```yaml
# .github/workflows/ci.yml structure

jobs:
  build:
    steps:
      - name: Instruction Sync
        run: bash scripts/check-instructions-sync.sh

      - name: Build
        run: dotnet build --configuration Release --warnaserror

      - name: Format Check
        run: dotnet format --verify-no-changes

      - name: Analyzers
        # Roslynator, nullable, etc. - built into build step

  test:
    needs: build
    services:
      postgres:
        image: postgis/postgis:16-3.4
    steps:
      - name: Run Tests
        run: dotnet test --configuration Release --collect:"XPlat Code Coverage"

      - name: Coverage Gate
        run: |
          # Fail if coverage < 80%
          reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage
          # Parse and gate

  quality:
    needs: build
    steps:
      - name: Architecture Tests
        run: dotnet test --filter "Category=Architecture"

      - name: Dependency Vulnerability Scan
        run: dotnet list package --vulnerable --include-transitive

      - name: Security Scan
        uses: github/codeql-action/analyze@v3

  performance:
    needs: build
    services:
      postgres:
        image: postgis/postgis:16-3.4
    steps:
      - name: Run Benchmarks
        run: dotnet run --project benchmarks/Honua.Benchmarks -c Release

      - name: Performance Regression Gate
        run: python scripts/check-perf-regression.py --threshold 0.10

  aot-build:
    needs: build
    steps:
      - name: Build Native AOT
        run: dotnet publish src/Honua.Server -c Release -p:PublishAot=true

      - name: Verify AOT Binary
        run: |
          # Ensure no trim warnings
          # Verify binary size < 50MB
          # Smoke test startup time < 100ms
```

### Change-Level Definition of Done (Every PR)

- Tests updated or added (integration-first)
- Coverage does not fall below the current phase checkpoint
- Format, analyzers, architecture tests, and instruction sync all pass
- Security checks pass for modified surface area
- Docs updated when behavior or configuration changes

### Instruction Parity (Claude/Codex)

- `CODEX.md` must match `CLAUDE.md`
- `.codex/` must mirror `.claude/` for settings and guidance
- CI enforces sync to prevent drift

### Architecture Guardrails

- NetArchTest rules enforce vertical slice boundaries and dependency limits
- Dependency limits: max 5 dependencies per endpoint, max 4 per handler
- Architecture tests run in CI and block merges on violation

### Security Baseline (Always-On)

- SAST: CodeQL on every PR
- SCA: `dotnet list package --vulnerable --include-transitive` gate for high/critical issues
- Secrets: GitHub secret scanning enabled; local `gitleaks` optional for releases
- HTTP security headers configured for API + admin UI
- Security regression tests cover injection, path traversal, and XSS

### Code Quality Standards

| Standard | Enforcement |
|----------|-------------|
| **Warnings as Errors** | `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` |
| **Nullable** | `<Nullable>enable</Nullable>` |
| **Implicit Usings** | `<ImplicitUsings>enable</ImplicitUsings>` |
| **Analysis Level** | `<AnalysisLevel>latest-all</AnalysisLevel>` |
| **Analysis Mode** | `<AnalysisMode>All</AnalysisMode>` |
| **Format** | `dotnet format` with `.editorconfig` |

### Analyzer Configuration

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <AnalysisLevel>latest-all</AnalysisLevel>
  <AnalysisMode>All</AnalysisMode>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
</PropertyGroup>

<ItemGroup>
  <!-- Roslynator - comprehensive C# analysis -->
  <PackageReference Include="Roslynator.Analyzers" Version="4.*" PrivateAssets="all" />
  <PackageReference Include="Roslynator.CodeAnalysis.Analyzers" Version="4.*" PrivateAssets="all" />
  <PackageReference Include="Roslynator.Formatting.Analyzers" Version="4.*" PrivateAssets="all" />

  <!-- Meziantou - strict coding standards -->
  <PackageReference Include="Meziantou.Analyzer" Version="2.*" PrivateAssets="all" />

  <!-- Async best practices -->
  <PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers" Version="17.*" PrivateAssets="all" />
  <PackageReference Include="AsyncFixer" Version="1.*" PrivateAssets="all" />

  <!-- IDisposable correctness -->
  <PackageReference Include="IDisposableAnalyzers" Version="4.*" PrivateAssets="all" />

  <!-- Security -->
  <PackageReference Include="SecurityCodeScan.VS2019" Version="5.*" PrivateAssets="all" />

  <!-- AOT/Trimming compatibility -->
  <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="9.*" />
</ItemGroup>
```

### Analyzer Categories

| Analyzer | Focus | Key Rules |
|----------|-------|-----------|
| **NetAnalyzers** | .NET best practices | CA1xxx (design), CA2xxx (security) |
| **Roslynator** | C# idioms | RCS1xxx (simplification, formatting) |
| **Meziantou** | Strict standards | MA0xxx (async, strings, performance) |
| **Threading.Analyzers** | Async/await | VSTHRD1xx (deadlocks, ConfigureAwait) |
| **AsyncFixer** | Async patterns | AsyncFixer01-05 (common mistakes) |
| **IDisposableAnalyzers** | Resource leaks | IDISP001-025 (dispose patterns) |
| **SecurityCodeScan** | Vulnerabilities | SCS0xxx (SQL injection, XSS, etc.) |

### Critical Rules (Errors)

```ini
# .editorconfig - rules that MUST be errors
dotnet_diagnostic.CA2007.severity = none          # ConfigureAwait not needed in ASP.NET
dotnet_diagnostic.CA1062.severity = error         # Validate arguments
dotnet_diagnostic.CA2100.severity = error         # SQL injection
dotnet_diagnostic.CA2301.severity = error         # BinaryFormatter deserialization
dotnet_diagnostic.CA3001.severity = error         # SQL injection
dotnet_diagnostic.CA3003.severity = error         # File path injection
dotnet_diagnostic.CA5350.severity = error         # Weak crypto
dotnet_diagnostic.CA5351.severity = error         # Broken crypto

# Async errors
dotnet_diagnostic.VSTHRD002.severity = error      # Avoid problematic sync waits
dotnet_diagnostic.VSTHRD103.severity = error      # Call async methods correctly
dotnet_diagnostic.VSTHRD110.severity = error      # Observe awaited tasks

# Dispose errors
dotnet_diagnostic.IDISP001.severity = error       # Dispose created
dotnet_diagnostic.IDISP004.severity = error       # Don't ignore return value of dispose
dotnet_diagnostic.IDISP007.severity = error       # Don't dispose injected

# Security errors
dotnet_diagnostic.SCS0001.severity = error        # Command injection
dotnet_diagnostic.SCS0002.severity = error        # SQL injection
dotnet_diagnostic.SCS0005.severity = error        # Weak random
dotnet_diagnostic.SCS0018.severity = error        # Path traversal
dotnet_diagnostic.SCS0029.severity = error        # XSS

# AOT compatibility
dotnet_diagnostic.IL2026.severity = error         # RequiresUnreferencedCode
dotnet_diagnostic.IL2046.severity = error         # RequiresDynamicCode
dotnet_diagnostic.IL2072.severity = error         # Trim warnings
```

### Rules to Suppress (With Justification)

```ini
# .editorconfig - intentional suppressions
dotnet_diagnostic.CA1848.severity = none          # LoggerMessage perf - we use source gen
dotnet_diagnostic.CA2234.severity = none          # Pass Uri - we use strings for GeoServices compat
dotnet_diagnostic.MA0004.severity = none          # Use ConfigureAwait - not needed in ASP.NET
dotnet_diagnostic.RCS1090.severity = suggestion   # ConfigureAwait - not needed in ASP.NET
```

### .editorconfig (Key Rules)

```ini
[*.cs]
# Formatting
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

# Naming
dotnet_naming_style.pascal_case.capitalization = pascal_case
dotnet_naming_style.camel_case.capitalization = camel_case

# Code style
csharp_style_var_for_built_in_types = true
csharp_style_var_when_type_is_apparent = true
csharp_prefer_simple_using_statement = true
csharp_style_expression_bodied_methods = when_on_single_line

# Severity
dotnet_analyzer_diagnostic.severity = warning
dotnet_diagnostic.CA1062.severity = error  # Validate arguments
dotnet_diagnostic.CA2007.severity = none   # ConfigureAwait (not needed in ASP.NET)
```

### Testing Strategy

```
┌─────────────────────────────────────────────────────────────────┐
│  TESTING PYRAMID (Integration-First)                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  E2E / Contract Tests                                    │   │
│  │  - OGC API Features conformance                          │   │
│  │  - GeoServices FeatureServer compatibility                      │   │
│  │  - ~10% of tests                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Integration Tests (PRIMARY)                             │   │
│  │  - Real PostgreSQL via Testcontainers                    │   │
│  │  - Full endpoint → database → response                   │   │
│  │  - ~70% of tests                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Unit Tests                                              │   │
│  │  - Query builders, parsers, geometry operations          │   │
│  │  - Pure functions, no I/O                                │   │
│  │  - ~20% of tests                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Coverage Requirements

| Metric | Minimum | Target |
|--------|---------|--------|
| **Line Coverage** | 80% | 90% |
| **Branch Coverage** | 70% | 85% |

**Staged enforcement:** CI uses a temporary 40% line / 30% branch gate during Phase 0-1 to avoid slowing iteration, with a return to 80%/70% by Phase 3.
| **Critical Paths** | 95% | 100% |

Critical paths include:
- Query parsing and execution
- ApplyEdits transaction handling
- Authentication and authorization
- Error response formatting

---

## Implementation Phases

### Phase 0: Foundation

**Goal:** Repository setup with all guardrails, empty but deployable.

| Task | Deliverable |
|------|-------------|
| Create repo with solution structure | `Honua.sln` with all projects |
| Configure CI/CD pipeline | GitHub Actions with all gates |
| Docker setup | Multi-stage Dockerfile, compose for dev |
| Database schema v1 | Migrations for layers, metadata |
| Test harness | Testcontainers fixture, first passing test |
| Basic health endpoint | `/healthz/live`, `/healthz/ready` |

**Exit Criteria:**
- [ ] `dotnet build` passes with warnings-as-errors
- [ ] `dotnet format --verify-no-changes` passes
- [ ] One integration test runs against real PostgreSQL
- [ ] Docker image builds and starts
- [ ] CI pipeline runs and passes
- [ ] **Coverage checkpoint:** Staged CI threshold met (40% line / 30% branch)

### Phase 1: FeatureServer Query

**Goal:** Read-only FeatureServer query endpoint, production-quality.

| Task | Deliverable |
|------|-------------|
| Layer metadata API | List layers, layer details |
| Query endpoint | `/FeatureServer/{layerId}/query` |
| Query parsing | `where`, `outFields`, `geometry`, `spatialRel` |
| Paging | `resultOffset`, `resultRecordCount`, `exceededTransferLimit` |
| Output formats | GeoServices JSON, GeoJSON |
| Error handling | GeoServices-compatible error responses |

**Exit Criteria:**
- [ ] Query with `where` clause returns filtered results
- [ ] Spatial queries (bbox, intersects) work
- [ ] Paging returns correct pages
- [ ] Error responses match GeoServices format
- [ ] esri-leaflet FeatureLayer smoke tests pass against real ArcGIS Server and Honua
- [ ] 80%+ coverage on query paths
- [ ] **Coverage checkpoint:** 85%+ line coverage on `Honua.Core`, 80%+ on `Features/Query/`

### Phase 2: FeatureServer Editing

**Goal:** Full CRUD via applyEdits endpoint.

| Task | Deliverable |
|------|-------------|
| ApplyEdits endpoint | `/FeatureServer/{layerId}/applyEdits` |
| Add features | Insert with geometry, attributes |
| Update features | Update by objectId |
| Delete features | Delete by objectId |
| Attachments | Add, query, update, delete |
| Related records | queryRelatedRecords |

**Exit Criteria:**
- [ ] Add/update/delete features via applyEdits
- [ ] Attachments CRUD works
- [ ] Related records query works
- [ ] Transaction rollback on partial failure
- [ ] 80%+ coverage on editing paths
- [ ] **Coverage checkpoint:** 80%+ on `Features/Edit/`, 75%+ cumulative on `Honua.Server`

### Phase 3: OGC API Features

**Goal:** OGC API Features Core + Transactions conformance.

| Task | Deliverable |
|------|-------------|
| Landing page | `/ogc/features` with links |
| Conformance | `/ogc/features/conformance` |
| Collections | List and single collection |
| Items | GET with bbox, filter, limit |
| Transactions | POST/PUT/DELETE features |
| Content negotiation | JSON, GeoJSON, HTML |

**Exit Criteria:**
- [ ] OGC CITE Team Engine tests pass (OGC API Features Core conformance)
- [ ] OGC CITE Team Engine tests pass (OGC API Features Transactions, if available)
- [ ] Transactions work (create, replace, delete)
- [ ] Content negotiation works
- [ ] CQL filter support (basic)
- [ ] 80%+ coverage on OGC paths
- [ ] **Coverage checkpoint:** 80%+ on `Features/OgcFeatures/`, 78%+ cumulative on `Honua.Server`

### Phase 3.25: Vector Tiles (MVT)

**Goal:** MVT tile endpoint + TileJSON for MapLibre clients.

| Task | Deliverable |
|------|-------------|
| Tile endpoint | MVT tiles from PostGIS (`ST_AsMVT`) |
| TileJSON | Metadata endpoint for client discovery |
| Layer config | Field selection, geometry clipping, bounds |
| Caching headers | `Cache-Control` for tiles |
| Tests | Basic tile endpoint tests |

**Exit Criteria:**
- [ ] MVT endpoint returns valid tiles for a published layer
- [ ] TileJSON loads in MapLibre with correct bounds/metadata
- [ ] Cache headers set for tiles
- [ ] **Coverage checkpoint:** 75%+ on `Features/VectorTiles/`, 78%+ cumulative maintained

### Phase 3.5: OData v4

**Goal:** Full OData v4 for Excel/Power BI integration with spatial queries and CRUD operations.

| Task | Deliverable |
|------|-------------|
| OData query parser | `$filter`, `$select`, `$top`, `$skip`, `$orderby`, `$count` |
| Spatial functions | `geo.distance()`, `geo.intersects()`, `geo.length()` |
| Metadata endpoint | `/$metadata` CSDL generation |
| Features endpoint | `/odata/v4/Layers('{id}')/Features` |
| Filter translation | OData filter → SQL WHERE (including spatial → PostGIS) |
| Paging | `@odata.nextLink` support |
| CRUD operations | POST (create), PATCH (update), DELETE |

**Exit Criteria:**
- [ ] Excel `=OData.Feed()` connects and queries
- [ ] Power BI auto-discovers schema from `$metadata`
- [ ] `$filter` with eq/ne/gt/lt/contains works
- [ ] `geo.distance` and `geo.intersects` work
- [ ] `$select` returns only requested fields
- [ ] POST creates feature, returns 201 with Location header
- [ ] PATCH updates feature attributes/geometry
- [ ] DELETE removes feature, returns 204
- [ ] 80%+ coverage on OData paths
- [ ] **Coverage checkpoint:** 80%+ on `Features/OData/`, 80%+ cumulative on `Honua.Server`

**Deferred to GA:** OData `/$batch` endpoint for multi-operation requests.

### Phase 4: Admin UI + File Import

**Goal:** Blazor WASM admin interface and file import for MVP operations.

| Task | Deliverable |
|------|-------------|
| Blazor project setup | WASM project, API integration |
| Admin UI authentication | OIDC PKCE for browser UI (API key is automation-only) |
| Connection management | Add/test PostGIS connections |
| Table discovery | List tables/views with geometry, PK detection, row-count estimate |
| Layer publishing | Create layer from table |
| Service management | Enable/disable layers |
| GeoServices Import Wizard | Parse GeoServices service URL, import |
| **File Import** | GeoJSON, Shapefile, GeoPackage, CSV, KML readers |
| **CRS Detection** | Auto-detect from .prj, GeoPackage metadata |
| **Reprojection** | PostGIS ST_Transform, any EPSG code |
| Health dashboard | Service status, metrics |
| Map preview | MapLibre preview for published layers |
| Style editor | Embedded Maputnik for MapLibre styles |

**Exit Criteria:**
- [ ] Can add PostGIS connection and discover tables
- [ ] Table discovery excludes system tables, includes geometry type, SRID, PK detection, and row-count estimate
- [ ] Can publish table as FeatureServer layer
- [ ] Can import layer from GeoServices service URL
- [ ] Can import GeoJSON/Shapefile/GeoPackage/CSV/KML files
- [ ] CRS auto-detected or manually specified
- [ ] Can enable/disable services
- [ ] Disabled layers return 404 on all endpoints; bulk enable/disable supported
- [ ] Basic health status visible
- [ ] Map preview renders MVT tiles
- [ ] Styles can be edited and saved via Maputnik
- [ ] Admin UI authenticates via OIDC; browser clients do not use API keys
- [ ] **Coverage checkpoint:** 70%+ on `Honua.Admin` (Blazor), 75%+ on `Features/Admin/` API, 80%+ cumulative maintained

### Phase 4.5: Deployment Templates

**Goal:** Production-ready deployment options for Kubernetes plus AWS and Azure.

| Task | Deliverable |
|------|-------------|
| **Helm chart** | Complete K8s deployment with configurable values |
| **AWS Terraform** | ECS/Fargate module with RDS PostgreSQL, ALB, secrets |
| **Azure Terraform** | Container Apps module with Azure Database for PostgreSQL |
| **Examples** | Complete working examples for AWS and Azure |
| **Documentation** | Step-by-step deployment guides for each platform |

**Helm Chart Features:**
- Deployment, Service, Ingress, ConfigMap, Secret templates
- Horizontal Pod Autoscaler (HPA) support
- PostgreSQL subchart option (Bitnami) for dev/test
- External PostgreSQL connection for production
- Redis subchart option for caching
- Configurable resource limits and requests
- Health check probes (liveness, readiness)
- OIDC configuration via values

**Terraform Module Features:**
- VPC/network setup (or use existing)
- Managed PostgreSQL with PostGIS
- Container service (ECS/Container Apps)
- Load balancer with TLS termination
- Secrets management (Secrets Manager/Key Vault)
- IAM/RBAC for least-privilege access
- Optional Redis for caching
- Outputs for connection strings and endpoints

**Exit Criteria:**
- [ ] Helm chart installs successfully on fresh K8s cluster
- [ ] `helm test` passes (connectivity, health checks)
- [ ] AWS Terraform applies cleanly, Honua accessible via ALB
- [ ] Azure Terraform applies cleanly, Honua accessible via Container Apps endpoint
- [ ] Each deployment connects to managed PostgreSQL with PostGIS
- [ ] OIDC authentication works in all deployment scenarios
- [ ] Deployment guides tested by someone other than author
- [ ] **Coverage checkpoint:** N/A (infrastructure-only phase), 80%+ cumulative maintained

### Phase 5: Authentication + Polish

**Goal:** Production-ready MVP with real authentication.

| Task | Deliverable |
|------|-------------|
| **OIDC Authentication** | Admin UI + secured endpoints (PKCE for browser UI) |
| **Auth providers** | Azure AD, Google, generic OIDC support |
| **Token validation** | JWT validation, claims extraction |
| **Admin protection** | Admin UI and API endpoints require auth |
| **Dev bypass mode** | `HONUA_DEV_AUTH=true` skips auth for local dev |
| Error handling audit | Consistent error responses |
| Edge cases | Null handling, large payloads, unicode |
| Performance | Query optimization, connection pooling, query plan review, load/soak tests |
| Redis cache (optional) | Metadata cache via Redis with in-memory fallback |
| Documentation | README, API docs, deployment guide |
| Docker optimization | Multi-stage, minimal image, non-root, read-only FS |
| Security hardening | Input validation, security headers, SQL injection prevention |

**Exit Criteria:**
- [ ] OIDC login flow works with Azure AD
- [ ] OIDC login flow works with Google
- [ ] Generic OIDC provider can be configured via env vars
- [ ] Admin endpoints return 401 without valid token
- [ ] Dev bypass mode allows local development without auth
- [ ] No known critical bugs
- [ ] Documentation complete
- [ ] Docker image < 100MB
- [ ] Container runs as non-root and is read-only filesystem compatible
- [ ] Container drops unnecessary Linux capabilities
- [ ] Cold start < 1s
- [ ] Load test baseline meets latency/throughput targets; memory profile shows no leaks
- [ ] Security scan clean (CodeQL + dependency vulnerability scan)
- [ ] Security headers set on all responses (HSTS, X-Content-Type-Options, frame-ancestors/CSP, Referrer-Policy)
- [ ] Redis metadata cache works when configured (in-memory fallback otherwise)
- [ ] **Coverage checkpoint (FINAL):** 80%+ line coverage overall, 70%+ branch coverage, 95%+ on critical paths (query execution, transaction handling, auth middleware)

---

## Risk Management

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| **Scope creep** | High | High | Strict MVP boundary, defer to backlog |
| **GeoServices compatibility edge cases** | Medium | Medium | Test against real GeoServices services, document gaps |
| **Solo dev burnout** | Medium | High | Sustainable pace, quality over speed |
| **PostGIS complexity** | Low | Medium | Leverage existing Honua code as reference |
| **Blazor WASM learning curve** | Medium | Low | Server-rendered fallback if needed |

---

## Success Criteria (Greenfield MVP)

### Functional

- [ ] FeatureServer query works with filters, spatial predicates, paging
- [ ] FeatureServer editing works (applyEdits, attachments, related)
- [ ] OGC API Features works (collections, items, transactions)
- [ ] OGC CITE Team Engine conformance tests pass
- [ ] Vector tiles (MVT) work with TileJSON metadata
- [ ] OData v4 works (Excel/Power BI can connect, filter, page, POST/PATCH/DELETE)
- [ ] Admin UI can connect, discover, publish, import, and edit styles
- [ ] OIDC authentication works (Azure AD, Google, generic provider)
- [ ] Single Docker container deploys and runs
- [ ] Helm chart deploys to Kubernetes
- [ ] Terraform modules deploy to AWS and Azure
- [ ] Redis cache works when configured (in-memory fallback otherwise)

### Quality

- [ ] 80%+ code coverage
- [ ] All CI gates pass (build, test, quality, performance, aot-build)
- [ ] No high/critical security issues
- [ ] Native AOT build succeeds with no trim warnings
- [ ] AOT binary size < 50MB
- [ ] Cold start (AOT) < 100ms
- [ ] p50 query latency < 50ms (100 features)
- [ ] p99 query latency < 300ms (100 features)
- [ ] Throughput > 1000 rps for simple queries
- [ ] All benchmarks within 10% of baseline
- [ ] Memory soak benchmarks show no unbounded growth under sustained load

### Documentation

- [ ] README with quick start
- [ ] API documentation (OpenAPI)
- [ ] Deployment guides (Kubernetes, AWS, Azure)
- [ ] Architecture decisions recorded

---

## Next Steps

1. **Complete MVP gaps** (Admin UI, styles, TileJSON, service enable/disable)
2. **Ship deployment templates** (Helm + AWS/Azure)
3. **Finalize documentation + security hardening**
4. **Validate MVP exit criteria**

---

## Appendix: Reference from Existing Codebase

The following components from the existing Honua.Server can be used as reference (not ported directly):

| Component | Location | Use As Reference For |
|-----------|----------|----------------------|
| Query parsing | `src/domain/geoservices/featureserver/` | FeatureServer query syntax |
| Geometry handling | `src/platform/core/Geometry/` | PostGIS geometry operations |
| GeoServices JSON serialization | `src/domain/geoservices/` | Output format |
| OGC API Features | `src/apps/host/OgcApi/` | Endpoint structure, CQL |
| Import wizard | `src/apps/host/Admin/` | GeoServices service parsing |
| **Shared Filter AST** | `src/platform/core/Query/Filter/` | Protocol-agnostic filter parsing |
| CQL Filter Parser | `src/platform/core/Query/Filter/CqlFilterParser.cs` | CQL2 text/JSON parsing |
| OData Filter Parser | `src/platform/core/Query/Filter/ODataFilterParser.cs` | OData $filter parsing |
| SQL Filter Translator | `src/platform/core/Data/Query/SqlFilterTranslator.cs` | Filter → SQL translation |
| Spatial Filter Translators | `src/platform/core/Data/Postgres/PostgresSpatialFilterTranslator.cs` | PostGIS spatial operations |

**Architecture Note:** The existing codebase has a unified filter infrastructure where:
- Each protocol parser (CQL, OData, GeoServices WHERE) produces a common filter AST
- `SqlFilterTranslator` converts the AST to SQL WHERE clauses
- Database-specific translators handle spatial operations

This pattern should be preserved in the greenfield implementation.

**Do not copy code directly.** Use as documentation for behavior and edge cases.
