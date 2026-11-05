// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Models;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

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
        FeatureQuery query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken,
        Activity? activity = null,
        string? serviceId = null,
        string? layerId = null,
        string? tileMatrixSetId = null,
        GridGeometry? gridGeometry = null)
    {
        byte[]? tileData;
        try
        {
            tileData = await tileProvider.GetMvtTileAsync(
                storageLayerId,
                tileCol,
                tileRow,
                zoomLevel,
                query,
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
            ApplyCacheHeaders(context, tileOptions, serviceId, layerId, storageLayerId, tileMatrixSetId);
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", tileData.Length);
        ApplyCacheHeaders(context, tileOptions, serviceId, layerId, storageLayerId, tileMatrixSetId);
        return Results.Bytes(tileData, MvtContentType);
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

    internal static void ApplyCacheHeaders(
        HttpContext context,
        TileOptions tileOptions,
        string? serviceId,
        string? layerId,
        int storageLayerId,
        string? tileMatrixSetId = null)
    {
        var ttlSeconds = TilesetTtlResolver.Resolve(
            tileOptions,
            serviceId ?? string.Empty,
            layerId ?? storageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tileMatrixSetId ?? DefaultTileMatrixSetId);
        var credentialed = context.User.Identity?.IsAuthenticated == true
            || context.Request.Headers.ContainsKey(HeaderNames.Authorization)
            || context.Request.Headers.ContainsKey("X-API-Key");
        context.Response.Headers[HeaderNames.CacheControl] =
            $"{(credentialed ? "private" : "public")}, max-age={ttlSeconds}";
        if (credentialed)
        {
            context.Response.Headers[HeaderNames.Vary] = "Authorization, X-API-Key";
        }
    }
}
