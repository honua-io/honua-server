// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server tile operations.
/// Provides pre-tiled image access for efficient web mapping.
/// Falls back to cloud-hosted COG tile serving when PostGIS does not produce a tile for the requested coordinates (Pro edition).
/// </summary>
internal sealed class ImageServerTileHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ICogTileResolver? _cogTileResolver;
    private readonly ILogger<ImageServerTileHandler> _logger;

    public ImageServerTileHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerTileHandler> logger,
        ICogTileResolver? cogTileResolver = null)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _cogTileResolver = cogTileResolver;
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

            if (!ImageServerMosaicHelpers.TryParseTime(context.Request.Query["time"], out var timestamp, out var timeError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, timeError ?? "Invalid time parameter.");
            }

            var editionError = ImageServerMosaicHelpers.RequireTemporalMosaicAccess(context, timestamp);
            if (editionError != null)
            {
                return editionError;
            }

            var mergeStrategy = ImageServerMosaicHelpers.ResolveMergeStrategy(
                layer.Metadata,
                context.Request.Query["mosaicRule"]);

            var tileGeometry = CreateTileEnvelope(level, row, col);
            var selectedRasters = await _rasterStore.QueryRastersAsync(
                layerId,
                new RasterSelectionQuery
                {
                    Geometry = tileGeometry,
                    GeometrySrid = 3857,
                    Timestamp = timestamp
                },
                cancellationToken);

            ImageServerLog.ImageTileRequested(_logger, layerId, level, row, col);

            if (selectedRasters.Length == 0 && _cogTileResolver == null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            if (selectedRasters.Length > 0)
            {
                // Get the image tile from PostGIS
                var tileResult = selectedRasters.Length == 1
                    ? await _rasterStore.GetImageTileAsync(
                        layerId,
                        selectedRasters[0].Id,
                        level,
                        row,
                        col,
                        rasterFormat,
                        cancellationToken)
                    : await _rasterStore.GetMosaicImageTileAsync(
                        layerId,
                        selectedRasters.Select(r => r.Id).ToArray(),
                        mergeStrategy,
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

            // Fallback: Check COGs (Pro edition required)
            if (_cogTileResolver != null)
            {
                var lookup = await _cogTileResolver.GetTileForLayerAsync(
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

    private static byte[] CreateTileEnvelope(int level, int row, int col)
    {
        const double worldExtent = 20037508.342789244;
        var tileSpan = (worldExtent * 2d) / (1 << level);
        var minX = -worldExtent + (col * tileSpan);
        var maxX = minX + tileSpan;
        var maxY = worldExtent - (row * tileSpan);
        var minY = maxY - tileSpan;
        return ImageServerMosaicHelpers.CreateEnvelopeGeometry(minX, minY, maxX, maxY);
    }
}
