// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Decorator that wraps <see cref="CachingLayerCatalog"/> to serve stale cache entries
/// while triggering background refreshes for entries approaching expiration.
/// </summary>
/// <remarks>
/// Cold cache misses are handled synchronously by the inner catalog (unavoidable — no stale value to serve).
/// Near-expiry entries are returned immediately while a background refresh is enqueued.
/// Refresh callbacks resolve a fresh scoped data-source catalog (keyed as "uncached") via
/// <see cref="IServiceScopeFactory"/> to bypass the caching layer and fetch directly from the database.
/// The fresh value is written through to the cache, atomically replacing the stale entry — concurrent
/// requests continue to see the stale value until the overwrite completes (no cold-miss window).
/// Negative cache entries (missing layers/services) are not background-refreshed to avoid churn.
/// Explicit invalidation bypasses background refresh and evicts keys immediately.
/// If a key is invalidated while a refresh is in-flight, the write-back is skipped to avoid
/// restoring data that was intentionally evicted.
/// </remarks>
internal sealed partial class BackgroundRefreshCacheDecorator : ILayerCatalog
{
    /// <summary>
    /// Keyed service name for the data-source catalog that bypasses the caching layer.
    /// </summary>
    internal const string UncachedCatalogServiceKey = "uncached";

    private readonly ILayerCatalog _innerCatalog;
    private readonly ICacheService _cacheService;
    private readonly ICacheRefreshCoordinator _refreshCoordinator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISchemaContext? _schemaContext;
    private readonly CacheOptions _options;
    private readonly ILogger<BackgroundRefreshCacheDecorator> _logger;

    public BackgroundRefreshCacheDecorator(
        ILayerCatalog innerCatalog,
        ICacheService cacheService,
        ICacheRefreshCoordinator refreshCoordinator,
        IServiceScopeFactory scopeFactory,
        IOptions<CacheOptions> options,
        ILogger<BackgroundRefreshCacheDecorator> logger,
        ISchemaContext? schemaContext = null)
    {
        _innerCatalog = innerCatalog ?? throw new ArgumentNullException(nameof(innerCatalog));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _schemaContext = schemaContext;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string cacheKey = await CachingLayerCatalog.ScopeKeyAsync(
            _cacheService,
            $"{CachingLayerCatalog.LayerKeyPrefix}{layerId}",
            _schemaContext?.CurrentSchema,
            cancellationToken).ConfigureAwait(false);

        var entry = await _cacheService.GetWithMetadataAsync<LayerDefinition>(cacheKey, cancellationToken).ConfigureAwait(false);

        if (entry.HasValue)
        {
            if (IsNearExpiry(entry.RemainingTtl, _options.LayerTtl))
            {
                string existenceKey = await CachingLayerCatalog.ScopeKeyAsync(
                    _cacheService,
                    $"{CachingLayerCatalog.LayerExistsKeyPrefix}{layerId}",
                    _schemaContext?.CurrentSchema,
                    cancellationToken).ConfigureAwait(false);
                EnqueueWriteThroughRefresh(cacheKey,
                    async (catalog, ct) => await catalog.GetLayerAsync(layerId, ct).ConfigureAwait(false),
                    () => _options.GetLayerTtlWithJitter(),
                    existenceKey: existenceKey);
            }

            return entry.Value;
        }

        // Cold miss — delegate to inner catalog (which handles caching + negative cache)
        return await _innerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        string listKey = await CachingLayerCatalog.ScopeKeyAsync(
            _cacheService,
            CachingLayerCatalog.LayerListKey,
            _schemaContext?.CurrentSchema,
            cancellationToken).ConfigureAwait(false);
        var entry = await _cacheService.GetWithMetadataAsync<CachedLayerList>(listKey, cancellationToken).ConfigureAwait(false);

        if (entry.HasValue)
        {
            if (IsNearExpiry(entry.RemainingTtl, _options.LayerTtl))
            {
                EnqueueWriteThroughRefresh<CachedLayerList>(listKey,
                    async (catalog, ct) =>
                    {
                        var layers = await catalog.ListLayersAsync(ct).ConfigureAwait(false);
                        return new CachedLayerList(layers);
                    },
                    () => _options.GetLayerTtlWithJitter());
            }

            return entry.Value!.Layers;
        }

        // Cold miss
        return await _innerCatalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string normalizedName = serviceName.ToLowerInvariant();
        string cacheKey = await CachingLayerCatalog.ScopeKeyAsync(
            _cacheService,
            $"{CachingLayerCatalog.ServiceKeyPrefix}{normalizedName}",
            _schemaContext?.CurrentSchema,
            cancellationToken).ConfigureAwait(false);

        var entry = await _cacheService.GetWithMetadataAsync<ServiceDefinition>(cacheKey, cancellationToken).ConfigureAwait(false);

        if (entry.HasValue)
        {
            if (IsNearExpiry(entry.RemainingTtl, _options.ServiceTtl))
            {
                string existenceKey = await CachingLayerCatalog.ScopeKeyAsync(
                    _cacheService,
                    $"{CachingLayerCatalog.ServiceExistsKeyPrefix}{normalizedName}",
                    _schemaContext?.CurrentSchema,
                    cancellationToken).ConfigureAwait(false);
                EnqueueWriteThroughRefresh(cacheKey,
                    async (catalog, ct) => await catalog.GetServiceAsync(serviceName, ct).ConfigureAwait(false),
                    () => _options.GetServiceTtlWithJitter(),
                    existenceKey: existenceKey);
            }

            return entry.Value;
        }

        // Cold miss
        return await _innerCatalog.GetServiceAsync(serviceName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        string listKey = await CachingLayerCatalog.ScopeKeyAsync(
            _cacheService,
            CachingLayerCatalog.ServiceListKey,
            _schemaContext?.CurrentSchema,
            cancellationToken).ConfigureAwait(false);
        var entry = await _cacheService.GetWithMetadataAsync<CachedServiceList>(listKey, cancellationToken).ConfigureAwait(false);

        if (entry.HasValue)
        {
            if (IsNearExpiry(entry.RemainingTtl, _options.ServiceTtl))
            {
                EnqueueWriteThroughRefresh<CachedServiceList>(listKey,
                    async (catalog, ct) =>
                    {
                        var services = await catalog.ListServicesAsync(ct).ConfigureAwait(false);
                        return new CachedServiceList(services);
                    },
                    () => _options.GetServiceTtlWithJitter());
            }

            return entry.Value!.Services;
        }

        // Cold miss
        return await _innerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Existence checks are lightweight; delegate directly without background refresh
        return _innerCatalog.LayerExistsAsync(layerId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Existence checks are lightweight; delegate directly without background refresh
        return _innerCatalog.ServiceExistsAsync(serviceName, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        // Relationships are extracted from layer cache; delegate to inner catalog
        return _innerCatalog.GetRelationshipAsync(layerId, relationshipId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Relationships are extracted from layer cache; delegate to inner catalog
        return _innerCatalog.ListRelationshipsAsync(layerId, cancellationToken);
    }

    /// <summary>
    /// Enqueues a write-through background refresh for a near-expiry cache entry.
    /// Fetches fresh data from the data-source catalog (bypassing cache) and overwrites
    /// the stale entry directly — no eviction, so concurrent readers always see a value.
    /// </summary>
    private void EnqueueWriteThroughRefresh<T>(
        string cacheKey,
        Func<ILayerCatalog, CancellationToken, Task<T?>> fetchAction,
        Func<TimeSpan> ttlFactory,
        string? existenceKey = null) where T : class
    {
        // Capture singleton fields as locals and evaluate ttlFactory eagerly so the
        // closure does not capture 'this' — even transitively through the ttlFactory
        // delegate — which would keep the scoped decorator (and its inner catalog
        // chain) alive past the request scope until the background refresh completes.
        var cacheService = _cacheService;
        var scopeFactory = _scopeFactory;
        var refreshCoordinator = _refreshCoordinator;
        var logger = _logger;
        var ttl = ttlFactory();

        // Capture the current request schema so the background refresh queries
        // the same database schema as the triggering request.  Matches the
        // propagation pattern in TileOperationJobService.
        var currentSchema = _schemaContext?.CurrentSchema;

        _refreshCoordinator.TryEnqueueRefresh(cacheKey, async ct =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();

            // Propagate request schema into the child scope so database providers
            // set the correct search_path (mirrors TileOperationJobService pattern).
            if (!string.IsNullOrWhiteSpace(currentSchema))
            {
                var childSchemaContext = scope.ServiceProvider.GetService<SchemaContext>();
                if (childSchemaContext != null)
                {
                    childSchemaContext.CurrentSchema = currentSchema;
                }
            }

            var catalog = scope.ServiceProvider.GetRequiredKeyedService<ILayerCatalog>(UncachedCatalogServiceKey);
            var fresh = await fetchAction(catalog, ct).ConfigureAwait(false);

            // Atomically claim write-back rights. Fails if the key was invalidated
            // between enqueue and now — closing the TOCTOU race between the old
            // WasInvalidated check and the subsequent cache write.
            if (!refreshCoordinator.TryClaimWriteBack(cacheKey))
            {
                Log.BackgroundRefreshSkippedInvalidated(logger, cacheKey);
                return;
            }

            if (fresh != null)
            {
                // Write-through: overwrite the stale entry with fresh data.
                // Concurrent readers continue to see the stale value until this completes.
                await cacheService.SetAsync(cacheKey, fresh, ttl, ct).ConfigureAwait(false);

                // Post-write check: if invalidation arrived after the claim (state 2 → 1)
                // but before the write completed, undo the write to honor the invalidation.
                if (refreshCoordinator.WasInvalidated(cacheKey))
                {
                    await cacheService.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
                    Log.BackgroundRefreshSkippedInvalidated(logger, cacheKey);
                    return;
                }
            }
            else
            {
                // Resource no longer exists — remove the stale cache entry and its
                // companion existence key so LayerExistsAsync/ServiceExistsAsync
                // don't continue returning a stale positive result.
                await cacheService.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
                if (existenceKey != null)
                {
                    await cacheService.RemoveAsync(existenceKey, ct).ConfigureAwait(false);
                }
            }

            Log.BackgroundRefreshCompleted(logger, cacheKey);
        });
    }

    /// <summary>
    /// Determines whether a cache entry is near expiry based on the configured threshold.
    /// </summary>
    private bool IsNearExpiry(TimeSpan remainingTtl, TimeSpan originalTtl)
    {
        if (originalTtl <= TimeSpan.Zero)
            return false;

        var threshold = originalTtl * _options.BackgroundRefreshThreshold;
        return remainingTtl <= threshold;
    }

    private static partial class Log
    {
        [LoggerMessage(1105, LogLevel.Debug, "Background refresh completed for cache key {CacheKey}")]
        public static partial void BackgroundRefreshCompleted(ILogger logger, string cacheKey);

        [LoggerMessage(1106, LogLevel.Debug, "Background refresh skipped write-back for invalidated key {CacheKey}")]
        public static partial void BackgroundRefreshSkippedInvalidated(ILogger logger, string cacheKey);
    }
}
