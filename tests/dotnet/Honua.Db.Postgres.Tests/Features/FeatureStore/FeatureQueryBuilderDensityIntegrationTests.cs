// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class FeatureQueryBuilderDensityIntegrationTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(DensityBinningMode.SquareGrid, false)]
    [InlineData(DensityBinningMode.SquareGrid, true)]
    [InlineData(DensityBinningMode.HexGrid, false)]
    [InlineData(DensityBinningMode.HexGrid, true)]
    public async Task BuildDensityQuery_BoundariesAndCoincidentFeatures_ContributeExactlyOnce(
        DensityBinningMode mode, bool weighted)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("DensityBoundary");
        try
        {
            var gridFunction = mode == DensityBinningMode.HexGrid ? "ST_HexagonGrid" : "ST_SquareGrid";
            // Use the actual grid's vertex, edge midpoint and interior. The two
            // distant anchors keep every neighboring cell inside the data envelope.
            // Different object IDs at the same vertex must retain both weights.
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, geometry geometry,
                    attributes jsonb);
                WITH cell AS (
                    SELECT geom FROM {{gridFunction}}(1000, ST_MakeEnvelope(-3000, -3000, 3000, 3000, 3857))
                    WHERE i = 0 AND j = 0
                ), locations AS (
                    SELECT ST_PointN(ST_ExteriorRing(geom), 1) AS vertex,
                        ST_LineInterpolatePoint(ST_MakeLine(
                            ST_PointN(ST_ExteriorRing(geom), 1),
                            ST_PointN(ST_ExteriorRing(geom), 2)), 0.5) AS edge,
                        ST_Centroid(geom) AS interior
                    FROM cell
                )
                INSERT INTO {{schema}}.features
                SELECT v.id, 1, v.geom, jsonb_build_object('mass', v.weight)
                FROM locations, LATERAL (VALUES
                    (1, vertex, 2), (2, vertex, 3), (3, edge, 5),
                    (4, interior, 7), (5, ST_Buffer(interior, 10), 11),
                    (6, ST_SetSRID(ST_MakePoint(-3000, -3000), 3857), 13),
                    (7, ST_SetSRID(ST_MakePoint(3000, 3000), 3857), 17)
                ) v(id, geom, weight);
                """);

            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildDensityQuery(1, new FeatureQuery { SpatialReferenceSrid = 3857 },
                new DensityQuery
                {
                    Mode = mode,
                    CellSizeMeters = 1000,
                    WeightField = weighted ? "mass" : null,
                    MaxCells = 100,
                    MaxInputFeatures = 100
                });

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.Parameters.AddWithValue(1);
            foreach (var parameter in query.WhereParameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync();
            long featureCount = 0;
            double totalWeight = 0;
            var counts = new List<long>();
            var weights = new List<double>();
            while (await reader.ReadAsync())
            {
                reader.GetInt64(0).Should().Be(7);
                var count = reader.GetInt64(2);
                featureCount += count;
                counts.Add(count);
                if (weighted)
                {
                    var weight = reader.GetDouble(3);
                    totalWeight += weight;
                    weights.Add(weight);
                }
            }

            featureCount.Should().Be(7, "each source row contributes to exactly one cell");
            counts.Should().Contain(count => count >= 2, "coincident rows are distinct contributions");
            if (weighted)
            {
                totalWeight.Should().Be(58);
                weights.Should().Contain(weight => weight >= 18, "the interior point and polygon centroid share their cell");
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
