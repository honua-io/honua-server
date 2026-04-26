// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.ServiceDefaults;
using SkiaSharp;

namespace Honua.Server.Features.StaticMap;

/// <summary>
/// Request handlers for static map image endpoints.
/// </summary>
internal static partial class StaticMapEndpoints
{
    private const int DefaultWidth = 600;
    private const int DefaultHeight = 400;
    private const int DefaultDpi = 72;
    private const int MinDimension = 1;
    private const int MaxZoom = 22;
    private const int MaxFeaturesPerLayer = 10_000;
    // Safety cap on render surface allocation. At high DPI the effective pixel count
    // can exceed edition dimension limits, so both checks are needed. For example,
    // 4096x4096 at 150 DPI (~72.8 M pixels) exceeds this cap — users must reduce
    // dimensions or DPI. The edition limits and this cap are intentionally independent.
    private const long MaxRenderPixels = 64_000_000; // ~8192x8192, caps surface at ~244 MB
    private const double EarthCircumference = SpatialConstants.EarthRadius * 2 * Math.PI;

    /// <summary>
    /// Handles center+zoom static map requests.
    /// Route: /static/{serviceId}/{lon},{lat},{zoom}/{width}x{height}.{format}
    /// </summary>
    internal static async Task<IResult> HandleCenterZoom(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        // Parse center: "lon,lat,zoom"
        var centerRaw = context.Request.RouteValues["center"]?.ToString();
        if (!TryParseCenterZoom(centerRaw, out var lon, out var lat, out var zoom))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid center parameter. Expected format: lon,lat,zoom");
        }

        if (!TryParseDimensions(context, out var width, out var height, out var format))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid dimensions. Expected format: {width}x{height}.{png|jpeg|webp}");
        }

        var dpi = ParseDpiQueryParam(context);

        // Convert center+zoom to bbox in EPSG:4326
        var extent = CenterZoomToExtent(lon, lat, zoom, width, height);

        var parameters = new StaticMapParameters
        {
            Lon = lon,
            Lat = lat,
            Zoom = zoom,
            Width = width,
            Height = height,
            Format = NormalizeFormat(format),
            Dpi = dpi,
            Layers = context.Request.Query["layers"].FirstOrDefault(),
            Markers = context.Request.Query["markers"].FirstOrDefault(),
            Path = context.Request.Query["path"].FirstOrDefault(),
            Filter = context.Request.Query["filter"].FirstOrDefault()
        };

        return await RenderStaticMap(context, serviceId, parameters, extent, renderSrid: 3857);
    }

    /// <summary>
    /// Handles bbox-based static map requests.
    /// Route: /static/{serviceId}/bbox/{bbox}/{width}x{height}.{format}
    /// </summary>
    internal static async Task<IResult> HandleBbox(HttpContext context)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var bboxRaw = context.Request.RouteValues["bbox"]?.ToString();
        if (!TryParseBbox(bboxRaw, out var extent))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid bbox parameter. Expected format: xmin,ymin,xmax,ymax");
        }

        if (!TryParseDimensions(context, out var width, out var height, out var format))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid dimensions. Expected format: {width}x{height}.{png|jpeg|webp}");
        }

        var dpi = ParseDpiQueryParam(context);

        var parameters = new StaticMapParameters
        {
            Bbox = bboxRaw,
            Width = width,
            Height = height,
            Format = NormalizeFormat(format),
            Dpi = dpi,
            Layers = context.Request.Query["layers"].FirstOrDefault(),
            Markers = context.Request.Query["markers"].FirstOrDefault(),
            Path = context.Request.Query["path"].FirstOrDefault(),
            Filter = context.Request.Query["filter"].FirstOrDefault()
        };

        return await RenderStaticMap(context, serviceId, parameters, extent);
    }

    /// <summary>
    /// Core rendering pipeline shared by center+zoom and bbox handlers.
    /// </summary>
    private static async Task<IResult> RenderStaticMap(
        HttpContext context,
        string serviceId,
        StaticMapParameters parameters,
        RenderExtent extent,
        int renderSrid = 4326)
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Honua.Server.StaticMapEndpoints");

        try
        {
            // Validate service exists
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

            // Validate MapServer protocol is enabled (static map depends on MapServer rendering)
            var protocolError = ProtocolValidationHelpers.ValidateProtocolEnabled(context, service, ServiceProtocols.MapServer);
            if (protocolError is not null)
            {
                return protocolError;
            }

            // Enforce edition limits
            var licenseProvider = context.RequestServices.GetRequiredService<ILicenseStatusProvider>();
            var edition = licenseProvider.GetCurrentStatus().Edition;

            var maxDimension = edition >= HonuaEdition.Pro
                ? StaticMapEditionLimits.ProMaxDimension
                : StaticMapEditionLimits.CommunityMaxDimension;
            var maxDpi = edition >= HonuaEdition.Pro
                ? StaticMapEditionLimits.ProMaxDpi
                : StaticMapEditionLimits.CommunityMaxDpi;

            if (parameters.Width > maxDimension || parameters.Height > maxDimension)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Image dimensions exceed {edition} edition limit of {maxDimension}x{maxDimension}.");
            }

            if (parameters.Dpi > maxDpi)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"DPI {parameters.Dpi} exceeds {edition} edition limit of {maxDpi}.");
            }

            // Validate format
            if (!IsSupportedFormat(parameters.Format))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Unsupported format '{parameters.Format}'. Supported: png, jpeg, webp.");
            }

            // Parse overlays with edition limits
            var maxMarkers = edition >= HonuaEdition.Pro
                ? StaticMapEditionLimits.ProMaxMarkers
                : StaticMapEditionLimits.CommunityMaxMarkers;
            var maxPathVertices = edition >= HonuaEdition.Pro
                ? StaticMapEditionLimits.ProMaxPathVertices
                : StaticMapEditionLimits.CommunityMaxPathVertices;

            if (!TryParseMarkers(parameters.Markers, maxMarkers, out var markers, out var markerError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, markerError!);
            }

            if (!TryParsePath(parameters.Path, maxPathVertices, out var path, out var pathError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, pathError!);
            }

            StaticMapLog.Requested(logger, serviceId, parameters.Width, parameters.Height, parameters.Format, parameters.Dpi);
            var stopwatch = Stopwatch.StartNew();
            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.StaticMapRender, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.StaticMap);
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "render");
            activity?.SetTag("honua.staticmap.width", parameters.Width);
            activity?.SetTag("honua.staticmap.height", parameters.Height);
            activity?.SetTag("honua.staticmap.format", parameters.Format);
            activity?.SetTag("honua.staticmap.dpi", parameters.Dpi);

            var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, service.Layers, service);
            if (accessError != null)
            {
                return accessError;
            }

            // Scale dimensions by DPI factor for high-res rendering
            var dpiFactor = parameters.Dpi / 72.0;
            var renderWidth = (int)Math.Round(parameters.Width * dpiFactor);
            var renderHeight = (int)Math.Round(parameters.Height * dpiFactor);

            if ((long)renderWidth * renderHeight > MaxRenderPixels)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Effective render size {renderWidth}x{renderHeight} at {parameters.Dpi} DPI exceeds the maximum of {MaxRenderPixels:N0} pixels. Reduce dimensions or DPI.");
            }

            // Resolve layers to render (filtered by per-layer access policy)
            var (renderLayerIds, layerError) = ResolveLayerIds(parameters.Layers, service, context);
            if (layerError is not null)
            {
                return layerError;
            }

            // Query features and render
            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

            var totalFeatureCount = 0;
            var serviceSrid = service.SpatialReference.Srid;
            var spatialFilter = CreateBboxSpatialFilter(extent);

            await using var renderLease = await context.RequestServices
                .GetRequiredService<RasterRenderCapacityLimiter>()
                .TryAcquireAsync(renderWidth, renderHeight, context.RequestAborted)
                .ConfigureAwait(false);
            if (renderLease is null)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    RasterRenderCapacityLimiter.CapacityExceededMessage,
                    RasterRenderCapacityLimiter.RetryAfterSeconds);
            }

            using var surface = SKSurface.Create(new SKImageInfo(renderWidth, renderHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface.");
            }

            var canvas = surface.Canvas;
            canvas.Clear(parameters.Format == "jpeg" ? SKColors.White : SKColors.Transparent);

            // For center/zoom (renderSrid=3857), render in Mercator for correct pixel placement.
            // For bbox (renderSrid=4326), render in geographic as the user-specified extent defines.
            var useMercator = renderSrid == 3857;
            var renderExtent = useMercator
                ? CoordinateTransformer.TransformExtent(extent, 4326, 3857)
                : extent;
            var transform = SkiaMapRenderer.BuildTransform(renderExtent, renderWidth, renderHeight);

            // Overlay markers/paths are always specified in lon/lat — project to Mercator first
            // when rendering in 3857, otherwise use the same transform.
            var overlayTransform = useMercator
                ? (double lon, double lat) =>
                {
                    var (mx, my) = CoordinateTransformer.LonLatToWebMercator(lon, lat);
                    return transform(mx, my);
                }
            : transform;

            foreach (var layerId in renderLayerIds)
            {
                context.RequestAborted.ThrowIfCancellationRequested();

                var layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
                if (layer is null || !layer.HasGeometry)
                {
                    continue;
                }

                var (sqlFilter, filterError) = BuildFilterSql(parameters.Filter, layer, context);
                if (filterError is not null)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, filterError);
                }

                var featureQuery = new FeatureQuery
                {
                    SpatialFilter = spatialFilter,
                    SpatialReferenceSrid = serviceSrid,
                    OutputSrid = renderSrid,
                    Limit = MaxFeaturesPerLayer,
                    SqlFilter = sqlFilter
                };

                var queryResult = await featureReader.QueryAsync(layer.Id, featureQuery, context.RequestAborted);

                if (queryResult.Items.Length == 0)
                {
                    continue;
                }

                totalFeatureCount += queryResult.Items.Length;

                var style = await styleCatalog.GetLayerStyleAsync(layer.Id, context.RequestAborted);
                var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);

                RenderLayerToCanvas(canvas, queryResult.Items, styleLayers, transform, layer.GeometryType);
            }

            // Render overlay markers
            if (markers.Length > 0)
            {
                RenderMarkers(canvas, markers, overlayTransform, (float)dpiFactor);
            }

            // Render overlay path
            if (path is not null)
            {
                RenderPath(canvas, path, overlayTransform, (float)dpiFactor);
            }

            var imageBytes = SkiaMapRenderer.EncodeSurface(surface, parameters.Format);
            var contentType = SkiaMapRenderer.GetContentType(parameters.Format);

            stopwatch.Stop();
            StaticMapLog.Completed(logger, serviceId, totalFeatureCount, stopwatch.Elapsed.TotalMilliseconds);
            HonuaTelemetry.SetSuccess(activity, totalFeatureCount);
            HonuaTelemetry.CategorizeLatency(activity, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Bytes(imageBytes, contentType);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StaticMapLog.Failed(logger, serviceId, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Static map rendering failed.");
        }
    }

    // ----- Parsing helpers -----

    private static bool TryParseCenterZoom(string? value, out double lon, out double lat, out int zoom)
    {
        lon = lat = 0;
        zoom = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out zoom))
        {
            return false;
        }

        if (lon < -180 || lon > 180 || lat < -90 || lat > 90 || zoom < 0 || zoom > MaxZoom)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseBbox(string? value, out RenderExtent extent)
    {
        extent = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmin) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymin) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xmax) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ymax))
        {
            return false;
        }

        if (xmin < -180.0 || xmin > 180.0 ||
            xmax < -180.0 || xmax > 180.0 ||
            ymin < -90.0 || ymin > 90.0 ||
            ymax < -90.0 || ymax > 90.0)
        {
            return false;
        }

        if (ymin >= ymax)
        {
            return false;
        }

        if (xmin >= xmax && !CoordinateTransformer.IsWrappedGeographicExtent(new SkiaMapRenderer.RenderExtent(xmin, ymin, xmax, ymax)))
        {
            return false;
        }

        extent = new RenderExtent(xmin, ymin, xmax, ymax);
        return true;
    }

    private static bool TryParseDimensions(HttpContext context, out int width, out int height, out string format)
    {
        width = DefaultWidth;
        height = DefaultHeight;
        format = "png";

        var dimensionsRaw = context.Request.RouteValues["dimensions"]?.ToString();
        var formatRaw = context.Request.RouteValues["format"]?.ToString();

        if (string.IsNullOrWhiteSpace(dimensionsRaw) || string.IsNullOrWhiteSpace(formatRaw))
        {
            return false;
        }

        format = formatRaw;

        var parts = dimensionsRaw.Split('x');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        {
            return false;
        }

        return width >= MinDimension && height >= MinDimension;
    }

    private static int ParseDpiQueryParam(HttpContext context)
    {
        var dpiRaw = context.Request.Query["dpi"].FirstOrDefault();
        if (int.TryParse(dpiRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi) && IsSupportedDpi(dpi))
        {
            return dpi;
        }

        return DefaultDpi;
    }

    private static bool TryParseMarkers(string? value, int maxMarkers, out MapMarker[] markers, out string? error)
    {
        markers = [];
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var segments = value.Split('|');
        if (segments.Length > maxMarkers)
        {
            error = $"Too many markers ({segments.Length}). Maximum allowed: {maxMarkers}.";
            return false;
        }

        var result = new MapMarker[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var parts = segments[i].Split(',');
            if (parts.Length < 2)
            {
                error = $"Invalid marker at index {i}. Expected format: lon,lat[,color].";
                return false;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mLon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mLat))
            {
                error = $"Invalid marker coordinates at index {i}.";
                return false;
            }

            var color = parts.Length > 2 ? parts[2] : "#FF0000";
            result[i] = new MapMarker(mLon, mLat, color);
        }

        markers = result;
        return true;
    }

    private static bool TryParsePath(string? value, int maxVertices, out MapPath? path, out string? error)
    {
        path = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // Format: color:width|lon1,lat1|lon2,lat2|...
        // or just: lon1,lat1|lon2,lat2|...
        var segments = value.Split('|');
        var startIndex = 0;
        var color = "#FF0000";
        var strokeWidth = 2f;

        // Check if first segment is style spec (contains ':')
        if (segments.Length > 0 && segments[0].Contains(':'))
        {
            var styleParts = segments[0].Split(':');
            if (styleParts.Length >= 1)
            {
                color = styleParts[0];
            }

            if (styleParts.Length >= 2 &&
                float.TryParse(styleParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
            {
                strokeWidth = sw;
            }

            startIndex = 1;
        }

        var vertexCount = segments.Length - startIndex;
        if (vertexCount < 2)
        {
            error = "Path requires at least 2 vertices.";
            return false;
        }

        if (vertexCount > maxVertices)
        {
            error = $"Too many path vertices ({vertexCount}). Maximum allowed: {maxVertices}.";
            return false;
        }

        var vertices = new (double Lon, double Lat)[vertexCount];
        for (var i = startIndex; i < segments.Length; i++)
        {
            var parts = segments[i].Split(',');
            if (parts.Length != 2)
            {
                error = $"Invalid path vertex at index {i - startIndex}. Expected format: lon,lat.";
                return false;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pLon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pLat))
            {
                error = $"Invalid path vertex coordinates at index {i - startIndex}.";
                return false;
            }

            vertices[i - startIndex] = (pLon, pLat);
        }

        path = new MapPath
        {
            Vertices = vertices,
            Color = color,
            StrokeWidth = strokeWidth
        };
        return true;
    }

    // ----- Coordinate helpers -----

    /// <summary>
    /// Converts center point and zoom level to a geographic bounding box.
    /// Projects to Web Mercator, offsets in projected metres, then inverse-projects
    /// the corners back to geographic coordinates. At zoom z each pixel represents
    /// <c>2πR / (256 × 2^z)</c> metres in projected space.
    /// </summary>
    internal static RenderExtent CenterZoomToExtent(
        double lon, double lat, int zoom, int width, int height)
    {
        // Metres per pixel in the projected Web Mercator coordinate space.
        var metersPerPixel = EarthCircumference / (256.0 * Math.Pow(2, zoom));

        // Project the center to Web Mercator (internally clamps lat to ±85.0511°).
        var (cx, cy) = CoordinateTransformer.LonLatToWebMercator(lon, lat);

        // Offset in projected metres.
        var halfW = width / 2.0 * metersPerPixel;
        var halfH = height / 2.0 * metersPerPixel;

        // Clamp to the Web Mercator world bounds before inverse-projecting.
        var worldHalf = SpatialConstants.EarthRadius * Math.PI; // ≈ 20 037 508.34 m
        var minX = Math.Clamp(cx - halfW, -worldHalf, worldHalf);
        var maxX = Math.Clamp(cx + halfW, -worldHalf, worldHalf);
        var minY = Math.Clamp(cy - halfH, -worldHalf, worldHalf);
        var maxY = Math.Clamp(cy + halfH, -worldHalf, worldHalf);

        // Inverse-project corners back to geographic coordinates.
        var (minLon, minLat) = CoordinateTransformer.WebMercatorToLonLat(minX, minY);
        var (maxLon, maxLat) = CoordinateTransformer.WebMercatorToLonLat(maxX, maxY);

        // Guard against floating-point overshoot at the world boundary.
        return new RenderExtent(
            Math.Clamp(minLon, -180, 180),
            Math.Clamp(minLat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude),
            Math.Clamp(maxLon, -180, 180),
            Math.Clamp(maxLat, -SpatialConstants.WebMercatorMaxLatitude, SpatialConstants.WebMercatorMaxLatitude));
    }

    // ----- Rendering helpers -----

    private static (int[] LayerIds, IResult? Error) ResolveLayerIds(string? layerParam, ServiceDefinition service, HttpContext context)
    {
        var accessibleLayers = service.Layers
            .Where(l => l.HasGeometry && AccessPolicyHelpers.IsLayerAccessible(context, l, service))
            .ToArray();

        if (string.IsNullOrWhiteSpace(layerParam))
        {
            // Default: accessible layers with default visibility
            return (accessibleLayers
                .Where(l => l.DefaultVisibility)
                .Select(l => l.Id)
                .ToArray(), null);
        }

        var requestedIds = new HashSet<int>();
        var tokens = layerParam.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            return (Array.Empty<int>(), StandardErrorHelpers.CreateBadRequest(context, "Invalid layers parameter."));
        }
        foreach (var segment in tokens)
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                requestedIds.Add(id);
                continue;
            }

            return (Array.Empty<int>(), StandardErrorHelpers.CreateBadRequest(context, $"Invalid layers parameter '{layerParam}'."));
        }

        var accessibleLayerIds = accessibleLayers.Select(layer => layer.Id).ToHashSet();
        if (requestedIds.Any(id => !accessibleLayerIds.Contains(id)))
        {
            return (Array.Empty<int>(), StandardErrorHelpers.CreateBadRequest(
                context,
                "layers parameter references an invalid or inaccessible layer."));
        }

        return (accessibleLayers
            .Where(l => requestedIds.Contains(l.Id))
            .Select(l => l.Id)
            .ToArray(), null);
    }

    private static SpatialFilter CreateBboxSpatialFilter(RenderExtent extent)
    {
        return SpatialFilterHelpers.CreateBboxSpatialFilter(
            extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, 4326);
    }

    private static (SqlFragment? Filter, string? Error) BuildFilterSql(
        string? filterExpression, LayerDefinition layer, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return (null, null);
        }

        var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
        var result = filterService.Translate(FilterLanguage.Cql2Text, filterExpression, layer);
        if (!result.IsSuccess)
        {
            return (null, result.ErrorMessage ?? "Invalid filter expression.");
        }

        return (result.SqlFilter, null);
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
            SkiaMapRenderer.RenderWithStyles(canvas, features, styleLayers, transform);
        }
        else
        {
            SkiaMapRenderer.RenderWithDefaults(canvas, features, transform, geometryType);
        }
    }

    private static void RenderMarkers(
        SKCanvas canvas,
        MapMarker[] markers,
        Func<double, double, SKPoint> transform,
        float dpiFactor)
    {
        var radius = 6f * dpiFactor;
        var strokeWidth = 2f * dpiFactor;

        foreach (var marker in markers)
        {
            var point = transform(marker.Lon, marker.Lat);

            var fillColor = TryParseSkColor(marker.Color) ?? SKColors.Red;

            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = fillColor,
                IsAntialias = true
            };
            canvas.DrawCircle(point, radius, fillPaint);

            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColors.White,
                StrokeWidth = strokeWidth,
                IsAntialias = true
            };
            canvas.DrawCircle(point, radius, strokePaint);
        }
    }

    private static void RenderPath(
        SKCanvas canvas,
        MapPath path,
        Func<double, double, SKPoint> transform,
        float dpiFactor)
    {
        if (path.Vertices.Length < 2)
        {
            return;
        }

        var strokeColor = TryParseSkColor(path.Color) ?? SKColors.Red;

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = strokeColor,
            StrokeWidth = path.StrokeWidth * dpiFactor,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        using var skPath = new SKPath();
        var first = transform(path.Vertices[0].Lon, path.Vertices[0].Lat);
        skPath.MoveTo(first);

        for (var i = 1; i < path.Vertices.Length; i++)
        {
            var pt = transform(path.Vertices[i].Lon, path.Vertices[i].Lat);
            skPath.LineTo(pt);
        }

        canvas.DrawPath(skPath, paint);
    }

    // ----- Utility helpers -----

    private static string NormalizeFormat(string format)
    {
        var lower = format.ToLowerInvariant();
        return lower switch
        {
            "jpg" => "jpeg",
            _ => lower
        };
    }

    private static bool IsSupportedFormat(string format)
        => format is "png" or "jpeg" or "webp";

    private static bool IsSupportedDpi(int dpi)
        => dpi is 72 or 150 or 300;

    private static SKColor? TryParseSkColor(string colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr))
        {
            return null;
        }

        if (SKColor.TryParse(colorStr, out var color))
        {
            return color;
        }

        // Try with # prefix
        if (!colorStr.StartsWith('#') && SKColor.TryParse("#" + colorStr, out color))
        {
            return color;
        }

        return null;
    }
}
