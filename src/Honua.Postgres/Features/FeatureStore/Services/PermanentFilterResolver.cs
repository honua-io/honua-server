// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using PermanentFilterLanguages = Honua.Core.Features.Metadata.Domain.V2.MetadataV2PermanentFilterLanguages;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Resolves a layer's metadata-v2 permanent (row-visibility) filter to a
/// parameterized SQL fragment. Shared by <c>PostgresFeatureStoreRefactored</c>
/// and <c>PostgresSpatialAnalyticsReader</c> so every Postgres read surface
/// enforces the same row-visibility rules.
/// </summary>
internal static class PermanentFilterResolver
{
    /// <summary>
    /// Resolves the permanent filter for <paramref name="layerId"/>, or null when
    /// the layer has no enforceable filter (or the optional metadata/filter
    /// services are unavailable). Throws when a saved filter exists but cannot be
    /// parsed or translated — serving unfiltered rows would leak hidden data.
    /// </summary>
    public static async Task<SqlFragment?> ResolveAsync(
        IMetadataV2GraphProvider? v2Provider,
        IFilterExpressionService? filterExpressionService,
        int layerId,
        CancellationToken cancellationToken)
    {
        if (filterExpressionService == null || v2Provider == null)
        {
            return null;
        }

        // Resolve the resource via the storageLayerId -> resource index and read
        // the typed PermanentFilter from the canonical Metadata v2 resource.
        var snapshot = await v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
        {
            return null;
        }

        var v2Filter = resource.PermanentFilter;
        if (v2Filter == null || string.IsNullOrWhiteSpace(v2Filter.Expression))
        {
            return null;
        }
        if (!TryResolveFilterLanguage(v2Filter.Language, out var v2Language))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' uses unsupported language '{v2Filter.Language}'.");
        }
        var v2ParseAndNormalize = filterExpressionService.ParseAndNormalize(v2Language, v2Filter.Expression, resource);
        if (!v2ParseAndNormalize.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' is invalid: {v2ParseAndNormalize.ErrorMessage ?? "Invalid filter."}");
        }
        if (v2ParseAndNormalize.Expression == null)
        {
            return null;
        }
        var v2Translation = filterExpressionService.Translate(v2ParseAndNormalize.Expression, resource);
        if (!v2Translation.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' failed SQL translation: {v2Translation.ErrorMessage ?? "Invalid filter."}");
        }

        return v2Translation.SqlFilter;
    }

    private static bool TryResolveFilterLanguage(string? language, out FilterLanguage filterLanguage)
    {
        filterLanguage = FilterLanguage.ArcGisSql;
        var normalized = (language ?? PermanentFilterLanguages.ArcGisSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case PermanentFilterLanguages.ArcGisSql:
            case "arcgis":
            case "geoservices-sql":
                filterLanguage = FilterLanguage.ArcGisSql;
                return true;
            case PermanentFilterLanguages.Cql2Text:
            case "cql2":
                filterLanguage = FilterLanguage.Cql2Text;
                return true;
            case PermanentFilterLanguages.Cql2Json:
                filterLanguage = FilterLanguage.Cql2Json;
                return true;
            default:
                return false;
        }
    }
}
