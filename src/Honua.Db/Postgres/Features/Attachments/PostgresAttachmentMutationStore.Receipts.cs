// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentMutationStore
{
    private async Task<bool> InsertReceiptAsync(AttachmentPublicationTransaction database, AttachmentDeleteRequest request, byte[] fingerprint)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_receipts} (owner_id, service_id, layer_id, feature_id, operation,
                idempotency_key, request_sha256, attachment_id, expires_at)
            VALUES (@owner, @service, @layer, @feature, 'delete', @key, @hash, @attachment, now() + @lifetime)
            ON CONFLICT (owner_id, service_id, layer_id, feature_id, operation, idempotency_key) DO NOTHING
            RETURNING attachment_id
            """, database.Connection, database.Transaction);
        BindScope(command, request);
        command.Parameters.AddWithValue("hash", fingerprint);
        command.Parameters.AddWithValue("attachment", request.AttachmentId);
        command.Parameters.AddWithValue("lifetime", request.Lifetime);
        return await command.ExecuteScalarAsync(database.CancellationToken).ConfigureAwait(false) is long;
    }

    private async Task VerifyReceiptAsync(AttachmentPublicationTransaction database, AttachmentDeleteRequest request, byte[] fingerprint)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_sha256, expires_at <= clock_timestamp() FROM {_receipts}
            WHERE owner_id = @owner AND service_id = @service AND layer_id = @layer
                AND feature_id = @feature AND operation = 'delete' AND idempotency_key = @key
            FOR UPDATE
            """, database.Connection, database.Transaction);
        BindScope(command, request);
        await using var reader = await command.ExecuteReaderAsync(database.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(database.CancellationToken).ConfigureAwait(false))
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.NotFound, "Delete receipt was not found.");
        }
        if (reader.GetBoolean(1))
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.Expired, "Delete receipt has expired.");
        }
        if (!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(0), fingerprint))
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.Conflict, "Idempotency key belongs to a different deletion.");
        }
    }
}
