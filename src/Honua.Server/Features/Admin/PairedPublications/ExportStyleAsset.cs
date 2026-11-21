// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Server.Features.Admin.PairedPublications;

// A MapLibre style for the export's PMTiles file, made from the layer's stored style. Every layer
// is bound to one pmtiles:// source next to the style, so the file works wherever the version is copied.
// Symbol layers are dropped: the export ships no sprite or glyphs, and a symbol layer without them
// fails to render.
internal static class ExportStyleAsset
{
    internal const string SourceId = "export";
    private const string Color = "#2D69A5";
    private const string OutlineColor = "#1A4D80";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Render(ExportCompanionInput input)
    {
        var descriptor = input.Descriptor;
        var vectorLayer = descriptor.Artifacts.Pmtiles.VectorLayer;
        var layers = PublicStoredLayers(input.StoredMapLibreStyle, [.. input.PublicFields.Select(field => field.Name)])
            ?? DefaultLayers(descriptor.Geometry.Types);
        // Layer ids and metadata are author text that may name any field, so neither is copied.
        foreach (var (layer, index) in layers.OfType<JsonObject>().Select((layer, index) => (layer, index)))
        {
            layer["id"] = $"{SourceId}-{index}";
            layer.Remove("metadata");
            layer["source"] = SourceId;
            layer["source-layer"] = vectorLayer;
        }

        var source = new JsonObject
        {
            ["type"] = "vector",
            ["url"] = $"pmtiles://{descriptor.Artifacts.Pmtiles.Href}",
            ["minzoom"] = descriptor.Artifacts.Pmtiles.Minzoom,
            ["maxzoom"] = descriptor.Artifacts.Pmtiles.Maxzoom
        };
        if (!string.IsNullOrWhiteSpace(input.Metadata.Attribution))
        {
            source["attribution"] = input.Metadata.Attribution.Trim();
        }

        var style = new JsonObject
        {
            ["version"] = 8,
            ["name"] = input.DisplayTitle,
            ["sources"] = new JsonObject { [SourceId] = source },
            ["layers"] = layers
        };
        return style.ToJsonString(Indented) + "\n";
    }

    // The stored style's drawable layers that read only public fields, or null when none remain.
    // A layer whose filter, paint or layout names any other property is dropped: it would publish
    // a hidden field's name and draw nothing, since the tiles do not carry that field.
    internal static JsonArray? PublicStoredLayers(string? stored, IReadOnlyList<string> publicFields)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            var layers = JsonNode.Parse(stored) switch
            {
                JsonObject root => root["layers"] as JsonArray,
                JsonArray bare => bare,
                _ => null
            };
            var kept = (layers ?? []).OfType<JsonObject>()
                .Where(layer => IsDrawable(layer) && StyleFieldReferences.Of(layer).All(name => publicFields.Contains(name)))
                .Select(layer => (JsonNode)layer.DeepClone())
                .ToArray();
            return kept.Length == 0 ? null : new JsonArray(kept);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // An unparseable stored style must not fail the export; the default draws the data.
            return null;
        }
    }

    // The public stored layers as a style document for the thumbnail renderer, or null for its defaults.
    public static string? PublicStoredStyle(string? stored, IReadOnlyList<string> publicFields)
        => PublicStoredLayers(stored, publicFields) is { } layers ? new JsonObject { ["layers"] = layers }.ToJsonString() : null;

    private static bool IsDrawable(JsonObject layer)
        => layer["type"] is JsonValue type && type.TryGetValue(out string? value)
            && value is not ("symbol" or "background" or "raster" or "hillshade");

    private static JsonArray DefaultLayers(IReadOnlyList<string> geometryTypes)
    {
        var layers = new JsonArray();
        if (geometryTypes.Count == 0 || geometryTypes.Any(type => type.Contains("Polygon", StringComparison.OrdinalIgnoreCase)))
        {
            layers.Add(Layer("fill", new JsonObject { ["fill-color"] = Color, ["fill-opacity"] = 0.4 }));
            layers.Add(Layer("line", new JsonObject { ["line-color"] = OutlineColor, ["line-width"] = 0.75 }));
        }

        if (geometryTypes.Count == 0 || geometryTypes.Any(type => type.Contains("Line", StringComparison.OrdinalIgnoreCase)))
        {
            layers.Add(Layer("line", new JsonObject { ["line-color"] = Color, ["line-width"] = 2 }));
        }

        if (geometryTypes.Count == 0 || geometryTypes.Any(type => type.Contains("Point", StringComparison.OrdinalIgnoreCase)))
        {
            layers.Add(Layer("circle", new JsonObject
            {
                ["circle-color"] = Color,
                ["circle-radius"] = 4,
                ["circle-stroke-color"] = "#FFFFFF",
                ["circle-stroke-width"] = 1
            }));
        }

        return layers;
    }

    private static JsonObject Layer(string type, JsonObject paint) => new() { ["id"] = SourceId, ["type"] = type, ["paint"] = paint };
}
