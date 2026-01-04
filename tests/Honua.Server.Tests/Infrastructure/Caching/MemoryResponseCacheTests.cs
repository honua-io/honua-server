// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Caching;

/// <summary>
/// Tests for memory response cache implementation
/// </summary>
[Collection("Unit")]
public class MemoryResponseCacheTests : IDisposable
{
    private readonly MemoryResponseCache _cache;
    private readonly MemoryCache _memoryCache;

    public MemoryResponseCacheTests()
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _cache = new MemoryResponseCache(_memoryCache, NullLogger<MemoryResponseCache>.Instance);
    }

    [Fact]
    public async Task GetAsync_NonExistentKey_ReturnsNull()
    {
        // Act
        var result = await _cache.GetAsync<string>("nonexistent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAndGetAsync_ValidKeyValue_ReturnsStoredValue()
    {
        // Arrange
        var key = "test-key";
        var value = "test-value";
        var expiration = TimeSpan.FromMinutes(5);

        // Act
        await _cache.SetAsync(key, value, expiration);
        var result = await _cache.GetAsync<string>(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task GetOrCreateAsync_NonExistentKey_CallsFactory()
    {
        // Arrange
        var key = "factory-key";
        var expectedValue = "factory-value";
        var factoryCalled = false;

        // Act
        var result = await _cache.GetOrCreateAsync(
            key,
            async () =>
            {
                factoryCalled = true;
                await Task.Delay(10); // Simulate async work
                return expectedValue;
            },
            TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(expectedValue, result);
        Assert.True(factoryCalled);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExistingKey_DoesNotCallFactory()
    {
        // Arrange
        var key = "existing-key";
        var originalValue = "original-value";
        var newValue = "new-value";
        var factoryCalled = false;

        await _cache.SetAsync(key, originalValue, TimeSpan.FromMinutes(5));

        // Act
        var result = await _cache.GetOrCreateAsync(
            key,
            async () =>
            {
                factoryCalled = true;
                await Task.Delay(10);
                return newValue;
            },
            TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(originalValue, result);
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RemoveAsync_ExistingKey_RemovesValue()
    {
        // Arrange
        var key = "remove-key";
        var value = "remove-value";

        await _cache.SetAsync(key, value, TimeSpan.FromMinutes(5));

        // Act
        await _cache.RemoveAsync(key);
        var result = await _cache.GetAsync<string>(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveByPatternAsync_MatchingKeys_RemovesAllMatching()
    {
        // Arrange
        await _cache.SetAsync("layer:definition:1", "layer1", TimeSpan.FromMinutes(5));
        await _cache.SetAsync("layer:definition:2", "layer2", TimeSpan.FromMinutes(5));
        await _cache.SetAsync("layer:metadata:1", "metadata1", TimeSpan.FromMinutes(5));
        await _cache.SetAsync("service:definition:1", "service1", TimeSpan.FromMinutes(5));

        // Act
        await _cache.RemoveByPatternAsync("layer:*");

        // Assert
        Assert.Null(await _cache.GetAsync<string>("layer:definition:1"));
        Assert.Null(await _cache.GetAsync<string>("layer:definition:2"));
        Assert.Null(await _cache.GetAsync<string>("layer:metadata:1"));
        Assert.NotNull(await _cache.GetAsync<string>("service:definition:1"));
    }

    [Fact]
    public async Task SetAsync_ComplexObject_StoresAndRetrievesCorrectly()
    {
        // Arrange
        var key = "complex-object";
        var value = new TestObject
        {
            Id = 123,
            Name = "Test Object",
            Tags = new[] { "tag1", "tag2" },
            Metadata = new Dictionary<string, object>
            {
                ["property1"] = "value1",
                ["property2"] = 42
            }
        };

        // Act
        await _cache.SetAsync(key, value, TimeSpan.FromMinutes(5));
        var result = await _cache.GetAsync<TestObject>(key);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(value.Id, result.Id);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Tags, result.Tags);
        Assert.Equal(value.Metadata, result.Metadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAsync_InvalidKey_ThrowsException(string invalidKey)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _cache.GetAsync<string>(invalidKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetAsync_InvalidKey_ThrowsException(string invalidKey)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _cache.SetAsync(invalidKey, "value", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task SetAsync_NullValue_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _cache.SetAsync("key", (string)null!, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task GetOrCreateAsync_NullFactory_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _cache.GetOrCreateAsync<string>("key", null!, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task GetAsync_WrongType_ReturnsNull()
    {
        // Arrange
        await _cache.SetAsync("string-key", "string-value", TimeSpan.FromMinutes(5));

        // Act
        var result = await _cache.GetAsync<string>("string-key");

        // Assert
        Assert.Equal("string-value", result);
    }

    [Fact]
    public async Task SetAsync_DifferentExpirationTimes_RespectsExpiration()
    {
        // Arrange
        var shortKey = "short-lived";
        var longKey = "long-lived";

        // Act
        await _cache.SetAsync(shortKey, "short-value", TimeSpan.FromMilliseconds(50));
        await _cache.SetAsync(longKey, "long-value", TimeSpan.FromMinutes(5));

        await Task.Delay(100); // Wait for short-lived to expire

        // Assert
        Assert.Null(await _cache.GetAsync<string>(shortKey));
        Assert.NotNull(await _cache.GetAsync<string>(longKey));
    }

    public void Dispose()
    {
        _cache?.Dispose();
        _memoryCache?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}
