// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class StyleDefaults
{
    public static readonly int[] DefaultStrokeColor = [45, 105, 165, 255];

    public const double DefaultLineWidth = 2d;
    public const double DefaultOutlineWidth = 1d;
    /// <summary>Conversion fallback when an existing style omits point size; distinct from default style radius (4d) and GeoServices size (8d).</summary>
    public const double DefaultPointSize = 6d;

    public const string SourceLayerName = "layer";

    public static string GetSourceId(LayerDefinition layer) => $"layer-{layer.Id}";

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

    public static Dictionary<string, object?> BuildDefaultMapLibreStyle(LayerDefinition layer)
    {
        var sourceId = GetSourceId(layer);
        var layers = BuildDefaultLayers(layer, sourceId);
        return BuildStyleDocument(layer, layers);
    }

    /// <summary>
    /// Wraps a layer list in a complete MapLibre v8 style document with vector tile source.
    /// </summary>
    internal static Dictionary<string, object?> BuildStyleDocument(
        LayerDefinition layer, List<Dictionary<string, object?>> layers)
    {
        var sourceId = GetSourceId(layer);
        return new Dictionary<string, object?>
        {
            ["version"] = 8,
            ["name"] = layer.Name,
            ["sources"] = new Dictionary<string, object?>
            {
                [sourceId] = new Dictionary<string, object?>
                {
                    ["type"] = "vector",
                    ["tiles"] = new[] { GetTileUrl(layer.Id) },
                    ["minzoom"] = 0,
                    ["maxzoom"] = 22
                }
            },
            ["layers"] = layers
        };
    }

    public static Dictionary<string, object?> BuildDefaultDrawingInfo(LayerDefinition layer)
    {
        var symbol = BuildDefaultGeoServicesSymbol(layer.GeometryType);
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
    internal static Dictionary<string, object?> BuildDefaultGeoServicesSymbol(GeometryType geometryType)
    {
        // #2D69A5 = RGB(45, 105, 165)
        return geometryType switch
        {
            // MapLibre: circle-color=#2D69A5, radius=4 (→ size 8), stroke=#FFFFFF, opacity=0.85
            GeometryType.Point or GeometryType.MultiPoint =>
                GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 217),
                    new StyleColor(255, 255, 255, 255),
                    1d, 8d),

            // MapLibre: line-color=#2D69A5, width=2, opacity=0.9
            GeometryType.LineString or GeometryType.MultiLineString =>
                GeoServicesStyleBuilder.BuildSymbol(geometryType,
                    new StyleColor(45, 105, 165, 230),
                    null, 2d, null),

            // MapLibre: fill=#2D69A5/0.4, outline=#1A4D80/0.8, width=0.75
            GeometryType.Polygon or GeometryType.MultiPolygon or GeometryType.GeometryCollection =>
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

    internal static List<Dictionary<string, object?>> BuildDefaultLayers(LayerDefinition layer, string sourceId)
    {
        var layers = new List<Dictionary<string, object?>>();

        switch (layer.GeometryType)
        {
            case GeometryType.Point:
            case GeometryType.MultiPoint:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layer.Id}-circle",
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
            case GeometryType.LineString:
            case GeometryType.MultiLineString:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layer.Id}-line",
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
            case GeometryType.Polygon:
            case GeometryType.MultiPolygon:
            case GeometryType.GeometryCollection:
                layers.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"layer-{layer.Id}-fill",
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
                    ["id"] = $"layer-{layer.Id}-outline",
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
