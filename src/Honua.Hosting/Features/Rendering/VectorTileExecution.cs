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

namespace Honua.Infrastructure.Rendering;

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
        var ttlSeconds = TilesetTtlResolver.Resolve(
            tileOptions,
            serviceId ?? string.Empty,
            layerId ?? storageLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tileMatrixSetId ?? DefaultTileMatrixSetId);
        var cacheControl = $"public, max-age={ttlSeconds}";

        var tileData = await tileProvider.GetMvtTileAsync(
            storageLayerId,
            tileCol,
            tileRow,
            zoomLevel,
            query,
            tileOptions,
            tileLimits,
            gridGeometry,
            cancellationToken);

        if (tileData == null || tileData.Length == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, 0);
            context.Response.Headers["Cache-Control"] = cacheControl;
            return Results.NoContent();
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        activity?.SetTag("honua.tile.bytes", tileData.Length);
        context.Response.Headers["Cache-Control"] = cacheControl;
        return Results.Bytes(tileData, MvtContentType);
    }
}
