// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class GeoServicesStyleBuilder
{
    public static Dictionary<string, object?> BuildSymbol(
        GeometryType geometryType,
        StyleColor fillColor,
        StyleColor? outlineColor,
        double? lineWidth,
        double? size)
    {
        switch (geometryType)
        {
            case GeometryType.Point:
            case GeometryType.MultiPoint:
                return new Dictionary<string, object?>
                {
                    ["type"] = "esriSMS",
                    ["style"] = "esriSMSCircle",
                    ["color"] = fillColor.ToArray(),
                    ["size"] = size ?? StyleDefaults.DefaultPointSize,
                    ["outline"] = outlineColor.HasValue
                        ? BuildOutlineSymbol(outlineColor.Value, lineWidth ?? StyleDefaults.DefaultOutlineWidth)
                        : null
                };
            case GeometryType.LineString:
            case GeometryType.MultiLineString:
                return new Dictionary<string, object?>
                {
                    ["type"] = "esriSLS",
                    ["style"] = "esriSLSSolid",
                    ["color"] = fillColor.ToArray(),
                    ["width"] = lineWidth ?? StyleDefaults.DefaultLineWidth
                };
            case GeometryType.Polygon:
            case GeometryType.MultiPolygon:
            case GeometryType.GeometryCollection:
                return new Dictionary<string, object?>
                {
                    ["type"] = "esriSFS",
                    ["style"] = "esriSFSSolid",
                    ["color"] = fillColor.ToArray(),
                    ["outline"] = outlineColor.HasValue
                        ? BuildOutlineSymbol(outlineColor.Value, lineWidth ?? StyleDefaults.DefaultOutlineWidth)
                        : null
                };
            default:
                return new Dictionary<string, object?>
                {
                    ["type"] = "esriSLS",
                    ["style"] = "esriSLSSolid",
                    ["color"] = fillColor.ToArray(),
                    ["width"] = lineWidth ?? StyleDefaults.DefaultLineWidth
                };
        }
    }

    public static Dictionary<string, object?> BuildOutlineSymbol(StyleColor color, double width)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "esriSLS",
            ["style"] = "esriSLSSolid",
            ["color"] = color.ToArray(),
            ["width"] = width
        };
    }
}
