// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server tile operations.
/// Provides pre-tiled image access for efficient web mapping.
/// </summary>
internal sealed class ImageServerTileHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerTileHandler> _logger;

    public ImageServerTileHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerTileHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a pre-generated image tile for efficient web mapping display.
    /// </summary>
    public async Task<IResult> GetImageTileAsync(
        int layerId,
        int level,
        int row,
        int col,
        string format,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return Results.NotFound();
            }

            // Validate tile coordinates
            if (level < 0 || row < 0 || col < 0)
            {
                return Results.BadRequest("Tile coordinates (level, row, col) must be non-negative");
            }

            // Start telemetry activity with tile-specific tags
            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "tile",
                HonuaTelemetry.Protocols.ImageServer,
                layerId.ToString(CultureInfo.InvariantCulture));
            featureActivity?.SetTag(HonuaTelemetry.Tags.Operation, "get-image-tile");
            featureActivity?.SetTag(HonuaTelemetry.Tags.TileZ, level);
            featureActivity?.SetTag(HonuaTelemetry.Tags.TileY, row);
            featureActivity?.SetTag(HonuaTelemetry.Tags.TileX, col);

            // Get raster data
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return Results.NotFound();
            }

            ImageServerLog.ImageTileRequested(_logger, layerId, level, row, col);

            // Use the first raster (could be enhanced for multi-raster scenarios)
            var primaryRaster = rasters[0];

            // Parse format
            var rasterFormat = RasterParsingHelpers.ParseRasterFormat(format);

            // Get the image tile
            var tileResult = await _rasterStore.GetImageTileAsync(
                layerId,
                primaryRaster.Id,
                level,
                row,
                col,
                rasterFormat,
                cancellationToken);

            if (tileResult == null)
            {
                ImageServerLog.ImageTileNotFound(_logger, layerId, level, row, col);
                return Results.NotFound();
            }

            var result = tileResult.Value;
            ImageServerLog.ImageTileGenerated(_logger, layerId, result.Data.Length);

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
            ImageServerLog.ImageTileFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return Results.Problem("An error occurred while retrieving the image tile.", statusCode: 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }
}
