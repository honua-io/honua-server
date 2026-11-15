// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresOperationEvidenceProtocolTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaInternalAsync("operation_evidence", applySeed: false);
        await fixture.ExecuteAsync($$"""
            CREATE SEQUENCE "{{_schema}}".generations CACHE 1 NO CYCLE;
            CREATE TABLE "{{_schema}}".features (
                objectid bigint PRIMARY KEY, layer_id integer NOT NULL,
                branch text NOT NULL, value integer NOT NULL);
            CREATE TABLE "{{_schema}}".changes (generation bigint PRIMARY KEY);
            CREATE TABLE "{{_schema}}".receipts (operation_id text PRIMARY KEY, evidence jsonb NOT NULL);
            CREATE FUNCTION "{{_schema}}".record_evidence() RETURNS trigger LANGUAGE plpgsql AS $body$
            DECLARE
                changed record;
                generation bigint;
                evidence jsonb;
            BEGIN
                changed := CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                PERFORM pg_current_xact_id();
                generation := nextval('"{{_schema}}".generations');
                INSERT INTO "{{_schema}}".changes VALUES (generation);
                evidence := COALESCE(NULLIF(current_setting('honua.operation_evidence', true), ''), '[]')::jsonb;
                PERFORM set_config('honua.operation_evidence', (evidence || jsonb_build_array(
                    jsonb_build_object('generation', generation, 'objectid', changed.objectid,
                        'layer', changed.layer_id, 'branch', changed.branch, 'operation', TG_OP)))::text, true);
                RETURN NULL;
            END;
            $body$;
            CREATE TRIGGER evidence AFTER INSERT OR UPDATE OR DELETE ON "{{_schema}}".features
                FOR EACH ROW EXECUTE FUNCTION "{{_schema}}".record_evidence();
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTest]
    public async Task OperationEvidence_RepeatedObjectAndConcurrentWriter_RetainsOnlyOwnOperation()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var other = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
        (await GenerationsAsync(connection)).Should().Equal(1);
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"UPDATE \"{_schema}\".features SET value = 2 WHERE objectid = 1");
        await ExecuteAsync(other, $"INSERT INTO \"{_schema}\".features VALUES (2, 8, 'branch-b', 1)");

        (await GenerationsAsync(connection)).Should().Equal(2);
        (await ScalarAsync(connection, $"SELECT MAX(generation) FROM \"{_schema}\".changes"))
            .Should().Be(3L);
        (await ScalarAsync(connection, """
            SELECT current_setting('honua.operation_evidence')::jsonb @>
                '[{"generation":2,"objectid":1,"layer":7,"branch":"branch-a","operation":"UPDATE"}]'::jsonb
            """)).Should().Be(true);
        await transaction.CommitAsync();
    }

    [IntegrationTest]
    public async Task OperationEvidence_SavepointRollbackAndMissingRow_ExcludeDiscardedAndPreviousWrites()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
        await transaction.SaveAsync("operation");
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"UPDATE \"{_schema}\".features SET value = 2 WHERE objectid = 1");
        (await GenerationsAsync(connection)).Should().Equal(2);
        await transaction.RollbackAsync("operation");

        (await GenerationsAsync(connection)).Should().Equal(1);
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"UPDATE \"{_schema}\".features SET value = 3 WHERE objectid = 99");
        (await GenerationsAsync(connection)).Should().BeEmpty();
        await transaction.CommitAsync();
        (await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{_schema}\".changes"))
            .Should().Be(1L);
    }

    [IntegrationTest]
    public async Task OperationEvidence_MultipleRowsAndDelete_PreservesEveryProducedGeneration()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1), (2, 7, 'branch-a', 1)");
        (await GenerationsAsync(connection)).Should().Equal(1, 2);
        await ResetAsync(connection);
        await ExecuteAsync(connection, $"DELETE FROM \"{_schema}\".features WHERE objectid = 1");
        (await GenerationsAsync(connection)).Should().Equal(3);
        (await ScalarAsync(connection, """
            SELECT current_setting('honua.operation_evidence')::jsonb @>
                '[{"generation":3,"objectid":1,"layer":7,"branch":"branch-a","operation":"DELETE"}]'::jsonb
            """)).Should().Be(true);
        await transaction.CommitAsync();
    }

    [IntegrationTest]
    public async Task OperationEvidence_TransactionRollback_ClearsEvidenceAndChanges()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ResetAsync(connection);
            await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
            (await GenerationsAsync(connection)).Should().Equal(1);
            await transaction.RollbackAsync();
        }

        (await GenerationsAsync(connection)).Should().BeEmpty();
        (await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{_schema}\".changes"))
            .Should().Be(0L);
    }

    [IntegrationTest]
    public async Task OperationEvidence_Commit_ClearsTransientEvidenceDespiteDurableChanges()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ResetAsync(connection);
            await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
            (await GenerationsAsync(connection)).Should().Equal(1);
            await transaction.CommitAsync();
        }

        (await GenerationsAsync(connection)).Should().BeEmpty();
        (await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{_schema}\".changes"))
            .Should().Be(1L);
    }

    [IntegrationTest]
    public async Task OperationEvidence_ReceiptCommittedWithMutation_ReplaysFromAnotherConnection()
    {
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ResetAsync(connection);
            await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
            await StoreReceiptAsync(connection);
            await transaction.CommitAsync();
            (await GenerationsAsync(connection)).Should().BeEmpty();
        }

        await using var replay = await fixture.DataSource.OpenConnectionAsync();
        (await ScalarAsync(replay, $$"""
            SELECT evidence @> '[{"generation":1,"objectid":1,"layer":7,"branch":"branch-a","operation":"INSERT"}]'::jsonb
            FROM "{{_schema}}".receipts WHERE operation_id = 'upload-1:operation-0'
            """)).Should().Be(true);
        (await ScalarAsync(replay, $"SELECT COUNT(*) FROM \"{_schema}\".changes"))
            .Should().Be(1L);
    }

    [IntegrationTest]
    public async Task OperationEvidence_ReceiptRolledBackWithMutation_CannotReplayDiscardedEvidence()
    {
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ResetAsync(connection);
            await ExecuteAsync(connection, $"INSERT INTO \"{_schema}\".features VALUES (1, 7, 'branch-a', 1)");
            await StoreReceiptAsync(connection);
            await transaction.RollbackAsync();
        }

        await using var replay = await fixture.DataSource.OpenConnectionAsync();
        (await ScalarAsync(replay, $"SELECT COUNT(*) FROM \"{_schema}\".receipts"))
            .Should().Be(0L);
        (await ScalarAsync(replay, $"SELECT COUNT(*) FROM \"{_schema}\".changes"))
            .Should().Be(0L);
    }

    private Task StoreReceiptAsync(NpgsqlConnection connection) =>
        ExecuteAsync(connection, $$"""
            INSERT INTO "{{_schema}}".receipts
            SELECT 'upload-1:operation-0', current_setting('honua.operation_evidence')::jsonb
            """);

    private static Task ResetAsync(NpgsqlConnection connection) =>
        ExecuteAsync(connection, "SELECT set_config('honua.operation_evidence', '[]', true)");

    private static async Task<long[]> GenerationsAsync(NpgsqlConnection connection) =>
        (long[])(await ScalarAsync(connection, """
            SELECT COALESCE(array_agg((entry->>'generation')::bigint ORDER BY ordinal), ARRAY[]::bigint[])
            FROM jsonb_array_elements(COALESCE(NULLIF(current_setting('honua.operation_evidence', true), ''), '[]')::jsonb)
                WITH ORDINALITY AS evidence(entry, ordinal)
            """))!;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
