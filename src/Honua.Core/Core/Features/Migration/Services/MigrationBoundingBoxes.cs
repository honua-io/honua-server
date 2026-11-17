// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Migration.Services;

internal static class MigrationBoundingBoxes
{
    internal static double[]? Normalize(double[]? bbox)
    {
        if (bbox is null || bbox.Length != 4)
        {
            return null;
        }

        var minX = Math.Min(bbox[0], bbox[2]);
        var minY = Math.Min(bbox[1], bbox[3]);
        var maxX = Math.Max(bbox[0], bbox[2]);
        var maxY = Math.Max(bbox[1], bbox[3]);
        return [minX, minY, maxX, maxY];
    }

    internal static bool Overlap(double[] a, double[] b)
        => a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];

    internal static string Format(double[] bbox)
        => string.Create(CultureInfo.InvariantCulture,
            $"[{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}]");
}
