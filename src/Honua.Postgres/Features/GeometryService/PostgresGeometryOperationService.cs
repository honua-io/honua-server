// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data.Common;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.GeometryService;

/// <summary>
/// PostGIS-backed implementation of geometry operations (buffer, simplify, project, union).
/// </summary>
internal sealed class PostgresGeometryOperationService(
    IDatabaseConnectionProvider connectionProvider) : IGeometryOperationService
{
    private readonly record struct CrsMetrics(bool IsGeographic, double MetersPerUnit);
    private static readonly ConcurrentDictionary<int, CrsMetrics> _crsMetricsCache = new();

    private readonly IDatabaseConnectionProvider _connectionProvider = connectionProvider
        ?? throw new ArgumentNullException(nameof(connectionProvider));

    public async Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        var crsMetrics = await GetCrsMetricsAsync(connection, srid, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (geodesic)
        {
            // Transform to WGS84 before geography cast; PostGIS geography only supports SRID 4326.
            // Wrap result with ST_SetSRID to ensure the output geometry has SRID 4326 set explicitly.
            cmd.CommandText = "SELECT ST_AsBinary(ST_SetSRID(ST_Buffer(ST_Transform(ST_SetSRID($1::geometry, $2), 4326)::geography, $3)::geometry, 4326))";
        }
        else if (crsMetrics.IsGeographic)
        {
            // Planar buffers over geographic CRS need linear units. Buffer in Web Mercator meters, then transform back.
            cmd.CommandText = "SELECT ST_AsBinary(ST_Transform(ST_Buffer(ST_Transform(ST_SetSRID($1::geometry, $2), 3857), $3), $2))";
        }
        else
        {
            distance = ConvertMetersToNativeUnits(distance, crsMetrics.MetersPerUnit);
            cmd.CommandText = "SELECT ST_AsBinary(ST_Buffer(ST_SetSRID($1::geometry, $2), $3))";
        }

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = distance });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS buffer returned null.");
    }

    public async Task<byte[]> SimplifyAsync(byte[] wkb, double tolerance, bool preserveTopology, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = preserveTopology
            ? "SELECT ST_AsBinary(ST_SimplifyPreserveTopology($1::geometry, $2))"
            : "SELECT ST_AsBinary(ST_Simplify($1::geometry, $2))";

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = tolerance });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS simplify returned null.");
    }

    public async Task<byte[]> ProjectAsync(byte[] wkb, int fromSrid, int toSrid, CancellationToken ct = default)
    {
        // Same-SRID no-op optimization
        if (fromSrid == toSrid)
        {
            return wkb;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = "SELECT ST_AsBinary(ST_Transform(ST_SetSRID($1::geometry, $2), $3))";

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromSrid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toSrid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS project returned null.");
    }

    public async Task<byte[]> MakeValidAsync(byte[] wkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = "SELECT ST_AsBinary(ST_MakeValid(ST_SetSRID($1::geometry, $2)))";

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS MakeValid returned null.");
    }

    public async Task<byte[]> UnionAsync(byte[][] wkbs, int srid, CancellationToken ct = default)
    {
        if (wkbs.Length == 0)
        {
            throw new ArgumentException("At least one geometry is required for union.", nameof(wkbs));
        }

        if (wkbs.Length == 1)
        {
            return wkbs[0];
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        // Build a UNION of all geometries using ST_Union aggregate over unnested array
        cmd.CommandText = "SELECT ST_AsBinary(ST_Union(ST_SetSRID(geom::geometry, $2))) FROM unnest($1::bytea[]) AS geom";

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkbs });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS union returned null.");
    }

    public async Task<byte[]> IntersectAsync(byte[] targetWkb, byte[] intersectorWkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT ST_AsBinary(
                COALESCE(
                    ST_Intersection(ST_SetSRID($1::geometry, $3), ST_SetSRID($2::geometry, $3)),
                    ST_GeomFromText('GEOMETRYCOLLECTION EMPTY', $3)
                ))
            """;

        cmd.Parameters.Add(new NpgsqlParameter { Value = targetWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = intersectorWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS intersect returned null.");
    }

    public async Task<byte[]> ClipAsync(byte[] targetWkb, byte[] clipEnvelopeWkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT ST_AsBinary(
                COALESCE(
                    ST_Intersection(ST_SetSRID($1::geometry, $3), ST_Envelope(ST_SetSRID($2::geometry, $3))),
                    ST_GeomFromText('GEOMETRYCOLLECTION EMPTY', $3)
                ))
            """;

        cmd.Parameters.Add(new NpgsqlParameter { Value = targetWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = clipEnvelopeWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS clip returned null.");
    }

    public async Task<byte[]> DifferenceAsync(byte[] targetWkb, byte[] eraserWkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        cmd.CommandText = """
            SELECT ST_AsBinary(
                COALESCE(
                    ST_Difference(ST_SetSRID($1::geometry, $3), ST_SetSRID($2::geometry, $3)),
                    ST_GeomFromText('GEOMETRYCOLLECTION EMPTY', $3)
                ))
            """;

        cmd.Parameters.Add(new NpgsqlParameter { Value = targetWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = eraserWkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS difference returned null.");
    }

    public async Task<double> AreaAsync(byte[] wkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        var crsMetrics = await GetCrsMetricsAsync(connection, srid, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (crsMetrics.IsGeographic)
        {
            cmd.CommandText = "SELECT ST_Area(ST_Transform(ST_SetSRID($1::geometry, $2), 4326)::geography)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
            cmd.Parameters.Add(new NpgsqlParameter { Value = srid });
        }
        else
        {
            cmd.CommandText = "SELECT ST_Area(ST_SetSRID($1::geometry, $2)) * $3";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
            cmd.Parameters.Add(new NpgsqlParameter { Value = srid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = crsMetrics.MetersPerUnit * crsMetrics.MetersPerUnit });
        }

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToDouble(
            result ?? throw new InvalidOperationException("PostGIS area returned null."),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<double> LengthAsync(byte[] wkb, int srid, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        var crsMetrics = await GetCrsMetricsAsync(connection, srid, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (crsMetrics.IsGeographic)
        {
            cmd.CommandText = "SELECT ST_Length(ST_Transform(ST_SetSRID($1::geometry, $2), 4326)::geography)";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
            cmd.Parameters.Add(new NpgsqlParameter { Value = srid });
        }
        else
        {
            cmd.CommandText = "SELECT ST_Length(ST_SetSRID($1::geometry, $2)) * $3";
            cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
            cmd.Parameters.Add(new NpgsqlParameter { Value = srid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = crsMetrics.MetersPerUnit });
        }

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToDouble(
            result ?? throw new InvalidOperationException("PostGIS length returned null."),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double ConvertMetersToNativeUnits(double distanceMeters, double metersPerUnit)
    {
        var normalizedMetersPerUnit = NormalizeMetersPerUnit(metersPerUnit);
        return distanceMeters / normalizedMetersPerUnit;
    }

    private static double NormalizeMetersPerUnit(double metersPerUnit)
    {
        return double.IsFinite(metersPerUnit) && metersPerUnit > 0
            ? metersPerUnit
            : 1.0;
    }

    private static async Task<CrsMetrics> GetCrsMetricsAsync(DbConnection connection, int srid, CancellationToken ct)
    {
        if (_crsMetricsCache.TryGetValue(srid, out var cached))
        {
            return cached;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                CASE
                    WHEN upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEOGCS[%'
                        OR upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEOGRAPHICCRS[%'
                        OR upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEODCRS[%'
                        OR COALESCE(proj4text, '') ILIKE '%+proj=longlat%'
                        OR srid BETWEEN 4000 AND 4999
                    THEN TRUE
                    ELSE FALSE
                END AS is_geographic,
                COALESCE(
                    NULLIF((regexp_match(COALESCE(proj4text, ''), '[+]to_meter=([0-9.eE+-]+)'))[1], '')::double precision,
                    CASE
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=m%' THEN 1.0
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=km%' THEN 1000.0
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=us-ft%' THEN 0.3048006096012192
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=ft%' THEN 0.3048
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=mi%' THEN 1609.344
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=yd%' THEN 0.9144
                        WHEN COALESCE(proj4text, '') ILIKE '%+units=nm%' THEN 1852.0
                        ELSE NULL
                    END,
                    NULLIF((regexp_match(COALESCE(srtext, ''), 'UNIT\\[[^\\]]*,\\s*([0-9.eE+-]+)\\]\\s*\\]$'))[1], '')::double precision,
                    1.0
                ) AS meters_per_unit
            FROM spatial_ref_sys
            WHERE srid = $1
            LIMIT 1
            """;
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var fallback = new CrsMetrics(IsLikelyGeographicSrid(srid), 1.0);
            _crsMetricsCache.TryAdd(srid, fallback);
            return fallback;
        }

        var isGeographic = !reader.IsDBNull(0) && reader.GetBoolean(0);
        var metersPerUnit = reader.IsDBNull(1) ? 1.0 : reader.GetDouble(1);
        var metrics = new CrsMetrics(isGeographic, NormalizeMetersPerUnit(metersPerUnit));
        _crsMetricsCache.TryAdd(srid, metrics);
        return metrics;
    }

    private static bool IsLikelyGeographicSrid(int srid)
        => srid is 4326 or 4269 or 4267 or (>= 4000 and <= 4999);
}
