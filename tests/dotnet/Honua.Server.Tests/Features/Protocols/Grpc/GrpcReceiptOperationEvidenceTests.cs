// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Collection("Database.CoreFeatureStore")]
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcReceiptOperationEvidenceTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private string _schema = null!;
    private string Table => $"{_schema}.grpc_apply_edits_receipt";

    public async Task InitializeAsync()
    {
        _schema = await database.CreateIsolatedSchemaAsync(nameof(GrpcReceiptOperationEvidenceTests));
        await ApplyMigrationAsync("130_CreateGrpcApplyEditsReceipt.sql");
        await ApplyMigrationAsync("195_AddGrpcReceiptOperationEvidence.sql");
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    public async Task CommittedSidecar_ReplaysOnNewConnection_WithoutChangingLegacyBytes()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table} (scope_key, receipt, expires_at) VALUES ('retry', '\\x010203', now() + interval '1 hour'); UPDATE {Table} SET operation_evidence = '{{\"version\":1,\"creates\":[null,[]]}}'::jsonb WHERE scope_key = 'retry'";
        await command.ExecuteNonQueryAsync();

        (await ScalarAsync($"SELECT count(*) FROM {Table}")).Should().Be(0L);
        await transaction.CommitAsync();

        (await ScalarAsync($"SELECT encode(receipt, 'hex') FROM {Table}")).Should().Be("010203");
        (await ScalarAsync($"SELECT operation_evidence->'creates'->0 = 'null'::jsonb AND operation_evidence->'creates'->1 = '[]'::jsonb FROM {Table}"))
            .Should().Be(true);
    }

    [IntegrationTheory]
    [InlineData("receipt = '\\x0104'")]
    [InlineData("receipt = receipt")]
    [InlineData("expires_at = now() + interval '2 hours'")]
    public async Task LegacyWriter_ReplacingReceiptOrExpiry_ClearsEvidence(string replacement)
    {
        await SeedEvidenceAsync();

        await ExecuteAsync($"UPDATE {Table} SET {replacement} WHERE scope_key = 'retry'");

        (await ScalarAsync($"SELECT operation_evidence IS NULL FROM {Table}")).Should().Be(true);
    }

    [IntegrationTest]
    public async Task LegacyExpiredScopeUpsert_ClearsEvidenceFromPriorAttempt()
    {
        await ExecuteAsync($"INSERT INTO {Table} (scope_key, receipt, expires_at) VALUES ('retry', '\\x0102', now() - interval '1 hour')");
        await SetEvidenceAsync();

        await ExecuteAsync($"INSERT INTO {Table} AS existing (scope_key, receipt, expires_at) VALUES ('retry', '\\x0103', now() + interval '1 hour') ON CONFLICT (scope_key) DO UPDATE SET receipt = EXCLUDED.receipt, expires_at = EXCLUDED.expires_at, created_at = now() WHERE existing.expires_at <= now()");

        (await ScalarAsync($"SELECT operation_evidence IS NULL AND encode(receipt, 'hex') = '0103' FROM {Table}"))
            .Should().Be(true);
    }

    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesCommittedEvidenceAndInvalidation()
    {
        await SeedEvidenceAsync();

        await ApplyMigrationAsync("195_AddGrpcReceiptOperationEvidence.sql");

        (await ScalarAsync($"SELECT operation_evidence->>'version' FROM {Table}")).Should().Be("1");
        await ExecuteAsync($"UPDATE {Table} SET receipt = receipt");
        (await ScalarAsync($"SELECT operation_evidence IS NULL FROM {Table}")).Should().Be(true);
    }

    [IntegrationTest]
    public async Task RolledBackSidecar_IsNeverVisibleToAnotherConnection()
    {
        await SeedEvidenceAsync();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE {Table} SET operation_evidence = '{{\"version\":2}}'::jsonb WHERE scope_key = 'retry'";
        await command.ExecuteNonQueryAsync();

        (await ScalarAsync($"SELECT operation_evidence->>'version' FROM {Table}")).Should().Be("1");
        await transaction.RollbackAsync();
        (await ScalarAsync($"SELECT operation_evidence->>'version' FROM {Table}")).Should().Be("1");
    }

    private async Task SeedEvidenceAsync()
    {
        await ExecuteAsync($"INSERT INTO {Table} (scope_key, receipt, expires_at) VALUES ('retry', '\\x0102', now() + interval '1 hour')");
        await SetEvidenceAsync();
    }

    private Task SetEvidenceAsync() => ExecuteAsync(
        $"UPDATE {Table} SET operation_evidence = '{{\"version\":1}}'::jsonb WHERE scope_key = 'retry'");

    private async Task ApplyMigrationAsync(string suffix)
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        if (name is null)
        {
            return; // The RED baseline has no sidecar migration yet.
        }
        await using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", _schema, StringComparison.Ordinal);
        await ExecuteAsync(sql);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
