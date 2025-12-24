// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Benchmarks;

/// <summary>
/// Memory soak benchmarks for detecting memory leaks under sustained load.
/// These benchmarks run many operations and measure heap growth to detect:
/// - Memory leaks from unmanaged resources
/// - Connection pool exhaustion
/// - Growing object graphs
///
/// Targets from performance-testing.md:
/// - Memory delta after 10k queries: less than 50MB
/// - No unbounded memory growth under sustained load
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class MemorySoakBenchmarks : IDisposable
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;
    private const int QueryIterations = 10_000;
    private const int MixedIterations = 5_000;
    private const int ConnectionIterations = 2_000;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Create factory with real services (connecting to test db if available)
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseEnvironment("Testing");
            });

        _client = _factory.CreateClient();

        // Force GC and capture baseline
        ForceGarbageCollection();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Measures heap growth after executing 10,000 query requests.
    /// Target: less than 50MB growth after full GC.
    /// </summary>
    [Benchmark(Description = "Query soak (10k requests) - measures memory delta")]
    public async Task<MemorySoakResult> Query_Soak_10k()
    {
        ForceGarbageCollection();
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < QueryIterations; i++)
        {
            var response = await _client!.GetAsync(
                "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=10&f=json");

            // Read and discard to ensure full request completion
            _ = await response.Content.ReadAsStringAsync();
        }

        ForceGarbageCollection();
        var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

        return new MemorySoakResult
        {
            IterationCount = QueryIterations,
            BeforeMemoryBytes = beforeMemory,
            AfterMemoryBytes = afterMemory,
            MemoryDeltaBytes = afterMemory - beforeMemory,
            MemoryDeltaMB = (afterMemory - beforeMemory) / (1024.0 * 1024.0),
            IsMemoryLeakSuspected = (afterMemory - beforeMemory) > 50 * 1024 * 1024 // 50MB threshold
        };
    }

    /// <summary>
    /// Measures heap growth after mixed query operations (various parameters).
    /// Target: less than 20MB growth after 5,000 mixed operations.
    /// </summary>
    [Benchmark(Description = "Mixed operations soak (5k requests) - varies query params")]
    public async Task<MemorySoakResult> Mixed_Soak_5k()
    {
        ForceGarbageCollection();
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        var queryVariants = new[]
        {
            "/rest/services/test/FeatureServer/0/query?where=1=1&f=json",
            "/rest/services/test/FeatureServer/0/query?where=1=1&f=geojson",
            "/rest/services/test/FeatureServer/0/query?geometry=-180,-90,180,90&geometryType=esriGeometryEnvelope&f=json",
            "/rest/services/test/FeatureServer/0/query?resultOffset=0&resultRecordCount=100&f=json",
            "/rest/services/test/FeatureServer/0/query?outFields=*&f=json"
        };

        for (int i = 0; i < MixedIterations; i++)
        {
            var queryUrl = queryVariants[i % queryVariants.Length];
            var response = await _client!.GetAsync(queryUrl);
            _ = await response.Content.ReadAsStringAsync();
        }

        ForceGarbageCollection();
        var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

        return new MemorySoakResult
        {
            IterationCount = MixedIterations,
            BeforeMemoryBytes = beforeMemory,
            AfterMemoryBytes = afterMemory,
            MemoryDeltaBytes = afterMemory - beforeMemory,
            MemoryDeltaMB = (afterMemory - beforeMemory) / (1024.0 * 1024.0),
            IsMemoryLeakSuspected = (afterMemory - beforeMemory) > 20 * 1024 * 1024 // 20MB threshold
        };
    }

    /// <summary>
    /// Specifically tests connection pool behavior under sustained load.
    /// Creates many concurrent requests to stress the connection pool.
    /// Target: less than 5MB memory delta, no connection exhaustion.
    /// </summary>
    [Benchmark(Description = "Connection pool soak (2k requests) - tests pool recycling")]
    public async Task<MemorySoakResult> ConnectionPool_Soak_2k()
    {
        ForceGarbageCollection();
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Execute requests with some concurrency to stress connection pool
        var batchSize = 20;
        for (int batch = 0; batch < ConnectionIterations / batchSize; batch++)
        {
            var tasks = Enumerable.Range(0, batchSize).Select(async _ =>
            {
                var response = await _client!.GetAsync(
                    "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=5&f=json");
                var content = await response.Content.ReadAsStringAsync();
            });

            await Task.WhenAll(tasks);
        }

        ForceGarbageCollection();
        var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

        return new MemorySoakResult
        {
            IterationCount = ConnectionIterations,
            BeforeMemoryBytes = beforeMemory,
            AfterMemoryBytes = afterMemory,
            MemoryDeltaBytes = afterMemory - beforeMemory,
            MemoryDeltaMB = (afterMemory - beforeMemory) / (1024.0 * 1024.0),
            IsMemoryLeakSuspected = (afterMemory - beforeMemory) > 5 * 1024 * 1024 // 5MB threshold
        };
    }

    /// <summary>
    /// Tests for connection leaks by repeatedly acquiring and releasing connections.
    /// Uses NpgsqlDataSource directly if available.
    /// </summary>
    [Benchmark(Description = "Direct connection leak test - opens/closes 1k connections")]
    public async Task<ConnectionLeakResult> ConnectionLeak_Direct_1k()
    {
        var dataSource = _factory?.Services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return new ConnectionLeakResult
            {
                IterationCount = 0,
                ConnectionsOpened = 0,
                ConnectionsClosed = 0,
                LeakDetected = false,
                Message = "NpgsqlDataSource not available in test environment"
            };
        }

        ForceGarbageCollection();
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        int opened = 0;
        int closed = 0;
        const int iterations = 1000;

        for (int i = 0; i < iterations; i++)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync();
                opened++;

                // Execute a simple query
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
            }
            catch
            {
                // Connection pool exhausted or other error
            }
            finally
            {
                closed++;
            }
        }

        ForceGarbageCollection();
        var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

        // Check if all connections were properly returned
        var memoryGrowth = afterMemory - beforeMemory;
        var leakDetected = memoryGrowth > 10 * 1024 * 1024 || opened != closed;

        return new ConnectionLeakResult
        {
            IterationCount = iterations,
            ConnectionsOpened = opened,
            ConnectionsClosed = closed,
            LeakDetected = leakDetected,
            MemoryDeltaMB = memoryGrowth / (1024.0 * 1024.0),
            Message = leakDetected
                ? $"Potential leak: {opened} opened, {closed} closed, {memoryGrowth / 1024.0:F2}KB growth"
                : "No leak detected"
        };
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

/// <summary>
/// Result structure for memory soak benchmarks.
/// </summary>
public readonly record struct MemorySoakResult
{
    public int IterationCount { get; init; }
    public long BeforeMemoryBytes { get; init; }
    public long AfterMemoryBytes { get; init; }
    public long MemoryDeltaBytes { get; init; }
    public double MemoryDeltaMB { get; init; }
    public bool IsMemoryLeakSuspected { get; init; }

    public override string ToString() =>
        $"{IterationCount} iterations, delta: {MemoryDeltaMB:F2}MB, leak: {IsMemoryLeakSuspected}";
}

/// <summary>
/// Result structure for connection leak tests.
/// </summary>
public readonly record struct ConnectionLeakResult
{
    public int IterationCount { get; init; }
    public int ConnectionsOpened { get; init; }
    public int ConnectionsClosed { get; init; }
    public bool LeakDetected { get; init; }
    public double MemoryDeltaMB { get; init; }
    public string Message { get; init; }

    public override string ToString() =>
        $"Connections: {ConnectionsOpened}/{ConnectionsClosed}, leak: {LeakDetected}, {Message}";
}
