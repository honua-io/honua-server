// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.MapServer.Models;
using Honua.Server.Features.MapServer.Rendering;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using SkiaSharp;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private const int MaxImageDimension = 4096;
    private const int DefaultImageWidth = 400;
    private const int DefaultImageHeight = 400;
    private const int DefaultDpi = 96;
    private const int MaxFeaturesPerLayer = 10_000;
    private const string InvalidExportRequestMessage = "Invalid export request parameters.";
    private const string InvalidLayerDefsJsonMessage = "layerDefs contains invalid JSON.";
    private const string InvalidLayerTimeOptionsJsonMessage = "layerTimeOptions contains invalid JSON.";
    private const string InvalidDynamicLayersJsonMessage = "dynamicLayers contains invalid JSON.";
    private const string InvalidTimeParameterMessage = "Invalid time parameter.";
    private const string InvalidSpatialReferenceMessage = "Invalid spatial reference.";

    /// <summary>
    /// Handle MapServer export (map image generation) requests.
    /// </summary>
    private static async Task<IResult> HandleExport(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.MapServerEndpoints");

        try
        {
            var (values, readError) = await TryReadMapServerRequestValuesAsync(context);
            if (values == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
            }

            var responseFormat = GetValue(values, "f") ?? "image";
            if (!IsSupportedExportResponseFormat(responseFormat))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Output format '{responseFormat}' is not supported.");
            }

            var bboxValue = GetValue(values, "bbox");
            if (!TryParseBbox(bboxValue, out var extent))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid or missing bbox parameter. Expected format: xmin,ymin,xmax,ymax");
            }

            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
            if (!serviceResult.IsValid)
            {
                var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
                if (serviceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            var service = serviceResult.Resource!;
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            var mapConfig = service.Metadata?.MapServer;
            var maxDimensionW = mapConfig?.MaxImageWidth ?? MaxImageDimension;
            var maxDimensionH = mapConfig?.MaxImageHeight ?? MaxImageDimension;
            var defaultWidth = mapConfig?.DefaultImageWidth ?? DefaultImageWidth;
            var defaultHeight = mapConfig?.DefaultImageHeight ?? DefaultImageHeight;
            var defaultDpi = mapConfig?.DefaultDpi ?? DefaultDpi;
            var defaultFormat = mapConfig?.DefaultFormat ?? "png";
            var defaultTransparent = mapConfig?.DefaultTransparent ?? false;
            var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;

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

            var serviceSrid = service.SpatialReference.Srid;
            bboxSrid ??= serviceSrid;
            imageSrid ??= serviceSrid;

            var transformResult = await TryTransformExtentAsync(
                context,
                extent,
                bboxSrid.Value,
                imageSrid.Value,
                context.RequestAborted);
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

            if (!TryParseDynamicLayers(GetValue(values, "dynamicLayers"), service, queryValidator, out var dynamicLayers, out var dynamicLayersError))
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

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
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
                .TryAcquireAsync(imageWidth, imageHeight, context.RequestAborted)
                .ConfigureAwait(false);
            if (renderLease is null)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    RasterRenderCapacityLimiter.CapacityExceededMessage,
                    RasterRenderCapacityLimiter.RetryAfterSeconds);
            }

            var renderLayers = ResolveRenderLayers(service, parameters.Layers, dynamicLayers, context);
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
                    GeometryType.None);

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
                    context.RequestAborted);
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
                context.RequestAborted.ThrowIfCancellationRequested();

                var layer = renderLayer.Layer;
                if (!IsLayerVisibleAtScale(layer, scaleDenominator))
                {
                    continue;
                }

                if (!layer.HasGeometry)
                {
                    continue;
                }

                layerDefs.TryGetValue(layer.Id, out var layerDef);
                var combinedDefinition = CombineDefinitionExpressions(renderLayer.DefinitionExpression, layerDef);

                if (!TryGetEffectiveTimeParameters(
                        timeValue,
                        timeRelationValue,
                        layer,
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
                        layer,
                        combinedDefinition,
                        effectiveTime,
                        effectiveTimeRelation,
                        out var sqlFilter,
                        out var filterError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        filterError ?? "Invalid filter parameter.");
                }

                var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
                var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);
                var featureQuery = CreateRasterFeatureQuery(
                    styleLayers,
                    spatialFilter,
                    serviceSrid,
                    imageSrid,
                    maxFeatures,
                    sqlFilter);

                var features = await QueryRasterFeaturesAsync(featureReader, layer.Id, featureQuery, context.RequestAborted);

                if (features.Length == 0)
                {
                    continue;
                }

                totalFeatureCount += features.Length;
                RenderLayerToCanvas(canvas, features, styleLayers, transform, layer.GeometryType);
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
                context.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            MapServerLog.ExportFailed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateBadRequest(context, InvalidExportRequestMessage);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
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
                    cancellationToken);
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

    private static void RenderLayerToCanvas(
        SKCanvas canvas,
        ImmutableArray<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        Func<double, double, SKPoint> transform,
        GeometryType geometryType)
    {
        if (styleLayers.Length > 0)
        {
            foreach (var styleLayer in styleLayers)
            {
                if (styleLayer.Type == null)
                {
                    continue;
                }

                foreach (var feature in features)
                {
                    if (feature.Geometry == null || feature.Geometry.Length < 5)
                    {
                        continue;
                    }

                    if (styleLayer.Filter is { } filter && !EvaluateFilter(filter, feature.Attributes))
                    {
                        continue;
                    }

                    RenderStyledFeature(canvas, feature, styleLayer, transform);
                }
            }
        }
        else
        {
            var (fill, stroke) = StyleTranslator.CreateDefaultPaints(geometryType);
            try
            {
                if (geometryType == GeometryType.Point)
                {
                    RenderDefaultPoints(canvas, features, transform, fill);
                    return;
                }

                foreach (var feature in features)
                {
                    if (feature.Geometry == null || feature.Geometry.Length < 5)
                    {
                        continue;
                    }

                    var result = WkbToSkiaConverter.Convert(feature.Geometry, transform);
                    RenderConversionResult(canvas, result, fill, stroke);
                }
            }
            finally
            {
                fill.Dispose();
                stroke?.Dispose();
            }
        }
    }

    private static void RenderDefaultPoints(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        Func<double, double, SKPoint> transform,
        SKPaint fill)
    {
        if (features.Count == 0)
        {
            return;
        }

        var rented = ArrayPool<SKPoint>.Shared.Rent(features.Count);
        var count = 0;

        try
        {
            foreach (var feature in features)
            {
                if (WkbToSkiaConverter.TryConvertPoint(feature.Geometry, transform, out var point))
                {
                    rented[count++] = point;
                }
            }

            if (count == 0)
            {
                return;
            }

            if (count == rented.Length)
            {
                canvas.DrawPoints(SKPointMode.Points, rented, fill);
                return;
            }

            var points = new SKPoint[count];
            Array.Copy(rented, points, count);
            canvas.DrawPoints(SKPointMode.Points, points, fill);
        }
        finally
        {
            ArrayPool<SKPoint>.Shared.Return(rented);
        }
    }

    private static void RenderStyledFeature(
        SKCanvas canvas,
        Feature feature,
        MapLibreStyleLayer styleLayer,
        Func<double, double, SKPoint> transform)
    {
        var result = WkbToSkiaConverter.Convert(feature.Geometry!, transform);

        switch (styleLayer.Type)
        {
            case "fill":
                {
                    var fillStyle = StyleTranslator.ResolveFillStyle(styleLayer, feature.Attributes);
                    using var fillPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = fillStyle.FillColor,
                        IsAntialias = fillStyle.Antialias
                    };

                    if (result.Path != null)
                    {
                        canvas.DrawPath(result.Path, fillPaint);
                    }

                    if (fillStyle.OutlineColor.HasValue && result.Path != null)
                    {
                        using var outlinePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = fillStyle.OutlineColor.Value,
                            StrokeWidth = fillStyle.OutlineWidth,
                            IsAntialias = fillStyle.Antialias
                        };
                        canvas.DrawPath(result.Path, outlinePaint);
                    }

                    break;
                }
            case "line":
                {
                    var lineStyle = StyleTranslator.ResolveLineStyle(styleLayer, feature.Attributes);
                    using var linePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = lineStyle.LineColor,
                        StrokeWidth = lineStyle.LineWidth,
                        StrokeCap = lineStyle.LineCap,
                        StrokeJoin = lineStyle.LineJoin,
                        IsAntialias = true
                    };

                    if (lineStyle.DashArray is { Length: > 0 })
                    {
                        using var dashEffect = SKPathEffect.CreateDash(lineStyle.DashArray, 0);
                        linePaint.PathEffect = dashEffect;
                    }

                    if (result.Path != null)
                    {
                        canvas.DrawPath(result.Path, linePaint);
                    }

                    break;
                }
            case "circle":
                {
                    var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, feature.Attributes);
                    using var circlePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = circleStyle.FillColor,
                        IsAntialias = true
                    };

                    if (result.Points != null)
                    {
                        foreach (var point in result.Points)
                        {
                            canvas.DrawCircle(point, circleStyle.Radius, circlePaint);
                        }

                        if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                        {
                            using var strokePaint = new SKPaint
                            {
                                Style = SKPaintStyle.Stroke,
                                Color = circleStyle.StrokeColor.Value,
                                StrokeWidth = circleStyle.StrokeWidth,
                                IsAntialias = true
                            };
                            foreach (var point in result.Points)
                            {
                                canvas.DrawCircle(point, circleStyle.Radius, strokePaint);
                            }
                        }
                    }

                    break;
                }
        }

        result.Path?.Dispose();
    }

    private static void RenderConversionResult(
        SKCanvas canvas,
        WkbToSkiaConverter.GeometryConversionResult result,
        SKPaint fill,
        SKPaint? stroke)
    {
        if (result.IsPoint && result.Points != null)
        {
            canvas.DrawPoints(SKPointMode.Points, result.Points, fill);
        }
        else if (result.Path != null)
        {
            canvas.DrawPath(result.Path, fill);
            if (stroke != null)
            {
                canvas.DrawPath(result.Path, stroke);
            }
        }

        result.Path?.Dispose();
    }

    private static FeatureQuery CreateRasterFeatureQuery(
        MapLibreStyleLayer[] styleLayers,
        SpatialFilter spatialFilter,
        int spatialReferenceSrid,
        int? outputSrid,
        int limit,
        SqlFragment? sqlFilter = null)
    {
        var referencedFields = StyleTranslator.CollectReferencedFields(styleLayers);

        return new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = spatialReferenceSrid,
            OutputSrid = outputSrid,
            Limit = limit,
            SqlFilter = sqlFilter,
            OutFields = referencedFields.Length > 0 ? ImmutableArray.CreateRange(referencedFields) : null,
            ExcludeAttributes = referencedFields.Length == 0
        };
    }

    private static async Task<ImmutableArray<Feature>> QueryRasterFeaturesAsync(
        IFeatureReader featureReader,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (featureReader is IPagedFeatureReader pagedFeatureReader &&
            query.Limit.HasValue &&
            query.Limit.Value != int.MaxValue)
        {
            var pagedResult = await pagedFeatureReader.QueryPageAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return pagedResult.Items;
        }

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    private static bool TryParseBbox(string? bbox, out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        if (bbox is null || !RasterParsingHelpers.TryParseBoundingBox(bbox, out var minX, out var minY, out var maxX, out var maxY))
        {
            return false;
        }

        if (minX >= maxX || minY >= maxY)
            return false;

        extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        return true;
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
            case "gif":
                normalized = candidate;
                return true;
            default:
                error = $"Output format '{format}' is not supported.";
                return false;
        }
    }

    private static int? TryParseSrid(string? sr)
        => SpatialReferenceHelpers.TryParseSrid(sr);

    private static async Task<ExtentTransformResult> TryTransformExtentAsync(
        HttpContext context,
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        var transformService = context.RequestServices.GetService<ICoordinateTransformService>();
        if (transformService == null)
        {
            // No transform service available — try in-memory only
            try
            {
                return ExtentTransformResult.Success(
                    CoordinateTransformer.TransformExtent(extent, fromSrid, toSrid));
            }
            catch (NotSupportedException)
            {
                return ExtentTransformResult.Failure(InvalidSpatialReferenceMessage);
            }
        }

        var result = await transformService.TransformExtentAsync(
            extent.MinX, extent.MinY, extent.MaxX, extent.MaxY,
            fromSrid, toSrid, cancellationToken);

        return result.HasValue
            ? ExtentTransformResult.Success(new SkiaMapRenderer.RenderExtent(
                result.Value.MinX, result.Value.MinY, result.Value.MaxX, result.Value.MaxY))
            : ExtentTransformResult.Failure(InvalidSpatialReferenceMessage);
    }

    private readonly record struct ExtentTransformResult(
        bool IsSuccess,
        SkiaMapRenderer.RenderExtent Extent,
        string? Error)
    {
        public static ExtentTransformResult Success(SkiaMapRenderer.RenderExtent extent)
            => new(true, extent, null);

        public static ExtentTransformResult Failure(string error)
            => new(false, default, error);
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

    private sealed record RenderLayer(
        LayerDefinition Layer,
        int Id,
        string? DefinitionExpression);

    private static bool TryParseLayerDefs(
        string? layerDefsValue,
        ICommonQueryValidator queryValidator,
        out Dictionary<int, string?> layerDefs,
        out string? error)
    {
        layerDefs = new Dictionary<int, string?>();
        error = null;

        if (string.IsNullOrWhiteSpace(layerDefsValue))
        {
            return true;
        }

        var trimmed = layerDefsValue.Trim();
        if (trimmed.StartsWith('{'))
        {
            return TryParseLayerDefsJson(trimmed, queryValidator, layerDefs, out error);
        }

        return TryParseLayerDefsPairs(trimmed, queryValidator, layerDefs, out error);
    }

    private static bool TryParseLayerDefsJson(
        string layerDefsValue,
        ICommonQueryValidator queryValidator,
        Dictionary<int, string?> layerDefs,
        out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(layerDefsValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "layerDefs must be a JSON object.";
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
                {
                    error = $"Invalid layer id '{property.Name}' in layerDefs.";
                    return false;
                }

                string? where;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    where = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    where = null;
                }
                else
                {
                    error = $"Invalid layerDefs value for layer '{property.Name}'. Expected a string.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(where))
                {
                    var validation = queryValidator.ValidateWhereClause(where);
                    if (!validation.IsValid)
                    {
                        error = validation.ErrorMessage ?? "Invalid layerDefs where clause.";
                        return false;
                    }
                }

                layerDefs[layerId] = string.IsNullOrWhiteSpace(where) ? null : where;
            }

            return true;
        }
        catch (JsonException)
        {
            error = InvalidLayerDefsJsonMessage;
            return false;
        }
    }

    private static bool TryParseLayerDefsPairs(
        string layerDefsValue,
        ICommonQueryValidator queryValidator,
        Dictionary<int, string?> layerDefs,
        out string? error)
    {
        error = null;

        var pairs = layerDefsValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pair in pairs)
        {
            var separatorIndex = pair.IndexOf(':');
            if (separatorIndex <= 0)
            {
                error = "layerDefs must use the format: layerId:where;layerId:where";
                return false;
            }

            var idPart = pair[..separatorIndex].Trim();
            var where = separatorIndex == pair.Length - 1
                ? string.Empty
                : pair[(separatorIndex + 1)..].Trim();

            if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                error = $"Invalid layer id '{idPart}' in layerDefs.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(where))
            {
                var validation = queryValidator.ValidateWhereClause(where);
                if (!validation.IsValid)
                {
                    error = validation.ErrorMessage ?? "Invalid layerDefs where clause.";
                    return false;
                }
            }

            layerDefs[layerId] = string.IsNullOrWhiteSpace(where) ? null : where;
        }

        return true;
    }

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
        ServiceDefinition service,
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

            var knownLayers = service.Layers.ToDictionary(layer => layer.Id);
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

                if (!knownLayers.ContainsKey(mapLayerId))
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

    private static RenderLayer[] ResolveRenderLayers(
        ServiceDefinition service,
        string? layersParam,
        IReadOnlyList<DynamicLayerDefinition> dynamicLayers,
        HttpContext context)
    {
        if (dynamicLayers.Count == 0)
        {
            return ResolveVisibleLayers(service, layersParam, context)
                .Select(layer => new RenderLayer(layer, layer.Id, null))
                .ToArray();
        }

        var layerLookup = service.Layers.ToDictionary(layer => layer.Id);
        IEnumerable<DynamicLayerDefinition> selected = dynamicLayers;

        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var spec = layersParam.Trim();
            if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["show:".Length..]);
                selected = dynamicLayers.Where(layer => ids.Contains(layer.Id));
            }
            else if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["hide:".Length..]);
                selected = dynamicLayers.Where(layer => !ids.Contains(layer.Id));
            }
            else if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["include:".Length..]);
                selected = dynamicLayers.Where(layer =>
                {
                    if (!layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer))
                    {
                        return false;
                    }

                    return mapLayer.DefaultVisibility || ids.Contains(layer.Id);
                });
            }
            else if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
            {
                var ids = ParseLayerIds(spec["exclude:".Length..]);
                selected = dynamicLayers.Where(layer =>
                {
                    if (!layerLookup.TryGetValue(layer.MapLayerId, out var mapLayer))
                    {
                        return false;
                    }

                    return mapLayer.DefaultVisibility && !ids.Contains(layer.Id);
                });
            }
            else
            {
                var ids = ParseLayerIds(spec);
                selected = dynamicLayers.Where(layer => ids.Contains(layer.Id));
            }
        }

        var renderLayers = new List<RenderLayer>();
        foreach (var dynamicLayer in selected)
        {
            if (!layerLookup.TryGetValue(dynamicLayer.MapLayerId, out var layer))
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsLayerAccessible(context, layer, service))
            {
                continue;
            }

            renderLayers.Add(new RenderLayer(layer, dynamicLayer.Id, dynamicLayer.DefinitionExpression));
        }

        return renderLayers.ToArray();
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
        LayerDefinition layer,
        IReadOnlyDictionary<int, LayerTimeOptions> layerTimeOptions,
        out string? effectiveTime,
        out string? effectiveTimeRelation,
        out string? error)
    {
        effectiveTime = globalTime;
        effectiveTimeRelation = globalTimeRelation;
        error = null;

        LayerTimeOptions? options = null;
        if (layerTimeOptions.TryGetValue(layer.Id, out var resolvedOptions))
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

        if (!FeatureServerTemporalQueryBuilder.TryParseTimeParameter(effectiveTime, out var start, out var end))
        {
            error = InvalidTimeParameterMessage;
            return false;
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

    private static bool TryResolveTemporalFieldSelection(
        LayerDefinition layer,
        out bool applyTemporalFilter,
        out string? error)
    {
        applyTemporalFilter = false;
        error = null;

        var timeInfo = layer.Metadata?.TimeInfo;
        FieldDefinition? startField = null;
        FieldDefinition? endField = null;

        if (timeInfo != null)
        {
            if (!string.IsNullOrWhiteSpace(timeInfo.StartTimeField))
            {
                startField = FindTemporalField(layer, timeInfo.StartTimeField);
                if (startField == null)
                {
                    error = $"Temporal field '{timeInfo.StartTimeField}' is not defined on layer '{layer.Name}'.";
                    return false;
                }
            }
            else
            {
                startField = layer.AttributeFields.FirstOrDefault(field => field.Type is FieldType.DateTime or FieldType.Date);
            }

            if (!string.IsNullOrWhiteSpace(timeInfo.EndTimeField))
            {
                endField = FindTemporalField(layer, timeInfo.EndTimeField);
                if (endField == null)
                {
                    error = $"Temporal field '{timeInfo.EndTimeField}' is not defined on layer '{layer.Name}'.";
                    return false;
                }
            }
        }
        else
        {
            startField = layer.AttributeFields.FirstOrDefault(field => field.Type is FieldType.DateTime or FieldType.Date);
        }

        if (startField == null)
        {
            applyTemporalFilter = false;
            return true;
        }

        if (endField != null && endField.Type != startField.Type)
        {
            error = "Start and end time fields must use the same temporal type.";
            return false;
        }

        applyTemporalFilter = true;
        return true;
    }

    private static FieldDefinition? FindTemporalField(LayerDefinition layer, string fieldName)
    {
        return layer.AttributeFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is FieldType.DateTime or FieldType.Date);
    }

    private static bool TryBuildLayerSqlFilter(
        IFilterExpressionService filterExpressionService,
        LayerDefinition layer,
        string? where,
        string? time,
        string? timeRelation,
        out SqlFragment? sqlFilter,
        out string? error)
    {
        sqlFilter = null;
        error = null;

        FilterExpression? filterExpression = null;
        if (!string.IsNullOrWhiteSpace(where))
        {
            var parseResult = filterExpressionService.Parse(FilterLanguage.ArcGisSql, where);
            if (!parseResult.IsSuccess)
            {
                error = parseResult.ErrorMessage ?? "Invalid filter syntax.";
                return false;
            }

            filterExpression = parseResult.Expression;
            if (filterExpression != null && !FilterExpressionHelpers.IsBooleanFilterExpression(filterExpression))
            {
                error = "Invalid where clause.";
                return false;
            }
        }

        FilterExpression? temporalExpression = null;
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TryResolveTemporalFieldSelection(layer, out var applyTemporalFilter, out var temporalError))
            {
                error = temporalError ?? "Invalid temporal field configuration.";
                return false;
            }

            if (applyTemporalFilter)
            {
                try
                {
                    temporalExpression = FeatureServerTemporalQueryBuilder.BuildTemporalExpression(time, timeRelation, layer);
                }
                catch (ArgumentException)
                {
                    error = InvalidTimeParameterMessage;
                    return false;
                }
            }
        }

        if (filterExpression != null && temporalExpression != null)
        {
            filterExpression = new BinaryExpression(filterExpression, BinaryOperator.And, temporalExpression);
        }
        else
        {
            filterExpression ??= temporalExpression;
        }

        if (filterExpression != null)
        {
            var translationResult = filterExpressionService.Translate(filterExpression, layer);
            if (!translationResult.IsSuccess)
            {
                error = translationResult.ErrorMessage ?? "Invalid filter syntax.";
                return false;
            }

            sqlFilter = translationResult.SqlFilter;
        }

        return true;
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

    private static LayerDefinition[] ResolveVisibleLayers(
        ServiceDefinition service,
        string? layersParam,
        HttpContext context)
    {
        var accessibleLayers = service.Layers
            .Where(l => AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        if (string.IsNullOrWhiteSpace(layersParam))
        {
            return accessibleLayers.Where(l => l.DefaultVisibility).ToArray();
        }

        var spec = layersParam.Trim();

        if (spec.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ParseLayerIds(spec["show:".Length..]);
            return accessibleLayers.Where(l => ids.Contains(l.Id)).ToArray();
        }

        if (spec.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ParseLayerIds(spec["hide:".Length..]);
            return accessibleLayers.Where(l => !ids.Contains(l.Id)).ToArray();
        }

        if (spec.StartsWith("include:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ParseLayerIds(spec["include:".Length..]);
            return accessibleLayers
                .Where(l => l.DefaultVisibility || ids.Contains(l.Id))
                .ToArray();
        }

        if (spec.StartsWith("exclude:", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ParseLayerIds(spec["exclude:".Length..]);
            return accessibleLayers
                .Where(l => l.DefaultVisibility && !ids.Contains(l.Id))
                .ToArray();
        }

        var showIds = ParseLayerIds(spec);
        return accessibleLayers.Where(l => showIds.Contains(l.Id)).ToArray();
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

    private static bool IsLayerVisibleAtScale(LayerDefinition layer, double scaleDenominator)
    {
        if (scaleDenominator <= 0)
        {
            return true;
        }

        if (layer.MinScale.HasValue && layer.MinScale.Value > 0 && scaleDenominator > layer.MinScale.Value)
        {
            return false;
        }

        if (layer.MaxScale.HasValue && layer.MaxScale.Value > 0 && scaleDenominator < layer.MaxScale.Value)
        {
            return false;
        }

        return true;
    }

    private static SpatialFilter CreateBboxSpatialFilter(SkiaMapRenderer.RenderExtent extent, int srid)
        => SpatialFilterHelpers.CreateBboxSpatialFilter(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, srid);

    private static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
        => SpatialFilterHelpers.CreateEnvelopeWkb(minX, minY, maxX, maxY);

    private static bool EvaluateFilter(MapLibreExpression filter, System.Collections.Immutable.ImmutableDictionary<string, object?> properties)
    {
        var result = ExpressionEvaluator.Evaluate(filter, properties);
        return result is bool b && b;
    }
}
