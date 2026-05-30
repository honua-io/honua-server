// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

        if (!TryGetRequiredQueryValue(query, "VERSION", out var versionValue))
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

        var bboxOutsideCrsBounds = !IsExtentWithinCrsBounds(requestedExtent, normalizedCrs);

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
            return CreateWmsServiceException(context, "InvalidFormat", "FORMAT must be image/png or image/jpeg.");
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
        var layerFilters = filterResult.Filters;
        activity?.SetTag("wms.filter_applied", layerFilters is not null);

        var temporalResult = TryParseWmsLayerTemporalFilters(context, query, renderLayers);
        if (temporalResult.Error != null)
        {
            return temporalResult.Error;
        }
        var layerTemporalFilters = temporalResult.Filters;
        activity?.SetTag("wms.time_applied", layerTemporalFilters is not null);

        var effectiveTransparent = transparent && string.Equals(imageFormat, "png", StringComparison.OrdinalIgnoreCase);
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

        if (TryHandleCiteWmsGetMap(
                context,
                service,
                renderLayers,
                query,
                requestedExtent,
                imageWidth,
                imageHeight,
                imageFormat,
                contentType,
                effectiveTransparent,
                backgroundColor,
                layerFilters,
                out var citeResult))
        {
            return citeResult;
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

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var maxFeatures = MaxFeaturesPerLayer;
        var totalFeatureCount = 0;

        var canvas = surface.Canvas;
        canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor);
        var transformFn = SkiaMapRenderer.BuildTransform(requestedExtent, imageWidth, imageHeight);

        for (var i = 0; i < renderLayers.Length; i++)
        {
            var layer = renderLayers[i];
            cancellationToken.ThrowIfCancellationRequested();

            if (!ResourceHasGeometry(layer.Resource))
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
                styleCatalog,
                layer.StorageLayerId,
                cancellationToken).ConfigureAwait(false);
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                serviceSrid,
                requestSrid,
                maxFeatures,
                layerFilters?[i],
                layerTemporalFilters?[i]);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                canvas,
                featureReader,
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

            var features = await QueryRasterFeaturesAsync(featureReader, layer.StorageLayerId, featureQuery, cancellationToken);
            if (features.Length == 0)
            {
                continue;
            }

            totalFeatureCount += features.Length;
            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transformFn, geometryType);
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, imageFormat);

        stopwatch.Stop();
        HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
        HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

        return Results.Bytes(imageBytes, contentType);
    }
}
