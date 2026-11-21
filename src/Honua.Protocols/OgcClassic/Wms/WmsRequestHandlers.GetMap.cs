// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic;
using Honua.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

internal static partial class WmsRequestHandlers
{
    private static async Task<IResult> HandleWmsGetMap(
        HttpContext context,
        MetadataV2Service service,
        WmsLayer[] accessibleLayers,
        string serviceId,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        OgcClassicLog.WmsRequested(logger, serviceId, "GetMap");
        var stopwatch = Stopwatch.StartNew();
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapRender, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcMaps);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wms-getmap");

        var query = context.Request.Query;
        if (!TryGetRequiredQueryValue(query, "LAYERS", out var layersParam))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "LAYERS parameter is required.");
        }

        if (!TryGetRequiredQueryValue(query, "STYLES", out var stylesParam, allowEmpty: true))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "STYLES parameter is required.");
        }

        if (!TryGetRequiredQueryValue(query, "BBOX", out var bboxValue))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Invalid BBOX parameter. Expected format: xmin,ymin,xmax,ymax.");
        }

        if (!TryGetRequiredWmsVersion(query, out var versionValue))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "VERSION parameter is required.");
        }

        if (!IsSupportedWmsVersion(versionValue))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Unsupported VERSION value. Supported values are 1.3.0 and 1.1.1.");
        }

        var crsValue = GetQueryValue(query, "CRS");
        if (string.IsNullOrWhiteSpace(crsValue))
        {
            crsValue = GetQueryValue(query, "SRS");
        }

        if (!TryParseWmsCrs(crsValue, out var requestSrid, out var normalizedCrs))
        {
            return CreateWmsServiceException(context, "InvalidCRS", "Invalid or missing CRS/SRS parameter.");
        }

        if (!TryParseWmsBbox(bboxValue, normalizedCrs, versionValue, out var requestedExtent))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Invalid BBOX parameter. Expected format: xmin,ymin,xmax,ymax.");
        }

        // Only short-circuit to a blank image when the requested bbox does NOT
        // intersect the CRS bounds at all. A partial overlap (e.g. a longitude
        // range that extends past ±180) still renders: the feature query filters
        // to in-bounds geometry and the canvas is georeferenced to the full
        // requested bbox, so the out-of-bounds margin simply paints as background.
        var bboxOutsideCrsBounds = !DoesExtentIntersectCrsBounds(requestedExtent, normalizedCrs);

        if (!TryGetRequiredQueryValue(query, "WIDTH", out var widthValue) ||
            !int.TryParse(widthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageWidth) ||
            imageWidth <= 0 || imageWidth > WmsMaxImageDimension)
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", $"WIDTH must be an integer between 1 and {WmsMaxImageDimension.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!TryGetRequiredQueryValue(query, "HEIGHT", out var heightValue) ||
            !int.TryParse(heightValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageHeight) ||
            imageHeight <= 0 || imageHeight > WmsMaxImageDimension)
        {
            return CreateWmsServiceException(context, "InvalidDimensionValue", $"HEIGHT must be an integer between 1 and {WmsMaxImageDimension.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!TryGetRequiredQueryValue(query, "FORMAT", out var formatValue))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "FORMAT parameter is required.");
        }

        if (!TryNormalizeWmsMapFormat(formatValue, out var imageFormat, out var contentType))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "FORMAT must be image/png, image/png8, image/jpeg, or image/webp.");
        }

        if (!TryParseWmsFormatOptionsDpi(GetQueryValue(query, "FORMAT_OPTIONS"), out var requestedDpi, out var formatOptionsError))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", formatOptionsError);
        }

        var exceptionsValue = GetQueryValue(query, "EXCEPTIONS");
        if (!IsSupportedWmsExceptionFormat(exceptionsValue))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "Unsupported EXCEPTIONS format. Only XML exceptions are supported.");
        }

        if (!TryParseWmsTransparent(GetQueryValue(query, "TRANSPARENT"), out var transparent))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "TRANSPARENT must be TRUE or FALSE.");
        }

        var backgroundColor = SKColors.White;
        var backgroundValue = GetQueryValue(query, "BGCOLOR");
        if (!string.IsNullOrWhiteSpace(backgroundValue) &&
            !TryParseWmsBackgroundColor(backgroundValue, out backgroundColor))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "BGCOLOR must be formatted as 0xRRGGBB or #RRGGBB.");
        }

        if (!TryParseCsvTokens(layersParam, allowEmptyTokens: false, out var layerTokens))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "LAYERS must contain at least one layer name.");
        }

        var layerCountError = ValidateWmsLayerTokenCount(context, "LAYERS", layerTokens.Length, GetWmsMaxLayersPerRequest(context));
        if (layerCountError != null)
        {
            return layerCountError;
        }

        if (!TryResolveWmsRequestedLayers(accessibleLayers, layerTokens, out var renderLayers, out var unresolvedLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedLayer) ? "requested layer" : unresolvedLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        if (!ValidateWmsStyles(stylesParam, renderLayers.Length, out var styleError))
        {
            return CreateWmsServiceException(context, "StyleNotDefined", styleError);
        }

        var filterResult = TryParseWmsLayerFilters(context, query, renderLayers);
        if (filterResult.Error != null)
        {
            return filterResult.Error;
        }
        // STYLES and FILTER are positional against the requested LAYERS list, so they are
        // validated above before repeated storage layers collapse to their first occurrence.
        // Each surviving layer is queried and drawn once.
        (renderLayers, var layerFilters) = DeduplicateWmsLayers(renderLayers, filterResult.Filters);
        activity?.SetTag("wms.filter_applied", layerFilters is not null);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var temporalResult = await TryResolveWmsLayerTemporalFiltersAsync(
            context,
            query,
            renderLayers,
            featureReader,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (temporalResult.Error != null)
        {
            return temporalResult.Error;
        }
        var layerTemporalFilters = temporalResult.Filters;
        AppendWmsWarnings(context, temporalResult.Warnings);
        activity?.SetTag("wms.time_applied", layerTemporalFilters is not null);

        var effectiveTransparent = transparent && !string.Equals(imageFormat, "jpg", StringComparison.OrdinalIgnoreCase);
        await using var renderLease = await context.RequestServices
            .GetRequiredService<RasterRenderCapacityLimiter>()
            .TryAcquireAsync(imageWidth, imageHeight, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is null)
        {
            return CreateWmsServiceException(context,
                "NoApplicableCode",
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                StatusCodes.Status503ServiceUnavailable);
        }

        if (bboxOutsideCrsBounds)
        {
            using var outsideSurface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (outsideSurface is null)
            {
                return CreateWmsServiceException(context, "NoApplicableCode", "Failed to allocate render surface.", StatusCodes.Status500InternalServerError);
            }

            outsideSurface.Canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor);
            var outsideBytes = SkiaMapRenderer.EncodeSurface(outsideSurface, imageFormat);
            return Results.Bytes(outsideBytes, contentType);
        }

        // Resolve the source SRID per requested layer: the V2 graph carries spatial
        // metadata on the service and on each resource, and they can diverge for
        // services that aggregate resources from heterogeneous CRSes. Walk the
        // request once and group layers by their effective rendering SRID so the
        // single canvas can apply per-group reprojection of the request bbox into
        // the storage CRS the feature reader expects.
        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return CreateWmsServiceException(context, "NoApplicableCode", "Failed to allocate render surface.", StatusCodes.Status500InternalServerError);
        }

        var styleSources = new RasterStyleSources(
            context.RequestServices.GetRequiredService<ILayerStyleCatalog>(),
            context.RequestServices.GetService<IGeoServicesStyleConverter>());
        var maxFeatures = MaxFeaturesPerLayer;
        var totalFeatureCount = 0;

        var canvas = surface.Canvas;
        canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor);

        var hasDpiScaling = requestedDpi.HasValue;
        var dpiScale = hasDpiScaling ? requestedDpi!.Value / DefaultWmsDpi : 1.0;
        Func<double, double, SKPoint> transformFn;
        int zoomImageWidth;
        int zoomImageHeight;

        if (hasDpiScaling)
        {
            var logicalWidth = imageWidth / dpiScale;
            var logicalHeight = imageHeight / dpiScale;
            canvas.Save();
            canvas.Scale((float)dpiScale, (float)dpiScale);
            transformFn = SkiaMapRenderer.BuildTransform(requestedExtent, logicalWidth, logicalHeight);
            zoomImageWidth = Math.Max(1, (int)Math.Round(logicalWidth));
            zoomImageHeight = Math.Max(1, (int)Math.Round(logicalHeight));
        }
        else
        {
            transformFn = SkiaMapRenderer.BuildTransform(requestedExtent, imageWidth, imageHeight);
            zoomImageWidth = imageWidth;
            zoomImageHeight = imageHeight;
        }

        // BBOX + WIDTH/HEIGHT + CRS (and optional FORMAT_OPTIONS=dpi:) fix the scale the map is
        // drawn at, which is what a style's minzoom/maxzoom is authored against.
        var renderZoom = await DeriveRenderZoomAsync(
            context,
            requestedExtent,
            requestSrid,
            zoomImageWidth,
            zoomImageHeight,
            cancellationToken).ConfigureAwait(false);
        activity?.SetTag("wms.render_zoom", renderZoom.Level);

        for (var i = 0; i < renderLayers.Length; i++)
        {
            var layer = renderLayers[i];
            cancellationToken.ThrowIfCancellationRequested();

            if (OgcClassicRasterSupport.IsRasterResource(layer.Resource))
            {
                if (hasDpiScaling)
                {
                    canvas.Save();
                    canvas.ResetMatrix();
                }

                try
                {
                    totalFeatureCount += await TryDrawWmsRasterLayerAsync(
                        context,
                        (canvas, new(layer.StorageLayerId, requestedExtent, requestSrid, imageWidth, imageHeight)),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (hasDpiScaling)
                    {
                        canvas.Restore();
                    }
                }

                continue;
            }

            if (!OgcClassicRasterSupport.ResourceHasGeometry(layer.Resource))
            {
                continue;
            }

            // Per-layer source SRID — see ResolveServiceSrid for the resolution order.
            // The feature store stores geometries in this CRS, so the request bbox is
            // reprojected into it before the SpatialFilter is issued.
            var serviceSrid = ResolveServiceSrid(service, layer.Resource);
            var queryExtent = requestedExtent;
            if (requestSrid != serviceSrid)
            {
                var extentTransformResult = await TryTransformExtentAsync(
                    context,
                    requestedExtent,
                    requestSrid,
                    serviceSrid,
                    cancellationToken);
                if (!extentTransformResult.IsSuccess)
                {
                    return CreateWmsServiceException(context, "InvalidCRS", extentTransformResult.Error ?? "Invalid spatial reference.");
                }

                queryExtent = extentTransformResult.Extent;
            }

            var spatialFilter = CreateBboxSpatialFilter(queryExtent, serviceSrid);
            var geometryType = layer.Resource.ReadGeometryType();

            var stylePlan = await GetRasterStylePlanAsync(
                styleSources,
                layer.Resource,
                layer.StorageLayerId,
                cancellationToken).ConfigureAwait(false);
            if (!StyleTranslator.IsAnyLayerInZoomRange(stylePlan.StyleLayers, renderZoom))
            {
                continue;
            }

            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                serviceSrid,
                requestSrid,
                maxFeatures,
                layerFilters?[i],
                layerTemporalFilters?[i]);

            var layerReader = await ResolveLayerReaderAsync(
                context,
                layer,
                featureReader,
                cancellationToken).ConfigureAwait(false);
            if (!hasDpiScaling)
            {
                var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                    canvas,
                    layerReader,
                    layer.StorageLayerId,
                    geometryType,
                    stylePlan,
                    featureQuery,
                    requestedExtent,
                    imageWidth,
                    imageHeight,
                    transformFn,
                    cancellationToken).ConfigureAwait(false);
                if (renderedPointCount >= 0)
                {
                    totalFeatureCount += renderedPointCount;
                    continue;
                }
            }

            var features = await QueryRasterFeaturesAsync(layerReader, layer.StorageLayerId, featureQuery, cancellationToken);
            if (features.Length == 0)
            {
                continue;
            }

            totalFeatureCount += features.Length;
            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transformFn, geometryType, renderZoom);
        }

        if (hasDpiScaling)
        {
            canvas.Restore();
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, imageFormat);

        stopwatch.Stop();
        HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
        HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

        return Results.Bytes(imageBytes, contentType);
    }

    private static async Task<int> TryDrawWmsRasterLayerAsync(
        HttpContext context,
        (SKCanvas Canvas, (int StorageLayerId, SkiaMapRenderer.RenderExtent Extent, int RequestSrid, int Width, int Height) Input) target,
        CancellationToken cancellationToken)
    {
        var input = new OgcClassicRasterSupport.RasterMapRenderInput
        {
            StorageLayerId = target.Input.StorageLayerId,
            Extent = target.Input.Extent,
            RequestSrid = target.Input.RequestSrid,
            Width = target.Input.Width,
            Height = target.Input.Height
        };
        var rasterBytes = await OgcClassicRasterSupport.TryRenderRasterMapLayerAsync(
            context, input, cancellationToken).ConfigureAwait(false);
        if (rasterBytes is not { Length: > 0 })
        {
            return 0;
        }

        OgcClassicRasterSupport.DrawEncodedRaster(
            target.Canvas, rasterBytes, new SKRect(0, 0, input.Width, input.Height));
        return 1;
    }
}
