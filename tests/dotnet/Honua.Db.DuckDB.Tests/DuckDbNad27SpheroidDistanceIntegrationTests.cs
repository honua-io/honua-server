// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using DuckDB.NET.Data;
using Honua.Db.DuckDB.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Db.DuckDB.Tests;

/// <summary>
/// Compares a CONUS NAD27 point pair measured by the shipped DuckDB transform with the
/// same shift on the PostGIS spheroid. Skipped unless <c>HONUA_TEST_POSTGRES=1</c>.
/// </summary>
public sealed class DuckDbNad27SpheroidDistanceIntegrationTests
{
    private const string Left = "POINT(-100 40)";
    private const string Right = "POINT(-99.98 40.02)";

    [RequiredEnvironmentFact("HONUA_TEST_POSTGRES", "1", skipReason: "Set HONUA_TEST_POSTGRES=1 to compare DuckDB NAD27 distance with PostGIS.")]
    public async Task Nad27ConusPair_MatchesPostgresSpheroidWithinOneMetreAfterTheSameShift()
    {
        await using var postgres = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:18-3.6")
            .Build();
        await postgres.StartAsync();

        await using var duck = new DuckDBConnection("Data Source=:memory:");
        await duck.OpenAsync();
        using var bootstrap = new DuckDBSpatialBootstrap(extensionPath: null, NullLogger<DuckDBSpatialBootstrap>.Instance);
        await bootstrap.EnsureSpatialExtensionAsync(duck, CancellationToken.None);

        var shiftedLongitude = await ScalarAsync(
            duck,
            "SELECT ST_X(ST_Transform(ST_GeomFromText('POINT(-100 40)'), 'EPSG:4267', 'EPSG:4326', always_xy := true))");
        Assert.True(
            Math.Abs(shiftedLongitude - (-100d)) > 1e-4,
            $"NAD27 to EPSG:4326 did not move the longitude (got {shiftedLongitude}).");

        var duckDistance = await ScalarAsync(
            duck,
            "SELECT " + DuckDbGeodesicSql.Nad27SpheroidDistance(
                $"ST_GeomFromText('{Left}')",
                $"ST_GeomFromText('{Right}')"));

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using (var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS postgis", connection))
        {
            await extension.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT ST_Distance(
                ST_Transform(ST_SetSRID(ST_MakePoint(-100, 40), 4267), 4326)::geography,
                ST_Transform(ST_SetSRID(ST_MakePoint(-99.98, 40.02), 4267), 4326)::geography)
            """,
            connection);
        var postgresDistance = Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        Assert.True(duckDistance > 1000, $"Expected metres from DuckDB, got {duckDistance}.");
        Assert.True(
            Math.Abs(duckDistance - postgresDistance) < 1d,
            $"DuckDB {duckDistance} m differs from PostGIS {postgresDistance} m by more than a metre.");
    }

    private static async Task<double> ScalarAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToDouble(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
