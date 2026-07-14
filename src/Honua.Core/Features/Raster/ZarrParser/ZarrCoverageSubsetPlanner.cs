// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// One named-dimension subset over a Zarr array expressed as grid indices:
/// inclusive <paramref name="Start"/>, exclusive <paramref name="Stop"/>.
/// </summary>
/// <param name="Dimension">Dimension name as discovered in the store metadata.</param>
/// <param name="Start">Inclusive start index.</param>
/// <param name="Stop">Exclusive stop index.</param>
public sealed record ZarrCoverageDimensionSubset(string Dimension, int Start, int Stop);

/// <summary>
/// Validated plan for one bounded Zarr coverage read: the resolved array and
/// the per-dimension subset request ready for <see cref="Abstractions.IZarrSubsetReader"/>.
/// </summary>
/// <param name="Array">Array (variable) metadata the plan targets.</param>
/// <param name="Request">Bounded subset request covering every dimension of the array.</param>
public sealed record ZarrCoverageSubsetPlan(ZarrArrayMetadata Array, ZarrSubsetRequest Request);

/// <summary>
/// Translates protocol-level coverage subset requests (named dimensions, grid
/// indices) into bounded <see cref="ZarrSubsetRequest"/> reads. Shared by every
/// protocol adapter that serves registered Zarr stores so variable selection,
/// dimension matching, bounds validation, and output-size limits stay consistent.
/// </summary>
public static class ZarrCoverageSubsetPlanner
{
    /// <summary>
    /// Attempts to build a bounded subset plan for one variable of a Zarr store.
    /// Dimensions without an explicit subset default to their full index range.
    /// </summary>
    /// <param name="metadata">Scanned store metadata.</param>
    /// <param name="variable">Variable name, or null to use the store's primary variable.</param>
    /// <param name="subsets">Named-dimension subsets; may be empty.</param>
    /// <param name="maxOutputBytes">Upper bound for the raw decoded subset payload.</param>
    /// <param name="plan">Validated plan when the method returns true.</param>
    /// <param name="error">Client-safe validation error when the method returns false.</param>
    /// <returns>True when the request is valid and within limits.</returns>
    public static bool TryPlan(
        ZarrStoreMetadata metadata,
        string? variable,
        IReadOnlyList<ZarrCoverageDimensionSubset> subsets,
        long maxOutputBytes,
        out ZarrCoverageSubsetPlan? plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(subsets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);

        plan = null;
        error = null;

        if (metadata.Arrays.Length == 0)
        {
            error = "The Zarr store does not expose any variables.";
            return false;
        }

        var resolvedVariable = string.IsNullOrWhiteSpace(variable)
            ? metadata.PrimaryVariable ?? metadata.Arrays[0].Name
            : variable;

        var array = metadata.Arrays.FirstOrDefault(candidate => string.Equals(candidate.Name, resolvedVariable, StringComparison.Ordinal));

        if (array is null)
        {
            error = $"Variable '{resolvedVariable}' is not available. Available variables: {JoinNames(metadata.Arrays)}.";
            return false;
        }

        var rank = array.Shape.Length;
        var start = new int[rank];
        var stop = new int[rank];
        for (var i = 0; i < rank; i++)
        {
            start[i] = 0;
            stop[i] = array.Shape[i];
        }

        var seen = new HashSet<int>();
        foreach (var subset in subsets)
        {
            var dimensionIndex = FindDimension(array, subset.Dimension);
            if (dimensionIndex < 0)
            {
                error = $"Unknown subset dimension '{subset.Dimension}'. Available dimensions: {string.Join(", ", array.DimensionNames)}.";
                return false;
            }

            if (!seen.Add(dimensionIndex))
            {
                error = $"Subset dimension '{subset.Dimension}' was specified more than once.";
                return false;
            }

            if (subset.Start < 0 || subset.Stop <= subset.Start || subset.Stop > array.Shape[dimensionIndex])
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Subset bounds for dimension '{subset.Dimension}' must satisfy 0 <= low <= high < {array.Shape[dimensionIndex]}.");
                return false;
            }

            start[dimensionIndex] = subset.Start;
            stop[dimensionIndex] = subset.Stop;
        }

        int elementSize;
        try
        {
            elementSize = ZarrSubsetReader.ResolveElementSize(array.DataType);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            error = $"Variable '{array.Name}' uses a data type that is not supported for coverage output.";
            return false;
        }

        var totalBytes = (long)elementSize;
        for (var i = 0; i < rank; i++)
        {
            totalBytes = checked(totalBytes * (stop[i] - start[i]));
        }

        if (totalBytes > maxOutputBytes)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The requested subset decodes to {totalBytes:N0} bytes, exceeding the {maxOutputBytes:N0} byte limit. Add or tighten subset bounds.");
            return false;
        }

        plan = new ZarrCoverageSubsetPlan(array, new ZarrSubsetRequest
        {
            Variable = array.Name,
            Start = start,
            Stop = stop
        });
        return true;
    }

    private static int FindDimension(ZarrArrayMetadata array, string dimension)
    {
        for (var i = 0; i < array.DimensionNames.Length; i++)
        {
            if (string.Equals(array.DimensionNames[i], dimension, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static string JoinNames(ZarrArrayMetadata[] arrays)
    {
        var names = new string[arrays.Length];
        for (var i = 0; i < arrays.Length; i++)
        {
            names[i] = arrays[i].Name;
        }
        return string.Join(", ", names);
    }
}
