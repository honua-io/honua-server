// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Ai.Protocols.Mcp.MapTools;

// These property sets must track StyleTranslator fill, line, circle, and visibility handling, or Applied claims a rendering the rasterizer did not draw.
internal static class AppliedStyleRenderSupport
{
    public const string Applied = "applied";

    public const string Default = "default";

    // Constructs the rasterizer cannot draw are reported and the stored default style is not substituted for them.
    public const string UnsupportedStyleConstruct = "unsupported-style-construct";

    private static readonly Dictionary<string, HashSet<string>> HonouredPaint = new(StringComparer.Ordinal)
    {
        ["fill"] = new(StringComparer.Ordinal) { "fill-color", "fill-opacity", "fill-outline-color", "fill-antialias" },
        ["line"] = new(StringComparer.Ordinal) { "line-color", "line-width", "line-opacity", "line-dasharray" },
        ["circle"] = new(StringComparer.Ordinal)
        {
            "circle-radius", "circle-color", "circle-opacity",
            "circle-stroke-color", "circle-stroke-opacity", "circle-stroke-width",
        },
    };

    private static readonly Dictionary<string, HashSet<string>> HonouredLayout = new(StringComparer.Ordinal)
    {
        ["fill"] = new(StringComparer.Ordinal) { "visibility" },
        ["line"] = new(StringComparer.Ordinal) { "visibility", "line-cap", "line-join" },
        ["circle"] = new(StringComparer.Ordinal) { "visibility" },
    };

    private static readonly string[] PointLayerTypes = ["circle"];
    private static readonly string[] LineLayerTypes = ["line"];
    private static readonly string[] PolygonLayerTypes = ["fill", "line"];

    public static IReadOnlyList<string> FindUnsupportedConstructs(
        string? mapLibreStyleJson,
        MetadataV2GeometryType geometryType)
    {
        if (string.IsNullOrWhiteSpace(mapLibreStyleJson))
        {
            return ["the applied style has no MapLibre style document; nothing was drawn for this layer"];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(mapLibreStyleJson);
        }
        catch (JsonException)
        {
            return ["the applied style is not a valid MapLibre style document; nothing was drawn for this layer"];
        }

        using (document)
        {
            var layers = ReadStyleLayers(document.RootElement);
            if (layers.Count == 0)
            {
                return ["the applied style has no style layers; nothing was drawn for this layer"];
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
                else
                {
                    AddIgnoredProperties(layer, type, label, unsupported);
                }
            }

            return unsupported;
        }
    }

    private static void AddIgnoredProperties(JsonElement layer, string type, string label, List<string> unsupported)
    {
        var hasPaint = layer.TryGetProperty("paint", out var paint) && paint.ValueKind == JsonValueKind.Object;
        if (!hasPaint || !paint.EnumerateObject().Any())
        {
            // StyleTranslator substitutes its own default symbology for a layer without paint.
            unsupported.Add($"{label} has no paint properties; the server substitutes its default symbology");
        }
        else
        {
            unsupported.AddRange(paint.EnumerateObject()
                .Where(property => !HonouredPaint[type].Contains(property.Name))
                .Select(property => $"{label} paint property '{property.Name}' is not rendered by the server renderer"));
        }

        if (layer.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object)
        {
            unsupported.AddRange(layout.EnumerateObject()
                .Where(property => !HonouredLayout[type].Contains(property.Name))
                .Select(property => $"{label} layout property '{property.Name}' is not rendered by the server renderer"));
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
        layers.AddRange(array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object));
    }
}
