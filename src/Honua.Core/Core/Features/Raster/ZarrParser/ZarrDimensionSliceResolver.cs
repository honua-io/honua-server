// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public static class ZarrDimensionSliceResolver
{
    public static bool TryResolveSliceIndex(
        ZarrStoreMetadata metadata,
        string dimensionName,
        double coordinate,
        out int index,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);

        index = 0;
        error = null;

        // Time axis: the request encodes the coordinate as epoch milliseconds.
        if (metadata.TemporalDimension is { } timeDim &&
            string.Equals(timeDim, dimensionName, StringComparison.OrdinalIgnoreCase))
        {
            if (metadata.Temporal is not { } temporal)
            {
                error = $"The coverage declares time dimension '{timeDim}' but no resolvable time extent.";
                return false;
            }

            DateTimeOffset instant;
            try
            {
                instant = DateTimeOffset.FromUnixTimeMilliseconds((long)coordinate);
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "The requested time coordinate is outside the supported instant range.";
                return false;
            }

            if (!CfTimeAxisIndexer.TryResolveTimeIndexRange(
                    temporal.Start, temporal.End, temporal.StepCount, instant, instant, out var low, out _, out var timeError))
            {
                error = timeError;
                return false;
            }
            index = low;
            return true;
        }

        var axis = metadata.Axes.FirstOrDefault(a => string.Equals(a.Name, dimensionName, StringComparison.OrdinalIgnoreCase));
        if (axis is not null)
        {
            if (!CfCoordinateAxisIndexer.TryResolveIndexRange(axis, coordinate, coordinate, out var low, out _, out var axisError))
            {
                error = axisError;
                return false;
            }
            index = low;
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"The coverage does not declare a resolvable dimension axis '{dimensionName}'. Available axes: {DescribeAxes(metadata)}.");
        return false;
    }

    // StdZ maps only when the store has exactly one non-temporal coordinate axis, so the mapping is never a guess.
    public static string ResolveAdvertisedDimensionName(ZarrStoreMetadata metadata, string dimensionName)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(dimensionName) || DeclaresDimension(metadata, dimensionName))
        {
            return dimensionName;
        }

        if (string.Equals(dimensionName, "StdTime", StringComparison.OrdinalIgnoreCase))
        {
            return metadata.TemporalDimension ?? dimensionName;
        }

        if (string.Equals(dimensionName, "StdZ", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = metadata.Axes
                .Where(axis => !string.Equals(axis.Name, metadata.TemporalDimension, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return candidates.Length == 1 ? candidates[0].Name : dimensionName;
        }

        return dimensionName;
    }

    private static bool DeclaresDimension(ZarrStoreMetadata metadata, string dimensionName)
        => string.Equals(metadata.TemporalDimension, dimensionName, StringComparison.OrdinalIgnoreCase) ||
           metadata.Axes.Any(axis => string.Equals(axis.Name, dimensionName, StringComparison.OrdinalIgnoreCase)) ||
           metadata.Arrays.Any(array => array.DimensionNames.Any(
               name => string.Equals(name, dimensionName, StringComparison.OrdinalIgnoreCase)));

    public static bool TryFindArrayDimension(ZarrArrayMetadata array, string dimensionName, out int dimensionIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < array.DimensionNames.Length; i++)
        {
            if (string.Equals(array.DimensionNames[i], dimensionName, StringComparison.OrdinalIgnoreCase))
            {
                dimensionIndex = i;
                return true;
            }
        }
        dimensionIndex = -1;
        return false;
    }

    private static string DescribeAxes(ZarrStoreMetadata metadata)
    {
        var names = new List<string>();
        if (metadata.TemporalDimension is { } t)
        {
            names.Add(t);
        }
        foreach (var axis in metadata.Axes)
        {
            names.Add(axis.Name);
        }
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
