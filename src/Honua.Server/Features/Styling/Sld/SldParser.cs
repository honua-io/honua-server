// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Honua.Infrastructure.Helpers;

namespace Honua.Server.Features.Styling.Sld;

internal static class SldParser
{
    private const int MaxInputCharacters = 1_048_576; // 1 MiB - migration uploads are small.
    private const double AnchorLowerBound = 0.33d;
    private const double AnchorUpperBound = 0.67d;

    private static readonly char[] DashArraySeparators = [' ', ','];

    public static SldDocument Parse(string sldXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sldXml);

        if (sldXml.Length > MaxInputCharacters)
        {
            throw new SldParseException(
                $"SLD document exceeds the {MaxInputCharacters} character limit.");
        }

        XDocument document;
        try
        {
            document = SecureXmlDocumentParser.Parse(sldXml);
        }
        catch (XmlException ex)
        {
            throw new SldParseException("SLD document is not well-formed XML.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new SldParseException("SLD document was empty or whitespace.", ex);
        }

        var root = document.Root
            ?? throw new SldParseException("SLD document has no root element.");

        if (!IsRecognizedRoot(root))
        {
            throw new SldParseException(
                "SLD document root must be StyledLayerDescriptor, FeatureTypeStyle, or UserStyle.");
        }

        var diagnostics = new List<SldConversionDiagnostic>();
        var version = DetectVersion(root);
        var namedLayers = ParseRootContents(root, diagnostics);

        return new SldDocument(version, namedLayers, diagnostics);
    }

    private static bool IsRecognizedRoot(XElement root)
    {
        var local = root.Name.LocalName;
        return local is "StyledLayerDescriptor"
            or "UserStyle"
            or "FeatureTypeStyle";
    }

    private static SldVersion DetectVersion(XElement root)
    {
        var versionAttribute = root.Attribute("version")?.Value;
        if (!string.IsNullOrWhiteSpace(versionAttribute) && versionAttribute.StartsWith("1.1", StringComparison.Ordinal))
        {
            return SldVersion.Sld11;
        }

        // Heuristic: if the root or any descendant uses the SE namespace, treat as 1.1.
        var usesSeNamespace = root.DescendantsAndSelf()
            .Any(descendant => string.Equals(descendant.Name.NamespaceName, SldNamespaces.Se, StringComparison.Ordinal));

        return usesSeNamespace ? SldVersion.Sld11 : SldVersion.Sld10;
    }

    private static List<SldNamedLayer> ParseRootContents(XElement root, List<SldConversionDiagnostic> diagnostics)
    {
        var local = root.Name.LocalName;
        if (local == "StyledLayerDescriptor")
        {
            var named = new List<SldNamedLayer>();
            foreach (var element in root.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "NamedLayer":
                        named.Add(ParseNamedLayer(element, diagnostics));
                        break;
                    case "UserLayer":
                        diagnostics.Add(Warn(
                            "UserLayer",
                            "UserLayer is not supported; only NamedLayer entries are converted."));
                        break;
                }
            }

            return named;
        }

        if (local == "UserStyle")
        {
            var styles = new List<SldUserStyle> { ParseUserStyle(root, diagnostics) };
            return new List<SldNamedLayer> { new(null, styles) };
        }

        // FeatureTypeStyle root.
        var featureTypeStyles = new List<SldFeatureTypeStyle> { ParseFeatureTypeStyle(root, diagnostics) };
        var userStyle = new SldUserStyle(null, featureTypeStyles);
        return new List<SldNamedLayer> { new(null, new List<SldUserStyle> { userStyle }) };
    }

    private static SldNamedLayer ParseNamedLayer(XElement element, List<SldConversionDiagnostic> diagnostics)
    {
        var name = ChildLocal(element, "Name")?.Value?.Trim();
        var styles = new List<SldUserStyle>();

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "UserStyle":
                    styles.Add(ParseUserStyle(child, diagnostics));
                    break;
                case "NamedStyle":
                    diagnostics.Add(Warn(
                        "NamedStyle",
                        $"NamedStyle '{ChildLocal(child, "Name")?.Value}' references a server-side style and cannot be converted."));
                    break;
            }
        }

        return new SldNamedLayer(name, styles);
    }

    private static SldUserStyle ParseUserStyle(XElement element, List<SldConversionDiagnostic> diagnostics)
    {
        var name = ChildLocal(element, "Name")?.Value?.Trim();
        var featureTypeStyles = new List<SldFeatureTypeStyle>();

        foreach (var child in element.Elements().Where(child => child.Name.LocalName == "FeatureTypeStyle"))
        {
            featureTypeStyles.Add(ParseFeatureTypeStyle(child, diagnostics));
        }

        return new SldUserStyle(name, featureTypeStyles);
    }

    private static SldFeatureTypeStyle ParseFeatureTypeStyle(XElement element, List<SldConversionDiagnostic> diagnostics)
    {
        var name = ChildLocal(element, "Name")?.Value?.Trim();
        var rules = new List<SldRule>();

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Rule":
                    rules.Add(ParseRule(child, diagnostics));
                    break;
                case "VendorOption":
                    diagnostics.Add(Warn(
                        "VendorOption",
                        $"VendorOption '{child.Attribute("name")?.Value}' is not portable to MapLibre and was ignored."));
                    break;
                case "Transformation":
                    diagnostics.Add(Warn(
                        "Transformation",
                        "FeatureTypeStyle Transformation is not supported and was ignored."));
                    break;
            }
        }

        return new SldFeatureTypeStyle(name, rules);
    }

    private static SldRule ParseRule(XElement element, List<SldConversionDiagnostic> diagnostics)
    {
        string? name = null;
        SldFilter? filter = null;
        var isElseFilter = false;
        double? minScale = null;
        double? maxScale = null;
        var symbolizers = new List<SldSymbolizer>();

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Name":
                    name = child.Value?.Trim();
                    break;
                case "Filter":
                    filter = SldFilterParser.ParseFilter(child, diagnostics, name);
                    break;
                case "ElseFilter":
                    isElseFilter = true;
                    break;
                case "VendorOption":
                    diagnostics.Add(Warn(
                        "VendorOption",
                        $"Rule VendorOption '{child.Attribute("name")?.Value}' is not portable to MapLibre and was ignored.",
                        name));
                    break;
                case "MinScaleDenominator":
                    minScale = ParseDouble(child.Value);
                    break;
                case "MaxScaleDenominator":
                    maxScale = ParseDouble(child.Value);
                    break;
                case "PointSymbolizer":
                    symbolizers.Add(ParsePointSymbolizer(child, diagnostics, name));
                    break;
                case "LineSymbolizer":
                    var line = ParseLineSymbolizer(child, diagnostics, name);
                    if (line != null)
                    {
                        symbolizers.Add(line);
                    }
                    break;
                case "PolygonSymbolizer":
                    symbolizers.Add(ParsePolygonSymbolizer(child, diagnostics, name));
                    break;
                case "TextSymbolizer":
                    symbolizers.Add(ParseTextSymbolizer(child, diagnostics, name));
                    break;
                case "RasterSymbolizer":
                    diagnostics.Add(Warn(
                        "RasterSymbolizer",
                        "RasterSymbolizer is not supported; raster styling is handled separately.",
                        name));
                    break;
            }
        }

        return new SldRule
        {
            Name = name,
            Filter = filter,
            Kind = isElseFilter ? SldRuleKind.ElseFilter : SldRuleKind.Standard,
            MinScaleDenominator = minScale,
            MaxScaleDenominator = maxScale,
            Symbolizers = symbolizers
        };
    }

    private static SldPointSymbolizer ParsePointSymbolizer(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        SldMark? mark = null;
        SldExternalGraphic? externalGraphic = null;
        SldValueExpression? size = null;
        SldValueExpression? opacity = null;
        SldValueExpression? rotation = null;
        var parseContext = new SldDiagnosticContext(diagnostics, ruleName);

        EmitSymbolizerVendorOptionDiagnostics(element, diagnostics, ruleName, "PointSymbolizer");

        var graphic = ChildLocal(element, "Graphic");
        if (graphic != null)
        {
            foreach (var graphicChild in graphic.Elements())
            {
                switch (graphicChild.Name.LocalName)
                {
                    case "Mark":
                        mark = ParseMark(graphicChild, diagnostics, ruleName);
                        break;
                    case "ExternalGraphic":
                        externalGraphic = ParseExternalGraphic(graphicChild, diagnostics, ruleName);
                        break;
                    case "Size":
                        size = SldExpressionParser.ParseValueExpression(graphicChild, parseContext, "Graphic.Size");
                        break;
                    case "Opacity":
                        opacity = SldExpressionParser.ParseValueExpression(graphicChild, parseContext, "Graphic.Opacity");
                        break;
                    case "Rotation":
                        rotation = SldExpressionParser.ParseValueExpression(graphicChild, parseContext, "Graphic.Rotation");
                        break;
                }
            }
        }

        return new SldPointSymbolizer
        {
            Mark = mark,
            ExternalGraphic = externalGraphic,
            Size = size,
            Opacity = opacity,
            Rotation = rotation
        };
    }

    private static SldMark ParseMark(XElement element, List<SldConversionDiagnostic> diagnostics, string? ruleName)
    {
        var well = ChildLocal(element, "WellKnownName")?.Value?.Trim();
        var fill = ParseFill(ChildLocal(element, "Fill"), diagnostics, ruleName, "Mark Fill");
        var stroke = ParseStroke(ChildLocal(element, "Stroke"), diagnostics, ruleName, "Mark Stroke");
        return new SldMark(well, fill, stroke);
    }

    private static SldExternalGraphic ParseExternalGraphic(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        string? href = null;
        var onlineResource = ChildLocal(element, "OnlineResource");
        if (onlineResource != null)
        {
            // xlink:href attribute is the URI reference.
            href = onlineResource.Attribute(SldNamespaces.XlinkNs + "href")?.Value
                ?? onlineResource.Attribute("href")?.Value;
        }

        var format = ChildLocal(element, "Format")?.Value?.Trim();

        if (!string.IsNullOrWhiteSpace(href) && IsRemoteUri(href))
        {
            diagnostics.Add(Warn(
                "ExternalGraphic",
                $"ExternalGraphic references remote URI '{href}'. Honua does not fetch external resources; icon-image will reference the URI as-is and migration users must add the icon to their sprite sheet.",
                ruleName));
        }
        else if (!string.IsNullOrWhiteSpace(href))
        {
            diagnostics.Add(Warn(
                "ExternalGraphic",
                $"ExternalGraphic references '{href}'. Sprite asset must be supplied separately; icon-image is set to the resource name.",
                ruleName));
        }

        return new SldExternalGraphic(href, format);
    }

    private static SldLineSymbolizer? ParseLineSymbolizer(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        EmitSymbolizerVendorOptionDiagnostics(element, diagnostics, ruleName, "LineSymbolizer");

        var strokeElement = ChildLocal(element, "Stroke");
        var stroke = ParseStroke(strokeElement, diagnostics, ruleName, "LineSymbolizer Stroke");
        if (ChildLocal(element, "PerpendicularOffset") != null)
        {
            diagnostics.Add(Warn(
                "PerpendicularOffset",
                "LineSymbolizer PerpendicularOffset has no MapLibre equivalent and was ignored.",
                ruleName));
        }

        // Drop the symbolizer when no portable stroke content remains (missing
        // <Stroke>, empty <Stroke/>, or only GraphicFill/GraphicStroke children).
        // Otherwise the converter would emit a line layer whose empty paint defaults
        // to opaque black at render time and silently substitute for the unsupported
        // construct. Diagnostics are already emitted at parse time.
        return stroke == null ? null : new SldLineSymbolizer(stroke);
    }

    private static SldPolygonSymbolizer ParsePolygonSymbolizer(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        EmitSymbolizerVendorOptionDiagnostics(element, diagnostics, ruleName, "PolygonSymbolizer");

        var fill = ParseFill(ChildLocal(element, "Fill"), diagnostics, ruleName, "PolygonSymbolizer Fill");
        var stroke = ParseStroke(ChildLocal(element, "Stroke"), diagnostics, ruleName, "PolygonSymbolizer Stroke");
        if (ChildLocal(element, "Displacement") != null)
        {
            diagnostics.Add(Warn(
                "Displacement",
                "PolygonSymbolizer Displacement has no MapLibre equivalent and was ignored.",
                ruleName));
        }

        return new SldPolygonSymbolizer(fill, stroke);
    }

    private static SldTextSymbolizer ParseTextSymbolizer(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        EmitSymbolizerVendorOptionDiagnostics(element, diagnostics, ruleName, "TextSymbolizer");

        var label = ParseLabelExpression(ChildLocal(element, "Label"), diagnostics, ruleName);
        var font = ParseFont(ChildLocal(element, "Font"), new SldDiagnosticContext(diagnostics, ruleName));
        var fill = ParseFill(ChildLocal(element, "Fill"), diagnostics, ruleName, "TextSymbolizer Fill");
        var halo = ParseHalo(ChildLocal(element, "Halo"), diagnostics, ruleName);
        var placement = ParseLabelPlacement(ChildLocal(element, "LabelPlacement"), new SldDiagnosticContext(diagnostics, ruleName));

        return new SldTextSymbolizer
        {
            Label = label,
            Font = font,
            Fill = fill,
            Halo = halo,
            Placement = placement
        };
    }

    private static SldLabelPlacement? ParseLabelPlacement(XElement? element, SldDiagnosticContext context)
    {
        if (element == null)
        {
            return null;
        }

        if (ChildLocal(element, "LinePlacement") != null)
        {
            return new SldLabelPlacement { Kind = SldPlacementKind.Line };
        }

        var pointPlacement = ChildLocal(element, "PointPlacement");
        if (pointPlacement == null)
        {
            return null;
        }

        return new SldLabelPlacement
        {
            Kind = SldPlacementKind.Point,
            TextAnchor = ParseAnchorPoint(ChildLocal(pointPlacement, "AnchorPoint")),
            Offset = ParseDisplacementOffset(ChildLocal(pointPlacement, "Displacement")),
            Rotation = SldExpressionParser.ParseValueExpression(
                ChildLocal(pointPlacement, "Rotation"),
                context,
                "LabelPlacement.Rotation")
        };
    }

    private static string? ParseAnchorPoint(XElement? anchorElement)
    {
        if (anchorElement == null)
        {
            return null;
        }

        var x = ParseDouble(ChildLocal(anchorElement, "AnchorPointX")?.Value) ?? 0.5d;
        var y = ParseDouble(ChildLocal(anchorElement, "AnchorPointY")?.Value) ?? 0.5d;

        var horizontal = x < AnchorLowerBound ? "left" : x > AnchorUpperBound ? "right" : string.Empty;
        var vertical = y < AnchorLowerBound ? "bottom" : y > AnchorUpperBound ? "top" : string.Empty;

        if (horizontal.Length == 0 && vertical.Length == 0)
        {
            return "center";
        }

        if (horizontal.Length == 0)
        {
            return vertical;
        }

        return vertical.Length == 0 ? horizontal : $"{vertical}-{horizontal}";
    }

    private static double[]? ParseDisplacementOffset(XElement? displacementElement)
    {
        if (displacementElement == null)
        {
            return null;
        }

        var dx = ParseDouble(ChildLocal(displacementElement, "DisplacementX")?.Value);
        var dy = ParseDouble(ChildLocal(displacementElement, "DisplacementY")?.Value);
        if (!dx.HasValue && !dy.HasValue)
        {
            return null;
        }

        return [dx ?? 0d, -(dy ?? 0d)];
    }

    private static string? ParseLabelExpression(
        XElement? labelElement,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        if (labelElement == null)
        {
            return null;
        }

        // <Label><ogc:PropertyName>name</ogc:PropertyName></Label>
        var propertyName = labelElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "PropertyName");
        if (propertyName != null)
        {
            return $"{{{propertyName.Value.Trim()}}}";
        }

        if (labelElement.Elements().Any(e => e.Name.LocalName == "Function"))
        {
            diagnostics.Add(Warn(
                "Function",
                "Label uses an OGC Function expression; only PropertyName references are converted.",
                ruleName));
            return null;
        }

        var literal = labelElement.Value?.Trim();
        return string.IsNullOrEmpty(literal) ? null : literal;
    }

    private static SldFill? ParseFill(
        XElement? fill,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName,
        string scope)
    {
        if (fill == null)
        {
            return null;
        }

        SldValueExpression? color = null;
        SldValueExpression? opacity = null;
        foreach (var child in fill.Elements())
        {
            if (child.Name.LocalName == "GraphicFill")
            {
                // SLD <GraphicFill> embeds a <Graphic> sub-tree to fill the polygon with a
                // tiled pattern. MapLibre has no portable equivalent — fill-pattern requires
                // a sprite, which the migration path cannot synthesize. Emit a warning so
                // the operator sees the unsupported construct; the caller skips the fill
                // layer when no portable CssParameter remains.
                diagnostics.Add(Warn(
                    "GraphicFill",
                    $"{scope} GraphicFill has no portable MapLibre equivalent (fill-pattern requires a sprite). Fill omitted.",
                    ruleName));
                continue;
            }

            if (child.Name.LocalName != "CssParameter" && child.Name.LocalName != "SvgParameter")
            {
                continue;
            }

            var paramName = child.Attribute("name")?.Value;
            var parseContext = new SldDiagnosticContext(diagnostics, ruleName);
            switch (paramName)
            {
                case "fill":
                    color = SldExpressionParser.ParseValueExpression(child, parseContext, $"{scope}.fill");
                    break;
                case "fill-opacity":
                    opacity = SldExpressionParser.ParseValueExpression(child, parseContext, $"{scope}.fill-opacity");
                    break;
            }
        }

        // Drop empty fills (missing CssParameter/SvgParameter children, e.g. only
        // GraphicFill or empty <Fill/>) so the converter does not emit a fill layer
        // that would default to opaque black at render time.
        if (color == null && opacity == null)
        {
            return null;
        }

        return new SldFill(color, opacity);
    }

    private static SldStroke? ParseStroke(
        XElement? stroke,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName,
        string scope)
    {
        if (stroke == null)
        {
            return null;
        }

        SldValueExpression? color = null;
        SldValueExpression? opacity = null;
        SldValueExpression? width = null;
        string? lineCap = null;
        string? lineJoin = null;
        double[]? dashArray = null;
        var parseContext = new SldDiagnosticContext(diagnostics, ruleName);

        foreach (var child in stroke.Elements())
        {
            if (child.Name.LocalName is "GraphicFill" or "GraphicStroke")
            {
                // <GraphicFill>/<GraphicStroke> embed a <Graphic> sub-tree for stroke fill
                // or repeating sprite. MapLibre has no portable equivalent (line-pattern
                // requires a sprite). Emit a warning so the operator sees the unsupported
                // construct; the caller skips the line layer when no portable CssParameter
                // remains.
                diagnostics.Add(Warn(
                    child.Name.LocalName,
                    $"{scope} {child.Name.LocalName} has no portable MapLibre equivalent (line-pattern requires a sprite). Stroke omitted.",
                    ruleName));
                continue;
            }

            if (child.Name.LocalName != "CssParameter" && child.Name.LocalName != "SvgParameter")
            {
                continue;
            }

            var paramName = child.Attribute("name")?.Value;
            var value = child.Value?.Trim();
            switch (paramName)
            {
                case "stroke":
                    color = SldExpressionParser.ParseValueExpression(child, parseContext, $"{scope}.stroke");
                    break;
                case "stroke-opacity":
                    opacity = SldExpressionParser.ParseValueExpression(child, parseContext, $"{scope}.stroke-opacity");
                    break;
                case "stroke-width":
                    width = SldExpressionParser.ParseValueExpression(child, parseContext, $"{scope}.stroke-width");
                    break;
                case "stroke-linecap":
                    lineCap = value;
                    break;
                case "stroke-linejoin":
                    lineJoin = value;
                    break;
                case "stroke-dasharray":
                    dashArray = ParseDashArray(value);
                    break;
            }
        }

        // Drop empty strokes (missing CssParameter/SvgParameter children, e.g. only
        // GraphicStroke or empty <Stroke/>) so the converter does not emit a line
        // layer that would default to a 1px opaque line at render time.
        if (color == null
            && opacity == null
            && width == null
            && lineCap == null
            && lineJoin == null
            && dashArray == null)
        {
            return null;
        }

        return new SldStroke
        {
            Color = color,
            Opacity = opacity,
            Width = width,
            LineCap = lineCap,
            LineJoin = lineJoin,
            DashArray = dashArray
        };
    }

    private static SldFont? ParseFont(XElement? font, SldDiagnosticContext context)
    {
        if (font == null)
        {
            return null;
        }

        string? family = null;
        SldValueExpression? size = null;
        string? style = null;
        string? weight = null;
        foreach (var css in font.Elements().Where(e => e.Name.LocalName is "CssParameter" or "SvgParameter"))
        {
            var value = css.Value?.Trim();
            switch (css.Attribute("name")?.Value)
            {
                case "font-family": family = value; break;
                case "font-size": size = SldExpressionParser.ParseValueExpression(css, context, "Font.font-size"); break;
                case "font-style": style = value; break;
                case "font-weight": weight = value; break;
            }
        }

        return new SldFont { Family = family, Size = size, Style = style, Weight = weight };
    }

    private static SldHalo? ParseHalo(XElement? halo, List<SldConversionDiagnostic> diagnostics, string? ruleName)
    {
        if (halo == null)
        {
            return null;
        }

        var radius = SldExpressionParser.ParseValueExpression(
            ChildLocal(halo, "Radius"),
            new SldDiagnosticContext(diagnostics, ruleName),
            "Halo.Radius");
        var fill = ParseFill(ChildLocal(halo, "Fill"), diagnostics, ruleName, "Halo Fill");
        return new SldHalo(radius, fill);
    }

    private static double[]? ParseDashArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(DashArraySeparators, StringSplitOptions.RemoveEmptyEntries);
        var result = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
            {
                return null;
            }
        }

        return result.Length > 0 ? result : null;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static XElement? ChildLocal(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(child => child.Name.LocalName == localName);
    }

    private static bool IsRemoteUri(string href)
    {
        return href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
    }

    private static SldConversionDiagnostic Warn(string construct, string message, string? ruleName = null) => new()
    {
        Severity = SldDiagnosticSeverity.Warning,
        Construct = construct,
        Message = message,
        RuleName = ruleName
    };

    private static void EmitSymbolizerVendorOptionDiagnostics(
        XElement element,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName,
        string scope)
    {
        // GeoServer attaches vendor options at the symbolizer level (e.g. labelObstacle,
        // graphic-margin, partials, autoWrap, repeat). They have no portable MapLibre
        // equivalent; record a diagnostic per occurrence so callers see exactly what
        // would have been ignored.
        foreach (var child in element.Elements().Where(child => child.Name.LocalName == "VendorOption"))
        {
            diagnostics.Add(Warn(
                "VendorOption",
                $"{scope} VendorOption '{child.Attribute("name")?.Value}' is not portable to MapLibre and was ignored.",
                ruleName));
        }
    }
}

internal static class SldNamespaces
{
    public const string Sld = "http://www.opengis.net/sld";
    public const string Se = "http://www.opengis.net/se";
    public const string Ogc = "http://www.opengis.net/ogc";
    public const string Xlink = "http://www.w3.org/1999/xlink";

    public static readonly XNamespace SldNs = XNamespace.Get(Sld);
    public static readonly XNamespace SeNs = XNamespace.Get(Se);
    public static readonly XNamespace OgcNs = XNamespace.Get(Ogc);
    public static readonly XNamespace XlinkNs = XNamespace.Get(Xlink);
}
