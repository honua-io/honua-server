// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Resolves a requested non-spatial dimension coordinate (a single slice value)
/// against a registered Zarr store's declared axes to a grid-index slice.
/// Shared by protocol adapters that pin a single slice of a multidimensional
/// cube by coordinate value — for example the Esri ImageServer
/// <c>multidimensionalDefinition</c> / <c>getSamples</c> path (#1869) and the
/// classic WCS additional-dimension <c>SUBSET</c> path (#1872).
/// </summary>
/// <remarks>
/// Resolution covers the additional coordinate axes
/// (<see cref="ZarrStoreMetadata.Axes"/>) and the temporal axis
/// (<see cref="ZarrStoreMetadata.Temporal"/>); spatial axes are addressed
/// geometrically by the caller and are not resolvable here.
/// </remarks>
public static class ZarrDimensionSliceResolver
{
    /// <summary>
    /// Resolves a single slice coordinate on a named dimension to its inclusive
    /// grid index.
    /// </summary>
    /// <param name="metadata">Scanned store metadata.</param>
    /// <param name="dimensionName">Requested dimension name (e.g. <c>StdZ</c>, an elevation axis, or the time axis).</param>
    /// <param name="coordinate">Requested coordinate value. For the time axis this is epoch milliseconds.</param>
    /// <param name="index">Resolved inclusive grid index when the method returns true.</param>
    /// <param name="error">Client-safe error when the method returns false.</param>
    /// <returns>True when the dimension is known and the coordinate resolves to an in-range slice.</returns>
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

    /// <summary>
    /// Locates the grid dimension index for a named axis within a variable's
    /// <see cref="ZarrArrayMetadata.DimensionNames"/>.
    /// </summary>
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
