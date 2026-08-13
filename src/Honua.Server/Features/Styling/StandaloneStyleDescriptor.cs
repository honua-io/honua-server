// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using GeometryType = Honua.Core.Features.Metadata.Domain.V2.MetadataV2GeometryType;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Builds a <see cref="StyleLayerDescriptor"/> for a standalone (styleId-keyed) catalog
/// style (ADR-0048, Phase 2). Phase 1 styles borrow the descriptor from the owning
/// metadata-v2 resource, but a standalone style is decoupled from any layer, so the
/// geometry type and storage-layer id have to be read back out of the style documents
/// themselves. Both style ↔ drawingInfo converters are geometry-driven, so without this
/// the standalone encodings degrade to the "no geometry" default document.
/// </summary>
internal static class StandaloneStyleDescriptor
{
    /// <summary>
    /// Derives a descriptor from a canonical MapLibre style document: the storage-layer
    /// id from its Honua tile source (<c>layer-{id}</c>), the geometry type from the
    /// first symbolizing layer's type.
    /// </summary>
    /// <param name="styleId">Stable style identifier, used as the descriptor name.</param>
    /// <param name="mapLibreStyleJson">Canonical MapLibre style JSON.</param>
    /// <returns>A descriptor usable by the style converters.</returns>
    public static StyleLayerDescriptor FromMapLibre(string styleId, string mapLibreStyleJson)
    {
        var layerId = 0;
        var geometryType = GeometryType.None;

        try
        {
            using var document = JsonDocument.Parse(mapLibreStyleJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                layerId = ReadStorageLayerId(root);
                geometryType = ReadGeometryType(root);
            }
        }
        catch (JsonException)
        {
            // A malformed document cannot describe a layer; the converters fall back to
            // their default documents for GeometryType.None.
        }

        return new StyleLayerDescriptor(layerId, styleId, geometryType);
    }

    /// <summary>
    /// Infers the geometry type a GeoServices <c>drawingInfo</c> renderer symbolizes, from
    /// the Esri symbol type of its simple symbol or of its first unique-value / class-break
    /// info.
    /// </summary>
    /// <param name="drawingInfo">Parsed <c>drawingInfo</c> document.</param>
    /// <returns>The inferred geometry type, or <see cref="GeometryType.None"/> when unknown.</returns>
    public static GeometryType InferGeometryType(JsonElement drawingInfo)
    {
        if (drawingInfo.ValueKind != JsonValueKind.Object
            || !drawingInfo.TryGetProperty("renderer", out var renderer)
            || renderer.ValueKind != JsonValueKind.Object)
        {
            return GeometryType.None;
        }

        if (TryReadSymbolGeometry(renderer, "symbol", out var geometryType))
        {
            return geometryType;
        }

        foreach (var infosProperty in new[] { "uniqueValueInfos", "classBreakInfos" })
        {
            if (!renderer.TryGetProperty(infosProperty, out var infos) || infos.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var inferredGeometryType = infos.EnumerateArray()
                .Where(info => info.ValueKind == JsonValueKind.Object)
                .Select(info => TryReadSymbolGeometry(info, "symbol", out var inferred)
                    ? inferred
                    : GeometryType.None)
                .FirstOrDefault(candidate => candidate != GeometryType.None);
            if (inferredGeometryType != GeometryType.None)
            {
                return inferredGeometryType;
            }
        }

        return GeometryType.None;
    }

    private static bool TryReadSymbolGeometry(JsonElement owner, string propertyName, out GeometryType geometryType)
    {
        geometryType = GeometryType.None;

        if (!owner.TryGetProperty(propertyName, out var symbol)
            || symbol.ValueKind != JsonValueKind.Object
            || !symbol.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        geometryType = typeElement.GetString() switch
        {
            "esriSMS" or "esriPMS" => GeometryType.Point,
            "esriSLS" => GeometryType.LineString,
            "esriSFS" or "esriPFS" => GeometryType.Polygon,
            _ => GeometryType.None
        };

        return geometryType != GeometryType.None;
    }

    private static GeometryType ReadGeometryType(JsonElement root)
    {
        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            return GeometryType.None;
        }

        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object
                || !layer.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // First symbolizing layer wins: the default documents emit the primary
            // symbolizer first and any outline/label layer after it.
            var geometryType = typeElement.GetString() switch
            {
                "fill" or "fill-extrusion" => GeometryType.Polygon,
                "line" => GeometryType.LineString,
                "circle" or "symbol" or "heatmap" => GeometryType.Point,
                _ => GeometryType.None
            };

            if (geometryType != GeometryType.None)
            {
                return geometryType;
            }
        }

        return GeometryType.None;
    }

    private static int ReadStorageLayerId(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var name in sources.EnumerateObject().Select(source => source.Name))
        {
            if (!name.StartsWith("layer-", StringComparison.Ordinal))
            {
                continue;
            }

            if (int.TryParse(
                    name.AsSpan("layer-".Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var layerId))
            {
                return layerId;
            }
        }

        return 0;
    }
}
