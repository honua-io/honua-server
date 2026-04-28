// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Server.Features.Infrastructure.Rendering;

namespace Honua.Server.Features.Infrastructure.Styling.Sld;

/// <summary>
/// Best-effort export of MapLibre layers to SLD 1.0. Only the symbolizer subset
/// supported by <see cref="SldToMapLibreConverter"/> is reproduced; complex
/// data-driven expressions surface as warnings rather than aborting export.
/// </summary>
internal static class MapLibreToSldConverter
{
    private static readonly XNamespace Sld = SldNamespaces.SldNs;
    private static readonly XNamespace Ogc = SldNamespaces.OgcNs;
    private static readonly XNamespace Xlink = SldNamespaces.XlinkNs;

    /// <summary>
    /// Web Mercator zoom-to-scale-denominator constant. Inverse of the import side.
    /// </summary>
    private const double WebMercatorScaleReference = 559_082_264.028d;

    public static SldExportResult Export(MapLibreStyleLayer[] layers, string layerName)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);

        var diagnostics = new List<SldConversionDiagnostic>();
        var rules = new List<XElement>();

        foreach (var layer in layers)
        {
            var rule = ConvertLayerToRule(layer, diagnostics);
            if (rule != null)
            {
                rules.Add(rule);
            }
        }

        if (rules.Count == 0)
        {
            diagnostics.Add(new SldConversionDiagnostic
            {
                Severity = SldDiagnosticSeverity.Error,
                Construct = "MapLibreLayers",
                Message = "Stored MapLibre style contained no layers convertible to SLD."
            });
        }

        var featureTypeStyle = new XElement(Sld + "FeatureTypeStyle", rules);
        var userStyle = new XElement(Sld + "UserStyle",
            new XElement(Sld + "Name", layerName),
            new XElement(Sld + "Title", layerName),
            featureTypeStyle);
        var namedLayer = new XElement(Sld + "NamedLayer",
            new XElement(Sld + "Name", layerName),
            userStyle);
        var root = new XElement(Sld + "StyledLayerDescriptor",
            new XAttribute("version", "1.0.0"),
            new XAttribute(XNamespace.Xmlns + "ogc", Ogc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xlink", Xlink.NamespaceName),
            namedLayer);
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);

        return new SldExportResult
        {
            SldXml = document.Declaration + Environment.NewLine + document.ToString(),
            Diagnostics = diagnostics.ToArray()
        };
    }

    private static XElement? ConvertLayerToRule(MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(layer.Type))
        {
            // Hidden layer (visibility: none).
            diagnostics.Add(Warn(
                "MapLibreLayer",
                $"Layer '{layer.Id}' has no type or is hidden; skipped.",
                layer.Id));
            return null;
        }

        var rule = new XElement(Sld + "Rule");
        if (!string.IsNullOrEmpty(layer.Id))
        {
            rule.Add(new XElement(Sld + "Name", layer.Id));
        }

        var filterElement = ConvertFilter(layer.Filter, diagnostics, layer.Id);
        if (filterElement != null)
        {
            rule.Add(filterElement);
        }

        if (layer.MaxZoom.HasValue)
        {
            var minScale = ZoomToScale(layer.MaxZoom.Value);
            rule.Add(new XElement(Sld + "MinScaleDenominator", minScale.ToString("R", CultureInfo.InvariantCulture)));
        }

        if (layer.MinZoom.HasValue)
        {
            var maxScale = ZoomToScale(layer.MinZoom.Value);
            rule.Add(new XElement(Sld + "MaxScaleDenominator", maxScale.ToString("R", CultureInfo.InvariantCulture)));
        }

        switch (layer.Type)
        {
            case "circle":
                AddPointSymbolizer(rule, layer, diagnostics);
                return rule;
            case "line":
                AddLineSymbolizer(rule, layer, diagnostics);
                return rule;
            case "fill":
                AddPolygonSymbolizer(rule, layer, diagnostics);
                return rule;
            case "symbol":
                AddSymbolLayer(rule, layer, diagnostics);
                return rule;
            case "background":
                diagnostics.Add(Warn(
                    "BackgroundLayer",
                    "Background layer has no SLD equivalent and was skipped.",
                    layer.Id));
                return null;
            default:
                diagnostics.Add(Warn(
                    "UnsupportedLayerType",
                    $"Layer type '{layer.Type}' is not exported.",
                    layer.Id));
                return null;
        }
    }

    private static void AddPointSymbolizer(XElement rule, MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        var graphic = new XElement(Sld + "Graphic");
        var mark = new XElement(Sld + "Mark",
            new XElement(Sld + "WellKnownName", "circle"));

        var fillColor = TryGetLiteralString(layer, "circle-color", diagnostics);
        var fillOpacity = TryGetLiteralNumber(layer, "circle-opacity", diagnostics);
        if (fillColor != null || fillOpacity.HasValue)
        {
            mark.Add(BuildFill(fillColor, fillOpacity));
        }

        var strokeColor = TryGetLiteralString(layer, "circle-stroke-color", diagnostics);
        var strokeOpacity = TryGetLiteralNumber(layer, "circle-stroke-opacity", diagnostics);
        var strokeWidth = TryGetLiteralNumber(layer, "circle-stroke-width", diagnostics);
        if (strokeColor != null || strokeOpacity.HasValue || strokeWidth.HasValue)
        {
            mark.Add(BuildStroke(strokeColor, strokeOpacity, strokeWidth, null, null, null));
        }

        graphic.Add(mark);
        var radius = TryGetLiteralNumber(layer, "circle-radius", diagnostics);
        if (radius.HasValue)
        {
            graphic.Add(new XElement(Sld + "Size", FormatNumber(radius.Value * 2d)));
        }

        rule.Add(new XElement(Sld + "PointSymbolizer", graphic));
    }

    private static void AddLineSymbolizer(XElement rule, MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        var color = TryGetLiteralString(layer, "line-color", diagnostics);
        var opacity = TryGetLiteralNumber(layer, "line-opacity", diagnostics);
        var width = TryGetLiteralNumber(layer, "line-width", diagnostics);
        var dashArray = TryGetLiteralNumberArray(layer, "line-dasharray", diagnostics);
        var lineCap = TryGetLiteralString(layer, "line-cap", diagnostics, isLayout: true);
        var lineJoin = TryGetLiteralString(layer, "line-join", diagnostics, isLayout: true);

        var symbolizer = new XElement(Sld + "LineSymbolizer",
            BuildStroke(color, opacity, width, lineCap, lineJoin, dashArray));
        rule.Add(symbolizer);
    }

    private static void AddPolygonSymbolizer(XElement rule, MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        var fillColor = TryGetLiteralString(layer, "fill-color", diagnostics);
        var fillOpacity = TryGetLiteralNumber(layer, "fill-opacity", diagnostics);
        var outlineColor = TryGetLiteralString(layer, "fill-outline-color", diagnostics);

        var symbolizer = new XElement(Sld + "PolygonSymbolizer",
            BuildFill(fillColor, fillOpacity));

        if (outlineColor != null)
        {
            symbolizer.Add(BuildStroke(outlineColor, null, null, null, null, null));
        }

        rule.Add(symbolizer);
    }

    private static void AddSymbolLayer(XElement rule, MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        var iconImage = TryGetLiteralString(layer, "icon-image", diagnostics, isLayout: true);
        var hasText = layer.Layout?.ContainsKey("text-field") == true
            || layer.Paint?.ContainsKey("text-color") == true;

        if (!string.IsNullOrEmpty(iconImage))
        {
            var graphic = new XElement(Sld + "Graphic",
                new XElement(Sld + "ExternalGraphic",
                    new XElement(Sld + "OnlineResource",
                        new XAttribute(Xlink + "href", iconImage)),
                    new XElement(Sld + "Format", "image/png")));
            var iconSize = TryGetLiteralNumber(layer, "icon-size", diagnostics, isLayout: true);
            if (iconSize.HasValue)
            {
                // MapLibre icon-size is a scale factor (1.0 = native size); SLD Graphic Size is
                // absolute pixels. Without sprite intrinsic dimensions the conversion is lossy,
                // so omit <Size> and emit a diagnostic rather than emit a wrong absolute value.
                diagnostics.Add(Warn(
                    "icon-size",
                    $"MapLibre icon-size ({FormatNumber(iconSize.Value)}) is a scale factor; SLD Graphic Size requires absolute pixels. <Size> omitted from export.",
                    layer.Id));
            }

            rule.Add(new XElement(Sld + "PointSymbolizer", graphic));
        }

        if (hasText)
        {
            var textField = TryGetLiteralString(layer, "text-field", diagnostics, isLayout: true);
            var textColor = TryGetLiteralString(layer, "text-color", diagnostics);
            var textSize = TryGetLiteralNumber(layer, "text-size", diagnostics, isLayout: true);
            var haloColor = TryGetLiteralString(layer, "text-halo-color", diagnostics);
            var haloWidth = TryGetLiteralNumber(layer, "text-halo-width", diagnostics);

            var textSymbolizer = new XElement(Sld + "TextSymbolizer");
            if (!string.IsNullOrEmpty(textField))
            {
                textSymbolizer.Add(BuildLabel(textField));
            }

            if (textSize.HasValue || !string.IsNullOrEmpty(GetFontFamily(layer, diagnostics)))
            {
                var font = new XElement(Sld + "Font");
                var family = GetFontFamily(layer, diagnostics);
                if (!string.IsNullOrEmpty(family))
                {
                    font.Add(BuildCss("font-family", family));
                }

                if (textSize.HasValue)
                {
                    font.Add(BuildCss("font-size", FormatNumber(textSize.Value)));
                }

                textSymbolizer.Add(font);
            }

            if (!string.IsNullOrEmpty(textColor))
            {
                textSymbolizer.Add(BuildFill(textColor, null));
            }

            if (haloWidth.HasValue || !string.IsNullOrEmpty(haloColor))
            {
                var halo = new XElement(Sld + "Halo");
                if (haloWidth.HasValue)
                {
                    halo.Add(new XElement(Sld + "Radius", FormatNumber(haloWidth.Value)));
                }

                if (!string.IsNullOrEmpty(haloColor))
                {
                    halo.Add(BuildFill(haloColor, null));
                }

                textSymbolizer.Add(halo);
            }

            rule.Add(textSymbolizer);
        }

        if (string.IsNullOrEmpty(iconImage) && !hasText)
        {
            diagnostics.Add(Warn(
                "SymbolLayer",
                $"Symbol layer '{layer.Id}' has no icon-image or text-field; nothing to export.",
                layer.Id));
        }
    }

    private static string? GetFontFamily(MapLibreStyleLayer layer, List<SldConversionDiagnostic> diagnostics)
    {
        if (layer.Layout == null || !layer.Layout.TryGetValue("text-font", out var expr))
        {
            return null;
        }

        if (expr.Kind == MapLibreExpressionKind.String)
        {
            return expr.StringValue;
        }

        if (expr.Kind == MapLibreExpressionKind.Array && expr.Items is { Length: > 0 } items)
        {
            var first = items[0];
            if (first.Kind == MapLibreExpressionKind.String)
            {
                return first.StringValue;
            }
        }

        diagnostics.Add(Warn(
            "text-font",
            "text-font expression is not a literal string; SLD font-family omitted.",
            layer.Id));
        return null;
    }

    private static XElement BuildFill(string? color, double? opacity)
    {
        var fill = new XElement(Sld + "Fill");
        if (!string.IsNullOrEmpty(color))
        {
            fill.Add(BuildCss("fill", color));
        }

        if (opacity.HasValue)
        {
            fill.Add(BuildCss("fill-opacity", FormatNumber(opacity.Value)));
        }

        return fill;
    }

    private static XElement BuildStroke(
        string? color,
        double? opacity,
        double? width,
        string? lineCap,
        string? lineJoin,
        double[]? dashArray)
    {
        var stroke = new XElement(Sld + "Stroke");
        if (!string.IsNullOrEmpty(color))
        {
            stroke.Add(BuildCss("stroke", color));
        }

        if (opacity.HasValue)
        {
            stroke.Add(BuildCss("stroke-opacity", FormatNumber(opacity.Value)));
        }

        if (width.HasValue)
        {
            stroke.Add(BuildCss("stroke-width", FormatNumber(width.Value)));
        }

        if (!string.IsNullOrEmpty(lineCap))
        {
            stroke.Add(BuildCss("stroke-linecap", lineCap));
        }

        if (!string.IsNullOrEmpty(lineJoin))
        {
            stroke.Add(BuildCss("stroke-linejoin", lineJoin));
        }

        if (dashArray is { Length: > 0 })
        {
            var formatted = string.Join(' ', dashArray.Select(d => FormatNumber(d)));
            stroke.Add(BuildCss("stroke-dasharray", formatted));
        }

        return stroke;
    }

    private static XElement BuildLabel(string textField)
    {
        // {fieldName} → ogc:PropertyName; otherwise treat as literal text.
        if (textField.Length > 2 && textField.StartsWith('{') && textField.EndsWith('}'))
        {
            var propertyName = textField[1..^1];
            return new XElement(Sld + "Label",
                new XElement(Ogc + "PropertyName", propertyName));
        }

        return new XElement(Sld + "Label", textField);
    }

    private static XElement BuildCss(string name, string value) =>
        new(Sld + "CssParameter", new XAttribute("name", name), value);

    private static XElement? ConvertFilter(MapLibreExpression? filter, List<SldConversionDiagnostic> diagnostics, string? layerId)
    {
        if (!filter.HasValue || filter.Value.Kind != MapLibreExpressionKind.Array)
        {
            return null;
        }

        var converted = ConvertFilterExpression(filter.Value, diagnostics, layerId);
        if (converted == null)
        {
            return null;
        }

        return new XElement(Ogc + "Filter", converted);
    }

    private static XElement? ConvertFilterExpression(MapLibreExpression expression, List<SldConversionDiagnostic> diagnostics, string? layerId)
    {
        if (expression.Kind != MapLibreExpressionKind.Array || expression.Items is null || expression.Items.Length == 0)
        {
            return null;
        }

        var head = expression.Items[0];
        if (head.Kind != MapLibreExpressionKind.String || head.StringValue is null)
        {
            diagnostics.Add(Warn(
                "filter",
                "Filter expression operator must be a string literal; filter omitted.",
                layerId));
            return null;
        }

        var op = head.StringValue;
        switch (op)
        {
            case "all":
                {
                    var element = new XElement(Ogc + "And");
                    if (!TryAppendOperands(element, expression.Items, diagnostics, layerId))
                    {
                        return null;
                    }

                    return element.Elements().Any() ? element : null;
                }

            case "any":
                {
                    var element = new XElement(Ogc + "Or");
                    if (!TryAppendOperands(element, expression.Items, diagnostics, layerId))
                    {
                        return null;
                    }

                    return element.Elements().Any() ? element : null;
                }

            case "!":
                {
                    if (expression.Items.Length < 2)
                    {
                        return null;
                    }

                    var inner = ConvertFilterExpression(expression.Items[1], diagnostics, layerId);
                    return inner == null ? null : new XElement(Ogc + "Not", inner);
                }

            case "==":
                return ConvertComparison("PropertyIsEqualTo", expression, diagnostics, layerId);
            case "!=":
                return ConvertComparison("PropertyIsNotEqualTo", expression, diagnostics, layerId);
            case "<":
                return ConvertComparison("PropertyIsLessThan", expression, diagnostics, layerId);
            case "<=":
                return ConvertComparison("PropertyIsLessThanOrEqualTo", expression, diagnostics, layerId);
            case ">":
                return ConvertComparison("PropertyIsGreaterThan", expression, diagnostics, layerId);
            case ">=":
                return ConvertComparison("PropertyIsGreaterThanOrEqualTo", expression, diagnostics, layerId);

            default:
                diagnostics.Add(Warn(
                    "filter",
                    $"MapLibre filter operator '{op}' has no portable SLD form; filter omitted.",
                    layerId));
                return null;
        }
    }

    private static bool TryAppendOperands(XElement target, MapLibreExpression[] items, List<SldConversionDiagnostic> diagnostics, string? layerId)
    {
        for (var i = 1; i < items.Length; i++)
        {
            var converted = ConvertFilterExpression(items[i], diagnostics, layerId);
            if (converted == null)
            {
                // One of the operands could not be expressed in SLD; the caller must drop
                // the entire compound filter so the exported document does not silently
                // narrow (And) or broaden (Or) the original rule semantics.
                return false;
            }

            target.Add(converted);
        }

        return true;
    }

    private static XElement? ConvertComparison(string predicateName, MapLibreExpression expression, List<SldConversionDiagnostic> diagnostics, string? layerId)
    {
        if (expression.Items is null || expression.Items.Length < 3)
        {
            return null;
        }

        var lhs = expression.Items[1];
        var rhs = expression.Items[2];

        if (!TryExtractPropertyName(lhs, out var propertyName))
        {
            diagnostics.Add(Warn(
                "filter",
                "Filter comparison left-hand side must be a property reference (`get`); filter omitted.",
                layerId));
            return null;
        }

        if (!TryExtractLiteral(rhs, out var literal))
        {
            diagnostics.Add(Warn(
                "filter",
                "Filter comparison right-hand side must be a literal value; filter omitted.",
                layerId));
            return null;
        }

        return new XElement(Ogc + predicateName,
            new XElement(Ogc + "PropertyName", propertyName),
            new XElement(Ogc + "Literal", literal));
    }

    private static bool TryExtractPropertyName(MapLibreExpression expr, out string propertyName)
    {
        propertyName = string.Empty;
        if (expr.Kind == MapLibreExpressionKind.Array
            && expr.Items is { Length: 2 } items
            && items[0].Kind == MapLibreExpressionKind.String
            && items[0].StringValue == "get"
            && items[1].Kind == MapLibreExpressionKind.String
            && items[1].StringValue is { } name)
        {
            propertyName = name;
            return true;
        }

        return false;
    }

    private static bool TryExtractLiteral(MapLibreExpression expr, out string literal)
    {
        switch (expr.Kind)
        {
            case MapLibreExpressionKind.String:
                literal = expr.StringValue ?? string.Empty;
                return true;
            case MapLibreExpressionKind.Number:
                literal = expr.NumberValue.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case MapLibreExpressionKind.Boolean:
                literal = expr.BoolValue ? "true" : "false";
                return true;
            default:
                literal = string.Empty;
                return false;
        }
    }

    private static string? TryGetLiteralString(MapLibreStyleLayer layer, string key, List<SldConversionDiagnostic> diagnostics, bool isLayout = false)
    {
        var dict = isLayout ? layer.Layout : layer.Paint;
        if (dict == null || !dict.TryGetValue(key, out var expr))
        {
            return null;
        }

        if (expr.Kind == MapLibreExpressionKind.String)
        {
            return expr.StringValue;
        }

        diagnostics.Add(Warn(
            key,
            $"Property '{key}' is a non-literal expression and was omitted from SLD output.",
            layer.Id));
        return null;
    }

    private static double? TryGetLiteralNumber(MapLibreStyleLayer layer, string key, List<SldConversionDiagnostic> diagnostics, bool isLayout = false)
    {
        var dict = isLayout ? layer.Layout : layer.Paint;
        if (dict == null || !dict.TryGetValue(key, out var expr))
        {
            return null;
        }

        if (expr.Kind == MapLibreExpressionKind.Number)
        {
            return expr.NumberValue;
        }

        diagnostics.Add(Warn(
            key,
            $"Property '{key}' is a non-literal expression and was omitted from SLD output.",
            layer.Id));
        return null;
    }

    private static double[]? TryGetLiteralNumberArray(MapLibreStyleLayer layer, string key, List<SldConversionDiagnostic> diagnostics)
    {
        if (layer.Paint == null || !layer.Paint.TryGetValue(key, out var expr))
        {
            return null;
        }

        if (expr.Kind != MapLibreExpressionKind.Array || expr.Items is null)
        {
            diagnostics.Add(Warn(
                key,
                $"Property '{key}' is not a literal array and was omitted from SLD output.",
                layer.Id));
            return null;
        }

        var result = new double[expr.Items.Length];
        for (var i = 0; i < expr.Items.Length; i++)
        {
            if (expr.Items[i].Kind != MapLibreExpressionKind.Number)
            {
                diagnostics.Add(Warn(
                    key,
                    $"Property '{key}' contains a non-numeric element and was omitted from SLD output.",
                    layer.Id));
                return null;
            }

            result[i] = expr.Items[i].NumberValue;
        }

        return result;
    }

    private static double ZoomToScale(double zoom)
    {
        var clamped = Math.Clamp(zoom, 0d, 24d);
        return WebMercatorScaleReference / Math.Pow(2d, clamped);
    }

    private static string FormatNumber(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    public static string SerializeMapLibreLayers(JsonElement style, out string? error)
    {
        error = null;
        if (style.ValueKind != JsonValueKind.Object || !style.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
        {
            error = "Stored style is missing the layers array.";
            return string.Empty;
        }

        return layersElement.GetRawText();
    }

    private static SldConversionDiagnostic Warn(string construct, string message, string? ruleName) => new()
    {
        Severity = SldDiagnosticSeverity.Warning,
        Construct = construct,
        Message = message,
        RuleName = ruleName
    };
}
