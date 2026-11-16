// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    public Task<Attachment> CommitAsync(AttachmentUploadSessionKey key, string preparedStoragePath, CancellationToken cancellationToken = default)
        => CommitAsync(key, (_, _) => Task.FromResult(preparedStoragePath), cancellationToken);

    public async Task<Attachment> CommitAsync(AttachmentUploadSessionKey key,
        Func<AttachmentUploadSession, CancellationToken, Task<string>> publishContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishContent);
        var publisher = _attachmentStore ?? throw new InvalidOperationException("Attachment publication is not configured.");
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, key, "FOR UPDATE").ConfigureAwait(false)).Session;
        if (session.State == AttachmentUploadState.Committed)
        {
            return await ReadCommittedAttachmentAsync(database, key).ConfigureAwait(false);
        }
        EnsureUploading(session);
        if (session.NextOffsetBytes != session.Request.ExpectedSize)
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Upload content is incomplete.");
        }
        await publisher.LockUploadParentAsync(
            new AttachmentPublicationTransaction(connection, transaction, cancellationToken),
            session.Request.LayerId, session.Request.FeatureId).ConfigureAwait(false);
        await CopyVerifiedChunksAsync(database, session, Stream.Null).ConfigureAwait(false);
        var preparedStoragePath = await publishContent(session, cancellationToken).ConfigureAwait(false);
        var request = session.Request;
        var attachment = CreatePublishedAttachment(session, preparedStoragePath);
        var published = await publisher.PublishUploadAsync(new AttachmentPublicationTransaction(connection, transaction, cancellationToken), attachment, request.ReplaceAttachmentId).ConfigureAwait(false);
        published = published with
        {
            NativeMetadata = await publisher.RecordNativeMetadataAsync(
                new AttachmentPublicationTransaction(connection, transaction, cancellationToken), published, request).ConfigureAwait(false)
        };
        await SaveCommitReceiptAsync(database, key, published).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    private static Attachment CreatePublishedAttachment(AttachmentUploadSession session, string preparedStoragePath)
    {
        if (!string.Equals(preparedStoragePath, AttachmentUploadObjectKey.ForSession(session.SessionId), StringComparison.Ordinal))
        {
            throw Failure(AttachmentUploadFailure.InvalidContent, "Publication must use the upload session object key.");
        }
        var request = session.Request;
        return Attachment.CreateForUpload(0, request.FeatureId, request.LayerId, request.Filename,
            request.ContentType, request.ExpectedSize, preparedStoragePath, string.Join(',', request.Keywords));
    }

    private async Task SaveCommitReceiptAsync(SessionDatabase database, AttachmentUploadSessionKey key, Attachment attachment)
    {
        await using var command = database.Command($"""
            UPDATE {_sessions} SET state = 'committed', committed_attachment = jsonb_build_object(
                'id', @id::bigint, 'feature', @feature::bigint, 'layer', @layer::integer,
                'filename', @filename::text, 'type', @type::text, 'size', @size::bigint,
                'created', @created::timestamptz, 'path', @path::text, 'keywords', @keywords::text,
                'global_id', @global::uuid, 'native_keywords', @nativeKeywords::text[], 'digest', @digest::text)
            WHERE session_id = @session AND owner_id = @owner;
            DELETE FROM {_chunks} WHERE session_id = @session;
            """);
        BindKey(command, key);
        var metadata = attachment.NativeMetadata ?? throw new InvalidOperationException("Native upload metadata is unavailable.");
        command.Parameters.AddWithValue("global", metadata.GlobalId);
        command.Parameters.AddWithValue("nativeKeywords", metadata.Keywords);
        command.Parameters.AddWithValue("digest", Convert.ToHexString(metadata.Sha256));
        command.Parameters.AddWithValue("id", attachment.Id);
        command.Parameters.AddWithValue("feature", attachment.FeatureId);
        command.Parameters.AddWithValue("layer", attachment.LayerId);
        command.Parameters.AddWithValue("filename", attachment.Filename);
        command.Parameters.AddWithValue("type", attachment.ContentType);
        command.Parameters.AddWithValue("size", attachment.Size);
        command.Parameters.AddWithValue("created", attachment.CreatedAt);
        command.Parameters.AddWithValue("path", attachment.StoragePath);
        command.Parameters.AddWithValue("keywords", NpgsqlTypes.NpgsqlDbType.Text, attachment.Keywords ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(database.CancellationToken).ConfigureAwait(false);
    }

    private async Task<Attachment> ReadCommittedAttachmentAsync(SessionDatabase database, AttachmentUploadSessionKey key)
    {
        await using var command = database.Command($"""
            SELECT (committed_attachment->>'id')::bigint, (committed_attachment->>'feature')::bigint,
                (committed_attachment->>'layer')::integer, committed_attachment->>'filename',
                committed_attachment->>'type', (committed_attachment->>'size')::bigint,
                (committed_attachment->>'created')::timestamptz, committed_attachment->>'path', committed_attachment->>'keywords',
                (committed_attachment->>'global_id')::uuid,
                ARRAY(SELECT jsonb_array_elements_text(committed_attachment->'native_keywords')),
                committed_attachment->>'digest'
            FROM {_sessions} WHERE session_id = @session AND owner_id = @owner
            """);
        BindKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(database.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(database.CancellationToken).ConfigureAwait(false))
        {
            throw Failure(AttachmentUploadFailure.NotFound, "Upload receipt was not found.");
        }
        var attachment = PostgresAttachmentStore.ReadAttachment(reader);
        return reader.IsDBNull(9) ? attachment : attachment with
        {
            NativeMetadata = new AttachmentNativeMetadata(reader.GetGuid(9),
                reader.GetFieldValue<string[]>(10), Convert.FromHexString(reader.GetString(11)))
        };
    }
}
