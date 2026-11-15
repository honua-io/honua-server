// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

public sealed partial class BranchVersioningIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task OperationEvidence_DefaultTrigger_RecordsExactMutationAndRestoresSavepoint()
    {
        var feature = await CreateFeatureStore().CreateAsync(
            PointsLayerId, BuildFeature("evidence", 1, 1), CancellationToken.None);
        await using var connection = await OpenSchemaConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteEvidenceSqlAsync(connection, """
            SET LOCAL honua.capture_operation_evidence = 'on';
            SET LOCAL honua.operation_evidence = '[]';
            """);
        await UpdateEvidenceFeatureAsync(connection, feature.Id);
        var initial = await ReadOperationEvidenceAsync(connection);
        initial.GetArrayLength().Should().Be(1);
        await AssertEvidenceMatchesChangeAsync(connection, initial[0]);
        initial[0].GetProperty("version_id").ValueKind.Should().Be(JsonValueKind.Null);
        initial[0].GetProperty("operation").GetInt32().Should().Be(2);
        await transaction.SaveAsync("discarded");
        await UpdateEvidenceFeatureAsync(connection, feature.Id);
        (await ReadOperationEvidenceAsync(connection)).GetArrayLength().Should().Be(2);
        await transaction.RollbackAsync("discarded");
        (await ReadOperationEvidenceAsync(connection)).GetRawText().Should().Be(initial.GetRawText());
        await ExecuteEvidenceSqlAsync(connection, "SET LOCAL honua.operation_evidence = '[]'");
        await UpdateEvidenceFeatureAsync(connection, long.MaxValue);
        (await ReadOperationEvidenceAsync(connection)).GetArrayLength().Should().Be(0);
        await transaction.CommitAsync();
        await using var cleared = new NpgsqlCommand(
            "SELECT NULLIF(current_setting('honua.operation_evidence', true), '')", connection);
        (await cleared.ExecuteScalarAsync()).Should().Be(DBNull.Value);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task OperationEvidence_BranchTrigger_RecordsLogicalDeleteButNotPhysicalRemoval()
    {
        var version = await CreateVersionManager().CreateAsync(
            new CreateVersionRequest($"Evidence{Guid.NewGuid():N}", "sde", VersionAccess.Public));
        await using var connection = await OpenSchemaConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteEvidenceSqlAsync(connection, """
            SET LOCAL honua.capture_operation_evidence = 'on';
            SET LOCAL honua.operation_evidence = '[]';
            """);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO honua.version_edits (version_id, layer_id, objectid, operation, attributes)
            VALUES (@version, @layer, 44001, 3, '{"name":"deleted"}'::jsonb)
            """, connection);
        insert.Parameters.AddWithValue("version", version.VersionId);
        insert.Parameters.AddWithValue("layer", PointsLayerId);
        await insert.ExecuteNonQueryAsync();
        var evidence = await ReadOperationEvidenceAsync(connection);
        evidence.GetArrayLength().Should().Be(1);
        evidence[0].GetProperty("version_id").GetGuid().Should().Be(version.VersionId);
        evidence[0].GetProperty("operation").GetInt32().Should().Be(3);
        await AssertEvidenceMatchesChangeAsync(connection, evidence[0]);
        await ExecuteEvidenceSqlAsync(connection, "SET LOCAL honua.operation_evidence = '[]'");
        await using var delete = new NpgsqlCommand(
            "DELETE FROM honua.version_edits WHERE version_id = @version", connection);
        delete.Parameters.AddWithValue("version", version.VersionId);
        (await delete.ExecuteNonQueryAsync()).Should().Be(1);
        (await ReadOperationEvidenceAsync(connection)).GetArrayLength().Should().Be(0);
        await transaction.RollbackAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task OperationEvidence_DefaultDelete_CapturesEachRowOnlyWhenEnabled()
    {
        var store = CreateFeatureStore();
        var first = await store.CreateAsync(PointsLayerId, BuildFeature("first", 1, 1), CancellationToken.None);
        var second = await store.CreateAsync(PointsLayerId, BuildFeature("second", 2, 2), CancellationToken.None);
        await using var connection = await OpenSchemaConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteEvidenceSqlAsync(connection, """
            SET LOCAL honua.capture_operation_evidence = 'off';
            SET LOCAL honua.operation_evidence = '[]';
            """);
        await UpdateEvidenceFeatureAsync(connection, first.Id);
        (await ReadOperationEvidenceAsync(connection)).GetArrayLength().Should().Be(0);
        await ExecuteEvidenceSqlAsync(connection, "SET LOCAL honua.capture_operation_evidence = 'on'");
        await using var delete = new NpgsqlCommand(
            "DELETE FROM features WHERE objectid = ANY(@ids)", connection);
        delete.Parameters.AddWithValue("ids", new[] { first.Id, second.Id });
        (await delete.ExecuteNonQueryAsync()).Should().Be(2);
        var evidence = await ReadOperationEvidenceAsync(connection);
        evidence.GetArrayLength().Should().Be(2);
        foreach (var change in evidence.EnumerateArray())
        {
            change.GetProperty("operation").GetInt32().Should().Be(3);
            await AssertEvidenceMatchesChangeAsync(connection, change);
        }
        await transaction.RollbackAsync();
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task OperationEvidence_ActualReceipt_ReplaysCommittedEvidenceAfterLaterWriter()
    {
        var store = CreateFeatureStore();
        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("receipt", 1, 1), CancellationToken.None);
        var scope = $"evidence-{Guid.NewGuid():N}";
        await using var connection = await OpenSchemaConnectionAsync();
        JsonElement original;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteEvidenceSqlAsync(connection, """
                SET LOCAL honua.capture_operation_evidence = 'on';
                SET LOCAL honua.operation_evidence = '[]';
                """);
            await UpdateEvidenceFeatureAsync(connection, feature.Id);
            await UpdateEvidenceFeatureAsync(connection, feature.Id);
            original = await ReadOperationEvidenceAsync(connection);
            original.GetArrayLength().Should().Be(2);
            original[1].GetProperty("generation").GetInt64().Should().BeGreaterThan(
                original[0].GetProperty("generation").GetInt64());
            await InsertEvidenceReceiptAsync(connection, scope, original);
            await transaction.CommitAsync();
        }
        await store.CreateAsync(PointsLayerId, BuildFeature("later", 2, 2), CancellationToken.None);
        await using var replayConnection = await OpenSchemaConnectionAsync();
        await using var replay = new NpgsqlCommand(
            "SELECT receipt FROM grpc_apply_edits_receipt WHERE scope_key = @scope", replayConnection);
        replay.Parameters.AddWithValue("scope", scope);
        using var document = JsonDocument.Parse((byte[])(await replay.ExecuteScalarAsync())!);
        JsonElement.DeepEquals(document.RootElement, original).Should().BeTrue();
        foreach (var change in document.RootElement.EnumerateArray())
        {
            await AssertEvidenceMatchesChangeAsync(replayConnection, change);
        }
    }

    private static async Task InsertEvidenceReceiptAsync(NpgsqlConnection connection, string scope, JsonElement evidence)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO grpc_apply_edits_receipt (scope_key, receipt, expires_at)
            VALUES (@scope, @receipt, now() + interval '1 hour')
            """, connection);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("receipt", System.Text.Encoding.UTF8.GetBytes(evidence.GetRawText()));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateEvidenceFeatureAsync(NpgsqlConnection connection, long objectId)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE features SET attributes = '{"name":"changed"}'::jsonb WHERE objectid = @id
            """, connection);
        command.Parameters.AddWithValue("id", objectId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteEvidenceSqlAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> ReadOperationEvidenceAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('honua.operation_evidence', true)", connection);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        return document.RootElement.Clone();
    }

    private static async Task AssertEvidenceMatchesChangeAsync(NpgsqlConnection connection, JsonElement evidence)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object('generation', generation, 'layer_id', layer_id,
                'objectid', objectid, 'public_objectid', public_objectid,
                'version_id', version_id, 'operation', operation)::text
            FROM honua.feature_changes WHERE generation = @generation
            """, connection);
        command.Parameters.AddWithValue("generation", evidence.GetProperty("generation").GetInt64());
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        JsonElement.DeepEquals(evidence, document.RootElement).Should().BeTrue();
    }
}
