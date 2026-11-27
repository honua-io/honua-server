// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    private static readonly ConcurrentDictionary<Guid, SessionCommitGate> _sessionCommitGates = new();
    private static readonly TimeSpan _cleanupLedgerTimeout = TimeSpan.FromSeconds(5);

    public Task<Attachment> CommitAsync(AttachmentUploadSessionKey key, string preparedStoragePath, CancellationToken cancellationToken = default)
        => CommitAsync(key, (_, _) => Task.FromResult(preparedStoragePath), cancellationToken);

    public async Task<Attachment> CommitAsync(AttachmentUploadSessionKey key,
        Func<AttachmentUploadSession, CancellationToken, Task<string>> publishContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishContent);
        var publisher = _attachmentStore ?? throw new InvalidOperationException("Attachment publication is not configured.");

        await using var gateLease = await AcquireSessionCommitGateAsync(key.SessionId, cancellationToken).ConfigureAwait(false);
        var preflight = await PrepareCommitAsync(key, publisher, cancellationToken).ConfigureAwait(false);
        if (preflight.CommittedAttachment is { } alreadyCommitted)
        {
            return alreadyCommitted;
        }

        // Execute cloud storage upload completely outside any PostgreSQL connection
        // or transaction so network jitter never triggers idle_in_transaction timeouts
        // or holds row locks on the parent feature or upload session.
        var preparedStoragePath = await publishContent(preflight.Session, cancellationToken).ConfigureAwait(false);
        var attachment = CreatePublishedAttachment(preflight.Session, preparedStoragePath);
        return await CommitPublishedAttachmentAsync(
            new CommitPublicationContext(key, preflight.Session, attachment),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommitPreflight> PrepareCommitAsync(
        AttachmentUploadSessionKey key,
        PostgresAttachmentStore publisher,
        CancellationToken cancellationToken)
    {
        await using var connectionOwner = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = connectionOwner.RequireNpgsqlConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, key, "FOR UPDATE").ConfigureAwait(false)).Session;
        if (session.State == AttachmentUploadState.Committed)
        {
            return new CommitPreflight(session, await ReadCommittedAttachmentAsync(database, key).ConfigureAwait(false));
        }

        ValidateReadyForCommit(session);
        await publisher.LockUploadParentAsync(
            new AttachmentPublicationTransaction(connection, transaction, cancellationToken),
            session.Request.LayerId,
            session.Request.FeatureId).ConfigureAwait(false);
        await CopyVerifiedChunksAsync(database, session, Stream.Null).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new CommitPreflight(session, null);
    }

    private async Task<Attachment> CommitPublishedAttachmentAsync(
        CommitPublicationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteCommitTransactionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await TryRecordOrphanCleanupAsync(context.Session, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Attachment> ExecuteCommitTransactionAsync(
        CommitPublicationContext context,
        CancellationToken cancellationToken)
    {
        var publisher = _attachmentStore ?? throw new InvalidOperationException("Attachment publication is not configured.");
        await using var connectionOwner = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connection = connectionOwner.RequireNpgsqlConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, transaction, cancellationToken);
        var session = (await ReadSessionAsync(database, context.Key, "FOR UPDATE").ConfigureAwait(false)).Session;
        if (session.State == AttachmentUploadState.Committed)
        {
            return await ReadCommittedAttachmentAsync(database, context.Key).ConfigureAwait(false);
        }

        ValidateReadyForCommit(session);
        var publicationTx = new AttachmentPublicationTransaction(connection, transaction, cancellationToken);
        await publisher.LockUploadParentAsync(publicationTx, session.Request.LayerId, session.Request.FeatureId).ConfigureAwait(false);
        var published = await publisher.PublishUploadAsync(publicationTx, context.Attachment, session.Request.ReplaceAttachmentId).ConfigureAwait(false);
        published = published with
        {
            NativeMetadata = await publisher.RecordNativeMetadataAsync(publicationTx, published, session.Request).ConfigureAwait(false)
        };
        await SaveCommitReceiptAsync(database, context.Key, published).ConfigureAwait(false);
        await ClearOrphanCleanupAsync(database, context.Key.SessionId).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }

    private static void ValidateReadyForCommit(AttachmentUploadSession session)
    {
        EnsureUploading(session);
        if (session.NextOffsetBytes != session.Request.ExpectedSize)
        {
            throw Failure(AttachmentUploadFailure.InvalidState, "Upload content is incomplete.");
        }
    }

    private async Task ClearOrphanCleanupAsync(SessionDatabase database, Guid sessionId)
    {
        await using var check = database.Command("SELECT to_regclass(@ledger) IS NOT NULL");
        check.Parameters.AddWithValue("ledger", _cleanupLedger);
        if (await check.ExecuteScalarAsync(database.CancellationToken).ConfigureAwait(false) is not true)
        {
            return;
        }

        await using var delete = database.Command($"DELETE FROM {_cleanupLedger} WHERE session_id = @session");
        delete.Parameters.AddWithValue("session", sessionId);
        await delete.ExecuteNonQueryAsync(database.CancellationToken).ConfigureAwait(false);
    }

    private async Task TryRecordOrphanCleanupAsync(AttachmentUploadSession session, Exception failure)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(_cleanupLedgerTimeout);
            await using var connectionOwner = await _connectionProvider.OpenConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
            var connection = connectionOwner.RequireNpgsqlConnection();
            var nextAttemptAt = IsTerminalCommitFailure(failure) ? DateTimeOffset.UtcNow : session.ExpiresAt;
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_cleanupLedger} (session_id, layer_id, feature_id, next_attempt_at)
                SELECT @session, @layer, @feature, @nextAttemptAt
                WHERE NOT EXISTS (
                    SELECT 1 FROM {_sessions}
                    WHERE session_id = @session AND state = 'committed'
                )
                ON CONFLICT (session_id) DO UPDATE
                    SET next_attempt_at = LEAST({_cleanupLedger}.next_attempt_at, EXCLUDED.next_attempt_at)
                """, connection);
            command.Parameters.AddWithValue("session", session.SessionId);
            command.Parameters.AddWithValue("layer", session.Request.LayerId);
            command.Parameters.AddWithValue("feature", session.Request.FeatureId);
            command.Parameters.AddWithValue("nextAttemptAt", nextAttemptAt);
            await command.ExecuteNonQueryAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine(ex);
        }
    }

    internal static bool IsTerminalCommitFailure(Exception failure)
        => failure is AttachmentUploadException
        {
            Failure: AttachmentUploadFailure.NotFound
                or AttachmentUploadFailure.Expired
                or AttachmentUploadFailure.InvalidState
        };

    private static async Task<SessionCommitGateLease> AcquireSessionCommitGateAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var gate = AcquireGateEntry(sessionId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionCommitGateLease(sessionId, gate);
        }
        catch
        {
            ReleaseGateReference(sessionId, gate);
            throw;
        }
    }

    private static SessionCommitGate AcquireGateEntry(Guid sessionId)
    {
        while (true)
        {
            var gate = _sessionCommitGates.GetOrAdd(sessionId, static _ => new SessionCommitGate());
            if (gate.TryAddRef())
            {
                return gate;
            }
        }
    }

    private static void ReleaseGateReference(Guid sessionId, SessionCommitGate gate)
    {
        if (gate.ReleaseRef())
        {
            _sessionCommitGates.TryRemove(new KeyValuePair<Guid, SessionCommitGate>(sessionId, gate));
            gate.Semaphore.Dispose();
        }
    }

    private readonly record struct CommitPreflight(AttachmentUploadSession Session, Attachment? CommittedAttachment);

    private readonly record struct CommitPublicationContext(
        AttachmentUploadSessionKey Key,
        AttachmentUploadSession Session,
        Attachment Attachment);

    private sealed class SessionCommitGate
    {
        private int _refCount;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                if (current < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public bool ReleaseRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                var next = current == 1 ? -1 : current - 1;
                if (Interlocked.CompareExchange(ref _refCount, next, current) == current)
                {
                    return next < 0;
                }
            }
        }
    }

    private readonly struct SessionCommitGateLease(Guid sessionId, SessionCommitGate gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Semaphore.Release();
            ReleaseGateReference(sessionId, gate);
            return ValueTask.CompletedTask;
        }
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
