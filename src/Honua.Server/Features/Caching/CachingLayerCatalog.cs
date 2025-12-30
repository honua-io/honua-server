// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Caching;

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

    // Cache key constants
    private const string LayerKeyPrefix = "layer:";
    private const string LayerListKey = "layers:all";
    private const string ServiceKeyPrefix = "service:";
    private const string ServiceListKey = "services:all";
    private const string RelationshipKeyPrefix = "relationship:";

    public CachingLayerCatalog(
        ILayerCatalog innerCatalog,
        ICacheService cacheService,
        IOptions<CacheOptions> options)
    {
        _innerCatalog = innerCatalog ?? throw new ArgumentNullException(nameof(innerCatalog));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"{LayerKeyPrefix}{layerId}";

        return await _cacheService.GetOrSetAsync(
            cacheKey,
            ct => _innerCatalog.GetLayerAsync(layerId, ct),
            _options.LayerTtl,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
    {
        // Use wrapper type for proper serialization
        CachedLayerList? cached = await _cacheService.GetAsync<CachedLayerList>(LayerListKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached.Layers;

        // Fetch from underlying catalog
        LayerDefinition[] layers = await _innerCatalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);

        // Cache the result
        await _cacheService.SetAsync(LayerListKey, new CachedLayerList(layers), _options.LayerTtl, cancellationToken).ConfigureAwait(false);

        return layers;
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"{ServiceKeyPrefix}{serviceName.ToLowerInvariant()}";

        return await _cacheService.GetOrSetAsync(
            cacheKey,
            ct => _innerCatalog.GetServiceAsync(serviceName, ct),
            _options.ServiceTtl,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        // Use wrapper type for proper serialization
        CachedServiceList? cached = await _cacheService.GetAsync<CachedServiceList>(ServiceListKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
            return cached.Services;

        // Fetch from underlying catalog
        ServiceDefinition[] services = await _innerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);

        // Cache the result
        await _cacheService.SetAsync(ServiceListKey, new CachedServiceList(services), _options.ServiceTtl, cancellationToken).ConfigureAwait(false);

        return services;
    }

    /// <inheritdoc />
    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        // Try to get from cache first (quick path)
        string cacheKey = $"{LayerKeyPrefix}{layerId}";
        LayerDefinition? cached = await _cacheService.GetAsync<LayerDefinition>(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached != null)
            return true;

        // Fall back to database check
        return await _innerCatalog.LayerExistsAsync(layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Try to get from cache first (quick path)
        string cacheKey = $"{ServiceKeyPrefix}{serviceName.ToLowerInvariant()}";
        ServiceDefinition? cached = await _cacheService.GetAsync<ServiceDefinition>(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached != null)
            return true;

        // Fall back to database check
        return await _innerCatalog.ServiceExistsAsync(serviceName, cancellationToken).ConfigureAwait(false);
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
        await _cacheService.RemoveAsync($"{LayerKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false);

        // Remove layer list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(LayerListKey, cancellationToken).ConfigureAwait(false);

        // Remove relationship caches for this layer
        await _cacheService.RemoveByPatternAsync($"{RelationshipKeyPrefix}{layerId}:*", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates cache entries for a specific service.
    /// </summary>
    /// <param name="serviceName">Service name to invalidate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Remove specific service cache
        await _cacheService.RemoveAsync($"{ServiceKeyPrefix}{serviceName.ToLowerInvariant()}", cancellationToken).ConfigureAwait(false);

        // Remove service list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(ServiceListKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates all cached metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        await _cacheService.RemoveByPatternAsync($"{LayerKeyPrefix}*", cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync($"{ServiceKeyPrefix}*", cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveByPatternAsync($"{RelationshipKeyPrefix}*", cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(LayerListKey, cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(ServiceListKey, cancellationToken).ConfigureAwait(false);
    }
}
