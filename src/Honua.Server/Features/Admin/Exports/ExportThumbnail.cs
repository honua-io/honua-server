// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Rendering;
using SkiaSharp;

namespace Honua.Server.Features.Admin.Exports;

internal sealed record ExportThumbnailSource(IReadOnlyList<Feature> Features, double[] Bounds, MetadataV2GeometryType GeometryType);

// A square PNG of the captured features over their own extent, drawn in Web Mercator by the same
// renderer as static maps, with the layer's stored style (or the default paints without one).
internal static class ExportThumbnail
{
    internal const string FileName = "thumbnail.png";
    internal const string MediaType = "image/png";
    internal const int Size = 512;
    private const double PaddingRatio = 1.05;
    private const double MinimumHalfSpanMeters = 500;
    private const int WebMercatorSrid = 3857;

    // Static maps draw at most this many features per layer; a thumbnail needs no more.
    internal const int MaxFeatures = 10_000;

    public static byte[] Render(ExportThumbnailSource source, string? storedMapLibreStyle)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        var extent = SquareMercatorExtent(source.Bounds);
        var toPixel = SkiaMapRenderer.BuildTransform(extent, Size, Size);
        SKPoint Transform(double longitude, double latitude)
        {
            var (x, y) = WebMercatorMath.LonLatToWebMercator(longitude, latitude);
            return toPixel(x, y);
        }

        var styleLayers = StyleTranslator.ParseStyleLayers(storedMapLibreStyle);
        if (styleLayers.Length > 0)
        {
            SkiaMapRenderer.RenderWithStyles(surface.Canvas, Sample(source.Features), styleLayers, Transform,
                RenderZoom.FromExtent(extent, Size, Size, WebMercatorSrid));
        }
        else
        {
            SkiaMapRenderer.RenderWithDefaults(surface.Canvas, Sample(source.Features), Transform, source.GeometryType);
        }

        return SkiaMapRenderer.EncodeSurface(surface, "png");
    }

    // Every n-th feature, so a large layer still shows its whole spread.
    internal static IReadOnlyList<Feature> Sample(IReadOnlyList<Feature> features)
    {
        if (features.Count <= MaxFeatures)
        {
            return features;
        }

        var stride = (double)features.Count / MaxFeatures;
        return [.. Enumerable.Range(0, MaxFeatures).Select(index => features[(int)(index * stride)])];
    }

    // The bounds (west, south, east, north in WGS 84) as a padded square in Web Mercator metres.
    internal static SkiaMapRenderer.RenderExtent SquareMercatorExtent(IReadOnlyList<double> bounds)
    {
        var (minX, minY) = WebMercatorMath.LonLatToWebMercator(bounds[0], bounds[1]);
        var (maxX, maxY) = WebMercatorMath.LonLatToWebMercator(bounds[2], bounds[3]);
        var half = Math.Max(Math.Max(maxX - minX, maxY - minY) / 2 * PaddingRatio, MinimumHalfSpanMeters);
        var (centerX, centerY) = ((minX + maxX) / 2, (minY + maxY) / 2);
        return new SkiaMapRenderer.RenderExtent(centerX - half, centerY - half, centerX + half, centerY + half);
    }
}
