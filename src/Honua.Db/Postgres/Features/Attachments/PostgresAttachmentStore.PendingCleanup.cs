// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    private string CleanupRetriesTable => SchemaSearchPath.QualifyTable("attachment_cleanup_retries", _canonicalSchema);

    private async Task<List<(string Path, long FeatureId)>> ReadPendingCleanupAsync(
        NpgsqlConnection connection, string cleanupTable, int layerId)
    {
        var pending = new List<(string Path, long FeatureId)>();
        await using (var command = new NpgsqlCommand($"""
            SELECT c.storage_path, c.feature_id FROM {cleanupTable} c
            LEFT JOIN {CleanupRetriesTable} r ON r.cleanup_authority = $2 AND r.storage_path = c.storage_path
            WHERE c.layer_id = $1 AND (r.next_attempt_at IS NULL OR r.next_attempt_at <= clock_timestamp())
            ORDER BY r.next_attempt_at NULLS FIRST, c.storage_path LIMIT 100
            """, connection))
        {
            command.Parameters.AddWithValue(layerId);
            command.Parameters.AddWithValue(cleanupTable);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
            while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                pending.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }
        return pending;
    }

    private async Task ScheduleCleanupRetryAsync(NpgsqlConnection connection, string authority, string path)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CleanupRetriesTable} (cleanup_authority, storage_path, next_attempt_at)
            VALUES ($1, $2, clock_timestamp() + interval '5 minutes')
            ON CONFLICT (cleanup_authority, storage_path) DO UPDATE SET next_attempt_at = EXCLUDED.next_attempt_at
            """, connection);
        command.Parameters.AddWithValue(authority);
        command.Parameters.AddWithValue(path);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ClearCleanupRetryAsync(NpgsqlConnection connection, string authority, string path)
    {
        await using var command = new NpgsqlCommand($"DELETE FROM {CleanupRetriesTable} WHERE cleanup_authority = $1 AND storage_path = $2", connection);
        command.Parameters.AddWithValue(authority);
        command.Parameters.AddWithValue(path);
        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task ExpireUploadSessionsAsync(AttachmentPublicationTransaction database, string sessions)
    {
        var (connection, transaction, cancellationToken) = database;
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        var ledger = SchemaSearchPath.QualifyTable("attachment_upload_cleanup_ledger", _canonicalSchema);
        await using var command = new NpgsqlCommand($"""
            WITH expired AS MATERIALIZED (
                SELECT session_id, layer_id, feature_id, state FROM {sessions}
                WHERE expires_at <= clock_timestamp()
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            ), tombstones AS (
                INSERT INTO {ledger} (session_id, layer_id, feature_id)
                SELECT session_id, layer_id, feature_id FROM expired WHERE state <> 'committed'
                ON CONFLICT (session_id) DO NOTHING
            ), cleanup AS (
                INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
                SELECT @prefix || replace(session_id::text, '-', ''), layer_id, feature_id
                FROM expired WHERE state <> 'committed'
                ON CONFLICT (storage_path) DO NOTHING
            )
            DELETE FROM {sessions} WHERE session_id IN (SELECT session_id FROM expired)
            """, connection, transaction);
        command.Parameters.AddWithValue("prefix", AttachmentUploadObjectKey.Prefix);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await RetryUploadCleanupAsync(database, tables.Cleanup, ledger).ConfigureAwait(false);
    }

    private static async Task RetryUploadCleanupAsync(
        AttachmentPublicationTransaction database, string cleanupTable, string ledger)
    {
        var (connection, transaction, cancellationToken) = database;
        // A prior delete can race with an object write that survived a lost DB
        // connection. Keep tombstones and revisit a bounded, fairly ordered batch.
        await using var retry = new NpgsqlCommand($"""
            WITH due AS MATERIALIZED (
                SELECT session_id, layer_id, feature_id FROM {ledger}
                WHERE next_attempt_at <= clock_timestamp()
                ORDER BY next_attempt_at, session_id LIMIT 100 FOR UPDATE SKIP LOCKED
            ), cleanup AS (
                INSERT INTO {cleanupTable} (storage_path, layer_id, feature_id)
                SELECT @prefix || replace(session_id::text, '-', ''), layer_id, feature_id FROM due
                ON CONFLICT (storage_path) DO NOTHING
            )
            UPDATE {ledger} SET next_attempt_at = clock_timestamp() + interval '1 hour'
            WHERE session_id IN (SELECT session_id FROM due)
            """, connection, transaction);
        retry.Parameters.AddWithValue("prefix", AttachmentUploadObjectKey.Prefix);
        await retry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task DrainPendingAttachmentCleanupAsync(CancellationToken cancellationToken)
    {
        var layers = new List<int>();
        await using (var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            var tables = await ResolveTablesAsync(connection, null, true, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"""
                SELECT DISTINCT c.layer_id FROM {tables.Cleanup} c
                LEFT JOIN {CleanupRetriesTable} r ON r.cleanup_authority = $1 AND r.storage_path = c.storage_path
                WHERE r.next_attempt_at IS NULL OR r.next_attempt_at <= clock_timestamp()
                ORDER BY c.layer_id LIMIT 100
                """, connection);
            command.Parameters.AddWithValue(tables.Cleanup);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                layers.Add(reader.GetInt32(0));
            }
        }
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainImportCleanupAsync(layer).ConfigureAwait(false);
        }
    }
}
