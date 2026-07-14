// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Protocols.GeoServices;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Image Server tile operations.
/// Provides pre-tiled image access for efficient web mapping.
/// Falls back to cloud-hosted COG tile serving when PostGIS does not produce a tile for the requested coordinates (Pro edition).
/// </summary>
internal sealed class ImageServerTileHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ICogTileResolver? _cogTileResolver;
    private readonly ILogger<ImageServerTileHandler> _logger;

    public ImageServerTileHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerTileHandler> logger,
        ICogTileResolver? cogTileResolver = null)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
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
            // Validate layer exists in the Metadata v2 graph
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            try
            {
                WebMercatorTileCoordinates.Validate(level, row, col);
            }
            catch (ArgumentOutOfRangeException)
            {
                // lgtm[cs/user-controlled-bypass]
                // This branch validates public tile coordinates only. ImageServerEndpoints enforces
                // resource access before invoking the handler; coordinate values cannot bypass it.
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

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(
                resolved.Resource,
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
                var storage = context.RequestServices.GetService<ICloudFileStorage>();
                var storageOptions = context.RequestServices.GetService<IOptions<CloudStorageOptions>>()?.Value;
                var tileCacheKeyIndex = context.RequestServices.GetService<ITileCacheKeyIndex>();
                var tileCacheKey = ImageServerTileCacheKey.Build(
                    storageOptions,
                    snapshot.Etag,
                    layerId,
                    TileMatrixSetRegistry.WebMercatorQuadId,
                    DefaultStyleId,
                    ResolveTenantAuthKey(context),
                    selectedRasters,
                    mergeStrategy,
                    timestamp,
                    context.Request.Query["mosaicRule"].ToString(),
                    rasterFormat,
                    level,
                    row,
                    col);

                if (await GeoServicesCloudTileCache.TryReadAsync(storage, storageOptions, tileCacheKey, cancellationToken, tileCacheKeyIndex).ConfigureAwait(false) is { } cachedTile)
                {
                    ImageServerLog.ImageTileGenerated(_logger, layerId, cachedTile.Data.Length);
                    scope.SetSuccess(1);
                    return Results.File(cachedTile.Data, cachedTile.ContentType);
                }

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
                    await GeoServicesCloudTileCache.TryWriteAsync(
                        storage,
                        storageOptions,
                        tileCacheKey,
                        result.Data,
                        result.ContentType,
                        $"{level.ToString(CultureInfo.InvariantCulture)}-{col.ToString(CultureInfo.InvariantCulture)}-{row.ToString(CultureInfo.InvariantCulture)}.{ImageServerTileCacheKey.GetTileFileExtension(rasterFormat)}",
                        ImmutableDictionary<string, string>.Empty
                            .Add("operation", "tile")
                            .Add("protocol", "ImageServer")
                            .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
                            .Add("tileMatrixSetId", TileMatrixSetRegistry.WebMercatorQuadId)
                            .Add("z", level.ToString(CultureInfo.InvariantCulture))
                            .Add("x", col.ToString(CultureInfo.InvariantCulture))
                            .Add("y", row.ToString(CultureInfo.InvariantCulture))
                            .Add("format", rasterFormat.ToString())
                            .Add("metadataEtag", snapshot.Etag),
                        cancellationToken,
                        tileCacheKeyIndex).ConfigureAwait(false);
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
                    return StandardErrorHelpers.CreatePaymentRequired(
                        context,
                        "COG Serving requires an active Pro entitlement. Install a license that includes 'raster.cloud-cog-serving'.",
                        ["entitlement: raster.cloud-cog-serving"]);
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
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.ImageTileFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the image tile.");
        }
    }

    /// <summary>
    /// Gets an image tile aligned to an explicit tile matrix set (gridset) other than the default
    /// WebMercatorQuad, driving all tile geometry from the one canonical
    /// <see cref="GridGeometry"/> and rendering/reprojecting through the shared raster pipeline
    /// (<see cref="IRasterStore.GetImageTileAsync(int, long, RasterTileWindow, RasterFormat, CancellationToken)"/>).
    /// No protocol-local geodesy is performed here: the tile bounds come from
    /// <see cref="GridGeometry.GetTileBounds(int, int, int)"/>.
    /// </summary>
    public async Task<IResult> GetGridImageTileAsync(
        HttpContext context,
        int layerId,
        GridGeometry grid,
        int level,
        int row,
        int col,
        string format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);

        using var scope = HonuaTelemetryScope.StartFeature(
            "tile",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-image-tile")
             .WithTag("honua.tile.matrix_set", grid.Id)
             .WithTag(HonuaTelemetry.Tags.TileZ, level)
             .WithTag(HonuaTelemetry.Tags.TileY, row)
             .WithTag(HonuaTelemetry.Tags.TileX, col);

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (grid.GetTileBounds(col, row, level) is not { } bounds)
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

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(
                resolved.Resource,
                context.Request.Query["mosaicRule"]);

            var tileGeometry = ImageServerMosaicHelpers.CreateEnvelopeGeometry(
                bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax);
            var selectedRasters = await _rasterStore.QueryRastersAsync(
                layerId,
                new RasterSelectionQuery
                {
                    Geometry = tileGeometry,
                    GeometrySrid = grid.Srid,
                    Timestamp = timestamp
                },
                cancellationToken);

            ImageServerLog.ImageTileRequested(_logger, layerId, level, row, col);

            if (selectedRasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var storage = context.RequestServices.GetService<ICloudFileStorage>();
            var storageOptions = context.RequestServices.GetService<IOptions<CloudStorageOptions>>()?.Value;
            var tileCacheKeyIndex = context.RequestServices.GetService<ITileCacheKeyIndex>();
            var window = new RasterTileWindow
            {
                MinX = bounds.XMin,
                MinY = bounds.YMin,
                MaxX = bounds.XMax,
                MaxY = bounds.YMax,
                Srid = grid.Srid,
                TileWidth = grid.TileWidth,
                TileHeight = grid.TileHeight
            };
            var tileCacheKey = ImageServerTileCacheKey.Build(
                storageOptions,
                snapshot.Etag,
                layerId,
                grid.Id,
                DefaultStyleId,
                ResolveTenantAuthKey(context),
                selectedRasters,
                mergeStrategy,
                timestamp,
                context.Request.Query["mosaicRule"].ToString(),
                rasterFormat,
                level,
                row,
                col,
                window);

            if (await GeoServicesCloudTileCache.TryReadAsync(storage, storageOptions, tileCacheKey, cancellationToken, tileCacheKeyIndex).ConfigureAwait(false) is { } cachedTile)
            {
                ImageServerLog.ImageTileGenerated(_logger, layerId, cachedTile.Data.Length);
                scope.SetSuccess(1);
                return Results.File(cachedTile.Data, cachedTile.ContentType);
            }

            var tileResult = selectedRasters.Length == 1
                ? await _rasterStore.GetImageTileAsync(
                    layerId,
                    selectedRasters[0].Id,
                    window,
                    rasterFormat,
                    cancellationToken)
                : await _rasterStore.GetMosaicImageTileAsync(
                    layerId,
                    selectedRasters.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    window,
                    rasterFormat,
                    cancellationToken);

            if (tileResult == null)
            {
                ImageServerLog.ImageTileNotFound(_logger, layerId, level, row, col);
                return StandardErrorHelpers.CreateNotFound(context, "Image tile not found.");
            }

            var result = tileResult.Value;
            ImageServerLog.ImageTileGenerated(_logger, layerId, result.Data.Length);
            scope.SetSuccess(1);
            await GeoServicesCloudTileCache.TryWriteAsync(
                storage,
                storageOptions,
                tileCacheKey,
                result.Data,
                result.ContentType,
                $"{level.ToString(CultureInfo.InvariantCulture)}-{col.ToString(CultureInfo.InvariantCulture)}-{row.ToString(CultureInfo.InvariantCulture)}.{ImageServerTileCacheKey.GetTileFileExtension(rasterFormat)}",
                ImmutableDictionary<string, string>.Empty
                    .Add("operation", "tile")
                    .Add("protocol", "ImageServer")
                    .Add("layerId", layerId.ToString(CultureInfo.InvariantCulture))
                    .Add("tileMatrixSetId", grid.Id)
                    .Add("z", level.ToString(CultureInfo.InvariantCulture))
                    .Add("x", col.ToString(CultureInfo.InvariantCulture))
                    .Add("y", row.ToString(CultureInfo.InvariantCulture))
                    .Add("format", rasterFormat.ToString())
                    .Add("metadataEtag", snapshot.Etag),
                cancellationToken,
                tileCacheKeyIndex).ConfigureAwait(false);
            return Results.File(result.Data, result.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
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

    private static class WebMercatorTileCoordinates
    {
        private const int MaxZoomLevel = 28;

        internal static void Validate(int level, int row, int col)
        {
            if (level < 0 || level > MaxZoomLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(level));
            }

            var matrixWidth = 1 << level;
            if (row < 0 || row >= matrixWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            if (col < 0 || col >= matrixWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(col));
            }
        }
    }

    // ImageServer tiles are only served in the "default" style; kept as a named constant so the
    // cache key partitions by style even though a non-default style cannot currently be requested.
    private const string DefaultStyleId = "default";

    // Derives the tenant/auth discriminator folded into the tile cache key. Tenant context must
    // vary the key even for anonymous requests because a configured default tenant can still scope
    // catalog and raster access. The value is hashed by ImageServerTileCacheKey and is never exposed
    // in the object path.
    internal static string ResolveTenantAuthKey(HttpContext context)
    {
        var tenantId = context.RequestServices.GetService<ITenantContext>()?.TenantId ?? string.Empty;
        var principal = context.User;
        var identity = principal.Identity;
        var principalId = identity?.IsAuthenticated == true
            ? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub")
                ?? identity.Name
                ?? string.Empty
            : string.Empty;
        var authenticationType = identity?.IsAuthenticated == true
            ? identity.AuthenticationType ?? string.Empty
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenant:{tenantId.Length}:{tenantId}|auth:{authenticationType.Length}:{authenticationType}|principal:{principalId.Length}:{principalId}");
    }
}
