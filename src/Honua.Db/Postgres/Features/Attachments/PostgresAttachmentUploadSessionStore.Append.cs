// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    public async Task<AttachmentUploadSession> AppendAsync(AttachmentUploadChunk chunk, CancellationToken cancellationToken = default)
    {
        if (chunk.Content is null || chunk.Sha256 is null)
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Chunk bytes and digest are required.");
        }
        chunk = chunk with { Content = (byte[])chunk.Content.Clone(), Sha256 = (byte[])chunk.Sha256.Clone() };
        ValidateChunk(chunk);
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, chunk.Key, "FOR UPDATE").ConfigureAwait(false)).Session;
        EnsureUploading(session);
        if (chunk.Content.Length > session.Request.MaxChunkBytes)
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Chunk exceeds the negotiated byte limit.");
        }
        if (chunk.OffsetBytes < session.NextOffsetBytes)
        {
            await CheckRetryAsync(database, chunk).ConfigureAwait(false);
        }
        else
        {
            await AppendNewChunkAsync(database, chunk, session).ConfigureAwait(false);
            session = session with { NextOffsetBytes = session.NextOffsetBytes + chunk.Content.Length };
        }
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task CheckRetryAsync(SessionDatabase database, AttachmentUploadChunk chunk)
    {
        await using var command = database.Command($"SELECT content, content_sha256 FROM {_chunks} WHERE session_id = @session AND offset_bytes = @offset");
        command.Parameters.AddWithValue("session", chunk.Key.SessionId);
        command.Parameters.AddWithValue("offset", chunk.OffsetBytes);
        await using var reader = await command.ExecuteReaderAsync(database.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(database.CancellationToken).ConfigureAwait(false)
            || !CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(1), chunk.Sha256)
            || !reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(chunk.Content))
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Chunk retry conflicts with previously accepted bytes.");
        }
    }

    private async Task AppendNewChunkAsync(SessionDatabase database, AttachmentUploadChunk chunk, AttachmentUploadSession session)
    {
        if (chunk.OffsetBytes != session.NextOffsetBytes || chunk.Content.Length > session.Request.ExpectedSize - session.NextOffsetBytes)
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Chunk offset or length does not match the remaining upload.");
        }
        await using var command = database.Command($"""
            INSERT INTO {_chunks} (session_id, offset_bytes, content, content_sha256)
                VALUES (@session, @offset, @content, @digest);
            UPDATE {_sessions} SET next_offset_bytes = next_offset_bytes + @length WHERE session_id = @session;
            """);
        command.Parameters.AddWithValue("session", chunk.Key.SessionId);
        command.Parameters.AddWithValue("offset", chunk.OffsetBytes);
        command.Parameters.AddWithValue("content", chunk.Content);
        command.Parameters.AddWithValue("digest", chunk.Sha256);
        command.Parameters.AddWithValue("length", chunk.Content.Length);
        await command.ExecuteNonQueryAsync(database.CancellationToken).ConfigureAwait(false);
    }

    public async Task AbortAsync(AttachmentUploadSessionKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, key, "FOR UPDATE").ConfigureAwait(false)).Session;
        if (session.State != AttachmentUploadState.Uploading)
        {
            return; // Aborting a committed session never removes its attachment.
        }
        await using var command = database.Command($"""
            DELETE FROM {_chunks} WHERE session_id = @session;
            UPDATE {_sessions} SET state = 'aborted' WHERE session_id = @session;
            """);
        command.Parameters.AddWithValue("session", key.SessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureUploading(AttachmentUploadSession session)
    {
        if (session.State != AttachmentUploadState.Uploading)
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Upload session is no longer accepting chunks.");
        }
    }
}
