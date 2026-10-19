// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>Real PostgreSQL proof of the atomic destination/receipt persistence contract.</summary>
[Collection("Database")]
public sealed class ImportCommitReceiptIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string OperationId = "0123456789abcdef0123456789abcdef";
    private static readonly string Intent = "v1:sha256:" + new string('a', 64);
    private static readonly byte[] Receipt = "{\"success\":true,\"featureCount\":1}"u8.ToArray();
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(ImportCommitReceiptIntegrationTests));
        var migration = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory,
            "Migrations", "136_CreateImportCommitReceipts.sql"));
        await fixture.ExecuteAsync(migration.Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
        await fixture.ExecuteAsync($"CREATE TABLE {_schema}.destination (id integer PRIMARY KEY)");
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_WaitsForOriginalTransactionDecisionBeforeReportingReceiptAbsence(bool commit)
    {
        await using var writer = await OpenAsync();
        await using var reader = await OpenAsync();
        await using var observer = await OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await InsertDestinationAsync(writer, transaction, 1);
        await WriteAsync(writer, transaction);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var recovering = PostgresImportCommitReceipt.TryReadAsync(reader, _schema, OperationId, Intent, deadline.Token);
        try
        {
            // Verify the actual PostgreSQL wait graph, not a sleep-based assertion that
            // the client happened to be slow. Both connections use the real receipt code.
            await using var blocked = new NpgsqlCommand("SELECT $1 = ANY(pg_blocking_pids($2))", observer);
            blocked.Parameters.AddWithValue(writer.ProcessID);
            blocked.Parameters.AddWithValue(reader.ProcessID);
            while (!(bool)(await blocked.ExecuteScalarAsync(deadline.Token))!)
            {
                recovering.IsCompleted.Should().BeFalse("recovery must wait for the transaction's decision");
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
            recovering.IsCompleted.Should().BeFalse();
            if (commit) await transaction.CommitSafelyAsync(deadline.Token);
            else await transaction.RollbackAsync(deadline.Token);
            var receipt = await recovering.WaitAsync(deadline.Token);
            if (commit) receipt.Should().Equal(Receipt);
            else receipt.Should().BeNull();
            (await CountAsync(observer)).Should().Be(commit ? 1 : 0);
        }
        finally
        {
            // A failed assertion must not leave a live query/transaction behind.
            await transaction.DisposeAsync();
            await deadline.CancelAsync();
            try { await recovering; }
            catch (OperationCanceledException) { }
        }
    }

    [IntegrationTest]
    public async Task LostResultAfterCommit_FreshConnectionRecoversOriginalWithoutSecondMutation()
    {
        await using (var first = await OpenAsync())
        {
            await using var transaction = await first.BeginTransactionAsync();
            await InsertDestinationAsync(first, transaction, 1);
            await WriteAsync(first, transaction);
            await transaction.CommitAsync();
            // Discard the in-memory result and the entire original connection.
        }

        await using var recovery = await OpenAsync();
        (await ReadAsync(recovery)).Should().Equal(Receipt);
        (await CountAsync(recovery)).Should().Be(1);
        (await ReadAsync(recovery)).Should().Equal(Receipt);
        (await CountAsync(recovery)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Rollback_RemovesBothDestinationMutationAndReceipt()
    {
        await using var connection = await OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertDestinationAsync(connection, transaction, 1);
            await WriteAsync(connection, transaction);
            await transaction.RollbackAsync();
        }
        (await ReadAsync(connection)).Should().BeNull();
        (await CountAsync(connection)).Should().Be(0);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOperation_AbortsSecondMutationAndCannotReplaceOriginal(bool changedIntent)
    {
        await using var connection = await OpenAsync();
        await using (var original = await connection.BeginTransactionAsync())
        {
            await InsertDestinationAsync(connection, original, 1);
            await WriteAsync(connection, original);
            await original.CommitAsync();
        }
        await using (var retry = await connection.BeginTransactionAsync())
        {
            await InsertDestinationAsync(connection, retry, 2);
            var duplicate = () => PostgresImportCommitReceipt.WriteAsync(connection, retry, _schema,
                OperationId, changedIntent ? "v1:sha256:" + new string('b', 64) : Intent,
                "different"u8.ToArray(), default);
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.UniqueViolation);
            await retry.RollbackAsync();
        }
        (await CountAsync(connection)).Should().Be(1);
        (await ReadAsync(connection)).Should().Equal(Receipt);
    }

    [IntegrationTest]
    public async Task Recovery_RejectsDifferentFullJobIntent()
    {
        await using var connection = await OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await WriteAsync(connection, transaction);
            await transaction.CommitAsync();
        }
        var read = () => PostgresImportCommitReceipt.TryReadAsync(connection, _schema, OperationId,
            "v1:sha256:" + new string('b', 64), default);
        await read.Should().ThrowAsync<InvalidOperationException>();
        (await ReadAsync(connection)).Should().Equal(Receipt);
    }

    [IntegrationTest]
    public async Task Receipt_CannotJoinAnotherConnectionsTransaction()
    {
        await using var connection = await OpenAsync();
        await using var other = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var write = () => WriteAsync(other, transaction);
        await write.Should().ThrowAsync<InvalidOperationException>();
        await transaction.RollbackAsync();
        (await ReadAsync(other)).Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1048577)]
    public async Task Receipt_RejectsUnboundedOrEmptyPayloadBeforeWrite(int size)
    {
        await using var connection = await OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var write = () => PostgresImportCommitReceipt.WriteAsync(connection, transaction, _schema,
                OperationId, Intent, new byte[size], default);
            await write.Should().ThrowAsync<ArgumentException>();
            await transaction.RollbackAsync();
        }
        (await ReadAsync(connection)).Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("bad-operation", "valid")]
    [InlineData("valid", "v1:sha256:abcd")]
    [InlineData("valid", "v1:sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Database_RejectsNoncanonicalIdentityEvenWhenWriterIsBypassed(string operation, string fingerprint)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.import_commit_receipt (operation_id, intent_fingerprint, receipt) VALUES ($1, $2, $3)", connection);
        command.Parameters.AddWithValue(operation == "valid" ? OperationId : operation);
        command.Parameters.AddWithValue(fingerprint == "valid" ? Intent : fingerprint);
        command.Parameters.AddWithValue(Receipt);
        var write = () => command.ExecuteNonQueryAsync();
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        PostgresImportCommitReceipt.WriteAsync(connection, transaction, _schema, OperationId, Intent, Receipt, default);

    private Task<byte[]?> ReadAsync(NpgsqlConnection connection) =>
        PostgresImportCommitReceipt.TryReadAsync(connection, _schema, OperationId, Intent, default);

    private async Task InsertDestinationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int id)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {_schema}.destination VALUES ($1)", connection, transaction);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_schema}.destination", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
