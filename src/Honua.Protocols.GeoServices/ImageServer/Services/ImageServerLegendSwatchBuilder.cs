// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using SkiaSharp;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Builds the per-class swatches that the Esri Image Server <c>legend</c>
/// endpoint returns. The MVP renders a constant-size PNG with a solid colour
/// and a 1px black border because raster legend swatches are documented to be
/// fixed dimensions across all classes.
/// </summary>
internal interface IImageServerLegendSwatchBuilder
{
    /// <summary>
    /// Renders a single legend swatch.
    /// </summary>
    /// <param name="color">Fill colour for the swatch.</param>
    /// <returns>Base64-encoded PNG bytes plus the swatch dimensions.</returns>
    LegendSwatch Render(SKColor color);
}

/// <summary>
/// Result of <see cref="IImageServerLegendSwatchBuilder.Render"/>.
/// </summary>
internal readonly record struct LegendSwatch(string Base64Png, int Width, int Height);

/// <summary>
/// Default <see cref="IImageServerLegendSwatchBuilder"/> backed by SkiaSharp.
/// </summary>
internal sealed class ImageServerLegendSwatchBuilder : IImageServerLegendSwatchBuilder
{
    /// <summary>Esri-default swatch width (pixels).</summary>
    public const int SwatchWidth = 20;

    /// <summary>Esri-default swatch height (pixels).</summary>
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
