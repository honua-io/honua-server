// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore : IAttachmentUploadSessionStore
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly PostgresAttachmentStore? _attachmentStore;
    private readonly string _sessions;
    private readonly string _chunks;

    public PostgresAttachmentUploadSessionStore(IAdoNetDatabaseConnectionProvider connectionProvider, string schema, PostgresAttachmentStore? attachmentStore = null)
    {
        _connectionProvider = connectionProvider;
        _attachmentStore = attachmentStore;
        _sessions = SchemaSearchPath.QualifyTable("attachment_upload_sessions", schema);
        _chunks = SchemaSearchPath.QualifyTable("attachment_upload_chunks", schema);
    }

    public async Task<AttachmentUploadSession> GetAsync(AttachmentUploadSessionKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stored = await ReadSessionAsync(new SessionDatabase(connection, null, cancellationToken), key, "").ConfigureAwait(false);
        return stored.Session;
    }

    private async Task<StoredSession> ReadSessionAsync(SessionDatabase database, AttachmentUploadSessionKey key, string locking)
    {
        await using var command = database.Command($"""
            SELECT session_id, owner_id, service_id, layer_id, feature_id, operation,
                idempotency_key, filename, content_type, keywords, expected_size,
                content_sha256, replace_attachment_id, max_chunk_bytes, next_offset_bytes,
                state, created_at, expires_at, request_sha256, expires_at <= clock_timestamp(), published_layer_id
            FROM {_sessions} WHERE session_id = @session AND owner_id = @owner {locking}
            """);
        BindKey(command, key);
        await using var reader = await command.ExecuteReaderAsync(database.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(database.CancellationToken).ConfigureAwait(false))
        {
            throw Failure(AttachmentUploadFailure.NotFound, "Upload session was not found.");
        }
        if (reader.GetBoolean(19))
        {
            throw Failure(AttachmentUploadFailure.Expired, "Upload session has expired.");
        }
        return MapSession(reader);
    }

    private static StoredSession MapSession(NpgsqlDataReader reader)
    {
        var request = new AttachmentUploadRequest
        {
            OwnerId = reader.GetString(1), ServiceId = reader.GetString(2),
            LayerId = reader.GetInt32(3), PublishedLayerId = reader.IsDBNull(20) ? null : reader.GetInt32(20),
            FeatureId = reader.GetInt64(4), Operation = reader.GetString(5),
            IdempotencyKey = reader.GetString(6), Filename = reader.GetString(7), ContentType = reader.GetString(8),
            Keywords = reader.GetFieldValue<string[]>(9), ExpectedSize = reader.GetInt64(10),
            Sha256 = reader.GetFieldValue<byte[]>(11), ReplaceAttachmentId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            MaxChunkBytes = reader.GetInt32(13), Lifetime = reader.GetDateTime(17) - reader.GetDateTime(16)
        };
        var session = new AttachmentUploadSession
        {
            SessionId = reader.GetGuid(0), Request = request, NextOffsetBytes = reader.GetInt64(14),
            State = reader.GetString(15) switch
            {
                "uploading" => AttachmentUploadState.Uploading,
                "committed" => AttachmentUploadState.Committed,
                "aborted" => AttachmentUploadState.Aborted,
                _ => throw Failure(AttachmentUploadFailure.InvalidState, "Unknown upload session state.")
            },
            ExpiresAt = new DateTimeOffset(reader.GetDateTime(17))
        };
        return new StoredSession(session, reader.GetFieldValue<byte[]>(18));
    }

    private static void BindKey(NpgsqlCommand command, AttachmentUploadSessionKey key)
    {
        command.Parameters.AddWithValue("session", key.SessionId);
        command.Parameters.AddWithValue("owner", key.OwnerId);
    }

    private static AttachmentUploadException Failure(AttachmentUploadFailure failure, string message) => new(failure, message);

    private sealed record StoredSession(AttachmentUploadSession Session, byte[] RequestSha256);

    private readonly record struct SessionDatabase(NpgsqlConnection Connection, NpgsqlTransaction? Transaction, CancellationToken CancellationToken)
    {
        public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);
    }
}
