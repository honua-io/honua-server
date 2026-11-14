// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ObservationEventLogTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Insert_StatementGroupsDatastreamsAndSplitsAtOneThousand()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertAsync(harness, 2001, "tenant-a", null);

        (await harness.ScalarAsync<string>($"SELECT string_agg(scope_key || ':' || jsonb_array_length(payload->'observations'), ',' ORDER BY seq) FROM {harness.Table("realtime_events")}"))
            .Should().Be("1:1000,2:1000,2:1");
        (await harness.ScalarAsync<bool>($"SELECT bool_and(payload->'scope' = jsonb_build_object('tenantId','tenant-a','schema',null)) FROM {harness.Table("realtime_events")}"))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Insert_RollbackRollsBackObservationsAndLogHead()
    {
        await using var harness = await CreateHarnessAsync();
        await using (var connection = await harness.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand($"INSERT INTO {harness.Table("sta_observation")} (id,datastream_id,phenomenon_time,result_json) VALUES (1,1,now(),'42')", connection, transaction);
            await insert.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("sta_observation")}")).Should().Be(0);
        (await harness.ScalarAsync<long>($"SELECT head FROM {harness.Table("realtime_log_heads")} WHERE family='observations'")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Insert_ScopePreservesTenantAndNullVersusNamedSchema()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertAsync(harness, 1, "tenant-a", null);
        await InsertAsync(harness, 1, "tenant-b", null);
        await InsertAsync(harness, 1, "tenant-a", "public");

        (await harness.ScalarAsync<string>($"SELECT string_agg(payload->'scope'->>'tenantId' || ':' || coalesce(payload->'scope'->>'schema','<null>'), ',' ORDER BY seq) FROM {harness.Table("realtime_events")}"))
            .Should().Be("tenant-a:<null>,tenant-b:<null>,tenant-a:public");
    }

    [IntegrationTest]
    public async Task Insert_LargeBatchSplitsBeforePayloadLimitWithoutDroppingRows()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertAsync(harness, 100, null, null, 2000);

        (await harness.ScalarAsync<long>($"SELECT sum(jsonb_array_length(payload->'observations'))::bigint FROM {harness.Table("realtime_events")}"))
            .Should().Be(100);
        (await harness.ScalarAsync<bool>($"SELECT count(*) > 1 AND bool_and(octet_length(payload::text)<=65536) FROM {harness.Table("realtime_events")}"))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Insert_OversizedSingleObservationRejectsWholeStatement()
    {
        await using var harness = await CreateHarnessAsync();
        var insert = () => InsertAsync(harness, 1, null, null, 65536);

        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("54000");
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("sta_observation")}")).Should().Be(0);
        (await harness.ReadSequencesAsync("observations")).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task Migration_ReapplyingDoesNotDuplicateEvents()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.171_CaptureObservationRealtimeEvents.sql");
        await InsertAsync(harness, 1, null, null);

        (await harness.ReadSequencesAsync("observations")).Should().Equal(1);
    }

    [IntegrationTest]
    public async Task Insert_TransactionScopeDoesNotLeakToUnscopedSql()
    {
        await using var harness = await CreateHarnessAsync();
        await InsertAsync(harness, 1, "tenant-a", "named");
        await harness.ExecuteAsync($"INSERT INTO {harness.Table("sta_observation")} (id,datastream_id,phenomenon_time,result_json) VALUES (2,1,now(),'42')");

        (await harness.ScalarAsync<bool>($"SELECT payload->'scope' = jsonb_build_object('tenantId',null,'schema',null) FROM {harness.Table("realtime_events")} ORDER BY seq DESC LIMIT 1"))
            .Should().BeTrue();
    }

    [IntegrationTest]
    public async Task Insert_ScopeForDifferentPhysicalSchemaRejectsWholeStatement()
    {
        await using var harness = await CreateHarnessAsync();
        var insert = () => harness.ExecuteAsync($"""
            BEGIN;
            SELECT set_config('honua.observation_scope',jsonb_build_object('tenantId','tenant-a','schema',null,'tableSchema','wrong')::text,true);
            INSERT INTO {harness.Table("sta_observation")} (id,datastream_id,phenomenon_time,result_json) VALUES (1,1,now(),'42');
            COMMIT;
            """);

        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22023");
        (await harness.ReadSequencesAsync("observations")).Should().BeEmpty();
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("sta_observation")}")).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Insert_EmitsPublicReferencesAndIntervalWithoutLosingResultShape()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.ExecuteAsync($"""
            INSERT INTO {harness.Table("sta_observation")}
                (id,datastream_id,datastream_reference_id,phenomenon_time,phenomenon_time_end,result_time,result_json,feature_of_interest_id,feature_of_interest_reference_id)
            VALUES (7,1,42,'2026-01-01T00:00:00Z','2026-01-01T01:00:00Z','2026-01-01T02:00:00Z',jsonb_build_object('temperature',20),3,99);
            """);

        (await harness.ScalarAsync<string>($"SELECT scope_key FROM {harness.Table("realtime_events")}")).Should().Be("42");
        (await harness.ScalarAsync<bool>($"""
            SELECT payload->'datastreamId' = '42'::jsonb
                AND payload->'observations'->0->'id' = '7'::jsonb
                AND payload->'observations'->0->'featureOfInterestId' = '99'::jsonb
                AND payload->'observations'->0->'result' = jsonb_build_object('temperature',20)
                AND payload->'observations'->0->>'phenomenonTime' = '2026-01-01T00:00:00+00:00/2026-01-01T01:00:00+00:00'
            FROM {harness.Table("realtime_events")}
            """)).Should().BeTrue();
    }

    private async Task<RealtimeLogHarness> CreateHarnessAsync()
    {
        var harness = await RealtimeLogHarness.CreateAsync(database);
        await harness.ExecuteAsync($"""
            CREATE TABLE {harness.Table("sta_observation")} (
                id bigint NOT NULL, datastream_id bigint NOT NULL, datastream_reference_id bigint,
                phenomenon_time timestamptz NOT NULL, phenomenon_time_end timestamptz,
                result_time timestamptz, result_json jsonb, result double precision,
                feature_of_interest_id bigint, feature_of_interest_reference_id bigint
            ) PARTITION BY RANGE (phenomenon_time);
            CREATE TABLE {harness.Table("sta_observation_default")} PARTITION OF {harness.Table("sta_observation")} DEFAULT;
            """);
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.171_CaptureObservationRealtimeEvents.sql");
        return harness;
    }

    private static async Task InsertAsync(RealtimeLogHarness harness, int count, string? tenant, string? schema, int resultSize = 0)
    {
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT set_config('honua.observation_scope',jsonb_build_object('tenantId',@tenant,'schema',@schema,'tableSchema',@table_schema)::text,true);
            INSERT INTO {harness.Table("sta_observation")} (id,datastream_id,phenomenon_time,result_json)
            SELECT n, 1 + n % 2, now(), CASE WHEN @size=0 THEN '42'::jsonb ELSE to_jsonb(repeat('x',@size)) END
            FROM generate_series(1,@count) n;
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Text, (object?)tenant ?? DBNull.Value);
        command.Parameters.AddWithValue("schema", NpgsqlTypes.NpgsqlDbType.Text, (object?)schema ?? DBNull.Value);
        command.Parameters.AddWithValue("table_schema", NpgsqlTypes.NpgsqlDbType.Text, harness.Table("sta_observation").Split('.')[0].Trim('"'));
        command.Parameters.AddWithValue("size", resultSize);
        command.Parameters.AddWithValue("count", count);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
