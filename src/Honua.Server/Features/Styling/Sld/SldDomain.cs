// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Styling.Sld;

internal sealed record SldDocument(
    SldVersion Version,
    IReadOnlyList<SldNamedLayer> NamedLayers,
    IReadOnlyList<SldConversionDiagnostic> ParseDiagnostics);

internal enum SldVersion
{
    Sld10,
    Sld11
}

internal enum SldRuleKind
{
    Standard,
    ElseFilter
}

internal enum SldPlacementKind
{
    Point,
    Line
}

internal enum SldMatchCase
{
    Sensitive,
    Insensitive
}

internal sealed record SldNamedLayer(string? Name, IReadOnlyList<SldUserStyle> UserStyles);

internal sealed record SldUserStyle(string? Name, IReadOnlyList<SldFeatureTypeStyle> FeatureTypeStyles);

internal sealed record SldFeatureTypeStyle(string? Name, IReadOnlyList<SldRule> Rules);

internal sealed record SldRule
{
    public string? Name { get; init; }
    public SldFilter? Filter { get; init; }
    public SldRuleKind Kind { get; init; }
    public double? MinScaleDenominator { get; init; }
    public double? MaxScaleDenominator { get; init; }
    public IReadOnlyList<SldSymbolizer> Symbolizers { get; init; } = [];
}

internal abstract record SldSymbolizer;

internal sealed record SldPointSymbolizer : SldSymbolizer
{
    public SldMark? Mark { get; init; }
    public SldExternalGraphic? ExternalGraphic { get; init; }
    public SldValueExpression? Size { get; init; }
    public SldValueExpression? Opacity { get; init; }
    public SldValueExpression? Rotation { get; init; }
}

internal sealed record SldLineSymbolizer(SldStroke Stroke) : SldSymbolizer;

internal sealed record SldPolygonSymbolizer(SldFill? Fill, SldStroke? Stroke) : SldSymbolizer;

internal sealed record SldTextSymbolizer : SldSymbolizer
{
    public string? Label { get; init; }
    public SldFont? Font { get; init; }
    public SldFill? Fill { get; init; }
    public SldHalo? Halo { get; init; }
    public SldLabelPlacement? Placement { get; init; }
}

internal sealed record SldLabelPlacement
{
    public SldPlacementKind Kind { get; init; }
    public string? TextAnchor { get; init; }
    public double[]? Offset { get; init; }
    public SldValueExpression? Rotation { get; init; }
}

internal sealed record SldMark(string? WellKnownName, SldFill? Fill, SldStroke? Stroke);

internal sealed record SldExternalGraphic(string? OnlineResourceHref, string? Format);

internal sealed record SldFill(SldValueExpression? Color, SldValueExpression? Opacity);

internal sealed record SldStroke
{
    public SldValueExpression? Color { get; init; }
    public SldValueExpression? Opacity { get; init; }
    public SldValueExpression? Width { get; init; }
    public string? LineCap { get; init; }
    public string? LineJoin { get; init; }
    public double[]? DashArray { get; init; }
}

internal sealed record SldFont
{
    public string? Family { get; init; }
    public SldValueExpression? Size { get; init; }
    public string? Style { get; init; }
    public string? Weight { get; init; }
}

internal sealed record SldHalo(SldValueExpression? Radius, SldFill? Fill);

internal abstract record SldValueExpression;

internal sealed record SldLiteralValue(string Value) : SldValueExpression;

internal sealed record SldPropertyValue(string PropertyName) : SldValueExpression;

internal sealed record SldRecodeEntry(string Data, string Value);

internal sealed record SldRecodeExpression(
    string PropertyName,
    IReadOnlyList<SldRecodeEntry> Entries,
    string? FallbackValue) : SldValueExpression;

internal sealed record SldCategorizeStep(double Threshold, string Value);

internal sealed record SldCategorizeExpression : SldValueExpression
{
    public required string PropertyName { get; init; }
    public required string InitialValue { get; init; }
    public required IReadOnlyList<SldCategorizeStep> Steps { get; init; }
    public string? ThresholdsBelongTo { get; init; }
}

internal sealed record SldInterpolatePoint(double Data, string Value);

internal sealed record SldInterpolateExpression : SldValueExpression
{
    public required string PropertyName { get; init; }
    public required IReadOnlyList<SldInterpolatePoint> Points { get; init; }
    public string? Mode { get; init; }
    public string? Method { get; init; }
}

internal abstract record SldFilter;

internal sealed record SldFilterAnd(IReadOnlyList<SldFilter> Operands) : SldFilter;

internal sealed record SldFilterOr(IReadOnlyList<SldFilter> Operands) : SldFilter;

internal sealed record SldFilterNot(SldFilter Operand) : SldFilter;

internal sealed record SldFilterComparison(
    SldFilterComparisonOperator Operator,
    string PropertyName,
    string Literal) : SldFilter;

internal sealed record SldFilterLike : SldFilter
{
    public required string PropertyName { get; init; }
    public required string Pattern { get; init; }
    public char WildCard { get; init; }
    public char SingleChar { get; init; }
    public char EscapeChar { get; init; }
    public SldMatchCase MatchCase { get; init; }
}

internal sealed record SldFilterUnsupported(string Construct, string? Detail) : SldFilter;

internal enum SldFilterComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

internal readonly record struct SldDiagnosticContext(
    List<SldConversionDiagnostic> Diagnostics,
    string? RuleName)
{
    public void Warn(string construct, string message)
    {
        Diagnostics.Add(new SldConversionDiagnostic
        {
            Severity = SldDiagnosticSeverity.Warning,
            Construct = construct,
            Message = message,
            RuleName = RuleName
        });
    }
}

internal static class SldSharedConstants
{
    public const double WellKnownMarkBaseSizePx = 16d;
    public const double DefaultFontSizePx = 16d;
    public const string WellKnownMarkPrefix = "honua-mark-";
}
