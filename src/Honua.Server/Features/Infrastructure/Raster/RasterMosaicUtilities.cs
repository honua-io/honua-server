// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Infrastructure.Raster;

internal static class RasterMosaicUtilities
{
    internal static RasterMergeStrategy ResolveMergeStrategy(CatalogMetadata? metadata, string? mosaicRule = null)
    {
        if (TryParseMergeStrategy(mosaicRule, out var requestStrategy))
        {
            return requestStrategy;
        }

        if (TryParseMergeStrategy(metadata?.RasterMosaic?.MergeStrategy, out var metadataStrategy))
        {
            return metadataStrategy;
        }

        return RasterMergeStrategy.Newest;
    }

    private static bool TryParseMergeStrategy(string? value, out RasterMergeStrategy strategy)
    {
        strategy = RasterMergeStrategy.Newest;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.TryGetProperty("mergeStrategy", out var mergeStrategyProperty))
                {
                    candidate = mergeStrategyProperty.GetString() ?? string.Empty;
                }
                else if (document.RootElement.TryGetProperty("operation", out var operationProperty))
                {
                    candidate = operationProperty.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return TryParseMergeStrategyToken(candidate, out strategy);
    }

    private static bool TryParseMergeStrategyToken(string value, out RasterMergeStrategy strategy)
    {
        strategy = value.Trim().ToLowerInvariant() switch
        {
            "newest" or "latest" or "last" or "mt_last" => RasterMergeStrategy.Newest,
            "oldest" or "first" or "mt_first" => RasterMergeStrategy.Oldest,
            "average" or "avg" or "mean" or "mt_mean" => RasterMergeStrategy.Average,
            "max" or "maximum" or "mt_max" => RasterMergeStrategy.Max,
            "min" or "minimum" or "mt_min" => RasterMergeStrategy.Min,
            _ => RasterMergeStrategy.Newest
        };

        return value.Trim().ToLowerInvariant() is
            "newest" or "latest" or "last" or "mt_last" or
            "oldest" or "first" or "mt_first" or
            "average" or "avg" or "mean" or "mt_mean" or
            "max" or "maximum" or "mt_max" or
            "min" or "minimum" or "mt_min";
    }
}
