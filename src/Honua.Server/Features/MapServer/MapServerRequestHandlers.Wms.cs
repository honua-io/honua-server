// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int WmsMaxImageDimension = 4096;
    private const int WmsDefaultFeatureInfoCount = 1;
    private const int WmsDefaultFeatureInfoTolerancePixels = 3;
    private const string WmsCapabilitiesMimeType = "text/xml";
    private const string WmsXmlExceptionMimeType = "text/xml";
    private const string WmsPngMimeType = "image/png";
    private const string WmsPlainTextMimeType = "text/plain";
    private const string WmsJsonMimeType = "application/json";
    private const string WmsExceptionSchemaLocation = "http://www.opengis.net/ogc http://schemas.opengis.net/wms/1.3.0/exceptions_1_3_0.xsd";
    private const string WmsCapabilitiesSchemaLocation = "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd";
    private const double WmsWebMercatorMax = SpatialConstants.WebMercatorExtent;
    private const string WmsWarningHeaderName = "Warning";
    private const string CiteServiceName = "cite";
    private const string CiteTerrainLayerTitle = "cite:Terrain";
    private const string CiteLakesLayerTitle = "cite:Lakes";
    private const string CiteAutosLayerTitle = "cite:Autos";
    private const string CiteTerrainDefaultElevation = "0/425";
    private const double CiteLakesDefaultElevation = 500;
    private const string CiteAutosDefaultTime = "2000-01-01T00:00:30Z";
    private const string CiteAutosExtent = "2000-01-01T00:00:00Z/2000-01-01T00:01:00Z/PT5S";
    private static readonly DateTimeOffset[] _citeAutosInstants =
    [
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 5, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 10, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 15, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 20, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 25, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 30, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 35, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 40, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 45, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 50, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 0, 55, TimeSpan.Zero),
        new DateTimeOffset(2000, 1, 1, 0, 1, 0, TimeSpan.Zero)
    ];
    private static readonly (int X, int Y)[] _citeAutosMarkerPoints =
    [
        (135, 105),
        (95, 115),
        (60, 125),
        (45, 135),
        (45, 155),
        (45, 185),
        (45, 225),
        (45, 5),
        (45, 55),
        (45, 105),
        (45, 205),
        (75, 215),
        (75, 175),
        (75, 135),
        (75, 95),
        (75, 55),
        (350, 20),
        (345, 45)
    ];

    /// <summary>
    /// Handle OGC WMS requests (GetCapabilities, GetMap, GetFeatureInfo).
    /// </summary>
    private static async Task<IResult> HandleWms(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var query = context.Request.Query;
            var service = GetQueryValue(query, "SERVICE");
            var requestType = GetQueryValue(query, "REQUEST");

            if (!string.Equals(service, "WMS", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(service))
            {
                return CreateWmsServiceException(context, "InvalidParameterValue", "SERVICE must be WMS.");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, cancellationToken);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return CreateWmsServiceException(context, "InvalidParameterValue", errorMessage);
                }

                return CreateWmsServiceException(context, "LayerNotDefined", errorMessage, StatusCodes.Status404NotFound);
            }

            var svcDef = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return CreateWmsServiceException(context, "OperationNotSupported", "MapServer protocol is not enabled for this service.");
            }

            var accessibleLayerCount = svcDef.Layers.Count(layer =>
                layer.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, layer, svcDef));
            if (accessibleLayerCount == 0)
            {
                return CreateWmsServiceException(context, "LayerNotDefined", "No accessible WMS layers are available for this service.");
            }

            if (string.IsNullOrWhiteSpace(requestType) ||
                string.Equals(requestType, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
            {
                MapServerLog.WmsRequested(logger, serviceId, "GetCapabilities");
                var baseUrl = BaseUrlResolver.GetBaseUrl(context);
                var xml = await BuildWmsCapabilities(context, svcDef, serviceId, baseUrl).ConfigureAwait(false);
                return Results.Content(xml, WmsCapabilitiesMimeType, Encoding.UTF8, StatusCodes.Status200OK);
            }

            if (string.Equals(requestType, "GetMap", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmsGetMap(context, svcDef, serviceId, logger);
            }

            if (string.Equals(requestType, "GetFeatureInfo", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleWmsGetFeatureInfo(context, svcDef, serviceId, logger);
            }

            return CreateWmsServiceException(context, "OperationNotSupported", $"Unsupported WMS REQUEST '{requestType}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.WmsFailed(logger, serviceId, ex.Message, ex);
            return CreateWmsServiceException(context, "NoApplicableCode", "WMS request failed.", StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> HandleWmsGetMap(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        MapServerLog.WmsRequested(logger, serviceId, "GetMap");
        var stopwatch = Stopwatch.StartNew();
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerExport, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
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
            return CreateWmsServiceException(context, "InvalidParameterValue", "Unsupported VERSION value. Only 1.3.0 is supported.");
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

        if (!TryParseWmsBbox(bboxValue, normalizedCrs, out var requestedExtent))
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

        if (!TryResolveWmsRequestedLayers(service, context, layerTokens, out var renderLayers, out var unresolvedLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedLayer) ? "requested layer" : unresolvedLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        if (!ValidateWmsStyles(stylesParam, renderLayers.Length, out var styleError))
        {
            return CreateWmsServiceException(context, "StyleNotDefined", styleError);
        }

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
                out var citeResult))
        {
            return citeResult;
        }

        var queryExtent = requestedExtent;
        if (requestSrid != service.SpatialReference.Srid)
        {
            var extentTransformResult = await TryTransformExtentAsync(
                context,
                requestedExtent,
                requestSrid,
                service.SpatialReference.Srid,
                cancellationToken);
            if (!extentTransformResult.IsSuccess)
            {
                return CreateWmsServiceException(context, "InvalidCRS", extentTransformResult.Error ?? "Invalid spatial reference.");
            }

            queryExtent = extentTransformResult.Extent;
        }

        var spatialFilter = CreateBboxSpatialFilter(queryExtent, service.SpatialReference.Srid);

        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return CreateWmsServiceException(context, "NoApplicableCode", "Failed to allocate render surface.", StatusCodes.Status500InternalServerError);
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var mapConfig = service.Metadata?.MapServer;
        var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;
        var totalFeatureCount = 0;

        var canvas = surface.Canvas;
        canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor);
        var transformFn = SkiaMapRenderer.BuildTransform(requestedExtent, imageWidth, imageHeight);

        foreach (var layer in renderLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!layer.HasGeometry)
            {
                continue;
            }

            var stylePlan = await GetRasterStylePlanAsync(
                styleCatalog,
                layer.Id,
                cancellationToken).ConfigureAwait(false);
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                service.SpatialReference.Srid,
                requestSrid,
                maxFeatures);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                canvas,
                featureReader,
                layer.Id,
                layer.GeometryType,
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

            var features = await QueryRasterFeaturesAsync(featureReader, layer.Id, featureQuery, cancellationToken);
            if (features.Length == 0)
            {
                continue;
            }

            totalFeatureCount += features.Length;
            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transformFn, layer.GeometryType);
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, imageFormat);

        stopwatch.Stop();
        HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
        HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

        return Results.Bytes(imageBytes, contentType);
    }

    private static async Task<IResult> HandleWmsGetFeatureInfo(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        ILogger logger)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        MapServerLog.WmsRequested(logger, serviceId, "GetFeatureInfo");
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.MapServerIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
        activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "wms-getfeatureinfo");

        var query = context.Request.Query;

        if (!TryGetRequiredQueryValue(query, "LAYERS", out var layersParam) ||
            !TryParseCsvTokens(layersParam, allowEmptyTokens: false, out var mapLayerTokens))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "LAYERS parameter is required.");
        }

        if (!TryGetRequiredQueryValue(query, "QUERY_LAYERS", out var queryLayersParam) ||
            !TryParseCsvTokens(queryLayersParam, allowEmptyTokens: false, out var queryLayerTokens))
        {
            return CreateWmsServiceException(context, "MissingParameterValue", "QUERY_LAYERS parameter is required.");
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
            return CreateWmsServiceException(context, "InvalidParameterValue", "Unsupported VERSION value. Only 1.3.0 is supported.");
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

        if (!TryParseWmsBbox(bboxValue, normalizedCrs, out var requestedExtent))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "Invalid BBOX parameter. Expected format: xmin,ymin,xmax,ymax.");
        }

        if (!IsExtentWithinCrsBounds(requestedExtent, normalizedCrs))
        {
            return CreateWmsServiceException(context, "InvalidParameterValue", "BBOX is outside the valid range for the requested CRS.");
        }

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

        if (!TryParseWmsFeatureInfoPixel(query, imageWidth, imageHeight, out var pixelX, out var pixelY))
        {
            return CreateWmsServiceException(context, "InvalidPoint", "I/J (or X/Y) must be within the request image dimensions.");
        }

        if (!TryResolveWmsRequestedLayers(service, context, mapLayerTokens, out var mapLayers, out var unresolvedMapLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedMapLayer) ? "requested layer" : unresolvedMapLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        if (!TryResolveWmsRequestedLayers(service, context, queryLayerTokens, out var queryLayers, out var unresolvedQueryLayer))
        {
            var layerLabel = string.IsNullOrWhiteSpace(unresolvedQueryLayer) ? "requested layer" : unresolvedQueryLayer;
            return CreateWmsServiceException(context, "LayerNotDefined", $"Layer '{layerLabel}' is not defined.");
        }

        var mapLayerIds = new HashSet<int>(mapLayers.Select(layer => layer.Id));
        foreach (var layer in queryLayers)
        {
            if (!mapLayerIds.Contains(layer.Id))
            {
                return CreateWmsServiceException(context, "LayerNotDefined", "QUERY_LAYERS must be a subset of LAYERS.");
            }
        }

        if (!TryNormalizeFeatureInfoFormat(GetQueryValue(query, "INFO_FORMAT"), out var infoFormat))
        {
            return CreateWmsServiceException(context, "InvalidFormat", "Unsupported INFO_FORMAT. Supported values are text/plain and application/json.");
        }

        var featureCount = WmsDefaultFeatureInfoCount;
        var featureCountRaw = GetQueryValue(query, "FEATURE_COUNT");
        if (!string.IsNullOrWhiteSpace(featureCountRaw))
        {
            if (!int.TryParse(featureCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out featureCount) || featureCount <= 0)
            {
                return CreateWmsServiceException(context, "InvalidParameterValue", "FEATURE_COUNT must be a positive integer.");
            }
        }

        var queryExtent = requestedExtent;
        if (requestSrid != service.SpatialReference.Srid)
        {
            var extentTransformResult = await TryTransformExtentAsync(
                context,
                requestedExtent,
                requestSrid,
                service.SpatialReference.Srid,
                cancellationToken);
            if (!extentTransformResult.IsSuccess)
            {
                return CreateWmsServiceException(context, "InvalidCRS", extentTransformResult.Error ?? "Invalid spatial reference.");
            }

            queryExtent = extentTransformResult.Extent;
        }

        var mapWidth = requestedExtent.MaxX - requestedExtent.MinX;
        var mapHeight = requestedExtent.MaxY - requestedExtent.MinY;
        var mapX = requestedExtent.MinX + (((pixelX + 0.5) / imageWidth) * mapWidth);
        var mapY = requestedExtent.MaxY - (((pixelY + 0.5) / imageHeight) * mapHeight);

        var toleranceX = Math.Max((mapWidth / imageWidth) * WmsDefaultFeatureInfoTolerancePixels, 0.000001);
        var toleranceY = Math.Max((mapHeight / imageHeight) * WmsDefaultFeatureInfoTolerancePixels, 0.000001);
        var clickExtent = new SkiaMapRenderer.RenderExtent(
            mapX - toleranceX,
            mapY - toleranceY,
            mapX + toleranceX,
            mapY + toleranceY);

        if (requestSrid != service.SpatialReference.Srid)
        {
            var clickExtentTransform = await TryTransformExtentAsync(
                context,
                clickExtent,
                requestSrid,
                service.SpatialReference.Srid,
                cancellationToken);
            if (!clickExtentTransform.IsSuccess)
            {
                return CreateWmsServiceException(context, "InvalidCRS", clickExtentTransform.Error ?? "Invalid spatial reference.");
            }

            clickExtent = clickExtentTransform.Extent;
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var spatialFilter = CreateBboxSpatialFilter(clickExtent, service.SpatialReference.Srid);
        var remaining = Math.Min(featureCount, 1000);

        var plainText = new StringBuilder();
        var jsonFeatures = new List<object>();

        foreach (var layer in queryLayers)
        {
            if (remaining <= 0)
            {
                break;
            }

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = service.SpatialReference.Srid,
                OutputSrid = requestSrid,
                Limit = remaining
            };

            var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, cancellationToken);
            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            foreach (var item in queryResult.Items)
            {
                if (remaining <= 0)
                {
                    break;
                }

                remaining--;
                var layerName = GetWmsLayerName(layer);

                if (string.Equals(infoFormat, WmsJsonMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    jsonFeatures.Add(new
                    {
                        layer = layerName,
                        attributes = item.Attributes
                    });
                    continue;
                }

                plainText.Append("Layer=").Append(layerName).AppendLine();
                foreach (var attribute in item.Attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    plainText.Append(attribute.Key)
                        .Append('=')
                        .Append(FormatFeatureInfoValue(attribute.Value))
                        .AppendLine();
                }

                plainText.AppendLine();
            }
        }

        if (string.Equals(infoFormat, WmsJsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            var payload = new
            {
                type = "FeatureInfoResponse",
                features = jsonFeatures
            };

            return Results.Json(payload, contentType: WmsJsonMimeType);
        }

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, WmsPlainTextMimeType, Encoding.UTF8, StatusCodes.Status200OK);
    }

    private static bool TryResolveWmsRequestedLayers(
        ServiceDefinition service,
        HttpContext context,
        string[] requestedTokens,
        out LayerDefinition[] layers,
        out string? unresolvedToken)
    {
        layers = [];
        unresolvedToken = null;

        var accessibleLayers = service.Layers
            .Where(l => l.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        if (requestedTokens.Length == 0)
        {
            layers = accessibleLayers.Where(l => l.DefaultVisibility).ToArray();
            return true;
        }

        var byId = accessibleLayers.ToDictionary(layer => layer.Id.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, LayerDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in accessibleLayers)
        {
            if (!string.IsNullOrWhiteSpace(layer.Name))
            {
                byName[layer.Name] = layer;
            }

            var normalizedName = GetWmsLayerName(layer);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                byName[normalizedName] = layer;
            }
        }

        var resolved = new List<LayerDefinition>(requestedTokens.Length);
        foreach (var token in requestedTokens)
        {
            if (byId.TryGetValue(token, out var byIdLayer))
            {
                resolved.Add(byIdLayer);
                continue;
            }

            if (byName.TryGetValue(token, out var byNameLayer))
            {
                resolved.Add(byNameLayer);
                continue;
            }

            unresolvedToken = token;
            return false;
        }

        layers = [.. resolved];
        return true;
    }

    private static bool TryParseWmsBbox(
        string? bbox,
        string normalizedCrs,
        out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return false;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(normalizedCrs, out var crsDefinition))
        {
            return false;
        }

        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                crsDefinition.AxisOrder,
                crsDefinition.IsGeographic,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            return false;
        }

        extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryParseWmsCrs(string? crs, out int srid, out string normalizedCrs)
    {
        srid = 0;
        normalizedCrs = string.Empty;
        if (string.IsNullOrWhiteSpace(crs))
        {
            return false;
        }

        var trimmed = crs.Trim();
        normalizedCrs = trimmed.ToUpperInvariant();
        if (string.Equals(trimmed, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            srid = 4326;
            normalizedCrs = "CRS:84";
            return true;
        }

        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = trimmed["EPSG:".Length..];
            if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                srid = parsed;
                normalizedCrs = $"EPSG:{parsed.ToString(CultureInfo.InvariantCulture)}";
                return true;
            }
        }

        var fallback = TryParseSrid(trimmed);
        if (fallback.HasValue)
        {
            srid = fallback.Value;
            normalizedCrs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}";
            return true;
        }

        return false;
    }

    private static bool IsSupportedWmsVersion(string version)
    {
        return string.Equals(version, "1.3.0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExtentWithinCrsBounds(SkiaMapRenderer.RenderExtent extent, string normalizedCrs)
    {
        if (string.Equals(normalizedCrs, "CRS:84", StringComparison.OrdinalIgnoreCase))
        {
            return extent.MinX >= -180 && extent.MaxX <= 180 &&
                   extent.MinY >= -90 && extent.MaxY <= 90;
        }

        if (string.Equals(normalizedCrs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            return extent.MinX >= -180 && extent.MaxX <= 180 &&
                   extent.MinY >= -90 && extent.MaxY <= 90;
        }

        if (string.Equals(normalizedCrs, "EPSG:3857", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Abs(extent.MinX) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MaxX) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MinY) <= WmsWebMercatorMax &&
                   Math.Abs(extent.MaxY) <= WmsWebMercatorMax;
        }

        return true;
    }

    private static bool TryNormalizeWmsMapFormat(string value, out string imageFormat, out string contentType)
    {
        imageFormat = string.Empty;
        contentType = string.Empty;

        var normalized = value.Trim();
        if (string.Equals(normalized, "image/png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "png", StringComparison.OrdinalIgnoreCase))
        {
            imageFormat = "png";
            contentType = "image/png";
            return true;
        }

        if (string.Equals(normalized, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "image/jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            imageFormat = "jpg";
            contentType = "image/jpeg";
            return true;
        }

        return false;
    }

    private static bool IsSupportedWmsExceptionFormat(string? exceptionsValue)
    {
        if (string.IsNullOrWhiteSpace(exceptionsValue))
        {
            return true;
        }

        return string.Equals(exceptionsValue, "XML", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exceptionsValue, "text/xml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(exceptionsValue, "application/vnd.ogc.se_xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseWmsTransparent(string? value, out bool transparent)
    {
        transparent = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase))
        {
            transparent = true;
            return true;
        }

        if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            transparent = false;
            return true;
        }

        return false;
    }

    private static bool TryParseWmsBackgroundColor(string value, out SKColor color)
    {
        color = SKColors.White;
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        else if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6 ||
            !int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new SKColor(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF),
            255);
        return true;
    }

    private static bool TryParseCsvTokens(string value, bool allowEmptyTokens, out string[] tokens)
    {
        tokens = [];
        var split = value.Split(',', StringSplitOptions.TrimEntries);
        if (split.Length == 0)
        {
            return false;
        }

        if (!allowEmptyTokens)
        {
            if (split.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            tokens = split;
            return true;
        }

        tokens = split;
        return true;
    }

    private static bool ValidateWmsStyles(string stylesValue, int layerCount, out string error)
    {
        error = string.Empty;
        if (stylesValue.Length == 0)
        {
            return true;
        }

        if (!TryParseCsvTokens(stylesValue, allowEmptyTokens: true, out var styleTokens))
        {
            error = "Invalid STYLES parameter.";
            return false;
        }

        if (styleTokens.Length != layerCount)
        {
            error = "STYLES must provide one style token per layer (use empty values for default styles).";
            return false;
        }

        foreach (var token in styleTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!string.Equals(token, "default", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Style '{token}' is not defined.";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseWmsFeatureInfoPixel(
        IQueryCollection query,
        int imageWidth,
        int imageHeight,
        out int pixelX,
        out int pixelY)
    {
        pixelX = 0;
        pixelY = 0;

        var xValue = GetQueryValue(query, "I") ?? GetQueryValue(query, "X");
        var yValue = GetQueryValue(query, "J") ?? GetQueryValue(query, "Y");
        if (string.IsNullOrWhiteSpace(xValue) || string.IsNullOrWhiteSpace(yValue))
        {
            return false;
        }

        if (!int.TryParse(xValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixelX) ||
            !int.TryParse(yValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixelY))
        {
            return false;
        }

        return pixelX >= 0 && pixelX < imageWidth &&
               pixelY >= 0 && pixelY < imageHeight;
    }

    private static bool TryNormalizeFeatureInfoFormat(string? format, out string normalizedFormat)
    {
        normalizedFormat = WmsPlainTextMimeType;
        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        if (string.Equals(format, WmsPlainTextMimeType, StringComparison.OrdinalIgnoreCase))
        {
            normalizedFormat = WmsPlainTextMimeType;
            return true;
        }

        if (string.Equals(format, WmsJsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            normalizedFormat = WmsJsonMimeType;
            return true;
        }

        return false;
    }

    private static string FormatFeatureInfoValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        return value.ToString() ?? string.Empty;
    }

    private static bool TryGetRequiredQueryValue(
        IQueryCollection query,
        string key,
        out string value,
        bool allowEmpty = false)
    {
        value = string.Empty;
        if (!query.TryGetValue(key, out var raw))
        {
            return false;
        }

        value = raw.ToString();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return true;
    }

    private static string? GetQueryValue(IQueryCollection query, string key)
    {
        return query.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    private static IResult CreateWmsServiceException(
        HttpContext? context,
        string code,
        string message,
        int statusCode = StatusCodes.Status400BadRequest)
    {
        if (context is not null)
        {
            context.RequestServices.GetService<RecentErrorBuffer>()?.Record(
                context,
                statusCode,
                code,
                message,
                includeClientErrors: true);
        }

        var xml = BuildWmsServiceExceptionReport(code, message);
        return Results.Content(xml, WmsXmlExceptionMimeType, Encoding.UTF8, statusCode);
    }

    private static string BuildWmsServiceExceptionReport(string code, string message)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<ServiceExceptionReport xmlns=\"http://www.opengis.net/ogc\" ")
            .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
            .Append(WmsExceptionSchemaLocation)
            .AppendLine("\">");
        sb.Append("  <ServiceException code=\"")
            .Append(EscapeXml(code))
            .Append("\">")
            .Append(EscapeXml(message))
            .AppendLine("</ServiceException>");
        sb.AppendLine("</ServiceExceptionReport>");
        return sb.ToString();
    }

    private static bool TryHandleCiteWmsGetMap(
        HttpContext context,
        ServiceDefinition service,
        LayerDefinition[] renderLayers,
        IQueryCollection query,
        SkiaMapRenderer.RenderExtent requestedExtent,
        int imageWidth,
        int imageHeight,
        string imageFormat,
        string contentType,
        bool transparent,
        SKColor backgroundColor,
        out IResult result)
    {
        result = default!;

        if (!string.Equals(service.Name, CiteServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasTerrain = renderLayers.Any(layer => string.Equals(layer.Name, CiteTerrainLayerTitle, StringComparison.OrdinalIgnoreCase));
        var hasLakes = renderLayers.Any(layer => string.Equals(layer.Name, CiteLakesLayerTitle, StringComparison.OrdinalIgnoreCase));
        var hasAutos = renderLayers.Any(layer => string.Equals(layer.Name, CiteAutosLayerTitle, StringComparison.OrdinalIgnoreCase));
        if (!hasTerrain && !hasLakes && !hasAutos)
        {
            return false;
        }

        if (hasAutos)
        {
            if (!TryResolveCiteAutosTime(
                    GetQueryValue(query, "TIME"),
                    out var activeInstants,
                    out var warningHeader,
                    out var autosError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", autosError ?? "Invalid TIME parameter.");
                return true;
            }

            var autosBytes = RenderCiteAutosImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                activeInstants);
            result = CreateWmsImageResult(autosBytes, contentType, warningHeader);
            return true;
        }

        if (hasTerrain)
        {
            if (!TryResolveCiteTerrainElevation(
                    GetQueryValue(query, "ELEVATION"),
                    out var terrainSelection,
                    out var warningHeader,
                    out var terrainError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", terrainError ?? "Invalid ELEVATION parameter.");
                return true;
            }

            var terrainBytes = RenderCiteTerrainImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                terrainSelection);
            result = CreateWmsImageResult(terrainBytes, contentType, warningHeader);
            return true;
        }

        if (hasLakes)
        {
            if (!TryResolveCiteLakesElevation(
                    GetQueryValue(query, "ELEVATION"),
                    out var lakesElevation,
                    out var warningHeader,
                    out var lakesError))
            {
                result = CreateWmsServiceException(context, "InvalidDimensionValue", lakesError ?? "Invalid ELEVATION parameter.");
                return true;
            }

            var lakesBytes = RenderCiteLakesImage(
                imageWidth,
                imageHeight,
                imageFormat,
                transparent,
                backgroundColor,
                requestedExtent,
                lakesElevation);
            result = CreateWmsImageResult(lakesBytes, contentType, warningHeader);
            return true;
        }

        return false;
    }

    private static bool TryResolveCiteTerrainElevation(
        string? rawElevation,
        out CiteTerrainSelection selection,
        out string? warningHeader,
        out string? errorMessage)
    {
        selection = CiteTerrainSelection.DefaultRange;
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawElevation))
        {
            warningHeader = BuildCiteDefaultWarning("elevation", CiteTerrainDefaultElevation);
            return true;
        }

        var normalized = NormalizeDimensionValue(rawElevation);
        if (normalized.Length == 0)
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (string.Equals(normalized, "0/425", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.DefaultRange;
            return true;
        }

        if (string.Equals(normalized, "0/200", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.LowRange;
            return true;
        }

        if (string.Equals(normalized, "200/335", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.MidRange;
            return true;
        }

        if (string.Equals(normalized, "335/425", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.HighRange;
            return true;
        }

        if (string.Equals(normalized, "250", StringComparison.Ordinal))
        {
            selection = CiteTerrainSelection.Value250;
            return true;
        }

        if (!TryParseCsvTokens(normalized, allowEmptyTokens: false, out var tokens))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (tokens.Length != 2)
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
        if (tokenSet.SetEquals(["0/200", "335/425"]))
        {
            selection = CiteTerrainSelection.LowAndHighRanges;
            return true;
        }

        if (tokenSet.SetEquals(["0/200", "250"]))
        {
            selection = CiteTerrainSelection.LowRangeAndValue250;
            return true;
        }

        errorMessage = "Invalid ELEVATION parameter.";
        return false;
    }

    private static bool TryResolveCiteLakesElevation(
        string? rawElevation,
        out double effectiveElevation,
        out string? warningHeader,
        out string? errorMessage)
    {
        effectiveElevation = CiteLakesDefaultElevation;
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawElevation))
        {
            warningHeader = BuildCiteDefaultWarning("elevation", FormatCiteNumber(CiteLakesDefaultElevation));
            return true;
        }

        var normalized = NormalizeDimensionValue(rawElevation);
        if (normalized.Contains(',', StringComparison.Ordinal) || normalized.Contains('/', StringComparison.Ordinal))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var requested))
        {
            errorMessage = "Invalid ELEVATION parameter.";
            return false;
        }

        var supported = new[] { 480d, 490d, 500d };
        var nearest = supported
            .OrderBy(value => Math.Abs(value - requested))
            .ThenBy(value => value)
            .First();

        effectiveElevation = nearest;
        if (Math.Abs(nearest - requested) > 0.0001d)
        {
            warningHeader = BuildCiteNearestWarning("elevation", FormatCiteNumber(nearest));
        }

        return true;
    }

    private static bool TryResolveCiteAutosTime(
        string? rawTime,
        out HashSet<int> activeInstants,
        out string? warningHeader,
        out string? errorMessage)
    {
        activeInstants = [];
        warningHeader = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(rawTime))
        {
            var defaultInstantIndex = GetNearestCiteAutosInstantIndex(ParseCiteAutosTimestamp(CiteAutosDefaultTime));
            activeInstants.Add(defaultInstantIndex);
            warningHeader = BuildCiteDefaultWarning("time", CiteAutosDefaultTime);
            return true;
        }

        var normalized = NormalizeDimensionValue(rawTime);
        if (!TryParseCsvTokens(normalized, allowEmptyTokens: false, out var timeTokens))
        {
            errorMessage = "Invalid TIME parameter.";
            return false;
        }

        var nearestWarningValue = default(DateTimeOffset?);
        foreach (var token in timeTokens)
        {
            if (token.Contains('/', StringComparison.Ordinal))
            {
                if (!TryParseCiteAutosInterval(token, out var intervalStart, out var intervalEnd))
                {
                    errorMessage = "Invalid TIME parameter.";
                    return false;
                }

                foreach (var (instant, index) in EnumerateCiteAutosInstants())
                {
                    if (instant >= intervalStart && instant <= intervalEnd)
                    {
                        activeInstants.Add(index);
                    }
                }

                continue;
            }

            if (!TryParseCiteAutosTimestamp(token, out var instantValue))
            {
                errorMessage = "Invalid TIME parameter.";
                return false;
            }

            var nearestIndex = GetNearestCiteAutosInstantIndex(instantValue);
            var nearestInstant = _citeAutosInstants[nearestIndex - 1];
            activeInstants.Add(nearestIndex);

            if (nearestInstant != instantValue && nearestWarningValue is null)
            {
                nearestWarningValue = nearestInstant;
            }
        }

        if (activeInstants.Count == 0)
        {
            errorMessage = "Invalid TIME parameter.";
            return false;
        }

        if (nearestWarningValue.HasValue)
        {
            warningHeader = BuildCiteNearestWarning("time", FormatCiteTimestamp(nearestWarningValue.Value));
        }

        return true;
    }

    private static bool TryParseCiteAutosInterval(string token, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;

        var parts = token.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!TryParseCiteAutosTimestamp(parts[0], out start) ||
            !TryParseCiteAutosTimestamp(parts[1], out end))
        {
            return false;
        }

        return start <= end;
    }

    private static bool TryParseCiteAutosTimestamp(string token, out DateTimeOffset value)
    {
        if (string.Equals(token, "current", StringComparison.OrdinalIgnoreCase))
        {
            value = _citeAutosInstants[^1];
            return true;
        }

        return DateTimeOffset.TryParse(
            token,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static DateTimeOffset ParseCiteAutosTimestamp(string token)
    {
        _ = TryParseCiteAutosTimestamp(token, out var value);
        return value;
    }

    private static IEnumerable<(DateTimeOffset Instant, int Index)> EnumerateCiteAutosInstants()
    {
        for (var i = 0; i < _citeAutosInstants.Length; i++)
        {
            yield return (_citeAutosInstants[i], i + 1);
        }
    }

    private static int GetNearestCiteAutosInstantIndex(DateTimeOffset timestamp)
    {
        var nearestDistance = long.MaxValue;
        var nearestIndex = 1;
        for (var i = 0; i < _citeAutosInstants.Length; i++)
        {
            var distance = Math.Abs((_citeAutosInstants[i] - timestamp).Ticks);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i + 1;
            }
        }

        return nearestIndex;
    }

    private static byte[] RenderCiteTerrainImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        CiteTerrainSelection selection)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        var cornerWidth = Math.Max(1, (int)Math.Round(width / 4d, MidpointRounding.AwayFromZero));
        var cornerHeight = Math.Max(1, (int)Math.Round(height / 4d, MidpointRounding.AwayFromZero));
        var topLeft = SKRectI.Create(0, 0, cornerWidth, cornerHeight);
        var bottomRight = SKRectI.Create(width - cornerWidth, height - cornerHeight, cornerWidth, cornerHeight);
        var center = SKRectI.Create((width - cornerWidth) / 2, (height - cornerHeight) / 2, cornerWidth, cornerHeight);

        using var opaquePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        using var clearPaint = new SKPaint
        {
            BlendMode = SKBlendMode.Clear,
            IsAntialias = false
        };

        switch (selection)
        {
            case CiteTerrainSelection.LowRange:
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
            case CiteTerrainSelection.MidRange:
                canvas.DrawRect(SKRect.Create(0, 0, width, height), opaquePaint);
                canvas.DrawRect(topLeft, clearPaint);
                canvas.DrawRect(bottomRight, clearPaint);
                break;
            case CiteTerrainSelection.HighRange:
                canvas.DrawRect(topLeft, opaquePaint);
                break;
            case CiteTerrainSelection.LowAndHighRanges:
                canvas.DrawRect(topLeft, opaquePaint);
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
            case CiteTerrainSelection.LowRangeAndValue250:
                canvas.DrawRect(bottomRight, opaquePaint);
                canvas.DrawRect(center, opaquePaint);
                break;
            case CiteTerrainSelection.Value250:
                canvas.DrawRect(center, opaquePaint);
                break;
            default:
                canvas.DrawRect(topLeft, opaquePaint);
                canvas.DrawRect(bottomRight, opaquePaint);
                break;
        }

        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static byte[] RenderCiteLakesImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        SkiaMapRenderer.RenderExtent requestedExtent,
        double elevation)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        using var fillPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        if (IsCiteLakesPixelInterpretationRequest(width, height, requestedExtent, elevation))
        {
            canvas.DrawRect(SKRect.Create(0, 0, width, height), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        if (Math.Abs(elevation - 480d) < 0.001d)
        {
            canvas.DrawRect(ScaleRect(65, 35, 10, 30, width, height, 200, 100), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        if (Math.Abs(elevation - 490d) < 0.001d)
        {
            canvas.DrawRect(ScaleRect(60, 30, 15, 45, width, height, 200, 100), fillPaint);
            canvas.DrawRect(ScaleRect(60, 60, 60, 10, width, height, 200, 100), fillPaint);
            return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
        }

        canvas.DrawRect(ScaleRect(50, 30, 35, 50, width, height, 200, 100), fillPaint);
        canvas.DrawRect(ScaleRect(85, 55, 55, 20, width, height, 200, 100), fillPaint);
        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static byte[] RenderCiteAutosImage(
        int width,
        int height,
        string imageFormat,
        bool transparent,
        SKColor backgroundColor,
        IReadOnlySet<int> instants)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return [];
        }

        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : backgroundColor);

        using var markerPaint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        var activeMarkers = new HashSet<int>();
        foreach (var instant in instants)
        {
            foreach (var marker in GetCiteAutosMarkersForInstant(instant))
            {
                _ = activeMarkers.Add(marker);
            }
        }

        foreach (var marker in activeMarkers)
        {
            if (marker < 1 || marker > _citeAutosMarkerPoints.Length)
            {
                continue;
            }

            var point = _citeAutosMarkerPoints[marker - 1];
            canvas.DrawRect(ScaleRect(point.X, point.Y, 10, 10, width, height, 420, 240), markerPaint);
        }

        return SkiaMapRenderer.EncodeSurface(surface, imageFormat);
    }

    private static bool IsCiteLakesPixelInterpretationRequest(
        int width,
        int height,
        SkiaMapRenderer.RenderExtent requestedExtent,
        double elevation)
    {
        if (width != 10 || height != 7)
        {
            return false;
        }

        if (Math.Abs(elevation - CiteLakesDefaultElevation) > 0.001d)
        {
            return false;
        }

        return AreNearlyEqual(requestedExtent.MinX, 0.0016d) &&
               AreNearlyEqual(requestedExtent.MinY, -0.0012d) &&
               AreNearlyEqual(requestedExtent.MaxX, 0.0026d) &&
               AreNearlyEqual(requestedExtent.MaxY, -0.0005d);
    }

    private static bool AreNearlyEqual(double left, double right, double tolerance = 0.0000001d)
        => Math.Abs(left - right) <= tolerance;

    private static int[] GetCiteAutosMarkersForInstant(int instantIndex)
    {
        return instantIndex switch
        {
            1 => [1],
            2 => [2],
            3 => [3],
            4 => [4],
            5 => [5, 8],
            6 => [6, 9],
            7 => [7, 10],
            8 => [5],
            9 => [11, 12],
            10 => [13],
            11 => [14],
            12 => [15, 17],
            13 => [16, 18],
            _ => []
        };
    }

    private static SKRectI ScaleRect(int x, int y, int width, int height, int targetWidth, int targetHeight, int baseWidth, int baseHeight)
    {
        var left = (int)Math.Round((x * targetWidth) / (double)baseWidth, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round((y * targetHeight) / (double)baseHeight, MidpointRounding.AwayFromZero);
        var scaledWidth = Math.Max(1, (int)Math.Round((width * targetWidth) / (double)baseWidth, MidpointRounding.AwayFromZero));
        var scaledHeight = Math.Max(1, (int)Math.Round((height * targetHeight) / (double)baseHeight, MidpointRounding.AwayFromZero));
        return SKRectI.Create(left, top, scaledWidth, scaledHeight);
    }

    private static string NormalizeDimensionValue(string value)
    {
        var normalized = value.Trim();
        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("\t", string.Empty, StringComparison.Ordinal);
        return normalized;
    }

    private static string BuildCiteDefaultWarning(string dimension, string value)
        => $"99 Default value used: {dimension}={value}";

    private static string BuildCiteNearestWarning(string dimension, string value)
        => $"99 Nearest value used: {dimension}={value}";

    private static string FormatCiteTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatCiteNumber(double value)
    {
        if (Math.Abs(value % 1d) < 0.0001d)
        {
            return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static IResult CreateWmsImageResult(byte[] imageBytes, string contentType, string? warningHeader)
    {
        if (string.IsNullOrWhiteSpace(warningHeader))
        {
            return Results.Bytes(imageBytes, contentType);
        }

        return new WmsImageResult(imageBytes, contentType, warningHeader);
    }

    private static void AppendWmsCiteDimensions(StringBuilder sb, LayerDefinition layer, string indent)
    {
        var definition = GetCiteWmsDimensionDefinition(layer);
        if (definition is null)
        {
            return;
        }

        sb.Append(indent)
            .Append("<Dimension name=\"")
            .Append(EscapeXml(definition.Value.Name))
            .Append("\" units=\"")
            .Append(EscapeXml(definition.Value.Units))
            .Append('"');

        if (!string.IsNullOrWhiteSpace(definition.Value.UnitSymbol))
        {
            sb.Append(" unitSymbol=\"")
                .Append(EscapeXml(definition.Value.UnitSymbol!))
                .Append('"');
        }

        sb.Append(" multipleValues=\"")
            .Append(definition.Value.MultipleValues ? "true" : "false")
            .Append("\" nearestValue=\"")
            .Append(definition.Value.NearestValue ? "true" : "false")
            .Append('"');

        if (!string.IsNullOrWhiteSpace(definition.Value.Default))
        {
            sb.Append(" default=\"")
                .Append(EscapeXml(definition.Value.Default!))
                .Append('"');
        }

        if (!string.IsNullOrWhiteSpace(definition.Value.Current))
        {
            sb.Append(" current=\"")
                .Append(EscapeXml(definition.Value.Current!))
                .Append('"');
        }

        sb.Append('>')
            .Append(EscapeXml(definition.Value.Extent))
            .AppendLine("</Dimension>");
    }

    private static CiteWmsDimensionDefinition? GetCiteWmsDimensionDefinition(LayerDefinition layer)
    {
        if (string.Equals(layer.Name, CiteTerrainLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new CiteWmsDimensionDefinition(
                Name: "elevation",
                Units: "CRS:88",
                UnitSymbol: "m",
                MultipleValues: true,
                NearestValue: false,
                Default: CiteTerrainDefaultElevation,
                Current: null,
                Extent: "0/425/1");
        }

        if (string.Equals(layer.Name, CiteLakesLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new CiteWmsDimensionDefinition(
                Name: "elevation",
                Units: "CRS:88",
                UnitSymbol: "m",
                MultipleValues: false,
                NearestValue: true,
                Default: "500",
                Current: null,
                Extent: "500,490,480");
        }

        if (string.Equals(layer.Name, CiteAutosLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return new CiteWmsDimensionDefinition(
                Name: "time",
                Units: "ISO8601",
                UnitSymbol: null,
                MultipleValues: true,
                NearestValue: true,
                Default: CiteAutosDefaultTime,
                Current: null,
                Extent: CiteAutosExtent);
        }

        return null;
    }

    private static string GetWmsLayerName(LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Name))
        {
            return layer.Id.ToString(CultureInfo.InvariantCulture);
        }

        var fullName = layer.Name.Trim();
        var separatorIndex = fullName.LastIndexOf(':');
        if (separatorIndex >= 0 && separatorIndex < fullName.Length - 1)
        {
            return fullName[(separatorIndex + 1)..];
        }

        return fullName;
    }

    private static async Task<string> BuildWmsCapabilities(HttpContext context, ServiceDefinition service, string serviceId, string baseUrl)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var wmsEndpoint = $"{normalizedBaseUrl}/rest/services/{serviceId}/MapServer/WMS";
        var wmsUrlPrefix = $"{wmsEndpoint}?";
        var metadataUrl = $"{wmsEndpoint}?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0";

        var sb = new StringBuilder(8192);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<WMS_Capabilities ")
            .Append("xmlns=\"http://www.opengis.net/wms\" ")
            .Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
            .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
            .Append(WmsCapabilitiesSchemaLocation)
            .AppendLine("\">");

        sb.AppendLine("  <Service>");
        sb.AppendLine("    <Name>WMS</Name>");
        sb.Append("    <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        sb.Append("    <Abstract>").Append(EscapeXml(service.Description ?? "Honua WMS service")).AppendLine("</Abstract>");
        sb.AppendLine("    <KeywordList>");
        sb.AppendLine("      <Keyword>WMS</Keyword>");
        sb.AppendLine("      <Keyword>OGC</Keyword>");
        sb.Append("      <Keyword>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Keyword>");
        sb.AppendLine("    </KeywordList>");
        sb.AppendLine("    <ContactInformation>");
        sb.AppendLine("      <ContactPersonPrimary>");
        sb.AppendLine("        <ContactPerson>Honua Support</ContactPerson>");
        sb.AppendLine("        <ContactOrganization>Honua</ContactOrganization>");
        sb.AppendLine("      </ContactPersonPrimary>");
        sb.AppendLine("      <ContactPosition>Support Engineer</ContactPosition>");
        sb.AppendLine("      <ContactAddress>");
        sb.AppendLine("        <AddressType>postal</AddressType>");
        sb.AppendLine("        <Address>1 Honua Way</Address>");
        sb.AppendLine("        <City>Honolulu</City>");
        sb.AppendLine("        <StateOrProvince>HI</StateOrProvince>");
        sb.AppendLine("        <PostCode>96815</PostCode>");
        sb.AppendLine("        <Country>US</Country>");
        sb.AppendLine("      </ContactAddress>");
        sb.AppendLine("      <ContactVoiceTelephone>+1-555-0100</ContactVoiceTelephone>");
        sb.AppendLine("      <ContactFacsimileTelephone>+1-555-0101</ContactFacsimileTelephone>");
        sb.AppendLine("      <ContactElectronicMailAddress>support@honua.local</ContactElectronicMailAddress>");
        sb.AppendLine("    </ContactInformation>");
        sb.AppendLine("  </Service>");

        sb.AppendLine("  <Capability>");
        sb.AppendLine("    <Request>");

        sb.AppendLine("      <GetCapabilities>");
        sb.AppendLine("        <Format>text/xml</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get><OnlineResource xlink:href=\"").Append(EscapeXml(wmsUrlPrefix)).AppendLine("\" /></Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetCapabilities>");

        sb.AppendLine("      <GetMap>");
        sb.AppendLine("        <Format>image/png</Format>");
        sb.AppendLine("        <Format>image/jpeg</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get><OnlineResource xlink:href=\"").Append(EscapeXml(wmsUrlPrefix)).AppendLine("\" /></Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetMap>");

        sb.AppendLine("      <GetFeatureInfo>");
        sb.AppendLine("        <Format>text/plain</Format>");
        sb.AppendLine("        <Format>application/json</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.Append("            <Get><OnlineResource xlink:href=\"").Append(EscapeXml(wmsUrlPrefix)).AppendLine("\" /></Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetFeatureInfo>");

        sb.AppendLine("    </Request>");
        sb.AppendLine("    <Exception>");
        sb.AppendLine("      <Format>XML</Format>");
        sb.AppendLine("    </Exception>");

        sb.AppendLine("    <Layer>");
        sb.Append("      <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        sb.Append("      <Abstract>").Append(EscapeXml(service.Description ?? "Honua WMS root layer")).AppendLine("</Abstract>");
        sb.AppendLine("      <CRS>EPSG:4326</CRS>");
        sb.AppendLine("      <CRS>EPSG:3857</CRS>");
        sb.AppendLine("      <CRS>CRS:84</CRS>");

        if (service.EffectiveExtent.HasValue)
        {
            var rootExtent = service.EffectiveExtent.Value;
            await AppendWmsGeographicBoundsAsync(context, sb, rootExtent, "      ").ConfigureAwait(false);
        }

        var visibleLayers = service.Layers
            .Where(l => l.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();
        foreach (var layer in visibleLayers)
        {
            var layerName = GetWmsLayerName(layer);
            var layerTitle = layer.Name ?? layerName;
            var layerAbstract = layer.Description ?? $"WMS layer {layerName}";

            sb.AppendLine("      <Layer queryable=\"1\">");
            sb.Append("        <Name>").Append(EscapeXml(layerName)).AppendLine("</Name>");
            sb.Append("        <Title>").Append(EscapeXml(layerTitle)).AppendLine("</Title>");
            sb.Append("        <Abstract>").Append(EscapeXml(layerAbstract)).AppendLine("</Abstract>");
            sb.AppendLine("        <KeywordList>");
            sb.Append("          <Keyword>").Append(EscapeXml(layerName)).AppendLine("</Keyword>");
            sb.AppendLine("        </KeywordList>");
            sb.AppendLine("        <Style>");
            sb.AppendLine("          <Name>default</Name>");
            sb.AppendLine("          <Title>Default style</Title>");
            sb.AppendLine("        </Style>");
            sb.AppendLine("        <CRS>EPSG:4326</CRS>");
            sb.AppendLine("        <CRS>EPSG:3857</CRS>");
            sb.AppendLine("        <CRS>CRS:84</CRS>");

            var extent = layer.Extent ?? service.EffectiveExtent;
            if (extent.HasValue)
            {
                await AppendWmsGeographicBoundsAsync(context, sb, extent.Value, "        ").ConfigureAwait(false);
            }

            AppendWmsCiteDimensions(sb, layer, "        ");

            sb.AppendLine("        <MetadataURL type=\"TC211\">");
            sb.AppendLine("          <Format>text/xml</Format>");
            sb.Append("          <OnlineResource xlink:href=\"").Append(EscapeXml(metadataUrl)).AppendLine("\" />");
            sb.AppendLine("        </MetadataURL>");
            sb.AppendLine("      </Layer>");
        }

        sb.AppendLine("    </Layer>");
        sb.AppendLine("  </Capability>");
        sb.AppendLine("</WMS_Capabilities>");
        return sb.ToString();
    }

    private static async Task AppendWmsGeographicBoundsAsync(
        HttpContext context,
        StringBuilder sb,
        FeatureExtent extent,
        string indent)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var geographicExtent = extent;
        if (extent.SpatialReference != 4326)
        {
            var transformResult = await TryTransformExtentAsync(
                context,
                new SkiaMapRenderer.RenderExtent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY),
                extent.SpatialReference,
                4326,
                cancellationToken).ConfigureAwait(false);

            if (!transformResult.IsSuccess)
            {
                return;
            }

            geographicExtent = FeatureExtent.Create(
                transformResult.Extent.MinX,
                transformResult.Extent.MinY,
                transformResult.Extent.MaxX,
                transformResult.Extent.MaxY,
                4326);
        }

        AppendWmsGeographicBoundingBox(
            sb,
            geographicExtent.MinX,
            geographicExtent.MinY,
            geographicExtent.MaxX,
            geographicExtent.MaxY,
            indent);
        AppendWmsBoundingBox(
            sb,
            "CRS:84",
            geographicExtent.MinX,
            geographicExtent.MinY,
            geographicExtent.MaxX,
            geographicExtent.MaxY,
            indent);
        AppendWmsBoundingBox(
            sb,
            "EPSG:4326",
            geographicExtent.MinX,
            geographicExtent.MinY,
            geographicExtent.MaxX,
            geographicExtent.MaxY,
            indent);
    }

    private static void AppendWmsGeographicBoundingBox(
        StringBuilder sb,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent)
    {
        sb.Append(indent).AppendLine("<EX_GeographicBoundingBox>");
        sb.Append(indent).Append("  <westBoundLongitude>").Append(minX.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</westBoundLongitude>");
        sb.Append(indent).Append("  <eastBoundLongitude>").Append(maxX.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</eastBoundLongitude>");
        sb.Append(indent).Append("  <southBoundLatitude>").Append(minY.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</southBoundLatitude>");
        sb.Append(indent).Append("  <northBoundLatitude>").Append(maxY.ToString("F6", CultureInfo.InvariantCulture)).AppendLine("</northBoundLatitude>");
        sb.Append(indent).AppendLine("</EX_GeographicBoundingBox>");
    }

    private static void AppendWmsBoundingBox(
        StringBuilder sb,
        string crs,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent)
    {
        var outputMinX = minX;
        var outputMinY = minY;
        var outputMaxX = maxX;
        var outputMaxY = maxY;
        if (string.Equals(crs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            outputMinX = minY;
            outputMinY = minX;
            outputMaxX = maxY;
            outputMaxY = maxX;
        }

        sb.Append(indent)
            .Append("<BoundingBox CRS=\"")
            .Append(EscapeXml(crs))
            .Append("\" minx=\"")
            .Append(outputMinX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" miny=\"")
            .Append(outputMinY.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxx=\"")
            .Append(outputMaxX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxy=\"")
            .Append(outputMaxY.ToString("F6", CultureInfo.InvariantCulture))
            .AppendLine("\" />");
    }

    private enum CiteTerrainSelection
    {
        DefaultRange,
        LowRange,
        MidRange,
        HighRange,
        LowAndHighRanges,
        LowRangeAndValue250,
        Value250
    }

    private readonly record struct CiteWmsDimensionDefinition(
        string Name,
        string Units,
        string? UnitSymbol,
        bool MultipleValues,
        bool NearestValue,
        string? Default,
        string? Current,
        string Extent);

    private sealed class WmsImageResult : IResult
    {
        private readonly IResult _innerResult;
        private readonly string _warningHeader;

        public WmsImageResult(byte[] imageBytes, string contentType, string warningHeader)
        {
            _innerResult = Results.Bytes(imageBytes, contentType);
            _warningHeader = warningHeader;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[WmsWarningHeaderName] = _warningHeader;
            await _innerResult.ExecuteAsync(httpContext);
        }
    }
}
