// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// Classifies how <c>honua_render_map</c> renders a layer's applied style, so the
/// tool reports a typed outcome instead of silently drawing something else. The
/// shared Skia vector rasterizer draws MapLibre <c>fill</c>, <c>line</c>, and
/// <c>circle</c> layers (colour, width, opacity, stroke, dash, filters, zoom ranges
/// and data-driven expressions); every other construct is reported as an
/// <see cref="UnsupportedStyleConstruct"/> and is not drawn.
/// </summary>
internal static class AppliedStyleRenderSupport
{
    /// <summary>The layer rendered with its applied catalog style; every style layer was drawable.</summary>
    public const string Applied = "applied";

    /// <summary>No catalog style is applied; the layer rendered with its stored default style.</summary>
    public const string Default = "default";

    /// <summary>
    /// The applied style contains constructs the server rasterizer cannot draw. The drawable
    /// style layers render; the listed constructs do not, and the stored default is not
    /// substituted for them.
    /// </summary>
    public const string UnsupportedStyleConstruct = "unsupported-style-construct";

    private static readonly string[] PointLayerTypes = ["circle"];
    private static readonly string[] LineLayerTypes = ["line"];
    private static readonly string[] PolygonLayerTypes = ["fill", "line"];

    /// <summary>
    /// Returns the constructs of <paramref name="mapLibreStyleJson"/> that the server
    /// rasterizer cannot draw for a layer of <paramref name="geometryType"/>. An empty
    /// list means the whole style renders.
    /// </summary>
    public static IReadOnlyList<string> FindUnsupportedConstructs(
        string? mapLibreStyleJson,
        MetadataV2GeometryType geometryType)
    {
        if (string.IsNullOrWhiteSpace(mapLibreStyleJson))
        {
            return ["the applied style has no MapLibre style document"];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(mapLibreStyleJson);
        }
        catch (JsonException)
        {
            return ["the applied style is not a valid MapLibre style document"];
        }

        using (document)
        {
            var layers = ReadStyleLayers(document.RootElement);
            if (layers.Count == 0)
            {
                return ["the applied style has no style layers"];
            }

            var drawable = DrawableLayerTypes(geometryType);
            var unsupported = new List<string>();
            foreach (var layer in layers)
            {
                var id = layer.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
                var type = layer.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                var label = id is null ? "style layer" : $"style layer '{id}'";
                if (type is null)
                {
                    unsupported.Add($"{label} has no type");
                }
                else if (!IsRasterizableType(type))
                {
                    unsupported.Add($"{label} type '{type}' is not rasterized by the server renderer (supported: fill, line, circle)");
                }
                else if (drawable is not null && Array.IndexOf(drawable, type) < 0)
                {
                    unsupported.Add($"{label} type '{type}' does not draw {geometryType.ToString().ToLowerInvariant()} geometry");
                }
            }

            return unsupported;
        }
    }

    private static bool IsRasterizableType(string type)
        => type is "fill" or "line" or "circle";

    private static string[]? DrawableLayerTypes(MetadataV2GeometryType geometryType) => geometryType switch
    {
        MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => PointLayerTypes,
        MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => LineLayerTypes,
        MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => PolygonLayerTypes,
        _ => null,
    };

    // Mirrors the renderer's StyleTranslator.ParseStyleLayers: a layer array, a style
    // document with a non-empty "layers" array, or a single style layer object.
    private static List<JsonElement> ReadStyleLayers(JsonElement root)
    {
        var layers = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            AddObjects(root, layers);
            return layers;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return layers;
        }

        if (root.TryGetProperty("layers", out var layersElement) &&
            layersElement.ValueKind == JsonValueKind.Array &&
            layersElement.GetArrayLength() > 0)
        {
            AddObjects(layersElement, layers);
            return layers;
        }

        if (root.TryGetProperty("type", out _))
        {
            layers.Add(root);
        }

        return layers;
    }

    private static void AddObjects(JsonElement array, List<JsonElement> layers)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                layers.Add(item);
            }
        }
    }
}
