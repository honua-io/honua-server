// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Decorator that adds caching to ILayerStyleCatalog operations.
/// </summary>
internal sealed class CachingLayerStyleCatalog : ILayerStyleCatalog
{
    private const string StyleKeyPrefix = "layer-style:";
    private const string StyleExistsKeyPrefix = "layer-style:exists:";

    private readonly ILayerStyleCatalog _innerCatalog;
    private readonly ICacheService _cacheService;
    private readonly CacheOptions _options;
    private readonly ISchemaContext? _schemaContext;

    public CachingLayerStyleCatalog(
        ILayerStyleCatalog innerCatalog,
        ICacheService cacheService,
        IOptions<CacheOptions> options,
        ISchemaContext? schemaContext = null)
    {
        _innerCatalog = innerCatalog ?? throw new ArgumentNullException(nameof(innerCatalog));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _schemaContext = schemaContext;
    }

    public async Task<LayerStyleDefinition?> GetLayerStyleAsync(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var styleKey = GetStyleKey(layerId);
        var existsKey = GetStyleExistsKey(layerId);

        var cached = await _cacheService.GetAsync<LayerStyleDefinition>(styleKey, cancellationToken).ConfigureAwait(false);
        if (cached != null)
        {
            return cached;
        }

        var cachedExists = await _cacheService.GetAsync<CachedExistenceResult>(existsKey, cancellationToken).ConfigureAwait(false);
        if (cachedExists != null && !cachedExists.Exists)
        {
            return null;
        }

        var styleTtl = _options.GetLayerTtlWithJitter();
        var style = await _cacheService.GetOrSetAsync(
            styleKey,
            ct => _innerCatalog.GetLayerStyleAsync(layerId, ct),
            styleTtl,
            cancellationToken).ConfigureAwait(false);

        if (style != null)
        {
            await _cacheService.SetAsync(
                existsKey,
                new CachedExistenceResult(true),
                styleTtl,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _cacheService.SetAsync(
                existsKey,
                new CachedExistenceResult(false),
                _options.GetNegativeTtlWithJitter(),
                cancellationToken).ConfigureAwait(false);
        }

        return style;
    }

    public async Task<LayerStyleDefinition?> SetMapLibreStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        var updated = await _innerCatalog
            .SetMapLibreStyleAsync(layerId, mapLibreStyleJson, revisedBy, changeSummary, cancellationToken)
            .ConfigureAwait(false);
        await RefreshCacheAsync(layerId, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<LayerStyleDefinition?> SetStyleAsync(
        int layerId,
        string mapLibreStyleJson,
        string drawingInfoJson,
        string? revisedBy = null,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        var updated = await _innerCatalog
            .SetStyleAsync(layerId, mapLibreStyleJson, drawingInfoJson, revisedBy, changeSummary, cancellationToken)
            .ConfigureAwait(false);
        await RefreshCacheAsync(layerId, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<LayerStyleDefinition?> SetDrawingInfoAsync(
        int layerId,
        string drawingInfoJson,
        CancellationToken cancellationToken = default)
    {
        var updated = await _innerCatalog.SetDrawingInfoAsync(layerId, drawingInfoJson, cancellationToken).ConfigureAwait(false);
        await RefreshCacheAsync(layerId, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task RefreshCacheAsync(
        int layerId,
        LayerStyleDefinition? style,
        CancellationToken cancellationToken)
    {
        var styleKey = GetStyleKey(layerId);
        var existsKey = GetStyleExistsKey(layerId);

        if (style == null)
        {
            await _cacheService.RemoveAsync(styleKey, cancellationToken).ConfigureAwait(false);
            await _cacheService.SetAsync(
                existsKey,
                new CachedExistenceResult(false),
                _options.GetNegativeTtlWithJitter(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var ttl = _options.GetLayerTtlWithJitter();
        await _cacheService.SetAsync(styleKey, style, ttl, cancellationToken).ConfigureAwait(false);
        await _cacheService.SetAsync(
            existsKey,
            new CachedExistenceResult(true),
            ttl,
            cancellationToken).ConfigureAwait(false);
    }

    private string GetStyleKey(int layerId) => ScopeKey($"{StyleKeyPrefix}{layerId}");

    private string GetStyleExistsKey(int layerId) => ScopeKey($"{StyleExistsKeyPrefix}{layerId}");

    private string ScopeKey(string key) => $"{GetScopePrefix()}{key}";

    private string GetScopePrefix()
    {
        var schema = _schemaContext?.CurrentSchema;
        return string.IsNullOrWhiteSpace(schema)
            ? "scope:default:"
            : $"scope:schema:{schema.Trim().ToLowerInvariant()}:";
    }
}
