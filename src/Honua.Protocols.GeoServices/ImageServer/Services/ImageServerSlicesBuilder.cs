// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Derives the Esri ImageServer <c>slices</c> document from a
/// <see cref="ImageServerMultidimensionalInfo"/>. A slice is one unique combination
/// of dimension coordinate values for a variable. Slices can only be enumerated when
/// every relevant dimension exposes explicit coordinate <c>values</c>; dimensions
/// that only carry an extent (no enumerable values) cannot be sliced, so such
/// variables contribute no slices (the spec-correct honest empty result).
/// </summary>
internal static class ImageServerSlicesBuilder
{
    /// <summary>
    /// Builds the ordered set of slices for the supplied multidimensional info. The
    /// result is empty when there are no variables or no dimension carries enumerable
    /// coordinate values.
    /// </summary>
    public static ImageServerSlice[] Build(ImageServerMultidimensionalInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (info.Variables.Length == 0)
        {
            return [];
        }

        var slices = new List<ImageServerSlice>();
        long sliceId = 0;

        foreach (var variable in info.Variables)
        {
            // Only dimensions with explicit, enumerable coordinate values can be
            // sliced. Dimensions known only by extent are skipped.
            var sliceableDimensions = variable.Dimensions
                .Where(static d => d.Values is { Length: > 0 })
                .ToArray();

            if (sliceableDimensions.Length == 0)
            {
                continue;
            }

            foreach (var combination in CartesianProduct(sliceableDimensions))
            {
                slices.Add(new ImageServerSlice
                {
                    SliceId = sliceId++,
                    MultidimensionalDefinition = combination
                        .Select((value, index) => new ImageServerSliceDimension
                        {
                            VariableName = variable.Name,
                            DimensionName = sliceableDimensions[index].Name,
                            Values = [value]
                        })
                        .ToArray()
                });
            }
        }

        return [.. slices];
    }

    /// <summary>
    /// Enumerates the Cartesian product of each dimension's coordinate values,
    /// yielding one coordinate per dimension in the original dimension order.
    /// </summary>
    private static IEnumerable<double[]> CartesianProduct(ImageServerMultidimensionalDimension[] dimensions)
    {
        IEnumerable<double[]> seed = [[]];

        return dimensions.Aggregate(
            seed,
            static (accumulator, dimension) =>
                from prefix in accumulator
                from value in dimension.Values!
                select (double[])[.. prefix, value]);
    }
}
