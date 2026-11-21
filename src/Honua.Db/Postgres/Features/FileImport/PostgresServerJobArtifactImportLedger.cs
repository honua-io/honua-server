// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed class PostgresServerJobArtifactImportLedger : IServerJobArtifactImportLedger
{
    private const string Reserved = "reserved";
    private const string Committed = "committed";
    private const string Discarded = "discarded";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _metadataSchema;
    private readonly string _table;
    private readonly string _layersTable;
    private readonly string _storageBindingsIdxTable;
    private readonly string _metadataV2CurrentTable;
    private readonly PostgresSessionLockConnections _sessionLockConnections;

    internal PostgresServerJobArtifactImportLedger(
        NpgsqlDataSource dataSource,
        PostgresSchemaConfiguration schema,
        Microsoft.Extensions.Options.IOptions<PostgresSessionLockConnections>? sessionLockConnections = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(schema);
        _dataSource = dataSource;
        _metadataSchema = schema.MetadataSchema;
        _sessionLockConnections = sessionLockConnections?.Value ?? PostgresSessionLockConnections.Pooled;
        _table = SchemaSearchPath.QualifyTable("job_artifact_import_receipt", schema.MetadataSchema);
        _layersTable = SchemaSearchPath.QualifyTable("layers", schema.MetadataSchema);
        _storageBindingsIdxTable = SchemaSearchPath.QualifyTable("metadata_v2_storage_bindings_idx", schema.MetadataSchema);
        _metadataV2CurrentTable = SchemaSearchPath.QualifyTable("metadata_v2_current", schema.MetadataSchema);
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
        // The attempt holds a session advisory lock. On Lambda the connection is unpooled, so
        // closing it ends the RDS Proxy-pinned session.
        await using var connection = await _sessionLockConnections.OpenAsync(_dataSource, cancellationToken)
            .ConfigureAwait(false);
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
                // The receipt is already decided. A failed unlock must not replace it.
                // The next attempt takes over a reserved row when the session is gone.
                Trace.TraceWarning(
                    "Job artifact import left its session lock for takeover: {0}",
                    exception.GetType().Name);
            }
        }
    }

    public async Task<ServerJobArtifactImportDiscardReceipt> DiscardAsync(
        ServerJobArtifactImportDiscardScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireDiscardScope(scope);
        var scopeKey = ScopeKey(scope.TenantId, scope.ActorId, scope.IntentId);
        await using var connection = await _sessionLockConnections.OpenAsync(_dataSource, cancellationToken)
            .ConfigureAwait(false);
        if (!await TryLockAsync(connection, scopeKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ServerJobArtifactImportInProgressException();
        }

        try
        {
            var request = new DiscardRequest(connection, scope, scopeKey);
            return await DiscardLockedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await UnlockAsync(connection, scopeKey, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning(
                    "Job artifact import discard left its session lock for takeover: {0}",
                    exception.GetType().Name);
            }
        }
    }

    private async Task<ServerJobArtifactImportDiscardReceipt> DiscardLockedAsync(
        DiscardRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(request.Connection, request.ScopeKey, cancellationToken).ConfigureAwait(false);
        ValidateDiscardableReceipt(existing, request.Scope);
        if (existing!.Phase == Discarded)
        {
            return ToAlreadyDiscardedReceipt(existing);
        }

        EnsureOwnedStagedTarget(existing, request.Scope.ExpectedTable);
        await DropAndMarkDiscardedAsync(request, existing, cancellationToken).ConfigureAwait(false);
        return ToFreshDiscardReceipt(existing);
    }

    private static void ValidateDiscardableReceipt(StoredReceipt? existing, ServerJobArtifactImportDiscardScope scope)
    {
        if (existing is null
            || !string.Equals(existing.TenantId, scope.TenantId, StringComparison.Ordinal)
            || !string.Equals(existing.ActorId, scope.ActorId, StringComparison.Ordinal)
            || !string.Equals(existing.IntentId, scope.IntentId, StringComparison.Ordinal))
        {
            throw new ServerJobArtifactImportNotFoundException(scope.IntentId);
        }

        if (existing.Phase == Reserved)
        {
            throw new ServerJobArtifactImportInProgressException();
        }

        if (string.IsNullOrWhiteSpace(existing.PhysicalTable))
        {
            throw new ServerJobArtifactImportIncompleteException();
        }
    }

    private static ServerJobArtifactImportDiscardReceipt ToFreshDiscardReceipt(StoredReceipt existing) => new()
    {
        IntentId = existing.IntentId,
        Schema = existing.SchemaName,
        Table = existing.TableName,
        PhysicalTable = existing.PhysicalTable!,
        AlreadyDiscarded = false,
    };

    private static ServerJobArtifactImportDiscardReceipt ToAlreadyDiscardedReceipt(StoredReceipt existing) => new()
    {
        IntentId = existing.IntentId,
        Schema = existing.SchemaName,
        Table = existing.TableName,
        PhysicalTable = existing.PhysicalTable!,
        AlreadyDiscarded = true,
    };

    private void EnsureOwnedStagedTarget(StoredReceipt existing, string expectedTable)
    {
        var physical = existing.PhysicalTable!;
        var isExpectedName = existing.TableName.StartsWith("staged_", StringComparison.Ordinal)
            && string.Equals(existing.TableName, expectedTable, StringComparison.Ordinal)
            && (string.Equals(physical, expectedTable, StringComparison.Ordinal)
                || string.Equals(physical, "imported_" + expectedTable, StringComparison.Ordinal));
        var isForbiddenSchema = string.Equals(existing.SchemaName, _metadataSchema, StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.SchemaName, "honua", StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.SchemaName, "pg_catalog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(existing.SchemaName, "information_schema", StringComparison.OrdinalIgnoreCase);
        if (!isExpectedName || isForbiddenSchema)
        {
            throw new ServerJobArtifactImportNotOwnedException(existing.SchemaName, physical);
        }
    }

    private async Task DropAndMarkDiscardedAsync(
        DiscardRequest request,
        StoredReceipt existing,
        CancellationToken cancellationToken)
    {
        await using var transaction = await request.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tx = new TransactionContext(request.Connection, transaction);
        if (await IsReferencedByLayerAsync(tx, existing, cancellationToken).ConfigureAwait(false)
            || await IsReferencedByStorageBindingAsync(tx, existing, cancellationToken).ConfigureAwait(false))
        {
            throw new ServerJobArtifactImportReferencedException(existing.SchemaName, existing.PhysicalTable!);
        }

        await DropOwnedTableAsync(tx, existing, cancellationToken).ConfigureAwait(false);
        await MarkReceiptDiscardedAsync(tx, request.ScopeKey, cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DropOwnedTableAsync(
        TransactionContext tx,
        StoredReceipt existing,
        CancellationToken cancellationToken)
    {
        var physical = existing.PhysicalTable!;
        bool existsAndOwned;
        try
        {
            existsAndOwned = await ImportTableOwnership.AssertOwnedAsync(
                tx.Connection, existing.SchemaName, physical, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportTableOwnershipException)
        {
            throw new ServerJobArtifactImportNotOwnedException(existing.SchemaName, physical);
        }

        if (existsAndOwned)
        {
            await using var drop = new NpgsqlCommand(
                $"DROP TABLE {SqlIdentifier.QuoteIdentifier(existing.SchemaName)}.{SqlIdentifier.QuoteIdentifier(physical)} RESTRICT",
                tx.Connection,
                tx.Transaction);
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MarkReceiptDiscardedAsync(
        TransactionContext tx,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {_table}
            SET phase = '{Discarded}',
                updated_at = now()
            WHERE scope_key = @scope_key
              AND phase = '{Committed}'
            """,
            tx.Connection,
            tx.Transaction);
        update.Parameters.AddWithValue("scope_key", scopeKey);
        var updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new ServerJobArtifactImportInProgressException();
        }
    }

    private async Task<bool> IsReferencedByLayerAsync(
        TransactionContext tx,
        StoredReceipt existing,
        CancellationToken cancellationToken)
    {
        await using var layerCommand = new NpgsqlCommand(
            $"""
            SELECT 1
            FROM {_layersTable}
            WHERE lower(table_schema) = lower(@schema)
              AND (lower(table_name) = lower(@physical_table) OR lower(table_name) = lower(@logical_table))
            LIMIT 1
            """,
            tx.Connection,
            tx.Transaction);
        layerCommand.Parameters.AddWithValue("schema", existing.SchemaName);
        layerCommand.Parameters.AddWithValue("physical_table", existing.PhysicalTable!);
        layerCommand.Parameters.AddWithValue("logical_table", existing.TableName);
        return await layerCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<bool> IsReferencedByStorageBindingAsync(
        TransactionContext tx,
        StoredReceipt existing,
        CancellationToken cancellationToken)
    {
        var physical = existing.PhysicalTable!;
        await using var bindingCommand = new NpgsqlCommand(
            $"""
            SELECT 1
            FROM {_storageBindingsIdxTable} sb
            INNER JOIN {_metadataV2CurrentTable} cur
                ON cur.environment = sb.environment
               AND cur.revision = sb.revision
            WHERE lower(replace(sb.locator, '"', '')) IN (lower(@qualified_physical), lower(@qualified_logical))
               OR (lower(@schema) = 'public'
                   AND lower(replace(sb.locator, '"', '')) IN (lower(@physical_table), lower(@logical_table)))
            LIMIT 1
            """,
            tx.Connection,
            tx.Transaction);
        bindingCommand.Parameters.AddWithValue("schema", existing.SchemaName);
        bindingCommand.Parameters.AddWithValue("physical_table", physical);
        bindingCommand.Parameters.AddWithValue("logical_table", existing.TableName);
        bindingCommand.Parameters.AddWithValue("qualified_physical", $"{existing.SchemaName}.{physical}");
        bindingCommand.Parameters.AddWithValue("qualified_logical", $"{existing.SchemaName}.{existing.TableName}");
        return await bindingCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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

        if (existing is { Phase: Discarded })
        {
            throw new ServerJobArtifactImportDiscardedException(scope.IntentId);
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
            // The original import error is what the caller sees. A reserved row that
            // survives is taken over by the next attempt with the same fingerprint.
            Trace.TraceWarning(
                "Job artifact import left a reserved receipt for takeover: {0}",
                exception.GetType().Name);
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
                   source_srid, target_srid, content_sha256, crs_state, intent_id, tenant_id, actor_id
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

        return new StoredReceipt
        {
            Phase = reader.GetString(0),
            Fingerprint = reader.GetString(1),
            SchemaName = reader.GetString(2),
            TableName = reader.GetString(3),
            PhysicalTable = reader.IsDBNull(4) ? null : reader.GetString(4),
            FeatureCount = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            SourceSrid = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            TargetSrid = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            ContentSha256 = reader.IsDBNull(8) ? null : reader.GetString(8),
            CrsState = reader.IsDBNull(9) ? null : reader.GetString(9),
            IntentId = reader.GetString(10),
            TenantId = reader.GetString(11),
            ActorId = reader.GetString(12),
        };
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
        => ScopeKey(scope.TenantId, scope.ActorId, scope.IntentId);

    private static string ScopeKey(string tenantId, string actorId, string intentId)
    {
        var text = tenantId + "\n" + actorId + "\n" + intentId;
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

    private static void RequireDiscardScope(ServerJobArtifactImportDiscardScope scope)
    {
        if (scope.TenantId.Length is < 1 or > 128
            || scope.ActorId.Length is < 1 or > 128
            || scope.IntentId.Length is < 1 or > 128
            || scope.ExpectedTable.Length is < 1 or > 63)
        {
            throw new ArgumentException("The job artifact import discard scope is invalid.", nameof(scope));
        }
    }

    private readonly record struct DiscardRequest(
        NpgsqlConnection Connection,
        ServerJobArtifactImportDiscardScope Scope,
        string ScopeKey);

    private readonly record struct TransactionContext(
        NpgsqlConnection Connection,
        NpgsqlTransaction Transaction);

    private sealed record StoredReceipt
    {
        public required string Phase { get; init; }
        public required string Fingerprint { get; init; }
        public required string SchemaName { get; init; }
        public required string TableName { get; init; }
        public string? PhysicalTable { get; init; }
        public int? FeatureCount { get; init; }
        public int? SourceSrid { get; init; }
        public int? TargetSrid { get; init; }
        public string? ContentSha256 { get; init; }
        public string? CrsState { get; init; }
        public required string IntentId { get; init; }
        public required string TenantId { get; init; }
        public required string ActorId { get; init; }
    }

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
