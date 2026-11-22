// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.SharedCaching;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.Rendering;

internal static partial class VectorTileExecution
{
    private const string MvtContentType = "application/vnd.mapbox-vector-tile";

    internal static FeatureQuery CreateQuery(
        int spatialReferenceSrid,
        string? where = null,
        SqlFragment? sqlFilter = null,
        TemporalFilter? temporalFilter = null)
        => new()
        {
            Where = where,
            SqlFilter = sqlFilter,
            SpatialReferenceSrid = spatialReferenceSrid,
            TemporalFilter = temporalFilter
        };

    // Default tile matrix set for tile serve paths that do not carry an explicit
    // matrix set in their route (e.g. the GeoServices /tiles/{layerId} endpoint).
    private const string DefaultTileMatrixSetId = "WebMercatorQuad";

    internal static async Task<IResult> ExecuteAsync(
        HttpContext context,
        ITileProvider tileProvider,
        int storageLayerId,
        int tileCol,
        int tileRow,
        int zoomLevel,
        VectorTileQuery query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken,
        Activity? activity = null,
        string? serviceId = null,
        string? layerId = null,
        string? tileMatrixSetId = null,
        GridGeometry? gridGeometry = null)
    {
        if (query.IsRefused)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Invalid query parameters", ["The request references a field that is not available."]);
        }

        if (tileProvider is IPooledTileProvider pooledProvider)
        {
            return await ExecutePooledAsync(
                pooledProvider,
                new MvtTileRequest { LayerId = storageLayerId, X = tileCol, Y = tileRow, Z = zoomLevel, Query = query.Value, TileOptions = tileOptions, TileLimits = tileLimits, GridGeometry = gridGeometry },
                new TileResponseContext { HttpContext = context, Activity = activity, ServiceId = serviceId, LayerId = layerId, TileMatrixSetId = tileMatrixSetId, CancellationToken = cancellationToken }).ConfigureAwait(false);
        }

        byte[]? tileData;
        try
        {
            tileData = await tileProvider.GetMvtTileAsync(
                storageLayerId,
                tileCol,
                tileRow,
                zoomLevel,
                query.Value,
                tileOptions,
                tileLimits,
                gridGeometry,
                cancellationToken);
        }
        catch (TileSizeLimitExceededException ex)
        {
            return RefuseOversizedTile(
                context, activity, storageLayerId, tileCol, tileRow, zoomLevel, ex.EncodedBytes, tileLimits.MaxTileSize);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (tileData?.LongLength > tileLimits.MaxTileSize)
        {
            return RefuseOversizedTile(
                context, activity, storageLayerId, tileCol, tileRow, zoomLevel, tileData.LongLength, tileLimits.MaxTileSize);
        }

        if (tileData == null || tileData.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            UseTilesetLifetime(context, tileOptions, serviceId, layerId, storageLayerId, tileMatrixSetId);
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", tileData.Length);
        UseTilesetLifetime(context, tileOptions, serviceId, layerId, storageLayerId, tileMatrixSetId);
        return Results.Bytes(tileData, MvtContentType);
    }

    private readonly record struct TileResponseContext
    {
        internal required HttpContext HttpContext { get; init; }
        internal Activity? Activity { get; init; }
        internal string? ServiceId { get; init; }
        internal string? LayerId { get; init; }
        internal string? TileMatrixSetId { get; init; }
        internal CancellationToken CancellationToken { get; init; }
    }

    private static async Task<IResult> ExecutePooledAsync(
        IPooledTileProvider pooledProvider, MvtTileRequest request, TileResponseContext response)
    {
        PooledTileData? tileData;
        try
        {
            tileData = await pooledProvider.GetMvtTilePooledAsync(request, response.CancellationToken).ConfigureAwait(false);
        }
        catch (TileSizeLimitExceededException ex)
        {
            return RefuseOversizedTile(
                response.HttpContext, response.Activity, request.LayerId, request.X, request.Y, request.Z,
                ex.EncodedBytes, request.TileLimits.MaxTileSize);
        }

        return FinalizePooledTile(tileData, request, response);
    }

    private static IResult FinalizePooledTile(PooledTileData? tileData, MvtTileRequest request, TileResponseContext response)
    {
        try
        {
            response.CancellationToken.ThrowIfCancellationRequested();
            if (tileData is { Length: > 0 } && tileData.Length > request.TileLimits.MaxTileSize)
            {
                var encodedBytes = tileData.Length;
                tileData.Dispose();
                return RefuseOversizedTile(
                    response.HttpContext, response.Activity, request.LayerId, request.X, request.Y, request.Z,
                    encodedBytes, request.TileLimits.MaxTileSize);
            }

            response.Activity?.SetStatus(ActivityStatusCode.Ok);
            UseTilesetLifetime(response.HttpContext, request.TileOptions, response.ServiceId, response.LayerId, request.LayerId, response.TileMatrixSetId);
            if (tileData is null || tileData.Length == 0)
            {
                tileData?.Dispose();
                response.Activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
                return Results.NoContent();
            }

            response.Activity?.SetTag("honua.tile.bytes", tileData.Length);
            return new PooledTileContentResult(tileData, MvtContentType);
        }
        catch
        {
            tileData?.Dispose();
            throw;
        }
    }

    // The over-budget refusal is logged because a cached refusal skips this path, so the line is once per tile per cache lifetime.
    private static IResult RefuseOversizedTile(
        HttpContext context,
        Activity? activity,
        int storageLayerId,
        int tileCol,
        int tileRow,
        int zoomLevel,
        long encodedBytes,
        long maxTileSize)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("honua.tile.bytes", encodedBytes);

        var logger = context.RequestServices
            .GetService<ILoggerFactory>()?
            .CreateLogger(typeof(VectorTileExecution));
        if (logger is not null)
        {
            LogTileRefusedOverBudget(logger, storageLayerId, zoomLevel, tileCol, tileRow, encodedBytes, maxTileSize);
        }

        return StandardErrorHelpers.CreatePayloadTooLarge(context, new TileSizeLimitExceededException().Message);
    }

    [LoggerMessage(
        EventId = 3473,
        Level = LogLevel.Warning,
        Message = "Refused vector tile {LayerId}/{Zoom}/{TileCol}/{TileRow}: encoded {EncodedBytes} bytes still exceeds Limits:Tiles:MaxTileSize ({MaxTileSize} bytes) after generalization.")]
    private static partial void LogTileRefusedOverBudget(
        ILogger logger,
        int layerId,
        int zoom,
        int tileCol,
        int tileRow,
        long encodedBytes,
        long maxTileSize);

    internal static void UseTilesetLifetime(
        HttpContext context,
        TileOptions tileOptions,
        string? serviceId,
        string? layerId,
        int storageLayerId,
        string? tileMatrixSetId = null)
    {
        var tileset = new TilesetAddress(
            serviceId ?? string.Empty,
            layerId ?? storageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tileMatrixSetId ?? DefaultTileMatrixSetId);
        SharedCacheResponse.UseTileset(context, tileOptions, tileset);
    }
}
