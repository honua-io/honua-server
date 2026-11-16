// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;
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
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
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
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
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
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task LegacyExpiredScopeUpsert_ClearsEvidenceFromPriorAttempt()
    {
        await ExecuteAsync($"INSERT INTO {Table} (scope_key, receipt, expires_at) VALUES ('retry', '\\x0102', now() - interval '1 hour')");
        await SetEvidenceAsync();

        await ExecuteAsync($"INSERT INTO {Table} AS existing (scope_key, receipt, expires_at) VALUES ('retry', '\\x0103', now() + interval '1 hour') ON CONFLICT (scope_key) DO UPDATE SET receipt = EXCLUDED.receipt, expires_at = EXCLUDED.expires_at, created_at = now() WHERE existing.expires_at <= now()");

        (await ScalarAsync($"SELECT operation_evidence IS NULL AND encode(receipt, 'hex') = '0103' FROM {Table}"))
            .Should().Be(true);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task Migration_Reapplied_PreservesCommittedEvidenceAndInvalidation()
    {
        await SeedEvidenceAsync();

        await ApplyMigrationAsync("195_AddGrpcReceiptOperationEvidence.sql");

        (await ScalarAsync($"SELECT operation_evidence->>'version' FROM {Table}")).Should().Be("1");
        await ExecuteAsync($"UPDATE {Table} SET receipt = receipt");
        (await ScalarAsync($"SELECT operation_evidence IS NULL FROM {Table}")).Should().Be(true);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
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

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task TryReadAsync_SidecarPresentOrAbsent_PreservesLegacyPayload()
    {
        await SeedEvidenceAsync();
        var receipts = new PostgresFeatureEditIdempotencyReceipts(database.DataSource, _schema);

        var expanded = await receipts.TryReadAsync("retry");
        expanded!.Value.ToArray().Should().Equal(1, 2);
        await ExecuteAsync($"DROP TRIGGER invalidate_grpc_receipt_operation_evidence ON {Table}; ALTER TABLE {Table} DROP COLUMN operation_evidence");
        var legacy = await receipts.TryReadAsync("retry");
        legacy!.Value.ToArray().Should().Equal(1, 2);
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task TryReadWithEvidenceAsync_CommittedSidecar_PreservesUnavailableAndConfirmedEmpty()
    {
        await SeedEvidenceAsync();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 }));
        await ExecuteAsync($"UPDATE {Table} SET operation_evidence = '{{\"version\":1,\"payloadSha256\":\"{hash}\",\"creates\":[null,[]],\"updates\":[],\"deletes\":[]}}'::jsonb");
        IFeatureEditIdempotencyReceipts receipts = new PostgresFeatureEditIdempotencyReceipts(database.DataSource, _schema);

        var replay = await receipts.TryReadWithEvidenceAsync("retry");

        replay.Should().NotBeNull();
        replay!.Value.Payload.ToArray().Should().Equal(1, 2);
        replay.Value.Evidence.Should().NotBeNull();
        replay.Value.Evidence!.Creates.Should().HaveCount(2);
        replay.Value.Evidence.Creates[0].IsDefault.Should().BeTrue();
        replay.Value.Evidence.Creates[1].IsDefault.Should().BeFalse();
        replay.Value.Evidence.Creates[1].Should().BeEmpty();
    }

    [IntegrationTheory]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    [InlineData("null")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":2,\"payloadSha256\":\"$hash\",\"creates\":[],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"wrong\",\"creates\":[],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[{}],\"updates\":[],\"deletes\":[]}")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"1\",\"payloadSha256\":\"$hash\",\"creates\":[],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":null,\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":0,\"layer_id\":91001,\"objectid\":10,\"public_objectid\":null,\"version_id\":null,\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":\"1\",\"layer_id\":91001,\"objectid\":10,\"public_objectid\":null,\"version_id\":null,\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":1,\"layer_id\":null,\"objectid\":10,\"public_objectid\":null,\"version_id\":null,\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":1,\"layer_id\":91001,\"objectid\":9223372036854775808,\"public_objectid\":null,\"version_id\":null,\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":1,\"layer_id\":91001,\"objectid\":10,\"public_objectid\":\"10\",\"version_id\":null,\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":1,\"layer_id\":91001,\"objectid\":10,\"public_objectid\":null,\"version_id\":\"invalid\",\"operation\":1}]],\"updates\":[],\"deletes\":[]}")]
    [InlineData("{\"version\":1,\"payloadSha256\":\"$hash\",\"creates\":[[{\"generation\":1,\"layer_id\":91001,\"objectid\":10,\"public_objectid\":null,\"version_id\":null,\"operation\":9}]],\"updates\":[],\"deletes\":[]}")]
    public async Task TryReadWithEvidenceAsync_UnusableSidecar_PreservesPayloadWithUnavailableEvidence(string json)
    {
        await SeedEvidenceAsync();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 }));
        json = json.Replace("$hash", hash, StringComparison.Ordinal);
        await using var command = database.DataSource.CreateCommand($"UPDATE {Table} SET operation_evidence = $1::jsonb");
        command.Parameters.AddWithValue(json);
        await command.ExecuteNonQueryAsync();
        IFeatureEditIdempotencyReceipts receipts = new PostgresFeatureEditIdempotencyReceipts(database.DataSource, _schema);

        var replay = await receipts.TryReadWithEvidenceAsync("retry");

        replay.Should().NotBeNull();
        replay!.Value.Payload.ToArray().Should().Equal(1, 2);
        replay.Value.Evidence.Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task TryReadWithEvidenceAsync_LegacySchema_PreservesPayloadWithUnavailableEvidence()
    {
        await SeedEvidenceAsync();
        await ExecuteAsync($"DROP TRIGGER invalidate_grpc_receipt_operation_evidence ON {Table}; ALTER TABLE {Table} DROP COLUMN operation_evidence");
        IFeatureEditIdempotencyReceipts receipts = new PostgresFeatureEditIdempotencyReceipts(database.DataSource, _schema);

        var replay = await receipts.TryReadWithEvidenceAsync("retry");

        replay.Should().NotBeNull();
        replay!.Value.Payload.ToArray().Should().Equal(1, 2);
        replay.Value.Evidence.Should().BeNull();
        (await receipts.TryReadWithEvidenceAsync("missing")).Should().BeNull();
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
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
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
