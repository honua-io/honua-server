// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
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
        // A prior delete can race with an object write that survived a lost DB
        // connection. Keep tombstones and revisit a bounded, fairly ordered batch.
        await using var retry = new NpgsqlCommand($"""
            WITH due AS MATERIALIZED (
                SELECT session_id, layer_id, feature_id FROM {ledger}
                WHERE next_attempt_at <= clock_timestamp()
                ORDER BY next_attempt_at, session_id LIMIT 100 FOR UPDATE SKIP LOCKED
            ), cleanup AS (
                INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
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
            await using var command = new NpgsqlCommand($"SELECT DISTINCT layer_id FROM {tables.Cleanup} ORDER BY layer_id LIMIT 100", connection);
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
