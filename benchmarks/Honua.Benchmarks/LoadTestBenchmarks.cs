// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Benchmarks;

/// <summary>
/// Load test benchmarks for measuring throughput and latency under concurrent load.
/// These benchmarks simulate realistic production traffic patterns:
/// - Concurrent requests from multiple clients
/// - Mixed workload scenarios
/// - Sustained load over time
///
/// Targets (Issue #46 AC):
/// - Simple queries: greater than 1000 RPS at p95 less than 100ms
/// - Spatial queries: greater than 500 RPS at p95 less than 100ms
/// - Mixed workload: greater than 800 RPS at p95 less than 150ms
/// - Sustained load: greater than 500 RPS stable over duration
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class LoadTestBenchmarks : IDisposable
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    // Concurrency settings
    private const int LowConcurrency = 10;
    private const int MediumConcurrency = 50;
    private const int HighConcurrency = 100;
    private const int RequestsPerClient = 100;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "true");
                builder.UseEnvironment("Testing");
            });

        _client = _factory.CreateClient();
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
    /// Simple query throughput test with low concurrency (10 clients).
    /// Target: greater than 1000 RPS total, p95 less than 150ms.
    /// </summary>
    [Benchmark(Description = "Simple query throughput (10 concurrent clients)")]
    public async Task<LoadTestResult> SimpleQueryLowConcurrency()
    {
        return await RunLoadTestAsync(
            "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=10&f=json",
            LowConcurrency,
            RequestsPerClient);
    }

    /// <summary>
    /// Simple query throughput test with medium concurrency (50 clients).
    /// Tests connection pool behavior under moderate load.
    /// </summary>
    [Benchmark(Description = "Simple query throughput (50 concurrent clients)")]
    public async Task<LoadTestResult> SimpleQueryMediumConcurrency()
    {
        return await RunLoadTestAsync(
            "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=10&f=json",
            MediumConcurrency,
            RequestsPerClient);
    }

    /// <summary>
    /// Simple query throughput test with high concurrency (100 clients).
    /// Tests system behavior under heavy load.
    /// </summary>
    [Benchmark(Description = "Simple query throughput (100 concurrent clients)")]
    public async Task<LoadTestResult> SimpleQueryHighConcurrency()
    {
        return await RunLoadTestAsync(
            "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=10&f=json",
            HighConcurrency,
            RequestsPerClient);
    }

    /// <summary>
    /// Spatial query throughput test with bbox intersection.
    /// Target: greater than 500 RPS at p95 less than 300ms.
    /// </summary>
    [Benchmark(Description = "Spatial query throughput (50 concurrent clients)")]
    public async Task<LoadTestResult> SpatialQueryMediumConcurrency()
    {
        return await RunLoadTestAsync(
            "/rest/services/test/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&f=json",
            MediumConcurrency,
            RequestsPerClient);
    }

    /// <summary>
    /// Mixed workload throughput test simulating real-world traffic.
    /// Includes: simple queries, spatial queries, paginated queries, different formats.
    /// Target: greater than 800 RPS at p95 less than 200ms.
    /// </summary>
    [Benchmark(Description = "Mixed workload throughput (50 concurrent clients)")]
    public async Task<LoadTestResult> MixedWorkloadMediumConcurrency()
    {
        var endpoints = new[]
        {
            "/rest/services/test/FeatureServer/0/query?where=1=1&f=json",
            "/rest/services/test/FeatureServer/0/query?where=1=1&f=geojson",
            "/rest/services/test/FeatureServer/0/query?geometry=-122.5,37.7,-122.3,37.8&geometryType=esriGeometryEnvelope&f=json",
            "/rest/services/test/FeatureServer/0/query?resultOffset=0&resultRecordCount=50&f=json",
            "/rest/services/test/FeatureServer/0/query?outFields=*&returnCountOnly=true&f=json"
        };

        return await RunMixedLoadTestAsync(endpoints, MediumConcurrency, RequestsPerClient);
    }

    /// <summary>
    /// Sustained load test running for a longer duration.
    /// Tests stability and consistency over time.
    /// </summary>
    [Benchmark(Description = "Sustained load (30 seconds, 50 clients)")]
    public async Task<LoadTestResult> SustainedLoad30Seconds()
    {
        return await RunSustainedLoadTestAsync(
            "/rest/services/test/FeatureServer/0/query?where=1=1&resultRecordCount=10&f=json",
            MediumConcurrency,
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Paginated query performance under load.
    /// Tests offset/limit query patterns.
    /// </summary>
    [Benchmark(Description = "Paginated queries (50 concurrent clients)")]
    public async Task<LoadTestResult> PaginatedQueriesMediumConcurrency()
    {
        var offsets = Enumerable.Range(0, 20).Select(i => i * 50).ToArray();
        return await RunParameterizedLoadTestAsync(
            offset => $"/rest/services/test/FeatureServer/0/query?resultOffset={offset}&resultRecordCount=50&f=json",
            offsets,
            MediumConcurrency,
            RequestsPerClient);
    }

    private async Task<LoadTestResult> RunLoadTestAsync(string url, int concurrency, int requestsPerClient)
    {
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();
        var stopwatch = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, concurrency).Select(async clientId =>
        {
            for (int i = 0; i < requestsPerClient; i++)
            {
                var requestWatch = Stopwatch.StartNew();
                try
                {
                    var response = await _client!.GetAsync(url);
                    requestWatch.Stop();
                    latencies.Add(requestWatch.Elapsed.TotalMilliseconds);

                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"Client {clientId}: {response.StatusCode}");
                    }
                    else
                    {
                        // Consume response
                        _ = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    requestWatch.Stop();
                    errors.Add($"Client {clientId}: {ex.Message}");
                }
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        return CalculateResult(latencies, errors, stopwatch.Elapsed, concurrency, requestsPerClient);
    }

    private async Task<LoadTestResult> RunMixedLoadTestAsync(string[] endpoints, int concurrency, int requestsPerClient)
    {
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();
        var stopwatch = Stopwatch.StartNew();
        var random = new Random();

        var tasks = Enumerable.Range(0, concurrency).Select(async clientId =>
        {
            for (int i = 0; i < requestsPerClient; i++)
            {
                var url = endpoints[random.Next(endpoints.Length)];
                var requestWatch = Stopwatch.StartNew();
                try
                {
                    var response = await _client!.GetAsync(url);
                    requestWatch.Stop();
                    latencies.Add(requestWatch.Elapsed.TotalMilliseconds);

                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"Client {clientId}: {response.StatusCode}");
                    }
                    else
                    {
                        _ = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    requestWatch.Stop();
                    errors.Add($"Client {clientId}: {ex.Message}");
                }
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        return CalculateResult(latencies, errors, stopwatch.Elapsed, concurrency, requestsPerClient);
    }

    private async Task<LoadTestResult> RunSustainedLoadTestAsync(string url, int concurrency, TimeSpan duration)
    {
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();
        var stopwatch = Stopwatch.StartNew();
        var endTime = DateTime.UtcNow.Add(duration);
        var requestCount = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(async clientId =>
        {
            while (DateTime.UtcNow < endTime)
            {
                var requestWatch = Stopwatch.StartNew();
                try
                {
                    var response = await _client!.GetAsync(url);
                    requestWatch.Stop();
                    latencies.Add(requestWatch.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref requestCount);

                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"Client {clientId}: {response.StatusCode}");
                    }
                    else
                    {
                        _ = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    requestWatch.Stop();
                    errors.Add($"Client {clientId}: {ex.Message}");
                }
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        return CalculateResult(latencies, errors, stopwatch.Elapsed, concurrency, requestCount / concurrency);
    }

    private async Task<LoadTestResult> RunParameterizedLoadTestAsync<T>(
        Func<T, string> urlBuilder,
        T[] parameters,
        int concurrency,
        int requestsPerClient)
    {
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentBag<string>();
        var stopwatch = Stopwatch.StartNew();
        var paramIndex = 0;

        var tasks = Enumerable.Range(0, concurrency).Select(async clientId =>
        {
            for (int i = 0; i < requestsPerClient; i++)
            {
                var paramValue = parameters[Interlocked.Increment(ref paramIndex) % parameters.Length];
                var url = urlBuilder(paramValue);
                var requestWatch = Stopwatch.StartNew();
                try
                {
                    var response = await _client!.GetAsync(url);
                    requestWatch.Stop();
                    latencies.Add(requestWatch.Elapsed.TotalMilliseconds);

                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"Client {clientId}: {response.StatusCode}");
                    }
                    else
                    {
                        _ = await response.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    requestWatch.Stop();
                    errors.Add($"Client {clientId}: {ex.Message}");
                }
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        return CalculateResult(latencies, errors, stopwatch.Elapsed, concurrency, requestsPerClient);
    }

    private static LoadTestResult CalculateResult(
        ConcurrentBag<double> latencies,
        ConcurrentBag<string> errors,
        TimeSpan totalDuration,
        int concurrency,
        int requestsPerClient)
    {
        var sortedLatencies = latencies.OrderBy(x => x).ToArray();
        var totalRequests = sortedLatencies.Length;

        if (totalRequests == 0)
        {
            return new LoadTestResult
            {
                TotalRequests = 0,
                ErrorCount = errors.Count,
                ErrorRatePercent = 100,
                TotalDurationSeconds = totalDuration.TotalSeconds,
                RequestsPerSecond = 0,
                Concurrency = concurrency
            };
        }

        var p50Index = (int)(totalRequests * 0.50);
        var p95Index = (int)(totalRequests * 0.95);
        var p99Index = (int)(totalRequests * 0.99);

        return new LoadTestResult
        {
            TotalRequests = totalRequests,
            ErrorCount = errors.Count,
            ErrorRatePercent = errors.Count * 100.0 / (totalRequests + errors.Count),
            TotalDurationSeconds = totalDuration.TotalSeconds,
            RequestsPerSecond = totalRequests / totalDuration.TotalSeconds,
            MeanLatencyMs = sortedLatencies.Average(),
            P50LatencyMs = sortedLatencies[Math.Min(p50Index, totalRequests - 1)],
            P95LatencyMs = sortedLatencies[Math.Min(p95Index, totalRequests - 1)],
            P99LatencyMs = sortedLatencies[Math.Min(p99Index, totalRequests - 1)],
            MinLatencyMs = sortedLatencies[0],
            MaxLatencyMs = sortedLatencies[^1],
            Concurrency = concurrency
        };
    }
}

/// <summary>
/// Result structure for load test benchmarks.
/// </summary>
public readonly record struct LoadTestResult
{
    public int TotalRequests { get; init; }
    public int ErrorCount { get; init; }
    public double ErrorRatePercent { get; init; }
    public double TotalDurationSeconds { get; init; }
    public double RequestsPerSecond { get; init; }
    public double MeanLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public double MinLatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public int Concurrency { get; init; }

    public override string ToString() =>
        $"{RequestsPerSecond:F1} RPS, p50: {P50LatencyMs:F1}ms, p95: {P95LatencyMs:F1}ms, p99: {P99LatencyMs:F1}ms, errors: {ErrorRatePercent:F2}%";
}
