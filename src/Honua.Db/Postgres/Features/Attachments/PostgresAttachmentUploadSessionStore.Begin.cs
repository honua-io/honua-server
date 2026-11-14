// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Attachments.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentUploadSessionStore
{
    public async Task<AttachmentUploadSession> BeginAsync(AttachmentUploadRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        request = request with { Sha256 = (byte[])request.Sha256.Clone(), Keywords = (string[])request.Keywords.Clone() };
        var digest = Fingerprint(request);
        await using var connection = (NpgsqlConnection)await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var database = new SessionDatabase(connection, null, cancellationToken);
        await InsertSessionAsync(database, request, digest).ConfigureAwait(false);
        var key = await FindSessionKeyAsync(database, request).ConfigureAwait(false);
        var stored = await ReadSessionAsync(database, key, "").ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(stored.RequestSha256, digest))
        {
            throw Failure(AttachmentUploadFailure.Conflict, "The idempotency key belongs to a different upload request.");
        }
        return stored.Session;
    }

    private async Task InsertSessionAsync(SessionDatabase database, AttachmentUploadRequest request, byte[] digest)
    {
        await using var command = database.Command($"""
            INSERT INTO {_sessions} (session_id, owner_id, service_id, layer_id, feature_id, operation,
                idempotency_key, request_sha256, filename, content_type, keywords, expected_size,
                content_sha256, replace_attachment_id, max_chunk_bytes, expires_at)
            VALUES (@session, @owner, @service, @layer, @feature, @operation, @key, @requestDigest,
                @filename, @contentType, @keywords, @size, @contentDigest, @replace, @chunkLimit, now() + @lifetime)
            ON CONFLICT (owner_id, service_id, layer_id, feature_id, operation, idempotency_key) DO NOTHING
            """);
        BindScope(command, request);
        command.Parameters.AddWithValue("session", Guid.NewGuid());
        command.Parameters.AddWithValue("requestDigest", digest);
        command.Parameters.AddWithValue("filename", request.Filename);
        command.Parameters.AddWithValue("contentType", request.ContentType);
        command.Parameters.AddWithValue("keywords", request.Keywords);
        command.Parameters.AddWithValue("size", request.ExpectedSize);
        command.Parameters.AddWithValue("contentDigest", request.Sha256);
        command.Parameters.AddWithValue("replace", NpgsqlDbType.Bigint, (object?)request.ReplaceAttachmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("chunkLimit", request.MaxChunkBytes);
        command.Parameters.AddWithValue("lifetime", request.Lifetime);
        await command.ExecuteNonQueryAsync(database.CancellationToken).ConfigureAwait(false);
    }

    private async Task<AttachmentUploadSessionKey> FindSessionKeyAsync(SessionDatabase database, AttachmentUploadRequest request)
    {
        await using var command = database.Command($"""
            SELECT session_id FROM {_sessions} WHERE owner_id = @owner AND service_id = @service
                AND layer_id = @layer AND feature_id = @feature AND operation = @operation AND idempotency_key = @key
            """);
        BindScope(command, request);
        var id = await command.ExecuteScalarAsync(database.CancellationToken).ConfigureAwait(false);
        return id is Guid sessionId ? new AttachmentUploadSessionKey(request.OwnerId, sessionId)
            : throw Failure(AttachmentUploadFailure.NotFound, "Upload session was not found.");
    }

    private static void BindScope(NpgsqlCommand command, AttachmentUploadRequest request)
    {
        command.Parameters.AddWithValue("owner", request.OwnerId);
        command.Parameters.AddWithValue("service", request.ServiceId);
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("feature", request.FeatureId);
        command.Parameters.AddWithValue("operation", request.Operation);
        command.Parameters.AddWithValue("key", request.IdempotencyKey);
    }
}
