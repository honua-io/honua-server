// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Decorator that adds caching to ILayerCatalog operations.
/// </summary>
/// <remarks>
/// Wraps the underlying catalog implementation and caches results for improved performance.
/// Uses Redis as primary cache with in-memory fallback.
/// Invalidates cache entries when layer data is updated.
/// </remarks>
internal sealed class CachingLayerCatalog : ILayerCatalog
{
    private readonly ILayerCatalog _innerCatalog;
    private readonly ICacheService _cacheService;
    private readonly CacheOptions _options;
    private readonly ISchemaContext? _schemaContext;

    // Cache key constants — shared keys are internal so BackgroundRefreshCacheDecorator uses the same values
    internal const string LayerKeyPrefix = "layer:";
    internal const string LayerListKey = "layers:all";
    internal const string ServiceKeyPrefix = "service:";
    internal const string ServiceListKey = "services:all";
    internal const string LayerExistsKeyPrefix = "layer:exists:";
    internal const string ServiceExistsKeyPrefix = "service:exists:";
    internal const string RelationshipKeyPrefix = "relationship:";

    public CachingLayerCatalog(
        ILayerCatalog innerCatalog,
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ISchemaContext? schemaContext = null)
    {
        _innerCatalog = innerCatalog ?? throw new ArgumentNullException(nameof(innerCatalog));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _schemaContext = schemaContext;
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string cacheKey = ScopeKey($"{LayerKeyPrefix}{layerId}");
        string existsKey = ScopeKey($"{LayerExistsKeyPrefix}{layerId}");

        // Single Redis call optimized: Try to get both layer and existence data
        // Use GetOrSetAsync with a factory that checks for cached existence data first
        var layerTtl = _options.GetLayerTtlWithJitter();
        LayerDefinition? layer = await _cacheService.GetOrSetAsync(
            cacheKey,
            async ct =>
            {
                // Check cached existence result to avoid unnecessary database queries
                CachedExistenceResult? cachedExists = await _cacheService.GetAsync<CachedExistenceResult>(existsKey, ct).ConfigureAwait(false);
                if (cachedExists != null && !cachedExists.Exists)
                {
                    return null; // Cached negative result, don't query database
                }

                // Query database since no cached negative result exists
                return await _innerCatalog.GetLayerAsync(layerId, ct).ConfigureAwait(false);
            },
            layerTtl,
            cancellationToken).ConfigureAwait(false);

        // Optimized: Only update existence cache if we don't have cached existence data already
        CachedExistenceResult? existsCached = await _cacheService.GetAsync<CachedExistenceResult>(existsKey, cancellationToken).ConfigureAwait(false);
        if (existsCached == null)
        {
            if (layer != null)
            {
                await _cacheService.SetAsync(
                    existsKey,
                    new CachedExistenceResult(true),
                    layerTtl,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var negativeTtl = _options.GetNegativeTtlWithJitter();
                await _cacheService.SetAsync(
                    existsKey,
                    new CachedExistenceResult(false),
                    negativeTtl,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return layer;
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        var listKey = ScopeKey(LayerListKey);

        // Use wrapper type for proper serialization
        CachedLayerList? cached = await _cacheService.GetAsync<CachedLayerList>(listKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached.Layers;

        // Fetch from underlying catalog
        LayerDefinition[] layers = await _innerCatalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);

        // Cache the result
        var ttl = _options.GetLayerTtlWithJitter();
        await _cacheService.SetAsync(listKey, new CachedLayerList(layers), ttl, cancellationToken).ConfigureAwait(false);

        return layers;
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string normalizedName = serviceName.ToLowerInvariant();
        string cacheKey = ScopeKey($"{ServiceKeyPrefix}{normalizedName}");
        string existsKey = ScopeKey($"{ServiceExistsKeyPrefix}{normalizedName}");

        // Single Redis call optimized: Try to get both service and existence data
        // Use GetOrSetAsync with a factory that checks for cached existence data first
        var serviceTtl = _options.GetServiceTtlWithJitter();
        ServiceDefinition? service = await _cacheService.GetOrSetAsync(
            cacheKey,
            async ct =>
            {
                // Check cached existence result to avoid unnecessary database queries
                CachedExistenceResult? cachedExists = await _cacheService.GetAsync<CachedExistenceResult>(existsKey, ct).ConfigureAwait(false);
                if (cachedExists != null && !cachedExists.Exists)
                {
                    return null; // Cached negative result, don't query database
                }

                // Query database since no cached negative result exists
                return await _innerCatalog.GetServiceAsync(serviceName, ct).ConfigureAwait(false);
            },
            serviceTtl,
            cancellationToken).ConfigureAwait(false);

        // Optimized: Only update existence cache if we don't have cached existence data already
        CachedExistenceResult? existsCached = await _cacheService.GetAsync<CachedExistenceResult>(existsKey, cancellationToken).ConfigureAwait(false);
        if (existsCached == null)
        {
            if (service != null)
            {
                await _cacheService.SetAsync(
                    existsKey,
                    new CachedExistenceResult(true),
                    serviceTtl,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var negativeTtl = _options.GetNegativeTtlWithJitter();
                await _cacheService.SetAsync(
                    existsKey,
                    new CachedExistenceResult(false),
                    negativeTtl,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return service;
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var listKey = ScopeKey(ServiceListKey);

        // Use wrapper type for proper serialization
        CachedServiceList? cached = await _cacheService.GetAsync<CachedServiceList>(listKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached.Services;

        // Fetch from underlying catalog
        ServiceDefinition[] services = await _innerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);

        // Cache the result
        var ttl = _options.GetServiceTtlWithJitter();
        await _cacheService.SetAsync(listKey, new CachedServiceList(services), ttl, cancellationToken).ConfigureAwait(false);

        return services;
    }

    /// <inheritdoc />
    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string existsKey = ScopeKey($"{LayerExistsKeyPrefix}{layerId}");
        string cacheKey = ScopeKey($"{LayerKeyPrefix}{layerId}");
        var positiveTtl = _options.GetLayerTtlWithJitter();
        var negativeTtl = _options.GetNegativeTtlWithJitter();

        CachedExistenceResult? cachedExists = await _cacheService
            .GetAsync<CachedExistenceResult>(existsKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedExists != null)
        {
            return cachedExists.Exists;
        }

        LayerDefinition? cachedLayer = await _cacheService
            .GetAsync<LayerDefinition>(cacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedLayer != null)
        {
            await _cacheService
                .SetAsync(existsKey, new CachedExistenceResult(true), positiveTtl, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        bool exists = await _innerCatalog.LayerExistsAsync(layerId, cancellationToken).ConfigureAwait(false);
        await _cacheService
            .SetAsync(
                existsKey,
                new CachedExistenceResult(exists),
                exists ? positiveTtl : negativeTtl,
                cancellationToken)
            .ConfigureAwait(false);

        return exists;
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string normalizedName = serviceName.ToLowerInvariant();
        string existsKey = ScopeKey($"{ServiceExistsKeyPrefix}{normalizedName}");
        string cacheKey = ScopeKey($"{ServiceKeyPrefix}{normalizedName}");
        var positiveTtl = _options.GetServiceTtlWithJitter();
        var negativeTtl = _options.GetNegativeTtlWithJitter();

        CachedExistenceResult? cachedExists = await _cacheService
            .GetAsync<CachedExistenceResult>(existsKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedExists != null)
        {
            return cachedExists.Exists;
        }

        ServiceDefinition? cachedService = await _cacheService
            .GetAsync<ServiceDefinition>(cacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedService != null)
        {
            await _cacheService
                .SetAsync(existsKey, new CachedExistenceResult(true), positiveTtl, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        bool exists = await _innerCatalog.ServiceExistsAsync(serviceName, cancellationToken).ConfigureAwait(false);
        await _cacheService
            .SetAsync(
                existsKey,
                new CachedExistenceResult(exists),
                exists ? positiveTtl : negativeTtl,
                cancellationToken)
            .ConfigureAwait(false);

        return exists;
    }

    /// <inheritdoc />
    public async Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
    {
        LayerDefinition? cachedLayer = await GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (cachedLayer != null)
        {
            var relationship = cachedLayer.LayerRelationships
                .FirstOrDefault(r => r.RelationshipId == relationshipId);

            return relationship.RelationshipId == 0 ? null : relationship;
        }

        return await _innerCatalog.GetRelationshipAsync(layerId, relationshipId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Relationships are included in layer definitions, so try to get from layer cache
        LayerDefinition? cachedLayer = await GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (cachedLayer != null)
            return cachedLayer.LayerRelationships;

        // Fall back to direct query
        return await _innerCatalog.ListRelationshipsAsync(layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates cache entries for a specific layer.
    /// </summary>
    /// <param name="layerId">Layer ID to invalidate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Remove specific layer cache
        await _cacheService.RemoveAsync(ScopeKey($"{LayerKeyPrefix}{layerId}"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(ScopeKey($"{LayerExistsKeyPrefix}{layerId}"), cancellationToken).ConfigureAwait(false);

        // Remove layer list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(ScopeKey(LayerListKey), cancellationToken).ConfigureAwait(false);

        // Remove relationship caches for this layer
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{RelationshipKeyPrefix}{layerId}:*"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates cache entries for a specific service.
    /// </summary>
    /// <param name="serviceName">Service name to invalidate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Remove specific service cache
        await _cacheService.RemoveAsync(ScopeKey($"{ServiceKeyPrefix}{serviceName.ToLowerInvariant()}"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(ScopeKey($"{ServiceExistsKeyPrefix}{serviceName.ToLowerInvariant()}"), cancellationToken).ConfigureAwait(false);

        // Remove service list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(ScopeKey(ServiceListKey), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates all cached metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{LayerKeyPrefix}*"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{LayerExistsKeyPrefix}*"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{ServiceKeyPrefix}*"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{ServiceExistsKeyPrefix}*"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync(ScopePattern($"{RelationshipKeyPrefix}*"), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(ScopeKey(LayerListKey), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(ScopeKey(ServiceListKey), cancellationToken).ConfigureAwait(false);
    }

    private string ScopeKey(string key) => $"{GetScopePrefix()}{key}";

    private string ScopePattern(string pattern) => $"{GetScopePrefix()}{pattern}";

    private string GetScopePrefix()
    {
        var schema = _schemaContext?.CurrentSchema;
        return string.IsNullOrWhiteSpace(schema)
            ? "scope:default:"
            : $"scope:schema:{schema.Trim().ToLowerInvariant()}:";
    }
}
