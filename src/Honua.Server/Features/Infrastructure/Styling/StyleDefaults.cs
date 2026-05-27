// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class StyleDefaults
{
    public static readonly int[] DefaultStrokeColor = [45, 105, 165, 255];

    public const double DefaultLineWidth = 2d;
    public const double DefaultOutlineWidth = 1d;
    /// <summary>Conversion fallback when an existing style omits point size; distinct from default style radius (4d) and GeoServices size (8d).</summary>
    public const double DefaultPointSize = 6d;

    public const string SourceLayerName = "layer";

    public static string GetSourceId(int layerId) => $"layer-{layerId}";

    public static string GetSourceId(StyleLayerDescriptor layer) => GetSourceId(layer.Id);

    public static string GetTileUrl(int layerId) => $"/tiles/{layerId}/{{z}}/{{x}}/{{y}}.mvt";

    /// <summary>
    /// Builds a MapLibre guard expression that returns <c>true</c> only when the field
    /// is present AND its runtime type is <c>"number"</c>.  This routes null, missing,
    /// string, and other non-numeric values to the fallback branch of a <c>case</c>
    /// expression, preventing <c>to-number</c> from silently coercing them to 0.
    /// </summary>
    internal static object[] BuildNumericFieldGuard(string fieldName) =>
    [
        "all",
        new object[] { "has", fieldName },
        new object[] { "==", new object[] { "typeof", new object[] { "get", fieldName } }, "number" }
    ];

    /// <summary>
    /// Builds a MapLibre guard expression that returns <c>true</c> only when the field
    /// is present AND its value is not null.  This routes missing and null values to
    /// the fallback branch of a <c>case</c> expression, preventing <c>to-string(null)</c>
    /// from coercing them to <c>""</c> and accidentally matching an empty-string category.
    /// </summary>
    internal static object[] BuildNonNullFieldGuard(string fieldName) =>
    [
        "all",
        new object[] { "has", fieldName },
        new object[] { "!=", new object[] { "typeof", new object[] { "get", fieldName } }, "null" }
    ];

    public static Dictionary<string, object?> BuildDefaultMapLibreStyle(
        MetadataV2Resource resource,
        int storageLayerId)
        => BuildDefaultMapLibreStyle(storageLayerId, resource.Metadata.Name, resource.ReadGeometryType());

    public static Dictionary<string, object?> BuildDefaultMapLibreStyle(StyleLayerDescriptor layer)
        => BuildDefaultMapLibreStyle(layer.Id, layer.Name, layer.GeometryType);

    public static Dictionary<string, object?> BuildDefaultMapLibreStyle(
        int layerId,
        string layerName,
        MetadataV2GeometryType geometryType)
    {
        var sourceId = GetSourceId(layerId);
        var layers = BuildDefaultLayers(layerId, geometryType, sourceId);
        return BuildStyleDocument(layerId, layerName, layers);
    }

    /// <summary>
    /// Wraps a layer list in a complete MapLibre v8 style document with vector tile source.
    /// </summary>
    internal static Dictionary<string, object?> BuildStyleDocument(
        int layerId,
        string layerName,
        List<Dictionary<string, object?>> layers)
    {
        var sourceId = GetSourceId(layerId);
        return new Dictionary<string, object?>
        {
            ["version"] = 8,
            ["name"] = layerName,
            ["sources"] = new Dictionary<string, object?>
            {
                [sourceId] = new Dictionary<string, object?>
                {
                    ["type"] = "vector",
                    ["tiles"] = new[] { GetTileUrl(layerId) },
                    ["minzoom"] = 0,
                    ["maxzoom"] = 22
                }
            },
            ["layers"] = layers
        };
    }

    internal static Dictionary<string, object?> BuildStyleDocument(
        StyleLayerDescriptor layer,
        List<Dictionary<string, object?>> layers)
        => BuildStyleDocument(layer.Id, layer.Name, layers);

    public static Dictionary<string, object?> BuildDefaultDrawingInfo(StyleLayerDescriptor layer)
        => BuildDefaultDrawingInfo(layer.GeometryType);

    public static Dictionary<string, object?> BuildDefaultDrawingInfo(MetadataV2GeometryType geometryType)
    {
        var symbol = BuildDefaultGeoServicesSymbol(geometryType);
        return new Dictionary<string, object?>
        {
            ["renderer"] = new Dictionary<string, object?>
            {
                ["type"] = "simple",
                ["symbol"] = symbol
            }
        };
    }

    /// <summary>
    /// Builds a GeoServices symbol matching the updated MapLibre default styling.
    /// Bakes MapLibre opacity into the GeoServices RGBA alpha channel so the two
    /// format defaults stay visually aligned.
    /// </summary>
    internal static Dictionary<string, object?> BuildDefaultGeoServicesSymbol(MetadataV2GeometryType geometryType)
    {
        // #2D69A5 = RGB(45, 105, 165)
        return geometryType switch
        {
            // MapLibre: circle-color=#2D69A5, radius=4 (→ size 8), stroke=#FFFFFF, opacity=0.85
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint =>
                GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 217),
                    new StyleColor(255, 255, 255, 255),
                    1d, 8d),

            // MapLibre: line-color=#2D69A5, width=2, opacity=0.9
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString =>
                GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 230),
                    null, 2d, null),

            // MapLibre: fill=#2D69A5/0.4, outline=#1A4D80/0.8, width=0.75
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon or
                MetadataV2GeometryType.GeometryCollection or MetadataV2GeometryType.Mixed =>
                GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 102),
                    new StyleColor(26, 77, 128, 204),
                    0.75, null),

            // Unrecognised geometry type — use polygon defaults as the safest catch-all
            // (matches BuildDefaultLayers, which groups GeometryCollection with polygon).
            _ => GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 102),
                    new StyleColor(26, 77, 128, 204),
                    0.75, null)
        };
    }

    internal static List<Dictionary<string, object?>> BuildDefaultLayers(
        int layerId,
        MetadataV2GeometryType geometryType,
        string sourceId)
    {
        var layers = new List<Dictionary<string, object?>>();

        switch (geometryType)
        {
            case MetadataV2GeometryType.Point:
            case MetadataV2GeometryType.MultiPoint:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layerId}-circle",
                    ["type"] = "circle",
                    ["source"] = sourceId,
                    ["source-layer"] = SourceLayerName,
                    ["paint"] = new Dictionary<string, object?>
                    {
                        ["circle-color"] = "#2D69A5",
                        ["circle-radius"] = 4d,
                        ["circle-stroke-color"] = "#FFFFFF",
                        ["circle-stroke-width"] = DefaultOutlineWidth,
                        ["circle-opacity"] = 0.85
                    }
                });
                break;
            case MetadataV2GeometryType.LineString:
            case MetadataV2GeometryType.MultiLineString:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layerId}-line",
                    ["type"] = "line",
                    ["source"] = sourceId,
                    ["source-layer"] = SourceLayerName,
                    ["paint"] = new Dictionary<string, object?>
                    {
                        ["line-color"] = "#2D69A5",
                        ["line-width"] = DefaultLineWidth,
                        ["line-opacity"] = 0.9
                    }
                });
                break;
            case MetadataV2GeometryType.Polygon:
            case MetadataV2GeometryType.MultiPolygon:
            case MetadataV2GeometryType.GeometryCollection:
            case MetadataV2GeometryType.Mixed:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layerId}-fill",
                    ["type"] = "fill",
                    ["source"] = sourceId,
                    ["source-layer"] = SourceLayerName,
                    ["paint"] = new Dictionary<string, object?>
                    {
                        ["fill-color"] = "#2D69A5",
                        ["fill-opacity"] = 0.4
                    }
                });
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layerId}-outline",
                    ["type"] = "line",
                    ["source"] = sourceId,
                    ["source-layer"] = SourceLayerName,
                    ["paint"] = new Dictionary<string, object?>
                    {
                        ["line-color"] = "#1A4D80",
                        ["line-width"] = 0.75,
                        ["line-opacity"] = 0.8
                    }
                });
                break;
        }

        return layers;
    }
}
