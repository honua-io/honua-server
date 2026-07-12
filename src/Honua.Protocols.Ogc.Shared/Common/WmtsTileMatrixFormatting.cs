// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Canonical WMTS coordinate formatting shared by every adapter that emits a tile matrix set.
/// </summary>
internal static class WmtsTileMatrixFormatting
{
    /// <summary>
    /// Formats an internal XY top-left origin in the axis order declared by the advertised CRS.
    /// </summary>
    /// <remarks>
    /// CRS84 is EastNorth (longitude, latitude), while geographic EPSG identifiers such as
    /// EPSG:4326 are NorthEast (latitude, longitude). Geographic classification alone cannot
    /// distinguish those contracts. Coordinates use the round-trip format so configured grid
    /// origins do not lose precision in capabilities documents.
    /// </remarks>
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
