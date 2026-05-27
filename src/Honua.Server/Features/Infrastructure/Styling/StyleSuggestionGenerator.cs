// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Generates MapLibre v8 style documents and GeoServices drawingInfo from style suggestions.
/// </summary>
internal static class StyleSuggestionGenerator
{
    /// <summary>
    /// Fallback color for features that do not match any classified category or break.
    /// Must stay in sync with the MapLibre match/step fallback emitted by <see cref="BuildColorExpression"/>.
    /// </summary>
    private const string FallbackColor = "#CCCCCC";
    /// <summary>
    /// Generates a complete MapLibre style document from a style suggestion.
    /// </summary>
    public static Dictionary<string, object?> GenerateMapLibreStyle(
        MetadataV2Resource resource,
        int storageLayerId,
        StyleSuggestion suggestion)
    {
        var sourceId = StyleDefaults.GetSourceId(storageLayerId);
        var layers = GenerateStyledLayers(resource.ReadGeometryType(), storageLayerId, sourceId, suggestion);
        return StyleDefaults.BuildStyleDocument(storageLayerId, resource.Metadata.Name, layers);
    }

    /// <summary>
    /// Generates a complete MapLibre style document from a style suggestion.
    /// </summary>
    public static Dictionary<string, object?> GenerateMapLibreStyle(
        StyleLayerDescriptor layer,
        StyleSuggestion suggestion)
    {
        var sourceId = StyleDefaults.GetSourceId(layer);
        var layers = GenerateStyledLayers(layer.GeometryType, layer.Id, sourceId, suggestion);
        return StyleDefaults.BuildStyleDocument(layer, layers);
    }

    /// <summary>
    /// Generates GeoServices drawingInfo from a style suggestion.
    /// </summary>
    public static Dictionary<string, object?> GenerateDrawingInfo(
        MetadataV2Resource resource,
        StyleSuggestion suggestion)
        => GenerateDrawingInfo(resource.ReadGeometryType(), suggestion);

    /// <summary>
    /// Generates GeoServices drawingInfo from a style suggestion.
    /// </summary>
    public static Dictionary<string, object?> GenerateDrawingInfo(
        StyleLayerDescriptor layer,
        StyleSuggestion suggestion)
        => GenerateDrawingInfo(layer.GeometryType, suggestion);

    private static Dictionary<string, object?> GenerateDrawingInfo(
        MetadataV2GeometryType geometryType,
        StyleSuggestion suggestion)
    {
        if (suggestion.Classification == null)
            return GenerateEnhancedDrawingInfo(geometryType);

        if (suggestion.Classification.Method == ClassificationMethod.UniqueValue)
            return BuildUniqueValueDrawingInfo(geometryType, suggestion);

        return BuildClassBreaksDrawingInfo(geometryType, suggestion);
    }

    /// <summary>
    /// Generates a GeoServices drawingInfo that matches the enhanced MapLibre defaults.
    /// Used for geometry-only suggestions (Community tier or no suitable classification field).
    /// </summary>
    public static Dictionary<string, object?> GenerateEnhancedDrawingInfo(MetadataV2Resource resource)
        => GenerateEnhancedDrawingInfo(resource.ReadGeometryType());

    /// <summary>
    /// Generates a GeoServices drawingInfo that matches the enhanced MapLibre defaults.
    /// </summary>
    public static Dictionary<string, object?> GenerateEnhancedDrawingInfo(StyleLayerDescriptor layer)
        => GenerateEnhancedDrawingInfo(layer.GeometryType);

    private static Dictionary<string, object?> GenerateEnhancedDrawingInfo(MetadataV2GeometryType geometryType)
        => StyleDefaults.BuildDefaultDrawingInfo(geometryType);

    /// <summary>
    /// Generates enhanced geometry-only defaults (Community tier).
    /// </summary>
    public static Dictionary<string, object?> GenerateEnhancedDefaults(
        MetadataV2Resource resource,
        int storageLayerId)
    {
        var sourceId = StyleDefaults.GetSourceId(storageLayerId);
        var layers = BuildEnhancedDefaultLayers(resource.ReadGeometryType(), storageLayerId, sourceId);
        return StyleDefaults.BuildStyleDocument(storageLayerId, resource.Metadata.Name, layers);
    }

    /// <summary>
    /// Generates enhanced geometry-only defaults (Community tier).
    /// </summary>
    public static Dictionary<string, object?> GenerateEnhancedDefaults(StyleLayerDescriptor layer)
    {
        var sourceId = StyleDefaults.GetSourceId(layer);
        var layers = BuildEnhancedDefaultLayers(layer.GeometryType, layer.Id, sourceId);
        return StyleDefaults.BuildStyleDocument(layer, layers);
    }

    private static List<Dictionary<string, object?>> GenerateStyledLayers(
        MetadataV2GeometryType geometryType,
        int storageLayerId,
        string sourceId,
        StyleSuggestion suggestion)
    {
        if (suggestion.Classification == null)
            return BuildEnhancedDefaultLayers(geometryType, storageLayerId, sourceId);

        var colorExpression = BuildColorExpression(suggestion);

        return geometryType switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint =>
                BuildStyledPointLayers(storageLayerId, sourceId, colorExpression),
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString =>
                BuildStyledLineLayers(storageLayerId, sourceId, colorExpression),
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon or
                MetadataV2GeometryType.GeometryCollection or MetadataV2GeometryType.Mixed =>
                BuildStyledPolygonLayers(storageLayerId, sourceId, colorExpression),
            _ => BuildEnhancedDefaultLayers(geometryType, storageLayerId, sourceId)
        };
    }

    private static object BuildColorExpression(StyleSuggestion suggestion)
    {
        var classification = suggestion.Classification!;
        var colors = suggestion.PaletteColors;

        if (classification.Method == ClassificationMethod.UniqueValue && classification.Categories != null)
        {
            // ["case", guard, ["match", ["to-string", ["get", fieldName]], value1, color1, ..., fallbackColor], fallbackColor]
            // Categories are always strings (from JSONB ->> text extraction), so coerce the
            // tile value to string to avoid type-mismatch when ST_AsMVT preserves native types.
            // The non-null guard routes missing/null features to the fallback, preventing
            // to-string(null) → "" from matching an empty-string category.
            var matchExpr = new List<object>
            {
                "match",
                new object[] { "to-string", new object[] { "get", classification.FieldName } }
            };

            for (var i = 0; i < classification.Categories.Length && i < colors.Length; i++)
            {
                matchExpr.Add(classification.Categories[i]);
                matchExpr.Add(colors[i]);
            }

            // Fallback color (gray) — must match the GeoServices defaultSymbol color
            matchExpr.Add(FallbackColor);
            return new object[]
            {
                "case",
                StyleDefaults.BuildNonNullFieldGuard(classification.FieldName),
                matchExpr.ToArray(),
                FallbackColor
            };
        }

        if (classification.Breaks != null)
        {
            // ["step", ["to-number", ["get", fieldName]], baseColor, break1, color1, ...]
            // Breaks are numeric doubles; coerce the tile value to number to handle
            // tile encodings that may stringify JSONB values.
            var stepExpr = new List<object>
            {
                "step",
                new object[] { "to-number", new object[] { "get", classification.FieldName } },
                colors.Length > 0 ? colors[0] : FallbackColor
            };

            for (var i = 0; i < classification.Breaks.Length && i + 1 < colors.Length; i++)
            {
                stepExpr.Add(classification.Breaks[i]);
                stepExpr.Add(colors[i + 1]);
            }

            // Guard null/missing AND non-castable text values: to-number(null) → 0
            // and to-number("N/A") → 0 would silently route features into the first
            // color bucket.  The typeof guard rejects strings so only native numbers
            // reach the step; missing/null features are caught by the has check.
            return new object[]
            {
                "case",
                StyleDefaults.BuildNumericFieldGuard(classification.FieldName),
                stepExpr.ToArray(),
                FallbackColor
            };
        }

        return "#2D69A5";
    }

    private static List<Dictionary<string, object?>> BuildStyledPointLayers(
        int storageLayerId, string sourceId, object colorExpression)
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["id"] = $"layer-{storageLayerId}-circle",
                ["type"] = "circle",
                ["source"] = sourceId,
                ["source-layer"] = StyleDefaults.SourceLayerName,
                ["paint"] = new Dictionary<string, object?>
                {
                    ["circle-color"] = colorExpression,
                    ["circle-radius"] = 4d,
                    ["circle-stroke-color"] = "#FFFFFF",
                    ["circle-stroke-width"] = 1d,
                    ["circle-opacity"] = 0.85
                }
            }
        ];
    }

    private static List<Dictionary<string, object?>> BuildStyledLineLayers(
        int storageLayerId, string sourceId, object colorExpression)
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["id"] = $"layer-{storageLayerId}-line",
                ["type"] = "line",
                ["source"] = sourceId,
                ["source-layer"] = StyleDefaults.SourceLayerName,
                ["paint"] = new Dictionary<string, object?>
                {
                    ["line-color"] = colorExpression,
                    ["line-width"] = 2d,
                    ["line-opacity"] = 0.9
                }
            }
        ];
    }

    private static List<Dictionary<string, object?>> BuildStyledPolygonLayers(
        int storageLayerId, string sourceId, object colorExpression)
    {
        return
        [
            new Dictionary<string, object?>
            {
                ["id"] = $"layer-{storageLayerId}-fill",
                ["type"] = "fill",
                ["source"] = sourceId,
                ["source-layer"] = StyleDefaults.SourceLayerName,
                ["paint"] = new Dictionary<string, object?>
                {
                    ["fill-color"] = colorExpression,
                    ["fill-opacity"] = 0.6
                }
            },
            new Dictionary<string, object?>
            {
                ["id"] = $"layer-{storageLayerId}-outline",
                ["type"] = "line",
                ["source"] = sourceId,
                ["source-layer"] = StyleDefaults.SourceLayerName,
                ["paint"] = new Dictionary<string, object?>
                {
                    ["line-color"] = "#333333",
                    ["line-width"] = 0.5,
                    ["line-opacity"] = 0.8
                }
            }
        ];
    }

    private static List<Dictionary<string, object?>> BuildEnhancedDefaultLayers(
        MetadataV2GeometryType geometryType,
        int storageLayerId,
        string sourceId)
    {
        // Use the standard default layers so that the MapLibre style and
        // GeoServices drawingInfo returned by the suggestion endpoint
        // describe identical rendering.  Zoom-dependent expressions would
        // diverge from drawingInfo which can only express static widths.
        return StyleDefaults.BuildDefaultLayers(storageLayerId, geometryType, sourceId);
    }

    private static Dictionary<string, object?> BuildUniqueValueDrawingInfo(
        MetadataV2GeometryType geometryType,
        StyleSuggestion suggestion)
    {
        var classification = suggestion.Classification!;
        var colors = suggestion.PaletteColors;
        var categories = classification.Categories!;
        var (fillAlpha, outlineColor, lineWidth, size) = GetGeometryParams(geometryType);

        var uniqueValueInfos = new List<Dictionary<string, object?>>();
        for (var i = 0; i < categories.Length && i < colors.Length; i++)
        {
            if (!StyleJsonUtilities.TryParseMapLibreColor(colors[i], out var styleColor))
                continue;

            var bakedColor = new StyleColor(styleColor.R, styleColor.G, styleColor.B, fillAlpha);
            uniqueValueInfos.Add(new Dictionary<string, object?>
            {
                ["value"] = categories[i],
                ["label"] = categories[i],
                ["symbol"] = GeoServicesStyleBuilder.BuildSymbol(
                    geometryType,
                    bakedColor,
                    outlineColor,
                    lineWidth,
                    size)
            });
        }

        return new Dictionary<string, object?>
        {
            ["renderer"] = new Dictionary<string, object?>
            {
                ["type"] = "uniqueValue",
                ["field1"] = classification.FieldName,
                ["defaultSymbol"] = BuildFallbackSymbol(geometryType, fillAlpha, outlineColor, lineWidth, size),
                ["defaultLabel"] = "Other",
                ["uniqueValueInfos"] = uniqueValueInfos
            }
        };
    }

    private static Dictionary<string, object?> BuildClassBreaksDrawingInfo(
        MetadataV2GeometryType geometryType,
        StyleSuggestion suggestion)
    {
        var classification = suggestion.Classification!;
        var colors = suggestion.PaletteColors;
        var breaks = classification.Breaks!;
        var (fillAlpha, outlineColor, lineWidth, size) = GetGeometryParams(geometryType);

        // Use actual data range for edge class bounds (GeoServices convention).
        // Profile min/max are always populated for numeric classification paths;
        // fall back to the nearest break if the profile is somehow incomplete.
        var dataMin = suggestion.SuggestedField?.Profile.MinValue ?? breaks[0];
        var dataMax = suggestion.SuggestedField?.Profile.MaxValue ?? breaks[^1];

        var classBreakInfos = new List<Dictionary<string, object?>>();
        for (var i = 0; i <= breaks.Length && i < colors.Length; i++)
        {
            var minValue = i == 0 ? dataMin : breaks[i - 1];
            var maxValue = i < breaks.Length ? breaks[i] : dataMax;

            if (!StyleJsonUtilities.TryParseMapLibreColor(colors[i], out var styleColor))
                continue;

            var bakedColor = new StyleColor(styleColor.R, styleColor.G, styleColor.B, fillAlpha);
            classBreakInfos.Add(new Dictionary<string, object?>
            {
                ["classMinValue"] = minValue,
                ["classMaxValue"] = maxValue,
                ["label"] = i == 0
                    ? $"< {maxValue:F2}"
                    : i == breaks.Length
                        ? $">= {minValue:F2}"
                        : $"{minValue:F2} - {maxValue:F2}",
                ["symbol"] = GeoServicesStyleBuilder.BuildSymbol(
                    geometryType,
                    bakedColor,
                    outlineColor,
                    lineWidth,
                    size)
            });
        }

        return new Dictionary<string, object?>
        {
            ["renderer"] = new Dictionary<string, object?>
            {
                ["type"] = "classBreaks",
                ["field"] = classification.FieldName,
                ["classBreakInfos"] = classBreakInfos,
                ["defaultSymbol"] = BuildFallbackSymbol(geometryType, fillAlpha, outlineColor, lineWidth, size),
                ["defaultLabel"] = "Other"
            }
        };
    }

    /// <summary>
    /// Returns geometry-specific visual parameters that match the MapLibre styled layers,
    /// with MapLibre opacity baked into the GeoServices alpha channel.
    /// </summary>
    private static (byte FillAlpha, StyleColor? OutlineColor, double LineWidth, double? Size) GetGeometryParams(
        MetadataV2GeometryType geometryType)
    {
        return geometryType switch
        {
            // MapLibre: circle-opacity=0.85, circle-stroke-color=#FFFFFF, circle-stroke-width=1, radius=4→size=8
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint =>
                (217, new StyleColor(255, 255, 255, 255), 1d, (double?)8d),
            // MapLibre: line-opacity=0.9, line-width=2
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString =>
                (230, null, 2d, null),
            // MapLibre: fill-opacity=0.6, outline=#333333/0.8, outline-width=0.5
            _ =>
                (153, new StyleColor(51, 51, 51, 204), 0.5, null)
        };
    }

    /// <summary>
    /// Builds a GeoServices symbol using <see cref="FallbackColor"/> so that the
    /// <c>defaultSymbol</c> in drawingInfo matches the MapLibre expression fallback.
    /// </summary>
    private static Dictionary<string, object?> BuildFallbackSymbol(
        MetadataV2GeometryType geometryType, byte fillAlpha, StyleColor? outlineColor,
        double lineWidth, double? size)
    {
        // #CCCCCC = RGB(204, 204, 204)
        var fallback = new StyleColor(204, 204, 204, fillAlpha);
        return GeoServicesStyleBuilder.BuildSymbol(geometryType, fallback, outlineColor, lineWidth, size);
    }
}
