// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    private readonly IDatabaseConnectionProvider _connectionProvider = connectionProvider
        ?? throw new ArgumentNullException(nameof(connectionProvider));

    public async Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        if (geodesic)
        {
            // Transform to WGS84 before geography cast; PostGIS geography only supports SRID 4326.
            // Wrap result with ST_SetSRID to ensure the output geometry has SRID 4326 set explicitly.
            cmd.CommandText = "SELECT ST_AsBinary(ST_SetSRID(ST_Buffer(ST_Transform(ST_SetSRID($1::geometry, $2), 4326)::geography, $3)::geometry, 4326))";
        }
        else
        {
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
}
