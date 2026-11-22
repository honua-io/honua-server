// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Honua.Protocols.Ogc.Classic.Wms;

internal static partial class WmsRequestHandlers
{
    private const double DefaultWmsDpi = 0.0254 / SpatialConstants.PixelSizeMeters;
    private const double MaxWmsDpi = 1200.0;

    internal static (SqlFragment?[]? LayerFilters, IResult? Error) ParseWmsCqlLayerFilters(
        HttpContext context,
        WmsLayer[] layers,
        string cqlFilterParam)
    {
        var cqlTokens = OgcClassicCqlFilter.SplitTokens(cqlFilterParam);
        if (cqlTokens.Length != 1 && cqlTokens.Length != layers.Length)
        {
            return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                $"CQL_FILTER must contain exactly 1 or {layers.Length.ToString(CultureInfo.InvariantCulture)} filter(s), separated by semicolons, matching the number of LAYERS."));
        }

        var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var layerFilters = new SqlFragment?[layers.Length];
        for (var i = 0; i < layers.Length; i++)
        {
            var token = cqlTokens.Length == 1 ? cqlTokens[0] : cqlTokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var (filter, error) = TranslateWmsLayerCqlFilter(context, (filterExpressionService, layers[i].Resource), token);
            if (error is not null)
            {
                return (null, error);
            }

            layerFilters[i] = filter;
        }

        return (layerFilters, null);
    }

    internal static (SqlFragment? Filter, IResult? Error) TranslateWmsLayerCqlFilter(
        HttpContext context,
        (IFilterExpressionService Service, MetadataV2Object Resource) target,
        string cqlText)
    {
        var (filter, error) = OgcClassicCqlFilter.Translate(target.Service, target.Resource, cqlText);
        return error is null
            ? (filter, null)
            : (null, CreateWmsServiceException(context, "InvalidParameterValue", error));
    }

    internal static (double? Dpi, string? Error) ParseWmsFormatOptionsDpi(string? formatOptions)
    {
        if (string.IsNullOrWhiteSpace(formatOptions))
        {
            return (null, null);
        }

        double? dpi = null;
        var pairs = formatOptions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var (parsedDpi, error) = ParseSingleFormatOptionDpi(pair);
            if (error is not null)
            {
                return (null, error);
            }

            if (parsedDpi.HasValue)
            {
                dpi = parsedDpi;
            }
        }

        return (dpi, null);
    }

    private static (double? Dpi, string? Error) ParseSingleFormatOptionDpi(string pair)
    {
        var separatorIndex = pair.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == pair.Length - 1)
        {
            return (null, "FORMAT_OPTIONS entries must use key:value syntax separated by semicolons.");
        }

        var key = pair[..separatorIndex].Trim();
        var value = pair[(separatorIndex + 1)..].Trim();
        if (!string.Equals(key, "dpi", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDpi) ||
            !double.IsFinite(parsedDpi) ||
            parsedDpi < 1.0 ||
            parsedDpi > MaxWmsDpi)
        {
            return (null, $"FORMAT_OPTIONS dpi must be a number between 1 and {MaxWmsDpi.ToString(CultureInfo.InvariantCulture)}.");
        }

        return (parsedDpi, null);
    }

    private static (Func<double, double, SKPoint> Transform, int ZoomWidth, int ZoomHeight) PrepareDpiCanvas(
        SKCanvas canvas,
        (SkiaMapRenderer.RenderExtent Extent, int Width, int Height) viewport,
        double? requestedDpi)
    {
        if (!requestedDpi.HasValue)
        {
            return (SkiaMapRenderer.BuildTransform(viewport.Extent, viewport.Width, viewport.Height), viewport.Width, viewport.Height);
        }

        var dpiScale = requestedDpi.Value / DefaultWmsDpi;
        var logicalWidth = viewport.Width / dpiScale;
        var logicalHeight = viewport.Height / dpiScale;
        canvas.Scale((float)dpiScale, (float)dpiScale);
        return (
            SkiaMapRenderer.BuildTransform(viewport.Extent, logicalWidth, logicalHeight),
            Math.Max(1, (int)Math.Round(logicalWidth)),
            Math.Max(1, (int)Math.Round(logicalHeight)));
    }

    private static async Task<int> DrawWmsRasterLayerUnscaledAsync(
        HttpContext context,
        (SKCanvas Canvas, (int StorageLayerId, SkiaMapRenderer.RenderExtent Extent, int RequestSrid, int Width, int Height) Input) target,
        CancellationToken cancellationToken)
    {
        target.Canvas.Save();
        target.Canvas.ResetMatrix();
        try
        {
            return await TryDrawWmsRasterLayerAsync(context, target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            target.Canvas.Restore();
        }
    }
}
