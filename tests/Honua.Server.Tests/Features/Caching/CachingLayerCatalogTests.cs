// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Caching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for CachingLayerCatalog - validates caching decorator behavior.
/// </summary>
[Protocol("Infrastructure")]
public sealed class CachingLayerCatalogTests : IDisposable
{
    private readonly MockLayerCatalog _innerCatalog;
    private readonly RedisCacheService _cacheService;
    private readonly CachingLayerCatalog _cachingCatalog;
    private readonly CacheOptions _options;

    public CachingLayerCatalogTests()
    {
        _innerCatalog = new MockLayerCatalog();
        _options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 300,
            LayerTtlSeconds = 60,
            ServiceTtlSeconds = 60,
            EnableFallback = true,
            FallbackMaxEntries = 100,
            KeyPrefix = "test:"
        };

        _cacheService = new RedisCacheService(
            null, // No Redis - tests fallback mode
            Options.Create(_options),
            new MockLogger<RedisCacheService>());

        _cachingCatalog = new CachingLayerCatalog(
            _innerCatalog,
            _cacheService,
            Options.Create(_options));
    }

    public void Dispose()
    {
        _cacheService.Dispose();
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetLayerAsync_FirstCall_QueriesInnerCatalog()
    {
        // Arrange
        _innerCatalog.GetLayerCallCount = 0;

        // Act
        var layer = await _cachingCatalog.GetLayerAsync(1);

        // Assert
        layer.Should().NotBeNull();
        layer!.Id.Should().Be(1);
        _innerCatalog.GetLayerCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetLayerAsync_SecondCall_UsesCachedValue()
    {
        // Arrange
        _innerCatalog.GetLayerCallCount = 0;

        // Act
        await _cachingCatalog.GetLayerAsync(1);
        await _cachingCatalog.GetLayerAsync(1);

        // Assert
        _innerCatalog.GetLayerCallCount.Should().Be(1); // Only called once
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task ListLayersAsync_FirstCall_QueriesInnerCatalog()
    {
        // Arrange
        _innerCatalog.ListLayersCallCount = 0;

        // Act
        var layers = await _cachingCatalog.ListLayersAsync();

        // Assert
        layers.Should().HaveCount(2);
        _innerCatalog.ListLayersCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task ListLayersAsync_SecondCall_UsesCachedValue()
    {
        // Arrange
        _innerCatalog.ListLayersCallCount = 0;

        // Act
        await _cachingCatalog.ListLayersAsync();
        await _cachingCatalog.ListLayersAsync();

        // Assert
        _innerCatalog.ListLayersCallCount.Should().Be(1); // Only called once
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetServiceAsync_FirstCall_QueriesInnerCatalog()
    {
        // Arrange
        _innerCatalog.GetServiceCallCount = 0;

        // Act
        var service = await _cachingCatalog.GetServiceAsync("TestService");

        // Assert
        service.Should().NotBeNull();
        service!.Name.Should().Be("TestService");
        _innerCatalog.GetServiceCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetServiceAsync_SecondCall_UsesCachedValue()
    {
        // Arrange
        _innerCatalog.GetServiceCallCount = 0;

        // Act
        await _cachingCatalog.GetServiceAsync("TestService");
        await _cachingCatalog.GetServiceAsync("TestService");

        // Assert
        _innerCatalog.GetServiceCallCount.Should().Be(1); // Only called once
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task InvalidateLayerAsync_ClearsLayerCache()
    {
        // Arrange
        await _cachingCatalog.GetLayerAsync(1);
        _innerCatalog.GetLayerCallCount = 0;

        // Act
        await _cachingCatalog.InvalidateLayerAsync(1);
        await _cachingCatalog.GetLayerAsync(1);

        // Assert
        _innerCatalog.GetLayerCallCount.Should().Be(1); // Called again after invalidation
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task InvalidateServiceAsync_ClearsServiceCache()
    {
        // Arrange
        await _cachingCatalog.GetServiceAsync("TestService");
        _innerCatalog.GetServiceCallCount = 0;

        // Act
        await _cachingCatalog.InvalidateServiceAsync("TestService");
        await _cachingCatalog.GetServiceAsync("TestService");

        // Assert
        _innerCatalog.GetServiceCallCount.Should().Be(1); // Called again after invalidation
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task LayerExistsAsync_WhenCached_ReturnsTrueWithoutQuery()
    {
        // Arrange
        await _cachingCatalog.GetLayerAsync(1);
        _innerCatalog.LayerExistsCallCount = 0;

        // Act
        var exists = await _cachingCatalog.LayerExistsAsync(1);

        // Assert
        exists.Should().BeTrue();
        _innerCatalog.LayerExistsCallCount.Should().Be(0); // Uses cached layer
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task ServiceExistsAsync_WhenCached_ReturnsTrueWithoutQuery()
    {
        // Arrange
        await _cachingCatalog.GetServiceAsync("TestService");
        _innerCatalog.ServiceExistsCallCount = 0;

        // Act
        var exists = await _cachingCatalog.ServiceExistsAsync("TestService");

        // Assert
        exists.Should().BeTrue();
        _innerCatalog.ServiceExistsCallCount.Should().Be(0); // Uses cached service
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task GetRelationshipAsync_FirstCall_QueriesInnerCatalog()
    {
        // Arrange
        _innerCatalog.GetRelationshipCallCount = 0;

        // Act
        var relationship = await _cachingCatalog.GetRelationshipAsync(1, 1);

        // Assert
        relationship.Should().NotBeNull();
        _innerCatalog.GetRelationshipCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation("Cache")]
    public async Task InvalidateAllAsync_ClearsAllCaches()
    {
        // Arrange
        await _cachingCatalog.GetLayerAsync(1);
        await _cachingCatalog.GetServiceAsync("TestService");
        await _cachingCatalog.ListLayersAsync();

        _innerCatalog.GetLayerCallCount = 0;
        _innerCatalog.GetServiceCallCount = 0;
        _innerCatalog.ListLayersCallCount = 0;

        // Act
        await _cachingCatalog.InvalidateAllAsync();

        await _cachingCatalog.GetLayerAsync(1);
        await _cachingCatalog.GetServiceAsync("TestService");
        await _cachingCatalog.ListLayersAsync();

        // Assert - All should be called again after invalidation
        _innerCatalog.GetLayerCallCount.Should().Be(1);
        _innerCatalog.GetServiceCallCount.Should().Be(1);
        _innerCatalog.ListLayersCallCount.Should().Be(1);
    }

    internal sealed class MockLayerCatalog : ILayerCatalog
    {
        public int GetLayerCallCount { get; set; }
        public int ListLayersCallCount { get; set; }
        public int GetServiceCallCount { get; set; }
        public int ListServicesCallCount { get; set; }
        public int LayerExistsCallCount { get; set; }
        public int ServiceExistsCallCount { get; set; }
        public int GetRelationshipCallCount { get; set; }
        public int ListRelationshipsCallCount { get; set; }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        {
            GetLayerCallCount++;
            return Task.FromResult<LayerDefinition?>(CreateTestLayer(layerId));
        }

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        {
            ListLayersCallCount++;
            return Task.FromResult(new[] { CreateTestLayer(1), CreateTestLayer(2) });
        }

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            GetServiceCallCount++;
            return Task.FromResult<ServiceDefinition?>(CreateTestService(serviceName));
        }

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        {
            ListServicesCallCount++;
            return Task.FromResult(new[] { CreateTestService("Service1"), CreateTestService("Service2") });
        }

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        {
            LayerExistsCallCount++;
            return Task.FromResult(layerId > 0 && layerId <= 2);
        }

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ServiceExistsCallCount++;
            return Task.FromResult(!string.IsNullOrEmpty(serviceName));
        }

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        {
            GetRelationshipCallCount++;
            return Task.FromResult<Relationship?>(Relationship.Create(relationshipId, "TestRelationship", 2, "esriRelCardinalityOneToMany", "id", "layer_id"));
        }

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        {
            ListRelationshipsCallCount++;
            return Task.FromResult(Array.Empty<Relationship>());
        }

        private static LayerDefinition CreateTestLayer(int id)
        {
            return new LayerDefinition(
                id,
                $"Layer{id}",
                $"Test layer {id}",
                GeometryType.Point,
                SpatialReference.WGS84,
                new[]
                {
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("name", FieldType.String, Length: 100)
                });
        }

        private static ServiceDefinition CreateTestService(string name)
        {
            return new ServiceDefinition(
                name,
                $"Test service {name}",
                new[] { CreateTestLayer(1) },
                SpatialReference.WGS84,
                1000,
                new[] { "json", "geojson" },
                new[] { "Query" });
        }
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
