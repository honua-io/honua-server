// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Centralized output cache invalidation for data mutations.
/// </summary>
internal sealed class OutputCacheInvalidationService
{
    private readonly IOutputCacheStore? _cacheStore;
    private readonly ILogger<OutputCacheInvalidationService> _logger;

    public OutputCacheInvalidationService(IOutputCacheStore? cacheStore, ILogger<OutputCacheInvalidationService> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public Task InvalidateLayerAsync(string? serviceId, int? layerId, CancellationToken cancellationToken)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            tags.Add($"service:{serviceId.Trim().ToLowerInvariant()}");
        }

        if (layerId.HasValue)
        {
            tags.Add($"layer:{layerId.Value}");
            tags.Add($"collection:{layerId.Value}");
        }

        tags.Add("ogc-metadata");
        tags.Add("ogc-tiles");

        return EvictTagsAsync(tags, cancellationToken);
    }

    public Task InvalidateCollectionAsync(string collectionId, CancellationToken cancellationToken)
    {
        var tags = new List<string>
        {
            $"collection:{collectionId.Trim().ToLowerInvariant()}",
            "ogc-metadata",
            "ogc-tiles"
        };

        return EvictTagsAsync(tags, cancellationToken);
    }

    public Task InvalidateOgcMetadataAsync(CancellationToken cancellationToken)
    {
        return EvictTagsAsync(["ogc-metadata", "ogc-tiles"], cancellationToken);
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
                _logger.LogWarning(ex, "Failed to evict output cache tag {Tag}", tag);
            }
        }
    }
}
