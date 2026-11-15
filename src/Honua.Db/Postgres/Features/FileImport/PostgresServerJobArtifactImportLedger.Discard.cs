// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class PostgresServerJobArtifactImportLedger
{
    // PostgreSQL refuses to remove a table while views or other objects depend on it.
    private const string DependentObjectsStillExist = "2BP01";

    public async Task<StagedImportDiscardOutcome> DiscardStagedAsync(
        StagedImportDiscardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeKey = ScopeKey(request.TenantId, request.ActorId, request.IntentId);
        return await WithScopeLockAsync(scopeKey,
            connection => DiscardLockedAsync(connection, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    // A discarded receipt keeps its row with this fingerprint: a retried import of the original
    // preview conflicts instead of recreating the table, and a repeated discard finds nothing.
    private static string DiscardedFingerprint(string scopeKey)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("staged-import-discarded/v1\n" + scopeKey)));

    private async Task<StagedImportDiscardOutcome> DiscardLockedAsync(
        NpgsqlConnection connection, StagedImportDiscardRequest request, CancellationToken cancellationToken)
    {
        var scopeKey = ScopeKey(request.TenantId, request.ActorId, request.IntentId);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadAsync(connection, scopeKey, cancellationToken).ConfigureAwait(false);
        // Only an unspent receipt naming the server-derived staging table qualifies. Callers cannot
        // import under the reserved staging prefix, so a caller-named table never matches.
        if (row is not { Phase: Committed, PhysicalTable: { } physical }
            || string.Equals(row.Fingerprint, DiscardedFingerprint(scopeKey), StringComparison.Ordinal)
            || !string.Equals(row.SchemaName, request.Schema, StringComparison.Ordinal)
            || !string.Equals(row.TableName, request.Table, StringComparison.Ordinal))
        {
            return StagedImportDiscardOutcome.NotFound;
        }

        // Serialize with import and publication of this table before deciding it is unpublished.
        await ImportedDatasetSpatialMetadata.AcquireTransactionLockAsync(
            connection, transaction, request.Schema, physical, cancellationToken).ConfigureAwait(false);
        if (await IsPublishedAsync(transaction, (request.Schema, physical), cancellationToken).ConfigureAwait(false))
        {
            return StagedImportDiscardOutcome.Referenced;
        }

        var refused = await TryDropOwnedAsync(transaction, (request.Schema, physical), cancellationToken).ConfigureAwait(false);
        if (refused is { } outcome)
        {
            return outcome;
        }

        await ImportedDatasetSpatialMetadata.DeleteTableFactsAsync(
            connection, transaction, _metadataSchema, (request.Schema, physical), cancellationToken).ConfigureAwait(false);
        await SpendReceiptAsync(transaction, scopeKey, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return StagedImportDiscardOutcome.Discarded;
    }

    private async Task SpendReceiptAsync(NpgsqlTransaction transaction, string scopeKey, CancellationToken cancellationToken)
    {
        await using var spend = new NpgsqlCommand(
            $"UPDATE {_table} SET fingerprint = @discarded, updated_at = now() WHERE scope_key = @scope_key",
            transaction.Connection!, transaction);
        spend.Parameters.AddWithValue("discarded", DiscardedFingerprint(scopeKey));
        spend.Parameters.AddWithValue("scope_key", scopeKey);
        await spend.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StagedImportDiscardOutcome?> TryDropOwnedAsync(
        NpgsqlTransaction transaction, (string Schema, string Table) target, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection!;
        try
        {
            if (await ImportTableOwnership.AssertOwnedStagedAsync(connection, target.Schema, target.Table, cancellationToken)
                    .ConfigureAwait(false))
            {
                await DropAsync(transaction, target, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ImportTableOwnershipException)
        {
            // The table exists without the importer marker, so it is not ours to drop.
            return StagedImportDiscardOutcome.NotOwned;
        }
        catch (PostgresException exception) when (exception.SqlState == DependentObjectsStillExist)
        {
            return StagedImportDiscardOutcome.Referenced;
        }

        return null;
    }

    private async Task<bool> IsPublishedAsync(
        NpgsqlTransaction transaction, (string Schema, string Table) target,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_layersTable} WHERE table_schema = @schema AND table_name = @table)",
            transaction.Connection!, transaction);
        command.Parameters.AddWithValue("schema", target.Schema);
        command.Parameters.AddWithValue("table", target.Table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task DropAsync(
        NpgsqlTransaction transaction, (string Schema, string Table) target,
        CancellationToken cancellationToken)
    {
        await using var format = new NpgsqlCommand("SELECT format('DROP TABLE %I.%I', @schema, @table)", transaction.Connection!, transaction);
        format.Parameters.AddWithValue("schema", target.Schema);
        format.Parameters.AddWithValue("table", target.Table);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await using var drop = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
