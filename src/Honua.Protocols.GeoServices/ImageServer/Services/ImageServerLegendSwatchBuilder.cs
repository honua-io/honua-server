// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using SkiaSharp;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal interface IImageServerLegendSwatchBuilder
{
    LegendSwatch Render(SKColor color);
}

internal readonly record struct LegendSwatch(string Base64Png, int Width, int Height);

internal sealed class ImageServerLegendSwatchBuilder : IImageServerLegendSwatchBuilder
{
    public const int SwatchWidth = 20;

    public const int SwatchHeight = 20;

    public LegendSwatch Render(SKColor color)
    {
        var info = new SKImageInfo(SwatchWidth, SwatchHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"Unable to allocate {SwatchWidth}x{SwatchHeight} swatch surface."));

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = color,
            IsAntialias = true,
        })
        {
            canvas.DrawRect(new SKRect(0, 0, SwatchWidth, SwatchHeight), fill);
        }

        using (var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = 1f,
            IsAntialias = true,
        })
        {
            canvas.DrawRect(new SKRect(0.5f, 0.5f, SwatchWidth - 0.5f, SwatchHeight - 0.5f), stroke);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return new LegendSwatch(Convert.ToBase64String(data.AsSpan()), SwatchWidth, SwatchHeight);
    }
}
