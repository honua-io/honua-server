// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class FeatureQueryBuilderExtentIntegrationTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData(4326)]
    public async Task BuildExtentQuery_ProjectedOutput_TransformsRowsBeforeAggregation(int? sourceSrid)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ProjectedExtent");
        try
        {
            // Fixed UTM bounds provide an independent coordinate oracle. A bbox transform
            // instead of a per-row transform can add corners that were never in the data.
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (layer_id integer, geometry geometry(Point, 4326));
                INSERT INTO {schema}.features VALUES
                    (1, ST_Transform(ST_SetSRID(ST_MakePoint(361431.356, 3736037.969), 26911), 4326)),
                    (1, ST_Transform(ST_SetSRID(ST_MakePoint(483556.921, 3783589.624), 26911), 4326));
                """);
            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildExtentQuery(1, new FeatureQuery
            {
                OutputSrid = 26911,
                SpatialReferenceSrid = sourceSrid
            });
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.Parameters.AddWithValue(1);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetDouble(0).Should().BeApproximately(361431.356, 0.001);
            reader.GetDouble(1).Should().BeApproximately(3736037.969, 0.001);
            reader.GetDouble(2).Should().BeApproximately(483556.921, 0.001);
            reader.GetDouble(3).Should().BeApproximately(3783589.624, 0.001);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
