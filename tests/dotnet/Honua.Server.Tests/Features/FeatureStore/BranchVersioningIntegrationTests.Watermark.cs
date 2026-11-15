// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task CreateAsync_DefaultWriteInFlight_UsesCommittedMergeBaseAndRetainsLateChange()
    {
        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("committed", 1, 1), CancellationToken.None);
        var tracker = new PostgresChangeTracker(new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema));
        var baseline = await tracker.GetCurrentGenerationAsync();
        await using var writer = await OpenSchemaConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE features SET attributes = '{"name":"late-commit"}'::jsonb WHERE objectid = @id
            """, writer, transaction))
        {
            update.Parameters.AddWithValue("id", feature.Id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var branch = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("InFlightMergeBase", "sde", VersionAccess.Public));
        branch.CommonAncestorGeneration.Should().Be(baseline);
        branch.BranchGeneration.Should().Be(baseline);
        await transaction.CommitAsync();

        var changes = await tracker.GetChangesSinceAsync(branch.CommonAncestorGeneration, [PointsLayerId]);
        changes.Should().Contain(change => change.ObjectId == feature.Id && change.Generation > baseline);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task CreateAsync_RolledBackAllocation_UsesCommittedMergeBase()
    {
        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("committed", 1, 1), CancellationToken.None);
        var tracker = new PostgresChangeTracker(new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema));
        var baseline = await tracker.GetCurrentGenerationAsync();
        await using var writer = await OpenSchemaConnectionAsync();
        await using (var transaction = await writer.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                UPDATE features SET attributes = '{"name":"rolled-back"}'::jsonb WHERE objectid = @id
                """, writer, transaction);
            update.Parameters.AddWithValue("id", feature.Id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
            await transaction.RollbackAsync();
        }

        var branch = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest("RolledBackMergeBase", "sde", VersionAccess.Public));
        branch.CommonAncestorGeneration.Should().Be(baseline);
        branch.BranchGeneration.Should().Be(baseline);
    }

    [IntegrationTest]
    [Operation(Operations.VersionManagement)]
    public async Task Update_OpenAllocation_IsVisibleAsInProgressBeforeCommit()
    {
        await AssertAllocationAssignsTransactionIdBeforeNextvalAsync("honua.track_feature_changes()");
        await AssertAllocationAssignsTransactionIdBeforeNextvalAsync("honua.track_version_edits()");

        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("xid-fence", 1, 1), CancellationToken.None);
        await using var writer = await OpenSchemaConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE features
            SET attributes = '{"name":"in-flight"}'::jsonb
            WHERE objectid = @id
            """, writer, transaction))
        {
            update.Parameters.AddWithValue("id", feature.Id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        long generation;
        string transactionId;
        await using (var allocated = new NpgsqlCommand("""
            SELECT changes.generation, pg_current_xact_id()::text
            FROM honua.feature_changes AS changes
            WHERE changes.layer_id = @layer
              AND changes.objectid = @id
            ORDER BY changes.generation DESC
            LIMIT 1
            """, writer, transaction))
        {
            allocated.Parameters.AddWithValue("layer", PointsLayerId);
            allocated.Parameters.AddWithValue("id", feature.Id);
            await using var reader = await allocated.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            generation = reader.GetInt64(0);
            transactionId = reader.GetString(1);
        }

        await using var observer = await OpenSchemaConnectionAsync();
        await AssertAllocationVisibilityAsync(observer, transactionId, generation, "in progress", visible: false);
        await transaction.CommitAsync();
        await AssertAllocationVisibilityAsync(observer, transactionId, generation, "committed", visible: true);
    }

    private async Task AssertAllocationAssignsTransactionIdBeforeNextvalAsync(string functionName)
    {
        // The assignment has to precede nextval inside the trigger. Observing the transaction id
        // after the statement returns is true of the previous body as well, because the change-row
        // insert assigns one. The definition order is the regression for that window.
        const string sql = """
            SELECT strpos(definition, 'pg_current_xact_id()') AS assigned,
                   strpos(definition, 'pg_advisory_xact_lock(144047712, 0)') AS locked,
                   strpos(definition, 'nextval(''honua.sync_generation'')') AS allocated
            FROM (SELECT pg_get_functiondef(@function::regprocedure) AS definition) AS source
            """;
        await using var connection = await OpenSchemaConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("function", functionName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var assigned = reader.GetInt32(0);
        var locked = reader.GetInt32(1);
        var allocated = reader.GetInt32(2);
        assigned.Should().BeGreaterThan(0, $"{functionName} must assign a transaction id");
        assigned.Should().BeLessThan(locked);
        locked.Should().BeLessThan(allocated);
    }

    private static async Task AssertAllocationVisibilityAsync(
        NpgsqlConnection observer,
        string transactionId,
        long generation,
        string expectedStatus,
        bool visible)
    {
        const string sql = """
            SELECT pg_xact_status(CAST(@transaction_id AS xid8))::text,
                   EXISTS (
                       SELECT 1
                       FROM honua.feature_changes
                       WHERE generation = @generation)
            """;
        await using var command = new NpgsqlCommand(sql, observer);
        command.Parameters.AddWithValue("transaction_id", transactionId);
        command.Parameters.AddWithValue("generation", generation);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(expectedStatus);
        reader.GetBoolean(1).Should().Be(visible);
    }
}
