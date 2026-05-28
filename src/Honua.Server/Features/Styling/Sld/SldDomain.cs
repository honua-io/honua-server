// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Styling.Sld;

/// <summary>
/// Internal SLD/SE document tree captured by <see cref="SldParser"/>.
/// </summary>
internal sealed record SldDocument(
    SldVersion Version,
    IReadOnlyList<SldNamedLayer> NamedLayers,
    IReadOnlyList<SldConversionDiagnostic> ParseDiagnostics);

/// <summary>
/// SLD specification version detected from the root element.
/// </summary>
internal enum SldVersion
{
    Sld10,
    Sld11
}

internal sealed record SldNamedLayer(string? Name, IReadOnlyList<SldUserStyle> UserStyles);

internal sealed record SldUserStyle(string? Name, IReadOnlyList<SldFeatureTypeStyle> FeatureTypeStyles);

internal sealed record SldFeatureTypeStyle(string? Name, IReadOnlyList<SldRule> Rules);

internal sealed record SldRule(
    string? Name,
    SldFilter? Filter,
    double? MinScaleDenominator,
    double? MaxScaleDenominator,
    IReadOnlyList<SldSymbolizer> Symbolizers);

internal abstract record SldSymbolizer;

internal sealed record SldPointSymbolizer(
    SldMark? Mark,
    SldExternalGraphic? ExternalGraphic,
    double? Size,
    double? Opacity) : SldSymbolizer;

internal sealed record SldLineSymbolizer(SldStroke Stroke) : SldSymbolizer;

internal sealed record SldPolygonSymbolizer(SldFill? Fill, SldStroke? Stroke) : SldSymbolizer;

internal sealed record SldTextSymbolizer(
    string? Label,
    SldFont? Font,
    SldFill? Fill,
    SldHalo? Halo) : SldSymbolizer;

internal sealed record SldMark(string? WellKnownName, SldFill? Fill, SldStroke? Stroke);

internal sealed record SldExternalGraphic(string? OnlineResourceHref, string? Format);

internal sealed record SldFill(string? Color, double? Opacity);

internal sealed record SldStroke(
    string? Color,
    double? Opacity,
    double? Width,
    string? LineCap,
    string? LineJoin,
    double[]? DashArray);

internal sealed record SldFont(string? Family, double? Size, string? Style, string? Weight);

internal sealed record SldHalo(double? Radius, SldFill? Fill);

/// <summary>
/// Parsed OGC Filter abstract syntax tree.
/// </summary>
internal abstract record SldFilter;

internal sealed record SldFilterAnd(IReadOnlyList<SldFilter> Operands) : SldFilter;

internal sealed record SldFilterOr(IReadOnlyList<SldFilter> Operands) : SldFilter;

internal sealed record SldFilterNot(SldFilter Operand) : SldFilter;

internal sealed record SldFilterComparison(
    SldFilterComparisonOperator Operator,
    string PropertyName,
    string Literal) : SldFilter;

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
