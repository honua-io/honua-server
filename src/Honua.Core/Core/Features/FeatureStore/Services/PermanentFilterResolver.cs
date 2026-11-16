// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using PermanentFilterLanguages = Honua.Core.Features.Metadata.Domain.V2.MetadataV2PermanentFilterLanguages;

namespace Honua.Core.Features.FeatureStore.Services;

internal static class PermanentFilterResolver
{
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

        return Resolve(resource, filterExpressionService);
    }

    public static SqlFragment? Resolve(
        MetadataV2Object resource,
        IFilterExpressionService? filterExpressionService)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (filterExpressionService == null)
        {
            return null;
        }

        var expression = ParseSavedExpression(resource, filterExpressionService);
        if (expression is null)
        {
            return null;
        }

        var v2Translation = filterExpressionService.Translate(expression, resource);
        if (!v2Translation.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' failed SQL translation: {v2Translation.ErrorMessage ?? "Invalid filter."}");
        }

        return v2Translation.SqlFilter;
    }

    public static FilterExpression? ParseSavedExpression(
        MetadataV2Object resource,
        IFilterExpressionService? filterExpressionService)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var v2Filter = resource.PermanentFilter;
        if (v2Filter == null || string.IsNullOrWhiteSpace(v2Filter.Expression))
        {
            return null;
        }

        if (filterExpressionService == null)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' cannot be evaluated.");
        }

        if (!TryResolveFilterLanguage(v2Filter.Language, out var v2Language))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' uses unsupported language '{v2Filter.Language}'.");
        }

        var parsed = filterExpressionService.ParseAndNormalize(v2Language, v2Filter.Expression, resource);
        if (!parsed.IsSuccess || parsed.Expression == null)
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' is invalid.");
        }

        return parsed.Expression;
    }

    private static bool TryResolveFilterLanguage(string? language, out FilterLanguage filterLanguage)
    {
        filterLanguage = FilterLanguage.GeoServicesSql;
        var normalized = (language ?? PermanentFilterLanguages.GeoServicesSql)
            .Trim()
            .ToLowerInvariant();

        switch (normalized)
        {
            case PermanentFilterLanguages.GeoServicesSql:
            case "arcgis":
            case "geoservices-sql":
                filterLanguage = FilterLanguage.GeoServicesSql;
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
