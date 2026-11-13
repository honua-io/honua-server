// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Protocol(TestProtocols.TestQuality)]
public sealed partial class InMemoryCacheServiceTests : IDisposable
{
    private readonly InMemoryCacheService _cacheService;
    private readonly CacheOptions _options;
    private readonly IPerformanceMonitor _performanceMonitor;

    public InMemoryCacheServiceTests()
    {
        _options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 60,
            MaxEntries = 100,
            KeyPrefix = "test:"
        };

        _performanceMonitor = Substitute.For<IPerformanceMonitor>();
        _cacheService = new InMemoryCacheService(
            Options.Create(_options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task SetAsync_QueryResponseOverEntryCap_IsNotStored()
    {
        var options = new CacheOptions
        {
            Enabled = true,
            ResponseCacheMaxEntryBytes = 1024,
            KeyPrefix = "test:"
        };
        using var cache = new InMemoryCacheService(
            Options.Create(options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);
        const string queryKey = "response:query:featureserver:service:alpha:layer:1:abc";
        const string metadataKey = "metadata:layer:1";

        await cache.SetAsync(queryKey, new CachedResponse(new byte[32], "application/json"), TimeSpan.FromMinutes(1));
        var small = await cache.GetAsync<CachedResponse>(queryKey);
        small.Should().NotBeNull();
        small!.Payload.Length.Should().Be(32);

        await cache.SetAsync(queryKey, new CachedResponse(new byte[4096], "application/json"), TimeSpan.FromMinutes(1));
        var unchanged = await cache.GetAsync<CachedResponse>(queryKey);
        unchanged.Should().NotBeNull();
        unchanged!.Payload.Length.Should().Be(32);

        await cache.SetAsync(metadataKey, new CachedResponse(new byte[4096], "application/json"), TimeSpan.FromMinutes(1));
        var metadata = await cache.GetAsync<CachedResponse>(metadataKey);
        metadata.Should().NotBeNull();
        metadata!.Payload.Length.Should().Be(4096);
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

        using var cache = new InMemoryCacheService(
            Options.Create(_options),
            NullLogger<InMemoryCacheService>.Instance,
            performanceMonitor);

        _ = await cache.GetAsync<MetadataV2Field>("layer:42:token=secret");

        operationScope.Received().WithTag("cache_type", "layer-catalog");
        operationScope.Received().WithTag("key_family", "layer");
        operationScope.DidNotReceive().WithTag("key", Arg.Any<string>());
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

            GetEntryKeys().Should().ContainSingle()
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
    public void IsProcessLocal_IsTrueWithoutFallback()
    {
        _cacheService.IsUsingFallback.Should().BeFalse();
        _cacheService.IsProcessLocal.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task IsCacheHealthyAsync_ReturnsTrue()
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
            MaxEntries = 3,
            KeyPrefix = "limit:"
        };

        using var cache = new InMemoryCacheService(
            Options.Create(options),
            NullLogger<InMemoryCacheService>.Instance,
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
    public async Task CacheDisabled_ReturnsNullAndDoesNotStore()
    {
        // Arrange
        var options = new CacheOptions { Enabled = false };
        using var disabledCache = new InMemoryCacheService(
            Options.Create(options),
            NullLogger<InMemoryCacheService>.Instance,
            _performanceMonitor);

        // Act
        await disabledCache.SetAsync("key", new MetadataV2Field { Name = "Test", Type = MetadataV2FieldType.String, Length = 10 });
        var result = await disabledCache.GetAsync<MetadataV2Field>("key");

        // Assert
        result.Should().BeNull();
    }

    private string[] GetEntryKeys()
        => GetEntryKeys(_cacheService);

    private static string[] GetEntryKeys(InMemoryCacheService cacheService)
    {
        var entriesField = typeof(InMemoryCacheService).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
        entriesField.Should().NotBeNull();

        var entries = entriesField!.GetValue(cacheService) as System.Collections.IEnumerable;
        if (entries is null)
        {
            throw new InvalidOperationException("_entries field was not the expected IEnumerable type.");
        }

        return entries
            .Cast<object>()
            .Select(entry => (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!)
            .ToArray();
    }
}
