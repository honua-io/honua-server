// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.PrintingTools.Models;
using SkiaSharp;

namespace Honua.Server.Features.PrintingTools.Layout;

/// <summary>
/// Composes a print layout by rendering map frame and layout elements onto a Skia canvas
/// according to a layout template's slot geometry.
/// </summary>
internal static class LayoutComposer
{
    private const float DefaultFontSize = 10f;
    private const float TitleFontSize = 16f;
    private const float AttributionFontSize = 8f;

    /// <summary>
    /// Composes a full page layout onto the given canvas at the specified DPI.
    /// </summary>
    /// <param name="canvas">Target canvas to draw on.</param>
    /// <param name="template">Layout template defining element positions.</param>
    /// <param name="mapImageBytes">Pre-rendered map frame image (PNG bytes).</param>
    /// <param name="legendSwatches">Legend entries with label and swatch image.</param>
    /// <param name="titleText">Title text to display, null to omit.</param>
    /// <param name="attributionText">Attribution/copyright text.</param>
    /// <param name="scaleDenominator">Map scale denominator for scale bar.</param>
    /// <param name="dpi">Output DPI for scaling.</param>
    internal static void Compose(
        SKCanvas canvas,
        PrintLayoutTemplate template,
        byte[] mapImageBytes,
        IReadOnlyList<LegendSwatchEntry>? legendSwatches,
        string? titleText,
        string? attributionText,
        double scaleDenominator,
        int dpi)
    {
        var dpiScale = dpi / 72f;

        // White page background
        canvas.Clear(SKColors.White);

        // Draw map frame (skip border for map-only exports)
        DrawMapFrame(canvas, template.MapFrame, mapImageBytes, dpiScale, drawBorder: !template.IsMapOnly);

        // Draw layout elements (only for non-map-only templates)
        if (!template.IsMapOnly)
        {
            if (template.Title is not null && !string.IsNullOrWhiteSpace(titleText))
            {
                DrawTitle(canvas, template.Title, titleText, dpiScale);
            }

            if (template.Legend is not null && legendSwatches is { Count: > 0 })
            {
                DrawLegend(canvas, template.Legend, legendSwatches, dpiScale);
            }

            if (template.ScaleBar is not null && scaleDenominator > 0)
            {
                DrawScaleBar(canvas, template.ScaleBar, scaleDenominator, dpiScale);
            }

            if (template.NorthArrow is not null)
            {
                DrawNorthArrow(canvas, template.NorthArrow, dpiScale);
            }

            if (template.Attribution is not null && !string.IsNullOrWhiteSpace(attributionText))
            {
                DrawAttribution(canvas, template.Attribution, attributionText, dpiScale);
            }
        }
    }

    private static void DrawMapFrame(SKCanvas canvas, LayoutSlot slot, byte[] mapImageBytes, float dpiScale, bool drawBorder = true)
    {
        using var mapImage = SKImage.FromEncodedData(mapImageBytes);
        if (mapImage is null) return;

        var destRect = ScaleSlot(slot, dpiScale);
        canvas.DrawImage(mapImage, destRect);

        if (drawBorder)
        {
            // Draw thin border around map frame
            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.Black,
                StrokeWidth = 0.5f * dpiScale,
                IsAntialias = true
            };
            canvas.DrawRect(destRect, borderPaint);
        }
    }

    private static void DrawTitle(SKCanvas canvas, LayoutSlot slot, string titleText, float dpiScale)
    {
        var rect = ScaleSlot(slot, dpiScale);

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = TitleFontSize * dpiScale,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            Typeface = SKTypeface.Default
        };

        var textY = rect.MidY + paint.TextSize / 3f;
        canvas.DrawText(titleText, rect.MidX, textY, paint);
    }

    private static void DrawLegend(SKCanvas canvas, LayoutSlot slot, IReadOnlyList<LegendSwatchEntry> entries, float dpiScale)
    {
        var rect = ScaleSlot(slot, dpiScale);
        var swatchSize = 16f * dpiScale;
        var spacing = 4f * dpiScale;
        var textOffset = swatchSize + spacing;
        var lineHeight = swatchSize + spacing;

        // Legend header
        using var headerPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = DefaultFontSize * dpiScale,
            IsAntialias = true,
            FakeBoldText = true,
            Typeface = SKTypeface.Default
        };
        canvas.DrawText("Legend", rect.Left + spacing, rect.Top + headerPaint.TextSize + spacing, headerPaint);

        using var labelPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = (DefaultFontSize - 1f) * dpiScale,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        var currentY = rect.Top + lineHeight + spacing * 2;

        foreach (var entry in entries)
        {
            if (currentY + swatchSize > rect.Bottom) break;

            // Draw swatch
            if (entry.SwatchBytes is { Length: > 0 })
            {
                using var swatchImage = SKImage.FromEncodedData(entry.SwatchBytes);
                if (swatchImage is not null)
                {
                    var swatchRect = new SKRect(
                        rect.Left + spacing,
                        currentY,
                        rect.Left + spacing + swatchSize,
                        currentY + swatchSize);
                    canvas.DrawImage(swatchImage, swatchRect);
                }
            }

            // Draw label
            var labelY = currentY + swatchSize / 2f + labelPaint.TextSize / 3f;
            canvas.DrawText(entry.Label ?? "", rect.Left + textOffset + spacing * 2, labelY, labelPaint);

            currentY += lineHeight;
        }
    }

    private static void DrawScaleBar(SKCanvas canvas, LayoutSlot slot, double scaleDenominator, float dpiScale)
    {
        var rect = ScaleSlot(slot, dpiScale);

        // Calculate a round distance for the scale bar
        // Scale denominator = map distance / ground distance
        // At the given DPI, 1 inch on paper = scaleDenominator inches on ground
        var groundDistancePerInch = scaleDenominator / 12.0; // feet per inch at scale
        var barWidthInches = (rect.Width / (72f * dpiScale)); // bar width in inches
        var totalGroundFeet = groundDistancePerInch * barWidthInches;

        // Pick the largest "nice" round number that fits within 80% of the
        // scale bar slot so the label always matches the visual bar length.
        // FloorToNiceNumber guarantees the result never exceeds its input,
        // which keeps barFraction <= 0.8.
        var maxDisplayableFeet = totalGroundFeet * 0.8;
        string unit;
        double displayDistance;
        if (maxDisplayableFeet > 5280)
        {
            var maxMiles = maxDisplayableFeet / 5280.0;
            displayDistance = FloorToNiceNumber(maxMiles);
            unit = displayDistance == 1.0 ? "mile" : "miles";
        }
        else if (maxDisplayableFeet > 100)
        {
            displayDistance = FloorToNiceNumber(maxDisplayableFeet);
            unit = "ft";
        }
        else
        {
            // Use meters for small scales
            var maxMeters = maxDisplayableFeet * 0.3048;
            displayDistance = FloorToNiceNumber(maxMeters);
            unit = "m";
        }

        // Calculate bar length proportional to the display distance.
        // Branch conditions must mirror the unit-selection thresholds above
        // (which use maxDisplayableFeet, not totalGroundFeet) so the bar
        // length formula always matches the unit of displayDistance.
        var barFraction = maxDisplayableFeet > 5280
            ? displayDistance * 5280.0 / totalGroundFeet
            : maxDisplayableFeet > 100
                ? displayDistance / totalGroundFeet
                : displayDistance / (totalGroundFeet * 0.3048);
        var barLength = (float)(rect.Width * Math.Min(barFraction, 0.8));

        var barY = rect.MidY;
        var barHeight = 4f * dpiScale;

        // Draw bar segments (alternating black/white)
        var segmentCount = 4;
        var segmentWidth = barLength / segmentCount;
        for (var i = 0; i < segmentCount; i++)
        {
            using var segPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = i % 2 == 0 ? SKColors.Black : SKColors.White,
                IsAntialias = true
            };
            canvas.DrawRect(rect.Left + i * segmentWidth, barY - barHeight / 2, segmentWidth, barHeight, segPaint);
        }

        // Draw border around entire bar
        using var borderPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = 0.5f * dpiScale,
            IsAntialias = true
        };
        canvas.DrawRect(rect.Left, barY - barHeight / 2, barLength, barHeight, borderPaint);

        // Draw distance label
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = AttributionFontSize * dpiScale,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        var label = $"{displayDistance:G} {unit}";
        canvas.DrawText("0", rect.Left, barY - barHeight / 2 - 2 * dpiScale, textPaint);
        canvas.DrawText(label, rect.Left + barLength + 2 * dpiScale, barY + textPaint.TextSize / 3f, textPaint);
    }

    private static void DrawNorthArrow(SKCanvas canvas, LayoutSlot slot, float dpiScale)
    {
        var rect = ScaleSlot(slot, dpiScale);
        var cx = rect.MidX;
        var cy = rect.MidY;
        var size = Math.Min(rect.Width, rect.Height) * 0.4f;

        // Draw arrow pointing north (up)
        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.Black,
            IsAntialias = true
        };

        using var whitePaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
            IsAntialias = true
        };

        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = 1f * dpiScale,
            IsAntialias = true
        };

        // Left half (filled black)
        using var leftPath = new SKPath();
        leftPath.MoveTo(cx, cy - size);       // Top (north)
        leftPath.LineTo(cx - size * 0.35f, cy + size * 0.5f); // Bottom left
        leftPath.LineTo(cx, cy + size * 0.15f); // Center
        leftPath.Close();
        canvas.DrawPath(leftPath, fillPaint);

        // Right half (white with outline)
        using var rightPath = new SKPath();
        rightPath.MoveTo(cx, cy - size);       // Top (north)
        rightPath.LineTo(cx + size * 0.35f, cy + size * 0.5f); // Bottom right
        rightPath.LineTo(cx, cy + size * 0.15f); // Center
        rightPath.Close();
        canvas.DrawPath(rightPath, whitePaint);
        canvas.DrawPath(rightPath, strokePaint);

        // "N" label
        using var textPaint = new SKPaint
        {
            Color = SKColors.Black,
            TextSize = 10f * dpiScale,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            FakeBoldText = true,
            Typeface = SKTypeface.Default
        };
        canvas.DrawText("N", cx, cy - size - 2 * dpiScale, textPaint);
    }

    private static void DrawAttribution(SKCanvas canvas, LayoutSlot slot, string text, float dpiScale)
    {
        var rect = ScaleSlot(slot, dpiScale);

        using var paint = new SKPaint
        {
            Color = new SKColor(100, 100, 100),
            TextSize = AttributionFontSize * dpiScale,
            IsAntialias = true,
            Typeface = SKTypeface.Default
        };

        var textY = rect.MidY + paint.TextSize / 3f;
        canvas.DrawText(text, rect.Left, textY, paint);
    }

    private static SKRect ScaleSlot(LayoutSlot slot, float dpiScale) =>
        new(slot.X * dpiScale, slot.Y * dpiScale,
            (slot.X + slot.Width) * dpiScale, (slot.Y + slot.Height) * dpiScale);

    /// <summary>
    /// Returns the largest value in {1, 2, 5} × 10^n that does not exceed <paramref name="value"/>.
    /// Used by the scale bar to guarantee the label never exceeds the visual bar length.
    /// </summary>
    private static double FloorToNiceNumber(double value)
    {
        if (value <= 0) return 1;
        var exponent = Math.Floor(Math.Log10(value));
        var mantissa = value / Math.Pow(10, exponent);
        double nice = mantissa switch
        {
            >= 5 => 5,
            >= 2 => 2,
            _ => 1
        };
        return nice * Math.Pow(10, exponent);
    }
}

/// <summary>
/// A legend swatch with label and image bytes.
/// </summary>
internal sealed class LegendSwatchEntry
{
    /// <summary>
    /// Display label for this legend entry.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// PNG swatch image bytes.
    /// </summary>
    public byte[]? SwatchBytes { get; init; }
}
