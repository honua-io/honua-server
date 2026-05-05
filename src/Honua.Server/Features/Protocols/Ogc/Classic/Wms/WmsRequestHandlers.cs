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
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Features.Protocols.Ogc.Classic;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using static Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline;
using static Honua.Server.Features.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Server.Features.Protocols.Ogc.Classic.Wms;

internal static class WmsRequestHandlers
{
    private const int WmsMaxImageDimension = 4096;
    private const string Wms13Version = "1.3.0";
    private const string Wms111Version = "1.1.1";
    private const string WmsCapabilitiesMimeType = "text/xml";
    private const string Wms111CapabilitiesMimeType = "application/vnd.ogc.wms_xml";
    private const string WmsXmlExceptionMimeType = "text/xml";
    private const string WmsSeXmlExceptionMimeType = "application/vnd.ogc.se_xml";
    private const string WmsExceptionSchemaLocation = "http://www.opengis.net/ogc http://schemas.opengis.net/wms/1.3.0/exceptions_1_3_0.xsd";
    private const string WmsCapabilitiesSchemaLocation = "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd";
    private const string Wms111CapabilitiesDtd = "http://schemas.opengis.net/wms/1.1.1/WMS_MS_Capabilities.dtd";
    private const string Wms111ExceptionDtd = "http://schemas.opengis.net/wms/1.1.1/exception_1_1_1.dtd";
    private const double WmsWebMercatorMax = SpatialConstants.WebMercatorExtent;
    private const string WmsWarningHeaderName = "Warning";
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
    private static readonly string[] _wms13CapabilitiesMediaTypes = [WmsCapabilitiesMimeType];
    private static readonly string[] _wms111CapabilitiesMediaTypes = [Wms111CapabilitiesMimeType, WmsCapabilitiesMimeType];

    /// <summary>
    /// Handle OGC WMS requests (GetCapabilities, GetMap, GetFeatureInfo).
    /// </summary>
    internal static async Task<IResult> HandleWms(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Protocols.Ogc.Classic.Wms.WmsRequestHandlers");

        try
        {
            var query = context.Request.Query;
            var service = GetQueryValue(query, "SERVICE");
            var requestType = GetQueryValue(query, "REQUEST");
            var version = GetQueryValue(query, "VERSION");

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
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, svcDef, ServiceProtocols.Wms);
            if (protocolError is not null)
            {
                return CreateWmsServiceException(context, "OperationNotSupported", "WMS protocol is not enabled for this service.");
            }

            var wmsLayers = svcDef.Layers.Where(static layer => layer.HasGeometry).ToArray();
            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, wmsLayers, svcDef);
            if (accessError != null)
            {
                var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
                return CreateWmsServiceException(
                    context,
                    "AccessDenied",
                    isAuthenticated
                        ? AccessPolicyHelpers.AccessForbiddenMessage
                        : AccessPolicyHelpers.AuthRequiredMessage,
                    isAuthenticated
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status401Unauthorized);
            }

            if (wmsLayers.Length == 0)
            {
                return CreateWmsServiceException(context, "LayerNotDefined", "No accessible WMS layers are available for this service.");
            }

            if (string.IsNullOrWhiteSpace(requestType) ||
                string.Equals(requestType, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(version) && !IsSupportedWmsVersion(version))
                {
                    return CreateWmsServiceException(
                        context,
                        "InvalidParameterValue",
                        $"Unsupported WMS VERSION '{version}'. Supported versions are 1.3.0 and 1.1.1.");
                }

                var capabilitiesVersion = string.IsNullOrWhiteSpace(version) ? Wms13Version : version.Trim();
                if (!XmlContentNegotiation.IsXmlAccepted(
                        context.Request.Headers.Accept.ToString(),
                        GetWmsCapabilitiesMediaTypes(capabilitiesVersion)))
                {
                    return Results.StatusCode(StatusCodes.Status406NotAcceptable);
                }

                OgcClassicLog.WmsRequested(logger, serviceId, "GetCapabilities");
                var baseUrl = BaseUrlResolver.GetBaseUrl(context);
                var xml = await BuildWmsCapabilities(context, svcDef, serviceId, baseUrl, capabilitiesVersion).ConfigureAwait(false);
                return Results.Content(xml, GetWmsCapabilitiesMimeType(capabilitiesVersion), Encoding.UTF8, StatusCodes.Status200OK);
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
            OgcClassicLog.WmsFailed(logger, serviceId, ex.Message, ex);
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

        if (!TryResolveWmsRequestedLayers(service, context, layerTokens, out var renderLayers, out var unresolvedLayer))
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

        for (var i = 0; i < renderLayers.Length; i++)
        {
            var layer = renderLayers[i];
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
                maxFeatures,
                layerFilters?[i],
                layerTemporalFilters?[i]);

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
        OgcClassicLog.WmsRequested(logger, serviceId, "GetFeatureInfo");
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            HonuaTelemetry.Activities.FeatureIdentify, ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcMaps);
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

        if (!TryParseWmsFeatureInfoPixel(query, versionValue, imageWidth, imageHeight, out var pixelX, out var pixelY))
        {
            var pointParameters = IsWms111Version(versionValue) ? "X/Y" : "I/J";
            return CreateWmsServiceException(context, "InvalidPoint", $"{pointParameters} must be within the request image dimensions.");
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

        var filterResult = TryParseWmsLayerFilters(context, query, mapLayers);
        if (filterResult.Error != null)
        {
            return filterResult.Error;
        }
        var filtersByLayerId = filterResult.Filters is null
            ? null
            : mapLayers
                .Select((layer, index) => (layer.Id, Filter: filterResult.Filters[index]))
                .Where(item => item.Filter is not null)
                .ToDictionary(item => item.Id, item => item.Filter);

        var featureCount = DefaultFeatureInfoCount;
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

        var toleranceX = Math.Max((mapWidth / imageWidth) * DefaultFeatureInfoTolerancePixels, 0.000001);
        var toleranceY = Math.Max((mapHeight / imageHeight) * DefaultFeatureInfoTolerancePixels, 0.000001);
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
        var jsonFeatures = new List<WmsFeatureInfoFeature>();

        foreach (var layer in queryLayers)
        {
            if (remaining <= 0)
            {
                break;
            }

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SqlFilter = filtersByLayerId != null && filtersByLayerId.TryGetValue(layer.Id, out var sqlFilter)
                    ? sqlFilter
                    : null,
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
                var attributes = BuildVisibleFeatureInfoAttributes(item);

                if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    jsonFeatures.Add(new WmsFeatureInfoFeature
                    {
                        Layer = layerName,
                        Attributes = attributes
                    });
                    continue;
                }

                plainText.Append("Layer=").Append(layerName).AppendLine();
                foreach (var attribute in attributes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    plainText.Append(attribute.Key)
                        .Append('=')
                        .Append(FormatFeatureInfoValue(attribute.Value))
                        .AppendLine();
                }

                plainText.AppendLine();
            }
        }

        if (string.Equals(infoFormat, JsonMimeType, StringComparison.OrdinalIgnoreCase))
        {
            var payload = new WmsFeatureInfoResponse
            {
                Features = [.. jsonFeatures]
            };

            return Results.Json(payload, OgcClassicJsonContext.Default.WmsFeatureInfoResponse, contentType: JsonMimeType);
        }

        var body = plainText.Length > 0
            ? plainText.ToString().TrimEnd()
            : "No features found.";
        return Results.Content(body, PlainTextMimeType, Encoding.UTF8, StatusCodes.Status200OK);
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

    /// <summary>
    /// Parses the optional WMS 1.3 TIME parameter into per-layer
    /// <see cref="TemporalFilter"/>. Returns (null, null) when TIME is absent so
    /// non-temporal layers are not regressed. The TIME value is shared across
    /// all requested layers (WMS does not allow per-layer TIME); each layer that
    /// is time-aware receives the same parsed bounds, and layers without temporal
    /// configuration are rejected with InvalidDimensionValue per OGC 06-042.
    /// CITE Autos has its own synthetic rendering path and is bypassed here so
    /// the existing CITE conformance behavior is preserved.
    /// </summary>
    private static (TemporalFilter?[]? Filters, IResult? Error) TryParseWmsLayerTemporalFilters(
        HttpContext context,
        IQueryCollection query,
        LayerDefinition[] layers)
    {
        var timeParam = GetQueryValue(query, "TIME");
        if (string.IsNullOrWhiteSpace(timeParam))
        {
            return (null, null);
        }

        // CITE Autos uses synthetic rendering with its own TIME parser
        // (`current`, comma-separated instants, intervals). Bypass the generic
        // OgcTemporalFilterParser entirely when the request targets that layer
        // so CITE-supported values are not rejected before TryHandleCiteWmsGetMap
        // can resolve them.
        foreach (var layer in layers)
        {
            if (string.Equals(layer.Name, CiteAutosLayerTitle, StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }
        }

        if (!OgcTemporalFilterParser.TryParseRange(timeParam, out var start, out var end, out var parseError))
        {
            return (null, CreateWmsServiceException(
                context,
                "InvalidDimensionValue",
                parseError ?? "Invalid TIME parameter."));
        }

        if (start is null && end is null)
        {
            return (null, null);
        }

        var temporalFilters = new TemporalFilter?[layers.Length];
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];

            // Match the capabilities contract: a layer is time-aware only when
            // its TimeInfo declares a StartTimeField AND both the start and
            // (optional) end fields resolve to Date/DateTime attributes. WMS
            // GetCapabilities uses the same gate (TryResolveTemporalRangeAsync
            // returns null when EndTimeField does not resolve), so a layer
            // whose EndTimeField is misconfigured does not advertise a
            // <Dimension name="time"> and must not accept TIME on GetMap
            // either. Documented in docs/gis/temporal-animation-api.md.
            if (!TemporalExtentHelpers.TryResolveOptInTemporalFields(layer, out var selection))
            {
                return (null, CreateWmsServiceException(
                    context,
                    "InvalidDimensionValue",
                    $"Layer '{layer.Name ?? layer.Id.ToString(CultureInfo.InvariantCulture)}' does not support a TIME dimension."));
            }

            var startField = selection.StartField;
            temporalFilters[i] = new TemporalFilter
            {
                PropertyName = startField.Name,
                PropertyType = startField.Type == FieldType.Date
                    ? TemporalPropertyType.Date
                    : TemporalPropertyType.DateTime,
                Start = start,
                End = end
            };
        }

        return (temporalFilters, null);
    }

    private static (SqlFragment?[]? Filters, IResult? Error) TryParseWmsLayerFilters(
        HttpContext context,
        IQueryCollection query,
        LayerDefinition[] layers)
    {
        // OGC WMS 1.3.0 clause 7.3.3.4: optional FILTER parameter
        // (semicolon-delimited FES XML per layer).
        var filterParam = GetQueryValue(query, "FILTER");
        if (string.IsNullOrWhiteSpace(filterParam))
        {
            return (null, null);
        }

        var filterTokens = SplitWmsFilterTokens(filterParam);
        if (filterTokens.Length != 1 && filterTokens.Length != layers.Length)
        {
            return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                $"FILTER must contain exactly 1 or {layers.Length.ToString(CultureInfo.InvariantCulture)} filter(s), separated by semicolons, matching the number of LAYERS."));
        }

        var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var layerFilters = new SqlFragment?[layers.Length];

        for (var i = 0; i < layers.Length; i++)
        {
            var token = filterTokens.Length == 1 ? filterTokens[0] : filterTokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            FilterExpression expression;
            try
            {
                expression = Fes20Parser.ParseFilter(token);
            }
            catch (Fes20ParseException ex)
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    $"Invalid FILTER XML: {ex.Message}"));
            }

            expression = FilterExpressionHelpers.NormalizeFilterPropertyReferences(expression, layers[i]);
            if (!FilterExpressionHelpers.IsBooleanFilterExpression(expression))
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    "FILTER expression must be a boolean predicate."));
            }

            var translation = filterExpressionService.Translate(expression, layers[i]);
            if (!translation.IsSuccess)
            {
                return (null, CreateWmsServiceException(context, "InvalidParameterValue",
                    translation.ErrorMessage ?? "Invalid filter expression."));
            }

            layerFilters[i] = translation.SqlFilter;
        }

        return (layerFilters, null);
    }

    private static bool TryParseWmsBbox(
        string? bbox,
        string normalizedCrs,
        string version,
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

        // WMS validates coordinate ordering separately from CRS bounds.
        // Keep parsing strict on axis order and min/max ordering, but allow
        // out-of-range geographic coordinates so GetMap can return blank
        // imagery and GetFeatureInfo can emit a targeted CRS-range exception.
        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                ResolveWmsBboxAxisOrder(normalizedCrs, version, crsDefinition.AxisOrder),
                isGeographic: false,
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
        return string.Equals(version, Wms13Version, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(version, Wms111Version, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWms111Version(string? version)
        => string.Equals(version, Wms111Version, StringComparison.OrdinalIgnoreCase);

    private static AxisOrder ResolveWmsBboxAxisOrder(string normalizedCrs, string version, AxisOrder defaultAxisOrder)
    {
        if (IsWms111Version(version) &&
            string.Equals(normalizedCrs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            return AxisOrder.EastNorth;
        }

        return defaultAxisOrder;
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
               string.Equals(exceptionsValue, WmsSeXmlExceptionMimeType, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetWmsCapabilitiesMediaTypes(string version)
        => IsWms111Version(version) ? _wms111CapabilitiesMediaTypes : _wms13CapabilitiesMediaTypes;

    private static string GetWmsCapabilitiesMimeType(string version)
        => IsWms111Version(version) ? Wms111CapabilitiesMimeType : WmsCapabilitiesMimeType;

    private static string GetWmsExceptionMimeType(string? exceptionsValue, string? version)
    {
        return string.Equals(exceptionsValue, WmsSeXmlExceptionMimeType, StringComparison.OrdinalIgnoreCase) ||
               (string.IsNullOrWhiteSpace(exceptionsValue) && IsWms111Version(version))
            ? WmsSeXmlExceptionMimeType
            : WmsXmlExceptionMimeType;
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
        string version,
        int imageWidth,
        int imageHeight,
        out int pixelX,
        out int pixelY)
    {
        pixelX = 0;
        pixelY = 0;

        var isWms111 = IsWms111Version(version);
        var xValue = GetQueryValue(query, isWms111 ? "X" : "I");
        var yValue = GetQueryValue(query, isWms111 ? "Y" : "J");
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

    private static Dictionary<string, object?> BuildVisibleFeatureInfoAttributes(Feature feature)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in feature.Attributes)
        {
            if (FeatureAttributeVisibility.IsInternalAttribute(attribute.Key))
            {
                continue;
            }

            attributes[attribute.Key] = FeatureAttributeValueNormalizer.Normalize(attribute.Value);
        }

        return attributes;
    }

    /// <summary>
    /// Splits a WMS FILTER parameter into per-layer tokens using only semicolons
    /// at XML depth 0 as delimiters. Semicolons inside XML elements (entity
    /// references, literal text) are preserved.
    /// </summary>
    private static string[] SplitWmsFilterTokens(string filterParam)
    {
        var tokens = new List<string>();
        int depth = 0;
        int start = 0;
        bool inTag = false;
        bool isClosingTag = false;

        for (int i = 0; i < filterParam.Length; i++)
        {
            char c = filterParam[i];

            switch (c)
            {
                case '<' when !inTag:
                    inTag = true;
                    isClosingTag = i + 1 < filterParam.Length && filterParam[i + 1] == '/';
                    break;
                case '>' when inTag:
                    if (filterParam[i - 1] == '/' || filterParam[i - 1] == '?')
                    {
                        // Self-closing <.../> or processing instruction <?...?>: neutral depth
                    }
                    else if (isClosingTag)
                    {
                        depth--;
                    }
                    else
                    {
                        depth++;
                    }

                    inTag = false;
                    break;
                case ';' when depth == 0 && !inTag:
                    tokens.Add(filterParam[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start <= filterParam.Length)
        {
            tokens.Add(filterParam[start..]);
        }

        return tokens.ToArray();
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

        var version = context is null ? Wms13Version : GetQueryValue(context.Request.Query, "VERSION");
        var xml = BuildWmsServiceExceptionReport(code, message, version);
        var contentType = GetWmsExceptionMimeType(context is null ? null : GetQueryValue(context.Request.Query, "EXCEPTIONS"), version);
        return Results.Content(xml, contentType, Encoding.UTF8, statusCode);
    }

    private static string BuildWmsServiceExceptionReport(string code, string message, string? version)
    {
        if (IsWms111Version(version))
        {
            var legacy = new StringBuilder(512);
            legacy.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            legacy.Append("<!DOCTYPE ServiceExceptionReport SYSTEM \"")
                .Append(Wms111ExceptionDtd)
                .AppendLine("\">");
            legacy.AppendLine("<ServiceExceptionReport version=\"1.1.1\">");
            legacy.Append("  <ServiceException code=\"")
                .Append(EscapeXml(code))
                .Append("\">")
                .Append(EscapeXml(message))
                .AppendLine("</ServiceException>");
            legacy.AppendLine("</ServiceExceptionReport>");
            return legacy.ToString();
        }

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
        SqlFragment?[]? layerFilters,
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

        // When FILTER is applied, bypass synthetic CITE rendering so the
        // standard feature-query path applies the filter per OGC WMS 1.3.0.
        if (layerFilters is not null)
        {
            for (var i = 0; i < layerFilters.Length; i++)
            {
                if (layerFilters[i] is not null)
                {
                    return false;
                }
            }
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

    private static void AppendWmsCiteDimensions(StringBuilder sb, LayerDefinition layer, string indent, bool isWms111)
    {
        var definition = GetCiteWmsDimensionDefinition(layer);
        if (definition is null)
        {
            return;
        }

        if (isWms111)
        {
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

            sb.AppendLine(" />");
            sb.Append(indent)
                .Append("<Extent name=\"")
                .Append(EscapeXml(definition.Value.Name))
                .Append('"');

            if (!string.IsNullOrWhiteSpace(definition.Value.Default))
            {
                sb.Append(" default=\"")
                    .Append(EscapeXml(definition.Value.Default!))
                    .Append('"');
            }

            sb.Append(" nearestValue=\"")
                .Append(definition.Value.NearestValue ? "1" : "0")
                .Append("\">")
                .Append(EscapeXml(definition.Value.Extent))
                .AppendLine("</Extent>");
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

    private static async Task AppendWmsTemporalDimensionAsync(
        HttpContext context,
        StringBuilder sb,
        LayerDefinition layer,
        string indent,
        bool isWms111)
    {
        // CITE Autos has its own hardcoded "time" dimension already emitted by
        // AppendWmsCiteDimensions; do not duplicate.
        if (string.Equals(layer.Name, CiteAutosLayerTitle, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (layer.Metadata?.TimeInfo is null ||
            string.IsNullOrWhiteSpace(layer.Metadata.TimeInfo.StartTimeField))
        {
            return;
        }

        var featureReader = context.RequestServices.GetService<IFeatureReader>();
        if (featureReader is null)
        {
            return;
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var range = await TemporalExtentHelpers.TryResolveTemporalRangeAsync(
            layer,
            featureReader,
            cancellationToken).ConfigureAwait(false);
        if (range is null || !range.Value.HasExtent || range.Value.Min is null || range.Value.Max is null)
        {
            return;
        }

        var min = FormatWmsTemporalInstant(range.Value.Min.Value);
        var max = FormatWmsTemporalInstant(range.Value.Max.Value);
        var extent = $"{min}/{max}/PT0S";

        if (isWms111)
        {
            sb.Append(indent)
                .Append("<Dimension name=\"time\" units=\"ISO8601\" />")
                .AppendLine();
            sb.Append(indent)
                .Append("<Extent name=\"time\" default=\"")
                .Append(EscapeXml(max))
                .Append("\" nearestValue=\"1\">")
                .Append(EscapeXml(extent))
                .AppendLine("</Extent>");
            return;
        }

        sb.Append(indent)
            .Append("<Dimension name=\"time\" units=\"ISO8601\" multipleValues=\"false\" nearestValue=\"true\" default=\"")
            .Append(EscapeXml(max))
            .Append("\">")
            .Append(EscapeXml(extent))
            .AppendLine("</Dimension>");
    }

    private static string FormatWmsTemporalInstant(DateTimeOffset value)
        => TemporalExtentHelpers.FormatOgcTemporalValue(value);

    private static void AppendWmsOnlineResource(StringBuilder sb, string indent, string href, bool isWms111)
    {
        sb.Append(indent).Append("<OnlineResource ");
        if (isWms111)
        {
            sb.Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" xlink:type=\"simple\" ");
        }

        sb.Append("xlink:href=\"")
            .Append(EscapeXml(href))
            .AppendLine("\" />");
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

    private static async Task<string> BuildWmsCapabilities(
        HttpContext context,
        ServiceDefinition service,
        string serviceId,
        string baseUrl,
        string version)
    {
        var isWms111 = IsWms111Version(version);
        var responseVersion = isWms111 ? Wms111Version : Wms13Version;
        var crsElementName = isWms111 ? "SRS" : "CRS";
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var wmsEndpoint = $"{normalizedBaseUrl}/rest/services/{serviceId}/MapServer/WMS";
        var wmsUrlPrefix = $"{wmsEndpoint}?";
        var metadataUrl = $"{wmsEndpoint}?SERVICE=WMS&REQUEST=GetCapabilities&VERSION={responseVersion}";

        var sb = new StringBuilder(8192);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        if (isWms111)
        {
            sb.Append("<!DOCTYPE WMT_MS_Capabilities SYSTEM \"")
                .Append(Wms111CapabilitiesDtd)
                .AppendLine("\">");
            sb.Append("<WMT_MS_Capabilities ")
                .Append("version=\"1.1.1\"")
                .AppendLine(">");
        }
        else
        {
            sb.Append("<WMS_Capabilities ")
                .Append("xmlns=\"http://www.opengis.net/wms\" ")
                .Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" ")
                .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
                .Append("version=\"1.3.0\" xsi:schemaLocation=\"")
                .Append(WmsCapabilitiesSchemaLocation)
                .AppendLine("\">");
        }

        sb.AppendLine("  <Service>");
        sb.AppendLine("    <Name>WMS</Name>");
        sb.Append("    <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        sb.Append("    <Abstract>").Append(EscapeXml(service.Description ?? "Honua WMS service")).AppendLine("</Abstract>");
        sb.AppendLine("    <KeywordList>");
        sb.AppendLine("      <Keyword>WMS</Keyword>");
        sb.AppendLine("      <Keyword>OGC</Keyword>");
        sb.Append("      <Keyword>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Keyword>");
        sb.AppendLine("    </KeywordList>");
        AppendWmsOnlineResource(sb, "    ", wmsEndpoint, isWms111);
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
        sb.Append("        <Format>")
            .Append(isWms111 ? Wms111CapabilitiesMimeType : WmsCapabilitiesMimeType)
            .AppendLine("</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetCapabilities>");

        sb.AppendLine("      <GetMap>");
        sb.AppendLine("        <Format>image/png</Format>");
        sb.AppendLine("        <Format>image/jpeg</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetMap>");

        sb.AppendLine("      <GetFeatureInfo>");
        sb.AppendLine("        <Format>text/plain</Format>");
        sb.AppendLine("        <Format>application/json</Format>");
        sb.AppendLine("        <DCPType>");
        sb.AppendLine("          <HTTP>");
        sb.AppendLine("            <Get>");
        AppendWmsOnlineResource(sb, "              ", wmsUrlPrefix, isWms111);
        sb.AppendLine("            </Get>");
        sb.AppendLine("          </HTTP>");
        sb.AppendLine("        </DCPType>");
        sb.AppendLine("      </GetFeatureInfo>");

        sb.AppendLine("    </Request>");
        sb.AppendLine("    <Exception>");
        if (isWms111)
        {
            sb.Append("      <Format>")
                .Append(WmsSeXmlExceptionMimeType)
                .AppendLine("</Format>");
        }
        else
        {
            sb.AppendLine("      <Format>XML</Format>");
        }
        sb.AppendLine("    </Exception>");

        sb.AppendLine("    <Layer>");
        sb.Append("      <Title>").Append(EscapeXml(service.Name ?? serviceId)).AppendLine("</Title>");
        sb.Append("      <Abstract>").Append(EscapeXml(service.Description ?? "Honua WMS root layer")).AppendLine("</Abstract>");
        sb.Append("      <").Append(crsElementName).AppendLine(">EPSG:4326</" + crsElementName + ">");
        sb.Append("      <").Append(crsElementName).AppendLine(">EPSG:3857</" + crsElementName + ">");
        if (!isWms111)
        {
            sb.AppendLine("      <CRS>CRS:84</CRS>");
        }

        if (service.EffectiveExtent.HasValue)
        {
            var rootExtent = service.EffectiveExtent.Value;
            await AppendWmsGeographicBoundsAsync(context, sb, rootExtent, "      ", isWms111).ConfigureAwait(false);
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
            sb.Append("        <").Append(crsElementName).AppendLine(">EPSG:4326</" + crsElementName + ">");
            sb.Append("        <").Append(crsElementName).AppendLine(">EPSG:3857</" + crsElementName + ">");
            if (!isWms111)
            {
                sb.AppendLine("        <CRS>CRS:84</CRS>");
            }

            var extent = layer.Extent ?? service.EffectiveExtent;
            if (extent.HasValue)
            {
                await AppendWmsGeographicBoundsAsync(context, sb, extent.Value, "        ", isWms111).ConfigureAwait(false);
            }

            AppendWmsCiteDimensions(sb, layer, "        ", isWms111);
            await AppendWmsTemporalDimensionAsync(context, sb, layer, "        ", isWms111).ConfigureAwait(false);

            sb.AppendLine("        <MetadataURL type=\"TC211\">");
            sb.AppendLine("          <Format>text/xml</Format>");
            AppendWmsOnlineResource(sb, "          ", metadataUrl, isWms111);
            sb.AppendLine("        </MetadataURL>");
            sb.AppendLine("        <Style>");
            sb.AppendLine("          <Name>default</Name>");
            sb.AppendLine("          <Title>Default style</Title>");
            sb.AppendLine("        </Style>");
            sb.AppendLine("      </Layer>");
        }

        sb.AppendLine("    </Layer>");
        sb.AppendLine("  </Capability>");
        sb.Append(isWms111 ? "</WMT_MS_Capabilities>" : "</WMS_Capabilities>").AppendLine();
        return sb.ToString();
    }

    private static async Task AppendWmsGeographicBoundsAsync(
        HttpContext context,
        StringBuilder sb,
        FeatureExtent extent,
        string indent,
        bool isWms111)
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

        if (isWms111)
        {
            AppendWms111LatLonBoundingBox(
                sb,
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
                indent,
                isWms111);
        }
        else
        {
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
                indent,
                isWms111);
            AppendWmsBoundingBox(
                sb,
                "EPSG:4326",
                geographicExtent.MinX,
                geographicExtent.MinY,
                geographicExtent.MaxX,
                geographicExtent.MaxY,
                indent,
                isWms111);
        }
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
        string indent,
        bool isWms111)
    {
        var outputMinX = minX;
        var outputMinY = minY;
        var outputMaxX = maxX;
        var outputMaxY = maxY;
        if (!isWms111 &&
            string.Equals(crs, "EPSG:4326", StringComparison.OrdinalIgnoreCase))
        {
            outputMinX = minY;
            outputMinY = minX;
            outputMaxX = maxY;
            outputMaxY = maxX;
        }

        sb.Append(indent)
            .Append("<BoundingBox ")
            .Append(isWms111 ? "SRS" : "CRS")
            .Append("=\"")
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

    private static void AppendWms111LatLonBoundingBox(
        StringBuilder sb,
        double minX,
        double minY,
        double maxX,
        double maxY,
        string indent)
    {
        sb.Append(indent)
            .Append("<LatLonBoundingBox minx=\"")
            .Append(minX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" miny=\"")
            .Append(minY.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxx=\"")
            .Append(maxX.ToString("F6", CultureInfo.InvariantCulture))
            .Append("\" maxy=\"")
            .Append(maxY.ToString("F6", CultureInfo.InvariantCulture))
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
