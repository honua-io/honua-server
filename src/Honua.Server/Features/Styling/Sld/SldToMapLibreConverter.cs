// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Infrastructure.Rendering;

namespace Honua.Server.Features.Styling.Sld;

internal static class SldToMapLibreConverter
{
    private const double WebMercatorScaleReference = 559_082_264.028d;
    private const int MaxZoom = 24;
    private const int MinZoom = 0;
    private const int MaxSanitizedIdentifierLength = 64;

    private static readonly HashSet<string> SupportedSymbolMarks = new(StringComparer.OrdinalIgnoreCase)
    {
        "square",
        "triangle",
        "star",
        "cross",
        "x"
    };

    public static SldConversionResult Convert(SldDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var accumulator = new ConversionAccumulator(document.ParseDiagnostics);
        foreach (var fts in document.NamedLayers
                     .SelectMany(n => n.UserStyles)
                     .SelectMany(u => u.FeatureTypeStyles))
        {
            ConvertFeatureTypeStyle(fts, accumulator);
        }

        return new SldConversionResult
        {
            Layers = accumulator.Layers.ToArray(),
            Diagnostics = accumulator.Diagnostics.ToArray(),
            DetectedVersion = document.Version
        };
    }

    private static void ConvertFeatureTypeStyle(SldFeatureTypeStyle fts, ConversionAccumulator accumulator)
    {
        var resolvedRules = new ResolvedRuleFilter[fts.Rules.Count];
        var elseState = CollectSiblingFilterState(fts.Rules, resolvedRules, accumulator.Diagnostics);

        for (var i = 0; i < fts.Rules.Count; i++)
        {
            var rule = fts.Rules[i];
            var ruleIndex = accumulator.NextRuleIndex();
            var context = new SldDiagnosticContext(accumulator.Diagnostics, rule.Name);
            var resolved = rule.Kind == SldRuleKind.ElseFilter
                ? ResolveElseFilter(elseState, context)
                : resolvedRules[i];

            if (resolved.Disposition == RuleFilterDisposition.Include)
            {
                EmitRuleLayers(rule, BuildRuleMetadata(rule, ruleIndex, resolved.Filter), accumulator);
            }
        }
    }

    private static ElseFilterState CollectSiblingFilterState(
        IReadOnlyList<SldRule> rules,
        ResolvedRuleFilter[] resolvedRules,
        List<SldConversionDiagnostic> diagnostics)
    {
        var tracker = new SiblingFilterTracker();

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Kind == SldRuleKind.ElseFilter)
            {
                continue;
            }

            var resolved = ResolveStandardRuleFilter(rule, new SldDiagnosticContext(diagnostics, rule.Name));
            resolvedRules[i] = resolved;
            tracker.Record(rule, resolved);
        }

        return new ElseFilterState { SiblingFilters = tracker.Filters, Status = tracker.Status };
    }

    private static ResolvedRuleFilter ResolveStandardRuleFilter(SldRule rule, SldDiagnosticContext context)
    {
        if (rule.Filter == null)
        {
            return new ResolvedRuleFilter { Disposition = RuleFilterDisposition.Include };
        }

        var converted = BuildFilterExpression(rule.Filter, context);
        return converted.HasValue
            ? new ResolvedRuleFilter { Filter = converted, Disposition = RuleFilterDisposition.Include }
            : new ResolvedRuleFilter { Disposition = RuleFilterDisposition.Omit };
    }

    private static ResolvedRuleFilter ResolveElseFilter(ElseFilterState state, SldDiagnosticContext context)
    {
        if (state.Status == ElseSiblingStatus.HasUnsupported)
        {
            context.Diagnostics.Add(Warn(
                "ElseFilter",
                "ElseFilter cannot be computed because a sibling rule has an unsupported filter; rule omitted.",
                context.RuleName));
            return new ResolvedRuleFilter { Disposition = RuleFilterDisposition.Omit };
        }

        if (state.Status == ElseSiblingStatus.HasUnconditional)
        {
            context.Warn("ElseFilter", "ElseFilter is unreachable because a sibling rule has no filter; rule omitted.");
            return new ResolvedRuleFilter { Disposition = RuleFilterDisposition.Omit };
        }

        if (state.Status == ElseSiblingStatus.HasScaledSibling)
        {
            context.Warn("ElseFilter", "ElseFilter negation does not fold sibling scale denominators; features outside a sibling scale range may still be excluded.");
        }

        return new ResolvedRuleFilter
        {
            Filter = NegateSiblingFilters(state.SiblingFilters),
            Disposition = RuleFilterDisposition.Include
        };
    }

    private static MapLibreExpression? NegateSiblingFilters(List<MapLibreExpression> siblingFilters)
    {
        if (siblingFilters.Count == 0)
        {
            return null;
        }

        if (siblingFilters.Count == 1)
        {
            return SldExpressionConverter.Expr("!", siblingFilters[0]);
        }

        var anyExpr = SldExpressionConverter.Expr("any", siblingFilters.ToArray());
        return SldExpressionConverter.Expr("!", anyExpr);
    }

    private static RuleLayerMetadata BuildRuleMetadata(SldRule rule, int ruleIndex, MapLibreExpression? filter)
    {
        var sanitized = SanitizeIdentifier(rule.Name);
        return new RuleLayerMetadata
        {
            RuleId = sanitized != null ? $"{sanitized}-{ruleIndex}" : $"rule{ruleIndex}",
            RuleName = rule.Name,
            Filter = filter,
            MinZoom = ScaleToZoom(rule.MaxScaleDenominator),
            MaxZoom = ScaleToZoom(rule.MinScaleDenominator)
        };
    }

    private static void EmitRuleLayers(SldRule rule, RuleLayerMetadata meta, ConversionAccumulator accumulator)
    {
        var context = new SldDiagnosticContext(accumulator.Diagnostics, rule.Name);
        for (var i = 0; i < rule.Symbolizers.Count; i++)
        {
            var slot = new SymbolizerSlot { Meta = meta, Index = i, Context = context };
            switch (rule.Symbolizers[i])
            {
                case SldPointSymbolizer point:
                    accumulator.Layers.Add(BuildPointLayer(point, slot));
                    break;
                case SldLineSymbolizer line:
                    accumulator.Layers.Add(BuildLineLayer(line, slot));
                    break;
                case SldPolygonSymbolizer polygon:
                    BuildPolygonLayers(polygon, slot, accumulator.Layers);
                    break;
                case SldTextSymbolizer text:
                    accumulator.Layers.Add(BuildTextLayer(text, slot));
                    break;
            }
        }
    }

    private static MapLibreStyleLayer BuildPointLayer(SldPointSymbolizer point, SymbolizerSlot slot)
    {
        if (point.ExternalGraphic != null && point.Mark == null)
        {
            return BuildExternalGraphicLayer(point, slot);
        }

        if (point.Mark?.WellKnownName is { Length: > 0 } well && SupportedSymbolMarks.Contains(well))
        {
            return BuildWellKnownMarkSymbolLayer(point, well, slot);
        }

        return BuildCircleLayer(point, slot);
    }

    private static MapLibreStyleLayer BuildExternalGraphicLayer(SldPointSymbolizer point, SymbolizerSlot slot)
    {
        var paint = new Dictionary<string, MapLibreExpression>();
        var layout = new Dictionary<string, MapLibreExpression>
        {
            ["icon-image"] = new MapLibreExpression(point.ExternalGraphic!.OnlineResourceHref ?? string.Empty)
        };

        if (point.Size != null)
        {
            var sizeDesc = point.Size is SldLiteralValue lit ? lit.Value : "expression";
            slot.Context.Diagnostics.Add(Warn(
                "Graphic.Size",
                $"SLD Graphic Size ({sizeDesc}) is in pixels; MapLibre icon-size is a scale factor. icon-size omitted — provide sprite metadata to set the scale factor.",
                slot.Context.RuleName));
        }

        if (SldExpressionConverter.ConvertValue(point.Rotation, SldValueTargetKind.Number, slot.Context) is { } rotate)
        {
            layout["icon-rotate"] = rotate;
        }

        if (SldExpressionConverter.ConvertValue(point.Opacity, SldValueTargetKind.Opacity, slot.Context) is { } opacity)
        {
            paint["icon-opacity"] = opacity;
        }

        return CreateLayer(slot, new LayerSpec { Type = "symbol", Paint = paint.Count > 0 ? paint : null, Layout = layout });
    }

    private static MapLibreStyleLayer BuildWellKnownMarkSymbolLayer(
        SldPointSymbolizer point,
        string wellKnownName,
        SymbolizerSlot slot)
    {
        var iconName = $"{SldSharedConstants.WellKnownMarkPrefix}{wellKnownName.ToLowerInvariant()}";
        slot.Context.Warn(
            "Mark.WellKnownName",
            $"WellKnownName '{wellKnownName}' was mapped to symbol icon-image '{iconName}'; supply that sprite in the client style to render the mark.");
        var layout = new Dictionary<string, MapLibreExpression>
        {
            ["icon-image"] = new MapLibreExpression(iconName)
        };
        var paint = new Dictionary<string, MapLibreExpression>();

        if (SldExpressionConverter.ConvertValue(point.Size, SldValueTargetKind.Number, slot.Context) is { } sizeExpr)
        {
            layout["icon-size"] = ScaleIconSize(sizeExpr);
        }

        if (SldExpressionConverter.ConvertValue(point.Rotation, SldValueTargetKind.Number, slot.Context) is { } rotate)
        {
            layout["icon-rotate"] = rotate;
        }

        var colorSource = point.Mark?.Fill?.Color ?? point.Mark?.Stroke?.Color;
        if (SldExpressionConverter.ConvertValue(colorSource, SldValueTargetKind.Color, slot.Context) is { } color)
        {
            paint["icon-color"] = color;
        }

        var opacitySource = point.Mark?.Fill?.Opacity ?? point.Opacity;
        if (SldExpressionConverter.ConvertValue(opacitySource, SldValueTargetKind.Opacity, slot.Context) is { } opacity)
        {
            paint["icon-opacity"] = opacity;
        }

        return CreateLayer(slot, new LayerSpec { Type = "symbol", Paint = paint.Count > 0 ? paint : null, Layout = layout });
    }

    private static MapLibreExpression ScaleIconSize(MapLibreExpression sizeExpr)
    {
        return sizeExpr.Kind == MapLibreExpressionKind.Number
            ? new MapLibreExpression(sizeExpr.NumberValue / SldSharedConstants.WellKnownMarkBaseSizePx)
            : SldExpressionConverter.Expr("/", sizeExpr, new MapLibreExpression(SldSharedConstants.WellKnownMarkBaseSizePx));
    }

    private static MapLibreStyleLayer BuildCircleLayer(SldPointSymbolizer point, SymbolizerSlot slot)
    {
        var paint = new Dictionary<string, MapLibreExpression>
        {
            ["circle-radius"] = SldExpressionConverter.ConvertValue(point.Size, SldValueTargetKind.HalfNumber, slot.Context)
                ?? new MapLibreExpression(5d)
        };

        AddIfPresent(paint, "circle-color", SldExpressionConverter.ConvertValue(point.Mark?.Fill?.Color, SldValueTargetKind.Color, slot.Context));
        AddIfPresent(paint, "circle-opacity", SldExpressionConverter.ConvertValue(point.Mark?.Fill?.Opacity ?? point.Opacity, SldValueTargetKind.Opacity, slot.Context));
        AddIfPresent(paint, "circle-stroke-color", SldExpressionConverter.ConvertValue(point.Mark?.Stroke?.Color, SldValueTargetKind.Color, slot.Context));
        AddIfPresent(paint, "circle-stroke-opacity", SldExpressionConverter.ConvertValue(point.Mark?.Stroke?.Opacity, SldValueTargetKind.Opacity, slot.Context));
        AddIfPresent(paint, "circle-stroke-width", SldExpressionConverter.ConvertValue(point.Mark?.Stroke?.Width, SldValueTargetKind.Number, slot.Context));

        if (point.Mark?.WellKnownName is { Length: > 0 } well
            && !string.Equals(well, "circle", StringComparison.OrdinalIgnoreCase))
        {
            slot.Context.Diagnostics.Add(Warn(
                "Mark.WellKnownName",
                $"WellKnownName '{well}' is rendered as a generic circle; MapLibre requires a sprite for shape support.",
                slot.Context.RuleName));
        }

        return CreateLayer(slot, new LayerSpec { Type = "circle", Paint = paint });
    }

    private static MapLibreStyleLayer BuildLineLayer(SldLineSymbolizer line, SymbolizerSlot slot)
    {
        var paint = new Dictionary<string, MapLibreExpression>();
        AddIfPresent(paint, "line-color", SldExpressionConverter.ConvertValue(line.Stroke.Color, SldValueTargetKind.Color, slot.Context));
        AddIfPresent(paint, "line-width", SldExpressionConverter.ConvertValue(line.Stroke.Width, SldValueTargetKind.Number, slot.Context));
        AddIfPresent(paint, "line-opacity", SldExpressionConverter.ConvertValue(line.Stroke.Opacity, SldValueTargetKind.Opacity, slot.Context));

        if (line.Stroke.DashArray is { Length: > 0 } dashArray)
        {
            paint["line-dasharray"] = ToExpressionArray(dashArray);
        }

        Dictionary<string, MapLibreExpression>? layout = null;
        if (!string.IsNullOrEmpty(line.Stroke.LineCap))
        {
            layout = new Dictionary<string, MapLibreExpression> { ["line-cap"] = new MapLibreExpression(line.Stroke.LineCap) };
        }

        if (!string.IsNullOrEmpty(line.Stroke.LineJoin))
        {
            layout ??= new Dictionary<string, MapLibreExpression>();
            layout["line-join"] = new MapLibreExpression(line.Stroke.LineJoin);
        }

        return CreateLayer(slot, new LayerSpec { Type = "line", Paint = paint, Layout = layout });
    }

    private static void BuildPolygonLayers(SldPolygonSymbolizer polygon, SymbolizerSlot slot, List<MapLibreStyleLayer> layers)
    {
        if (polygon.Fill != null)
        {
            var fillPaint = new Dictionary<string, MapLibreExpression>();
            AddIfPresent(fillPaint, "fill-color", SldExpressionConverter.ConvertValue(polygon.Fill.Color, SldValueTargetKind.Color, slot.Context));
            AddIfPresent(fillPaint, "fill-opacity", SldExpressionConverter.ConvertValue(polygon.Fill.Opacity, SldValueTargetKind.Opacity, slot.Context));
            layers.Add(CreateLayer(slot, new LayerSpec { Type = "fill", Paint = fillPaint.Count > 0 ? fillPaint : null }));
        }

        if (polygon.Stroke is not { } stroke)
        {
            return;
        }

        var linePaint = new Dictionary<string, MapLibreExpression>
        {
            ["line-width"] = SldExpressionConverter.ConvertValue(stroke.Width, SldValueTargetKind.Number, slot.Context)
                ?? new MapLibreExpression(1d)
        };
        AddIfPresent(linePaint, "line-color", SldExpressionConverter.ConvertValue(stroke.Color, SldValueTargetKind.Color, slot.Context));
        AddIfPresent(linePaint, "line-opacity", SldExpressionConverter.ConvertValue(stroke.Opacity, SldValueTargetKind.Opacity, slot.Context));
        if (stroke.DashArray is { Length: > 0 } dashArray)
        {
            linePaint["line-dasharray"] = ToExpressionArray(dashArray);
        }

        var suffix = polygon.Fill != null ? "-outline" : string.Empty;
        layers.Add(CreateLayer(slot, new LayerSpec { Type = "line", Paint = linePaint, IdSuffix = suffix }));
    }

    private static MapLibreStyleLayer BuildTextLayer(SldTextSymbolizer text, SymbolizerSlot slot)
    {
        var layout = new Dictionary<string, MapLibreExpression>();
        var paint = new Dictionary<string, MapLibreExpression>();

        if (!string.IsNullOrEmpty(text.Label))
        {
            layout["text-field"] = new MapLibreExpression(text.Label);
        }
        else
        {
            slot.Context.Diagnostics.Add(Warn(
                "TextSymbolizer.Label",
                "TextSymbolizer is missing a Label expression; text layer will not render.",
                slot.Context.RuleName));
        }

        ApplyTextFontAndPlacement(text, layout, slot.Context);
        ApplyTextFillAndHalo(text, paint, slot.Context);

        return CreateLayer(slot, new LayerSpec { Type = "symbol", Paint = paint.Count > 0 ? paint : null, Layout = layout });
    }

    private static void ApplyTextFontAndPlacement(
        SldTextSymbolizer text,
        Dictionary<string, MapLibreExpression> layout,
        SldDiagnosticContext context)
    {
        if (text.Font?.Family is { Length: > 0 } family)
        {
            layout["text-font"] = new MapLibreExpression(new[] { new MapLibreExpression(family) });
        }

        AddIfPresent(layout, "text-size", SldExpressionConverter.ConvertValue(text.Font?.Size, SldValueTargetKind.Number, context));
        if (text.Placement is not { } placement)
        {
            return;
        }

        if (placement.Kind == SldPlacementKind.Line)
        {
            layout["symbol-placement"] = new MapLibreExpression("line");
            return;
        }

        if (!string.IsNullOrEmpty(placement.TextAnchor))
        {
            layout["text-anchor"] = new MapLibreExpression(placement.TextAnchor);
        }

        if (placement.Offset is { Length: 2 } offset)
        {
            var emScale = ResolveFontSizePx(text.Font?.Size);
            layout["text-offset"] = ToExpressionArray([offset[0] / emScale, offset[1] / emScale]);
        }

        AddIfPresent(layout, "text-rotate", SldExpressionConverter.ConvertValue(placement.Rotation, SldValueTargetKind.Number, context));
    }

    private static double ResolveFontSizePx(SldValueExpression? size)
    {
        return size is SldLiteralValue lit
            && double.TryParse(lit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0d
            ? parsed
            : SldSharedConstants.DefaultFontSizePx;
    }

    private static void ApplyTextFillAndHalo(
        SldTextSymbolizer text,
        Dictionary<string, MapLibreExpression> paint,
        SldDiagnosticContext context)
    {
        AddIfPresent(paint, "text-color", SldExpressionConverter.ConvertValue(text.Fill?.Color, SldValueTargetKind.Color, context));
        AddIfPresent(paint, "text-opacity", SldExpressionConverter.ConvertValue(text.Fill?.Opacity, SldValueTargetKind.Opacity, context));
        if (text.Halo == null)
        {
            return;
        }

        AddIfPresent(paint, "text-halo-width", SldExpressionConverter.ConvertValue(text.Halo.Radius, SldValueTargetKind.Number, context));
        if (text.Halo.Fill?.Color is SldLiteralValue haloColorLit)
        {
            double? haloOpacity = text.Halo.Fill.Opacity is SldLiteralValue opLit
                && double.TryParse(opLit.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            paint["text-halo-color"] = new MapLibreExpression(SldExpressionConverter.NormalizeColor(haloColorLit.Value, haloOpacity));
        }
        else
        {
            AddIfPresent(paint, "text-halo-color", SldExpressionConverter.ConvertValue(text.Halo.Fill?.Color, SldValueTargetKind.Color, context));
        }
    }

    private static MapLibreExpression? BuildFilterExpression(SldFilter filter, SldDiagnosticContext context)
    {
        return filter switch
        {
            SldFilterAnd and => BuildCompoundFilter("all", and.Operands, context),
            SldFilterOr or => BuildCompoundFilter("any", or.Operands, context),
            SldFilterNot not => BuildFilterExpression(not.Operand, context) is { } inner
                ? SldExpressionConverter.Expr("!", inner)
                : null,
            SldFilterComparison comparison => BuildComparison(comparison),
            SldFilterLike like => SldLikeFilterConverter.Convert(like, context),
            SldFilterUnsupported => null,
            _ => null
        };
    }

    private static MapLibreExpression? BuildCompoundFilter(
        string op,
        IReadOnlyList<SldFilter> children,
        SldDiagnosticContext context)
    {
        if (children.Count == 0)
        {
            return null;
        }

        var operands = new List<MapLibreExpression> { new(op) };
        foreach (var child in children)
        {
            var converted = BuildFilterExpression(child, context);
            if (!converted.HasValue)
            {
                return null;
            }

            operands.Add(converted.Value);
        }

        return new MapLibreExpression(operands.ToArray());
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

        return SldExpressionConverter.Expr(
            op,
            SldExpressionConverter.Expr("get", new MapLibreExpression(comparison.PropertyName)),
            SldExpressionConverter.ParseLiteralExpression(comparison.Literal));
    }

    private static MapLibreStyleLayer CreateLayer(SymbolizerSlot slot, LayerSpec spec)
    {
        return new MapLibreStyleLayer
        {
            Id = $"{slot.Meta.RuleId}-{slot.Index}{spec.IdSuffix}",
            Type = spec.Type,
            Filter = slot.Meta.Filter,
            MinZoom = slot.Meta.MinZoom,
            MaxZoom = slot.Meta.MaxZoom,
            Paint = spec.Paint,
            Layout = spec.Layout
        };
    }

    private static void AddIfPresent(
        Dictionary<string, MapLibreExpression> target,
        string key,
        MapLibreExpression? value)
    {
        if (value.HasValue)
        {
            target[key] = value.Value;
        }
    }

    private static double? ScaleToZoom(double? scaleDenominator)
    {
        if (!scaleDenominator.HasValue || scaleDenominator.Value <= 0)
        {
            return null;
        }

        var zoom = Math.Log2(WebMercatorScaleReference / scaleDenominator.Value);
        return Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 2, MidpointRounding.AwayFromZero);
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

    private enum RuleFilterDisposition
    {
        Include,
        Omit
    }

    private enum ElseSiblingStatus
    {
        AllFiltered,
        HasScaledSibling,
        HasUnconditional,
        HasUnsupported
    }

    private sealed class ConversionAccumulator(IReadOnlyList<SldConversionDiagnostic> initialDiagnostics)
    {
        private int _ruleCounter;
        public List<MapLibreStyleLayer> Layers { get; } = [];
        public List<SldConversionDiagnostic> Diagnostics { get; } = new(initialDiagnostics);
        public int NextRuleIndex() => _ruleCounter++;
    }

    private sealed record ResolvedRuleFilter
    {
        public MapLibreExpression? Filter { get; init; }
        public RuleFilterDisposition Disposition { get; init; }
    }

    private sealed record ElseFilterState
    {
        public required List<MapLibreExpression> SiblingFilters { get; init; }
        public ElseSiblingStatus Status { get; init; }
    }

    private sealed record RuleLayerMetadata
    {
        public required string RuleId { get; init; }
        public string? RuleName { get; init; }
        public MapLibreExpression? Filter { get; init; }
        public double? MinZoom { get; init; }
        public double? MaxZoom { get; init; }
    }

    private sealed class SiblingFilterTracker
    {
        public List<MapLibreExpression> Filters { get; } = [];
        public ElseSiblingStatus Status { get; private set; } = ElseSiblingStatus.AllFiltered;

        public void Record(SldRule rule, ResolvedRuleFilter resolved)
        {
            var hasScale = rule.MinScaleDenominator.HasValue || rule.MaxScaleDenominator.HasValue;
            if (resolved.Disposition == RuleFilterDisposition.Omit)
            {
                Status = ElseSiblingStatus.HasUnsupported;
            }
            else if (resolved.Filter.HasValue)
            {
                Filters.Add(resolved.Filter.Value);
                if (hasScale && Status == ElseSiblingStatus.AllFiltered)
                {
                    Status = ElseSiblingStatus.HasScaledSibling;
                }
            }
            else if (!hasScale && Status != ElseSiblingStatus.HasUnsupported)
            {
                Status = ElseSiblingStatus.HasUnconditional;
            }
            else if (hasScale && Status == ElseSiblingStatus.AllFiltered)
            {
                Status = ElseSiblingStatus.HasScaledSibling;
            }
        }
    }

    private sealed record LayerSpec
    {
        public required string Type { get; init; }
        public Dictionary<string, MapLibreExpression>? Paint { get; init; }
        public Dictionary<string, MapLibreExpression>? Layout { get; init; }
        public string IdSuffix { get; init; } = string.Empty;
    }

    private sealed record SymbolizerSlot
    {
        public required RuleLayerMetadata Meta { get; init; }
        public int Index { get; init; }
        public SldDiagnosticContext Context { get; init; }
    }
}
