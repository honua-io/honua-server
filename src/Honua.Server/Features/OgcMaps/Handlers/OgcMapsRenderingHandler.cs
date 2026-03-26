// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.OgcMaps;
using Honua.Server.Features.OgcMaps.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.OgcMaps.Handlers;

/// <summary>
/// Handler for OGC API - Maps rendering operations.
/// Provides server-side map rendering from vector and raster collections.
/// </summary>
internal sealed class OgcMapsRenderingHandler
{
    private const int DefaultImageDimension = 256;
    private const int MaxImageDimension = 4096;
    private const int DefaultBboxCrsSrid = 4326;
    private static readonly Regex _backgroundColorPattern = new("^0x[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Supported image media types mapped to OGC format short names.
    /// </summary>
    private static readonly Dictionary<string, RasterFormat> _acceptMediaTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = RasterFormat.PNG,
        ["image/jpeg"] = RasterFormat.JPEG,
        ["image/tiff"] = RasterFormat.TIFF
    };

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterMapRenderer _mapRenderer;
    private readonly ILogger<OgcMapsRenderingHandler> _logger;

    public OgcMapsRenderingHandler(
        ILayerCatalog layerCatalog,
        IRasterMapRenderer mapRenderer,
        ILogger<OgcMapsRenderingHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _mapRenderer = mapRenderer ?? throw new ArgumentNullException(nameof(mapRenderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Renders a map from a single collection.
    /// </summary>
    public async Task<IResult> RenderCollectionMapAsync(
        int layerId,
        OgcMapRequest request,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "render",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "render-collection-map");

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            // Create map render request
            var (renderRequest, validationError) = CreateMapRenderRequest(request, layer, context);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, layerId, validationError!);
                return CreateBadRequestResult(context, validationError!);
            }

            OgcMapsLog.CollectionMapRenderStarted(_logger, layerId, renderRequest.Value.Width, renderRequest.Value.Height);

            // Render the map
            var result = await _mapRenderer.RenderCollectionMapAsync(layerId, renderRequest.Value, cancellationToken);
            if (result.Data.Length == 0)
            {
                OgcMapsLog.NoMapDataFound(_logger, layerId);
                return CreateNotFoundResult(context, $"No map data found for collection {layerId}");
            }

            OgcMapsLog.CollectionMapRenderCompleted(_logger, layerId, result.Data.Length);
            scope.SetSuccess(1);

            return CreateMapFileResult(context, result, renderRequest.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.CollectionMapRenderFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while rendering the collection map.");
        }
    }

    /// <summary>
    /// Renders a map from multiple collections (dataset-wide).
    /// </summary>
    public async Task<IResult> RenderDatasetMapAsync(
        int[] layerIds,
        OgcMapRequest request,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (layerIds.Length > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
        {
            return CreateBadRequestResult(
                context,
                $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be requested at once.");
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "render",
            HonuaTelemetry.Protocols.OgcMaps,
            string.Join(",", layerIds));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "render-dataset-map");

        var resolvedLayerCount = layerIds.Length;
        try
        {
            // Resolve dataset layers from explicit selection or all accessible layers.
            var layers = new List<Core.Features.Catalog.Domain.LayerDefinition>();
            int[] resolvedLayerIds;

            if (layerIds.Length == 0)
            {
                var allLayers = await _layerCatalog.ListLayersAsync(cancellationToken);
                if (allLayers.Length == 0)
                {
                    return CreateNotFoundResult(context, "No collections available for dataset map rendering.");
                }

                if (context is not null)
                {
                    foreach (var layer in allLayers)
                    {
                        if (AccessPolicyHelpers.IsLayerAccessible(context, layer))
                        {
                            layers.Add(layer);
                            if (layers.Count > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
                            {
                                return CreateBadRequestResult(
                                    context,
                                    $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be rendered in a dataset map request. " +
                                    "Specify the collections parameter to narrow the request.");
                            }
                        }
                    }
                }
                else
                {
                    if (allLayers.Length > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
                    {
                        return CreateBadRequestResult(
                            context,
                            $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be rendered in a dataset map request. " +
                            "Specify the collections parameter to narrow the request.");
                    }

                    layers.AddRange(allLayers);
                }

                if (layers.Count == 0)
                {
                    if (context is not null)
                    {
                        var accessError = AccessPolicyHelpers.RequireAnyLayerAccess(context, allLayers);
                        if (accessError != null)
                        {
                            return accessError;
                        }
                    }

                    return CreateNotFoundResult(context, "No collections available for dataset map rendering.");
                }

                if (layers.Count > OgcMapsLimits.MaxCollectionsPerDatasetMapRequest)
                {
                    return CreateBadRequestResult(
                        context,
                        $"A maximum of {OgcMapsLimits.MaxCollectionsPerDatasetMapRequest} collections can be rendered in a dataset map request.");
                }

                resolvedLayerIds = new int[layers.Count];
                for (var i = 0; i < layers.Count; i++)
                {
                    resolvedLayerIds[i] = layers[i].Id;
                }
            }
            else
            {
                foreach (var layerId in layerIds)
                {
                    var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
                    if (layer == null)
                    {
                        OgcMapsLog.CollectionNotFound(_logger, layerId);
                        return CreateNotFoundResult(context, $"Collection {layerId} not found");
                    }

                    if (context is not null)
                    {
                        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
                        if (accessError != null)
                        {
                            return accessError;
                        }
                    }

                    layers.Add(layer);
                }

                resolvedLayerIds = layerIds;
            }

            resolvedLayerCount = resolvedLayerIds.Length;
            scope.WithTag("layer_count", resolvedLayerCount);

            // Use the first layer for extent calculation
            var primaryLayer = layers[0];
            var (renderRequest, validationError) = CreateMapRenderRequest(request, primaryLayer, context);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, 0, validationError!);
                return CreateBadRequestResult(context, validationError!);
            }

            OgcMapsLog.DatasetMapRenderStarted(_logger, resolvedLayerCount, renderRequest.Value.Width, renderRequest.Value.Height);

            // Render the dataset map
            var result = await _mapRenderer.RenderDatasetMapAsync(resolvedLayerIds, renderRequest.Value, cancellationToken);
            if (result.Data.Length == 0)
            {
                OgcMapsLog.NoDatasetMapDataFound(_logger, resolvedLayerCount);
                return CreateNotFoundResult(context, "No map data found for dataset");
            }

            OgcMapsLog.DatasetMapRenderCompleted(_logger, resolvedLayerCount, result.Data.Length);
            scope.SetSuccess(resolvedLayerCount);

            return CreateMapFileResult(context, result, renderRequest.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.DatasetMapRenderFailed(_logger, ex, resolvedLayerCount);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while rendering the dataset map.");
        }
    }

    /// <summary>
    /// Renders a map with a specific style applied.
    /// </summary>
    public async Task<IResult> RenderStyledMapAsync(
        int layerId,
        string styleId,
        OgcMapRequest request,
        HttpContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "render",
            HonuaTelemetry.Protocols.OgcMaps,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "render-styled-map")
             .WithTag("style_id", styleId);

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return CreateNotFoundResult(context, $"Collection {layerId} not found");
            }

            if (context is not null)
            {
                var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer);
                if (accessError != null)
                {
                    return accessError;
                }
            }

            // Create map render request
            var (renderRequest, validationError) = CreateMapRenderRequest(request, layer, context);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, layerId, validationError!);
                return CreateBadRequestResult(context, validationError!);
            }

            OgcMapsLog.StyledMapRenderStarted(_logger, layerId, styleId, renderRequest.Value.Width, renderRequest.Value.Height);

            // Render the styled map
            var result = await _mapRenderer.RenderStyledMapAsync(layerId, styleId, renderRequest.Value, cancellationToken);
            if (result.Data.Length == 0)
            {
                OgcMapsLog.NoMapDataFound(_logger, layerId);
                return CreateNotFoundResult(context, $"No map data found for collection {layerId}");
            }

            OgcMapsLog.StyledMapRenderCompleted(_logger, layerId, styleId, result.Data.Length);
            scope.SetSuccess(1);

            return CreateMapFileResult(context, result, renderRequest.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotSupportedException ex)
        {
            OgcMapsLog.StyledMapRenderFailed(_logger, ex, layerId, styleId);
            scope.RecordException(ex);
            return Results.Problem(
                title: "Styled maps are not currently supported for raster collections.",
                detail: "Styled map rendering is not available for this collection type.",
                statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (Exception ex)
        {
            OgcMapsLog.StyledMapRenderFailed(_logger, ex, layerId, styleId);
            scope.RecordException(ex);
            return CreateErrorResult(context, "An error occurred while rendering the styled map.");
        }
    }

    /// <summary>
    /// Creates a not-found result using StandardErrorHelpers when context is available,
    /// or a plain 404 when it is not.
    /// </summary>
    private static IResult CreateBadRequestResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateBadRequest(context, message)
            : Results.BadRequest(message);

    /// <summary>
    /// Creates a not-found result using StandardErrorHelpers when context is available,
    /// or a plain 404 when it is not.
    /// </summary>
    private static IResult CreateNotFoundResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateNotFound(context, message)
            : Results.NotFound();

    /// <summary>
    /// Creates an internal server error result using StandardErrorHelpers when context is available,
    /// or a plain 500 Problem when it is not.
    /// </summary>
    private static IResult CreateErrorResult(HttpContext? context, string message)
        => context is not null
            ? StandardErrorHelpers.CreateInternalServerError(context, message)
            : Results.Problem(message, statusCode: 500);

    private static IResult CreateMapFileResult(HttpContext? context, RasterResult result, MapRenderRequest renderRequest)
    {
        if (context is not null)
        {
            string? contentBboxHeader = null;
            int? bboxSrid = null;
            if (result.Extent.HasValue)
            {
                contentBboxHeader = FormatContentBboxHeader(result.Extent.Value);
                bboxSrid = result.Extent.Value.Srid ?? result.Srid ?? renderRequest.Crs ?? renderRequest.BoundingBoxCrs;
            }
            else if (renderRequest.BoundingBox.Length == 4)
            {
                contentBboxHeader = FormatContentBboxHeader(renderRequest.BoundingBox);
                bboxSrid = renderRequest.BoundingBoxCrs ?? renderRequest.Crs ?? result.Srid;
            }

            if (contentBboxHeader is not null)
            {
                context.Response.Headers["Content-Bbox"] = contentBboxHeader;
            }

            var contentCrsHeader = FormatContentCrsHeader(bboxSrid);
            if (contentCrsHeader != null)
            {
                context.Response.Headers["Content-Crs"] = contentCrsHeader;
            }

            // Add self/alternate Link headers per RFC 8288 for format discovery
            AppendLinkHeaders(context, result.ContentType);
        }

        return Results.File(result.Data, result.ContentType);
    }

    /// <summary>
    /// Appends RFC 8288 Link headers for self and alternate image format representations.
    /// </summary>
    private static void AppendLinkHeaders(HttpContext context, string currentContentType)
    {
        var requestPath = context.Request.Path + context.Request.QueryString;
        context.Response.Headers.Append("Link", $"<{requestPath}>; rel=\"self\"; type=\"{currentContentType}\"");

        foreach (var (mediaType, _) in _acceptMediaTypeMap)
        {
            if (!string.Equals(mediaType, currentContentType, StringComparison.OrdinalIgnoreCase))
            {
                var altPath = ReplaceFormatInPath(context, mediaType);
                context.Response.Headers.Append("Link", $"<{altPath}>; rel=\"alternate\"; type=\"{mediaType}\"");
            }
        }
    }

    /// <summary>
    /// Constructs a path with the format query parameter replaced for alternate link generation.
    /// </summary>
    private static string ReplaceFormatInPath(HttpContext context, string mediaType)
    {
        var shortName = mediaType switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpeg",
            "image/tiff" => "tiff",
            _ => "png"
        };

        var query = context.Request.Query;
        var parts = new List<string>();
        foreach (var kvp in query)
        {
            if (string.Equals(kvp.Key, "f", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value.ToString())}");
        }

        parts.Add($"f={shortName}");
        return $"{context.Request.Path}?{string.Join("&", parts)}";
    }

    private static string? FormatContentCrsHeader(int? srid)
    {
        if (srid is null or <= 0)
        {
            return null;
        }

        return FormattableString.Invariant($"<https://www.opengis.net/def/crs/EPSG/0/{srid.Value}>");
    }

    private static string FormatContentBboxHeader(RasterExtent extent)
        => FormattableString.Invariant($"{extent.XMin},{extent.YMin},{extent.XMax},{extent.YMax}");

    private static string FormatContentBboxHeader(double[] bbox)
        => FormattableString.Invariant($"{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}");

    /// <summary>
    /// Resolves the output format from the Accept header and/or f query parameter.
    /// The f parameter takes precedence when present; otherwise the Accept header is used.
    /// Falls back to PNG when neither is specified.
    /// </summary>
    internal static RasterFormat? ResolveOutputFormat(string? fParam, HttpContext? context)
    {
        // f query parameter takes precedence when explicitly provided
        if (!string.IsNullOrWhiteSpace(fParam))
        {
            return fParam.Trim().ToLowerInvariant() switch
            {
                "png" => RasterFormat.PNG,
                "jpeg" or "jpg" => RasterFormat.JPEG,
                "tiff" or "tif" => RasterFormat.TIFF,
                _ => null // unsupported format
            };
        }

        // Fall back to HTTP Accept header content negotiation
        if (context is not null)
        {
            var acceptHeader = context.Request.Headers.Accept;
            if (!StringValues.IsNullOrEmpty(acceptHeader))
            {
                var parsed = acceptHeader
                    .SelectMany(h => h!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(ParseMediaTypeWithQuality)
                    .Where(m => m.HasValue)
                    .OrderByDescending(m => m!.Value.Quality)
                    .Select(m => m!.Value.MediaType)
                    .ToList();

                foreach (var mediaType in parsed)
                {
                    if (_acceptMediaTypeMap.TryGetValue(mediaType, out var format))
                    {
                        return format;
                    }

                    // Accept: image/* or */*
                    if (mediaType is "image/*" or "*/*")
                    {
                        return RasterFormat.PNG;
                    }
                }

                // Accept header present but no supported type matched
                return null;
            }
        }

        // Default to PNG when no format preference is expressed
        return RasterFormat.PNG;
    }

    private static (string MediaType, double Quality)? ParseMediaTypeWithQuality(string segment)
    {
        var parts = segment.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var mediaType = parts[0].Trim();
        var quality = 1.0;

        for (var i = 1; i < parts.Length; i++)
        {
            var param = parts[i].Trim();
            if (param.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(param[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q))
            {
                quality = q;
            }
        }

        return (mediaType, quality);
    }

    private (MapRenderRequest?, string?) CreateMapRenderRequest(
        OgcMapRequest request,
        Core.Features.Catalog.Domain.LayerDefinition layer,
        HttpContext? context = null)
    {
        try
        {
            // Parse CRS - log when requested CRS is not recognized
            var outputCrs = SpatialReferenceHelpers.TryParseSrid(request.Crs);
            if (!string.IsNullOrEmpty(request.Crs) && outputCrs == null)
            {
                OgcMapsLog.UnsupportedCrs(_logger, request.Crs);
                return (null, $"Unsupported CRS: '{request.Crs}'. Use EPSG codes or OGC URI format.");
            }

            var hasRequestedBboxCrs = SpatialReferenceHelpers.TryParseCrsDefinition(request.BboxCrs, out var requestedBboxCrsDefinition);
            if (!string.IsNullOrEmpty(request.BboxCrs) && !hasRequestedBboxCrs)
            {
                OgcMapsLog.UnsupportedCrs(_logger, request.BboxCrs);
                return (null, $"Unsupported bbox-crs: '{request.BboxCrs}'. Use EPSG codes or OGC URI format.");
            }

            // Parse bounding box
            double[] bbox;
            int? bboxCrs;
            if (!string.IsNullOrEmpty(request.Bbox))
            {
                var bboxAxisOrder = hasRequestedBboxCrs
                    ? requestedBboxCrsDefinition.AxisOrder
                    : AxisOrder.EastNorth;
                var bboxIsGeographic = hasRequestedBboxCrs
                    ? requestedBboxCrsDefinition.IsGeographic
                    : true;

                if (!RasterParsingHelpers.TryParseBoundingBox(
                        request.Bbox,
                        bboxAxisOrder,
                        bboxIsGeographic,
                        out var minX,
                        out var minY,
                        out var maxX,
                        out var maxY))
                {
                    return (null, "Invalid bbox format or coordinate values. Expected minX,minY,maxX,maxY values consistent with bbox-crs.");
                }

                bbox = [minX, minY, maxX, maxY];
                bboxCrs = hasRequestedBboxCrs
                    ? requestedBboxCrsDefinition.Srid
                    : DefaultBboxCrsSrid;
            }
            else
            {
                // Use layer extent as default - validate extent exists
                if (layer.Extent == null)
                {
                    return (null, "No bbox provided and the collection has no default extent.");
                }
                var extent = layer.Extent.Value;
                bbox = [extent.MinX, extent.MinY, extent.MaxX, extent.MaxY];
                bboxCrs = extent.SpatialReference;
                OgcMapsLog.UsingDefaultBounds(_logger, layer.Id, extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
            }

            // Resolve format from f parameter and/or Accept header
            var format = ResolveOutputFormat(request.F, context);
            if (format == null)
            {
                return (null, $"Unsupported format: '{request.F ?? "(from Accept header)"}'. Supported formats: png, jpeg, tiff.");
            }

            // Parse and validate dimensions (prevent DoS via oversized images)
            var width = request.Width ?? DefaultImageDimension;
            var height = request.Height ?? DefaultImageDimension;
            if (width < 1 || width > MaxImageDimension || height < 1 || height > MaxImageDimension)
            {
                OgcMapsLog.MapDimensionsExceeded(_logger, width, height, MaxImageDimension, MaxImageDimension);
                return (null, $"Map dimensions must be between 1 and {MaxImageDimension} pixels. Received: {width}x{height}.");
            }

            if (request.Quality is < 1 or > 100)
            {
                return (null, "Quality must be between 1 and 100.");
            }

            if (!string.IsNullOrEmpty(request.BackgroundColor) && !_backgroundColorPattern.IsMatch(request.BackgroundColor))
            {
                return (null, $"Invalid background color: '{request.BackgroundColor}'. Expected 0xRRGGBB hex format.");
            }

            // Parse datetime using invariant culture to avoid ambiguous date formats
            DateTimeOffset? datetime = null;
            if (!string.IsNullOrEmpty(request.Datetime))
            {
                if (!DateTimeOffset.TryParse(request.Datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
                {
                    return (null, $"Invalid datetime format: '{request.Datetime}'. Use ISO 8601 format.");
                }

                datetime = parsedDateTime;
            }

            return (new MapRenderRequest
            {
                BoundingBox = bbox,
                BoundingBoxCrs = bboxCrs,
                Crs = outputCrs,
                Width = width,
                Height = height,
                Format = format.Value,
                Transparent = request.Transparent ?? true,
                BackgroundColor = request.BackgroundColor,
                DateTime = datetime,
                Quality = request.Quality
            }, null);
        }
        catch (FormatException)
        {
            return (null, "Invalid numeric format in request parameters.");
        }
        catch (OverflowException)
        {
            return (null, "Numeric parameter value is out of range.");
        }
        catch (ArgumentException)
        {
            return (null, "Invalid map request parameters.");
        }
    }
}
