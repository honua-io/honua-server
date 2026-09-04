// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for RedisCacheService - validates caching with fallback behavior.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisCacheServiceTests : IDisposable
{
    private const string ScopedKeyPrefix = "test:scope:default:";
    private static readonly string[] ExpectedIndexedLayerKeys = [$"{ScopedKeyPrefix}layer:1", $"{ScopedKeyPrefix}layer:2"];
    private readonly RedisCacheService _cacheService;
    private readonly CacheOptions _options;
    private readonly IPerformanceMonitor _performanceMonitor;

    public RedisCacheServiceTests()
    {
        _options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 60,
            EnableFallback = true,
            FallbackMaxEntries = 100,
            KeyPrefix = "test:"
        };

        _performanceMonitor = Substitute.For<IPerformanceMonitor>();
        _cacheService = new RedisCacheService(
            null, // No Redis - tests fallback mode
            Options.Create(_options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_WhenKeyNotFound_ReturnsNull()
    {
        // Act
        var result = await _cacheService.GetAsync<MetadataV2Field>("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_DoesNotAttachRawCacheKeyToTelemetryTags()
    {
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        using var cache = new RedisCacheService(
            null,
            Options.Create(_options),
            NullLogger<RedisCacheService>.Instance,
            performanceMonitor);

        _ = await cache.GetAsync<MetadataV2Field>("layer:42:token=secret");

        operationScope.Received().WithTag("cache_type", "layer-catalog");
        operationScope.Received().WithTag("key_family", "layer");
        operationScope.DidNotReceive().WithTag("key", Arg.Any<string>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetAsync_WhenRedisFails_DoesNotLogRawCacheKeyInErrorContext()
    {
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();
        var operationScope = Substitute.For<IOperationScope>();
        performanceMonitor.StartOperation(Arg.Any<string>()).Returns(operationScope);
        operationScope.WithTag(Arg.Any<string>(), Arg.Any<string>()).Returns(operationScope);

        var distributedCache = Substitute.For<IDistributedCache>();
        distributedCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<byte[]?>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));

        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = false,
            KeyPrefix = "test:"
        };

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            performanceMonitor);

        _ = await cache.GetAsync<MetadataV2Field>("layer:42:token=secret");

        performanceMonitor.Received().RecordErrorWithContext(
            "cache_error",
            "redis_get",
            Arg.Is<IDictionary<string, object>>(context => HasExpectedSanitizedRedisErrorContext(context)),
            Arg.Any<RedisConnectionException>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_ThenGetAsync_ReturnsCachedValue()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };

        // Act
        await _cacheService.SetAsync("item:1", item);
        var result = await _cacheService.GetAsync<MetadataV2Field>("item:1");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("objectid");
        result.Type.Should().Be(MetadataV2FieldType.Integer);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_WithAmbientSchemaScope_IsolatesEntriesBySchema()
    {
        var schemaContext = new SchemaContext();

        try
        {
            schemaContext.CurrentSchema = "alpha";
            await _cacheService.SetAsync("layer:1", new MetadataV2Field { Name = "Alpha", Type = MetadataV2FieldType.String, Length = 10 });

            schemaContext.CurrentSchema = "beta";
            var betaResult = await _cacheService.GetAsync<MetadataV2Field>("layer:1");

            schemaContext.CurrentSchema = "alpha";
            var alphaResult = await _cacheService.GetAsync<MetadataV2Field>("layer:1");

            betaResult.Should().BeNull();
            alphaResult.Should().NotBeNull();
            alphaResult!.Name.Should().Be("Alpha");
        }
        finally
        {
            schemaContext.CurrentSchema = null;
        }
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_WithAlreadyScopedKey_DoesNotApplyAmbientScopeTwice()
    {
        var schemaContext = new SchemaContext();

        try
        {
            schemaContext.CurrentSchema = "alpha";
            await _cacheService.SetAsync(
                "scope:schema:alpha:layer:1",
                new MetadataV2Field { Name = "Scoped", Type = MetadataV2FieldType.String, Length = 10 });

            GetFallbackCacheKeys().Should().ContainSingle()
                .Which.Should().Be("test:scope:schema:alpha:layer:1");
        }
        finally
        {
            schemaContext.CurrentSchema = null;
        }
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_WithTtl_ExpiresAfterTtl()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        var shortTtl = TimeSpan.FromMilliseconds(50);

        // Act
        await _cacheService.SetAsync("item:short", item, shortTtl);

        // Wait for expiration
        await Task.Delay(100);

        var result = await _cacheService.GetAsync<MetadataV2Field>("item:short");

        // Assert
        result.Should().BeNull();
    }

    private static bool HasExpectedSanitizedRedisErrorContext(IDictionary<string, object> context)
        => context.TryGetValue("cache_type", out var cacheType) &&
           Equals(cacheType, "layer-catalog") &&
           context.TryGetValue("key_family", out var keyFamily) &&
           Equals(keyFamily, "layer") &&
           context.TryGetValue("source", out var source) &&
           Equals(source, "redis") &&
           !context.ContainsKey("cache_key");

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveAsync_RemovesCachedValue()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("item:remove", item);

        // Act
        await _cacheService.RemoveAsync("item:remove");
        var result = await _cacheService.GetAsync<MetadataV2Field>("item:remove");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrSetAsync_WhenNotCached_CallsFactory()
    {
        // Arrange
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrSetAsync<MetadataV2Field>("item:factory", async ct =>
        {
            factoryCalled = true;
            return new MetadataV2Field { Name = "fromFactory", Type = MetadataV2FieldType.String, Length = 20 };
        });

        // Assert
        factoryCalled.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Name.Should().Be("fromFactory");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetOrSetAsync_WhenCached_DoesNotCallFactory()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "cached", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("item:cached", item);
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrSetAsync<MetadataV2Field>("item:cached", async ct =>
        {
            factoryCalled = true;
            return new MetadataV2Field { Name = "notUsed", Type = MetadataV2FieldType.String, Length = 20 };
        });

        // Assert
        factoryCalled.Should().BeFalse();
        result.Should().NotBeNull();
        result!.Name.Should().Be("cached");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_RemovesMatchingKeys()
    {
        // Arrange
        await _cacheService.SetAsync("layer:1", new MetadataV2Field { Name = "Layer1", Type = MetadataV2FieldType.String, Length = 10 });
        await _cacheService.SetAsync("layer:2", new MetadataV2Field { Name = "Layer2", Type = MetadataV2FieldType.String, Length = 10 });
        await _cacheService.SetAsync("service:1", new MetadataV2Field { Name = "Service1", Type = MetadataV2FieldType.String, Length = 10 });

        // Act
        await _cacheService.RemoveByPatternAsync("layer:*");

        // Assert
        var layer1 = await _cacheService.GetAsync<MetadataV2Field>("layer:1");
        var layer2 = await _cacheService.GetAsync<MetadataV2Field>("layer:2");
        var service1 = await _cacheService.GetAsync<MetadataV2Field>("service:1");

        layer1.Should().BeNull();
        layer2.Should().BeNull();
        service1.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void IsUsingFallback_WithNoRedis_ReturnsTrue()
    {
        // Assert
        _cacheService.IsUsingFallback.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_WithFallbackEnabled_ReturnsTrue()
    {
        // Act
        var isHealthy = await _cacheService.IsCacheHealthyAsync();

        // Assert
        isHealthy.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_EnforcesMaxEntries()
    {
        // Arrange - Create cache with small limit
        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            FallbackMaxEntries = 3,
            KeyPrefix = "limit:"
        };

        using var cache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);

        // Act - Add more entries than limit
        await cache.SetAsync("a", new MetadataV2Field { Name = "A", Type = MetadataV2FieldType.String, Length = 10 });
        await cache.SetAsync("b", new MetadataV2Field { Name = "B", Type = MetadataV2FieldType.String, Length = 10 });
        await cache.SetAsync("c", new MetadataV2Field { Name = "C", Type = MetadataV2FieldType.String, Length = 10 });
        await cache.SetAsync("d", new MetadataV2Field { Name = "D", Type = MetadataV2FieldType.String, Length = 10 });

        // Assert - Newest entries should be present
        var d = await cache.GetAsync<MetadataV2Field>("d");
        d.Should().NotBeNull();

        // At least one old entry should have been evicted
        var a = await cache.GetAsync<MetadataV2Field>("a");
        var b = await cache.GetAsync<MetadataV2Field>("b");
        var c = await cache.GetAsync<MetadataV2Field>("c");

        // Total should not exceed limit
        var presentCount = new[] { a, b, c, d }.Count(x => x != null);
        presentCount.Should().BeLessOrEqualTo(3);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_EnforcesMaxEntries_AlsoCleansWriteMetadata()
    {
        // Regression test for finding: _writeMetadata must be trimmed alongside
        // _fallbackCache during capacity eviction to prevent unbounded memory growth.
        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            FallbackMaxEntries = 3,
            KeyPrefix = "evict:"
        };

        using var cache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);

        // Fill to capacity and trigger eviction
        await cache.SetAsync("a", new MetadataV2Field { Name = "A", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));
        await cache.SetAsync("b", new MetadataV2Field { Name = "B", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));
        await cache.SetAsync("c", new MetadataV2Field { Name = "C", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));
        await cache.SetAsync("d", new MetadataV2Field { Name = "D", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));
        await cache.SetAsync("e", new MetadataV2Field { Name = "E", Type = MetadataV2FieldType.String, Length = 10 }, TimeSpan.FromSeconds(60));

        // Verify write metadata dictionary is bounded alongside fallback cache
        var writeMetadataField = typeof(RedisCacheService)
            .GetField("_writeMetadata", BindingFlags.NonPublic | BindingFlags.Instance);
        writeMetadataField.Should().NotBeNull();

        var writeMetadata = writeMetadataField!.GetValue(cache) as System.Collections.IDictionary;
        if (writeMetadata is null)
        {
            throw new InvalidOperationException("_writeMetadata field was not the expected IDictionary type.");
        }

        // Write metadata count should not exceed fallback max entries
        writeMetadata.Count.Should().BeLessOrEqualTo(options.FallbackMaxEntries);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_DistributedCacheOutage_ProbesDuringFallbackAndRecovery()
    {
        var unavailable = false;
        var storedValues = new Dictionary<string, byte[]>();
        var distributedCache = Substitute.For<IDistributedCache>();
        distributedCache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(),
                Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (unavailable)
                {
                    return Task.FromException(new IOException("Redis unavailable"));
                }

                storedValues[call.ArgAt<string>(0)] = call.ArgAt<byte[]>(1);
                return Task.CompletedTask;
            });
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => unavailable
                ? Task.FromException<byte[]?>(new IOException("Redis unavailable"))
                : Task.FromResult(storedValues.GetValueOrDefault(call.ArgAt<string>(0))));
        distributedCache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedValues.Remove(call.ArgAt<string>(0));
                return Task.CompletedTask;
            });
        using var cache = new RedisCacheService(distributedCache,
            Options.Create(new CacheOptions { EnableFallback = true, RetryIntervalSeconds = 300 }),
            NullLogger<RedisCacheService>.Instance, _performanceMonitor);
        (await cache.IsCacheHealthyAsync()).Should().BeTrue();

        unavailable = true;
        await cache.GetAsync<MetadataV2Field>("layer:outage");
        cache.IsUsingFallback.Should().BeTrue();
        (await cache.IsCacheHealthyAsync()).Should().BeFalse();

        unavailable = false;
        (await cache.IsCacheHealthyAsync()).Should().BeTrue();
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_CacheCommandDenied_ReportsUnhealthyUntilPermissionsRecover(bool denyWrites)
    {
        var denied = true;
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.PingAsync(Arg.Any<CommandFlags>()).Returns(TimeSpan.Zero);
        var transaction = Substitute.For<ITransaction>();
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync().Returns(true);
        transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(true);
        transaction.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(true);
        transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns(_ => denied && denyWrites
                ? Task.FromException<bool>(new RedisServerException("READONLY writes unavailable"))
                : Task.FromResult(true));
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => denied && !denyWrites
                ? Task.FromException<RedisValue>(new RedisServerException("NOPERM cache read denied"))
                : Task.FromResult((RedisValue)"1"));
        using var cache = new RedisCacheService(Substitute.For<IDistributedCache>(),
            Options.Create(_options), NullLogger<RedisCacheService>.Instance,
            _performanceMonitor, redis, "deployment:");

        (await database.PingAsync()).Should().Be(TimeSpan.Zero);
        (await cache.IsCacheHealthyAsync()).Should().BeFalse();
        denied = false;
        (await cache.IsCacheHealthyAsync()).Should().BeTrue();
        var healthKey = transaction.ReceivedCalls()
            .First(call => call.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .GetArguments()[0]!.ToString()!;
        var healthKeyPrefix = $"deployment:{ScopedKeyPrefix}__health_check__:";
        Assert.StartsWith(healthKeyPrefix, healthKey, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(healthKey[healthKeyPrefix.Length..], "N", out _));
        await database.Received(denyWrites ? 1 : 2).StringGetAsync(healthKey);
        await transaction.Received(2).StringSetAsync(healthKey,
            Arg.Any<RedisValue>(), TimeSpan.FromSeconds(30));
        await transaction.Received(2).SetAddAsync($"deployment:{ScopedKeyPrefix}__cache_key_index__", healthKey);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_CallerCancelsPendingProbe_PropagatesCancellation(bool useMultiplexer)
    {
        using var cancellation = new CancellationTokenSource();
        var distributedCache = Substitute.For<IDistributedCache>();
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        distributedCache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.False(call.Arg<CancellationToken>().IsCancellationRequested);
                cleaned.TrySetResult();
                return Task.CompletedTask;
            });
        var distributedRead = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0).EndsWith("__cache_key_index__", StringComparison.Ordinal)
                ? Task.FromResult<byte[]?>(null)
                : distributedRead.Task);
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var transaction = Substitute.For<ITransaction>();
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync().Returns(true);
        transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>()).Returns(true);
        transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(true);
        transaction.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(_ =>
        {
            cleaned.TrySetResult();
            return Task.FromResult(true);
        });
        var read = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(read.Task);
        using var cache = new RedisCacheService(distributedCache,
            Options.Create(new CacheOptions { EnableFallback = true }),
            NullLogger<RedisCacheService>.Instance, _performanceMonitor,
            useMultiplexer ? redis : null);

        var probe = cache.IsCacheHealthyAsync(cancellation.Token);
        probe.IsCompleted.Should().BeFalse();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        exception.CancellationToken.Should().Be(cancellation.Token);
        read.TrySetResult((RedisValue)"1");
        distributedRead.TrySetResult("1"u8.ToArray());
        await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task Dispose_CanceledPendingProbe_DrainsIndexCleanup(bool asynchronous)
    {
        using var cancellation = new CancellationTokenSource();
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var transaction = Substitute.For<ITransaction>();
        database.CreateTransaction(Arg.Any<object>()).Returns(transaction);
        transaction.ExecuteAsync().Returns(true);
        transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>()).Returns(true);
        transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(true);
        transaction.KeyDeleteAsync(Arg.Any<RedisKey>()).Returns(true);
        var cleaned = false;
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>()).Returns(_ =>
        {
            cleaned = true;
            return Task.FromResult(true);
        });
        var read = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(read.Task);
        var cache = new RedisCacheService(Substitute.For<IDistributedCache>(),
            Options.Create(_options), NullLogger<RedisCacheService>.Instance, _performanceMonitor, redis);
        var probe = cache.IsCacheHealthyAsync(cancellation.Token);
        Assert.False(probe.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = Task.Run(async () =>
        {
            started.SetResult();
            if (asynchronous && (object)cache is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                cache.Dispose();
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.False(cleaned);
        }
        finally
        {
            read.TrySetResult((RedisValue)"1");
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(cleaned);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_ConcurrentProbes_DoNotDeleteEachOthersEntries(bool sameInstance)
    {
        var entries = new ConcurrentDictionary<string, byte[]>();
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var distributedCache = Substitute.For<IDistributedCache>();
        distributedCache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entries[call.ArgAt<string>(0)] = call.ArgAt<byte[]>(1);
                return Task.CompletedTask;
            });
        distributedCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    firstReadStarted.SetResult();
                    await releaseFirstRead.Task;
                }
                return entries.TryGetValue(call.ArgAt<string>(0), out var value) ? value : null;
            });
        distributedCache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entries.TryRemove(call.ArgAt<string>(0), out _);
                return Task.CompletedTask;
            });
        using var first = new RedisCacheService(distributedCache, Options.Create(_options),
            NullLogger<RedisCacheService>.Instance, _performanceMonitor);
        using var second = new RedisCacheService(distributedCache, Options.Create(_options),
            NullLogger<RedisCacheService>.Instance, _performanceMonitor);

        var firstProbe = first.IsCacheHealthyAsync();
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondProbe = (sameInstance ? first : second).IsCacheHealthyAsync();
        if (!sameInstance)
        {
            Assert.True(await secondProbe);
        }
        releaseFirstRead.SetResult();

        Assert.True(await firstProbe);
        Assert.True(await secondProbe);
        Assert.Empty(entries);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task TryRestoreRedisAsync_WhenRedisRecovers_ClearsFallbackState()
    {
        var distributedCache = Substitute.For<IDistributedCache>();
        distributedCache
            .SetAsync(
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<DistributedCacheEntryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis unavailable"));
        distributedCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<byte[]?>(null));

        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = true,
            FallbackMaxEntries = 10,
            KeyPrefix = "restore:"
        };

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);

        await cache.SetAsync("layer:1", new MetadataV2Field { Name = "Recovered", Type = MetadataV2FieldType.String, Length = 10 });

        cache.IsUsingFallback.Should().BeTrue();
        GetFallbackCacheKeys(cache).Should().ContainSingle();
        GetWriteMetadataCount(cache).Should().Be(1);

        var tryRestoreMethod = typeof(RedisCacheService)
            .GetMethod("TryRestoreRedisAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        tryRestoreMethod.Should().NotBeNull();

        var restored = await (Task<bool>)tryRestoreMethod!
            .Invoke(cache, [CancellationToken.None])!;

        restored.Should().BeTrue();
        cache.IsUsingFallback.Should().BeFalse();
        GetFallbackCacheKeys(cache).Should().BeEmpty();
        GetWriteMetadataCount(cache).Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task CacheDisabled_ReturnsNullAndDoesNotStore()
    {
        // Arrange
        var options = new CacheOptions { Enabled = false };
        using var disabledCache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);

        // Act
        await disabledCache.SetAsync("key", new MetadataV2Field { Name = "Test", Type = MetadataV2FieldType.String, Length = 10 });
        var result = await disabledCache.GetAsync<MetadataV2Field>("key");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public void PruneKeyLocks_IdleLock_RemovesDictionaryEntryWithoutDisposingSemaphore()
    {
        // Arrange
        var keyLocksField = typeof(RedisCacheService).GetField("_keyLocks", BindingFlags.NonPublic | BindingFlags.Instance);
        keyLocksField.Should().NotBeNull();

        var keyLocks = keyLocksField!.GetValue(_cacheService) as ConcurrentDictionary<string, SemaphoreSlim>;
        if (keyLocks is null)
        {
            throw new InvalidOperationException("_keyLocks field was not the expected ConcurrentDictionary type.");
        }

        var key = "prune:test";
        var semaphore = new SemaphoreSlim(1, 1);
        keyLocks[key] = semaphore;

        var pruneMethod = typeof(RedisCacheService).GetMethod("PruneKeyLocks", BindingFlags.NonPublic | BindingFlags.Instance);
        pruneMethod.Should().NotBeNull();

        // Act
        pruneMethod!.Invoke(_cacheService, null);

        // Assert
        keyLocks.ContainsKey(key).Should().BeFalse();

        var waited = semaphore.Wait(0);
        waited.Should().BeTrue();
        if (waited)
        {
            semaphore.Release();
        }
        semaphore.Dispose();
    }

    private string[] GetFallbackCacheKeys()
        => GetFallbackCacheKeys(_cacheService);

    private static string[] GetFallbackCacheKeys(RedisCacheService cacheService)
    {
        var fallbackCacheField = typeof(RedisCacheService).GetField("_fallbackCache", BindingFlags.NonPublic | BindingFlags.Instance);
        fallbackCacheField.Should().NotBeNull();

        var fallbackCache = fallbackCacheField!.GetValue(cacheService) as System.Collections.IEnumerable;
        if (fallbackCache is null)
        {
            throw new InvalidOperationException("_fallbackCache field was not the expected IEnumerable type.");
        }

        return fallbackCache
            .Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!)
            .ToArray();
    }

    private static int GetWriteMetadataCount(RedisCacheService cacheService)
    {
        var writeMetadataField = typeof(RedisCacheService).GetField("_writeMetadata", BindingFlags.NonPublic | BindingFlags.Instance);
        writeMetadataField.Should().NotBeNull();

        var writeMetadata = writeMetadataField!.GetValue(cacheService) as System.Collections.IDictionary;
        if (writeMetadata is null)
        {
            throw new InvalidOperationException("_writeMetadata field was not the expected IDictionary type.");
        }

        return writeMetadata.Count;
    }

    internal sealed class MockLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            new NullScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NullScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenKeyExists_ReturnsValueWithPositiveRemainingTtl()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        var ttl = TimeSpan.FromSeconds(60);
        await _cacheService.SetAsync("meta:1", item, ttl);

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:1");

        // Assert
        result.HasValue.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Name.Should().Be("objectid");
        result.RemainingTtl.Should().BeGreaterThan(TimeSpan.Zero);
        result.RemainingTtl.Should().BeLessOrEqualTo(ttl);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenKeyNotFound_ReturnsMiss()
    {
        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:nonexistent");

        // Assert
        result.HasValue.Should().BeFalse();
        result.Value.Should().BeNull();
        result.RemainingTtl.Should().Be(TimeSpan.Zero);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WhenExpired_ReturnsMiss()
    {
        // Arrange
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("meta:short", item, TimeSpan.FromMilliseconds(50));

        // Wait for expiration
        await Task.Delay(100);

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:short");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_CacheDisabled_ReturnsMiss()
    {
        // Arrange
        var options = new CacheOptions { Enabled = false };
        using var disabledCache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);

        // Act
        var result = await disabledCache.GetWithMetadataAsync<MetadataV2Field>("meta:1");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_NoMultiplexer_ComputesTtlFromWriteMetadata()
    {
        // Arrange: cache service with no IConnectionMultiplexer (simulates startup Redis failure)
        // — write metadata is the only TTL source, verifying Finding 1 fix.
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        var ttl = TimeSpan.FromSeconds(60);
        await _cacheService.SetAsync("meta:nomux", item, ttl);

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:nomux");

        // Assert: remaining TTL is derived from write metadata, not MaxValue
        result.HasValue.Should().BeTrue();
        result.RemainingTtl.Should().BeGreaterThan(TimeSpan.Zero);
        result.RemainingTtl.Should().BeLessOrEqualTo(ttl);
        // Crucially, TTL must NOT be MaxValue (which would disable near-expiry detection)
        result.RemainingTtl.Should().BeLessThan(TimeSpan.MaxValue);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_AfterRemove_ReturnsMiss()
    {
        // Arrange: set and then remove to verify write metadata is cleaned up
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        await _cacheService.SetAsync("meta:removed", item, TimeSpan.FromSeconds(60));
        await _cacheService.RemoveAsync("meta:removed");

        // Act
        var result = await _cacheService.GetWithMetadataAsync<MetadataV2Field>("meta:removed");

        // Assert
        result.HasValue.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetWithMetadataAsync_WarmRedisEntry_NoMultiplexer_ReturnsTtlZeroForRefresh()
    {
        // Arrange: simulate a warm Redis entry written by another node. This node has
        // no IConnectionMultiplexer (startup failure) and no _writeMetadata for the key.
        // ResolveRemainingTtlAsync should return Zero so background refresh self-corrects.
        var item = new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false };
        var serialized = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            item, CacheJsonContext.Default.MetadataV2Field);

        var distributedCache = Substitute.For<IDistributedCache>();
        distributedCache
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(serialized);

        var options = new CacheOptions
        {
            Enabled = true,
            EnableFallback = false, // force Redis-only path
            KeyPrefix = "test:"
        };

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor,
            redis: null, // no multiplexer
            distributedCacheKeyPrefix: null);

        // Act
        var result = await cache.GetWithMetadataAsync<MetadataV2Field>("meta:warm");

        // Assert: entry found, TTL is zero (triggers near-expiry / background refresh)
        result.HasValue.Should().BeTrue();
        result.Value!.Name.Should().Be("objectid");
        result.RemainingTtl.Should().Be(TimeSpan.Zero);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_WhenRedisMultiplexerIsAvailable_WritesPayloadAndIndexInOneTransaction()
    {
        var distributedCache = Substitute.For<IDistributedCache>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction().Returns(transaction);
        transaction.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        transaction.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(_options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor,
            redis);

        await cache.SetAsync("layer:42", new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false }, TimeSpan.FromSeconds(30));

        database.Received(1).CreateTransaction();
        _ = transaction.Received(1).ExecuteAsync(Arg.Any<CommandFlags>());
        _ = transaction.Received(1).SetAddAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"{ScopedKeyPrefix}__cache_key_index__"),
            Arg.Is<RedisValue>(value => value.ToString() == $"{ScopedKeyPrefix}layer:42"),
            Arg.Any<CommandFlags>());
        await distributedCache.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveAsync_WhenRedisMultiplexerIsAvailable_RemovesPayloadAndIndexInOneTransaction()
    {
        var distributedCache = Substitute.For<IDistributedCache>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.CreateTransaction().Returns(transaction);
        transaction.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(_options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor,
            redis);

        await cache.RemoveAsync("layer:42");

        database.Received(1).CreateTransaction();
        _ = transaction.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"{ScopedKeyPrefix}layer:42"),
            Arg.Any<CommandFlags>());
        _ = transaction.Received(1).SetRemoveAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"{ScopedKeyPrefix}__cache_key_index__"),
            Arg.Is<RedisValue>(value => value.ToString() == $"{ScopedKeyPrefix}layer:42"),
            Arg.Any<CommandFlags>());
        await distributedCache.DidNotReceive()
            .RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task RemoveByPatternAsync_WhenRedisMultiplexerIsAvailable_DeletesKeysAndIndexMembersInOneTransaction()
    {
        var distributedCache = Substitute.For<IDistributedCache>();
        var database = Substitute.For<IDatabase>();
        var transaction = Substitute.For<ITransaction>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(
                [
                    (RedisValue)$"{ScopedKeyPrefix}layer:1",
                    (RedisValue)$"{ScopedKeyPrefix}layer:2",
                    (RedisValue)$"{ScopedKeyPrefix}service:1"
                ]);
        database.CreateTransaction().Returns(transaction);
        transaction.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));
        transaction.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(2L));
        transaction.ExecuteAsync(Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        using var cache = new RedisCacheService(
            distributedCache,
            Options.Create(_options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor,
            redis);

        await cache.RemoveByPatternAsync("layer:*");

        database.Received(1).CreateTransaction();
        _ = transaction.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(keys => keys.Select(key => key.ToString()).OrderBy(static key => key).SequenceEqual(ExpectedIndexedLayerKeys)),
            Arg.Any<CommandFlags>());
        _ = transaction.Received(1).SetRemoveAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"{ScopedKeyPrefix}__cache_key_index__"),
            Arg.Is<RedisValue[]>(values => values.Select(value => value.ToString()).OrderBy(static value => value).SequenceEqual(ExpectedIndexedLayerKeys)),
            Arg.Any<CommandFlags>());
        await distributedCache.DidNotReceive()
            .RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

}
