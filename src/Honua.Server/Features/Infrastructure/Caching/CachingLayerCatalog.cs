// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
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
    internal const string MetadataGenerationKey = "catalog:generation";
    internal const string MetadataGenerationPrefix = "catalog-gen:";
    internal static readonly TimeSpan MetadataGenerationTtl = TimeSpan.FromDays(365);

    private string? _resolvedScopePrefix;

    public CachingLayerCatalog(
        ILayerCatalog innerCatalog,
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ISchemaContext? schemaContext = null)
    {
        // Validation framework eliminates 3 lines of duplicate null checks
        _innerCatalog = innerCatalog.ThrowIfNull();
        _cacheService = cacheService.ThrowIfNull();
        _options = options.ValidateAndGetValue();
        _schemaContext = schemaContext;
    }

    /// <inheritdoc />
    public async Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string cacheKey = await ScopeKeyAsync($"{LayerKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false);
        string existsKey = await ScopeKeyAsync($"{LayerExistsKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false);

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
        var listKey = await ScopeKeyAsync(LayerListKey, cancellationToken).ConfigureAwait(false);
        var ttl = _options.GetLayerTtlWithJitter();
        CachedLayerList? cached = await _cacheService.GetOrSetAsync(
            listKey,
            async ct => new CachedLayerList(await _innerCatalog.ListLayersAsync(ct).ConfigureAwait(false)),
            ttl,
            cancellationToken).ConfigureAwait(false);

        return cached?.Layers ?? [];
    }

    /// <inheritdoc />
    public async Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string normalizedName = serviceName.ToLowerInvariant();
        string cacheKey = await ScopeKeyAsync($"{ServiceKeyPrefix}{normalizedName}", cancellationToken).ConfigureAwait(false);
        string existsKey = await ScopeKeyAsync($"{ServiceExistsKeyPrefix}{normalizedName}", cancellationToken).ConfigureAwait(false);

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
        var listKey = await ScopeKeyAsync(ServiceListKey, cancellationToken).ConfigureAwait(false);
        var ttl = _options.GetServiceTtlWithJitter();
        CachedServiceList? cached = await _cacheService.GetOrSetAsync(
            listKey,
            async ct => new CachedServiceList(await _innerCatalog.ListServicesAsync(ct).ConfigureAwait(false)),
            ttl,
            cancellationToken).ConfigureAwait(false);

        return cached?.Services ?? [];
    }

    /// <inheritdoc />
    public async Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
    {
        string existsKey = await ScopeKeyAsync($"{LayerExistsKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false);
        string cacheKey = await ScopeKeyAsync($"{LayerKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false);
        var positiveTtl = _options.GetLayerTtlWithJitter();
        var negativeTtl = _options.GetNegativeTtlWithJitter();

        CachedExistenceResult? cachedExists = await _cacheService
            .GetAsync<CachedExistenceResult>(existsKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedExists != null)
        {
            return cachedExists.Exists;
        }

        CachedExistenceResult? exists = await _cacheService.GetOrSetAsync(
            existsKey,
            async ct =>
            {
                LayerDefinition? cachedLayer = await _cacheService
                    .GetAsync<LayerDefinition>(cacheKey, ct)
                    .ConfigureAwait(false);
                if (cachedLayer != null)
                {
                    return new CachedExistenceResult(true);
                }

                bool found = await _innerCatalog.LayerExistsAsync(layerId, ct).ConfigureAwait(false);
                return new CachedExistenceResult(found);
            },
            positiveTtl,
            cancellationToken).ConfigureAwait(false);

        if (exists is { Exists: false })
        {
            await _cacheService
                .SetAsync(existsKey, exists, negativeTtl, cancellationToken)
                .ConfigureAwait(false);
        }

        return exists?.Exists ?? false;
    }

    /// <inheritdoc />
    public async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        string normalizedName = serviceName.ToLowerInvariant();
        string existsKey = await ScopeKeyAsync($"{ServiceExistsKeyPrefix}{normalizedName}", cancellationToken).ConfigureAwait(false);
        string cacheKey = await ScopeKeyAsync($"{ServiceKeyPrefix}{normalizedName}", cancellationToken).ConfigureAwait(false);
        var positiveTtl = _options.GetServiceTtlWithJitter();
        var negativeTtl = _options.GetNegativeTtlWithJitter();

        CachedExistenceResult? cachedExists = await _cacheService
            .GetAsync<CachedExistenceResult>(existsKey, cancellationToken)
            .ConfigureAwait(false);
        if (cachedExists != null)
        {
            return cachedExists.Exists;
        }

        CachedExistenceResult? exists = await _cacheService.GetOrSetAsync(
            existsKey,
            async ct =>
            {
                ServiceDefinition? cachedService = await _cacheService
                    .GetAsync<ServiceDefinition>(cacheKey, ct)
                    .ConfigureAwait(false);
                if (cachedService != null)
                {
                    return new CachedExistenceResult(true);
                }

                bool found = await _innerCatalog.ServiceExistsAsync(serviceName, ct).ConfigureAwait(false);
                return new CachedExistenceResult(found);
            },
            positiveTtl,
            cancellationToken).ConfigureAwait(false);

        if (exists is { Exists: false })
        {
            await _cacheService
                .SetAsync(existsKey, exists, negativeTtl, cancellationToken)
                .ConfigureAwait(false);
        }

        return exists?.Exists ?? false;
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
        await _cacheService.RemoveAsync(await ScopeKeyAsync($"{LayerKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(await ScopeKeyAsync($"{LayerExistsKeyPrefix}{layerId}", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        // Remove layer list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(await ScopeKeyAsync(LayerListKey, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates cache entries for a specific service.
    /// </summary>
    /// <param name="serviceName">Service name to invalidate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Remove specific service cache
        await _cacheService.RemoveAsync(await ScopeKeyAsync($"{ServiceKeyPrefix}{serviceName.ToLowerInvariant()}", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _cacheService.RemoveAsync(await ScopeKeyAsync($"{ServiceExistsKeyPrefix}{serviceName.ToLowerInvariant()}", cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

        // Remove service list cache (will be rebuilt on next request)
        await _cacheService.RemoveAsync(await ScopeKeyAsync(ServiceListKey, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates all cached metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        _resolvedScopePrefix = null;
        await _cacheService.SetAsync(
            GetGenerationStateKey(_schemaContext?.CurrentSchema),
            Guid.NewGuid().ToString("N"),
            MetadataGenerationTtl,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string GetGenerationStateKey(string? schema) =>
        $"{CacheScopeKeys.GetScopePrefix(schema)}{MetadataGenerationKey}";

    internal static async Task<string> ScopeKeyAsync(
        ICacheService cacheService,
        string key,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        string scopePrefix = await GetCurrentScopePrefixAsync(cacheService, schema, cancellationToken).ConfigureAwait(false);
        return $"{scopePrefix}{key}";
    }

    internal static async Task<string> GetCurrentScopePrefixAsync(
        ICacheService cacheService,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        var baseScopePrefix = CacheScopeKeys.GetScopePrefix(schema);
        var generationStateKey = GetGenerationStateKey(schema);
        var generation = await cacheService.GetAsync<string>(generationStateKey, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(generation)
            ? baseScopePrefix
            : $"{baseScopePrefix}{MetadataGenerationPrefix}{generation}:";
    }

    private async Task<string> ScopeKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        string scopePrefix = await GetCurrentScopePrefixAsync(cancellationToken).ConfigureAwait(false);
        return $"{scopePrefix}{key}";
    }

    private async Task<string> GetCurrentScopePrefixAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedScopePrefix))
        {
            return _resolvedScopePrefix;
        }

        _resolvedScopePrefix = await GetCurrentScopePrefixAsync(_cacheService, _schemaContext?.CurrentSchema, cancellationToken).ConfigureAwait(false);
        return _resolvedScopePrefix;
    }
}
