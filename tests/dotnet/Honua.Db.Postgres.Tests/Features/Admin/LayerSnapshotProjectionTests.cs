// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class LayerSnapshotProjectionTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task BuildSnapshotPageSql_WideSource_SortsOnlyRowIdentitiesBeforeBoundingPage()
    {
        const int batchSize = 100;
        await using var connection = await fixture.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CreateProjectionTablesAsync(connection, transaction);

        var sql = PostgreSqlLayerPublishingService.BuildSnapshotPageSql(
            "pg_temp.projection_target", "pg_temp.projection_source", "NULL::integer",
            "jsonb_build_object('id', src.id, 'payload', src.payload)");
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + sql, connection, transaction);
        command.Parameters.AddWithValue("layerId", 1);
        command.Parameters.AddWithValue("hasCursor", false);
        command.Parameters.AddWithValue("tableOid", NpgsqlDbType.Oid, 0u);
        command.Parameters.AddWithValue("ctid", NpgsqlDbType.Text, DBNull.Value);
        command.Parameters.AddWithValue("batchSize", batchSize);
        using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        var unboundedSorts = Descendants(plan.RootElement[0].GetProperty("Plan"))
            .Where(node => node.GetProperty("Node Type").GetString() == "Sort"
                && node.GetProperty("Plans")[0].GetProperty("Actual Rows").GetDouble() > batchSize)
            .ToArray();
        unboundedSorts.Should().NotBeEmpty("the fixture must exercise sorting more than one page");
        unboundedSorts.Should().OnlyContain(node => node.GetProperty("Plan Width").GetInt32() <= 16,
            "sorting the remaining source must carry only table OIDs and tuple identities, not projected JSON");
        await using var count = new NpgsqlCommand("SELECT count(*) FROM projection_target", connection, transaction);
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(batchSize);
    }

    private static async Task CreateProjectionTablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE projection_source(id integer, payload text) ON COMMIT DROP;
            INSERT INTO projection_source SELECT id, repeat('x', 2000) FROM generate_series(1, 2000) AS id;
            ANALYZE projection_source;
            CREATE TEMP TABLE projection_target(layer_id integer, geometry integer, attributes jsonb) ON COMMIT DROP;
            """, connection, transaction);
        await setup.ExecuteNonQueryAsync();
    }

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children))
        {
            yield break;
        }

        foreach (var descendant in children.EnumerateArray().SelectMany(Descendants))
        {
            yield return descendant;
        }
    }
}
