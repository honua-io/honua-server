// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentMutationStore(
    IAdoNetDatabaseConnectionProvider connectionProvider, string schema, PostgresAttachmentStore attachmentStore)
    : IAttachmentMutationStore
{
    private readonly string _receipts = SchemaSearchPath.QualifyTable("attachment_mutation_receipts", schema);

    public async Task DeleteAsync(AttachmentDeleteRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await using var connection = (NpgsqlConnection)await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var database = new AttachmentPublicationTransaction(connection, transaction, cancellationToken);
        var fingerprint = Fingerprint(request.AttachmentId);
        if (await InsertReceiptAsync(database, request, fingerprint).ConfigureAwait(false))
        {
            await attachmentStore.DeleteWithCleanupAsync(database, request).ConfigureAwait(false);
        }
        else
        {
            await VerifyReceiptAsync(database, request, fingerprint).ConfigureAwait(false);
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(AttachmentDeleteRequest request)
    {
        if (!ValidKey(request.OwnerId) || !ValidKey(request.ServiceId) || !ValidKey(request.IdempotencyKey)
            || request.LayerId < 0 || request.AttachmentId <= 0 || request.Lifetime <= TimeSpan.Zero
            || request.Lifetime > TimeSpan.FromDays(7))
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.InvalidContent, "Delete request contains invalid metadata or bounds.");
        }
    }

    private static bool ValidKey(string value)
        => !string.IsNullOrEmpty(value) && !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= 256;

    private static byte[] Fingerprint(long attachmentId)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, attachmentId);
        return SHA256.HashData(bytes);
    }

    private static void BindScope(NpgsqlCommand command, AttachmentDeleteRequest request)
    {
        command.Parameters.AddWithValue("owner", request.OwnerId);
        command.Parameters.AddWithValue("service", request.ServiceId);
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("feature", request.FeatureId);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);
    }
}
