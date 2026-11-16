// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    public async Task CopyContentAsync(AttachmentUploadSessionKey key, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, key, "FOR SHARE").ConfigureAwait(false)).Session;
        EnsureUploading(session);
        if (session.NextOffsetBytes != session.Request.ExpectedSize)
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Upload content is incomplete.");
        }
        // The destination is private staging storage. Its bytes must not be published
        // until this method returns successfully and the security scan passes.
        await CopyVerifiedChunksAsync(database, session, destination).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyVerifiedChunksAsync(SessionDatabase database, AttachmentUploadSession session, Stream destination)
    {
        await using var command = database.Command($"SELECT offset_bytes, content, content_sha256 FROM {_chunks} WHERE session_id = @session ORDER BY offset_bytes");
        command.Parameters.AddWithValue("session", session.SessionId);
        await using var reader = await command.ExecuteReaderAsync(database.CancellationToken).ConfigureAwait(false);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (await reader.ReadAsync(database.CancellationToken).ConfigureAwait(false))
        {
            var content = reader.GetFieldValue<byte[]>(1);
            if (reader.GetInt64(0) != offset || content.Length > session.Request.MaxChunkBytes
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), reader.GetFieldValue<byte[]>(2)))
            {
                throw Failure(AttachmentUploadFailure.InvalidContent, "Stored upload chunk failed integrity validation.");
            }
            digest.AppendData(content);
            await destination.WriteAsync(content, database.CancellationToken).ConfigureAwait(false);
            offset = checked(offset + content.Length);
        }
        if (offset != session.Request.ExpectedSize
            || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), session.Request.Sha256))
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Upload content does not match the expected size and digest.");
        }
    }
}
