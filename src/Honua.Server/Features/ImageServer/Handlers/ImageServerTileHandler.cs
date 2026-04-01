// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server tile operations.
/// Provides pre-tiled image access for efficient web mapping.
/// Falls back to cloud-hosted COG tile serving when PostGIS does not produce a tile for the requested coordinates (Pro edition).
/// </summary>
internal sealed class ImageServerTileHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ICloudCogTileResolver? _cloudCogTileResolver;
    private readonly ILogger<ImageServerTileHandler> _logger;

    public ImageServerTileHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerTileHandler> logger,
        ICloudCogTileResolver? cloudCogTileResolver = null)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _cloudCogTileResolver = cloudCogTileResolver;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a pre-generated image tile for efficient web mapping display.
    /// </summary>
    public async Task<IResult> GetImageTileAsync(
        HttpContext context,
        int layerId,
        int level,
        int row,
        int col,
        string format,
        CancellationToken cancellationToken = default)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "tile",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-image-tile")
             .WithTag(HonuaTelemetry.Tags.TileZ, level)
             .WithTag(HonuaTelemetry.Tags.TileY, row)
             .WithTag(HonuaTelemetry.Tags.TileX, col);

        try
        {
            // Validate layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            // Validate tile coordinates (Web Mercator supports zoom levels 0-28)
            const int maxZoomLevel = 28;
            if (level < 0 || level > maxZoomLevel || row < 0 || col < 0 ||
                row >= (1 << level) || col >= (1 << level))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid tile coordinates");
            }

            if (!RasterParsingHelpers.TryParseRasterFormat(format, out var rasterFormat))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Unsupported tile format. Supported formats: png, jpg, jpeg, tiff, tif, cog.");
            }

            // Resolve the primary raster without scanning the entire layer.
            var primaryRaster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);

            ImageServerLog.ImageTileRequested(_logger, layerId, level, row, col);

            if (primaryRaster is null && _cloudCogTileResolver == null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            if (primaryRaster is not null)
            {
                // Get the image tile from PostGIS
                var tileResult = await _rasterStore.GetImageTileAsync(
                    layerId,
                    primaryRaster.Value.Id,
                    level,
                    row,
                    col,
                    rasterFormat,
                    cancellationToken);

                if (tileResult != null)
                {
                    var result = tileResult.Value;
                    ImageServerLog.ImageTileGenerated(_logger, layerId, result.Data.Length);
                    scope.SetSuccess(1);
                    return Results.File(result.Data, result.ContentType);
                }
            }

            // Fallback: Check cloud COGs (Pro edition required)
            if (_cloudCogTileResolver != null)
            {
                var lookup = await _cloudCogTileResolver.GetTileForLayerAsync(
                    layerId, level, row, col, rasterFormat, cancellationToken);

                if (lookup.EditionGateHit)
                {
                    scope.WithTag("edition.gated", "true");
                    return Results.StatusCode(StatusCodes.Status402PaymentRequired);
                }

                if (lookup.Result != null)
                {
                    var cloudResult = lookup.Result.Value;
                    ImageServerLog.ImageTileGenerated(_logger, layerId, cloudResult.Data.Length);
                    scope.SetSuccess(1);
                    return Results.File(cloudResult.Data, cloudResult.ContentType);
                }
            }

            ImageServerLog.ImageTileNotFound(_logger, layerId, level, row, col);
            return StandardErrorHelpers.CreateNotFound(context, "Image tile not found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ImageTileFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the image tile.");
        }
    }
}
