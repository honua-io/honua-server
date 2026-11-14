// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed class PostgresAttachmentMaintenance(
    IAdoNetDatabaseConnectionProvider connectionProvider, string schema, PostgresAttachmentStore attachmentStore)
{
    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await ExpireStagingAsync(cancellationToken).ConfigureAwait(false);
        await attachmentStore.DrainPendingAttachmentCleanupAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExpireStagingAsync(CancellationToken cancellationToken)
    {
        var sessions = SchemaSearchPath.QualifyTable("attachment_upload_sessions", schema);
        var receipts = SchemaSearchPath.QualifyTable("attachment_mutation_receipts", schema);
        await using var connection = (NpgsqlConnection)await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH expired AS (
                SELECT session_id FROM {sessions} WHERE expires_at <= clock_timestamp()
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            DELETE FROM {sessions} WHERE session_id IN (SELECT session_id FROM expired);
            WITH expired AS (
                SELECT ctid FROM {receipts} WHERE expires_at <= clock_timestamp()
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            )
            DELETE FROM {receipts} WHERE ctid IN (SELECT ctid FROM expired);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
