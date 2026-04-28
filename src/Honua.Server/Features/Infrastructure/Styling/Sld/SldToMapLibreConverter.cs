// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Server.Features.Infrastructure.Rendering;

namespace Honua.Server.Features.Infrastructure.Styling.Sld;

/// <summary>
/// Converts a parsed <see cref="SldDocument"/> into MapLibre style layer objects.
/// Each SLD Rule produces one MapLibre layer per supported symbolizer; unsupported
/// constructs surface as <see cref="SldConversionDiagnostic"/> entries instead of
/// being silently dropped.
/// </summary>
internal static class SldToMapLibreConverter
{
    /// <summary>
    /// Web Mercator zoom-from-scale-denominator constant. zoom = log2(reference / scale).
    /// Approximates MapLibre's zoom levels at the equator.
    /// </summary>
    private const double WebMercatorScaleReference = 559_082_264.028d;

    private const int MaxZoom = 24;
    private const int MinZoom = 0;

    /// <summary>
    /// Upper bound on sanitized SLD identifier length. The endpoint accepts up to a
    /// 1 MiB body, so an unbounded stackalloc keyed on the SLD <c>Name</c> length
    /// would let untrusted input drive multi-megabyte stack frames and risk a
    /// StackOverflow. 64 characters is comfortably above typical rule-name lengths
    /// while keeping the buffer trivially safe.
    /// </summary>
    private const int MaxSanitizedIdentifierLength = 64;

    public static SldConversionResult Convert(SldDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new List<SldConversionDiagnostic>(document.ParseDiagnostics);
        var layers = new List<MapLibreStyleLayer>();
        var ruleCounter = 0;

        foreach (var named in document.NamedLayers)
        {
            foreach (var userStyle in named.UserStyles)
            {
                foreach (var fts in userStyle.FeatureTypeStyles)
                {
                    foreach (var rule in fts.Rules)
                    {
                        var ruleIndex = ruleCounter++;
                        ConvertRule(named, userStyle, fts, rule, ruleIndex, layers, diagnostics);
                    }
                }
            }
        }

        return new SldConversionResult
        {
            Layers = layers.ToArray(),
            Diagnostics = diagnostics.ToArray(),
            DetectedVersion = document.Version
        };
    }

    private static void ConvertRule(
        SldNamedLayer named,
        SldUserStyle userStyle,
        SldFeatureTypeStyle fts,
        SldRule rule,
        int ruleIndex,
        List<MapLibreStyleLayer> layers,
        List<SldConversionDiagnostic> diagnostics)
    {
        // Always fold ruleIndex into ruleId so two SLD rules sharing a Name still
        // produce distinct MapLibre layer ids — MapLibreStyleNormalizer rejects
        // duplicate ids, which would turn an otherwise valid SLD into a 400 at
        // import time.
        var sanitized = SanitizeIdentifier(rule.Name);
        var ruleId = sanitized != null
            ? $"{sanitized}-{ruleIndex}"
            : $"rule{ruleIndex}";
        var minZoom = ScaleToZoom(rule.MaxScaleDenominator);
        var maxZoom = ScaleToZoom(rule.MinScaleDenominator);
        var filter = BuildFilter(rule.Filter);

        if (filter == null && rule.Filter is SldFilterUnsupported unsupported)
        {
            // Diagnostic was already emitted at parse time; render unfiltered.
            _ = unsupported;
        }

        var symbolizerIndex = 0;
        foreach (var symbolizer in rule.Symbolizers)
        {
            switch (symbolizer)
            {
                case SldPointSymbolizer point:
                    layers.Add(BuildPointLayer(point, ruleId, symbolizerIndex, filter, minZoom, maxZoom, diagnostics, rule.Name));
                    break;
                case SldLineSymbolizer line:
                    layers.Add(BuildLineLayer(line, ruleId, symbolizerIndex, filter, minZoom, maxZoom));
                    break;
                case SldPolygonSymbolizer polygon:
                    BuildPolygonLayers(polygon, ruleId, symbolizerIndex, filter, minZoom, maxZoom, layers);
                    break;
                case SldTextSymbolizer text:
                    layers.Add(BuildTextLayer(text, ruleId, symbolizerIndex, filter, minZoom, maxZoom, diagnostics, rule.Name));
                    break;
            }

            symbolizerIndex++;
        }
    }

    private static MapLibreStyleLayer BuildPointLayer(
        SldPointSymbolizer point,
        string ruleId,
        int symbolizerIndex,
        MapLibreExpression? filter,
        double? minZoom,
        double? maxZoom,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        if (point.ExternalGraphic != null && point.Mark == null)
        {
            var paint = new Dictionary<string, MapLibreExpression>();
            var layout = new Dictionary<string, MapLibreExpression>
            {
                ["icon-image"] = new MapLibreExpression(point.ExternalGraphic.OnlineResourceHref ?? string.Empty)
            };
            if (point.Size.HasValue)
            {
                // SLD Graphic Size is an absolute size in the symbolizer unit (pixels by default
                // per OGC SE 1.1.0 § 11.3.2). MapLibre icon-size is a scale factor relative to the
                // sprite's intrinsic dimensions (1.0 = native size). Without sprite metadata we
                // cannot compute a meaningful scale factor, so omit icon-size and surface a
                // diagnostic rather than silently mis-scaling the sprite.
                diagnostics.Add(Warn(
                    "Graphic.Size",
                    $"SLD Graphic Size ({point.Size.Value}) is in pixels; MapLibre icon-size is a scale factor. icon-size omitted — provide sprite metadata to set the scale factor.",
                    ruleName));
            }

            if (point.Opacity.HasValue)
            {
                paint["icon-opacity"] = new MapLibreExpression(point.Opacity.Value);
            }

            return new MapLibreStyleLayer
            {
                Id = $"{ruleId}-{symbolizerIndex}",
                Type = "symbol",
                Filter = filter,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                Paint = paint.Count > 0 ? paint : null,
                Layout = layout
            };
        }

        var circlePaint = new Dictionary<string, MapLibreExpression>();
        var radius = point.Size.HasValue ? point.Size.Value / 2d : 5d;
        circlePaint["circle-radius"] = new MapLibreExpression(radius);

        // Color and opacity are emitted as separate paint properties (matching the
        // polygon-fill convention). Baking the opacity into rgba() would double-apply
        // it once MapLibre multiplies *-color alpha by *-opacity.
        var fillColor = point.Mark?.Fill?.Color;
        if (!string.IsNullOrEmpty(fillColor))
        {
            circlePaint["circle-color"] = new MapLibreExpression(NormalizeColor(fillColor, null));
        }

        if (point.Mark?.Fill?.Opacity is { } fillOpacity)
        {
            circlePaint["circle-opacity"] = new MapLibreExpression(fillOpacity);
        }
        else if (point.Opacity is { } graphicOpacity)
        {
            circlePaint["circle-opacity"] = new MapLibreExpression(graphicOpacity);
        }

        // Stroke color and opacity are emitted as separate paint properties
        // (matching the polygon-fill / line / circle-fill convention).
        // circle-stroke-opacity is a first-class MapLibre paint property.
        var strokeColor = point.Mark?.Stroke?.Color;
        if (!string.IsNullOrEmpty(strokeColor))
        {
            circlePaint["circle-stroke-color"] = new MapLibreExpression(NormalizeColor(strokeColor, null));
        }

        if (point.Mark?.Stroke?.Opacity is { } strokeOpacity)
        {
            circlePaint["circle-stroke-opacity"] = new MapLibreExpression(strokeOpacity);
        }

        if (point.Mark?.Stroke?.Width is { } strokeWidth)
        {
            circlePaint["circle-stroke-width"] = new MapLibreExpression(strokeWidth);
        }

        if (point.Mark?.WellKnownName is { Length: > 0 } well
            && !string.Equals(well, "circle", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Warn(
                "Mark.WellKnownName",
                $"WellKnownName '{well}' is rendered as a generic circle; MapLibre requires a sprite for shape support.",
                ruleName));
        }

        return new MapLibreStyleLayer
        {
            Id = $"{ruleId}-{symbolizerIndex}",
            Type = "circle",
            Filter = filter,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Paint = circlePaint
        };
    }

    private static MapLibreStyleLayer BuildLineLayer(
        SldLineSymbolizer line,
        string ruleId,
        int symbolizerIndex,
        MapLibreExpression? filter,
        double? minZoom,
        double? maxZoom)
    {
        var paint = new Dictionary<string, MapLibreExpression>();
        if (!string.IsNullOrEmpty(line.Stroke.Color))
        {
            // Pass null for opacity: line-opacity is set as a separate paint
            // property below, so baking opacity into rgba() would double-apply.
            paint["line-color"] = new MapLibreExpression(NormalizeColor(line.Stroke.Color, null));
        }

        if (line.Stroke.Width.HasValue)
        {
            paint["line-width"] = new MapLibreExpression(line.Stroke.Width.Value);
        }

        if (line.Stroke.Opacity.HasValue)
        {
            paint["line-opacity"] = new MapLibreExpression(line.Stroke.Opacity.Value);
        }

        if (line.Stroke.DashArray is { Length: > 0 } dashArray)
        {
            paint["line-dasharray"] = ToExpressionArray(dashArray);
        }

        Dictionary<string, MapLibreExpression>? layout = null;
        if (!string.IsNullOrEmpty(line.Stroke.LineCap))
        {
            layout = new Dictionary<string, MapLibreExpression>
            {
                ["line-cap"] = new MapLibreExpression(line.Stroke.LineCap)
            };
        }

        if (!string.IsNullOrEmpty(line.Stroke.LineJoin))
        {
            layout ??= new Dictionary<string, MapLibreExpression>();
            layout["line-join"] = new MapLibreExpression(line.Stroke.LineJoin);
        }

        return new MapLibreStyleLayer
        {
            Id = $"{ruleId}-{symbolizerIndex}",
            Type = "line",
            Filter = filter,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Paint = paint,
            Layout = layout
        };
    }

    private static void BuildPolygonLayers(
        SldPolygonSymbolizer polygon,
        string ruleId,
        int symbolizerIndex,
        MapLibreExpression? filter,
        double? minZoom,
        double? maxZoom,
        List<MapLibreStyleLayer> layers)
    {
        // Emit a fill layer only when the SLD PolygonSymbolizer carries a Fill.
        // StyleTranslator.ResolveFillStyle defaults missing fill-color to opaque black,
        // so emitting an empty fill layer for stroke-only polygons would produce an
        // unintended solid fill.
        if (polygon.Fill != null)
        {
            var fillPaint = new Dictionary<string, MapLibreExpression>();
            if (polygon.Fill.Color is { Length: > 0 } fillColor)
            {
                fillPaint["fill-color"] = new MapLibreExpression(NormalizeColor(fillColor, null));
            }

            if (polygon.Fill.Opacity is { } fillOpacity)
            {
                fillPaint["fill-opacity"] = new MapLibreExpression(fillOpacity);
            }

            // Outline lives on the dedicated line layer below (when Stroke is present);
            // setting fill-outline-color in addition would render two outlines.
            layers.Add(new MapLibreStyleLayer
            {
                Id = $"{ruleId}-{symbolizerIndex}",
                Type = "fill",
                Filter = filter,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                Paint = fillPaint.Count > 0 ? fillPaint : null
            });
        }

        if (polygon.Stroke is { } stroke)
        {
            // SLD/SE default stroke width is 1.0 px when the CssParameter is omitted.
            var linePaint = new Dictionary<string, MapLibreExpression>
            {
                ["line-width"] = new MapLibreExpression(stroke.Width ?? 1d)
            };

            if (!string.IsNullOrEmpty(stroke.Color))
            {
                // Pass null for opacity: line-opacity is set as a separate paint
                // property below, so baking opacity into rgba() would double-apply.
                linePaint["line-color"] = new MapLibreExpression(NormalizeColor(stroke.Color, null));
            }

            if (stroke.Opacity.HasValue)
            {
                linePaint["line-opacity"] = new MapLibreExpression(stroke.Opacity.Value);
            }

            if (stroke.DashArray is { Length: > 0 } dashArray)
            {
                linePaint["line-dasharray"] = ToExpressionArray(dashArray);
            }

            // The id suffix flags this as the polygon outline. When the polygon also has
            // a Fill, the fill layer above keeps the canonical id and this layer carries
            // the "-outline" suffix for stable round-trip identity.
            var outlineSuffix = polygon.Fill != null ? "-outline" : string.Empty;
            layers.Add(new MapLibreStyleLayer
            {
                Id = $"{ruleId}-{symbolizerIndex}{outlineSuffix}",
                Type = "line",
                Filter = filter,
                MinZoom = minZoom,
                MaxZoom = maxZoom,
                Paint = linePaint
            });
        }
    }

    private static MapLibreStyleLayer BuildTextLayer(
        SldTextSymbolizer text,
        string ruleId,
        int symbolizerIndex,
        MapLibreExpression? filter,
        double? minZoom,
        double? maxZoom,
        List<SldConversionDiagnostic> diagnostics,
        string? ruleName)
    {
        var layout = new Dictionary<string, MapLibreExpression>();
        var paint = new Dictionary<string, MapLibreExpression>();

        if (!string.IsNullOrEmpty(text.Label))
        {
            layout["text-field"] = new MapLibreExpression(text.Label);
        }
        else
        {
            diagnostics.Add(Warn(
                "TextSymbolizer.Label",
                "TextSymbolizer is missing a Label expression; text layer will not render.",
                ruleName));
        }

        if (text.Font?.Family is { Length: > 0 } family)
        {
            layout["text-font"] = new MapLibreExpression(new[] { new MapLibreExpression(family) });
        }

        if (text.Font?.Size is { } size)
        {
            layout["text-size"] = new MapLibreExpression(size);
        }

        // Color and opacity are emitted as separate paint properties: MapLibre exposes
        // text-opacity as a first-class paint property, so baking the alpha into rgba()
        // would double-apply the alpha once MapLibre multiplies text-color × text-opacity.
        if (text.Fill?.Color is { Length: > 0 } color)
        {
            paint["text-color"] = new MapLibreExpression(NormalizeColor(color, null));
        }

        if (text.Fill?.Opacity is { } textOpacity)
        {
            paint["text-opacity"] = new MapLibreExpression(textOpacity);
        }

        if (text.Halo != null)
        {
            if (text.Halo.Radius is { } radius)
            {
                paint["text-halo-width"] = new MapLibreExpression(radius);
            }

            if (text.Halo.Fill?.Color is { Length: > 0 } haloColor)
            {
                paint["text-halo-color"] = new MapLibreExpression(NormalizeColor(haloColor, text.Halo.Fill.Opacity));
            }
        }

        return new MapLibreStyleLayer
        {
            Id = $"{ruleId}-{symbolizerIndex}",
            Type = "symbol",
            Filter = filter,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Paint = paint.Count > 0 ? paint : null,
            Layout = layout
        };
    }

    private static MapLibreExpression? BuildFilter(SldFilter? filter)
    {
        if (filter == null)
        {
            return null;
        }

        return BuildFilterExpression(filter);
    }

    private static MapLibreExpression? BuildFilterExpression(SldFilter filter)
    {
        switch (filter)
        {
            case SldFilterAnd and:
                {
                    if (and.Operands.Count == 0)
                    {
                        return null;
                    }

                    var operands = new List<MapLibreExpression> { new("all") };
                    foreach (var operand in and.Operands)
                    {
                        var converted = BuildFilterExpression(operand);
                        if (!converted.HasValue)
                        {
                            // Drop the entire compound filter rather than silently narrow the
                            // rule to the supported operands. Diagnostic was emitted at parse
                            // time when the operand is SldFilterUnsupported.
                            return null;
                        }

                        operands.Add(converted.Value);
                    }

                    return new MapLibreExpression(operands.ToArray());
                }

            case SldFilterOr or:
                {
                    if (or.Operands.Count == 0)
                    {
                        return null;
                    }

                    var operands = new List<MapLibreExpression> { new("any") };
                    foreach (var operand in or.Operands)
                    {
                        var converted = BuildFilterExpression(operand);
                        if (!converted.HasValue)
                        {
                            // Drop the entire compound filter rather than silently broaden the
                            // rule by removing an OR operand.
                            return null;
                        }

                        operands.Add(converted.Value);
                    }

                    return new MapLibreExpression(operands.ToArray());
                }

            case SldFilterNot not:
                {
                    var inner = BuildFilterExpression(not.Operand);
                    if (!inner.HasValue)
                    {
                        return null;
                    }

                    return new MapLibreExpression(new[] { new MapLibreExpression("!"), inner.Value });
                }

            case SldFilterComparison comparison:
                return BuildComparison(comparison);

            case SldFilterUnsupported:
                return null;

            default:
                return null;
        }
    }

    private static MapLibreExpression BuildComparison(SldFilterComparison comparison)
    {
        var op = comparison.Operator switch
        {
            SldFilterComparisonOperator.Equal => "==",
            SldFilterComparisonOperator.NotEqual => "!=",
            SldFilterComparisonOperator.LessThan => "<",
            SldFilterComparisonOperator.LessThanOrEqual => "<=",
            SldFilterComparisonOperator.GreaterThan => ">",
            SldFilterComparisonOperator.GreaterThanOrEqual => ">=",
            _ => "=="
        };

        var literal = ParseLiteralExpression(comparison.Literal);
        var get = new MapLibreExpression(new[]
        {
            new MapLibreExpression("get"),
            new MapLibreExpression(comparison.PropertyName)
        });

        return new MapLibreExpression(new[]
        {
            new MapLibreExpression(op),
            get,
            literal
        });
    }

    private static MapLibreExpression ParseLiteralExpression(string literal)
    {
        if (string.Equals(literal, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new MapLibreExpression(true);
        }

        if (string.Equals(literal, "false", StringComparison.OrdinalIgnoreCase))
        {
            return new MapLibreExpression(false);
        }

        if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new MapLibreExpression(number);
        }

        return new MapLibreExpression(literal);
    }

    private static double? ScaleToZoom(double? scaleDenominator)
    {
        if (!scaleDenominator.HasValue || scaleDenominator.Value <= 0)
        {
            return null;
        }

        var zoom = Math.Log2(WebMercatorScaleReference / scaleDenominator.Value);
        zoom = Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 2, MidpointRounding.AwayFromZero);
        return zoom;
    }

    private static string NormalizeColor(string color, double? opacity)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return color;
        }

        // SLD AARRGGBB hex (alpha-prefixed) needs to become rgba() for MapLibre.
        if (color.Length == 9 && color[0] == '#')
        {
            if (TryParseHexByte(color.AsSpan(1, 2), out var a)
                && TryParseHexByte(color.AsSpan(3, 2), out var r)
                && TryParseHexByte(color.AsSpan(5, 2), out var g)
                && TryParseHexByte(color.AsSpan(7, 2), out var b))
            {
                var alpha = a / 255d;
                return string.Create(CultureInfo.InvariantCulture, $"rgba({r},{g},{b},{alpha:0.###})");
            }
        }

        if (opacity.HasValue && opacity.Value < 1d && opacity.Value >= 0d
            && color.Length == 7 && color[0] == '#'
            && TryParseHexByte(color.AsSpan(1, 2), out var rr)
            && TryParseHexByte(color.AsSpan(3, 2), out var gg)
            && TryParseHexByte(color.AsSpan(5, 2), out var bb))
        {
            return string.Create(CultureInfo.InvariantCulture, $"rgba({rr},{gg},{bb},{opacity.Value:0.###})");
        }

        return color;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> span, out int value)
    {
        return int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static MapLibreExpression ToExpressionArray(double[] values)
    {
        var items = new MapLibreExpression[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            items[i] = new MapLibreExpression(values[i]);
        }

        return new MapLibreExpression(items);
    }

    private static string? SanitizeIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var span = name.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return null;
        }

        // Cap stack usage independently of the (1 MiB) body cap. Without this, a
        // pathological SLD Name element could drive a multi-megabyte stackalloc.
        // ruleIndex still guarantees ruleId uniqueness when truncation collapses
        // two distinct names to the same prefix.
        var bufferLength = Math.Min(span.Length, MaxSanitizedIdentifierLength);
        Span<char> buffer = stackalloc char[bufferLength];
        var written = 0;
        foreach (var c in span)
        {
            if (written == bufferLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                buffer[written++] = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                buffer[written++] = '-';
            }
        }

        return written == 0 ? null : new string(buffer[..written]);
    }

    private static SldConversionDiagnostic Warn(string construct, string message, string? ruleName) => new()
    {
        Severity = SldDiagnosticSeverity.Warning,
        Construct = construct,
        Message = message,
        RuleName = ruleName
    };
}
