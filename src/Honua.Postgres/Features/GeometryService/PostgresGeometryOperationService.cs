// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data.Common;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
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

    // Web Mercator (EPSG:3857) is only well-defined within roughly ±85.0511° latitude;
    // outside this band the planar distortion is severe enough to produce wildly wrong
    // buffer geometries. Planar buffers over geographic CRS route through 3857, so we
    // refuse when the input geometry extends past this limit.
    private const double WebMercatorMaxAbsoluteLatitudeDegrees = 85.0511287798066;

    public async Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        var crsMetrics = await GetCrsMetricsAsync(connection, srid, ct).ConfigureAwait(false);

        if (!geodesic && crsMetrics.IsGeographic)
        {
            var latitudeBounds = await GetGeographicLatitudeBoundsAsync(connection, wkb, srid, ct).ConfigureAwait(false);
            if (latitudeBounds is { } bounds
                && (Math.Abs(bounds.MinY) > WebMercatorMaxAbsoluteLatitudeDegrees
                    || Math.Abs(bounds.MaxY) > WebMercatorMaxAbsoluteLatitudeDegrees))
            {
                throw new ArgumentException(
                    $"Planar buffer on geographic CRS EPSG:{srid} is only accurate within "
                    + $"±{WebMercatorMaxAbsoluteLatitudeDegrees:F4}° latitude. Input extends to "
                    + $"[{bounds.MinY:F4}°, {bounds.MaxY:F4}°]. Use geodesic=true for polar geometries.");
            }
        }

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

    private readonly record struct LatitudeBounds(double MinY, double MaxY);

    private static async Task<LatitudeBounds?> GetGeographicLatitudeBoundsAsync(
        DbConnection connection, byte[] wkb, int srid, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ST_YMin(g), ST_YMax(g) FROM (SELECT ST_SetSRID($1::geometry, $2) AS g) t";
        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = srid });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        return new LatitudeBounds(reader.GetDouble(0), reader.GetDouble(1));
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

        if (fromSrid == 4326 && IsWebMercatorSrid(toSrid))
        {
            wkb = ClampWebMercatorLatitudes(wkb, fromSrid);
        }

        cmd.CommandText = "SELECT ST_AsBinary(ST_Transform(ST_SetSRID($1::geometry, $2), $3))";

        cmd.Parameters.Add(new NpgsqlParameter { Value = wkb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fromSrid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = toSrid });

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as byte[] ?? throw new InvalidOperationException("PostGIS project returned null.");
    }

    private static bool IsWebMercatorSrid(int srid)
        => srid is 3857 or 900913 or 102100 or 102113 or 3785;

    private static byte[] ClampWebMercatorLatitudes(byte[] wkb, int srid)
    {
        var geometry = new WKBReader().Read(wkb);
        var filter = new WebMercatorLatitudeClampFilter();
        geometry.Apply(filter);
        if (!filter.GeometryChanged)
        {
            return wkb;
        }

        geometry.SRID = srid;
        return new WKBWriter().Write(geometry);
    }

    private sealed class WebMercatorLatitudeClampFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged { get; private set; }

        public void Filter(CoordinateSequence seq, int i)
        {
            var latitude = seq.GetY(i);
            var clamped = Math.Clamp(
                latitude,
                -WebMercatorMaxAbsoluteLatitudeDegrees,
                WebMercatorMaxAbsoluteLatitudeDegrees);
            if (clamped.Equals(latitude))
            {
                return;
            }

            seq.SetOrdinate(i, Ordinate.Y, clamped);
            GeometryChanged = true;
        }
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
        // Geographic CRS detection must be authoritative (WKT prefix or +proj=longlat).
        // The prior 4000–4999 range rule misclassified projected and geocentric CRS that
        // fall in that range (notably EPSG:4978 WGS84 geocentric 3D Cartesian).
        cmd.CommandText = """
            SELECT
                CASE
                    WHEN upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEOGCS[%'
                        OR upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEOGRAPHICCRS[%'
                        OR upper(ltrim(COALESCE(srtext, ''))) LIKE 'GEODCRS[%'
                        OR COALESCE(proj4text, '') ILIKE '%+proj=longlat%'
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

    // Fallback used only when spatial_ref_sys has no row for the SRID.
    // Restricted to the well-known, unambiguously geographic codes; the prior range-rule
    // (4000–4999) swept in EPSG:4978 (geocentric) and assorted projected variants.
    private static bool IsLikelyGeographicSrid(int srid)
        => srid is 4326 or 4269 or 4267 or 4258 or 4619 or 4283;
}
