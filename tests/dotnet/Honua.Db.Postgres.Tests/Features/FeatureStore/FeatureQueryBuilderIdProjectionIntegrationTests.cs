// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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
    public enum QueryPlan
    {
        Standard,
        Optimized
    }

    [IntegrationTheory]
    [InlineData(QueryPlan.Standard)]
    [InlineData(QueryPlan.Optimized)]
    public Task BuildSelectQuery_WithStoredIdAttribute_PreservesValue(QueryPlan plan) =>
        AssertSelectedAttributesAsync(plan, ["secret"], attributes =>
            attributes.GetProperty("id").GetInt32().Should().Be(42));

    [IntegrationTheory]
    [InlineData(QueryPlan.Standard)]
    [InlineData(QueryPlan.Optimized)]
    public Task BuildSelectQuery_WithMaskedIdAttribute_ExcludesValue(QueryPlan plan) =>
        AssertSelectedAttributesAsync(plan, ["id", "secret"], attributes =>
            attributes.TryGetProperty("id", out _).Should().BeFalse());

    private async Task AssertSelectedAttributesAsync(QueryPlan plan, ImmutableArray<string> maskedFields, Action<JsonElement> assertId)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(FeatureQueryBuilderIdProjectionIntegrationTests));
        try
        {
            await SeedFeatureAsync(schema);
            var pool = new DefaultObjectPoolProvider().Create(new StringBuilderPooledObjectPolicy());
            var builder = new FeatureQueryBuilder(pool, new GeometryProcessor(), schema);
            var query = new FeatureQuery { OutFields = ["id", "name", "secret"], EnforcedMaskedFields = maskedFields };
            var result = plan == QueryPlan.Optimized
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
            assertId(attributes.RootElement);
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private Task SeedFeatureAsync(string schema) => fixture.ExecuteAsync($$"""
        CREATE TABLE {{schema}}.features (
            objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
            geometry geometry(Point,4326), attributes jsonb);
        INSERT INTO {{schema}}.features VALUES
            (7, 1, ST_SetSRID(ST_MakePoint(1,2),4326), '{"id":42,"name":"bravo","secret":"hidden"}');
        """);
}
