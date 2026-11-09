// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Operation(Operations.Create)]
    public async Task SnapshotPage_ProjectionFailureOutsideSelectedRows_DoesNotBlockFirstPage(
        bool geometryFailure, bool sourceAliasColumn)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var additionalColumn = sourceAliasColumn ? ", i AS src" : string.Empty;
        await using (var setup = new NpgsqlCommand($"""
            SET LOCAL min_parallel_table_scan_size = 0;
            SET LOCAL parallel_setup_cost = 0;
            SET LOCAL parallel_tuple_cost = 0;
            CREATE TEMP TABLE projection_source ON COMMIT DROP AS
                SELECT i AS id, ST_SetSRID(ST_MakePoint(i % 100, i % 40), 4326) AS geom{additionalColumn}
                FROM generate_series(1, 20000) AS i;
            CREATE TEMP TABLE projection_features (layer_id integer, geometry geometry, attributes jsonb) ON COMMIT DROP;
            ANALYZE projection_source;
            """, connection, transaction))
        {
            await setup.ExecuteNonQueryAsync();
        }
        var geometry = geometryFailure
            ? "CASE WHEN src.id <= 5000 THEN src.geom ELSE ST_Transform(ST_SetSRID(src.geom, 0), 4326) END"
            : "src.geom";
        var attributes = geometryFailure
            ? "jsonb_build_object('id', src.id)"
            : "jsonb_build_object('value', 100 / (src.id - 5001))";
        var sql = PostgreSqlLayerPublishingService.BuildSnapshotPageSql(
            "pg_temp.projection_features", "pg_temp.projection_source", geometry, attributes);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("hasCursor", false);
        command.Parameters.Add("tableOid", NpgsqlDbType.Oid).Value = 0u;
        command.Parameters.AddWithValue("ctid", "(0,0)");
        command.Parameters.AddWithValue("batchSize", 5000);
        command.Parameters.AddWithValue("layerId", 1);
        uint tableOid;
        string cursor;
        await using (var page = await command.ExecuteReaderAsync())
        {
            Assert.True(await page.ReadAsync());
            Assert.Equal(5000, page.GetInt32(0));
            tableOid = checked((uint)page.GetInt64(1));
            cursor = page.GetString(2);
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM pg_temp.projection_features", connection, transaction))
        {
            Assert.Equal(5000L, await count.ExecuteScalarAsync());
        }
        command.Parameters["hasCursor"].Value = true;
        command.Parameters["tableOid"].Value = tableOid;
        command.Parameters["ctid"].Value = cursor;
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
    }
}
