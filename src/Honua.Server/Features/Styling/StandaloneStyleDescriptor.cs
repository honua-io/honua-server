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
        int? layerId = null;
        var geometryType = GeometryType.None;

        try
        {
            using var document = JsonDocument.Parse(mapLibreStyleJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var (symbolizingGeometryType, sourceName) = ReadSymbolizingLayer(root);
                geometryType = symbolizingGeometryType;
                layerId = ResolveStorageLayerId(root, sourceName);
            }
        }
        catch (JsonException)
        {
            // A malformed document cannot describe a layer; the converters fall back to
            // their default documents for GeometryType.None.
        }

        return new StyleLayerDescriptor(
            layerId ?? 0,
            styleId,
            geometryType,
            IsBoundToStorageLayer: layerId.HasValue);
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

    /// <summary>
    /// Finds the layer whose symbology defines the style, returning both the geometry type
    /// it implies and the name of the source it draws from. The source travels with the
    /// geometry type because the conversion has to be bound to the layer that was actually
    /// selected, not to whichever source happens to be declared first.
    /// </summary>
    private static (GeometryType GeometryType, string? SourceName) ReadSymbolizingLayer(JsonElement root)
    {
        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            return (GeometryType.None, null);
        }

        var fallbackGeometryType = GeometryType.None;
        string? fallbackSourceName = null;

        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.ValueKind != JsonValueKind.Object
                || !layer.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var layerType = typeElement.GetString();
            var geometryType = layerType switch
            {
                "fill" or "fill-extrusion" => GeometryType.Polygon,
                "line" => GeometryType.LineString,
                "circle" or "heatmap" => GeometryType.Point,
                "symbol" => GeometryType.Point,
                _ => GeometryType.None
            };

            var sourceName = layer.TryGetProperty("source", out var sourceElement)
                && sourceElement.ValueKind == JsonValueKind.String
                    ? sourceElement.GetString()
                    : null;

            // A symbol layer is commonly a label overlay for a concrete fill/line/circle
            // layer later in the document. Remember it only as a fallback so labels do not
            // misclassify polygon or line styles as points.
            if (string.Equals(layerType, "symbol", StringComparison.Ordinal))
            {
                if (fallbackGeometryType == GeometryType.None)
                {
                    fallbackGeometryType = geometryType;
                    fallbackSourceName = sourceName;
                }
            }
            else if (geometryType != GeometryType.None)
            {
                return (geometryType, sourceName);
            }
        }

        return (fallbackGeometryType, fallbackSourceName);
    }

    /// <summary>
    /// Resolves the storage-layer id the conversion must be bound to, preferring the source
    /// the symbolizing layer actually references.
    /// </summary>
    private static int? ResolveStorageLayerId(JsonElement root, string? sourceName)
    {
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Bind to the source the selected layer draws from. With several layer-* sources
        // present, declaration order says nothing about which one the style symbolizes, and
        // picking the wrong one silently repoints the rebuilt canonical document (and its
        // tile URL) at an unrelated data layer.
        if (!string.IsNullOrEmpty(sourceName))
        {
            return sources.TryGetProperty(sourceName, out var source)
                && TryResolveHonuaSource(sourceName, source, out var boundLayerId)
                    ? boundLayerId
                    : null;
        }

        // The symbolizing layer named no source, so fall back to the document's own binding —
        // but only while it is unambiguous. Several distinct layer-* sources
        // with nothing selecting between them is a genuine ambiguity: report "no layer"
        // rather than guessing, so the converters use their geometry-only default instead of
        // rebuilding the style against an arbitrary layer.
        int? resolved = null;
        foreach (var source in sources.EnumerateObject())
        {
            if (!TryResolveHonuaSource(source.Name, source.Value, out var candidate))
            {
                continue;
            }

            if (resolved.HasValue && resolved.Value != candidate)
            {
                return null;
            }

            resolved = candidate;
        }

        return resolved;
    }

    private static bool TryParseStorageLayerId(string sourceName, out int layerId)
    {
        layerId = 0;
        return sourceName.StartsWith("layer-", StringComparison.Ordinal)
            && int.TryParse(
                sourceName.AsSpan("layer-".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out layerId)
            && string.Equals(sourceName, StyleDefaults.GetSourceId(layerId), StringComparison.Ordinal);
    }

    private static bool TryResolveHonuaSource(
        string sourceName,
        JsonElement source,
        out int layerId)
    {
        if (!TryParseStorageLayerId(sourceName, out layerId)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "vector", StringComparison.Ordinal)
            || !source.TryGetProperty("tiles", out var tiles)
            || tiles.ValueKind != JsonValueKind.Array
            || tiles.GetArrayLength() != 1)
        {
            return false;
        }

        var tile = tiles[0];
        return tile.ValueKind == JsonValueKind.String
            && string.Equals(tile.GetString(), StyleDefaults.GetTileUrl(layerId), StringComparison.Ordinal);
    }
}
