// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.DataEnrichment.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Caching;
using Honua.Server.Features.DataEnrichment.Models;

namespace Honua.Server.Features.DataEnrichment;

internal sealed class EnrichmentDatasetCatalogService
{
    private const string DatasetsCacheKey = "enrich:datasets:all";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly IEnrichmentDatasetCatalogStore? _store;
    private readonly EnrichmentCatalog _configCatalog;
    private readonly ICacheService? _cache;
    private readonly ISchemaContext? _schemaContext;

    public EnrichmentDatasetCatalogService(
        IEnrichmentDatasetCatalogStore? store,
        EnrichmentCatalog configCatalog,
        ICacheService? cache,
        ISchemaContext? schemaContext)
    {
        _store = store;
        _configCatalog = configCatalog ?? throw new ArgumentNullException(nameof(configCatalog));
        _cache = cache;
        _schemaContext = schemaContext;
    }

    public bool HasManagedStore => _store is not null;

    public async Task<IReadOnlyList<EnrichmentDatasetMetadata>> ListMetadataAsync(
        HonuaEdition edition,
        CancellationToken cancellationToken)
    {
        var merged = await GetMergedAsync(cancellationToken).ConfigureAwait(false);
        return merged
            .Where(m => ParseEdition(m.MinimumEdition) <= edition)
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<EnrichmentDatasetMetadata?> GetMetadataAsync(
        string id,
        HonuaEdition edition,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var merged = await GetMergedAsync(cancellationToken).ConfigureAwait(false);
        var match = merged.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null || ParseEdition(match.MinimumEdition) > edition)
        {
            return null;
        }

        return match;
    }

    public async Task<ResolvedEnrichmentDataset?> ResolveAsync(string? idOrKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idOrKey))
        {
            return null;
        }

        if (_store is not null)
        {
            var record = await _store.GetAsync(idOrKey, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                return ToResolved(record);
            }
        }

        var configDataset = _configCatalog.Resolve(idOrKey);
        return configDataset is null ? null : ToResolved(configDataset);
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        if (_cache is null)
        {
            return;
        }

        var schema = _schemaContext?.CurrentSchema;
        await _cache.SetAsync(
                CatalogCacheKeys.GetGenerationStateKey(schema),
                Guid.NewGuid().ToString("N"),
                CatalogCacheKeys.MetadataGenerationTtl,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<EnrichmentDatasetMetadata>> GetMergedAsync(CancellationToken cancellationToken)
    {
        var managed = await LoadManagedAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<EnrichmentDatasetMetadata>(managed.Length + 4);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in managed)
        {
            result.Add(ToMetadata(record));
            seenIds.Add(record.Id);
        }

        // Configuration-driven datasets supplement the managed registry; a managed
        // entry with the same id/key wins (the registry is authoritative).
        foreach (var configDataset in _configCatalog.Datasets)
        {
            if (string.IsNullOrWhiteSpace(configDataset.Key) || !seenIds.Add(configDataset.Key))
            {
                continue;
            }

            result.Add(ToMetadata(configDataset));
        }

        return result;
    }

    private async Task<EnrichmentDatasetRecord[]> LoadManagedAsync(CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return [];
        }

        if (_cache is null)
        {
            return (await _store.ListAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }

        var schema = _schemaContext?.CurrentSchema;
        var key = await CatalogCacheKeys.ScopeKeyAsync(_cache, DatasetsCacheKey, schema, cancellationToken)
            .ConfigureAwait(false);

        var cached = await _cache.GetOrSetAsync(
                key,
                async c => (await _store.ListAsync(c).ConfigureAwait(false)).ToArray(),
                CacheTtl,
                cancellationToken)
            .ConfigureAwait(false);

        return cached ?? [];
    }

    private static EnrichmentDatasetMetadata ToMetadata(EnrichmentDatasetRecord record) => new()
    {
        Id = record.Id,
        Title = record.Title,
        Category = record.Category,
        LayerId = record.LayerId,
        GeometryType = record.GeometryType,
        Attributes = record.JoinAttributes.ToArray(),
        DefaultPredicate = record.DefaultPredicate,
        DistanceMeters = record.DistanceMeters,
        Provenance = record.Provenance,
        Attribution = record.Attribution,
        License = record.License,
        MinimumEdition = record.MinimumEdition.ToString(),
        Source = "managed",
    };

    private static EnrichmentDatasetMetadata ToMetadata(
        EnrichmentDatasetOptions configDataset) => new()
        {
            Id = configDataset.Key,
            Title = configDataset.DisplayName ?? configDataset.Key,
            Category = NormalizeCategory(configDataset.Category),
            LayerId = configDataset.LayerId,
            GeometryType = null,
            Attributes = configDataset.Attributes.ToArray(),
            DefaultPredicate = string.IsNullOrWhiteSpace(configDataset.Predicate) ? "intersects" : configDataset.Predicate,
            DistanceMeters = configDataset.DistanceMeters,
            Provenance = null,
            Attribution = null,
            License = null,
            MinimumEdition = HonuaEdition.Pro.ToString(),
            Source = "config",
        };

    private static ResolvedEnrichmentDataset ToResolved(EnrichmentDatasetRecord record) => new(
        record.Id,
        record.Title,
        record.Category,
        record.LayerId,
        record.DefaultPredicate,
        record.DistanceMeters,
        record.JoinAttributes,
        record.Attribution,
        record.MinimumEdition,
        "managed");

    private static ResolvedEnrichmentDataset ToResolved(
        EnrichmentDatasetOptions configDataset) => new(
        configDataset.Key,
        configDataset.DisplayName ?? configDataset.Key,
        NormalizeCategory(configDataset.Category),
        configDataset.LayerId,
        string.IsNullOrWhiteSpace(configDataset.Predicate) ? "intersects" : configDataset.Predicate,
        configDataset.DistanceMeters,
        configDataset.Attributes.ToArray(),
        Attribution: null,
        HonuaEdition.Pro,
        "config");

    private static string NormalizeCategory(string? category)
        => EnrichmentDatasetCategories.IsValid(category) ? category!.ToLowerInvariant() : EnrichmentDatasetCategories.Boundary;

    private static HonuaEdition ParseEdition(string value)
        => Enum.TryParse<HonuaEdition>(value, ignoreCase: true, out var edition) ? edition : HonuaEdition.Pro;
}

internal sealed record ResolvedEnrichmentDataset(
    string Id,
    string? Title,
    string Category,
    int LayerId,
    string DefaultPredicate,
    double? DistanceMeters,
    IReadOnlyList<string> Attributes,
    string? Attribution,
    HonuaEdition MinimumEdition,
    string Source);
