// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Styling;

internal static class StyleDefaults
{
    public static readonly int[] DefaultStrokeColor = [45, 105, 165, 255];
    public static readonly int[] DefaultFillColor = [45, 105, 165, 64];

    public const double DefaultLineWidth = 2d;
    public const double DefaultOutlineWidth = 1d;
    public const double DefaultPointSize = 6d;

    public const string SourceLayerName = "layer";

    public static string GetSourceId(LayerDefinition layer) => $"layer-{layer.Id}";

    public static string GetTileUrl(int layerId) => $"/tiles/{layerId}/{{z}}/{{x}}/{{y}}.mvt";

    public static Dictionary<string, object?> BuildDefaultMapLibreStyle(LayerDefinition layer)
    {
        var sourceId = GetSourceId(layer);
        var layers = BuildDefaultLayers(layer, sourceId);

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
        var symbol = GeoServicesStyleBuilder.BuildDefaultSymbol(layer.GeometryType);
        return new Dictionary<string, object?>
        {
            ["renderer"] = new Dictionary<string, object?>
            {
                ["type"] = "simple",
                ["symbol"] = symbol
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildDefaultLayers(LayerDefinition layer, string sourceId)
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
                        ["circle-radius"] = DefaultPointSize / 2d,
                        ["circle-stroke-color"] = "#2D69A5",
                        ["circle-stroke-width"] = DefaultOutlineWidth
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
                        ["line-width"] = DefaultLineWidth
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
                        ["fill-opacity"] = 0.251
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
                        ["line-color"] = "#2D69A5",
                        ["line-width"] = DefaultOutlineWidth
                    }
                });
                break;
        }

        return layers;
    }
}
