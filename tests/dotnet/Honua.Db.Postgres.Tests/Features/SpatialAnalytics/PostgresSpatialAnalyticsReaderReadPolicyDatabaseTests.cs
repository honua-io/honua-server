// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit;
using static Honua.Db.Postgres.Tests.Features.SpatialAnalytics.PostgresSpatialAnalyticsReaderReadPolicyTests;

namespace Honua.Db.Postgres.Tests.Features.SpatialAnalytics;

/// <summary>
/// Executes the policy-carrying analytics SQL against PostGIS, so the row filter and
/// field-mask placement is proven by the rows that come back rather than by SQL shape.
/// </summary>
[Collection("Database")]
public sealed class PostgresSpatialAnalyticsReaderReadPolicyDatabaseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task QuerySpatialJoinAsync_WithPoliciesOnBothLayers_ReturnsOnlyVisibleRowsAndFields()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AnalyticsJoinReadPolicy");
        try
        {
            await SeedAsync(schema);
            var harness = new Harness(schema)
                .WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west")
                .WithRowPolicy(JoinLayerId, "attributes->>'owner' = @p0", "team-a")
                .WithMaskedFields(TargetLayerId, "secret");
            var join = Join() with { CarryFields = ["name"] };

            await harness.Reader.QuerySpatialJoinAsync(
                TargetLayerId, new FeatureQuery { SpatialReferenceSrid = 4326 }, join);

            var row = (await ExecuteAsync(harness, TargetLayerId)).Should().ContainSingle().Subject;
            row[0].Should().Be(1L, "only the target row the row policy admits is joined");
            AssertAttributes((string)row[1]);
            row[3].Should().Be(1L, "the join layer's own row policy narrows the matched rows");
            ((string[])row[4]).Should().Equal("visible");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task QueryClustersAsync_PerFeatureModeWithPolicies_ReturnsOnlyVisibleRowsAndFields()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("AnalyticsClusterReadPolicy");
        try
        {
            await SeedAsync(schema);
            var harness = new Harness(schema)
                .WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west")
                .WithMaskedFields(TargetLayerId, "secret");

            await harness.Reader.QueryClustersAsync(
                TargetLayerId, new FeatureQuery { SpatialReferenceSrid = 4326 }, KMeans() with { K = 1 });

            var row = (await ExecuteAsync(harness, TargetLayerId)).Should().ContainSingle().Subject;
            row[0].Should().Be(1L);
            AssertAttributes((string)row[2]);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static void AssertAttributes(string json)
    {
        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("region", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("secret", out _).Should().BeFalse("masked fields are removed");
    }

    private Task SeedAsync(string schema)
        => fixture.ExecuteAsync($$"""
            CREATE TABLE {{schema}}.features (
                objectid bigint, layer_id integer, attributes jsonb, geometry geometry(Geometry, 4326));
            INSERT INTO {{schema}}.features VALUES
                (1, {{TargetLayerId}}, '{"region":"west","secret":"s1"}', ST_GeomFromText('POLYGON((0 0,0 1,1 1,1 0,0 0))', 4326)),
                (2, {{TargetLayerId}}, '{"region":"east","secret":"s2"}', ST_GeomFromText('POLYGON((0 0,0 1,1 1,1 0,0 0))', 4326)),
                (10, {{JoinLayerId}}, '{"owner":"team-a","name":"visible"}', ST_GeomFromText('POINT(0.5 0.5)', 4326)),
                (11, {{JoinLayerId}}, '{"owner":"team-b","name":"hidden"}', ST_GeomFromText('POINT(0.6 0.6)', 4326));
            """);

    // Binds parameters the way the provider's data access does: the layer id is $1 and
    // the builder's parameters follow positionally.
    private async Task<List<object[]>> ExecuteAsync(Harness harness, int layerId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = harness.Sql;
        command.Parameters.AddWithValue(layerId);
        foreach (var parameter in harness.Parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var rows = new List<object[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return rows;
    }
}
