// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Caching.Abstractions;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Cache-key constants and schema-scoped key helpers for catalog/output cache entries.
/// The generation-stamped scope prefix lets <see cref="OutputCacheInvalidationService"/>
/// invalidate every catalog-derived cache entry by bumping a single generation marker.
/// </summary>
internal static class CatalogCacheKeys
{
    public const string LayerKeyPrefix = "layer:";
    public const string LayerListKey = "layers:all";
    public const string ServiceKeyPrefix = "service:";
    public const string ServiceListKey = "services:all";
    public const string LayerExistsKeyPrefix = "layer:exists:";
    public const string ServiceExistsKeyPrefix = "service:exists:";
    public const string MetadataGenerationKey = "catalog:generation";
    public const string MetadataGenerationPrefix = "catalog-gen:";
    public static readonly TimeSpan MetadataGenerationTtl = TimeSpan.FromDays(365);

    public static string GetGenerationStateKey(string? schema) =>
        $"{CacheScopeKeys.GetScopePrefix(schema)}{MetadataGenerationKey}";

    public static async Task<string> ScopeKeyAsync(
        ICacheService cacheService,
        string key,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        string scopePrefix = await GetCurrentScopePrefixAsync(cacheService, schema, cancellationToken).ConfigureAwait(false);
        return $"{scopePrefix}{key}";
    }

    public static async Task<string> GetCurrentScopePrefixAsync(
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
}
