// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Db.MySql;
using Honua.Db.MySql.Features.Infrastructure;
using Honua.TestKit.Attributes;
using MySqlConnector;
using Testcontainers.MySql;

namespace Honua.Db.MySql.Tests;

/// <summary>
/// Proves MySQL 8 <c>ST_Distance</c> on EPSG:4326 agrees with the WGS 84 spheroid.
/// The oracle lengths are the ImageServer Vincenty inverse for the same point pairs.
/// </summary>
[Trait("Category", "MySql")]
public sealed class MySqlGeodesicDistanceIntegrationTests
{
    private const string TestMySqlEnvVar = "HONUA_TEST_MYSQL";

    // ImageServer Wgs84Vincenty: (0,60) -> (1,60) and (0,60) -> (0,60.01), metres.
    private const double OneDegreeLongitudeAt60NMeters = 55_799.499891;
    private const double HundredthDegreeMeridianAt60NMeters = 1_114.123724;
    private const double RelativeTolerance = 0.0005;

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task StDistance_Wgs84PointPairs_AgreesWithSpheroidWithinPointZeroFivePercent()
    {
        await using var container = new MySqlBuilder()
            .WithImage("mysql:8.0.36")
            .WithDatabase("honua_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await container.StartAsync();

        await using var dataSource = new MySqlDataSourceBuilder(container.GetConnectionString()).Build();
        await using var connection = await dataSource.OpenConnectionAsync();

        var parallel = await MeasureAsync(connection, "POINT(0 60)", "POINT(1 60)");
        var meridian = await MeasureAsync(connection, "POINT(0 60)", "POINT(0 60.01)");

        AssertMetres(parallel, OneDegreeLongitudeAt60NMeters);
        AssertMetres(meridian, HundredthDegreeMeridianAt60NMeters);
    }

    private static async Task<double> MeasureAsync(MySqlConnection connection, string leftWkt, string rightWkt)
    {
        var left = $"ST_GeomFromText('{leftWkt}', 4326, 'axis-order=long-lat')";
        var right = $"ST_GeomFromText('{rightWkt}', 4326, 'axis-order=long-lat')";
        var sql = "SELECT " + MySqlSpatialSql.GeodesicDistance(left, right, srid: 4326, MySqlEngineFlavor.Mysql);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static void AssertMetres(double actual, double expected)
    {
        Assert.True(actual > 1000, $"Expected metres, got {actual}.");
        var relative = Math.Abs(actual - expected) / expected;
        Assert.True(
            relative <= RelativeTolerance,
            $"ST_Distance {actual} differs from the WGS 84 spheroid {expected} by {relative:P4}.");
    }
}
