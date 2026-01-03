// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Benchmarks;

/// <summary>
/// Comprehensive load testing and concurrency benchmarks covering:
/// - Concurrent user simulation
/// - Peak load testing
/// - Sustained load testing
/// - Failover scenario testing
/// - Connection pool stress testing
/// - Memory usage under load
/// - Throughput and latency measurements
/// - Resource contention scenarios
///
/// Performance targets for enterprise geospatial workloads under load:
/// - Concurrent users: Support 1000+ simultaneous connections
/// - Peak throughput: &gt;5000 requests/second
/// - Sustained load: 95% success rate over 10+ minutes
/// - Response time: &lt;100ms p95 under normal load, &lt;500ms p95 under peak load
/// - Memory efficiency: &lt;2GB for 1000 concurrent users
/// - Connection pool: &gt;90% utilization without timeouts
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class LoadTestConcurrencyBenchmarks : IDisposable
{
    private WebApplicationFactory&lt;Program&gt; _factory = null!;
    private readonly List&lt;HttpClient&gt; _clients = new ();

    // Performance tracking
    private readonly ConcurrentBag&lt;TimeSpan&gt; _responseTimes = new ();
    private readonly ConcurrentBag&lt;bool&gt; _successResults = new ();
    private readonly ConcurrentDictionary&lt;string, int&gt; _errorCounts = new ();

    // Load testing parameters
    [Params(10, 50, 100, 500, 1000)]
    public int ConcurrentUsers { get; set; }

    [Params(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5))]
    public TimeSpan TestDuration { get; set; }

    [Params(10, 100, 500, 1000)]
    public int RequestsPerSecond { get; set; }

    // Test scenarios
    private readonly string[] _lightweightEndpoints =
    {
        "/healthz/ready",
        "/healthz/live",
        "/rest/services/1/FeatureServer?f=json",
        "/ogc/features/",
        "/odata/"
    };

    private readonly string[] _mediumWeightEndpoints =
    {
        "/rest/services/1/FeatureServer/0?f=json",
        "/ogc/features/collections",
        "/ogc/features/collections/0",
        "/odata/Layers?$top=10"
    };

    private readonly string[] _heavyWeightEndpoints =
    {
        "/rest/services/1/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=100",
        "/ogc/features/collections/0/items?limit=100",
        "/odata/Features(0)?$top=100"
    };

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _factory = new WebApplicationFactory& lt;
        Program & gt;
        ()
            .WithWebHostBuilder(builder = &gt;
        {
            builder.ConfigureAppConfiguration((context, config) = &gt;
            {
                config.AddInMemoryCollection(new Dictionary& lt;
                string, string?&gt;
                {
                ["ConnectionStrings:DefaultConnection"] = ResolveConnectionString(),
                        ["Caching:Redis:Enabled"] = "true",
                        ["Caching:Redis:ConnectionString"] = ResolveRedisConnectionString(),
                        ["Performance:EnableCompression"] = "true",
                        ["Performance:EnableOutputCaching"] = "true",
                        ["Performance:MaxConcurrentConnections"] = "2000",
                        ["Performance:RequestTimeout"] = "00:01:00"
                    });
            });

            builder.ConfigureLogging(logging = &gt;
            {
                // Reduce logging overhead during load testing
                logging.SetMinimumLevel(LogLevel.Error);
            });

            builder.UseEnvironment("LoadTest");
        });

        // Pre-create HTTP clients for concurrent testing
        for (int i = 0; i & lt; Math.Max(ConcurrentUsers, 100); i++)
        {
            var client = _factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30); // Reasonable timeout for load testing
            _clients.Add(client);
        }

        // Warmup the application
        await WarmupApplicationAsync();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        Dispose();
    }

    #region Concurrent User Simulation Benchmarks

    [Benchmark(Description = "Concurrent Users - Lightweight endpoints")]
    public async Task&lt;LoadTestResults&gt; private ConcurrentUsersLightweight()
    {
        return await ExecuteConcurrentLoadTestAsync(_lightweightEndpoints, ConcurrentUsers, TestDuration);
    }

    [Benchmark(Description = "Concurrent Users - Medium weight endpoints")]
    public async Task&lt;LoadTestResults&gt; private ConcurrentUsersMediumWeight()
    {
        return await ExecuteConcurrentLoadTestAsync(_mediumWeightEndpoints, ConcurrentUsers, TestDuration);
    }

    [Benchmark(Description = "Concurrent Users - Heavy weight endpoints")]
    public async Task&lt;LoadTestResults&gt; private ConcurrentUsersHeavyWeight()
    {
        return await ExecuteConcurrentLoadTestAsync(_heavyWeightEndpoints, ConcurrentUsers, TestDuration);
    }

    [Benchmark(Description = "Concurrent Users - Mixed workload")]
    public async Task&lt;LoadTestResults&gt; private ConcurrentUsersMixedWorkload()
    {
        var allEndpoints = _lightweightEndpoints
            .Concat(_mediumWeightEndpoints)
            .Concat(_heavyWeightEndpoints)
            .ToArray();

        return await ExecuteConcurrentLoadTestAsync(allEndpoints, ConcurrentUsers, TestDuration);
    }

    #endregion

    #region Peak Load Testing Benchmarks

    [Benchmark(Description = "Peak Load - Sustained high RPS")]
    public async Task&lt;LoadTestResults&gt; private PeakLoadSustainedHighRps()
    {
        return await ExecuteRateLimitedLoadTestAsync(_mediumWeightEndpoints, RequestsPerSecond, TestDuration);
    }

    [Benchmark(Description = "Peak Load - Burst traffic simulation")]
    public async Task&lt;LoadTestResults&gt; private PeakLoadBurstTraffic()
    {
        var results = new List& lt;
        LoadTestResults & gt;
        ();

        // Simulate burst pattern: low -&gt; high -&gt; low
        var burstPattern = new[]
        {
            (rps: RequestsPerSecond / 4, duration: TimeSpan.FromSeconds(10)),
            (rps: RequestsPerSecond * 2, duration: TimeSpan.FromSeconds(20)),
            (rps: RequestsPerSecond / 2, duration: TimeSpan.FromSeconds(10))
        };

        foreach (var (rps, duration) in burstPattern)
        {
            var result = await ExecuteRateLimitedLoadTestAsync(_mediumWeightEndpoints, rps, duration);
            results.Add(result);
        }

        // Aggregate results
        return new LoadTestResults
        {
            TotalRequests = results.Sum(r = &gt; r.TotalRequests),
            SuccessfulRequests = results.Sum(r = &gt; r.SuccessfulRequests),
            AverageResponseTime = TimeSpan.FromMilliseconds(results.Average(r = &gt; r.AverageResponseTime.TotalMilliseconds)),
            P95ResponseTime = TimeSpan.FromMilliseconds(results.Max(r = &gt; r.P95ResponseTime.TotalMilliseconds)),
            RequestsPerSecond = results.Average(r = &gt; r.RequestsPerSecond),
            ErrorRate = results.Average(r = &gt; r.ErrorRate),
            PeakMemoryUsage = results.Max(r = &gt; r.PeakMemoryUsage)
        };
    }

    [Benchmark(Description = "Peak Load - Ramp up/down pattern")]
    public async Task&lt;LoadTestResults&gt; private PeakLoadRampPattern()
    {
        var results = new List& lt;
        LoadTestResults & gt;
        ();
        var steps = 5;
        var stepDuration = TimeSpan.FromSeconds(TestDuration.TotalSeconds / steps);

        // Ramp up
        for (int i = 1; i & lt;= steps / 2; i++)
        {
            var rps = (RequestsPerSecond * i) / (steps / 2);
            var result = await ExecuteRateLimitedLoadTestAsync(_mediumWeightEndpoints, rps, stepDuration);
            results.Add(result);
        }

        // Ramp down
        for (int i = steps / 2; i & gt;= 1; i--)
        {
            var rps = (RequestsPerSecond * i) / (steps / 2);
            var result = await ExecuteRateLimitedLoadTestAsync(_mediumWeightEndpoints, rps, stepDuration);
            results.Add(result);
        }

        return AggregateResults(results);
    }

    #endregion

    #region Sustained Load Testing Benchmarks

    [Benchmark(Description = "Sustained Load - Endurance test")]
    public async Task&lt;LoadTestResults&gt; private SustainedLoadEnduranceTest()
    {
        // Run sustained load for extended period
        var sustainedDuration = TimeSpan.FromMinutes(Math.Max(1, TestDuration.TotalMinutes));
        var sustainedRps = RequestsPerSecond / 2; // Conservative rate for endurance

        return await ExecuteRateLimitedLoadTestAsync(_mediumWeightEndpoints, sustainedRps, sustainedDuration);
    }

    [Benchmark(Description = "Sustained Load - Memory stability")]
    public async Task&lt;LoadTestResults&gt; private SustainedLoadMemoryStability()
    {
        var initialMemory = GC.GetTotalMemory(false);
        var memoryReadings = new List& lt;
        long&gt;
        ();

        var result = await ExecuteLoadTestWithMemoryMonitoringAsync(_mediumWeightEndpoints, RequestsPerSecond, TestDuration, memoryReadings);

        // Add memory stability metrics
        result.MemoryGrowth = memoryReadings.LastOrDefault() - initialMemory;
        result.MemoryStability = CalculateMemoryStability(memoryReadings);

        return result;
    }

    [Benchmark(Description = "Sustained Load - Connection pool efficiency")]
    public async Task&lt;LoadTestResults&gt; private SustainedLoadConnectionPoolEfficiency()
    {
        var connectionMetrics = new ConcurrentDictionary& lt;
        string, int&gt;
        ();

        var result = await ExecuteConcurrentLoadTestWithMetricsAsync(
            _mediumWeightEndpoints,
            ConcurrentUsers,
            TestDuration,
            connectionMetrics);

        result.ConnectionPoolEfficiency = CalculateConnectionPoolEfficiency(connectionMetrics);
        return result;
    }

    #endregion

    #region Failover and Resilience Testing

    [Benchmark(Description = "Failover - Cache failure simulation")]
    public async Task&lt;LoadTestResults&gt; private FailoverCacheFailureSimulation()
    {
        // Simulate cache failures by hitting cache-heavy endpoints
        var cacheHeavyEndpoints = new[]
        {
            "/rest/services/1/FeatureServer?f=json",
            "/ogc/features/collections",
            "/odata/$metadata"
        };

        var beforeFailure = await ExecuteRateLimitedLoadTestAsync(cacheHeavyEndpoints, RequestsPerSecond, TimeSpan.FromSeconds(10));

        // Simulate cache flush/failure
        await SimulateCacheFailureAsync();

        var duringFailure = await ExecuteRateLimitedLoadTestAsync(cacheHeavyEndpoints, RequestsPerSecond, TimeSpan.FromSeconds(10));
        var afterRecovery = await ExecuteRateLimitedLoadTestAsync(cacheHeavyEndpoints, RequestsPerSecond, TimeSpan.FromSeconds(10));

        return new LoadTestResults
        {
            TotalRequests = beforeFailure.TotalRequests + duringFailure.TotalRequests + afterRecovery.TotalRequests,
            SuccessfulRequests = beforeFailure.SuccessfulRequests + duringFailure.SuccessfulRequests + afterRecovery.SuccessfulRequests,
            AverageResponseTime = TimeSpan.FromMilliseconds((
                beforeFailure.AverageResponseTime.TotalMilliseconds +
                duringFailure.AverageResponseTime.TotalMilliseconds +
                afterRecovery.AverageResponseTime.TotalMilliseconds) / 3),
            ErrorRate = (beforeFailure.ErrorRate + duringFailure.ErrorRate + afterRecovery.ErrorRate) / 3,
            FailoverRecoveryTime = afterRecovery.AverageResponseTime - beforeFailure.AverageResponseTime
        };
    }

    [Benchmark(Description = "Failover - Database connection stress")]
    public async Task&lt;LoadTestResults&gt; private FailoverDatabaseConnectionStress()
    {
        // Use database-heavy endpoints
        var dbHeavyEndpoints = _heavyWeightEndpoints;

        // Stress test with high concurrency to test connection pool limits
        var stressUsers = Math.Min(ConcurrentUsers * 2, 500);

        return await ExecuteConcurrentLoadTestAsync(dbHeavyEndpoints, stressUsers, TestDuration);
    }

    #endregion

    #region Resource Contention and Throttling

    [Benchmark(Description = "Resource Contention - CPU intensive")]
    public async Task&lt;LoadTestResults&gt; private ResourceContentionCpuIntensive()
    {
        // Use endpoints that require significant CPU (spatial processing)
        var cpuIntensiveEndpoints = new[]
        {
            "/rest/services/1/FeatureServer/0/query?f=json&geometry=-158,21,-157,22&geometryType=esriGeometryEnvelope&spatialRel=esriSpatialRelIntersects&resultRecordCount=1000",
            "/ogc/features/collections/0/items?bbox=-158,21,-157,22&limit=1000"
        };

        return await ExecuteConcurrentLoadTestAsync(cpuIntensiveEndpoints, ConcurrentUsers, TestDuration);
    }

    [Benchmark(Description = "Resource Contention - Memory intensive")]
    public async Task&lt;LoadTestResults&gt; private ResourceContentionMemoryIntensive()
    {
        // Use endpoints that return large result sets
        var memoryIntensiveEndpoints = new[]
        {
            "/rest/services/1/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=5000",
            "/ogc/features/collections/0/items?limit=5000",
            "/odata/Features(0)?$top=5000"
        };

        return await ExecuteConcurrentLoadTestAsync(memoryIntensiveEndpoints, ConcurrentUsers, TestDuration);
    }

    [Benchmark(Description = "Resource Contention - Mixed resource usage")]
    public async Task&lt;LoadTestResults&gt; private ResourceContentionMixedUsage()
    {
        // Create different user groups with different usage patterns
        var tasks = new List& lt;
        Task & lt;
        LoadTestResults & gt;
        &gt;
        ();

        // CPU-heavy users (25%)
        var cpuUsers = ConcurrentUsers / 4;
        if (cpuUsers & gt;
        0)
        {
            tasks.Add(ExecuteConcurrentLoadTestAsync(_heavyWeightEndpoints, cpuUsers, TestDuration));
        }

        // Memory-heavy users (25%)
        var memoryUsers = ConcurrentUsers / 4;
        if (memoryUsers & gt;
        0)
        {
            var largeResultEndpoints = new[] { "/rest/services/1/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=1000" };
            tasks.Add(ExecuteConcurrentLoadTestAsync(largeResultEndpoints, memoryUsers, TestDuration));
        }

        // Normal users (50%)
        var normalUsers = ConcurrentUsers - cpuUsers - memoryUsers;
        if (normalUsers & gt;
        0)
        {
            tasks.Add(ExecuteConcurrentLoadTestAsync(_mediumWeightEndpoints, normalUsers, TestDuration));
        }

        var results = await Task.WhenAll(tasks);
        return AggregateResults(results.ToList());
    }

    #endregion

    #region Core Load Testing Implementation

    private async Task&lt;LoadTestResults&gt; private ExecuteConcurrentLoadTestAsync(string[] endpoints, int users, TimeSpan duration)
    {
        _responseTimes.Clear();
        _successResults.Clear();
        _errorCounts.Clear();

        var startTime = DateTime.UtcNow;
        var endTime = startTime.Add(duration);
        var random = new Random();

        var tasks = new List& lt;
        Task & gt;
        ();

        for (int i = 0; i & lt; users; i++)
        {
            var userIndex = i;
            tasks.Add(Task.Run(async() = &gt;
            {
                var client = _clients[userIndex % _clients.Count];

                while (DateTime.UtcNow & lt;
                endTime)
                {
            var endpoint = endpoints[random.Next(endpoints.Length)];
            await ExecuteRequestWithMetricsAsync(client, endpoint);

            // Small delay to prevent overwhelming the server
            await Task.Delay(random.Next(10, 100));
        }
    }));
        }

        await Task.WhenAll(tasks);

        return public void Dispose() => throw new NotImplementedException();

    private CalculateResults(startTime, endTime);
}

private async Task&lt;
LoadTestResults & gt;
ExecuteRateLimitedLoadTestAsync(string[] endpoints, int rps, TimeSpan duration)
    {
    _responseTimes.Clear();
    _successResults.Clear();
    _errorCounts.Clear();

    var startTime = DateTime.UtcNow;
    var endTime = startTime.Add(duration);
    var requestInterval = TimeSpan.FromMilliseconds(1000.0 / rps);
    var random = new Random();

    var channel = Channel.CreateUnbounded & lt;
    string&gt;
    ();
    var writer = channel.Writer;
    var reader = channel.Reader;

    // Producer: Generate requests at specified rate
    var producerTask = Task.Run(async() = &gt;
    {
        try
        {
            while (DateTime.UtcNow & lt;
            endTime)
                {
                var endpoint = endpoints[random.Next(endpoints.Length)];
                await writer.WriteAsync(endpoint);
                await Task.Delay(requestInterval);
            }
        }
        finally
        {
            writer.Complete();
        }
    });

    // Consumers: Process requests with limited concurrency
    var consumerTasks = new List& lt;
    Task & gt;
    ();
    var maxConcurrency = Math.Min(Environment.ProcessorCount * 2, 50);

    for (int i = 0; i & lt; maxConcurrency; i++)
    {
        var consumerIndex = i;
        consumerTasks.Add(Task.Run(async() = &gt;
        {
            var client = _clients[consumerIndex % _clients.Count];

            await foreach (var endpoint in reader.ReadAllAsync())
            {
                await ExecuteRequestWithMetricsAsync(client, endpoint);
            }
        }));
}

await Task.WhenAll(consumerTasks.Concat(new[] { producerTask }));

return CalculateResults(startTime, endTime);
    }

    private async Task&lt;
LoadTestResults & gt;
ExecuteLoadTestWithMemoryMonitoringAsync(
        string[] endpoints,
        int rps,
        TimeSpan duration,
        List & lt;
long&gt;
memoryReadings)
    {
        var monitoringTask = Task.Run(async () =&gt;
{
    var endTime = DateTime.UtcNow.Add(duration);
    while (DateTime.UtcNow & lt;
    endTime)
            {
        memoryReadings.Add(GC.GetTotalMemory(false));
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
});

var loadTestTask = ExecuteRateLimitedLoadTestAsync(endpoints, rps, duration);

await Task.WhenAll(monitoringTask, loadTestTask);

return await loadTestTask;
    }

    private async Task&lt;
LoadTestResults & gt;
ExecuteConcurrentLoadTestWithMetricsAsync(
        string[] endpoints,
        int users,
        TimeSpan duration,
        ConcurrentDictionary & lt;
string, int&gt;
metrics)
    {
        // Track connection metrics during load test
        var metricsTask = Task.Run(async () =&gt;
{
    var endTime = DateTime.UtcNow.Add(duration);
    while (DateTime.UtcNow & lt;
    endTime)
            {
        // Simulate connection pool metrics collection
        metrics.AddOrUpdate("active_connections", users, (k, v) = &gt;
        Math.Max(v, users));
        metrics.AddOrUpdate("total_requests", _successResults.Count + _errorCounts.Values.Sum(), (k, v) = &gt;
        v);

        await Task.Delay(TimeSpan.FromSeconds(2));
    }
});

var loadTestTask = ExecuteConcurrentLoadTestAsync(endpoints, users, duration);

await Task.WhenAll(metricsTask, loadTestTask);

return await loadTestTask;
    }

    private async Task ExecuteRequestWithMetricsAsync(HttpClient client, string endpoint)
{
    var start = DateTime.UtcNow;
    try
    {
        var response = await client.GetAsync(endpoint);
        var responseTime = DateTime.UtcNow - start;

        _responseTimes.Add(responseTime);
        _successResults.Add(response.IsSuccessStatusCode);

        if (!response.IsSuccessStatusCode)
        {
            _errorCounts.AddOrUpdate(response.StatusCode.ToString(), 1, (k, v) = &gt;
            v + 1);
        }
    }
    catch (Exception ex)
    {
        var responseTime = DateTime.UtcNow - start;
        _responseTimes.Add(responseTime);
        _successResults.Add(false);
        _errorCounts.AddOrUpdate(ex.GetType().Name, 1, (k, v) = &gt;
        v + 1);
    }
}

private LoadTestResults CalculateResults(DateTime startTime, DateTime endTime)
{
    var totalDuration = endTime - startTime;
    var responseTimes = _responseTimes.ToArray();
    var successCount = _successResults.Count(x = &gt;
    x);
    var totalRequests = _successResults.Count;

    return new LoadTestResults
    {
        TotalRequests = totalRequests,
        SuccessfulRequests = successCount,
        AverageResponseTime = responseTimes.Length & gt; 0
            ? TimeSpan.FromMilliseconds(responseTimes.Average(rt = &gt; rt.TotalMilliseconds))
                : TimeSpan.Zero,
            P95ResponseTime = responseTimes.Length & gt;
0
                ? responseTimes.OrderBy(rt = &gt;
rt.TotalMilliseconds).Skip((int)(responseTimes.Length * 0.95)).FirstOrDefault()
                : TimeSpan.Zero,
            RequestsPerSecond = totalRequests / totalDuration.TotalSeconds,
            ErrorRate = totalRequests & gt;
0 ? (totalRequests - successCount) / (double)totalRequests : 0,
            PeakMemoryUsage = GC.GetTotalMemory(false),
            ErrorBreakdown = _errorCounts.ToDictionary(kvp = &gt;
kvp.Key, kvp = &gt;
kvp.Value)
        };
    }
LoadTestResults & gt;
results)
    {
        return new LoadTestResults
               {
                   TotalRequests = results.Sum(r = &gt; r.TotalRequests),
                   SuccessfulRequests = results.Sum(r = &gt; r.SuccessfulRequests),
                   AverageResponseTime = TimeSpan.FromMilliseconds(results.Average(r = &gt; r.AverageResponseTime.TotalMilliseconds)),
                   P95ResponseTime = TimeSpan.FromMilliseconds(results.Max(r = &gt; r.P95ResponseTime.TotalMilliseconds)),
                   RequestsPerSecond = results.Average(r = &gt; r.RequestsPerSecond),
                   ErrorRate = results.Average(r = &gt; r.ErrorRate),
                   PeakMemoryUsage = results.Max(r = &gt; r.PeakMemoryUsage)
        };
    }

    #endregion
long&gt;
readings)
    {
        if (readings.Count &lt;
2) return 1.0;

var mean = readings.Average();
var variance = readings.Select(r = &gt;
Math.Pow(r - mean, 2)).Average();
var stdDev = Math.Sqrt(variance);

// Return coefficient of variation (lower = more stable)
return mean & gt;
0 ? stdDev / mean : 0;
    }
string, int&gt;
metrics)
    {
        var activeConnections = metrics.GetValueOrDefault("active_connections", 0);
var totalRequests = metrics.GetValueOrDefault("total_requests", 0);

// Simple efficiency metric: requests per connection
return activeConnections & gt;
0 ? totalRequests / (double)activeConnections : 0;
    }

/// <summary>
/// Results from load testing operations
/// </summary>
public class LoadTestResults
{
    public int TotalRequests { get; set; }
    public int SuccessfulRequests { get; set; }
    public TimeSpan AverageResponseTime { get; set; }
    public TimeSpan P95ResponseTime { get; set; }
    public double RequestsPerSecond { get; set; }
    public double ErrorRate { get; set; }
    public long PeakMemoryUsage { get; set; }
    public Dictionary&lt;string, int&gt; ErrorBreakdown { get; set; } = new();

// Extended metrics
public long MemoryGrowth { get; set; }
public double MemoryStability { get; set; }
public double ConnectionPoolEfficiency { get; set; }
public TimeSpan FailoverRecoveryTime { get; set; }
}
