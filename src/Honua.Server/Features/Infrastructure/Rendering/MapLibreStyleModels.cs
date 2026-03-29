// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Infrastructure.Rendering;

/// <summary>
/// Parsed MapLibre style layer definition.
/// </summary>
internal sealed class MapLibreStyleLayer
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Layer type: fill, line, circle, symbol, background.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Source layer name.
    /// </summary>
    [JsonPropertyName("source-layer")]
    public string? SourceLayer { get; init; }

    /// <summary>
    /// Minimum zoom level.
    /// </summary>
    [JsonPropertyName("minzoom")]
    public double? MinZoom { get; init; }

    /// <summary>
    /// Maximum zoom level.
    /// </summary>
    [JsonPropertyName("maxzoom")]
    public double? MaxZoom { get; init; }

    /// <summary>
    /// Filter expression.
    /// </summary>
    [JsonPropertyName("filter")]
    public MapLibreExpression? Filter { get; init; }

    /// <summary>
    /// Paint properties.
    /// </summary>
    [JsonPropertyName("paint")]
    public Dictionary<string, MapLibreExpression>? Paint { get; init; }

    /// <summary>
    /// Layout properties.
    /// </summary>
    [JsonPropertyName("layout")]
    public Dictionary<string, MapLibreExpression>? Layout { get; init; }
}

/// <summary>
/// Root MapLibre style document.
/// </summary>
internal sealed class MapLibreStyleDocument
{
    /// <summary>
    /// Style version (should be 8).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>
    /// Style name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Style layers.
    /// </summary>
    [JsonPropertyName("layers")]
    public MapLibreStyleLayer[]? Layers { get; init; }
}

/// <summary>
/// Resolved style properties ready for SkiaSharp rendering.
/// </summary>
internal sealed class ResolvedFillStyle
{
    /// <summary>
    /// Fill color with opacity applied.
    /// </summary>
    public SkiaSharp.SKColor FillColor { get; init; }

    /// <summary>
    /// Outline color.
    /// </summary>
    public SkiaSharp.SKColor? OutlineColor { get; init; }

    /// <summary>
    /// Outline width.
    /// </summary>
    public float OutlineWidth { get; init; } = 1f;

    /// <summary>
    /// Fill antialias.
    /// </summary>
    public bool Antialias { get; init; } = true;
}

/// <summary>
/// Resolved line style properties.
/// </summary>
internal sealed class ResolvedLineStyle
{
    /// <summary>
    /// Line color.
    /// </summary>
    public SkiaSharp.SKColor LineColor { get; init; }

    /// <summary>
    /// Line width.
    /// </summary>
    public float LineWidth { get; init; } = 1f;

    /// <summary>
    /// Line opacity.
    /// </summary>
    public float LineOpacity { get; init; } = 1f;

    /// <summary>
    /// Dash array pattern (alternating dash and gap lengths).
    /// </summary>
    public float[]? DashArray { get; init; }

    /// <summary>
    /// Line cap style.
    /// </summary>
    public SkiaSharp.SKStrokeCap LineCap { get; init; } = SkiaSharp.SKStrokeCap.Butt;

    /// <summary>
    /// Line join style.
    /// </summary>
    public SkiaSharp.SKStrokeJoin LineJoin { get; init; } = SkiaSharp.SKStrokeJoin.Miter;
}

/// <summary>
/// Resolved circle style properties.
/// </summary>
internal sealed class ResolvedCircleStyle
{
    /// <summary>
    /// Circle radius.
    /// </summary>
    public float Radius { get; init; } = 5f;

    /// <summary>
    /// Circle fill color.
    /// </summary>
    public SkiaSharp.SKColor FillColor { get; init; }

    /// <summary>
    /// Circle stroke color.
    /// </summary>
    public SkiaSharp.SKColor? StrokeColor { get; init; }

    /// <summary>
    /// Circle stroke width.
    /// </summary>
    public float StrokeWidth { get; init; } = 1f;
}
