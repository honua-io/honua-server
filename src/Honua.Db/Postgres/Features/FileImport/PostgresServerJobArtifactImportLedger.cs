// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

/// <summary>
/// Catalog receipt for job-artifact imports. A session advisory lock covers the attempt.
/// The committed update runs on the import transaction, not on this connection.
/// </summary>
internal sealed class PostgresServerJobArtifactImportLedger : IServerJobArtifactImportLedger
{
    private const string Reserved = "reserved";
    private const string Committed = "committed";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;

    internal PostgresServerJobArtifactImportLedger(NpgsqlDataSource dataSource, PostgresSchemaConfiguration schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(schema);
        _dataSource = dataSource;
        _table = SchemaSearchPath.QualifyTable("job_artifact_import_receipt", schema.MetadataSchema);
    }

    public async Task<ServerJobArtifactImportReceipt> GetOrImportAsync(
        ServerJobArtifactImportScope scope,
        Func<IServerJobArtifactImportAttempt, CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(import);
        RequireScope(scope);
        var scopeKey = ScopeKey(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!await TryLockAsync(connection, scopeKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ServerJobArtifactImportInProgressException();
        }

        try
        {
            return await GetOrImportLockedAsync(connection, scope, scopeKey, import, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Disconnect also releases the session lock. A failed unlock must not hide the receipt.
            try
            {
                await UnlockAsync(connection, scopeKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }
    }

    private async Task<ServerJobArtifactImportReceipt> GetOrImportLockedAsync(
        NpgsqlConnection connection,
        ServerJobArtifactImportScope scope,
        string scopeKey,
        Func<IServerJobArtifactImportAttempt, CancellationToken, Task<ServerJobArtifactImportReceipt>> import,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(connection, scopeKey, cancellationToken).ConfigureAwait(false);
        if (IsCommittedMatch(existing, scope))
        {
            return ToReceipt(existing!);
        }

        if (existing is { Phase: Committed })
        {
            throw new ServerJobArtifactImportConflictException(scope.IntentId);
        }

        if (existing is null)
        {
            await InsertReservedAsync(connection, scope, scopeKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // The previous holder died before the receipt committed, so the staging merge rolled back.
            await TakeOverReservedAsync(connection, scope, scopeKey, cancellationToken).ConfigureAwait(false);
        }

        var attempt = new Attempt(this, scope, scopeKey);
        try
        {
            await import(attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (await TryReadCommittedAsync(connection, scope, scopeKey).ConfigureAwait(false) is { } committed)
            {
                return committed;
            }

            await DeleteReservedAsync(connection, scopeKey, scope.Fingerprint, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        if (await TryReadCommittedAsync(connection, scope, scopeKey).ConfigureAwait(false) is { } stored)
        {
            return stored;
        }

        await DeleteReservedAsync(connection, scopeKey, scope.Fingerprint, CancellationToken.None)
            .ConfigureAwait(false);
        throw new ServerJobArtifactImportIncompleteException();
    }

    private async Task CompleteAsync(
        ServerJobArtifactImportScope scope,
        string scopeKey,
        ServerJobArtifactImportReceiptPrototype prototype,
        ImportCommitScope commit,
        CancellationToken cancellationToken)
    {
        if (commit.Transaction is not NpgsqlTransaction transaction || transaction.Connection is null)
        {
            throw new InvalidDataException("The import commit is not a PostgreSQL transaction.");
        }

        if (commit.FeatureCount <= 0
            || string.IsNullOrWhiteSpace(commit.Schema)
            || string.IsNullOrWhiteSpace(commit.PhysicalTable)
            || prototype.SourceSrid <= 0
            || prototype.TargetSrid <= 0
            || prototype.ContentSha256.Length != 64
            || prototype.CrsState is not ("known" or "unknown"))
        {
            throw new InvalidDataException("The job artifact import receipt does not match the committed rows.");
        }

        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_table}
            SET phase = '{Committed}',
                schema_name = @schema_name,
                physical_table = @physical_table,
                feature_count = @feature_count,
                source_srid = @source_srid,
                target_srid = @target_srid,
                content_sha256 = @content_sha256,
                crs_state = @crs_state,
                updated_at = now()
            WHERE scope_key = @scope_key
              AND phase = '{Reserved}'
              AND fingerprint = @fingerprint
            """,
            transaction.Connection,
            transaction);
        command.Parameters.AddWithValue("schema_name", commit.Schema);
        command.Parameters.AddWithValue("physical_table", commit.PhysicalTable);
        command.Parameters.AddWithValue("feature_count", commit.FeatureCount);
        command.Parameters.AddWithValue("source_srid", prototype.SourceSrid);
        command.Parameters.AddWithValue("target_srid", prototype.TargetSrid);
        command.Parameters.AddWithValue("content_sha256", prototype.ContentSha256);
        command.Parameters.AddWithValue("crs_state", prototype.CrsState);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("fingerprint", scope.Fingerprint);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidDataException("The job artifact import receipt was no longer reserved for this commit.");
        }
    }

    private async Task<ServerJobArtifactImportReceipt?> TryReadCommittedAsync(
        NpgsqlConnection connection,
        ServerJobArtifactImportScope scope,
        string scopeKey)
    {
        try
        {
            var after = await ReadAsync(connection, scopeKey, CancellationToken.None).ConfigureAwait(false);
            return IsCommittedMatch(after, scope) ? ToReceipt(after!) : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task InsertReservedAsync(
        NpgsqlConnection connection,
        ServerJobArtifactImportScope scope,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} (
                scope_key, tenant_id, actor_id, intent_id, fingerprint, phase, schema_name, table_name)
            VALUES (
                @scope_key, @tenant_id, @actor_id, @intent_id, @fingerprint, '{Reserved}', @schema_name, @table_name)
            """,
            connection);
        BindScope(command, scope, scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TakeOverReservedAsync(
        NpgsqlConnection connection,
        ServerJobArtifactImportScope scope,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_table}
            SET tenant_id = @tenant_id,
                actor_id = @actor_id,
                intent_id = @intent_id,
                fingerprint = @fingerprint,
                phase = '{Reserved}',
                schema_name = @schema_name,
                table_name = @table_name,
                physical_table = NULL,
                feature_count = NULL,
                source_srid = NULL,
                target_srid = NULL,
                content_sha256 = NULL,
                crs_state = NULL,
                updated_at = now()
            WHERE scope_key = @scope_key
              AND phase = '{Reserved}'
            """,
            connection);
        BindScope(command, scope, scopeKey);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new ServerJobArtifactImportInProgressException();
        }
    }

    private async Task DeleteReservedAsync(
        NpgsqlConnection connection,
        string scopeKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                $"""
                DELETE FROM {_table}
                WHERE scope_key = @scope_key
                  AND phase = '{Reserved}'
                  AND fingerprint = @fingerprint
                """,
                connection);
            command.Parameters.AddWithValue("scope_key", scopeKey);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    private async Task<StoredReceipt?> ReadAsync(
        NpgsqlConnection connection,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT phase, fingerprint, schema_name, table_name, physical_table, feature_count,
                   source_srid, target_srid, content_sha256, crs_state, intent_id
            FROM {_table}
            WHERE scope_key = @scope_key
            """,
            connection);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredReceipt(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10));
    }

    private static void BindScope(NpgsqlCommand command, ServerJobArtifactImportScope scope, string scopeKey)
    {
        command.Parameters.AddWithValue("scope_key", scopeKey);
        command.Parameters.AddWithValue("tenant_id", scope.TenantId);
        command.Parameters.AddWithValue("actor_id", scope.ActorId);
        command.Parameters.AddWithValue("intent_id", scope.IntentId);
        command.Parameters.AddWithValue("fingerprint", scope.Fingerprint);
        command.Parameters.AddWithValue("schema_name", scope.Schema);
        command.Parameters.AddWithValue("table_name", scope.Table);
    }

    private static bool IsCommittedMatch(StoredReceipt? row, ServerJobArtifactImportScope scope)
        => row is { Phase: Committed }
            && string.Equals(row.Fingerprint, scope.Fingerprint, StringComparison.Ordinal);

    private static ServerJobArtifactImportReceipt ToReceipt(StoredReceipt row)
    {
        if (row.PhysicalTable is null
            || row.FeatureCount is not int featureCount
            || row.SourceSrid is not int sourceSrid
            || row.TargetSrid is not int targetSrid
            || row.ContentSha256 is null
            || row.CrsState is null)
        {
            throw new ServerJobArtifactImportIncompleteException();
        }

        return new ServerJobArtifactImportReceipt(
            row.IntentId,
            row.SchemaName,
            row.PhysicalTable,
            row.PhysicalTable,
            featureCount,
            sourceSrid,
            targetSrid,
            row.ContentSha256,
            row.CrsState);
    }

    private static async Task<bool> TryLockAsync(
        NpgsqlConnection connection,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtextextended(@scope_key, 0))",
            connection);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task UnlockAsync(
        NpgsqlConnection connection,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtextextended(@scope_key, 0))",
            connection);
        command.Parameters.AddWithValue("scope_key", scopeKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ScopeKey(ServerJobArtifactImportScope scope)
    {
        var text = scope.TenantId + "\n" + scope.ActorId + "\n" + scope.IntentId;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static void RequireScope(ServerJobArtifactImportScope scope)
    {
        if (scope.TenantId.Length is < 1 or > 128
            || scope.ActorId.Length is < 1 or > 128
            || scope.IntentId.Length is < 1 or > 128
            || scope.Fingerprint.Length != 64
            || scope.Schema.Length is < 1 or > 63
            || scope.Table.Length is < 1 or > 63)
        {
            throw new ArgumentException("The job artifact import scope is invalid.", nameof(scope));
        }
    }

    private sealed record StoredReceipt(
        string Phase,
        string Fingerprint,
        string SchemaName,
        string TableName,
        string? PhysicalTable,
        int? FeatureCount,
        int? SourceSrid,
        int? TargetSrid,
        string? ContentSha256,
        string? CrsState,
        string IntentId);

    private sealed class Attempt(
        PostgresServerJobArtifactImportLedger ledger,
        ServerJobArtifactImportScope scope,
        string scopeKey) : IServerJobArtifactImportAttempt
    {
        private ServerJobArtifactImportReceiptPrototype? _prototype;

        public ImportCommitParticipant Participant => CompleteAsync;

        public void Prepare(ServerJobArtifactImportReceiptPrototype prototype)
        {
            ArgumentNullException.ThrowIfNull(prototype);
            _prototype = prototype;
        }

        private ValueTask CompleteAsync(ImportCommitScope commit, CancellationToken cancellationToken)
        {
            if (_prototype is null)
            {
                throw new InvalidDataException("The job artifact import receipt was not prepared.");
            }

            return new ValueTask(ledger.CompleteAsync(scope, scopeKey, _prototype, commit, cancellationToken));
        }
    }
}
