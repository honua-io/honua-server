// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Infrastructure.Raster;

internal static class RasterMosaicUtilities
{
    internal static RasterMergeStrategy ResolveMergeStrategy(MetadataV2Resource? resource, string? mosaicRule = null)
    {
        if (TryParseMergeStrategy(mosaicRule, out var requestStrategy))
        {
            return requestStrategy;
        }

        if (resource is not null &&
            resource.Extensions.TryGetValue("rasterMosaic", out var rasterMosaic) &&
            rasterMosaic.ValueKind == JsonValueKind.Object &&
            TryReadStringProperty(rasterMosaic, "mergeStrategy", out var mergeStrategy) &&
            TryParseMergeStrategy(mergeStrategy, out var resourceStrategy))
        {
            return resourceStrategy;
        }

        return RasterMergeStrategy.Newest;
    }

    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string? value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return true;
            }
        }

        value = null;
        return false;
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
