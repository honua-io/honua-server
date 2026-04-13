// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Middleware;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Centralized output cache invalidation for data mutations.
/// </summary>
internal sealed partial class OutputCacheInvalidationService
{
    private readonly IOutputCacheStore? _cacheStore;
    private readonly IResponseCache? _responseCache;
    private readonly ICacheService? _metadataCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICacheRefreshCoordinator? _refreshCoordinator;
    private readonly ILogger<OutputCacheInvalidationService> _logger;

    public OutputCacheInvalidationService(
        IOutputCacheStore? cacheStore,
        IResponseCache? responseCache,
        ICacheService? metadataCache,
        IServiceScopeFactory scopeFactory,
        ICacheRefreshCoordinator? refreshCoordinator,
        ILogger<OutputCacheInvalidationService> logger)
    {
        _cacheStore = cacheStore;
        _responseCache = responseCache;
        _metadataCache = metadataCache;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _refreshCoordinator = refreshCoordinator;
        _logger = logger;
    }

    public async Task InvalidateLayerAsync(string? serviceId, int? layerId, CancellationToken cancellationToken)
    {
        var normalizedServiceIds = await ResolveLayerServiceIdsAsync(serviceId, layerId, cancellationToken).ConfigureAwait(false);
        var tags = new List<string>();
        var responsePatterns = new List<string>();

        foreach (var normalizedServiceId in normalizedServiceIds)
        {
            tags.Add($"service:{normalizedServiceId}");
        }

        if (layerId.HasValue)
        {
            tags.Add($"layer:{layerId.Value}");
            tags.Add($"collection:{layerId.Value}");
            tags.Add("service-metadata");
            tags.Add("tiles");
            tags.Add("layer-metadata");
            tags.Add("layer-styles");
            tags.Add("ogc-maps");
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(layerId.Value));
            responsePatterns.Add(ResponseCacheUtilities.BuildODataLayerPattern(layerId.Value));
            responsePatterns.Add(ResponseCacheUtilities.BuildOgcCollectionPattern(
                layerId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (layerId.HasValue)
        {
            foreach (var normalizedServiceId in normalizedServiceIds)
            {
                responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerServicePattern(normalizedServiceId));
                responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(normalizedServiceId, layerId.Value));
            }
        }

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapServicePattern(serviceId));
        }
        else if (layerId.HasValue)
        {
            if (normalizedServiceIds.Length > 0)
            {
                // Use resolved owning services for targeted static-map eviction.
                foreach (var normalizedServiceId in normalizedServiceIds)
                {
                    responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapServicePattern(normalizedServiceId));
                }
            }
            else
            {
                // No owning services resolved — fall back to global eviction.
                responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapPattern());
            }
        }

        tags.Add("ogc-metadata");
        tags.Add("ogc-tiles");
        tags.Add("ogc-maps");
        tags.Add("mvt-tiles");

        await Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken)).ConfigureAwait(false);
    }

    public Task InvalidateCollectionAsync(string collectionId, CancellationToken cancellationToken)
    {
        var tags = new List<string>
        {
            $"collection:{collectionId.Trim().ToLowerInvariant()}",
            "ogc-metadata",
            "ogc-tiles",
            "ogc-maps",
            "mvt-tiles"
        };

        var responsePatterns = new List<string>
        {
            ResponseCacheUtilities.BuildOgcCollectionPattern(collectionId)
        };

        return Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken));
    }

    public Task InvalidateOgcMetadataAsync(CancellationToken cancellationToken)
    {
        return EvictTagsAsync(["ogc-metadata", "ogc-tiles", "ogc-maps"], cancellationToken);
    }

    public Task InvalidateServiceCatalogAsync(
        string? serviceId,
        IEnumerable<int>? layerIds,
        CancellationToken cancellationToken)
    {
        var normalizedServiceId = string.IsNullOrWhiteSpace(serviceId)
            ? null
            : serviceId.Trim().ToLowerInvariant();
        var layerIdList = layerIds?
            .Distinct()
            .ToArray() ?? [];

        var tags = new List<string>
        {
            "service-directory",
            "service-metadata",
            "tiles",
            "layer-metadata",
            "layer-styles",
            "metadata",
            "ogc-metadata",
            "ogc-tiles",
            "ogc-maps",
            "stac-metadata",
            "mvt-tiles"
        };

        if (!string.IsNullOrWhiteSpace(normalizedServiceId))
        {
            tags.Add($"service:{normalizedServiceId}");
        }

        var responsePatterns = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalizedServiceId))
        {
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerServicePattern(normalizedServiceId));
            responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapServicePattern(normalizedServiceId));
        }

        foreach (var layerId in layerIdList)
        {
            tags.Add($"layer:{layerId}");
            tags.Add($"collection:{layerId}");

            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(layerId));
            responsePatterns.Add(ResponseCacheUtilities.BuildODataLayerPattern(layerId));
            responsePatterns.Add(ResponseCacheUtilities.BuildOgcCollectionPattern(
                layerId.ToString(CultureInfo.InvariantCulture)));

            if (!string.IsNullOrWhiteSpace(normalizedServiceId))
            {
                responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(normalizedServiceId, layerId));
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedServiceId) && layerIdList.Length == 0)
        {
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerPattern());
            responsePatterns.Add(ResponseCacheUtilities.BuildOgcPattern());
            responsePatterns.Add(ResponseCacheUtilities.BuildODataPattern());
            responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapPattern());
        }
        else if (string.IsNullOrWhiteSpace(normalizedServiceId) && layerIdList.Length > 0)
        {
            // Without a serviceId we cannot scope to a single service's static map cache.
            // Evict all static map entries because any service may render these layers.
            responsePatterns.Add(ResponseCacheUtilities.BuildStaticMapPattern());
        }

        return Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken),
            EvictCatalogMetadataCacheAsync(normalizedServiceId, layerIdList, cancellationToken));
    }

    private async Task EvictTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        if (_cacheStore == null)
        {
            return;
        }

        foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _cacheStore.EvictByTagAsync(tag, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEvictTagFailed(_logger, tag, ex);
            }
        }
    }

    private async Task EvictResponseCacheAsync(IEnumerable<string> patterns, CancellationToken cancellationToken)
    {
        if (_responseCache == null)
        {
            return;
        }

        foreach (var pattern in patterns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                await _responseCache.RemoveByPatternAsync(pattern, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEvictPatternFailed(_logger, pattern, ex);
            }
        }
    }

    private async Task EvictCatalogMetadataCacheAsync(
        string? serviceId,
        IReadOnlyCollection<int> layerIds,
        CancellationToken cancellationToken)
    {
        if (_metadataCache == null)
        {
            return;
        }

        var keys = new List<string>
        {
            CachingLayerCatalog.ServiceListKey,
            CachingLayerCatalog.LayerListKey
        };

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            keys.Add($"{CachingLayerCatalog.ServiceKeyPrefix}{serviceId}");
            keys.Add($"{CachingLayerCatalog.ServiceExistsKeyPrefix}{serviceId}");
        }

        foreach (var layerId in layerIds)
        {
            keys.Add($"{CachingLayerCatalog.LayerKeyPrefix}{layerId}");
            keys.Add($"{CachingLayerCatalog.LayerExistsKeyPrefix}{layerId}");
        }

        string? currentSchema = SchemaContext.AmbientCurrentSchema;

        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string scopedKey = await CachingLayerCatalog.ScopeKeyAsync(
                _metadataCache,
                key,
                currentSchema,
                cancellationToken).ConfigureAwait(false);

            // Notify the refresh coordinator so any pending background refresh
            // for this key skips stale-value restoration on failure.
            _refreshCoordinator?.NotifyInvalidation(scopedKey);

            try
            {
                await _metadataCache.RemoveAsync(scopedKey, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEvictMetadataKeyFailed(_logger, scopedKey, ex);
            }
        }

        // Metadata cache keys are already schema-scoped by the cache implementation for
        // the current request. Explicit scoped wildcard invalidation forces Redis keyspace
        // scans on mutation paths, so this invalidator sticks to direct-key eviction here.
    }
    private async Task<string[]> ResolveLayerServiceIdsAsync(
        string? serviceId,
        int? layerId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            return [NormalizeServiceId(serviceId)];
        }

        if (!layerId.HasValue)
        {
            return [];
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var layerCatalog = scope.ServiceProvider.GetService<ILayerCatalog>();
            if (layerCatalog == null)
            {
                return [];
            }

            var services = await layerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
            return services
                .Where(service => service.Layers.Any(candidate => candidate.Id == layerId.Value))
                .Select(service => NormalizeServiceId(service.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveLayerServicesFailed(_logger, layerId.Value, ex);
            return [];
        }
    }

    private static string NormalizeServiceId(string serviceId)
        => serviceId.Trim().ToLowerInvariant();

    [LoggerMessage(EventId = 4510, Level = LogLevel.Warning,
        Message = "Failed to evict output cache tag {Tag}")]
    private static partial void LogEvictTagFailed(ILogger logger, string tag, Exception exception);

    [LoggerMessage(EventId = 4511, Level = LogLevel.Warning,
        Message = "Failed to evict response cache pattern {Pattern}")]
    private static partial void LogEvictPatternFailed(ILogger logger, string pattern, Exception exception);

    [LoggerMessage(EventId = 4512, Level = LogLevel.Warning,
        Message = "Failed to evict metadata cache key {Key}")]
    private static partial void LogEvictMetadataKeyFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(EventId = 4513, Level = LogLevel.Warning,
        Message = "Failed to evict metadata cache pattern {Pattern}")]
    private static partial void LogEvictMetadataPatternFailed(ILogger logger, string pattern, Exception exception);

    [LoggerMessage(EventId = 4515, Level = LogLevel.Warning,
        Message = "Failed to resolve owning services for layer {LayerId} during cache invalidation")]
    private static partial void LogResolveLayerServicesFailed(ILogger logger, int layerId, Exception exception);
}
