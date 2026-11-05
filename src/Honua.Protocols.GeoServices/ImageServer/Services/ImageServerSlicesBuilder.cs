// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerSlicesBuilder
{
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
