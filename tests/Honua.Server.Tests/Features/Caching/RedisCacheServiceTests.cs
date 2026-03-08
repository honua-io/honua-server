// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for RedisCacheService - validates caching with fallback behavior.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class RedisCacheServiceTests : IDisposable
{
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
            new MockLogger<RedisCacheService>(),
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
        var result = await _cacheService.GetAsync<FieldDefinition>("nonexistent");

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
            new MockLogger<RedisCacheService>(),
            performanceMonitor);

        _ = await cache.GetAsync<FieldDefinition>("layer:42:token=secret");

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
            new MockLogger<RedisCacheService>(),
            performanceMonitor);

        _ = await cache.GetAsync<FieldDefinition>("layer:42:token=secret");

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
        var item = new FieldDefinition("objectid", FieldType.Integer, Nullable: false);

        // Act
        await _cacheService.SetAsync("item:1", item);
        var result = await _cacheService.GetAsync<FieldDefinition>("item:1");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("objectid");
        result.Type.Should().Be(FieldType.Integer);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_WithTtl_ExpiresAfterTtl()
    {
        // Arrange
        var item = new FieldDefinition("objectid", FieldType.Integer, Nullable: false);
        var shortTtl = TimeSpan.FromMilliseconds(50);

        // Act
        await _cacheService.SetAsync("item:short", item, shortTtl);

        // Wait for expiration
        await Task.Delay(100);

        var result = await _cacheService.GetAsync<FieldDefinition>("item:short");

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
        var item = new FieldDefinition("objectid", FieldType.Integer, Nullable: false);
        await _cacheService.SetAsync("item:remove", item);

        // Act
        await _cacheService.RemoveAsync("item:remove");
        var result = await _cacheService.GetAsync<FieldDefinition>("item:remove");

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
        var result = await _cacheService.GetOrSetAsync<FieldDefinition>("item:factory", async ct =>
        {
            factoryCalled = true;
            return new FieldDefinition("fromFactory", FieldType.String, Length: 20);
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
        var item = new FieldDefinition("cached", FieldType.Integer, Nullable: false);
        await _cacheService.SetAsync("item:cached", item);
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrSetAsync<FieldDefinition>("item:cached", async ct =>
        {
            factoryCalled = true;
            return new FieldDefinition("notUsed", FieldType.String, Length: 20);
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
        await _cacheService.SetAsync("layer:1", new FieldDefinition("Layer1", FieldType.String, Length: 10));
        await _cacheService.SetAsync("layer:2", new FieldDefinition("Layer2", FieldType.String, Length: 10));
        await _cacheService.SetAsync("service:1", new FieldDefinition("Service1", FieldType.String, Length: 10));

        // Act
        await _cacheService.RemoveByPatternAsync("layer:*");

        // Assert
        var layer1 = await _cacheService.GetAsync<FieldDefinition>("layer:1");
        var layer2 = await _cacheService.GetAsync<FieldDefinition>("layer:2");
        var service1 = await _cacheService.GetAsync<FieldDefinition>("service:1");

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
            new MockLogger<RedisCacheService>(),
            _performanceMonitor);

        // Act - Add more entries than limit
        await cache.SetAsync("a", new FieldDefinition("A", FieldType.String, Length: 10));
        await cache.SetAsync("b", new FieldDefinition("B", FieldType.String, Length: 10));
        await cache.SetAsync("c", new FieldDefinition("C", FieldType.String, Length: 10));
        await cache.SetAsync("d", new FieldDefinition("D", FieldType.String, Length: 10));

        // Assert - Newest entries should be present
        var d = await cache.GetAsync<FieldDefinition>("d");
        d.Should().NotBeNull();

        // At least one old entry should have been evicted
        var a = await cache.GetAsync<FieldDefinition>("a");
        var b = await cache.GetAsync<FieldDefinition>("b");
        var c = await cache.GetAsync<FieldDefinition>("c");

        // Total should not exceed limit
        var presentCount = new[] { a, b, c, d }.Count(x => x != null);
        presentCount.Should().BeLessOrEqualTo(3);
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
            new MockLogger<RedisCacheService>(),
            _performanceMonitor);

        // Act
        await disabledCache.SetAsync("key", new FieldDefinition("Test", FieldType.String, Length: 10));
        var result = await disabledCache.GetAsync<FieldDefinition>("key");

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
        keyLocks.Should().NotBeNull();

        var key = "prune:test";
        var semaphore = new SemaphoreSlim(1, 1);
        keyLocks![key] = semaphore;

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
}
