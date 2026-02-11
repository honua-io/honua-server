// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.OgcMaps.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.OgcMaps.Handlers;

/// <summary>
/// Handler for OGC API - Maps rendering operations.
/// Provides server-side map rendering from vector and raster collections.
/// </summary>
internal sealed class OgcMapsRenderingHandler
{
    private const int DefaultImageDimension = 256;
    private const int MaxImageDimension = 4096;
    private static readonly Regex _backgroundColorPattern = new("^0x[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            var renderRequest = CreateMapRenderRequest(request, layer);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, layerId, "Failed to parse map request parameters");
                return Results.BadRequest("Invalid map request parameters");
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

            return Results.File(result.Data, result.ContentType);
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
        using var scope = HonuaTelemetryScope.StartFeature(
            "render",
            HonuaTelemetry.Protocols.OgcMaps,
            string.Join(",", layerIds));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "render-dataset-map")
             .WithTag("layer_count", layerIds.Length);

        try
        {
            // Validate all layers exist
            var layers = new List<Core.Features.Catalog.Domain.LayerDefinition>();
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

            // Use the first layer for extent calculation
            var primaryLayer = layers[0];
            var renderRequest = CreateMapRenderRequest(request, primaryLayer);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, 0, "Failed to parse dataset map request parameters");
                return Results.BadRequest("Invalid map request parameters");
            }

            OgcMapsLog.DatasetMapRenderStarted(_logger, layerIds.Length, renderRequest.Value.Width, renderRequest.Value.Height);

            // Render the dataset map
            var result = await _mapRenderer.RenderDatasetMapAsync(layerIds, renderRequest.Value, cancellationToken);
            if (result.Data.Length == 0)
            {
                OgcMapsLog.NoDatasetMapDataFound(_logger, layerIds.Length);
                return CreateNotFoundResult(context, "No map data found for dataset");
            }

            OgcMapsLog.DatasetMapRenderCompleted(_logger, layerIds.Length, result.Data.Length);
            scope.SetSuccess(layerIds.Length);

            return Results.File(result.Data, result.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.DatasetMapRenderFailed(_logger, ex, layerIds.Length);
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
            var renderRequest = CreateMapRenderRequest(request, layer);
            if (renderRequest == null)
            {
                OgcMapsLog.InvalidMapParameters(_logger, layerId, "Failed to parse styled map request parameters");
                return Results.BadRequest("Invalid map request parameters");
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

            return Results.File(result.Data, result.ContentType);
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
                detail: ex.Message,
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

    private MapRenderRequest? CreateMapRenderRequest(OgcMapRequest request, Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        try
        {
            // Parse bounding box
            double[] bbox;
            if (!string.IsNullOrEmpty(request.Bbox))
            {
                if (!RasterParsingHelpers.TryParseBoundingBox(request.Bbox, out var minX, out var minY, out var maxX, out var maxY))
                {
                    return null; // Invalid bbox format
                }
                bbox = [minX, minY, maxX, maxY];
            }
            else
            {
                // Use layer extent as default - validate extent exists
                if (layer.Extent == null)
                {
                    return null;
                }
                var extent = layer.Extent.Value;
                bbox = [extent.MinX, extent.MinY, extent.MaxX, extent.MaxY];
                OgcMapsLog.UsingDefaultBounds(_logger, layer.Id, extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
            }

            // Parse CRS - log when requested CRS is not recognized
            var outputCrs = SpatialReferenceHelpers.TryParseSrid(request.Crs);
            if (!string.IsNullOrEmpty(request.Crs) && outputCrs == null)
            {
                OgcMapsLog.UnsupportedCrs(_logger, request.Crs);
                return null;
            }

            var bboxCrs = SpatialReferenceHelpers.TryParseSrid(request.BboxCrs);
            if (!string.IsNullOrEmpty(request.BboxCrs) && bboxCrs == null)
            {
                OgcMapsLog.UnsupportedCrs(_logger, request.BboxCrs);
                return null;
            }

            // Parse format
            if (!TryParseRequestedFormat(request.F, out var format))
            {
                return null;
            }

            // Parse and validate dimensions (prevent DoS via oversized images)
            var width = request.Width ?? DefaultImageDimension;
            var height = request.Height ?? DefaultImageDimension;
            if (width < 1 || width > MaxImageDimension || height < 1 || height > MaxImageDimension)
            {
                OgcMapsLog.MapDimensionsExceeded(_logger, width, height, MaxImageDimension, MaxImageDimension);
                return null;
            }

            if (request.Quality is < 1 or > 100)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(request.BackgroundColor) && !_backgroundColorPattern.IsMatch(request.BackgroundColor))
            {
                return null;
            }

            // Parse datetime using invariant culture to avoid ambiguous date formats
            DateTimeOffset? datetime = null;
            if (!string.IsNullOrEmpty(request.Datetime))
            {
                if (!DateTimeOffset.TryParse(request.Datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
                {
                    return null;
                }

                datetime = parsedDateTime;
            }

            return new MapRenderRequest
            {
                BoundingBox = bbox,
                BoundingBoxCrs = bboxCrs,
                Crs = outputCrs,
                Width = width,
                Height = height,
                Format = format,
                Transparent = request.Transparent ?? true,
                BackgroundColor = request.BackgroundColor,
                DateTime = datetime,
                Quality = request.Quality
            };
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryParseRequestedFormat(string? requestedFormat, out RasterFormat rasterFormat)
    {
        rasterFormat = RasterFormat.PNG;

        if (string.IsNullOrWhiteSpace(requestedFormat))
        {
            return true;
        }

        switch (requestedFormat.Trim().ToLowerInvariant())
        {
            case "png":
                rasterFormat = RasterFormat.PNG;
                return true;
            case "jpeg":
            case "jpg":
                rasterFormat = RasterFormat.JPEG;
                return true;
            case "tiff":
            case "tif":
                rasterFormat = RasterFormat.TIFF;
                return true;
            default:
                return false;
        }
    }


}
