// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Xunit.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Executes the analytics SQL against PostGIS and checks ground distances.
/// A 1,000 m pair at 60°N must buffer at 1,000 m and not at 600 m. Longitudes
/// on either side of the antimeridian must cluster on the short arc. Points at
/// the edge of an extent wider than one azimuthal plane must stay within 1%
/// of the spheroid.
/// </summary>
[Collection("Database")]
public sealed class FeatureQueryBuilderGroundDistanceIntegrationTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const string EmptyJson = "{}";
    [Fact]
    public async Task Buffer_Points1000MetresApartAt60N_UsesGroundRadius()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("GroundBuffer60");
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, geometry geometry, attributes jsonb);
                INSERT INTO {schema}.features (objectid, layer_id, geometry, attributes)
                VALUES
                    (1, 1, ST_SetSRID(ST_MakePoint(0, 60), 4326), '{EmptyJson}'),
                    (2, 1, ST_Project(ST_SetSRID(ST_MakePoint(0, 60), 4326)::geography, 1000, radians(90))::geometry, '{EmptyJson}');
                """);

            var separation = await ScalarAsync(
                $"SELECT ST_Distance(a.geometry::geography, b.geometry::geography) FROM {schema}.features a, {schema}.features b WHERE a.objectid = 1 AND b.objectid = 2");
            separation.Should().BeApproximately(1000d, 0.05);

            var at1000 = await BufferRadiiAsync(schema, distanceMetres: 1000d);
            var at600 = await BufferRadiiAsync(schema, distanceMetres: 600d);
            output.WriteLine($"60N buffer radii at 1000 m request: {string.Join(", ", at1000.Select(row => row.Radius.ToString("F2", CultureInfo.InvariantCulture)))}");
            output.WriteLine($"60N buffer radii at 600 m request: {string.Join(", ", at600.Select(row => row.Radius.ToString("F2", CultureInfo.InvariantCulture)))}");
            output.WriteLine($"gap from the other point at 1000 m: {string.Join(", ", at1000.Select(row => row.GapToOther.ToString("F2", CultureInfo.InvariantCulture)))}");
            output.WriteLine($"gap from the other point at 600 m: {string.Join(", ", at600.Select(row => row.GapToOther.ToString("F2", CultureInfo.InvariantCulture)))}");

            at1000.Should().HaveCount(2);
            at600.Should().HaveCount(2);
            at1000.Should().OnlyContain(row => Math.Abs(row.Radius - 1000d) < 10d,
                "a 1,000 m geographic buffer is ground metres, not the Web Mercator half-radius (~500 m) at 60°N");
            at600.Should().OnlyContain(row => Math.Abs(row.Radius - 600d) < 6d);
            at1000.Should().OnlyContain(row => row.GapToOther < 15d,
                "the mate 1,000 m away is on the 1,000 m buffer");
            at600.Should().OnlyContain(row => row.GapToOther > 300d,
                "a 600 m buffer does not reach a point 1,000 m away");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData(1500d, 1)]
    [InlineData(500d, 2)]
    public async Task Cluster_AntimeridianPair_UsesShortArc(double eps, int expectedClusters)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("GroundDateline");
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, geometry geometry, attributes jsonb);
                INSERT INTO {schema}.features (objectid, layer_id, geometry, attributes)
                VALUES
                    (1, 1, ST_SetSRID(ST_MakePoint(179.995, 0), 4326), '{EmptyJson}'),
                    (2, 1, ST_Project(ST_SetSRID(ST_MakePoint(179.995, 0), 4326)::geography, 1000, radians(90))::geometry, '{EmptyJson}');
                """);
            var longitudes = await ReadDoublesAsync(
                $"SELECT ST_X(geometry) FROM {schema}.features ORDER BY objectid");
            var separation = await ScalarAsync(
                $"SELECT ST_Distance(a.geometry::geography, b.geometry::geography) FROM {schema}.features a, {schema}.features b WHERE a.objectid = 1 AND b.objectid = 2");
            output.WriteLine($"dateline longitudes {longitudes[0]:G17}, {longitudes[1]:G17}; separation {separation:F3} m");
            longitudes[0].Should().BeGreaterThan(179d);
            longitudes[1].Should().BeLessThan(-179d);
            separation.Should().BeApproximately(1000d, 0.05);

            var clusters = await ClusterIdsAsync(schema, eps);
            clusters.Values.Distinct().Count().Should().Be(expectedClusters);
            clusters.Values.Should().OnlyContain(id => id.HasValue);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task ClusterAndDensity_WideExtentEdge_StayWithinOnePercent()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("GroundWideEdge");
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, geometry geometry, attributes jsonb);
                INSERT INTO {schema}.features (objectid, layer_id, geometry, attributes)
                VALUES
                    (1, 1, ST_SetSRID(ST_MakePoint(-30, 60), 4326), '{EmptyJson}'),
                    (2, 1, ST_SetSRID(ST_MakePoint(30, 60), 4326), '{EmptyJson}'),
                    (3, 1, ST_Project(ST_SetSRID(ST_MakePoint(30, 60), 4326)::geography, 1000, radians(90))::geometry, '{EmptyJson}');
                """);
            var separation = await ScalarAsync(
                $"SELECT ST_Distance(a.geometry::geography, b.geometry::geography) FROM {schema}.features a, {schema}.features b WHERE a.objectid = 2 AND b.objectid = 3");
            separation.Should().BeApproximately(1000d, 0.05);
            var width = await ScalarAsync(
                $"SELECT ST_Distance(a.geometry::geography, b.geometry::geography) FROM {schema}.features a, {schema}.features b WHERE a.objectid = 1 AND b.objectid = 2");
            width.Should().BeGreaterThan(1_500_000d);
            output.WriteLine($"wide extent {width:F0} m; edge pair {separation:F3} m");

            var close = await ClusterIdsAsync(schema, eps: 1010d);
            var far = await ClusterIdsAsync(schema, eps: 990d);
            close[2].Should().Be(close[3], "edge points 1,000 m apart cluster when eps is 1% above the spheroidal distance");
            close[1].Should().NotBe(close[2]);
            far[2].Should().NotBe(far[3], "the same points stay apart when eps is 1% below");

            var perimeters = await DensityPerimetersAsync(schema, cellSizeMetres: 10_000d);
            output.WriteLine($"cell perimeters: {string.Join(", ", perimeters.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)))}");
            perimeters.Should().NotBeEmpty();
            var featureCount = await DensityFeatureCountAsync(schema);
            featureCount.Should().Be(3d);
            perimeters.Should().OnlyContain(perimeter => Math.Abs(perimeter - 40_000d) / 40_000d < 0.01,
                "a 10 km cell at the edge of a wide extent stays within 1% of the spheroid, not the Web Mercator scale");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<IReadOnlyList<(long ObjectId, double Radius, double GapToOther)>> BufferRadiiAsync(
        string schema, double distanceMetres)
    {
        var built = CreateBuilder(schema).BuildBufferAggregateQuery(
            1,
            new FeatureQuery { SpatialReferenceSrid = 4326 },
            new BufferAggregateQuery
            {
                Distance = distanceMetres,
                Unit = DistanceUnit.Meters,
                Dissolve = false,
                MaxInputFeatures = 10
            });
        var sql = $"""
            SELECT b."objectId",
                (SELECT max(ST_Distance(
                    p.geometry::geography,
                    ST_SetSRID((dp).geom, 4326)::geography))
                 FROM ST_DumpPoints(ST_GeomFromGeoJSON(b."bufferGeometry")) AS dp) AS radius_m,
                ST_Distance(
                    ST_GeomFromGeoJSON(b."bufferGeometry")::geography,
                    o.geometry::geography) AS gap_m
            FROM ({built.Sql}) b
            JOIN {schema}.features p ON p.objectid = b."objectId"
            JOIN {schema}.features o ON o.objectid = CASE WHEN b."objectId" = 1 THEN 2 ELSE 1 END
            ORDER BY 1
            """;
        var rows = new List<(long, double, double)>();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(1);
        foreach (var parameter in built.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2)));
        }

        return rows;
    }

    private async Task<Dictionary<long, long?>> ClusterIdsAsync(string schema, double eps)
    {
        var built = CreateBuilder(schema).BuildClusterQuery(
            1,
            new FeatureQuery { SpatialReferenceSrid = 4326 },
            new ClusterQuery
            {
                Algorithm = ClusterAlgorithm.DbScan,
                Eps = eps,
                MinPoints = 1,
                DistanceUnit = DistanceUnit.Meters,
                ReturnHullPerCluster = false,
                MaxInputFeatures = 10
            });
        var rows = new Dictionary<long, long?>();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = built.Sql;
        command.Parameters.AddWithValue(1);
        foreach (var parameter in built.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        output.WriteLine($"eps {eps}: {string.Join(", ", rows.Select(pair => pair.Key + "->" + pair.Value))}");
        return rows;
    }

    private async Task<List<double>> DensityPerimetersAsync(string schema, double cellSizeMetres)
    {
        var built = DensityQuery(schema, cellSizeMetres);
        var sql = $"""
            SELECT ST_Length(ST_ExteriorRing(ST_GeomFromGeoJSON("cellGeometry"))::geography)
            FROM ({built.Sql}) cells
            """;
        return await ReadDoublesAsync(sql, built.WhereParameters);
    }

    private async Task<double> DensityFeatureCountAsync(string schema)
    {
        var built = DensityQuery(schema, 10_000d);
        return await ScalarAsync(
            $"SELECT COALESCE(sum(\"featureCount\"), 0)::double precision FROM ({built.Sql}) cells",
            built.WhereParameters);
    }

    private ParameterizedQuery DensityQuery(string schema, double cellSizeMetres)
        => CreateBuilder(schema).BuildDensityQuery(
            1,
            new FeatureQuery { SpatialReferenceSrid = 4326 },
            new DensityQuery
            {
                Mode = DensityBinningMode.SquareGrid,
                CellSizeMeters = cellSizeMetres,
                MaxInputFeatures = 10,
                MaxCells = 20
            });

    private static FeatureQueryBuilder CreateBuilder(string schema)
    {
        var pool = new DefaultObjectPoolProvider().Create(
            new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
    }

    private async Task<double> ScalarAsync(string sql, IReadOnlyList<object>? parameters = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters != null)
        {
            command.Parameters.AddWithValue(1);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }
        }

        var value = await command.ExecuteScalarAsync();
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private async Task<List<double>> ReadDoublesAsync(string sql, IReadOnlyList<object>? parameters = null)
    {
        var values = new List<double>();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters != null)
        {
            command.Parameters.AddWithValue(1);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetDouble(0));
        }

        return values;
    }
}
