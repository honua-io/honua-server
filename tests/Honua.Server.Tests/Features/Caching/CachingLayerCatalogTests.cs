// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for CachingLayerCatalog - validates caching decorator behavior.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class CachingLayerCatalogTests : IDisposable
{
    private readonly MockLayerCatalog _innerCatalog;
    private readonly RedisCacheService _cacheService;
    private readonly CachingLayerCatalog _cachingCatalog;
    private readonly CacheOptions _options;
    private readonly IPerformanceMonitor _performanceMonitor;

    public CachingLayerCatalogTests()
    {
        _innerCatalog = new MockLayerCatalog();
        _options = new CacheOptions
        {
            Enabled = true,
            DefaultTtlSeconds = 300,
            LayerTtlSeconds = 60,
            ServiceTtlSeconds = 60,
            NegativeTtlSeconds = 30,
            JitterPercentage = 0,
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
    public async Task ListLayersAsync_MultipleConcurrentCacheMisses_QueryInnerCatalogOnce()
    {
        // Arrange
        _innerCatalog.ListLayersCallCount = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _innerCatalog.ListLayersEntered = entered;
        _innerCatalog.ListLayersRelease = release;

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _cachingCatalog.ListLayersAsync())
            .ToArray();

        await entered.Task.ConfigureAwait(false);
        _innerCatalog.ListLayersCallCount.Should().Be(1);
        release.SetResult(true);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert
        results.Should().OnlyContain(layers => layers.Length == 2);
        _innerCatalog.ListLayersCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
    public async Task ListServicesAsync_MultipleConcurrentCacheMisses_QueryInnerCatalogOnce()
    {
        // Arrange
        _innerCatalog.ListServicesCallCount = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _innerCatalog.ListServicesEntered = entered;
        _innerCatalog.ListServicesRelease = release;

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _cachingCatalog.ListServicesAsync())
            .ToArray();

        await entered.Task.ConfigureAwait(false);
        _innerCatalog.ListServicesCallCount.Should().Be(1);
        release.SetResult(true);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert
        results.Should().OnlyContain(services => services.Length == 2);
        _innerCatalog.ListServicesCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_MissingLayer_UsesNegativeCache()
    {
        // Arrange
        _innerCatalog.MissingLayerIds.Add(9);
        _innerCatalog.GetLayerCallCount = 0;

        // Act
        var first = await _cachingCatalog.GetLayerAsync(9);
        var second = await _cachingCatalog.GetLayerAsync(9);

        // Assert
        first.Should().BeNull();
        second.Should().BeNull();
        _innerCatalog.GetLayerCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServiceExistsAsync_MissingService_UsesNegativeCache()
    {
        // Arrange
        _innerCatalog.MissingServiceNames.Add("MissingService");
        _innerCatalog.ServiceExistsCallCount = 0;

        // Act
        var first = await _cachingCatalog.ServiceExistsAsync("MissingService");
        var second = await _cachingCatalog.ServiceExistsAsync("MissingService");

        // Assert
        first.Should().BeFalse();
        second.Should().BeFalse();
        _innerCatalog.ServiceExistsCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task LayerExistsAsync_MissingLayer_UsesConfiguredNegativeTtl()
    {
        _innerCatalog.MissingLayerIds.Add(77);

        var first = await _cachingCatalog.LayerExistsAsync(77);
        first.Should().BeFalse();

        await Task.Delay(TimeSpan.FromSeconds(_options.NegativeTtlSeconds + 1));

        var second = await _cachingCatalog.LayerExistsAsync(77);
        second.Should().BeFalse();
        _innerCatalog.LayerExistsCallCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task LayerExistsAsync_MultipleConcurrentCacheMisses_QueryInnerCatalogOnce()
    {
        // Arrange
        _innerCatalog.MissingLayerIds.Add(88);
        _innerCatalog.LayerExistsCallCount = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _innerCatalog.LayerExistsEntered = entered;
        _innerCatalog.LayerExistsRelease = release;

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _cachingCatalog.LayerExistsAsync(88))
            .ToArray();

        await entered.Task.ConfigureAwait(false);
        _innerCatalog.LayerExistsCallCount.Should().Be(1);
        release.SetResult(true);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert
        results.Should().OnlyContain(exists => !exists);
        _innerCatalog.LayerExistsCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServiceExistsAsync_MultipleConcurrentCacheMisses_QueryInnerCatalogOnce()
    {
        // Arrange
        _innerCatalog.MissingServiceNames.Add("missing-concurrent");
        _innerCatalog.ServiceExistsCallCount = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _innerCatalog.ServiceExistsEntered = entered;
        _innerCatalog.ServiceExistsRelease = release;

        // Act
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => _cachingCatalog.ServiceExistsAsync("missing-concurrent"))
            .ToArray();

        await entered.Task.ConfigureAwait(false);
        _innerCatalog.ServiceExistsCallCount.Should().Be(1);
        release.SetResult(true);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assert
        results.Should().OnlyContain(exists => !exists);
        _innerCatalog.ServiceExistsCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task LayerExistsAsync_MissingLayer_CachesNegativeResultWithNegativeTtl()
    {
        var options = new CacheOptions
        {
            Enabled = true,
            LayerTtlSeconds = 60,
            ServiceTtlSeconds = 60,
            NegativeTtlSeconds = 5,
            JitterPercentage = 0,
            EnableFallback = true,
            FallbackMaxEntries = 100,
            KeyPrefix = "neg-layer:"
        };

        using var cache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);
        var catalog = new MockLayerCatalog();
        catalog.MissingLayerIds.Add(101);
        var cachingCatalog = new CachingLayerCatalog(catalog, cache, Options.Create(options));

        var exists = await cachingCatalog.LayerExistsAsync(101);
        exists.Should().BeFalse();

        var metadata = await cache.GetWithMetadataAsync<CachedExistenceResult>(
            $"{CachingLayerCatalog.LayerExistsKeyPrefix}101");

        metadata.HasValue.Should().BeTrue();
        metadata.Value!.Exists.Should().BeFalse();
        metadata.RemainingTtl.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ServiceExistsAsync_MissingService_CachesNegativeResultWithNegativeTtl()
    {
        var options = new CacheOptions
        {
            Enabled = true,
            LayerTtlSeconds = 60,
            ServiceTtlSeconds = 60,
            NegativeTtlSeconds = 5,
            JitterPercentage = 0,
            EnableFallback = true,
            FallbackMaxEntries = 100,
            KeyPrefix = "neg-service:"
        };

        using var cache = new RedisCacheService(
            null,
            Options.Create(options),
            NullLogger<RedisCacheService>.Instance,
            _performanceMonitor);
        var catalog = new MockLayerCatalog();
        catalog.MissingServiceNames.Add("ghost");
        var cachingCatalog = new CachingLayerCatalog(catalog, cache, Options.Create(options));

        var exists = await cachingCatalog.ServiceExistsAsync("ghost");
        exists.Should().BeFalse();

        var metadata = await cache.GetWithMetadataAsync<CachedExistenceResult>(
            $"{CachingLayerCatalog.ServiceExistsKeyPrefix}ghost");

        metadata.HasValue.Should().BeTrue();
        metadata.Value!.Exists.Should().BeFalse();
        metadata.RemainingTtl.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_DoesNotUsePatternInvalidation()
    {
        var cache = Substitute.For<ICacheService>();
        var catalog = new CachingLayerCatalog(_innerCatalog, cache, Options.Create(_options));

        await catalog.InvalidateLayerAsync(7);

        await cache.DidNotReceive().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.Received().RemoveAsync("scope:default:layer:7", Arg.Any<CancellationToken>());
        await cache.Received().RemoveAsync("scope:default:layer:exists:7", Arg.Any<CancellationToken>());
        await cache.Received().RemoveAsync("scope:default:layers:all", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
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
    [Operation(Operations.Cache)]
    public async Task GetRelationshipAsync_FirstCall_QueriesInnerCatalog()
    {
        // Arrange
        _innerCatalog.GetRelationshipCallCount = 0;

        // Act
        var relationship = await _cachingCatalog.GetRelationshipAsync(1, 1);

        // Assert
        relationship.Should().NotBeNull();
        _innerCatalog.GetRelationshipCallCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
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

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateLayerAsync_DoesNotUsePatternDeletion()
    {
        var cache = new RecordingCacheService();
        var catalog = new CachingLayerCatalog(new MockLayerCatalog(), cache, Options.Create(_options));

        await catalog.GetLayerAsync(1);
        await catalog.InvalidateLayerAsync(1);

        cache.PatternRemovals.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateAllAsync_DoesNotUsePatternDeletion()
    {
        var cache = new RecordingCacheService();
        var catalog = new CachingLayerCatalog(new MockLayerCatalog(), cache, Options.Create(_options));

        await catalog.GetLayerAsync(1);
        await catalog.GetServiceAsync("TestService");
        await catalog.ListLayersAsync();
        await catalog.InvalidateAllAsync();

        cache.PatternRemovals.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task InvalidateAllAsync_BumpsGenerationWithoutPatternInvalidation()
    {
        var cache = Substitute.For<ICacheService>();

        var catalog = new CachingLayerCatalog(_innerCatalog, cache, Options.Create(_options));

        await catalog.InvalidateAllAsync();

        await cache.DidNotReceive().RemoveByPatternAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.Received().SetAsync(
            "scope:default:catalog:generation",
            Arg.Is<string>(generation => !string.IsNullOrWhiteSpace(generation)),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_DifferentSchemaContexts_DoNotShareCacheEntries()
    {
        using var sharedCache = new RedisCacheService(
            null,
            Options.Create(_options),
            new MockLogger<RedisCacheService>(),
            _performanceMonitor);

        var innerCatalogA = new MockLayerCatalog("A");
        var innerCatalogB = new MockLayerCatalog("B");
        var catalogA = new CachingLayerCatalog(
            innerCatalogA,
            sharedCache,
            Options.Create(_options),
            new TestSchemaContext("schema_a"));
        var catalogB = new CachingLayerCatalog(
            innerCatalogB,
            sharedCache,
            Options.Create(_options),
            new TestSchemaContext("schema_b"));

        var layerA = await catalogA.GetLayerAsync(1);
        var layerB = await catalogB.GetLayerAsync(1);

        layerA.Should().NotBeNull();
        layerB.Should().NotBeNull();
        layerA!.Name.Should().Be("ALayer1");
        layerB!.Name.Should().Be("BLayer1");
        innerCatalogA.GetLayerCallCount.Should().Be(1);
        innerCatalogB.GetLayerCallCount.Should().Be(1);
    }

    internal sealed class MockLayerCatalog : ILayerCatalog
    {
        private static readonly string[] _defaultFormats = ["json", "geojson"];
        private static readonly string[] _defaultCapabilities = ["Query"];
        private static readonly Relationship[] _defaultRelationships =
        [
            Relationship.Create(1, "TestRelationship", 2, "esriRelCardinalityOneToMany", "id", "layer_id")
        ];
        public HashSet<int> MissingLayerIds { get; } = new();
        public HashSet<string> MissingServiceNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int GetLayerCallCount { get; set; }
        public int ListLayersCallCount { get; set; }
        public int GetServiceCallCount { get; set; }
        public int ListServicesCallCount { get; set; }
        public int LayerExistsCallCount { get; set; }
        public int ServiceExistsCallCount { get; set; }
        public int GetRelationshipCallCount { get; set; }
        public int ListRelationshipsCallCount { get; set; }
        public TaskCompletionSource<bool>? ListLayersEntered { get; set; }
        public TaskCompletionSource<bool>? ListLayersRelease { get; set; }
        public TaskCompletionSource<bool>? ListServicesEntered { get; set; }
        public TaskCompletionSource<bool>? ListServicesRelease { get; set; }
        public TaskCompletionSource<bool>? LayerExistsEntered { get; set; }
        public TaskCompletionSource<bool>? LayerExistsRelease { get; set; }
        public TaskCompletionSource<bool>? ServiceExistsEntered { get; set; }
        public TaskCompletionSource<bool>? ServiceExistsRelease { get; set; }
        private readonly string _namePrefix;

        public MockLayerCatalog(string namePrefix = "")
        {
            _namePrefix = namePrefix;
        }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        {
            GetLayerCallCount++;
            if (MissingLayerIds.Contains(layerId))
            {
                return Task.FromResult<LayerDefinition?>(null);
            }
            return Task.FromResult<LayerDefinition?>(CreateTestLayer(layerId, _namePrefix));
        }

        public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        {
            ListLayersCallCount++;
            ListLayersEntered?.TrySetResult(true);
            if (ListLayersRelease != null)
            {
                await ListLayersRelease.Task.ConfigureAwait(false);
            }

            return [CreateTestLayer(1, _namePrefix), CreateTestLayer(2, _namePrefix)];
        }

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            GetServiceCallCount++;
            if (MissingServiceNames.Contains(serviceName))
            {
                return Task.FromResult<ServiceDefinition?>(null);
            }
            return Task.FromResult<ServiceDefinition?>(CreateTestService(serviceName, _namePrefix));
        }

        public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        {
            ListServicesCallCount++;
            ListServicesEntered?.TrySetResult(true);
            if (ListServicesRelease != null)
            {
                await ListServicesRelease.Task.ConfigureAwait(false);
            }

            return [CreateTestService("Service1", _namePrefix), CreateTestService("Service2", _namePrefix)];
        }

        public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        {
            LayerExistsCallCount++;
            LayerExistsEntered?.TrySetResult(true);
            if (LayerExistsRelease != null)
            {
                await LayerExistsRelease.Task.ConfigureAwait(false);
            }

            if (MissingLayerIds.Contains(layerId))
            {
                return false;
            }
            return layerId > 0 && layerId <= 2;
        }

        public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ServiceExistsCallCount++;
            ServiceExistsEntered?.TrySetResult(true);
            if (ServiceExistsRelease != null)
            {
                await ServiceExistsRelease.Task.ConfigureAwait(false);
            }

            if (MissingServiceNames.Contains(serviceName))
            {
                return false;
            }

            return !string.IsNullOrEmpty(serviceName);
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

        private static LayerDefinition CreateTestLayer(int id, string namePrefix)
        {
            return new LayerDefinition(
                id,
                $"{namePrefix}Layer{id}",
                $"Test layer {id}",
                GeometryType.Point,
                SpatialReference.WGS84,
                new[]
                {
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("name", FieldType.String, Length: 100)
                },
                Relationships: _defaultRelationships);
        }

        private static ServiceDefinition CreateTestService(string name, string namePrefix)
        {
            return new ServiceDefinition(
                $"{namePrefix}{name}",
                $"Test service {name}",
                new[] { CreateTestLayer(1, namePrefix) },
                SpatialReference.WGS84,
                _defaultFormats,
                _defaultCapabilities);
        }
    }

    private sealed class TestSchemaContext(string currentSchema) : ISchemaContext
    {
        public string? CurrentSchema { get; } = currentSchema;
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

    private sealed class RecordingCacheService : ICacheService
    {
        private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);

        public List<string> PatternRemovals { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? value as T : null);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
            => SetAsync(key, value, TimeSpan.FromMinutes(5), cancellationToken);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            _entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        {
            PatternRemovals.Add(pattern);
            return Task.CompletedTask;
        }

        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => await GetOrSetAsync(key, factory, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);

        public async Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            if (_entries.TryGetValue(key, out var cached) && cached is T typed)
            {
                return typed;
            }

            var created = await factory(cancellationToken).ConfigureAwait(false);
            if (created != null)
            {
                _entries[key] = created;
            }

            return created;
        }

        public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (_entries.TryGetValue(key, out var cached) && cached is T typed)
            {
                return Task.FromResult(new CacheEntryMetadata<T>(typed, TimeSpan.FromMinutes(5)));
            }

            return Task.FromResult(CacheEntryMetadata<T>.Miss());
        }
    }
}
