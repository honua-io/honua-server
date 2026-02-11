// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
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

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterMapRenderer _mapRenderer;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<OgcMapsRenderingHandler> _logger;

    public OgcMapsRenderingHandler(
        ILayerCatalog layerCatalog,
        IRasterMapRenderer mapRenderer,
        IRasterStore rasterStore,
        ILogger<OgcMapsRenderingHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _mapRenderer = mapRenderer ?? throw new ArgumentNullException(nameof(mapRenderer));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Renders a map from a single collection.
    /// </summary>
    public async Task<IResult> RenderCollectionMapAsync(
        int layerId,
        OgcMapRequest request,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return Results.NotFound();
            }

            // Start telemetry activity
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "render",
                HonuaTelemetry.Protocols.OgcMaps,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "render-collection-map");

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

            OgcMapsLog.CollectionMapRenderCompleted(_logger, layerId, result.Data.Length);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, 1);

            return Results.File(result.Data, result.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.CollectionMapRenderFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while rendering the collection map.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    /// <summary>
    /// Renders a map from multiple collections (dataset-wide).
    /// </summary>
    public async Task<IResult> RenderDatasetMapAsync(
        int[] layerIds,
        OgcMapRequest request,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

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
                    return Results.NotFound();
                }
                layers.Add(layer);
            }

            // Start telemetry activity for dataset map rendering
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "render",
                HonuaTelemetry.Protocols.OgcMaps,
                string.Join(",", layerIds));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "render-dataset-map");
            featureActivity?.SetTag("layer_count", layerIds.Length);

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

            OgcMapsLog.DatasetMapRenderCompleted(_logger, layerIds.Length, result.Data.Length);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, layerIds.Length);

            return Results.File(result.Data, result.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.DatasetMapRenderFailed(_logger, ex, layerIds.Length);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while rendering the dataset map.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    /// <summary>
    /// Renders a map with a specific style applied.
    /// </summary>
    public async Task<IResult> RenderStyledMapAsync(
        int layerId,
        string styleId,
        OgcMapRequest request,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                OgcMapsLog.CollectionNotFound(_logger, layerId);
                return Results.NotFound();
            }

            // Start telemetry activity
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "render",
                HonuaTelemetry.Protocols.OgcMaps,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "render-styled-map");
            featureActivity?.SetTag("style_id", styleId);

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

            OgcMapsLog.StyledMapRenderCompleted(_logger, layerId, styleId, result.Data.Length);

            // Record telemetry success
            HonuaTelemetry.SetSuccess(featureActivity, 1);

            return Results.File(result.Data, result.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcMapsLog.StyledMapRenderFailed(_logger, ex, layerId, styleId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while rendering the styled map.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

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
            }

            var bboxCrs = SpatialReferenceHelpers.TryParseSrid(request.BboxCrs);

            // Parse format
            var format = RasterParsingHelpers.ParseRasterFormat(request.F ?? "png");

            // Parse and validate dimensions (prevent DoS via oversized images)
            var width = request.Width ?? DefaultImageDimension;
            var height = request.Height ?? DefaultImageDimension;
            if (width < 1 || width > MaxImageDimension || height < 1 || height > MaxImageDimension)
            {
                OgcMapsLog.MapDimensionsExceeded(_logger, width, height, MaxImageDimension, MaxImageDimension);
                return null;
            }

            // Parse datetime using invariant culture to avoid ambiguous date formats
            DateTimeOffset? datetime = null;
            if (!string.IsNullOrEmpty(request.Datetime) &&
                DateTimeOffset.TryParse(request.Datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDateTime))
            {
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


}
