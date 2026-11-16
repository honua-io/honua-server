// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class FeatureQueryBuilderIdProjectionIntegrationTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BuildSelectQuery_WithStoredIdAttribute_PreservesValueAndMasking(bool optimized, bool masked)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(FeatureQueryBuilderIdProjectionIntegrationTests));
        try
        {
            await fixture.ExecuteAsync($$"""
                CREATE TABLE {{schema}}.features (
                    objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
                    geometry geometry(Point,4326), attributes jsonb);
                INSERT INTO {{schema}}.features VALUES
                    (7, 1, ST_SetSRID(ST_MakePoint(1,2),4326), '{"id":42,"name":"bravo","secret":"hidden"}');
                """);
            var pool = new DefaultObjectPoolProvider().Create(new Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = new FeatureQuery
            {
                OutFields = ["id", "name", "secret"],
                EnforcedMaskedFields = masked ? ["id", "secret"] : ["secret"]
            };
            var result = optimized
                ? builder.BuildOptimizedSelectQuery(1, query)
                : builder.BuildSelectQuery(1, query);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = result.Sql;
            command.Parameters.AddWithValue(1);
            foreach (var parameter in result.WhereParameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(7);
            using var attributes = JsonDocument.Parse(reader.GetString(2));
            attributes.RootElement.GetProperty("name").GetString().Should().Be("bravo");
            attributes.RootElement.TryGetProperty("secret", out _).Should().BeFalse();
            if (masked)
            {
                attributes.RootElement.TryGetProperty("id", out _).Should().BeFalse();
            }
            else
            {
                attributes.RootElement.GetProperty("id").GetInt32().Should().Be(42);
            }
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
