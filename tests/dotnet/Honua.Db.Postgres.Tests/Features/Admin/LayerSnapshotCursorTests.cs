// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

[Collection("Database")]
public sealed class LayerSnapshotCursorTests(PostgresFixture fixture)
{
    [IntegrationTheory]
    [InlineData(SourceLayout.Heap)]
    [InlineData(SourceLayout.Partitioned)]
    public async Task CopySnapshotPages_AcrossPhysicalBlockDigits_CopiesEverySourceRowExactlyOnce(SourceLayout layout)
    {
        const int rowCount = 500;
        const int batchSize = 100;
        await using var connection = await fixture.GetConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await CreateCursorSourceAsync(connection, transaction, layout);

        var sql = PostgreSqlLayerPublishingService.BuildSnapshotPageSql(
            "pg_temp.cursor_target", "pg_temp.cursor_source", "NULL::integer", "jsonb_build_object('id', src.id)");
        var reports = new List<(long Done, long Total)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var copied = await PostgreSqlLayerPublishingService.CopySnapshotPagesAsync(
            batchSize,
            async (hasCursor, tableOid, ctid, token) =>
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("layerId", 1);
                command.Parameters.AddWithValue("hasCursor", hasCursor);
                command.Parameters.AddWithValue("tableOid", NpgsqlDbType.Oid, tableOid);
                command.Parameters.AddWithValue("ctid", NpgsqlDbType.Text, (object?)ctid ?? DBNull.Value);
                command.Parameters.AddWithValue("batchSize", batchSize);
                await using var reader = await command.ExecuteReaderAsync(token);
                (await reader.ReadAsync(token)).Should().BeTrue();
                return new PostgreSqlLayerPublishingService.SnapshotPage(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : checked((uint)reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            },
            (done, total, _) =>
            {
                reports.Add((done, total));
                return Task.CompletedTask;
            },
            rowCount,
            timeout.Token);

        copied.Should().Be(rowCount);
        reports[^1].Should().Be((500L, 500L));
        reports.Should().OnlyContain(report => report.Done <= rowCount && report.Total == rowCount);
        await using var verify = new NpgsqlCommand("""
            SELECT count(*), count(DISTINCT attributes->>'id'),
                min((attributes->>'id')::integer), max((attributes->>'id')::integer)
            FROM cursor_target;
            """, connection, transaction);
        await using var result = await verify.ExecuteReaderAsync();
        (await result.ReadAsync()).Should().BeTrue();
        result.GetInt64(0).Should().Be(rowCount);
        result.GetInt64(1).Should().Be(rowCount);
        result.GetInt32(2).Should().Be(1);
        result.GetInt32(3).Should().Be(rowCount);
    }

    public enum SourceLayout
    {
        Heap,
        Partitioned
    }

    private static async Task CreateCursorSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SourceLayout layout)
    {
        var partitionClause = layout == SourceLayout.Partitioned ? "PARTITION BY RANGE (id)" : string.Empty;
        var partitionTables = layout == SourceLayout.Partitioned ? """
            CREATE TEMP TABLE cursor_source_low PARTITION OF cursor_source FOR VALUES FROM (MINVALUE) TO (251);
            CREATE TEMP TABLE cursor_source_high PARTITION OF cursor_source FOR VALUES FROM (251) TO (MAXVALUE);
            """ : string.Empty;
        await using var setup = new NpgsqlCommand($"""
            CREATE TEMP TABLE cursor_source(id integer PRIMARY KEY, padding text) {partitionClause} ON COMMIT DROP;
            ALTER TABLE cursor_source ALTER COLUMN padding SET STORAGE PLAIN;
            {partitionTables}
            INSERT INTO cursor_source SELECT id, repeat('x', 1000) FROM generate_series(1, 500) AS id;
            CREATE TEMP TABLE cursor_target(layer_id integer, geometry integer, attributes jsonb) ON COMMIT DROP;
            SELECT EXISTS (SELECT 1 FROM cursor_source WHERE ctid > '(10,0)'::tid);
            """, connection, transaction);
        (await setup.ExecuteScalarAsync()).Should().Be(true,
            "the fixture must cross physical block digits so lexical and native tid ordering differ");
        if (layout == SourceLayout.Partitioned)
        {
            await using var collisions = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM cursor_source GROUP BY ctid HAVING count(DISTINCT tableoid) > 1);
                """, connection, transaction);
            (await collisions.ExecuteScalarAsync()).Should().Be(true,
                "partition rows must share tuple identities to require matching the table OID as well");
        }
    }
}
