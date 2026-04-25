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
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

/// <summary>
/// Tests for BackgroundRefreshCacheDecorator — validates stale-while-revalidate behavior,
/// near-expiry detection, write-through refresh, and refresh deduplication.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class BackgroundRefreshCacheDecoratorTests : IDisposable
{
    private readonly MockLayerCatalog _innerCatalog;
    private readonly MockCacheService _cacheService;
    private readonly MockRefreshCoordinator _refreshCoordinator;
    private readonly BackgroundRefreshCacheDecorator _decorator;
    private readonly CacheOptions _options;

    public BackgroundRefreshCacheDecoratorTests()
    {
        _innerCatalog = new MockLayerCatalog();
        _cacheService = new MockCacheService();
        _refreshCoordinator = new MockRefreshCoordinator();

        _options = new CacheOptions
        {
            Enabled = true,
            LayerTtlSeconds = 60,
            ServiceTtlSeconds = 120,
            JitterPercentage = 0,
            BackgroundRefreshEnabled = true,
            BackgroundRefreshThreshold = 0.25
        };

        // Scope factory returns _innerCatalog as the keyed "uncached" catalog
        var scopeFactory = new TestServiceScopeFactory(_innerCatalog);

        _decorator = new BackgroundRefreshCacheDecorator(
            _innerCatalog,
            _cacheService,
            _refreshCoordinator,
            scopeFactory,
            Options.Create(_options),
            NullLogger<BackgroundRefreshCacheDecorator>.Instance);
    }

    public void Dispose()
    {
    }

    private static string ScopedKey(string key, string? schema = null) => CacheScopeKeys.EnsureScoped(key, schema);

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_CacheHitNotNearExpiry_ReturnsWithoutRefresh()
    {
        // Arrange: cached entry with plenty of TTL remaining (50s out of 60s)
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(50));

        // Act
        var result = await _decorator.GetLayerAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        _refreshCoordinator.EnqueuedKeys.Should().BeEmpty();
        _innerCatalog.GetLayerCallCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_CacheHitNearExpiry_ReturnsStaleAndEnqueuesRefresh()
    {
        // Arrange: cached entry with only 10s remaining out of 60s (16% < 25% threshold)
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act
        var result = await _decorator.GetLayerAsync(1);

        // Assert: stale value returned immediately
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);

        // Assert: background refresh was enqueued
        _refreshCoordinator.EnqueuedKeys.Should().Contain(ScopedKey("layer:1"));
        _innerCatalog.GetLayerCallCount.Should().Be(0); // NOT called synchronously
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_CacheMiss_DelegatesToInnerCatalog()
    {
        // Arrange: nothing in cache

        // Act
        var result = await _decorator.GetLayerAsync(1);

        // Assert: inner catalog called synchronously (cold miss)
        result.Should().NotBeNull();
        _innerCatalog.GetLayerCallCount.Should().Be(1);
        _refreshCoordinator.EnqueuedKeys.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetServiceAsync_NearExpiry_EnqueuesRefresh()
    {
        // Arrange: service cached with 20s remaining out of 120s (16% < 25% threshold)
        var service = CreateTestService("TestService");
        _cacheService.SetEntry("service:testservice", service, TimeSpan.FromSeconds(20));

        // Act
        var result = await _decorator.GetServiceAsync("TestService");

        // Assert
        result.Should().NotBeNull();
        _refreshCoordinator.EnqueuedKeys.Should().Contain(ScopedKey("service:testservice"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ListLayersAsync_NearExpiry_EnqueuesRefresh()
    {
        // Arrange: layer list cached near expiry
        var layers = new[] { CreateTestLayer(1), CreateTestLayer(2) };
        _cacheService.SetEntry("layers:all", new CachedLayerList(layers), TimeSpan.FromSeconds(5));

        // Act
        var result = await _decorator.ListLayersAsync();

        // Assert
        result.Should().HaveCount(2);
        _refreshCoordinator.EnqueuedKeys.Should().Contain(ScopedKey("layers:all"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ListServicesAsync_NearExpiry_EnqueuesRefresh()
    {
        // Arrange: service list cached near expiry
        var services = new[] { CreateTestService("S1"), CreateTestService("S2") };
        _cacheService.SetEntry("services:all", new CachedServiceList(services), TimeSpan.FromSeconds(5));

        // Act
        var result = await _decorator.ListServicesAsync();

        // Assert
        result.Should().HaveCount(2);
        _refreshCoordinator.EnqueuedKeys.Should().Contain(ScopedKey("services:all"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_MultipleConcurrentNearExpiry_OnlyOneRefreshEnqueued()
    {
        // Arrange: cached entry near expiry
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(5));

        // Act: multiple concurrent calls
        var tasks = Enumerable.Range(0, 5).Select(_ => _decorator.GetLayerAsync(1));
        var results = await Task.WhenAll(tasks);

        // Assert: all return stale value, but only one refresh enqueued
        results.Should().AllSatisfy(r => r.Should().NotBeNull());
        _refreshCoordinator.EnqueuedKeys.Count(k => k == ScopedKey("layer:1")).Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task LayerExistsAsync_DelegatesToInnerCatalog()
    {
        // Existence checks bypass background refresh
        var result = await _decorator.LayerExistsAsync(1);

        result.Should().BeTrue();
        _innerCatalog.LayerExistsCallCount.Should().Be(1);
        _refreshCoordinator.EnqueuedKeys.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetRelationshipAsync_DelegatesToInnerCatalog()
    {
        // Relationships bypass background refresh
        var result = await _decorator.GetRelationshipAsync(1, 1);

        result.Should().NotBeNull();
        _refreshCoordinator.EnqueuedKeys.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshCallback_WriteThroughWithoutEviction()
    {
        // Arrange: cached entry near expiry
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: trigger near-expiry path
        await _decorator.GetLayerAsync(1);

        // Execute the captured refresh callback (resolves keyed "uncached" catalog from scope)
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: stale entry was NOT evicted; fresh value was written through
        _cacheService.RemovedKeys.Should().NotContain(ScopedKey("layer:1"));
        _cacheService.SetKeys.Should().Contain(ScopedKey("layer:1"));
        _innerCatalog.GetLayerCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task ListLayersAsync_RefreshCallback_WriteThroughWithoutEviction()
    {
        // Arrange: layer list cached near expiry
        var layers = new[] { CreateTestLayer(1), CreateTestLayer(2) };
        _cacheService.SetEntry("layers:all", new CachedLayerList(layers), TimeSpan.FromSeconds(5));

        // Act
        await _decorator.ListLayersAsync();

        // Execute the captured refresh callback
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: write-through — no eviction, fresh value written
        _cacheService.RemovedKeys.Should().NotContain(ScopedKey("layers:all"));
        _cacheService.SetKeys.Should().Contain(ScopedKey("layers:all"));
        _innerCatalog.ListLayersCallCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshCallbackFails_StaleEntryStaysInCache()
    {
        // Arrange: cached entry near expiry, inner catalog will throw on refresh
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: trigger near-expiry path
        await _decorator.GetLayerAsync(1);

        // Make inner catalog fail for the refresh callback
        _innerCatalog.ThrowOnGetLayer = true;

        // Execute the captured refresh callback — it should fail
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None));

        // Assert: stale entry was never evicted or overwritten — it stays in cache naturally
        _cacheService.RemovedKeys.Should().NotContain(ScopedKey("layer:1"));
        _cacheService.SetKeys.Should().NotContain(ScopedKey("layer:1"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_NotNearExpiry_NoRefreshEnqueued()
    {
        // Arrange: cached entry with 50s remaining out of 60s (83% > 25% threshold)
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(50));

        // Act
        await _decorator.GetLayerAsync(1);

        // Assert
        _refreshCoordinator.EnqueuedKeys.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_ConcurrentRequestWhileRefreshPending_StillServedStale()
    {
        // Arrange: cached entry near expiry
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: first request triggers refresh enqueue
        var first = await _decorator.GetLayerAsync(1);
        _refreshCoordinator.EnqueuedKeys.Should().Contain(ScopedKey("layer:1"));

        // Callback is captured but NOT yet executed by background worker.
        // Concurrent requests before the worker runs should still get the stale value
        // from cache without hitting the inner catalog.
        var second = await _decorator.GetLayerAsync(1);
        var third = await _decorator.GetLayerAsync(1);

        // Assert: all three requests returned the stale value
        first.Should().NotBeNull();
        first!.Id.Should().Be(1);
        second.Should().NotBeNull();
        second!.Id.Should().Be(1);
        third.Should().NotBeNull();
        third!.Id.Should().Be(1);

        // Inner catalog was never called — stale served from cache throughout
        _innerCatalog.GetLayerCallCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshSucceedsAfterInvalidation_SkipsWriteBack()
    {
        // Arrange: cached entry near expiry
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: trigger near-expiry path to enqueue a refresh
        await _decorator.GetLayerAsync(1);

        // Simulate explicit invalidation while refresh is pending
        _refreshCoordinator.NotifyInvalidation(ScopedKey("layer:1"));

        // Execute the captured refresh callback — it succeeds but should NOT write back
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: inner catalog was called but write-back was skipped due to invalidation
        _innerCatalog.GetLayerCallCount.Should().Be(1);
        _cacheService.SetKeys.Should().NotContain(ScopedKey("layer:1"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_InvalidationAfterClaimBeforeWrite_UndoesWriteBack()
    {
        // Regression test for the TOCTOU race: invalidation arrives after
        // TryClaimWriteBack succeeds but before the cache write completes.
        // The post-write WasInvalidated check must detect this and remove the entry.
        var layer = CreateTestLayer(1);

        // Use the race-aware mock that invalidates the key during SetAsync
        var raceCacheService = new RaceAwareMockCacheService(
            invalidateKeyDuringSet: ScopedKey("layer:1"),
            coordinator: _refreshCoordinator);
        raceCacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        var scopeFactory = new TestServiceScopeFactory(_innerCatalog);
        var decorator = new BackgroundRefreshCacheDecorator(
            _innerCatalog,
            raceCacheService,
            _refreshCoordinator,
            scopeFactory,
            Options.Create(_options),
            NullLogger<BackgroundRefreshCacheDecorator>.Instance);

        // Trigger near-expiry path
        await decorator.GetLayerAsync(1);

        // Execute the refresh callback — SetAsync will trigger invalidation mid-write
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: the post-write check detected the invalidation and removed the entry
        raceCacheService.RemovedKeys.Should().Contain(ScopedKey("layer:1"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshReturnsNull_RemovesCacheEntryAndExistenceKey()
    {
        // Arrange: cached entry near expiry
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: trigger near-expiry path
        await _decorator.GetLayerAsync(1);

        // Make inner catalog return null (simulating a deleted resource)
        _innerCatalog.ReturnNullOnGetLayer = true;

        // Execute the captured refresh callback
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: stale entry AND companion existence key both removed
        _cacheService.RemovedKeys.Should().Contain(ScopedKey("layer:1"));
        _cacheService.RemovedKeys.Should().Contain(ScopedKey("layer:exists:1"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetServiceAsync_RefreshReturnsNull_RemovesCacheEntryAndExistenceKey()
    {
        // Arrange: service cached near expiry
        var service = CreateTestService("TestService");
        _cacheService.SetEntry("service:testservice", service, TimeSpan.FromSeconds(20));

        // Act: trigger near-expiry path
        await _decorator.GetServiceAsync("TestService");

        // Make inner catalog return null (simulating a deleted service)
        _innerCatalog.ReturnNullOnGetService = true;

        // Execute the captured refresh callback
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: stale entry AND companion existence key both removed
        _cacheService.RemovedKeys.Should().Contain(ScopedKey("service:testservice"));
        _cacheService.RemovedKeys.Should().Contain(ScopedKey("service:exists:testservice"));
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshCallback_PropagatesSchemaContextToChildScope()
    {
        // Regression test: background refresh must query the same database schema
        // as the triggering request (X-Honua-Test-Schema header propagation).
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry(ScopedKey("layer:1", "test_schema"), layer, TimeSpan.FromSeconds(10));

        // Create a schema-tracking scope factory so we can inspect the child scope's schema
        var childSchemaContext = new SchemaContext();
        var scopeFactory = new TestServiceScopeFactory(_innerCatalog, childSchemaContext);

        // Create decorator with a request schema context set to "test_schema"
        var requestSchemaContext = Substitute.For<ISchemaContext>();
        requestSchemaContext.CurrentSchema.Returns("test_schema");

        var decorator = new BackgroundRefreshCacheDecorator(
            _innerCatalog,
            _cacheService,
            _refreshCoordinator,
            scopeFactory,
            Options.Create(_options),
            NullLogger<BackgroundRefreshCacheDecorator>.Instance,
            requestSchemaContext);

        // Act: trigger near-expiry refresh
        await decorator.GetLayerAsync(1);

        // Execute the captured refresh callback
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: child scope's SchemaContext was set to the request schema
        childSchemaContext.CurrentSchema.Should().Be("test_schema");
    }

    [UnitTest]
    [Operation(Operations.Cache)]
    public async Task GetLayerAsync_RefreshCallback_NoSchemaContext_DoesNotFail()
    {
        // Verify that background refresh works when no schema context is present
        // (production path without TestSchemaMiddleware)
        var layer = CreateTestLayer(1);
        _cacheService.SetEntry("layer:1", layer, TimeSpan.FromSeconds(10));

        // Act: trigger near-expiry path (default decorator has no schema context)
        await _decorator.GetLayerAsync(1);

        // Execute the captured refresh callback — should succeed without schema propagation
        _refreshCoordinator.CapturedCallbacks.Should().HaveCount(1);
        await _refreshCoordinator.CapturedCallbacks[0](CancellationToken.None);

        // Assert: refresh completed normally
        _cacheService.SetKeys.Should().Contain(ScopedKey("layer:1"));
    }

    #region Test Helpers

    private static readonly string[] DefaultFormats = ["json", "geojson"];
    private static readonly string[] DefaultCapabilities = ["Query"];

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
            DefaultFormats,
            DefaultCapabilities);
    }

    #endregion

    #region Mock Implementations

    /// <summary>
    /// Mock cache service that supports GetWithMetadataAsync for testing near-expiry behavior.
    /// Tracks SetAsync and RemoveAsync calls to verify write-through refresh.
    /// </summary>
    private class MockCacheService : ICacheService
    {
        private readonly Dictionary<string, (object Value, TimeSpan RemainingTtl)> _entries = new();

        public List<string> RemovedKeys { get; } = new();

        public void SetEntry<T>(string key, T value, TimeSpan remainingTtl) where T : class
        {
            _entries[ScopedKey(key)] = (value, remainingTtl);
        }

        public Task<CacheEntryMetadata<T>> GetWithMetadataAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (_entries.TryGetValue(ScopedKey(key), out var entry) && entry.Value is T typedValue)
            {
                return Task.FromResult(new CacheEntryMetadata<T>(typedValue, entry.RemainingTtl));
            }

            return Task.FromResult(CacheEntryMetadata<T>.Miss());
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
            => Task.FromResult<T?>(null);

        public List<string> SetKeys { get; } = new();

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
        {
            var scopedKey = ScopedKey(key);
            SetKeys.Add(scopedKey);
            _entries[scopedKey] = (value, TimeSpan.Zero);
            return Task.CompletedTask;
        }

        public virtual Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
        {
            var scopedKey = ScopedKey(key);
            SetKeys.Add(scopedKey);
            _entries[scopedKey] = (value, ttl);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            var scopedKey = ScopedKey(key);
            RemovedKeys.Add(scopedKey);
            _entries.Remove(scopedKey);
            return Task.CompletedTask;
        }

        public Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, CancellationToken cancellationToken = default) where T : class
            => factory(cancellationToken);

        public Task<T?> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
            => factory(cancellationToken);
    }

    /// <summary>
    /// Mock cache service that triggers invalidation during SetAsync to simulate the
    /// TOCTOU race: invalidation arrives after TryClaimWriteBack but before write completes.
    /// </summary>
    private sealed class RaceAwareMockCacheService : MockCacheService
    {
        private readonly string _invalidateKeyDuringSet;
        private readonly ICacheRefreshCoordinator _coordinator;

        public RaceAwareMockCacheService(string invalidateKeyDuringSet, ICacheRefreshCoordinator coordinator)
        {
            _invalidateKeyDuringSet = invalidateKeyDuringSet;
            _coordinator = coordinator;
        }

        public override Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            // Simulate invalidation arriving during the cache write
            if (key == _invalidateKeyDuringSet)
            {
                _coordinator.NotifyInvalidation(key);
            }
            return base.SetAsync(key, value, ttl, cancellationToken);
        }
    }

    /// <summary>
    /// Mock refresh coordinator that records enqueued keys and captures callbacks for verification.
    /// </summary>
    private sealed class MockRefreshCoordinator : ICacheRefreshCoordinator
    {
        private readonly HashSet<string> _pending = new();
        private readonly HashSet<string> _invalidated = new();
        private readonly HashSet<string> _claimed = new();

        public List<string> EnqueuedKeys { get; } = new();

        public List<Func<CancellationToken, Task>> CapturedCallbacks { get; } = new();

        public int QueueDepth => _pending.Count;

        public long SuccessCount => 0;

        public long FailureCount => 0;

        public long SkippedCount => 0;

        public bool TryEnqueueRefresh(string key, Func<CancellationToken, Task> refreshCallback)
        {
            if (!_pending.Add(key))
                return false;

            EnqueuedKeys.Add(key);
            CapturedCallbacks.Add(refreshCallback);
            return true;
        }

        public void NotifyInvalidation(string key)
        {
            _invalidated.Add(key);
        }

        public bool WasInvalidated(string key)
        {
            return _invalidated.Contains(key);
        }

        public bool TryClaimWriteBack(string key)
        {
            // Mirrors production semantics: claim fails if already invalidated
            if (_invalidated.Contains(key))
                return false;
            return _claimed.Add(key);
        }
    }

    /// <summary>
    /// Mock layer catalog with call counters.
    /// </summary>
    private sealed class MockLayerCatalog : ILayerCatalog
    {
        public int GetLayerCallCount { get; set; }
        public int ListLayersCallCount { get; set; }
        public int GetServiceCallCount { get; set; }
        public int ListServicesCallCount { get; set; }
        public int LayerExistsCallCount { get; set; }
        public int ServiceExistsCallCount { get; set; }
        public bool ThrowOnGetLayer { get; set; }
        public bool ReturnNullOnGetLayer { get; set; }
        public bool ReturnNullOnGetService { get; set; }

        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        {
            GetLayerCallCount++;
            if (ThrowOnGetLayer)
                throw new InvalidOperationException("simulated failure");
            if (ReturnNullOnGetLayer)
                return Task.FromResult<LayerDefinition?>(null);
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
            if (ReturnNullOnGetService)
                return Task.FromResult<ServiceDefinition?>(null);
            return Task.FromResult<ServiceDefinition?>(CreateTestService(serviceName));
        }

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        {
            ListServicesCallCount++;
            return Task.FromResult(new[] { CreateTestService("S1"), CreateTestService("S2") });
        }

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        {
            LayerExistsCallCount++;
            return Task.FromResult(true);
        }

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            ServiceExistsCallCount++;
            return Task.FromResult(true);
        }

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(Relationship.Create(relationshipId, "Test", 2, "esriRelCardinalityOneToMany", "id", "layer_id"));

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    /// <summary>
    /// Test service scope factory that returns a scope resolving the provided layer catalog
    /// as the keyed "uncached" service, matching the production DI registration.
    /// Optionally provides a SchemaContext to verify schema propagation in background refresh.
    /// </summary>
    private sealed class TestServiceScopeFactory : IServiceScopeFactory
    {
        private readonly ILayerCatalog _catalog;
        private readonly SchemaContext? _schemaContext;

        public TestServiceScopeFactory(ILayerCatalog catalog, SchemaContext? schemaContext = null)
        {
            _catalog = catalog;
            _schemaContext = schemaContext;
        }

        public IServiceScope CreateScope() => new TestScope(_catalog, _schemaContext);

        private sealed class TestScope : IServiceScope
        {
            private readonly TestServiceProvider _provider;

            public TestScope(ILayerCatalog catalog, SchemaContext? schemaContext)
                => _provider = new TestServiceProvider(catalog, schemaContext);

            public IServiceProvider ServiceProvider => _provider;

            public void Dispose() { }
        }

        private sealed class TestServiceProvider : IServiceProvider, IKeyedServiceProvider
        {
            private readonly ILayerCatalog _catalog;
            private readonly SchemaContext? _schemaContext;

            public TestServiceProvider(ILayerCatalog catalog, SchemaContext? schemaContext)
            {
                _catalog = catalog;
                _schemaContext = schemaContext;
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ILayerCatalog))
                    return _catalog;
                if (serviceType == typeof(SchemaContext))
                    return _schemaContext;
                return null;
            }

            public object? GetKeyedService(Type serviceType, object? serviceKey)
            {
                if (serviceType == typeof(ILayerCatalog)
                    && serviceKey is string key
                    && key == BackgroundRefreshCacheDecorator.UncachedCatalogServiceKey)
                    return _catalog;
                return null;
            }

            public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
            {
                return GetKeyedService(serviceType, serviceKey)
                    ?? throw new InvalidOperationException($"Keyed service not found: {serviceType.Name} [{serviceKey}]");
            }
        }
    }

    #endregion
}
