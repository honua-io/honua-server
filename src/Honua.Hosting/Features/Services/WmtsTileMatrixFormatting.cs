// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Services;

internal static class WmtsTileMatrixFormatting
{
    // CRS84 is longitude then latitude, while a geographic EPSG code such as EPSG:4326 is latitude then longitude, so axis order is not implied by being geographic.
    internal static string FormatTopLeftCorner(string crs, bool isGeographic, double x, double y)
    {
        var axisOrder = SpatialReferenceHelpers.TryParseCrsDefinition(crs, out var definition)
            ? definition.AxisOrder
            : isGeographic
                ? AxisOrder.NorthEast
                : AxisOrder.EastNorth;
        var first = axisOrder == AxisOrder.NorthEast ? y : x;
        var second = axisOrder == AxisOrder.NorthEast ? x : y;

        return string.Concat(FormatOrdinate(first), " ", FormatOrdinate(second));
    }

    private static string FormatOrdinate(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);
}
