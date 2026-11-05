// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Rendering;

internal sealed class MapLibreStyleLayer
{
    private readonly string? _type;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type
    {
        get => IsHiddenByConstantVisibility() ? null : _type;
        init => _type = value;
    }

    [JsonPropertyName("source-layer")]
    public string? SourceLayer { get; init; }

    [JsonPropertyName("minzoom")]
    public double? MinZoom { get; init; }

    [JsonPropertyName("maxzoom")]
    public double? MaxZoom { get; init; }

    [JsonPropertyName("filter")]
    public MapLibreExpression? Filter { get; init; }

    [JsonPropertyName("paint")]
    public Dictionary<string, MapLibreExpression>? Paint { get; init; }

    [JsonPropertyName("layout")]
    public Dictionary<string, MapLibreExpression>? Layout { get; init; }

    private bool IsHiddenByConstantVisibility()
        => Layout != null
            && Layout.TryGetValue("visibility", out var visibility)
            && visibility.Kind == MapLibreExpressionKind.String
            && string.Equals(visibility.StringValue, "none", StringComparison.OrdinalIgnoreCase);
}

internal sealed class MapLibreStyleDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("layers")]
    public MapLibreStyleLayer[]? Layers { get; init; }
}

internal sealed class ResolvedFillStyle
{
    public SkiaSharp.SKColor FillColor { get; init; }

    public SkiaSharp.SKColor? OutlineColor { get; init; }

    public float OutlineWidth { get; init; } = 1f;

    public bool Antialias { get; init; } = true;
}

internal sealed class ResolvedLineStyle
{
    public SkiaSharp.SKColor LineColor { get; init; }

    public float LineWidth { get; init; } = 1f;

    public float LineOpacity { get; init; } = 1f;

    public float[]? DashArray { get; init; }

    public SkiaSharp.SKStrokeCap LineCap { get; init; } = SkiaSharp.SKStrokeCap.Butt;

    public SkiaSharp.SKStrokeJoin LineJoin { get; init; } = SkiaSharp.SKStrokeJoin.Miter;
}

internal sealed class ResolvedCircleStyle
{
    public float Radius { get; init; } = 5f;

    public SkiaSharp.SKColor FillColor { get; init; }

    public SkiaSharp.SKColor? StrokeColor { get; init; }

    public float StrokeWidth { get; init; } = 1f;
}
