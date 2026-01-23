// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Admin;

internal sealed class MetadataCacheInvalidator
{
    private const string LayerCacheKeyPrefix = "layer:";
    private const string LayerListCacheKey = "layers:all";
    private const string ServiceCacheKeyPrefix = "service:";
    private const string ServiceListCacheKey = "services:all";
    private const string RelationshipCacheKeyPrefix = "relationship:";

    private readonly CachingLayerCatalog? _cachingCatalog;
    private readonly ICacheService? _cacheService;
    private readonly IOutputCacheStore? _outputCache;

    public MetadataCacheInvalidator(
        CachingLayerCatalog? cachingCatalog = null,
        ICacheService? cacheService = null,
        IOutputCacheStore? outputCache = null)
    {
        _cachingCatalog = cachingCatalog;
        _cacheService = cacheService;
        _outputCache = outputCache;
    }

    public async Task InvalidateServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        var normalizedServiceName = serviceName.ToLowerInvariant();

        if (_cachingCatalog != null)
        {
            await _cachingCatalog.InvalidateServiceAsync(serviceName, cancellationToken);
        }

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync($"{ServiceCacheKeyPrefix}{normalizedServiceName}", cancellationToken);
            await _cacheService.RemoveAsync(ServiceListCacheKey, cancellationToken);
        }

        if (_outputCache != null)
        {
            await _outputCache.EvictByTagAsync($"service:{normalizedServiceName}", cancellationToken);
            await _outputCache.EvictByTagAsync("ogc-metadata", cancellationToken);
            await _outputCache.EvictByTagAsync("ogc-tiles", cancellationToken);
        }
    }

    public async Task InvalidateLayerAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_cachingCatalog != null)
        {
            await _cachingCatalog.InvalidateLayerAsync(layerId, cancellationToken);
        }

        if (_cacheService != null)
        {
            await _cacheService.RemoveAsync($"{LayerCacheKeyPrefix}{layerId}", cancellationToken);
            await _cacheService.RemoveAsync(LayerListCacheKey, cancellationToken);
            await _cacheService.RemoveByPatternAsync($"{RelationshipCacheKeyPrefix}{layerId}:*", cancellationToken);
        }

        if (_outputCache != null)
        {
            await _outputCache.EvictByTagAsync($"layer:{layerId}", cancellationToken);
            await _outputCache.EvictByTagAsync("ogc-metadata", cancellationToken);
            await _outputCache.EvictByTagAsync("ogc-tiles", cancellationToken);
        }
    }

    public Task InvalidateServiceAndLayerAsync(string serviceName, int layerId, CancellationToken cancellationToken)
        => Task.WhenAll(
            InvalidateServiceAsync(serviceName, cancellationToken),
            InvalidateLayerAsync(layerId, cancellationToken));
}
