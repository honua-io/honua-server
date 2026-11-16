// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Attachments.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal readonly record struct AttachmentPublicationTransaction(NpgsqlConnection Connection, NpgsqlTransaction Transaction, CancellationToken CancellationToken);

internal sealed partial class PostgresAttachmentStore
{
    private async Task EnforceUploadQuotaAsync(AttachmentPublicationTransaction database, Attachment attachment, long? excludedId)
    {
        await LockAttachmentQuotaAsync(database, attachment).ConfigureAwait(false);
        var (count, totalSize) = await ReadAttachmentQuotaAsync(database, attachment, excludedId).ConfigureAwait(false);
        var limits = _attachmentLimits;
        if (limits is null)
        {
            return;
        }
        if (attachment.Size > limits.MaxAttachmentSize)
        {
            throw new ValidationException(
                $"Attachment size exceeds maximum allowed ({limits.MaxAttachmentSize:N0} bytes)");
        }
        if (count >= limits.MaxAttachmentsPerFeature)
        {
            throw new ValidationException(
                $"Feature already has the maximum number of attachments ({limits.MaxAttachmentsPerFeature})");
        }

        if (attachment.Size > limits.MaxTotalAttachmentSize ||
            totalSize > limits.MaxTotalAttachmentSize - attachment.Size)
        {
            throw new ValidationException(
                $"Total attachment size would exceed maximum allowed ({limits.MaxTotalAttachmentSize:N0} bytes)");
        }
    }

    private static async Task LockAttachmentQuotaAsync(AttachmentPublicationTransaction database, Attachment attachment)
    {
        await using var lease = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('attachment:' || $1::text || ':' || $2::text, 0))",
            database.Connection, database.Transaction);
        lease.Parameters.AddWithValue(attachment.LayerId);
        lease.Parameters.AddWithValue(attachment.FeatureId);
        await lease.ExecuteNonQueryAsync(database.CancellationToken).ConfigureAwait(false);
    }

    private async Task<(long Count, long TotalSize)> ReadAttachmentQuotaAsync(
        AttachmentPublicationTransaction database, Attachment attachment, long? excludedId)
    {
        var (connection, transaction, cancellationToken) = database;
        var tables = await ResolveTablesAsync(connection, transaction, false, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT COUNT(*)::bigint, COALESCE(SUM(size), 0)::bigint
            FROM {tables.Attachments}
            WHERE layer_id = $1 AND feature_id = $2 AND ($3::bigint IS NULL OR id <> $3)
            """, connection, transaction);
        command.Parameters.AddWithValue(attachment.LayerId);
        command.Parameters.AddWithValue(attachment.FeatureId);
        command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint, excludedId ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Failed to count attachments");
        }
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

}
