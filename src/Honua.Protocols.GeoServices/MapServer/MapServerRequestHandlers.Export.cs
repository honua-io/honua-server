// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using SkiaSharp;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const int MaxImageDimension = 4096;
    private const int DefaultImageWidth = 400;
    private const int DefaultImageHeight = 400;
    private const int DefaultDpi = 96;
    private const string InvalidExportRequestMessage = "Invalid export request parameters.";
    private const string InvalidLayerTimeOptionsJsonMessage = "layerTimeOptions contains invalid JSON.";
    private const string InvalidDynamicLayersJsonMessage = "dynamicLayers contains invalid JSON.";
    private const string InvalidTimeParameterMessage = "Invalid time parameter.";
    private const string InvalidSpatialReferenceMessage = "Invalid spatial reference.";

    /// <summary>
    /// Handle MapServer export (map image generation) requests.
    /// </summary>
    private static async Task<IResult> HandleExport(HttpContext context)
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
            var earlyServiceError = await TryValidateMapServerServiceAsync(serviceId, context);
            if (earlyServiceError is not null)
            {
                return earlyServiceError;
            }

            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                if (GeoServicesRequestValueHelpers.TryGetUnsupportedMediaType(readError, out var receivedContentType))
                {
                    return GeoServicesRequestValueHelpers.CreateUnsupportedRequestContentTypeResult(context, receivedContentType);
                }

                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var responseFormat = GetValue(values, "f") ?? "image";
            if (!IsSupportedExportResponseFormat(responseFormat))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Output format '{responseFormat}' is not supported.");
            }

            var bboxValue = GetValue(values, "bbox");

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

            var maxDimensionW = service.Settings?.MaxImageWidth ?? MaxImageDimension;
            var maxDimensionH = service.Settings?.MaxImageHeight ?? MaxImageDimension;
            var defaultWidth = DefaultImageWidth;
            var defaultHeight = DefaultImageHeight;
            var defaultDpi = service.Settings?.DefaultDpi ?? DefaultDpi;
            var defaultFormat = service.Settings?.DefaultFormat ?? "png";
            var defaultTransparent = false;
            var maxFeatures = service.Settings?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;

            if (!TryParseSize(GetValue(values, "size"), defaultWidth, defaultHeight, out var imageWidth, out var imageHeight, out var sizeError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, sizeError ?? "Invalid size parameter.");
            }

            if (!TryParseDpi(GetValue(values, "dpi"), defaultDpi, out var dpi, out var dpiError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, dpiError ?? "Invalid dpi parameter.");
            }

            if (!TryParseTransparent(GetValue(values, "transparent"), defaultTransparent, out var transparent, out var transparentError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, transparentError ?? "Invalid transparent parameter.");
            }

            var formatRaw = GetValue(values, "format");
            if (string.IsNullOrWhiteSpace(formatRaw))
            {
                formatRaw = defaultFormat;
            }

            if (!TryNormalizeImageFormat(formatRaw, out var imageFormat, out var formatError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Invalid format parameter.");
            }

            var bboxSrRaw = GetValue(values, "bboxSR");
            var imageSrRaw = GetValue(values, "imageSR");
            var bboxSrid = TryParseSrid(bboxSrRaw);
            if (!string.IsNullOrWhiteSpace(bboxSrRaw) && !bboxSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid bboxSR parameter.");
            }

            var imageSrid = TryParseSrid(imageSrRaw);
            if (!string.IsNullOrWhiteSpace(imageSrRaw) && !imageSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid imageSR parameter.");
            }

            var serviceSrid = ResolveExportServiceSrid(service, publishedLayers);
            bboxSrid ??= serviceSrid;
            imageSrid ??= serviceSrid;
            var bboxCrsDefinition = SpatialReferenceHelpers.TryParseCrsDefinition(
                bboxSrRaw ?? bboxSrid.Value.ToString(CultureInfo.InvariantCulture),
                out var resolvedBboxDefinition)
                ? resolvedBboxDefinition
                : new CrsDefinition(
                    string.Empty,
                    bboxSrid.Value,
                    AxisOrder.EastNorth,
                    SpatialReference.Create(bboxSrid.Value).IsGeographic);

            if (!TryParseBbox(bboxValue, bboxCrsDefinition.AxisOrder, bboxCrsDefinition.IsGeographic, out var extent))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid or missing bbox parameter. Expected format: xmin,ymin,xmax,ymax");
            }

            var transformResult = await TryTransformExtentAsync(
                context,
                extent,
                bboxSrid.Value,
                imageSrid.Value,
                cancellationToken);
            if (!transformResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    transformResult.Error ?? "Invalid spatial reference.");
            }
            var renderExtent = transformResult.Extent;

            imageWidth = Math.Clamp(imageWidth, 1, maxDimensionW);
            imageHeight = Math.Clamp(imageHeight, 1, maxDimensionH);

            var backgroundColorValue = GetValue(values, "backgroundColor");
            SKColor? backgroundColor = null;
            if (!string.IsNullOrWhiteSpace(backgroundColorValue))
            {
                if (!TryParseRgbList(backgroundColorValue, out var parsedColor))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid backgroundColor parameter. Expected format: r,g,b or r,g,b,a");
                }

                backgroundColor = parsedColor;
            }

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            if (!TryParseLayerDefs(GetValue(values, "layerDefs"), queryValidator, out var layerDefs, out var layerDefsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    layerDefsError ?? "Invalid layerDefs parameter.");
            }

            if (!TryParseLayerTimeOptions(GetValue(values, "layerTimeOptions"), out var layerTimeOptions, out var layerTimeOptionsError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    layerTimeOptionsError ?? "Invalid layerTimeOptions parameter.");
            }

            var knownLayerIds = publishedLayers
                .Select(static layer => layer.PublicLayerId)
                .ToHashSet();
            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), knownLayerIds, queryValidator, out var dynamicLayers, out var dynamicLayersError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    dynamicLayersError ?? "Invalid dynamicLayers parameter.");
            }

            var timeValue = GetValue(values, "time");
            var timeRelationValue = NormalizeTimeRelation(GetValue(values, "timeRelation"));
            var layersValue = GetValue(values, "layers");
            if (HasEmptyLayerToken(layersValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter contains an empty layer id.");
            }

            if (HasNonIntegerExportLayerToken(layersValue))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "layers parameter must contain integer layer ids.");
            }

            MapServerLog.ExportRequested(logger, serviceId, imageWidth, imageHeight);
            var stopwatch = Stopwatch.StartNew();
            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.MapServerExport, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.MapServer);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "export");
            activity?.SetTag("honua.mapserver.width", imageWidth);
            activity?.SetTag("honua.mapserver.height", imageHeight);
            activity?.SetTag("honua.mapserver.format", imageFormat);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publishedLayers.Select(static layer => layer.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            var parameters = new ExportParameters
            {
                Bbox = bboxValue,
                Size = GetValue(values, "size"),
                Dpi = dpi,
                Format = imageFormat,
                Transparent = transparent,
                Layers = layersValue,
                BboxSr = bboxSrRaw,
                ImageSr = imageSrRaw,
                F = responseFormat,
                BackgroundColor = backgroundColorValue
            };

            await using var renderLease = await context.RequestServices
                .GetRequiredService<RasterRenderCapacityLimiter>()
                .TryAcquireAsync(imageWidth, imageHeight, cancellationToken)
                .ConfigureAwait(false);
            if (renderLease is null)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    RasterRenderCapacityLimiter.CapacityExceededMessage,
                    RasterRenderCapacityLimiter.RetryAfterSeconds);
            }

            var (renderLayers, renderLayersError) = ResolveRenderLayers(
                publishedLayers,
                service,
                parameters.Layers,
                dynamicLayers,
                context);
            if (renderLayersError is not null)
            {
                return renderLayersError;
            }

            if (renderLayers.Length == 0)
            {
                using var renderer = new SkiaMapRenderer();
                var emptyImage = renderer.RenderMap(
                    [],
                    [],
                    renderExtent,
                    imageWidth,
                    imageHeight,
                    parameters.Transparent,
                    backgroundColor,
                    MetadataV2GeometryType.None);

                stopwatch.Stop();
                MapServerLog.ExportCompleted(logger, serviceId, 0, stopwatch.Elapsed.TotalMilliseconds);
                HonuaTelemetry.SetSuccess(activity, 0);
                HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
                activity?.SetTag("honua.mapserver.layer_count", 0);

                return await ReturnImageResultAsync(
                    emptyImage,
                    parameters,
                    imageWidth,
                    imageHeight,
                    dpi,
                    renderExtent,
                    imageSrid.Value,
                    context,
                    cancellationToken);
            }

            var scaleDenominator = CoordinateTransformer.CalculateScaleDenominator(extent, imageWidth, dpi, bboxSrid.Value);

            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
            var filterExpressionService = context.RequestServices.GetRequiredService<IFilterExpressionService>();

            var totalFeatureCount = 0;
            var spatialFilter = CreateBboxSpatialFilter(extent, bboxSrid.Value);

            using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface.");
            }

            var canvas = surface.Canvas;

            if (parameters.Transparent)
            {
                canvas.Clear(SKColors.Transparent);
            }
            else
            {
                canvas.Clear(backgroundColor ?? SKColors.White);
            }

            var transform = SkiaMapRenderer.BuildTransform(renderExtent, imageWidth, imageHeight);

            foreach (var renderLayer in renderLayers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var layer = renderLayer.Layer;
                var resource = layer.Resource;
                if (!IsLayerVisibleAtScale(layer, scaleDenominator))
                {
                    continue;
                }

                if (!HasMapServerGeometry(resource))
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.PublicLayerId, out var layerDef);
                var combinedDefinition = CombineDefinitionExpressions(renderLayer.DefinitionExpression, layerDef);

                if (!TryGetEffectiveTimeParameters(
                        timeValue,
                        timeRelationValue,
                        renderLayer,
                        layerTimeOptions,
                        out var effectiveTime,
                        out var effectiveTimeRelation,
                        out var timeError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        timeError ?? "Invalid time parameter.");
                }

                if (!TryBuildLayerSqlFilter(
                        filterExpressionService,
                        resource,
                        combinedDefinition,
                        effectiveTime,
                        effectiveTimeRelation,
                        out var sqlFilter,
                        out var filterError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        filterError ?? "Invalid filter parameter.");
                }

                var stylePlan = await GetRasterStylePlanAsync(
                    styleCatalog,
                    layer.StorageLayerId,
                    cancellationToken).ConfigureAwait(false);
                var featureQuery = CreateRasterFeatureQuery(
                    stylePlan,
                    spatialFilter,
                    serviceSrid,
                    imageSrid,
                    maxFeatures,
                    sqlFilter);
                var geometryType = resource.ReadGeometryType();

                var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                    canvas,
                    featureReader,
                    layer.StorageLayerId,
                    geometryType,
                    stylePlan,
                    featureQuery,
                    renderExtent,
                    imageWidth,
                    imageHeight,
                    transform,
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
                RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, geometryType);
            }

            stopwatch.Stop();
            MapServerLog.ExportCompleted(logger, serviceId, totalFeatureCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetTag("honua.mapserver.layer_count", renderLayers.Length);

            var imageBytes = SkiaMapRenderer.EncodeSurface(surface, parameters.Format);
            return await ReturnImageResultAsync(
                imageBytes,
                parameters,
                imageWidth,
                imageHeight,
                dpi,
                renderExtent,
                imageSrid.Value,
                context,
                cancellationToken);
        }
        catch (ArgumentException ex)
        {
            MapServerLog.ExportFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(context, InvalidExportRequestMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MapServerLog.ExportFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "MapServer export failed.");
        }
    }

    private static async Task<IResult> ReturnImageResultAsync(
        byte[] imageBytes,
        ExportParameters parameters,
        int imageWidth,
        int imageHeight,
        int dpi,
        SkiaMapRenderer.RenderExtent extent,
        int imageSrid,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var isJsonResponse = string.Equals(parameters.F, "json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameters.F, "pjson", StringComparison.OrdinalIgnoreCase);
        if (isJsonResponse)
        {
            var imageContentType = SkiaMapRenderer.GetContentType(parameters.Format);
            var temporaryFileService = context.RequestServices.GetRequiredService<ITemporaryFileService>();
            string imageUrl;
            try
            {
                imageUrl = await temporaryFileService.StoreTemporaryFileAsync(
                    imageBytes,
                    imageContentType,
                    TimeSpan.FromHours(1),
                    principal: context.User,
                    cancellationToken: cancellationToken);
            }
            catch (TemporaryStorageLimitExceededException ex)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    "Temporary export storage is currently at capacity. Please retry shortly.",
                    ex.RetryAfterSeconds);
            }

            var scale = CoordinateTransformer.CalculateScaleDenominator(extent, imageWidth, dpi, imageSrid);

            var response = new ExportImageResponse
            {
                Href = imageUrl,
                Width = imageWidth,
                Height = imageHeight,
                Extent = new EsriExtent
                {
                    Xmin = extent.MinX,
                    Ymin = extent.MinY,
                    Xmax = extent.MaxX,
                    Ymax = extent.MaxY,
                    SpatialReference = new EsriSpatialReference
                    {
                        Wkid = imageSrid
                    }
                },
                Scale = scale
            };

            return Results.Json(response, MapServerJsonContext.Default.ExportImageResponse, contentType: "application/json");
        }

        var responseContentType = SkiaMapRenderer.GetContentType(parameters.Format);
        return Results.Bytes(imageBytes, responseContentType);
    }

    private static bool TryParseBbox(
        string? bbox,
        AxisOrder axisOrder,
        bool isGeographic,
        out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        if (bbox is null)
        {
            return false;
        }

        if (!TryParseBoundingBoxWithMapServerAxisFallback(
                bbox,
                axisOrder,
                isGeographic,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            return false;
        }

        if (isGeographic &&
            (minX < -180.0 || minX > 180.0 ||
             maxX < -180.0 || maxX > 180.0 ||
             minY < -90.0 || minY > 90.0 ||
             maxY < -90.0 || maxY > 90.0))
        {
            return false;
        }

        if (minY >= maxY)
            return false;

        if (minX >= maxX && !CoordinateTransformer.IsWrappedGeographicExtent(new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY)))
            return false;

        extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryParseBoundingBoxWithMapServerAxisFallback(
        string bbox,
        AxisOrder axisOrder,
        bool isGeographic,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        if (RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                axisOrder,
                isGeographic,
                out minX,
                out minY,
                out maxX,
                out maxY))
        {
            return true;
        }

        if (!isGeographic)
        {
            return false;
        }

        var fallbackAxisOrder = axisOrder == AxisOrder.NorthEast
            ? AxisOrder.EastNorth
            : AxisOrder.NorthEast;
        return RasterParsingHelpers.TryParseBoundingBox(
            bbox,
            fallbackAxisOrder,
            isGeographic,
            out minX,
            out minY,
            out maxX,
            out maxY);
    }

    private static bool TryParseSize(
        string? size,
        int defaultWidth,
        int defaultHeight,
        out int width,
        out int height,
        out string? error)
    {
        width = defaultWidth;
        height = defaultHeight;
        error = null;

        if (string.IsNullOrWhiteSpace(size))
        {
            return true;
        }

        var parts = size.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            error = "Invalid size parameter. Expected format: width,height";
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) || width <= 0)
        {
            error = "Invalid size width.";
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height <= 0)
        {
            error = "Invalid size height.";
            return false;
        }

        return true;
    }

    private static bool TryParseDpi(string? dpiValue, int defaultDpi, out int dpi, out string? error)
    {
        dpi = defaultDpi;
        error = null;

        if (string.IsNullOrWhiteSpace(dpiValue))
        {
            return true;
        }

        if (!int.TryParse(dpiValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out dpi) || dpi <= 0)
        {
            error = "Invalid dpi parameter.";
            return false;
        }

        return true;
    }

    private static bool TryParseTransparent(string? transparentValue, bool defaultValue, out bool transparent, out string? error)
    {
        transparent = defaultValue;
        error = null;

        if (string.IsNullOrWhiteSpace(transparentValue))
        {
            return true;
        }

        if (bool.TryParse(transparentValue, out var parsed))
        {
            transparent = parsed;
            return true;
        }

        if (int.TryParse(transparentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            transparent = numeric != 0;
            return true;
        }

        error = "Invalid transparent parameter.";
        return false;
    }

    private static bool IsSupportedExportResponseFormat(string format)
    {
        return string.Equals(format, "image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeImageFormat(string? format, out string normalized, out string? error)
    {
        normalized = "png";
        error = null;

        if (string.IsNullOrWhiteSpace(format))
        {
            return true;
        }

        var candidate = format.Trim().ToLowerInvariant();
        switch (candidate)
        {
            case "png":
            case "png8":
            case "png24":
            case "png32":
            case "jpg":
            case "jpeg":
                // gif is intentionally not accepted: SkiaSharp's default native build ships
                // no GIF encoder, so attempting it threw and surfaced as a 500. Reject it as
                // an unsupported format (clean 400) via the default branch below.
                normalized = candidate;
                return true;
            default:
                error = $"Output format '{format}' is not supported.";
                return false;
        }
    }

    private sealed record LayerTimeOptions(
        bool? UseTime,
        bool TimeDataCumulative,
        double? TimeOffset,
        string? TimeOffsetUnits,
        string? Time,
        string? TimeRelation);

    private sealed record DynamicLayerDefinition(
        int Id,
        int MapLayerId,
        string? DefinitionExpression);

    private sealed record ExportRenderLayer(
        MapServerMetadataLayerDescriptor Layer,
        int Id,
        string? DefinitionExpression);

    private static bool TryParseLayerDefs(
        string? layerDefsValue,
        ICommonQueryValidator queryValidator,
        out Dictionary<int, string?> layerDefs,
        out string? error)
        => LayerDefsParser.TryParse(layerDefsValue, queryValidator, out layerDefs, out error);

    private static bool TryParseLayerTimeOptions(
        string? layerTimeOptionsValue,
        out Dictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? error)
    {
        layerTimeOptions = new Dictionary<int, LayerTimeOptions>();
        error = null;

        if (string.IsNullOrWhiteSpace(layerTimeOptionsValue))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(layerTimeOptionsValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "layerTimeOptions must be a JSON object.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
                {
                    error = $"Invalid layer id '{property.Name}' in layerTimeOptions.";
                    return false;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"Invalid layerTimeOptions value for layer '{property.Name}'. Expected a JSON object.";
                    return false;
                }

                bool? useTime = null;
                var timeDataCumulative = false;
                double? timeOffset = null;
                string? timeOffsetUnits = null;
                string? time = null;
                string? timeRelation = null;

                foreach (var option in property.Value.EnumerateObject())
                {
                    switch (option.Name.ToLowerInvariant())
                    {
                        case "usetime":
                            if (option.Value.ValueKind == JsonValueKind.True ||
                                option.Value.ValueKind == JsonValueKind.False)
                            {
                                useTime = option.Value.GetBoolean();
                            }
                            else if (option.Value.ValueKind != JsonValueKind.Null)
                            {
                                error = $"Invalid useTime value for layer '{property.Name}'. Expected a boolean.";
                                return false;
                            }

                            break;
                        case "timedatacumulative":
                            if (option.Value.ValueKind == JsonValueKind.True ||
                                option.Value.ValueKind == JsonValueKind.False)
                            {
                                timeDataCumulative = option.Value.GetBoolean();
                            }
                            else if (option.Value.ValueKind != JsonValueKind.Null)
                            {
                                error = $"Invalid timeDataCumulative value for layer '{property.Name}'. Expected a boolean.";
                                return false;
                            }

                            break;
                        case "timeoffset":
                            if (option.Value.ValueKind == JsonValueKind.Number)
                            {
                                timeOffset = option.Value.GetDouble();
                            }
                            else if (option.Value.ValueKind == JsonValueKind.String &&
                                double.TryParse(option.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOffset))
                            {
                                timeOffset = parsedOffset;
                            }
                            else if (option.Value.ValueKind != JsonValueKind.Null)
                            {
                                error = $"Invalid timeOffset value for layer '{property.Name}'. Expected a number.";
                                return false;
                            }

                            break;
                        case "timeoffsetunits":
                            if (option.Value.ValueKind == JsonValueKind.String)
                            {
                                timeOffsetUnits = option.Value.GetString();
                            }
                            else if (option.Value.ValueKind != JsonValueKind.Null)
                            {
                                error = $"Invalid timeOffsetUnits value for layer '{property.Name}'. Expected a string.";
                                return false;
                            }

                            break;
                        case "time":
                            if (!TryReadJsonStringOrNumber(option.Value, out time))
                            {
                                error = $"Invalid time value for layer '{property.Name}'. Expected a string or number.";
                                return false;
                            }

                            break;
                        case "timerelation":
                            if (option.Value.ValueKind == JsonValueKind.String)
                            {
                                timeRelation = option.Value.GetString();
                            }
                            else if (option.Value.ValueKind != JsonValueKind.Null)
                            {
                                error = $"Invalid timeRelation value for layer '{property.Name}'. Expected a string.";
                                return false;
                            }

                            break;
                    }
                }

                layerTimeOptions[layerId] = new LayerTimeOptions(
                    useTime,
                    timeDataCumulative,
                    timeOffset,
                    timeOffsetUnits,
                    string.IsNullOrWhiteSpace(time) ? null : time,
                    string.IsNullOrWhiteSpace(timeRelation) ? null : timeRelation);
            }

            return true;
        }
        catch (JsonException)
        {
            error = InvalidLayerTimeOptionsJsonMessage;
            return false;
        }
    }

    private static bool TryParseDynamicLayers(
        string? dynamicLayersValue,
        HashSet<int> knownLayerIds,
        ICommonQueryValidator queryValidator,
        out List<DynamicLayerDefinition> dynamicLayers,
        out string? error)
    {
        dynamicLayers = new List<DynamicLayerDefinition>();
        error = null;

        if (string.IsNullOrWhiteSpace(dynamicLayersValue))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(dynamicLayersValue);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "dynamicLayers must be a JSON array.";
                return false;
            }

            var seenIds = new HashSet<int>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    error = "dynamicLayers entries must be JSON objects.";
                    return false;
                }

                if (!TryGetJsonInt(element, "id", out var id))
                {
                    error = "dynamicLayers entries must include an integer id.";
                    return false;
                }

                if (!seenIds.Add(id))
                {
                    error = $"Duplicate dynamic layer id '{id}'.";
                    return false;
                }

                if (!element.TryGetProperty("source", out var sourceElement) ||
                    sourceElement.ValueKind != JsonValueKind.Object)
                {
                    error = $"dynamicLayers entry '{id}' must include a source object.";
                    return false;
                }

                if (!TryGetJsonString(sourceElement, "type", out var sourceType) ||
                    !string.Equals(sourceType, "mapLayer", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"dynamicLayers entry '{id}' must use a mapLayer source.";
                    return false;
                }

                if (!TryGetJsonInt(sourceElement, "mapLayerId", out var mapLayerId))
                {
                    error = $"dynamicLayers entry '{id}' must include a mapLayerId.";
                    return false;
                }

                if (!knownLayerIds.Contains(mapLayerId))
                {
                    error = $"dynamicLayers entry '{id}' references unknown layer '{mapLayerId}'.";
                    return false;
                }

                string? definitionExpression = null;
                if (element.TryGetProperty("definitionExpression", out var definitionElement))
                {
                    if (!TryReadJsonStringOrNumber(definitionElement, out definitionExpression))
                    {
                        error = $"dynamicLayers entry '{id}' has an invalid definitionExpression.";
                        return false;
                    }
                }

                if (definitionExpression == null &&
                    element.TryGetProperty("layerDefinition", out var layerDefinitionElement) &&
                    layerDefinitionElement.ValueKind == JsonValueKind.Object &&
                    layerDefinitionElement.TryGetProperty("definitionExpression", out var nestedDefinition))
                {
                    if (!TryReadJsonStringOrNumber(nestedDefinition, out definitionExpression))
                    {
                        error = $"dynamicLayers entry '{id}' has an invalid layerDefinition.definitionExpression.";
                        return false;
                    }
                }

                if (!string.IsNullOrWhiteSpace(definitionExpression))
                {
                    var validation = queryValidator.ValidateWhereClause(definitionExpression);
                    if (!validation.IsValid)
                    {
                        error = validation.ErrorMessage ?? $"Invalid definitionExpression for layer '{id}'.";
                        return false;
                    }
                }

                dynamicLayers.Add(new DynamicLayerDefinition(
                    id,
                    mapLayerId,
                    string.IsNullOrWhiteSpace(definitionExpression) ? null : definitionExpression));
            }

            return true;
        }
        catch (JsonException)
        {
            error = InvalidDynamicLayersJsonMessage;
            return false;
        }
    }

    private static (ExportRenderLayer[] Layers, IResult? Error) ResolveRenderLayers(
        IReadOnlyList<MapServerMetadataLayerDescriptor> publishedLayers,
        MetadataV2Service service,
        string? layersParam,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        HttpContext context)
    {
        var layerLookup = publishedLayers.ToDictionary(static layer => layer.PublicLayerId);
        if (dynamicLayers.Count == 0)
        {
            var accessibleLayers = publishedLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();

            if (string.IsNullOrWhiteSpace(layersParam))
            {
                return (accessibleLayers
                    .Where(static layer => layer.Resource.Display?.DefaultVisibility ?? true)
                    .Select(static layer => new ExportRenderLayer(layer, layer.PublicLayerId, null))
                    .ToArray(), null);
            }

            var spec = layersParam.Trim();
            var (visibility, requestedStaticLayerIds) = ParseLayerVisibilitySpec(spec);
            if (requestedStaticLayerIds.Count == 0)
            {
                return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(context, "Invalid layers parameter."));
            }

            var accessibleLayerIds = accessibleLayers.Select(static layer => layer.PublicLayerId).ToHashSet();
            if (requestedStaticLayerIds.Any(id => !accessibleLayerIds.Contains(id)))
            {
                return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            // Apply GeoServices visibility semantics:
            //   show / (bare list) -> only the requested layers
            //   hide               -> all accessible layers except the requested ones
            //   include            -> default-visible layers plus the requested ones
            //   exclude            -> default-visible layers minus the requested ones
            bool LayerSelected(MapServerMetadataLayerDescriptor layer)
            {
                var requested = requestedStaticLayerIds.Contains(layer.PublicLayerId);
                var defaultVisible = layer.Resource.Display?.DefaultVisibility ?? true;
                return visibility switch
                {
                    ExportLayerVisibility.Show => requested,
                    ExportLayerVisibility.Hide => !requested,
                    ExportLayerVisibility.Include => defaultVisible || requested,
                    ExportLayerVisibility.Exclude => defaultVisible && !requested,
                    _ => requested,
                };
            }

            return (accessibleLayers
                .Where(LayerSelected)
                .Select(static layer => new ExportRenderLayer(layer, layer.PublicLayerId, null))
                .ToArray(), null);
        }

        IEnumerable<DynamicLayerDefinition> selected = dynamicLayers;
        HashSet<int>? requestedIds = null;

        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var spec = layersParam.Trim();
            var (visibility, ids) = ParseLayerVisibilitySpec(spec);
            requestedIds = ids;
            selected = visibility switch
            {
                ExportLayerVisibility.Hide => dynamicLayers.Where(layer => !ids.Contains(layer.Id)),
                ExportLayerVisibility.Include => dynamicLayers.Where(layer =>
                    layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer) &&
                    ((mapLayer.Resource.Display?.DefaultVisibility ?? true) || ids.Contains(layer.Id))),
                ExportLayerVisibility.Exclude => dynamicLayers.Where(layer =>
                    layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer) &&
                    (mapLayer.Resource.Display?.DefaultVisibility ?? true) && !ids.Contains(layer.Id)),
                _ => dynamicLayers.Where(layer => ids.Contains(layer.Id)),
            };

            if (requestedIds is { Count: > 0 })
            {
                var dynamicLayerIds = dynamicLayers.Select(layer => layer.Id).ToHashSet();
                if (requestedIds.Any(id => !dynamicLayerIds.Contains(id)))
                {
                    return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(
                        context,
                        "layers parameter references an invalid or inaccessible layer."));
                }

                foreach (var requestedId in requestedIds)
                {
                    var dynamicLayer = dynamicLayers.FirstOrDefault(layer => layer.Id == requestedId);
                    if (dynamicLayer is null ||
                        !layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var requestedMapLayer) ||
                        !AccessPolicyHelpers.IsResourceAccessible(context, requestedMapLayer.Resource, service))
                    {
                        return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(
                            context,
                            "layers parameter references an invalid or inaccessible layer."));
                    }
                }
            }
        }

        var renderLayers = new List<ExportRenderLayer>();
        foreach (var dynamicLayer in selected)
        {
            if (!layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var layer))
            {
                return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
            {
                return (Array.Empty<ExportRenderLayer>(), StandardErrorHelpers.CreateBadRequest(
                    context,
                    "layers parameter references an invalid or inaccessible layer."));
            }

            renderLayers.Add(new ExportRenderLayer(layer, dynamicLayer.Id, dynamicLayer.DefinitionExpression));
        }

        return (renderLayers.ToArray(), null);
    }

    private static string? NormalizeTimeRelation(string? timeRelation)
    {
        if (string.IsNullOrWhiteSpace(timeRelation))
        {
            return null;
        }

        var trimmed = timeRelation.Trim();
        if (string.Equals(trimmed, "esriTimeRelationAfterStartOverlapsEnd", StringComparison.OrdinalIgnoreCase))
        {
            return "esriTimeRelationOverlapsEndWithinStart";
        }

        return trimmed;
    }

    private static bool TryGetEffectiveTimeParameters(
        string? globalTime,
        string? globalTimeRelation,
        ExportRenderLayer renderLayer,
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? effectiveTime,
        out string? effectiveTimeRelation,
        out string? error)
    {
        effectiveTime = globalTime;
        effectiveTimeRelation = globalTimeRelation;
        error = null;

        LayerTimeOptions? options = null;
        if (TryResolveLayerTimeOptions(layerTimeOptions, renderLayer, out var resolvedOptions))
        {
            options = resolvedOptions;
            if (options.UseTime is false)
            {
                effectiveTime = null;
                effectiveTimeRelation = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(options.Time))
            {
                effectiveTime = options.Time;
            }

            if (!string.IsNullOrWhiteSpace(options.TimeRelation))
            {
                effectiveTimeRelation = options.TimeRelation;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveTime))
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (!GeoServicesTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
        {
            error = InvalidTimeParameterMessage;
            return false;
        }

        // time=null,null parses as both bounds null — the documented no-op
        // form. Treat it identically to an omitted time parameter so layer
        // rendering does not invent a degenerate filter.
        if (start is null && end is null && options?.TimeDataCumulative != true)
        {
            effectiveTime = null;
            effectiveTimeRelation = null;
            return true;
        }

        if (options?.TimeDataCumulative == true)
        {
            start = null;
        }

        if (options?.TimeOffset.HasValue == true)
        {
            if (!TryApplyTimeOffset(
                    start,
                    end,
                    options.TimeOffset.Value,
                    options.TimeOffsetUnits,
                    out var adjustedStart,
                    out var adjustedEnd,
                    out var offsetError))
            {
                error = offsetError ?? "Invalid time offset.";
                return false;
            }

            start = adjustedStart;
            end = adjustedEnd;
        }

        effectiveTime = BuildTimeParameter(start, end);
        effectiveTimeRelation = NormalizeTimeRelation(effectiveTimeRelation);
        return true;
    }

    private static bool TryResolveLayerTimeOptions(
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        ExportRenderLayer renderLayer,
        out LayerTimeOptions resolvedOptions)
    {
        if (layerTimeOptions.TryGetValue(renderLayer.Id, out var directOptions) &&
            directOptions is not null)
        {
            resolvedOptions = directOptions;
            return true;
        }

        if (renderLayer.Id != renderLayer.Layer.PublicLayerId &&
            layerTimeOptions.TryGetValue(renderLayer.Layer.PublicLayerId, out var sourceLayerOptions) &&
            sourceLayerOptions is not null)
        {
            resolvedOptions = sourceLayerOptions;
            return true;
        }

        resolvedOptions = default!;
        return false;
    }

    private static string BuildTimeParameter(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start.HasValue && end.HasValue)
        {
            var startValue = start.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var endValue = end.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return $"{startValue},{endValue}";
        }

        if (start.HasValue)
        {
            return start.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (end.HasValue)
        {
            var endValue = end.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return $",{endValue}";
        }

        throw new ArgumentException("Time parameter requires a start or end time.");
    }

    private static bool TryApplyTimeOffset(
        DateTimeOffset? start,
        DateTimeOffset? end,
        double offset,
        string? units,
        out DateTimeOffset? adjustedStart,
        out DateTimeOffset? adjustedEnd,
        out string? error)
    {
        adjustedStart = start;
        adjustedEnd = end;
        error = null;

        if (string.IsNullOrWhiteSpace(units))
        {
            error = "timeOffsetUnits is required when timeOffset is specified.";
            return false;
        }

        var normalizedUnits = units.Trim();
        var signedOffset = -offset;

        switch (normalizedUnits.ToLowerInvariant())
        {
            case "esritimeunitsyears":
            case "years":
            case "year":
                {
                    if (!TryToIntegerOffset(offset, out var intOffset, out error))
                    {
                        return false;
                    }

                    adjustedStart = start?.AddYears(-intOffset);
                    adjustedEnd = end?.AddYears(-intOffset);
                    return true;
                }
            case "esritimeunitsmonths":
            case "months":
            case "month":
                {
                    if (!TryToIntegerOffset(offset, out var intOffset, out error))
                    {
                        return false;
                    }

                    adjustedStart = start?.AddMonths(-intOffset);
                    adjustedEnd = end?.AddMonths(-intOffset);
                    return true;
                }
            case "esritimeunitsweeks":
            case "weeks":
            case "week":
                {
                    var span = TimeSpan.FromDays(7 * signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsdays":
            case "days":
            case "day":
                {
                    var span = TimeSpan.FromDays(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitshours":
            case "hours":
            case "hour":
                {
                    var span = TimeSpan.FromHours(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsminutes":
            case "minutes":
            case "minute":
                {
                    var span = TimeSpan.FromMinutes(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsseconds":
            case "seconds":
            case "second":
                {
                    var span = TimeSpan.FromSeconds(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            case "esritimeunitsmilliseconds":
            case "milliseconds":
            case "millisecond":
                {
                    var span = TimeSpan.FromMilliseconds(signedOffset);
                    adjustedStart = start?.Add(span);
                    adjustedEnd = end?.Add(span);
                    return true;
                }
            default:
                error = $"Unsupported timeOffsetUnits value '{units}'.";
                return false;
        }
    }

    private static bool TryToIntegerOffset(double offset, out int value, out string? error)
    {
        error = null;
        var rounded = Math.Round(offset, MidpointRounding.AwayFromZero);
        if (Math.Abs(offset - rounded) > 0.000001)
        {
            value = 0;
            error = "timeOffset for month/year units must be an integer.";
            return false;
        }

        value = (int)rounded;
        return true;
    }

    private static string? CombineDefinitionExpressions(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"({first}) AND ({second})";
    }

    private static bool TryReadJsonStringOrNumber(JsonElement element, out string? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            case JsonValueKind.Number:
                value = element.GetRawText();
                return true;
            case JsonValueKind.Null:
                value = null;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private static bool TryGetJsonInt(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetJsonString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        return false;
    }

    private static bool TryParseRgbList(string value, out SKColor color)
    {
        color = SKColors.White;

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]) ||
            (parts.Length == 4 && string.IsNullOrWhiteSpace(parts[3])))
        {
            return false;
        }

        if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        byte a = 255;
        if (parts.Length == 4 && !byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
        {
            return false;
        }

        color = new SKColor(r, g, b, a);
        return true;
    }

    private static HashSet<int> ParseLayerIds(string idList)
    {
        var ids = new HashSet<int>();
        foreach (var part in idList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Visibility prefix carried by a GeoServices <c>layers</c> parameter
    /// (<c>[show|hide|include|exclude]:id1,id2</c>). A bare id list is treated as <see cref="Show"/>.
    /// </summary>
    private enum ExportLayerVisibility
    {
        /// <summary>Render only the listed layers (also the meaning of a bare id list).</summary>
        Show,

        /// <summary>Render every layer except the listed ones.</summary>
        Hide,

        /// <summary>Render the default-visible layers plus the listed ones.</summary>
        Include,

        /// <summary>Render the default-visible layers minus the listed ones.</summary>
        Exclude,
    }

    /// <summary>
    /// Parses the optional visibility prefix from a GeoServices <c>layers</c> parameter and the
    /// associated layer ids. A bare id list (no prefix) is treated as <see cref="ExportLayerVisibility.Show"/>.
    /// </summary>
    private static (ExportLayerVisibility Visibility, HashSet<int> Ids) ParseLayerVisibilitySpec(string spec)
    {
        if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            return (ExportLayerVisibility.Show, ParseLayerIds(spec["show:".Length..]));
        }

        if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
        {
            return (ExportLayerVisibility.Hide, ParseLayerIds(spec["hide:".Length..]));
        }

        if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
        {
            return (ExportLayerVisibility.Include, ParseLayerIds(spec["include:".Length..]));
        }

        if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
        {
            return (ExportLayerVisibility.Exclude, ParseLayerIds(spec["exclude:".Length..]));
        }

        return (ExportLayerVisibility.Show, ParseLayerIds(spec));
    }

    private static bool HasEmptyLayerToken(string? layersParam)
    {
        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return false;
        }

        var spec = layersParam.Trim();
        if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["show:".Length..];
        }
        else if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["hide:".Length..];
        }
        else if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["include:".Length..];
        }
        else if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["exclude:".Length..];
        }

        foreach (var token in spec.Split(',', StringSplitOptions.None))
        {
            if (token.Trim().Length == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNonIntegerExportLayerToken(string? layersParam)
    {
        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return false;
        }

        var spec = layersParam.Trim();
        if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["show:".Length..];
        }
        else if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["hide:".Length..];
        }
        else if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["include:".Length..];
        }
        else if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
        {
            spec = spec["exclude:".Length..];
        }

        foreach (var token in spec.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static int ResolveExportServiceSrid(
        MetadataV2Service service,
        IReadOnlyList<MapServerMetadataLayerDescriptor> layers)
        => service.SpatialReference?.ResolveSrid()
            ?? layers.Select(static layer => layer.Resource.ReadSrid()).FirstOrDefault(static srid => srid.HasValue)
            ?? SpatialReference.WGS84.Wkid;

    private static bool IsLayerVisibleAtScale(MapServerMetadataLayerDescriptor layer, double scaleDenominator)
    {
        if (scaleDenominator <= 0)
        {
            return true;
        }

        var display = layer.Resource.Display;
        if (display?.MinScale is > 0 && scaleDenominator > display.MinScale.Value)
        {
            return false;
        }

        if (display?.MaxScale is > 0 && scaleDenominator < display.MaxScale.Value)
        {
            return false;
        }

        return true;
    }

}
