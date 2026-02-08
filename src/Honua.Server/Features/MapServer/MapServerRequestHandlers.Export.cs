// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.MapServer.Models;
using Honua.Server.Features.MapServer.Rendering;
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

        // Parse export parameters from query string
        var query = context.Request.Query;
        var parameters = new ExportParameters
        {
            Bbox = query["bbox"].FirstOrDefault(),
            Size = query["size"].FirstOrDefault(),
            Dpi = int.TryParse(query["dpi"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi) ? dpi : DefaultDpi,
            Format = query["format"].FirstOrDefault() ?? "png",
            Transparent = !string.Equals(query["transparent"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase),
            Layers = query["layers"].FirstOrDefault(),
            BboxSr = query["bboxSR"].FirstOrDefault(),
            ImageSr = query["imageSR"].FirstOrDefault(),
            F = query["f"].FirstOrDefault() ?? "image",
            BackgroundColor = query["backgroundColor"].FirstOrDefault()
        };

        // Parse bounding box
        if (!TryParseBbox(parameters.Bbox, out var extent))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid or missing bbox parameter. Expected format: xmin,ymin,xmax,ymax");
        }

        // Parse image size
        if (!TryParseSize(parameters.Size, out var imageWidth, out var imageHeight))
        {
            imageWidth = DefaultImageWidth;
            imageHeight = DefaultImageHeight;
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
        activity?.SetTag("honua.mapserver.format", parameters.Format);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await resourceValidator.ValidateServiceAsync(serviceId, context.RequestAborted);
        if (!serviceResult.IsValid)
        {
            var errorMessage = serviceResult.ErrorMessage ?? "Service not found.";
            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = serviceResult.Resource!;
        var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
        if (protocolError is not null)
        {
            return protocolError;
        }

        // Apply per-service MapServer config if present
        var mapConfig = service.Metadata?.MapServer;
        var maxDimension = mapConfig?.MaxImageWidth ?? MaxImageDimension;
        var maxDimensionH = mapConfig?.MaxImageHeight ?? MaxImageDimension;
        var maxFeatures = mapConfig?.MaxFeaturesPerLayer ?? MaxFeaturesPerLayer;
        imageWidth = Math.Clamp(imageWidth, 1, maxDimension);
        imageHeight = Math.Clamp(imageHeight, 1, maxDimensionH);

        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
        if (accessError != null)
        {
            return accessError;
        }

        // Determine visible layers
        var visibleLayers = ResolveVisibleLayers(service, parameters.Layers, context);
        if (visibleLayers.Length == 0)
        {
            // Return empty image
            using var renderer = new SkiaMapRenderer();
            var emptyImage = renderer.RenderMap(
                [],
                [],
                extent,
                imageWidth,
                imageHeight,
                parameters.Transparent,
                ParseBackgroundColor(parameters.BackgroundColor),
                GeometryType.None);

            return ReturnImageResult(emptyImage, parameters, imageWidth, imageHeight, extent, context);
        }

        // Resolve SRID context
        var serviceSrid = service.SpatialReference.Srid;
        var bboxSrid = TryParseSrid(parameters.BboxSr) ?? serviceSrid;
        var imageSrid = TryParseSrid(parameters.ImageSr) ?? serviceSrid;

        // Transform bbox to service SRID for querying if needed
        var queryExtent = CoordinateTransformer.TransformExtent(extent, bboxSrid, serviceSrid);

        // Calculate scale for visibility filtering
        var scaleDenominator = CoordinateTransformer.CalculateScaleDenominator(extent, imageWidth, parameters.Dpi, bboxSrid);

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

        var totalFeatureCount = 0;
        using var renderer2 = new SkiaMapRenderer();

        // Build the render extent in the output coordinate space
        var renderExtent = CoordinateTransformer.TransformExtent(extent, bboxSrid, imageSrid);

        // Create combined surface for all layers
        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        if (parameters.Transparent)
        {
            canvas.Clear(SKColors.Transparent);
        }
        else
        {
            canvas.Clear(ParseBackgroundColor(parameters.BackgroundColor) ?? SKColors.White);
        }

        var transform = SkiaMapRenderer.BuildTransform(renderExtent, imageWidth, imageHeight);

        foreach (var layer in visibleLayers)
        {
            // Check scale visibility
            if (!IsLayerVisibleAtScale(layer, scaleDenominator))
            {
                continue;
            }

            if (!layer.HasGeometry)
            {
                continue;
            }

            // Build spatial filter for bbox
            var spatialFilter = CreateBboxSpatialFilter(queryExtent, serviceSrid);

            var featureQuery = new FeatureQuery
            {
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = serviceSrid,
                OutputSrid = imageSrid,
                Limit = maxFeatures
            };

            var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);

            if (queryResult.Items.Length == 0)
            {
                continue;
            }

            totalFeatureCount += queryResult.Items.Length;

            // Load style for layer
            var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
            var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

            // Render features directly to canvas
            RenderLayerToCanvas(canvas, queryResult.Items, styleLayers, transform, layer.GeometryType);
        }

        stopwatch.Stop();
        MapServerLog.ExportCompleted(logger, serviceId, totalFeatureCount, stopwatch.Elapsed.TotalMilliseconds);
        HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
        HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);
        activity?.SetTag("honua.mapserver.layer_count", visibleLayers.Length);

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, parameters.Format);
        return ReturnImageResult(imageBytes, parameters, imageWidth, imageHeight, extent, context);
    }

    private static IResult ReturnImageResult(
        byte[] imageBytes,
        ExportParameters parameters,
        int imageWidth,
        int imageHeight,
        SkiaMapRenderer.RenderExtent extent,
        HttpContext context)
    {
        if (string.Equals(parameters.F, "json", StringComparison.OrdinalIgnoreCase))
        {
            var response = new ExportImageResponse
            {
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
                        Wkid = TryParseSrid(parameters.BboxSr) ?? 4326
                    }
                },
                ImageData = Convert.ToBase64String(imageBytes),
                ContentType = SkiaMapRenderer.GetContentType(parameters.Format)
            };

            return Results.Json(response, MapServerJsonContext.Default.ExportImageResponse, contentType: "application/json");
        }

        // Return raw image
        var contentType = SkiaMapRenderer.GetContentType(parameters.Format);
        return Results.Bytes(imageBytes, contentType);
    }

    private static void RenderLayerToCanvas(
        SKCanvas canvas,
        System.Collections.Immutable.ImmutableArray<Feature> features,
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

                    RenderStyledFeature(canvas, feature, styleLayer, transform);
                }
            }
        }
        else
        {
            var (fill, stroke) = StyleTranslator.CreateDefaultPaints(geometryType);
            try
            {
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
            foreach (var point in result.Points)
            {
                canvas.DrawCircle(point, 4f, fill);
            }
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

    private static bool TryParseBbox(string? bbox, out SkiaMapRenderer.RenderExtent extent)
    {
        extent = default;
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return false;
        }

        var parts = bbox.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            return false;
        }

        extent = new SkiaMapRenderer.RenderExtent(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryParseSize(string? size, out int width, out int height)
    {
        width = DefaultImageWidth;
        height = DefaultImageHeight;

        if (string.IsNullOrWhiteSpace(size))
        {
            return false;
        }

        var parts = size.Split(',');
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
    }

    private static int? TryParseSrid(string? sr)
    {
        if (string.IsNullOrWhiteSpace(sr))
        {
            return null;
        }

        if (int.TryParse(sr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
        {
            return srid;
        }

        return null;
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

        // Parse "show:0,1,2" or "hide:3" or just "0,1,2"
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

        // Default: treat as show list
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
    {
        // Create WKB polygon for the bounding box envelope
        var wkb = CreateEnvelopeWkb(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid);
    }

    /// <summary>
    /// Creates a WKB polygon representing a bounding box envelope.
    /// </summary>
    private static byte[] CreateEnvelopeWkb(double minX, double minY, double maxX, double maxY)
    {
        // WKB Polygon with 1 ring and 5 points (closed ring)
        // Byte order (1) + type (4) + num rings (4) + num points (4) + 5 * (x:8 + y:8) = 93 bytes
        var wkb = new byte[93];
        var offset = 0;

        // Byte order: little-endian
        wkb[offset++] = 1;

        // Geometry type: Polygon (3)
        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 3);
        offset += 4;

        // Number of rings: 1
        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 1);
        offset += 4;

        // Number of points: 5
        BitConverter.TryWriteBytes(wkb.AsSpan(offset), 5);
        offset += 4;

        // Ring: minX,minY -> maxX,minY -> maxX,maxY -> minX,maxY -> minX,minY
        WritePoint(wkb, ref offset, minX, minY);
        WritePoint(wkb, ref offset, maxX, minY);
        WritePoint(wkb, ref offset, maxX, maxY);
        WritePoint(wkb, ref offset, minX, maxY);
        WritePoint(wkb, ref offset, minX, minY);

        return wkb;
    }

    private static void WritePoint(byte[] buffer, ref int offset, double x, double y)
    {
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), x);
        offset += 8;
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), y);
        offset += 8;
    }

    private static SKColor? ParseBackgroundColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        return ExpressionEvaluator.ParseColor(color);
    }
}
