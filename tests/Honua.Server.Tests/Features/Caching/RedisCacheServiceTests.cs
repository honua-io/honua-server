// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Server.Features.Caching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for RedisCacheService - validates caching with fallback behavior.
/// </summary>
[Protocol("Infrastructure")]
public sealed class RedisCacheServiceTests : IDisposable
{
    private readonly RedisCacheService _cacheService;
    private readonly CacheOptions _options;

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

        _cacheService = new RedisCacheService(
            null, // No Redis - tests fallback mode
            Options.Create(_options),
            new MockLogger<RedisCacheService>());
    }

    public void Dispose()
    {
        _cacheService.Dispose();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetAsync_WhenKeyNotFound_ReturnsNull()
    {
        // Act
        var result = await _cacheService.GetAsync<TestCacheItem>("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task SetAsync_ThenGetAsync_ReturnsCachedValue()
    {
        // Arrange
        var item = new TestCacheItem { Id = 1, Name = "Test" };

        // Act
        await _cacheService.SetAsync("item:1", item);
        var result = await _cacheService.GetAsync<TestCacheItem>("item:1");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.Name.Should().Be("Test");
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task SetAsync_WithTtl_ExpiresAfterTtl()
    {
        // Arrange
        var item = new TestCacheItem { Id = 1, Name = "Test" };
        var shortTtl = TimeSpan.FromMilliseconds(50);

        // Act
        await _cacheService.SetAsync("item:short", item, shortTtl);

        // Wait for expiration
        await Task.Delay(100);

        var result = await _cacheService.GetAsync<TestCacheItem>("item:short");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task RemoveAsync_RemovesCachedValue()
    {
        // Arrange
        var item = new TestCacheItem { Id = 1, Name = "Test" };
        await _cacheService.SetAsync("item:remove", item);

        // Act
        await _cacheService.RemoveAsync("item:remove");
        var result = await _cacheService.GetAsync<TestCacheItem>("item:remove");

        // Assert
        result.Should().BeNull();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetOrSetAsync_WhenNotCached_CallsFactory()
    {
        // Arrange
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrSetAsync<TestCacheItem>("item:factory", async ct =>
        {
            factoryCalled = true;
            return new TestCacheItem { Id = 2, Name = "FromFactory" };
        });

        // Assert
        factoryCalled.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Name.Should().Be("FromFactory");
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetOrSetAsync_WhenCached_DoesNotCallFactory()
    {
        // Arrange
        var item = new TestCacheItem { Id = 1, Name = "Cached" };
        await _cacheService.SetAsync("item:cached", item);
        var factoryCalled = false;

        // Act
        var result = await _cacheService.GetOrSetAsync<TestCacheItem>("item:cached", async ct =>
        {
            factoryCalled = true;
            return new TestCacheItem { Id = 2, Name = "NotUsed" };
        });

        // Assert
        factoryCalled.Should().BeFalse();
        result.Should().NotBeNull();
        result!.Name.Should().Be("Cached");
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task RemoveByPatternAsync_RemovesMatchingKeys()
    {
        // Arrange
        await _cacheService.SetAsync("layer:1", new TestCacheItem { Id = 1, Name = "Layer1" });
        await _cacheService.SetAsync("layer:2", new TestCacheItem { Id = 2, Name = "Layer2" });
        await _cacheService.SetAsync("service:1", new TestCacheItem { Id = 3, Name = "Service1" });

        // Act
        await _cacheService.RemoveByPatternAsync("layer:*");

        // Assert
        var layer1 = await _cacheService.GetAsync<TestCacheItem>("layer:1");
        var layer2 = await _cacheService.GetAsync<TestCacheItem>("layer:2");
        var service1 = await _cacheService.GetAsync<TestCacheItem>("service:1");

        layer1.Should().BeNull();
        layer2.Should().BeNull();
        service1.Should().NotBeNull();
    }

    [UnitTest]
    [Operation("Cache")]
    public void IsUsingFallback_WithNoRedis_ReturnsTrue()
    {
        // Assert
        _cacheService.IsUsingFallback.Should().BeTrue();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task IsCacheHealthyAsync_WithFallbackEnabled_ReturnsTrue()
    {
        // Act
        var isHealthy = await _cacheService.IsCacheHealthyAsync();

        // Assert
        isHealthy.Should().BeTrue();
    }

    [UnitTest]
    [Operation("Cache")]
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
            new MockLogger<RedisCacheService>());

        // Act - Add more entries than limit
        await cache.SetAsync("a", new TestCacheItem { Id = 1, Name = "A" });
        await cache.SetAsync("b", new TestCacheItem { Id = 2, Name = "B" });
        await cache.SetAsync("c", new TestCacheItem { Id = 3, Name = "C" });
        await cache.SetAsync("d", new TestCacheItem { Id = 4, Name = "D" });

        // Assert - Newest entries should be present
        var d = await cache.GetAsync<TestCacheItem>("d");
        d.Should().NotBeNull();

        // At least one old entry should have been evicted
        var a = await cache.GetAsync<TestCacheItem>("a");
        var b = await cache.GetAsync<TestCacheItem>("b");
        var c = await cache.GetAsync<TestCacheItem>("c");

        // Total should not exceed limit
        var presentCount = new[] { a, b, c, d }.Count(x => x != null);
        presentCount.Should().BeLessOrEqualTo(3);
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task CacheDisabled_ReturnsNullAndDoesNotStore()
    {
        // Arrange
        var options = new CacheOptions { Enabled = false };
        using var disabledCache = new RedisCacheService(
            null,
            Options.Create(options),
            new MockLogger<RedisCacheService>());

        // Act
        await disabledCache.SetAsync("key", new TestCacheItem { Id = 1, Name = "Test" });
        var result = await disabledCache.GetAsync<TestCacheItem>("key");

        // Assert
        result.Should().BeNull();
    }

    internal sealed class TestCacheItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
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
