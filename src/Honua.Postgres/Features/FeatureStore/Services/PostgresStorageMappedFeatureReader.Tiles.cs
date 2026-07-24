// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Native MVT support for a PostGIS reader bound to a Metadata v2 storage mapping and secure
/// connection. Keeping tile SQL on this binding-scoped reader prevents routed publications from
/// falling back to the primary PostGIS connection.
/// </summary>
internal sealed partial class PostgresStorageMappedFeatureReader : ITileProvider
{
    /// <inheritdoc />
    public async Task<byte[]?> GetMvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GridGeometry? gridGeometry = null,
        CancellationToken cancellationToken = default)
    {
        if (_geometryColumn is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(tileOptions);
        ArgumentNullException.ThrowIfNull(tileLimits);

        var effectiveQuery = query ?? new FeatureQuery();
        var targetSrid = gridGeometry?.Srid
            ?? (effectiveQuery.OutputSrid == 4326 ? 4326 : 3857);
        var bounds = gridGeometry is null
            ? targetSrid == 4326
                ? TileMath.GetTileBoundsGeographic(x, y, z)
                : TileMath.GetTileBounds(x, y, z)
            : gridGeometry.GetTileBounds(x, y, z)
                ?? throw new ArgumentOutOfRangeException(
                    nameof(z),
                    $"Tile matrix level '{z}' is not part of gridset '{gridGeometry.Id}'.");

        var tileExtent = tileOptions.TileExtent > 0 ? tileOptions.TileExtent : 4096;
        var tileWidth = bounds.XMax - bounds.XMin;
        var bufferMapUnits = tileOptions.TileBuffer > 0
            ? (tileOptions.TileBuffer / (double)tileExtent) * tileWidth
            : 0d;

        var sql = new SqlBuilder();
        var tileEnvelope = BuildTileEnvelope(sql, bounds, targetSrid, x, y, z, gridGeometry is not null);
        var bufferedEnvelope = $"ST_Expand({tileEnvelope}, {sql.AddParameter(bufferMapUnits)})";
        if (gridGeometry is null)
        {
            bufferedEnvelope = targetSrid == 4326
                ? $"ST_Intersection({bufferedEnvelope}, ST_MakeEnvelope(-180, -90, 180, 90, 4326))"
                : $"ST_Intersection({bufferedEnvelope}, ST_TileEnvelope(0, 0, 0))";
        }

        var geometry = $"{_geometryColumn}::geometry";
        var geometryForTile = _storageSrid == targetSrid
            ? geometry
            : $"ST_Transform({geometry}, {targetSrid})";
        var envelopeForFilter = _storageSrid == targetSrid
            ? bufferedEnvelope
            : $"ST_Transform({bufferedEnvelope}, {_storageSrid})";
        var extentParameter = sql.AddParameter(tileExtent);
        var bufferParameter = sql.AddParameter(tileOptions.TileBuffer);
        var attributes = BuildAttributesExpression(effectiveQuery);

        sql.Append(CultureInfo.InvariantCulture, $"""
            SELECT ST_AsMVT(tile, 'layer', {extentParameter}, 'geom')
            FROM (
                SELECT
                    {_primaryKeyColumn}::bigint AS objectid,
                    ({attributes})::jsonb AS attributes,
                    ST_AsMVTGeom(
                        {geometryForTile},
                        {tileEnvelope},
                        {extentParameter},
                        {bufferParameter}
                    ) AS geom
                FROM {_qualifiedTableName}
            """);
        AppendFilter(sql, effectiveQuery);
        sql.Append(
            CultureInfo.InvariantCulture,
            $" {BuildWhereJoiner(sql)} {geometry} && {envelopeForFilter}" +
            $" AND ST_Intersects({geometry}, {envelopeForFilter})");

        if (tileLimits.MaxFeaturesPerTile > 0)
        {
            sql.Append(
                CultureInfo.InvariantCulture,
                $" LIMIT {sql.AddParameter(tileLimits.MaxFeaturesPerTile)}");
        }

        sql.Append(CultureInfo.InvariantCulture, """

            ) AS tile
            """);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateReadCommand(connection, sql);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : (byte[])result;
    }

    /// <inheritdoc />
    public Task<byte[]?> GetH3MvtTileAsync(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "H3 vector tiles are not supported for source-backed PostGIS layers yet.");

    private static string BuildTileEnvelope(
        SqlBuilder sql,
        TileBounds bounds,
        int targetSrid,
        int x,
        int y,
        int z,
        bool isCustomGrid)
    {
        if (!isCustomGrid && targetSrid != 4326)
        {
            var zParameter = sql.AddParameter(z);
            var xParameter = sql.AddParameter(x);
            var yParameter = sql.AddParameter(y);
            return $"ST_TileEnvelope({zParameter}, {xParameter}, {yParameter})";
        }

        var minX = sql.AddParameter(bounds.XMin);
        var minY = sql.AddParameter(bounds.YMin);
        var maxX = sql.AddParameter(bounds.XMax);
        var maxY = sql.AddParameter(bounds.YMax);
        return $"ST_MakeEnvelope({minX}, {minY}, {maxX}, {maxY}, {targetSrid})";
    }
}
