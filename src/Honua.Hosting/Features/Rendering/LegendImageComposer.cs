// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Composes classified legend entries into a single raster legend image.
/// </summary>
internal static class LegendImageComposer
{
    private const float EntryGap = 4f;
    private const float SwatchLabelGap = 6f;
    private const float LabelFontSize = 12f;
    private const float MaxLabelWidth = 320f;
    private const float OuterPadding = 4f;

    /// <summary>
    /// One legend row: the style layer to draw, and the class whose label is drawn
    /// beside the swatch and whose attributes resolve the symbology.
    /// </summary>
    internal readonly record struct LegendImageEntry(MapLibreStyleLayer StyleLayer, LegendClass Class);

    /// <summary>
    /// Renders stacked swatch/label rows into one image.
    ///
    /// Each swatch is drawn by <see cref="SkiaMapRenderer.DrawLegendSwatch"/> against the
    /// entry's synthetic attributes, so the symbology is produced by the same style
    /// resolution path GetMap uses.
    ///
    /// Labels are best-effort: where no usable typeface is available (the Lambda
    /// fontconfig/freetype surface from #1728) the swatch column still renders and the
    /// text is omitted rather than failing the request.
    /// </summary>
    internal static byte[] Compose(
        IReadOnlyList<LegendImageEntry> entries,
        MetadataV2GeometryType geometryType,
        int swatchWidth,
        int swatchHeight,
        SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var font = TryCreateLabelFont();
        var rowHeight = Math.Max(swatchHeight, font is null ? 0f : font.Size) + EntryGap;

        var labelWidth = 0f;
        if (font is not null)
        {
            foreach (var entry in entries)
            {
                labelWidth = Math.Max(labelWidth, Math.Min(font.MeasureText(entry.Class.Label), MaxLabelWidth));
            }
        }

        var imageWidth = (int)Math.Ceiling(
            (OuterPadding * 2) + swatchWidth + (labelWidth > 0 ? SwatchLabelGap + labelWidth : 0));
        var imageHeight = (int)Math.Ceiling((OuterPadding * 2) + (rowHeight * entries.Count));

        using var surface = SKSurface.Create(
            new SKImageInfo(Math.Max(imageWidth, 1), Math.Max(imageHeight, 1), SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException(
                $"Skia failed to allocate a render surface for the WMS legend at {imageWidth}x{imageHeight}.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var rowTop = OuterPadding + (i * rowHeight);

            canvas.Save();
            canvas.Translate(OuterPadding, rowTop);
            SkiaMapRenderer.DrawLegendSwatch(
                canvas,
                entry.StyleLayer,
                geometryType,
                swatchWidth,
                swatchHeight,
                entry.Class.Properties);
            canvas.Restore();

            if (font is null)
            {
                continue;
            }

            var baseline = rowTop + (swatchHeight / 2f) + (font.Size / 2f) - font.Metrics.Descent / 2f;
            canvas.DrawText(
                entry.Class.Label,
                OuterPadding + swatchWidth + SwatchLabelGap,
                baseline,
                SKTextAlign.Left,
                font,
                textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(format, 100);
        return data?.ToArray()
            ?? throw new InvalidOperationException("Skia failed to encode the WMS legend image.");
    }

    private static SKFont? TryCreateLabelFont()
    {
        try
        {
            var typeface = SKTypeface.Default;
            return typeface is null ? null : new SKFont(typeface, LabelFontSize);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException or DllNotFoundException)
        {
            return null;
        }
    }
}
