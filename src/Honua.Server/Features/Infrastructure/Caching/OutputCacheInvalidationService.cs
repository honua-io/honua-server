// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
        var ogcCollectionIds = layerId.HasValue
            ? await ResolveLayerCollectionIdsAsync(layerId.Value, cancellationToken).ConfigureAwait(false)
            : [];
        var tags = new List<string>();
        var responsePatterns = new List<string>();

        foreach (var normalizedServiceId in normalizedServiceIds)
        {
            tags.Add($"service:{normalizedServiceId}");
        }

        if (layerId.HasValue)
        {
            tags.Add($"layer:{layerId.Value}");
            tags.Add("service-metadata");
            tags.Add("tiles");
            tags.Add("layer-metadata");
            tags.Add("layer-styles");
            tags.Add("terrain");
            tags.Add("ogc-maps");
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(layerId.Value));
            responsePatterns.Add(ResponseCacheUtilities.BuildODataLayerPattern(layerId.Value));
            foreach (var ogcCollectionId in ogcCollectionIds)
            {
                tags.Add($"collection:{ogcCollectionId.Trim().ToLowerInvariant()}");
                responsePatterns.Add(ResponseCacheUtilities.BuildOgcCollectionPattern(ogcCollectionId));
            }
        }

        if (layerId.HasValue)
        {
            foreach (var normalizedServiceId in normalizedServiceIds)
            {
                responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerServicePattern(normalizedServiceId));
                responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(normalizedServiceId, layerId.Value));
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

    public async Task InvalidateCollectionAsync(string collectionId, CancellationToken cancellationToken)
    {
        var collectionIds = await ResolveCollectionIdsAsync(collectionId, cancellationToken).ConfigureAwait(false);
        var tags = new List<string>
        {
            "ogc-metadata",
            "ogc-tiles",
            "ogc-maps",
            "mvt-tiles",
            "terrain"
        };
        foreach (var resolvedCollectionId in collectionIds)
        {
            tags.Add($"collection:{resolvedCollectionId.Trim().ToLowerInvariant()}");
        }

        var responsePatterns = collectionIds
            .Select(ResponseCacheUtilities.BuildOgcCollectionPattern)
            .ToList();

        await Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken)).ConfigureAwait(false);
    }

    public Task InvalidateOgcMetadataAsync(CancellationToken cancellationToken)
    {
        return EvictTagsAsync(["ogc-metadata", "ogc-tiles", "ogc-maps"], cancellationToken);
    }

    /// <summary>
    /// Evicts hosted scene serving entries from the output cache after a
    /// scene-registry mutation (register/update/deactivate). Removes the
    /// per-scene tag plus the broader <c>scene</c> tag so anonymous cached
    /// tilesets and assets cannot survive an access-flag or deactivation
    /// change.
    /// </summary>
    public Task InvalidateSceneAsync(string? sceneId, CancellationToken cancellationToken)
    {
        var tags = new List<string> { "scene" };
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            tags.Add($"scene:{sceneId.Trim().ToLowerInvariant()}");
        }
        return EvictTagsAsync(tags, cancellationToken);
    }

    public async Task InvalidateServiceCatalogAsync(
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
            "mvt-tiles",
            "terrain"
        };

        if (!string.IsNullOrWhiteSpace(normalizedServiceId))
        {
            tags.Add($"service:{normalizedServiceId}");
        }

        var responsePatterns = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalizedServiceId))
        {
            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerServicePattern(normalizedServiceId));
        }

        foreach (var layerId in layerIdList)
        {
            tags.Add($"layer:{layerId}");

            responsePatterns.Add(ResponseCacheUtilities.BuildFeatureServerLayerPattern(layerId));
            responsePatterns.Add(ResponseCacheUtilities.BuildODataLayerPattern(layerId));
            var ogcCollectionIds = await ResolveLayerCollectionIdsAsync(layerId, cancellationToken).ConfigureAwait(false);
            foreach (var ogcCollectionId in ogcCollectionIds)
            {
                tags.Add($"collection:{ogcCollectionId.Trim().ToLowerInvariant()}");
                responsePatterns.Add(ResponseCacheUtilities.BuildOgcCollectionPattern(ogcCollectionId));
            }

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
        }

        await Task.WhenAll(
            EvictTagsAsync(tags, cancellationToken),
            EvictResponseCacheAsync(responsePatterns, cancellationToken),
            EvictCatalogCacheAsync(normalizedServiceId, layerIdList, cancellationToken)).ConfigureAwait(false);
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

    private async Task EvictCatalogCacheAsync(
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
            CatalogCacheKeys.ServiceListKey,
            CatalogCacheKeys.LayerListKey
        };

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            keys.Add($"{CatalogCacheKeys.ServiceKeyPrefix}{serviceId}");
            keys.Add($"{CatalogCacheKeys.ServiceExistsKeyPrefix}{serviceId}");
        }

        foreach (var layerId in layerIds)
        {
            keys.Add($"{CatalogCacheKeys.LayerKeyPrefix}{layerId}");
            keys.Add($"{CatalogCacheKeys.LayerExistsKeyPrefix}{layerId}");
        }

        string? currentSchema = SchemaContext.AmbientCurrentSchema;

        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string scopedKey = await CatalogCacheKeys.ScopeKeyAsync(
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
            var graphProvider = scope.ServiceProvider.GetService<IMetadataV2GraphProvider>();
            if (graphProvider == null)
            {
                return [];
            }

            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var serviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pub in snapshot.Graph.Publications)
            {
                if (pub.LayerIndex != layerId.Value)
                {
                    continue;
                }
                if (snapshot.Index.ServicesById.TryGetValue(pub.ServiceId, out var service))
                {
                    serviceIds.Add(NormalizeServiceId(service.Metadata.Name));
                }
            }
            return serviceIds.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveLayerServicesFailed(_logger, layerId.Value, ex);
            return [];
        }
    }

    private async Task<string[]> ResolveLayerCollectionIdsAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var collectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            layerId.ToString(CultureInfo.InvariantCulture)
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var graphProvider = scope.ServiceProvider.GetService<IMetadataV2GraphProvider>();
            if (graphProvider == null)
            {
                return collectionIds.ToArray();
            }

            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pub in snapshot.Graph.Publications)
            {
                if (pub.LayerIndex != layerId)
                {
                    continue;
                }
                var resource = snapshot.ResolveResource(pub);
                if (!string.IsNullOrWhiteSpace(resource?.Metadata.Name))
                {
                    collectionIds.Add(resource.Metadata.Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveLayerCollectionIdsFailed(_logger, layerId, ex);
        }

        return collectionIds.ToArray();
    }

    private async Task<string[]> ResolveCollectionIdsAsync(
        string collectionId,
        CancellationToken cancellationToken)
    {
        var collectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            collectionId
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var graphProvider = scope.ServiceProvider.GetService<IMetadataV2GraphProvider>();
            if (graphProvider == null)
            {
                return collectionIds.ToArray();
            }

            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            MetadataV2Publication? matchedPub = null;
            MetadataV2Resource? matchedResource = null;

            if (int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                matchedPub = snapshot.Graph.Publications
                    .Where(p => p.LayerIndex == layerId)
                    .OrderBy(p => p.Metadata.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (matchedPub is not null)
                {
                    matchedResource = snapshot.ResolveResource(matchedPub);
                }
            }

            if (matchedResource is null)
            {
                foreach (var resource in snapshot.Graph.Resources)
                {
                    if (string.Equals(resource.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedResource = resource;
                        matchedPub = snapshot.Index.PublicationsByResource[resource.Metadata.Id]
                            .OrderBy(p => p.LayerIndex ?? int.MaxValue)
                            .FirstOrDefault();
                        break;
                    }
                }
            }

            if (matchedResource is not null)
            {
                if (matchedPub?.LayerIndex.HasValue == true)
                {
                    collectionIds.Add(matchedPub.LayerIndex.Value.ToString(CultureInfo.InvariantCulture));
                }
                if (!string.IsNullOrWhiteSpace(matchedResource.Metadata.Name))
                {
                    collectionIds.Add(matchedResource.Metadata.Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResolveCollectionIdsFailed(_logger, collectionId, ex);
        }

        return collectionIds.ToArray();
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

    [LoggerMessage(EventId = 4516, Level = LogLevel.Warning,
        Message = "Failed to resolve OGC collection identifiers for layer {LayerId} during cache invalidation")]
    private static partial void LogResolveLayerCollectionIdsFailed(ILogger logger, int layerId, Exception exception);

    [LoggerMessage(EventId = 4517, Level = LogLevel.Warning,
        Message = "Failed to resolve OGC collection identifiers for collection {CollectionId} during cache invalidation")]
    private static partial void LogResolveCollectionIdsFailed(ILogger logger, string collectionId, Exception exception);
}
