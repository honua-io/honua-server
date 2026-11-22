// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Db.Postgres.Features.Raster;

internal sealed partial class PostgresTerrainTileService
{
    private const double WebMercatorSafeLatitude = 85.05112877;

    public async Task<TerrainElevationGrid> GetGeographicElevationGridAsync(
        TerrainGeographicGridRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.GridSize is < TerrainTmsGrid.MinGridSize or > TerrainTmsGrid.MaxGridSize)
        {
            throw new TerrainTileException(
                TerrainTileFailureKind.UnsupportedSource,
                $"Quantized-mesh grid size must be between {TerrainTmsGrid.MinGridSize} and {TerrainTmsGrid.MaxGridSize}.");
        }

        if (!TerrainTmsGrid.ValidateTileCoordinates(request.Z, request.X, request.Y))
        {
            throw new TerrainTileException(
                TerrainTileFailureKind.UnsupportedSource,
                $"Tile coordinates z={request.Z}, x={request.X}, y={request.Y} are outside TMS geographic bounds.");
        }

        _ = await ValidateSupportedMetadataAsync(
            request.LayerId,
            request.MergeStrategy,
            cancellationToken).ConfigureAwait(false);

        var bounds = TerrainTmsGrid.GetTileBounds(request.Z, request.X, request.Y);
        var rasterIds = await GetIntersectingRasterIdsGeographicAsync(
            request.LayerId,
            bounds,
            cancellationToken).ConfigureAwait(false);

        var elevations = new double?[request.GridSize * request.GridSize];
        if (rasterIds.Length == 0)
        {
            PostgresTerrainLog.QuantizedMeshGridSampled(
                _logger,
                request.LayerId,
                request.Z,
                request.X,
                request.Y,
                request.GridSize,
                rasterCount: 0,
                allNoData: true);
            return new TerrainElevationGrid
            {
                GridSize = request.GridSize,
                Bounds = bounds,
                Elevations = elevations,
                SelectedRasterCount = 0,
                IsAllNoData = true
            };
        }

        var hasData = await PopulateGeographicElevationGridAsync(
            request,
            bounds,
            rasterIds,
            elevations,
            cancellationToken).ConfigureAwait(false);

        PostgresTerrainLog.QuantizedMeshGridSampled(
            _logger,
            request.LayerId,
            request.Z,
            request.X,
            request.Y,
            request.GridSize,
            rasterIds.Length,
            allNoData: !hasData);

        return new TerrainElevationGrid
        {
            GridSize = request.GridSize,
            Bounds = bounds,
            Elevations = elevations,
            SelectedRasterCount = rasterIds.Length,
            IsAllNoData = !hasData
        };
    }

    private async Task<TerrainDatasetMetadata> ValidateSupportedMetadataAsync(
        int layerId,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken)
    {
        var metadata = await GetDatasetMetadataAsync(layerId, mergeStrategy, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new TerrainTileException(
                TerrainTileFailureKind.SourceUnavailable,
                "No raster source is registered for this terrain dataset.");
        }

        if (!metadata.IsSupported)
        {
            var kind = metadata.UnsupportedReasons.Any(static reason => reason.Contains("CRS", StringComparison.OrdinalIgnoreCase))
                ? TerrainTileFailureKind.UnknownCrs
                : TerrainTileFailureKind.UnsupportedSource;
            throw new TerrainTileException(kind, string.Join(" ", metadata.UnsupportedReasons));
        }

        return metadata;
    }

    private async Task<long[]> GetIntersectingRasterIdsGeographicAsync(
        int layerId,
        TerrainGeographicBounds bounds,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH tile_bounds AS (
                SELECT ST_MakeEnvelope(@west, @south, @east, @north, 4326) AS geom_4326,
                       CASE
                           WHEN GREATEST(@south, -@mercatorMaxLat) < LEAST(@north, @mercatorMaxLat)
                           THEN ST_Transform(
                               ST_MakeEnvelope(@west, GREATEST(@south, -@mercatorMaxLat), @east, LEAST(@north, @mercatorMaxLat), 4326),
                               3857)
                           ELSE NULL
                       END AS geom_3857
            )
            SELECT id
            FROM {_rasterDataTable}, tile_bounds tb
            WHERE layer_id = @layerId
              AND srid > 0
              AND (
                  (srid = 4326 AND ST_Intersects(ST_ConvexHull(raster), tb.geom_4326))
                  OR (srid = 3857 AND tb.geom_3857 IS NOT NULL AND ST_Intersects(ST_ConvexHull(raster), tb.geom_3857))
                  OR (srid NOT IN (4326, 3857) AND ST_Intersects(ST_ConvexHull(raster), ST_Transform(tb.geom_4326, srid)))
              )
            ORDER BY COALESCE(acquisition_date, created_at) DESC, created_at DESC, id DESC
            """;
        AddParameter(command, "@layerId", layerId);
        AddParameter(command, "@west", bounds.West);
        AddParameter(command, "@south", bounds.South);
        AddParameter(command, "@east", bounds.East);
        AddParameter(command, "@north", bounds.North);
        AddParameter(command, "@mercatorMaxLat", WebMercatorSafeLatitude);

        var rasterIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rasterIds.Add(reader.GetInt64(0));
        }

        return [.. rasterIds];
    }

    private async Task<bool> PopulateGeographicElevationGridAsync(
        TerrainGeographicGridRequest request,
        TerrainGeographicBounds bounds,
        long[] rasterIds,
        double?[] elevations,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH points AS (
                SELECT gx,
                       gy,
                       @west + (gx::double precision * (@east - @west) / (@gridSize - 1)) AS lon,
                       @south + (gy::double precision * (@north - @south) / (@gridSize - 1)) AS lat
                FROM generate_series(0, @gridSize - 1) AS gx,
                     generate_series(0, @gridSize - 1) AS gy
            ),
            projected AS (
                SELECT gx,
                       gy,
                       lat,
                       ST_SetSRID(ST_MakePoint(lon, lat), 4326) AS geom
                FROM points
            ),
            samples AS (
                SELECT p.gx,
                       p.gy,
                       r.id,
                       r.created_at,
                       COALESCE(r.acquisition_date, r.created_at) AS effective_acquisition,
                       ST_Value(
                           r.raster,
                           1,
                           CASE
                               WHEN r.srid = 4326 THEN p.geom
                               WHEN r.srid = 3857 THEN ST_Transform(p.geom, 3857)
                               ELSE ST_Transform(p.geom, r.srid)
                           END,
                           true
                       ) AS value
                FROM projected p
                JOIN {_rasterDataTable} r
                  ON r.layer_id = @layerId
                 AND r.id = ANY(@rasterIds)
                WHERE (r.srid <> 3857 OR ABS(p.lat) <= @mercatorMaxLat)
                  AND ST_Intersects(
                      ST_ConvexHull(r.raster),
                      CASE
                          WHEN r.srid = 4326 THEN p.geom
                          WHEN r.srid = 3857 THEN ST_Transform(p.geom, 3857)
                          ELSE ST_Transform(p.geom, r.srid)
                      END)
            )
            SELECT p.gx,
                   p.gy,
                   CASE @mergeStrategy
                       WHEN 'Average' THEN AVG(s.value)
                       WHEN 'Max' THEN MAX(s.value)
                       WHEN 'Min' THEN MIN(s.value)
                       WHEN 'Oldest' THEN (ARRAY_AGG(s.value ORDER BY s.effective_acquisition ASC, s.created_at ASC, s.id ASC) FILTER (WHERE s.value IS NOT NULL))[1]
                       ELSE (ARRAY_AGG(s.value ORDER BY s.effective_acquisition DESC, s.created_at DESC, s.id DESC) FILTER (WHERE s.value IS NOT NULL))[1]
                   END AS elevation
            FROM projected p
            LEFT JOIN samples s ON s.gx = p.gx AND s.gy = p.gy
            GROUP BY p.gx, p.gy
            ORDER BY p.gy, p.gx
            """;
        AddParameter(command, "@layerId", request.LayerId);
        AddParameter(command, "@west", bounds.West);
        AddParameter(command, "@south", bounds.South);
        AddParameter(command, "@east", bounds.East);
        AddParameter(command, "@north", bounds.North);
        AddParameter(command, "@gridSize", request.GridSize);
        AddParameter(command, "@rasterIds", rasterIds);
        AddParameter(command, "@mergeStrategy", request.MergeStrategy.ToString());
        AddParameter(command, "@mercatorMaxLat", WebMercatorSafeLatitude);

        var hasData = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var gx = reader.GetInt32(0);
            var gy = reader.GetInt32(1);
            var elevation = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            elevations[(gy * request.GridSize) + gx] = elevation;
            hasData |= elevation.HasValue && double.IsFinite(elevation.Value);
        }

        return hasData;
    }
}
