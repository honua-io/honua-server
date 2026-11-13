// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class CollaborationRealtimeTriggerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [Fact]
    public async Task Migration_IsAdditiveAndAcceptedByProductionSafetyClassifier()
    {
        const string resource = "Honua.Server.Migrations.161_PublishSavedMapOperationsToRealtime.sql";
        await using var stream = typeof(Program).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();

        MigrationSafetyClassifier.Classify(resource, sql).Classification.Should()
            .Be(MigrationSafetyClassification.Expand);
    }

    [IntegrationTest]
    public async Task InsertOperation_Commit_PublishesOnlyTheCommittedReference()
    {
        await using var harness = await CreateHarnessAsync();
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertOperationAsync(harness, connection);

        (await harness.ReadSequencesAsync("collab-ops")).Should().BeEmpty("uncommitted edits must remain invisible to another task");
        await transaction.CommitAsync();

        (await harness.ReadSequencesAsync("collab-ops")).Should().Equal(1L);
        await using var command = harness.DataSource.CreateCommand(
            $"SELECT scope_key, kind, payload::text FROM {harness.Table("realtime_events")} WHERE family = 'collab-ops'");
        await using var rows = await command.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        rows.GetString(0).Should().Be("map-a");
        rows.GetString(1).Should().Be("operation-appended");
        using var payload = JsonDocument.Parse(rows.GetString(2));
        var reference = payload.RootElement;
        reference.GetProperty("operationId").GetString().Should().Be("operation-a");
        reference.GetProperty("mapId").GetString().Should().Be("map-a");
        reference.GetProperty("serverCursor").GetInt64().Should().Be(1);
        reference.GetProperty("baseCursor").GetInt64().Should().Be(0);
        reference.GetProperty("actorId").GetString().Should().Be("actor-a");
        reference.GetProperty("kind").GetString().Should().Be("set-viewport");
        reference.EnumerateObject().Should().HaveCount(6, "the event references the operation log without copying the edit body");
    }

    [IntegrationTest]
    public async Task InsertOperation_Rollback_PublishesNothingAndDoesNotAdvanceTheFamilyHead()
    {
        await using var harness = await CreateHarnessAsync();
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertOperationAsync(harness, connection);
        await transaction.RollbackAsync();

        (await harness.ReadSequencesAsync("collab-ops")).Should().BeEmpty();
        (await harness.ScalarAsync<long>($"SELECT head FROM {harness.Table("realtime_log_heads")} WHERE family = 'collab-ops'"))
            .Should().Be(0);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("saved_map_operations")}"))
            .Should().Be(0);
    }

    [IntegrationTest]
    public async Task InsertOperation_DuplicateOrUpdate_DoesNotPublishAnotherEdit()
    {
        await using var harness = await CreateHarnessAsync();
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await InsertOperationAsync(harness, connection);
        await InsertOperationAsync(harness, connection);
        await harness.ExecuteAsync($"UPDATE {harness.Table("saved_map_operations")} SET payload = '{{\"changed\":true}}'::jsonb");

        (await harness.ReadSequencesAsync("collab-ops")).Should().Equal(1L);
    }

    private async Task<RealtimeLogHarness> CreateHarnessAsync()
    {
        var harness = await RealtimeLogHarness.CreateAsync(database);
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.092_CreateSavedMapOperationLog.sql");
        await harness.ApplyMigrationAsync("Honua.Server.Migrations.161_PublishSavedMapOperationsToRealtime.sql");
        return harness;
    }

    private static async Task InsertOperationAsync(RealtimeLogHarness harness, NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {harness.Table("saved_map_operation_log_heads")} (map_id, head_cursor)
            VALUES ('map-a', 1) ON CONFLICT DO NOTHING;
            INSERT INTO {harness.Table("saved_map_operations")}
                (map_id, server_cursor, operation_id, actor_id, base_cursor, operation_kind, payload, accepted_at)
            VALUES ('map-a', 1, 'operation-a', 'actor-a', 0, 'set-viewport', jsonb_build_object('secret', 'edit-body'), now())
            ON CONFLICT DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
