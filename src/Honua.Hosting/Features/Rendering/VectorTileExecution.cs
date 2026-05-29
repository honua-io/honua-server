// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Infrastructure.Rendering;

internal static class VectorTileExecution
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

    /// <summary>
    /// Executes vector tile rendering for a storage layer id resolved from metadata v2.
    /// <c>ITileProvider.GetMvtTileAsync</c> consumes <c>int layerId</c> as its
    /// storage abstraction, so no further V2 plumbing is needed here.
    /// </summary>
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
        Activity? activity = null)
    {
        var tileData = await tileProvider.GetMvtTileAsync(
            storageLayerId,
            tileCol,
            tileRow,
            zoomLevel,
            query,
            tileOptions,
            tileLimits,
            cancellationToken);

        if (tileData == null || tileData.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptions.CacheMaxAge}";
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", tileData.Length);
        context.Response.Headers["Cache-Control"] = $"public, max-age={tileOptions.CacheMaxAge}";
        return Results.Bytes(tileData, MvtContentType);
    }
}
