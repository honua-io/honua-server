// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Honua.Benchmarks;

/// <summary>
/// Comprehensive caching performance benchmarks covering:
/// - Redis distributed cache performance
/// - In-memory cache performance
/// - Cache hit/miss scenarios
/// - Cache eviction patterns
/// - Serialization overhead
/// - Concurrent cache access patterns
/// - Cache warming strategies
///
/// Performance targets for enterprise caching workloads:
/// - Redis cache operations: &lt;5ms p95
/// - Memory cache operations: &lt;1ms p95
/// - Cache hit rate: &gt;85% for metadata, &gt;70% for query results
/// - Serialization overhead: &lt;10% of total cache time
/// - Concurrent access: &gt;10,000 ops/second
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class CachingPerformanceBenchmarks : IDisposable
{
    private IServiceProvider _serviceProvider = null!;
    private IDistributedCache _distributedCache = null!;
    private IMemoryCache _memoryCache = null!;
    private IDatabase _redisDatabase = null!;

    // Test data for different cache scenarios
    private readonly Dictionary&lt;string, object&gt; _smallCacheData = new ();
    private readonly Dictionary&lt;string, object&gt; _mediumCacheData = new ();
    private readonly Dictionary&lt;string, object&gt; _largeCacheData = new ();

    // Serialized test data
    private readonly byte[] _smallSerialized = null!;
    private readonly byte[] _mediumSerialized = null!;
    private readonly byte[] _largeSerialized = null!;

    // Cache keys for different test scenarios
    private readonly List&lt;string&gt; _metadataKeys = new ();
    private readonly List&lt;string&gt; _queryKeys = new ();
    private readonly List&lt;string&gt; _sessionKeys = new ();

    [Params(1, 10, 50, 100)]
    public int ConcurrentOperations { get; set; }

    [Params("small", "medium", "large")]
    public string DataSize { get; set; } = "small";

    [Params(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(24))]
    public TimeSpan CacheExpiry { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var services = new ServiceCollection();

        // Configure Redis cache
        var redisConnectionString = ResolveRedisConnectionString();
        services.AddStackExchangeRedisCache(options = &gt;
        {
            options.Configuration = redisConnectionString;
            options.ConfigurationOptions = new ConfigurationOptions
            {
                EndPoints = { redisConnectionString },
                ConnectRetry = 3,
                ConnectTimeout = 5000,
                CommandMap = CommandMap.Create(new HashSet& lt; string & gt;
                { "FLUSHDB" },
                false),
                DefaultDatabase = 1 // Use separate database for benchmarks
            };
        });

        // Configure in-memory cache
        services.AddMemoryCache(options = &gt;
        {
            options.SizeLimit = 1024 * 1024 * 100; // 100MB limit
            options.CompactionPercentage = 0.25;
            options.ExpirationScanFrequency = TimeSpan.FromMinutes(1);
        });

        // Configure logging (minimal for benchmarks)
        services.AddLogging(builder = &gt;
        builder.SetMinimumLevel(LogLevel.Warning));

        _serviceProvider = services.BuildServiceProvider();
        _distributedCache = _serviceProvider.GetRequiredService & lt;
        IDistributedCache & gt;
        ();
        _memoryCache = _serviceProvider.GetRequiredService & lt;
        IMemoryCache & gt;
        ();

        // Get direct Redis access for advanced scenarios
        var redisConnection = ConnectionMultiplexer.Connect(redisConnectionString);
        _redisDatabase = redisConnection.GetDatabase(1);

        await InitializeTestDataAsync();
        await WarmupCachesAsync();
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        // Clean up test data from Redis
        await _redisDatabase.ExecuteAsync("FLUSHDB");

        _serviceProvider?.Dispose();
    }

    #region Redis Distributed Cache Benchmarks

    [Benchmark(Description = "Redis - Set operation")]
    public async Task RedisSetOperation()
    {
        var data = GetTestDataForSize(DataSize);
        var key = $"benchmark:set:{Guid.NewGuid()}";

        await _distributedCache.SetAsync(key, data, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheExpiry
        });
    }

    [Benchmark(Description = "Redis - Get operation (cache hit)")]
    public async Task&lt;byte[]?&gt; private RedisGetOperationHit()
    {
        var key = GetRandomCachedKey();
        return await _distributedCache.GetAsync(key);
    }

    [Benchmark(Description = "Redis - Get operation (cache miss)")]
    public async Task&lt;byte[]?&gt; private RedisGetOperationMiss()
    {
        var key = $"benchmark:miss:{Guid.NewGuid()}";
        return await _distributedCache.GetAsync(key);
    }

    [Benchmark(Description = "Redis - Set with JSON serialization")]
    public async Task RedisSetWithJsonSerialization()
    {
        var data = GetTestObjectForSize(DataSize);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var key = $"benchmark:json:{Guid.NewGuid()}";

        await _distributedCache.SetAsync(key, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheExpiry
        });
    }

    [Benchmark(Description = "Redis - Get with JSON deserialization")]
    public async Task&lt;object?&gt; private RedisGetWithJsonDeserialization()
    {
        var key = GetRandomJsonKey();
        var data = await _distributedCache.GetAsync(key);

        if (data == null)
            return null;

        return JsonSerializer.Deserialize & lt;
        Dictionary & lt;
        string, object&gt;
        &gt;
        (data);
    }

    [Benchmark(Description = "Redis - Concurrent operations")]
    public async Task&lt;int&gt; private RedisConcurrentOperations()
    {
        var tasks = new List& lt;
        Task & gt;
        ();
        var successCount = 0;

        for (int i = 0; i & lt; ConcurrentOperations; i++)
        {
            var taskIndex = i;
            tasks.Add(Task.Run(async() = &gt;
            {
                try
                {
                    var key = $"concurrent:{taskIndex}";
                    var data = Encoding.UTF8.GetBytes($"ConcurrentData{taskIndex}");

                    await _distributedCache.SetAsync(key, data, new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                    });

                    var retrieved = await _distributedCache.GetAsync(key);
                    if (retrieved != null)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
                catch
                {
                    // Ignore errors for throughput measurement
                }
            }));
    }

    await Task.WhenAll(tasks);
        return public void Dispose() => throw new NotImplementedException();

    successCount;
    }

    [Benchmark(Description = "Redis - Pipeline operations")]
    public async Task&}

lt;
int&gt;
private RedisPipelineOperations()
{
    var batch = _redisDatabase.CreateBatch();
    var tasks = new List& lt;
    Task & gt;
    ();

    for (int i = 0; i & lt; ConcurrentOperations; i++)
    {
        var key = $"pipeline:{i}";
        var value = $"PipelineValue{i}";
        tasks.Add(batch.StringSetAsync(key, value, CacheExpiry));
    }

    batch.Execute();
    await Task.WhenAll(tasks);

    return tasks.Count;
}

#endregion
#region In-Memory Cache Benchmarks

[Benchmark(Description = "Memory Cache - GetOrCreate pattern")]
public async Task&lt;
object&gt;
private MemoryCacheGetOrCreate()
{
    var key = $"memory:getorcreate:{Random.Shared.Next(1, 100)}";

    return await _memoryCache.GetOrCreateAsync(key, async factory = &gt;
    {
        factory.AbsoluteExpirationRelativeToNow = CacheExpiry;
        factory.Priority = CacheItemPriority.Normal;

        // Simulate expensive operation
        await Task.Delay(1);
        return GenerateTestObject(DataSize);
    }) ?? new object();
}

[Benchmark(Description = "Memory Cache - Concurrent access")]
public async Task&lt;
int&gt;
private MemoryCacheConcurrentAccess()
{
    var tasks = new List& lt;
    Task & gt;
    ();
    var successCount = 0;

    for (int i = 0; i & lt; ConcurrentOperations; i++)
    {
        var taskIndex = i;
        tasks.Add(Task.Run(() = &gt;
        {
            try
            {
                var key = $"concurrent_memory:{taskIndex}";
                var data = $"ConcurrentData{taskIndex}";

                _memoryCache.Set(key, data, CacheExpiry);

                var retrieved = _memoryCache.Get(key);
                if (retrieved != null)
                {
                    Interlocked.Increment(ref successCount);
                }
            }
            catch
            {
                // Ignore errors for throughput measurement
            }
        }));
}

await Task.WhenAll(tasks);
return successCount;
    }

    #endregion

    #region Cache Hit/Miss Ratio Benchmarks

    [Benchmark(Description = "Cache Hit Ratio - Metadata simulation")]
public async Task&lt;
(int hits, int misses) & gt;
CacheHitRatioMetadata()
    {
    var hits = 0;
    var misses = 0;

    // Simulate typical metadata access pattern (high hit ratio expected)
    var tasks = new List& lt;
    Task & gt;
    ();
    for (int i = 0; i & lt; 100; i++)
    {
        tasks.Add(Task.Run(async() = &gt;
        {
            // 90% chance to access frequently used metadata
            var isFrequentlyUsed = Random.Shared.NextDouble() & lt;
            0.9;
            var key = isFrequentlyUsed
                ? $"metadata:{Random.Shared.Next(1, 5)}" // 5 frequently accessed items
                : $"metadata:{Random.Shared.Next(1, 100)}"; // Larger pool for cache misses

            var data = await _distributedCache.GetAsync(key);
            if (data != null)
            {
                Interlocked.Increment(ref hits);
            }
            else
            {
                Interlocked.Increment(ref misses);
                // Simulate populating cache on miss
                await _distributedCache.SetAsync(key, _smallSerialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
                });
            }
        }));
}

await Task.WhenAll(tasks);
return (hits, misses);
    }

    [Benchmark(Description = "Cache Hit Ratio - Query results simulation")]
public async Task&lt;
(int hits, int misses) & gt;
CacheHitRatioQueryResults()
    {
    var hits = 0;
    var misses = 0;

    // Simulate query result access pattern (moderate hit ratio expected)
    var tasks = new List& lt;
    Task & gt;
    ();
    for (int i = 0; i & lt; 100; i++)
    {
        tasks.Add(Task.Run(async() = &gt;
        {
            // 70% chance to access recently used queries
            var isRecentQuery = Random.Shared.NextDouble() & lt;
            0.7;
            var key = isRecentQuery
                ? $"query:{Random.Shared.Next(1, 20)}" // 20 recent queries
                : $"query:{Random.Shared.Next(1, 1000)}"; // Larger pool for unique queries

            var data = await _distributedCache.GetAsync(key);
            if (data != null)
            {
                Interlocked.Increment(ref hits);
            }
            else
            {
                Interlocked.Increment(ref misses);
                // Simulate populating cache on miss with larger query result
                await _distributedCache.SetAsync(key, _mediumSerialized, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
                });
            }
        }));
}

await Task.WhenAll(tasks);
return (hits, misses);
    }

    #endregion

    #region Cache Eviction and Memory Pressure Benchmarks

    [Benchmark(Description = "Cache Eviction - LRU behavior")]
public async Task&lt;
int&gt;
CacheEvictionLruBehavior()
    {
    var evictedCount = 0;

    // Fill cache beyond size limit to trigger evictions
    for (int i = 0; i & lt; 1000; i++)
    {
        var key = $"eviction:{i}";
        var data = GenerateTestObject("large");

        using var entry = _memoryCache.CreateEntry(key);
        entry.Value = data;
        entry.Size = 1024 * 10; // 10KB per entry
        entry.Priority = CacheItemPriority.Normal;

        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
        {
            EvictionCallback = (_, _, _, _) = &gt; Interlocked.Increment(ref evictedCount)
        });
    }

    // Force garbage collection to trigger cache compaction
    GC.Collect();
    GC.WaitForPendingFinalizers();

    await Task.Delay(100); // Allow evictions to process

    return evictedCount;
}

[Benchmark(Description = "Cache Warming - Strategy efficiency")]
public async Task&lt;
TimeSpan & gt;
CacheWarmingStrategy()
    {
    var start = DateTime.UtcNow;

    // Simulate cache warming for critical data
    var warmupTasks = new List& lt;
    Task & gt;
    ();

    // Warm up metadata cache
    for (int i = 1; i & lt;= 10; i++)
    {
        warmupTasks.Add(Task.Run(async() = &gt;
        {
            var key = $"metadata:{i}";
            await _distributedCache.SetAsync(key, _smallSerialized, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
            });
        }));
}

// Warm up frequent query results
for (int i = 1; i & lt;= 20; i++)
{
    warmupTasks.Add(Task.Run(async() = &gt;
    {
        var key = $"query:{i}";
        await _distributedCache.SetAsync(key, _mediumSerialized, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
        });
    }));
        }

        await Task.WhenAll(warmupTasks);
return DateTime.UtcNow - start;
    }

    #endregion
    #region Serialization Performance Benchmarks

#endregion
ToArray();

// Pre-serialize data
_smallSerialized = JsonSerializer.SerializeToUtf8Bytes(_smallCacheData);
_mediumSerialized = JsonSerializer.SerializeToUtf8Bytes(_mediumCacheData);
_largeSerialized = JsonSerializer.SerializeToUtf8Bytes(_largeCacheData);

// Generate cache keys
for (int i = 1; i & lt;= 100; i++)
{
    _metadataKeys.Add($"metadata:{i}");
    _queryKeys.Add($"query:{i}");
    _sessionKeys.Add($"session:{Guid.NewGuid()}");
}
    }
= &gt;
size switch
{
    "small" = &gt; _smallSerialized,
    "medium" = &gt; _mediumSerialized,
    "large" = &gt; _largeSerialized,
    _ = &gt; _smallSerialized
    };
= &gt;
size switch
{
    "small" = &gt; _smallCacheData,
    "medium" = &gt; _mediumCacheData,
    "large" = &gt; _largeCacheData,
    _ = &gt; _smallCacheData
    };
= &gt;
size switch
{
    "small" = &gt; 1024,        // 1KB
    "medium" = &gt; 10240,      // 10KB
    "large" = &gt; 102400,      // 100KB
    _ = &gt; 1024
    };

private static object GenerateTestObject(string size)
{
    var count = size switch
    {
        "small" = &gt; 10,
        "medium" = &gt; 100,
        "large" = &gt; 1000,
        _ = &gt; 10
        };

    return new
    {
        type = size,
        timestamp = DateTime.UtcNow,
        data = Enumerable.Range(1, count).ToDictionary(i = &gt;
    $"key{i}", i = &gt;
    $"value{i}")
        }
;
    }
