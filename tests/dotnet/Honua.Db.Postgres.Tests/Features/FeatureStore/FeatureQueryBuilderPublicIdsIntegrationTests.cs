// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class FeatureQueryBuilderPublicIdsIntegrationTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("{}", 41L)]
    [InlineData("{\"id\":null}", 41L)]
    [InlineData("{\"id\":901}", 901L)]
    public async Task PublicObjectIds_MatchFeatureSerializationFallback(string attributes, long expectedId)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("PublicIds");
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE {schema}.features (
                    objectid bigint PRIMARY KEY, layer_id integer, attributes jsonb);
                """);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {schema}.features VALUES (41, 1, $1::jsonb)";
                insert.Parameters.AddWithValue(attributes);
                await insert.ExecuteNonQueryAsync();
            }

            var pool = new DefaultObjectPoolProvider().Create(
                new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = builder.BuildObjectIdsQuery(1, new FeatureQuery
            {
                PublicIdAttributeName = "id",
                ReturnPublicObjectIds = true
            });
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.Parameters.AddWithValue(1);
            foreach (var parameter in query.WhereParameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(expectedId);
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
